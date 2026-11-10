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
    private volatile string? liveGeneration;
    private string? saveErrorSnapshot;
    private readonly Dictionary<string, string> failedSaves = [];
    public bool IsWorkspaceCurrent => Volatile.Read(ref workspaceCurrent);
    public bool CanSafelyClose => Volatile.Read(ref saveErrorSnapshot) is null && unsavedNoteBases.IsEmpty;
    public string? LocalSaveError => Volatile.Read(ref saveErrorSnapshot);
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
                failedSaves.Clear();
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
        if (e is not null && workspace is { } currentRoot && Volatile.Read(ref workspaceCurrent))
        {
            var prefix = Path.Combine(currentRoot, "generations") + Path.DirectorySeparatorChar;
            if (e.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var relative = e.FullPath[prefix.Length..];
                var generation = relative.Split(Path.DirectorySeparatorChar)[0];
                if (!generation.Equals(liveGeneration, StringComparison.OrdinalIgnoreCase)) return;
            }
        }
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
            Status = conversionMessage ?? "Workspace refreshed from validated local files.";
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
        conversionMessage = null;
        if (!memory)
        {
            var pointerPath = Path.Combine(workspace!, "workspace.json");
            if (File.Exists(pointerPath))
            {
                RejectWorkspaceLink(pointerPath);
                using var pointer = JsonDocument.Parse(ReadWorkspaceText(pointerPath));
                ReadWorkspaceMetadata(pointer.RootElement);
            }
        }
        var bytes = memory ? null : CaptureWorkspaceBytes();
        int version;
        using (var read = bytes is null ? null : new WorkspaceReadScope(bytes))
            version = memory ? 2 : InspectWorkspaceVersions();
        if (version == 1)
        {
            ConvertVersionOneWorkspace();
            bytes = null;
        }
        ReadAndAcceptWorkspaceData(snapshot: bytes);
        compatibility = new(WorkspaceCompatibilityState.Compatible,
            conversionMessage ?? "Current validated Workspace. Settings remain available.",
            workspace, 2, compatibility.RecoveryPath);
    }

    private void ReadAndAcceptWorkspaceData(WorkspaceMetadata? conversionCandidate = null,
        IReadOnlyDictionary<string, byte[]>? snapshot = null, bool allGenerations = false)
    {
        using var access = memory ? null : CoordinateWorkspaceIo(workspace!);
        snapshot ??= memory ? null : CaptureWorkspaceBytes(allGenerations);
        using var read = snapshot is null ? null : new WorkspaceReadScope(snapshot);
        var current = memory ? null : WorkspaceFingerprint(snapshot: snapshot);
        var loaded = new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);
        var accepted = new Dictionary<string, AcceptedBatch>();
        var loadedPhotos = new Dictionary<string, byte[]>();
        var loadedTeamContexts = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, PersonHistory>(StringComparer.OrdinalIgnoreCase);
        var historicalContexts = new Dictionary<string, List<HistoricalTeamContext>>(StringComparer.OrdinalIgnoreCase);
        string? loadedGeneration = null;
        if (!memory)
        {
            var metadataPath = Path.Combine(workspace!, "workspace.json");
            if (File.Exists(metadataPath))
            {
                using var pointer = JsonDocument.Parse(ReadWorkspaceText(metadataPath));
                var metadata = conversionCandidate ?? ReadWorkspaceMetadata(pointer.RootElement);
                loadedGeneration = metadata.Generation;
                var generation = Path.Combine(workspace!, "generations", metadata.Generation);
                if (metadata.Files is { } files)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var relative in files)
                    {
                        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
                            relative.Split(['\\', '/']).Any(segment => segment is ".." or "" or ".") || !seen.Add(relative))
                            throw new FormatException("Invalid or duplicate Workspace generation file reference.");
                        if (!File.Exists(Path.Combine(generation, relative)))
                            throw new IOException($"Workspace generation file {relative} is unavailable; waiting for complete delivery.");
                    }
                }
                foreach (var file in Directory.GetFiles(Path.Combine(generation, "people"), "*.json"))
                {
                    using var document = JsonDocument.Parse(ReadWorkspaceText(file));
                    var root = document.RootElement;
                    CheckRecordVersion(root, "Person", metadata.SchemaVersion);
                    CheckProperties(root, ["schemaVersion", "email", "directoryName", "preferredName", "alias", "jobTitle", "department",
                        "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl",
                        "managerEmail", "archived", "isFavorite", "photoPath", "heading"], Path.GetFileName(file));
                    var email = Email(Required(root, "email", "Person"), "Person.email");
                    Required(root, "directoryName", email);
                    var manager = Required(root, "managerEmail", email);
                    if (manager.ValueKind != JsonValueKind.Null) Email(manager, $"{email}.managerEmail");
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToLowerInvariant()))).ToLowerInvariant();
                    if (!Path.GetFileName(file).Equals(key + ".json", StringComparison.OrdinalIgnoreCase))
                        throw new FormatException($"{email}: conflicting Person filename; resolve provider copies outside the app.");
                    var person = DeserializeVersionedRecord<PersonProfile>(root)
                        ?? throw new FormatException("Invalid Person data.");
                    Email(JsonSerializer.SerializeToElement(person.Email), "email");
                    foreach (var (field, property, _) in TextFields)
                        ValidateText(JsonSerializer.SerializeToElement(ReadText(person, field)), field, $"{person.Email}.{property}");
                    if (!loaded.TryAdd(person.Email, person)) throw new FormatException("Duplicate Person identity in Workspace.");
                }
                ValidateGraph(loaded);
                loadedTeamContexts = LoadTeamContextRevisions(generation, loaded);
                using var batchDocument = JsonDocument.Parse(ReadWorkspaceText(Path.Combine(generation, "batches.json")));
                CheckProperties(batchDocument.RootElement, batchDocument.RootElement.ValueKind == JsonValueKind.Object
                    ? batchDocument.RootElement.EnumerateObject().Select(p => p.Name).ToArray() : [], "Accepted batches");
                accepted = batchDocument.RootElement.Deserialize<Dictionary<string, AcceptedBatch>>(JsonOptions)
                    ?? throw new FormatException("Invalid accepted batch data.");
                foreach (var (id, batch) in accepted)
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
                loadedPhotos = LoadPhotos(generation, loaded);
                history = ReadProfileHistory(generation, loaded, metadata.SchemaVersion);
                historicalContexts = ReadHistoricalTeamContexts(generation, loaded);
            }
            else if (Directory.EnumerateFileSystemEntries(workspace!).Any())
                throw new IOException("Missing Workspace pointer or unrecognized folder; current data is unavailable.");
        }
        var loadedNotes = memory ? [] : LoadPersonNotes(workspace!, loaded);
        var loadedTeamNotes = memory ? new TeamNoteDocuments([], []) : LoadTeamNotes(workspace!, loaded);
        if (!memory && WorkspaceFingerprint(allGenerations: allGenerations) != current)
            throw new IOException("Workspace delivery changed while reading; waiting for complete local files.");
        people = loaded;
        batches = accepted;
        personNotes = loadedNotes;
        teamNotes = loadedTeamNotes.Notes;
        teamNoteConfirmations = loadedTeamNotes.Confirmations;
        teamContextRevisions = loadedTeamContexts;
        photos = loadedPhotos;
        profileHistory = history;
        historicalTeamContexts = historicalContexts;
        liveGeneration = loadedGeneration;
        InvalidateProjections();
        revision = current;
        workspaceCurrent = true;
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string root, bool allGenerations = false)
    {
        string? currentGeneration = null;
        var generations = Path.Combine(root, "generations");
        if (!allGenerations && File.Exists(Path.Combine(root, "workspace.json")))
        {
            using var pointer = JsonDocument.Parse(ReadWorkspaceBytesUncached(Path.Combine(root, "workspace.json")));
            currentGeneration = ReadWorkspaceMetadata(pointer.RootElement).Generation;
        }
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            RejectWorkspaceLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsWorkspacePublicationFile(root, entry)) continue;
                if (directory == root && Path.GetFileName(entry).Equals("recovery", StringComparison.OrdinalIgnoreCase)) continue;
                if (!allGenerations && directory.Equals(generations, StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(entry).Equals(currentGeneration, StringComparison.OrdinalIgnoreCase)) continue;
                RejectWorkspaceLink(entry);
                if (Directory.Exists(entry)) directories.Push(entry);
                else yield return entry;
            }
        }
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
