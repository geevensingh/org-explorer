using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private readonly object workspaceGate = new();
    private readonly object watcherGate = new();
    private FileSystemWatcher? watcher;
    private Timer? refreshTimer;
    private bool disposed;
    private bool workspaceCurrent;
    private long refreshNotBefore;
    private string? saveErrorSnapshot;
    private bool failedImportSnapshot;
    private readonly Dictionary<string, string> failedSaves = [];
    public bool IsWorkspaceCurrent => Volatile.Read(ref workspaceCurrent);
    public bool CanSafelyClose => Volatile.Read(ref saveErrorSnapshot) is null && unsavedNoteBases.IsEmpty &&
        ProfileEdit is not { IsDirty: true };
    public bool CanEnterImport
    {
        get
        {
            lock (workspaceGate)
                return failedSaves.Keys.All(operation => operation == "import") && unsavedNoteBases.IsEmpty &&
                    ProfileEdit is not { IsDirty: true };
        }
    }
    public string? LocalSaveError => Volatile.Read(ref saveErrorSnapshot);
    public bool HasFailedImport => Volatile.Read(ref failedImportSnapshot);

    public void DiscardImportChanges()
    {
        lock (workspaceGate)
        {
            pending = null;
            importPath = importHash = null;
            failedSaves.Remove("import");
            Status = "Import discarded. Independent note, profile and quick-action drafts are retained.";
        }
    }

    public void DiscardUnsavedChanges(Guid? noteId = null)
    {
        lock (workspaceGate)
        {
            if (noteId is { } id)
            {
                failedSaves.Remove($"note:{id}");
                unsavedNoteBases.TryRemove((NoteScope.Person, id), out _);
                failedSaves.Remove($"team-note:{id}");
                unsavedNoteBases.TryRemove((NoteScope.Team, id), out _);
            }
            else
            {
                foreach (var key in failedSaves.Keys.Where(key => key != "storage-maintenance").ToArray())
                    failedSaves.Remove(key);
                unsavedNoteBases.Clear();
            }
            Status = "Unsaved local changes deliberately discarded.";
        }
    }

    private bool SaveFailed(string operation, string message)
    {
        failedSaves[operation] = message;
        return Fail(message);
    }
    public event EventHandler? WorkspaceChanged;

    private void StartWatching()
    {
        refreshTimer = new Timer(_ => RefreshAfterFolderEvent(), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(workspace!)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += ScheduleRefresh;
        watcher.Created += ScheduleRefresh;
        watcher.Deleted += ScheduleRefresh;
        watcher.Renamed += ScheduleRefresh;
        watcher.Error += (_, _) => ScheduleRefresh(null, null!);
        watcher.EnableRaisingEvents = true;
    }

    private void ScheduleRefresh(object? sender, FileSystemEventArgs e)
    {
        if (e is not null && workspace is { } root && (IsWorkspacePublicationFile(root, e.FullPath) ||
            e.FullPath.StartsWith(Path.Combine(root, "recovery") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
        lock (watcherGate)
            if (!disposed)
            {
                Volatile.Write(ref refreshNotBefore, Environment.TickCount64 + 100);
                refreshTimer?.Change(100, Timeout.Infinite);
            }
    }

    private void StopWatching()
    {
        FileSystemWatcher? stoppedWatcher;
        Timer? stoppedTimer;
        lock (watcherGate)
        {
            stoppedWatcher = watcher;
            watcher = null;
            stoppedTimer = refreshTimer;
            refreshTimer = null;
        }
        stoppedWatcher?.Dispose();
        stoppedTimer?.Dispose();
    }

    private void RefreshAfterFolderEvent()
    {
        // Timer callbacks must not occupy pool workers behind synchronous publication.
        bool changed;
        if (!Monitor.TryEnter(workspaceGate))
        {
            DeferBusyRefresh();
            return;
        }
        try
        {
            if (disposed || workspace is null) return;
            var remaining = Volatile.Read(ref refreshNotBefore) - Environment.TickCount64;
            if (remaining > 0)
            {
                refreshTimer?.Change(remaining, Timeout.Infinite);
                return;
            }
            using var access = memory ? null : TryCoordinateWorkspaceIo(workspace);
            if (!memory && access is null)
            {
                DeferBusyRefresh();
                return;
            }
            var previousRevision = revision;
            var previousStatus = Status;
            var valid = RefreshWorkspaceCore();
            changed = revision != previousRevision || Status != previousStatus;
            if (!valid) refreshTimer?.Change(300, Timeout.Infinite);
        }
        finally { Monitor.Exit(workspaceGate); }
        if (changed) WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeferBusyRefresh()
    {
        lock (watcherGate)
            if (!disposed) refreshTimer?.Change(100, Timeout.Infinite);
    }

    public bool RefreshWorkspace()
    {
        bool valid;
        bool changed;
        lock (workspaceGate)
        {
            if (disposed) return Fail("The Workspace workflow is closed.");
            var previousRevision = revision;
            var previousStatus = Status;
            valid = RefreshWorkspaceCore();
            changed = previousRevision != revision || previousStatus != Status;
        }
        if (changed) WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return valid;
    }

    private bool RefreshWorkspaceCore()
    {
        if (workspace is null) return Fail("Open a Workspace before refreshing.");
        if (memory) return true;
        try
        {
            using var access = CoordinateWorkspaceIo(workspace);
            if (workspaceCurrent && WorkspaceFingerprint() == revision) return true;
            ReadAndAcceptWorkspace();
            pending = null;
            Status = "Workspace refreshed from validated local files.";
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            pending = null;
            workspaceCurrent = false;
            MarkCompatibilityFailure(ex);
            return Fail("Workspace unavailable; last valid information is STALE: " + ex.Message);
        }
    }

    private void ReadAndAcceptWorkspace()
    {
        using var access = memory ? null : CoordinateWorkspaceIo(workspace!);
        if (!memory)
        {
            OpenIncrementalWorkspace();
            return;
        }
        people = new(StringComparer.OrdinalIgnoreCase);
        batches = [];
        personNotes = [];
        teamNotes = [];
        teamNoteConfirmations = [];
        teamContextRevisions = new(StringComparer.OrdinalIgnoreCase);
        photos = [];
        profileHistory = new(StringComparer.OrdinalIgnoreCase);
        historicalTeamContexts = new(StringComparer.OrdinalIgnoreCase);
        InvalidateProjections();
        revision = null;
        workspaceCurrent = true;
        compatibility = new(WorkspaceCompatibilityState.Compatible,
            "Current Workspace. Settings remain available.", workspace, 3);
    }

    private static void ValidateAcceptedSource(JsonElement source)
    {
        var (_, _, records) = ValidateImportEnvelope(source, "Accepted batch", workspaceSource: true);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.EnumerateArray())
        {
            var email = ValidateImportRecord(record, "Accepted batch Person");
            if (!keys.Add(email)) throw new FormatException("Accepted batch: duplicate Person identity.");
        }
    }

    private static PersonProfile ReadPersonRecord(JsonElement root, int workspaceVersion)
    {
        CheckRecordVersion(root, "Person", workspaceVersion);
        CheckProperties(root, ["schemaVersion", "email", "directoryName", "preferredName", "alias", "jobTitle", "department",
            "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl",
            "managerEmail", "archived", "isFavorite", "photoPath", "heading"], "Person");
        var email = Email(Required(root, "email", "Person"), "Person.email");
        Required(root, "directoryName", email);
        var manager = Required(root, "managerEmail", email);
        if (manager.ValueKind != JsonValueKind.Null) Email(manager, $"{email}.managerEmail");
        var person = DeserializeVersionedRecord<PersonProfile>(root) ?? throw new FormatException("Invalid Person data.");
        foreach (var (field, property, _) in TextFields)
            ValidateText(JsonSerializer.SerializeToElement(ReadText(person, field)), field, $"{person.Email}.{property}");
        return person;
    }

    private static void ValidateAcceptedBatch(string id, AcceptedBatch? batch)
    {
        if (!Guid.TryParseExact(id, "D", out _) || batch is null ||
            batch.Fingerprint is null || !Regex.IsMatch(batch.Fingerprint, "^[A-F0-9]{64}$") ||
            batch.Source.ValueKind != JsonValueKind.Object ||
            !batch.Source.TryGetProperty("batchId", out var batchId) || batchId.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(batchId.GetString(), out var sourceId) || sourceId.ToString() != id ||
            batch.AppliedAt == default || batch.AppliedAt.Offset != TimeSpan.Zero)
            throw new FormatException("Invalid accepted batch identity, source evidence, fingerprint, or application time.");
        ValidateAcceptedSource(batch.Source);
    }

    private static void RejectWorkspaceLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Workspace data must be ordinary local files, not links: " + Path.GetFileName(path));
    }

    private static byte[] ReadWorkspaceBytes(string path)
    {
        if (workspaceReadSnapshot.Value is not { } snapshot) return ReadWorkspaceBytesUncached(path);
        return snapshot.TryGetValue(path, out var bytes) ? bytes
            : throw new IOException("Workspace delivery changed while reading: " + Path.GetFileName(path));
    }

    private static string ReadWorkspaceText(string path)
    {
        // Provider delivery must not be blocked by a reader, including clients
        // waiting on an incompatible/incomplete Workspace. Byte fingerprints
        // and whole-state validation, not read leases, establish a stable view.
        using Stream file = new MemoryStream(ReadWorkspaceBytes(path), false);
        using var text = new StreamReader(file, new UTF8Encoding(false, true));
        return text.ReadToEnd();
    }

    public void Dispose()
    {
        lock (workspaceGate)
        {
            disposed = true;
            StopWatching();
        }
    }
}
