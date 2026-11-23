using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private readonly object workspaceGate = new();
    private bool disposed;
    private bool workspaceCurrent;
    private string? saveErrorSnapshot;
    private bool failedImportSnapshot;
    private readonly IDictionary<string, string> failedSaves = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
    public bool IsWorkspaceCurrent => Volatile.Read(ref workspaceCurrent);
    public bool CanSafelyClose => !LocalSave.EditsPaused && LocalSave.OutstandingRequests == 0 &&
        Volatile.Read(ref saveErrorSnapshot) is null && unsavedNoteBases.IsEmpty &&
        !HasUnsavedProfiles && !HasRetainedPersonTextDrafts;
    public bool CanNavigatePeople => true;
    public bool CanEnterImport
    {
        get
        {
            if (localSqlite && LocalSave.HasUnresolvedOutcome) return false;
            lock (workspaceGate)
                return failedSaves.Keys.All(operation => operation == "import") && unsavedNoteBases.IsEmpty &&
                    !HasUnsavedProfiles;
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
        if (localSqlite && noteId is { } localId)
        {
            lock (profileDraftGate)
            {
                if (LocalSave.HasUnresolvedOutcome || LocalSave.OutstandingRequests != 0)
                {
                    Fail("Note discard waits for the original local outcome. Saving and recovery state are unchanged.");
                    return;
                }
                failedSaves.Remove($"note:{localId}");
                unsavedNoteBases.TryRemove((NoteScope.Person, localId), out _);
                failedSaves.Remove($"team-note:{localId}");
                unsavedNoteBases.TryRemove((NoteScope.Team, localId), out _);
                Status = "Unsaved local note draft deliberately discarded. Independent writing and recovery state are retained.";
                return;
            }
        }
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
                foreach (var key in failedSaves.Keys.ToArray())
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

    private bool RefreshWorkspaceCore(bool onlyIfRevisionChanged = false)
    {
        if (workspace is null) return Fail("Open a Workspace before refreshing.");
        if (memory) return true;
        try
        {
            if (onlyIfRevisionChanged && workspaceCurrent && LocalSqliteRevision() == revision) return true;
            var walReady = OpenLocalSqlite(create: false);
            if (!walReady) return Fail(localWalFault!.Message);
            Status = localWalFault?.Message ?? "Workspace refreshed from committed local SQLite data.";
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            LocalStorageFailed(ex);
            pending = null;
            workspaceCurrent = false;
            MarkCompatibilityFailure(ex);
            return Fail("Workspace unavailable; last valid information is STALE: " + ex.Message);
        }
    }

    private void OpenInMemoryWorkspace()
    {
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
            "Current Workspace. Settings remain available.", workspace, 3)
        { SupportsNonblockingNoteAutosave = true };
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

    private static PersonProfile ReadPersonRecord(JsonElement root)
    {
        RequireVersion(root, "Person", [2]);
        CheckProperties(root, ["schemaVersion", "email", "directoryName", "preferredName", "alias", "jobTitle", "department",
            "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl",
            "managerEmail", "archived", "isFavorite", "photoPath", "heading", "teamName"], "Person");
        var email = Email(Required(root, "email", "Person"), "Person.email");
        Required(root, "directoryName", email);
        var manager = Required(root, "managerEmail", email);
        if (manager.ValueKind != JsonValueKind.Null) Email(manager, $"{email}.managerEmail");
        var person = DeserializeVersionedRecord<PersonProfile>(root) ?? throw new FormatException("Invalid Person data.");
        foreach (var (field, property, _) in TextFields)
            ValidateText(JsonSerializer.SerializeToElement(ReadText(person, field)), field, $"{person.Email}.{property}");
        Text(JsonSerializer.SerializeToElement(person.TeamName), $"{person.Email}.teamName");
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

    private static void RejectWorkspaceLink(string path, bool allowMissing = false)
    {
        // A new FileInfo captures attributes and absence in one query, including directories.
        var attributes = allowMissing ? new FileInfo(path).Attributes : File.GetAttributes(path);
        if (allowMissing && attributes == (FileAttributes)(-1)) return;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Workspace data must be ordinary local files, not links: " + Path.GetFileName(path));
    }

    public void Dispose()
    {
        lock (workspaceGate)
        {
            disposed = true;
            localRequests.Dispose();
        }
    }
}
