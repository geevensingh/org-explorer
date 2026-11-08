using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private readonly object workspaceGate = new();
    private FileSystemWatcher? watcher;
    private Timer? refreshTimer;
    private bool disposed;
    private bool workspaceCurrent;
    private readonly Dictionary<string, string> failedSaves = [];
    public bool IsWorkspaceCurrent { get { lock (workspaceGate) return workspaceCurrent; } }
    public bool CanSafelyClose { get { lock (workspaceGate) return failedSaves.Count == 0; } }
    public string? LocalSaveError
    { get { lock (workspaceGate) return failedSaves.Count == 0 ? null : string.Join("\n", failedSaves.Values.Distinct()); } }
    public void DiscardUnsavedChanges(Guid? noteId = null)
    {
        lock (workspaceGate)
        {
            if (noteId is { } id)
            {
                failedSaves.Remove($"note:{id}");
                failedNoteBases.Remove(id);
                failedSaves.Remove($"team-note:{id}");
                failedTeamNoteBases.Remove(id);
            }
            else
            {
                failedSaves.Clear();
                failedNoteBases.Clear();
                failedTeamNoteBases.Clear();
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
        lock (workspaceGate)
            if (!disposed) refreshTimer?.Change(100, Timeout.Infinite);
    }

    private void StopWatching()
    {
        watcher?.Dispose();
        watcher = null;
        refreshTimer?.Dispose();
        refreshTimer = null;
    }

    private void RefreshAfterFolderEvent()
    {
        bool changed;
        lock (workspaceGate)
        {
            if (disposed || workspace is null) return;
            var previousRevision = revision;
            var previousStatus = Status;
            var valid = RefreshWorkspaceCore();
            changed = revision != previousRevision || Status != previousStatus;
            if (!valid) refreshTimer?.Change(300, Timeout.Infinite);
        }
        if (changed) WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RefreshWorkspace()
    {
        bool valid;
        bool changed;
        lock (workspaceGate)
        {
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
        conversionMessage = null;
        if (!memory && InspectWorkspaceVersions() == 1) ConvertVersionOneWorkspace();
        ReadAndAcceptWorkspaceData();
        compatibility = new(WorkspaceCompatibilityState.Compatible,
            conversionMessage ?? "Current validated Workspace. Settings remain available.",
            workspace, 2, compatibility.RecoveryPath);
    }

    private void ReadAndAcceptWorkspaceData()
    {
        var current = memory ? null : WorkspaceFingerprint();
        var loaded = new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);
        var accepted = new Dictionary<string, AcceptedBatch>();
        var loadedPhotos = new Dictionary<string, byte[]>();
        var loadedTeamContexts = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, PersonHistory>(StringComparer.OrdinalIgnoreCase);
        var historicalContexts = new Dictionary<string, List<HistoricalTeamContext>>(StringComparer.OrdinalIgnoreCase);
        if (!memory)
        {
            var metadataPath = Path.Combine(workspace!, "workspace.json");
            if (File.Exists(metadataPath))
            {
                using var pointer = JsonDocument.Parse(File.ReadAllText(metadataPath, new UTF8Encoding(false, true)));
                var metadata = ReadWorkspaceMetadata(pointer.RootElement);
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
                    using var document = JsonDocument.Parse(File.ReadAllText(file, new UTF8Encoding(false, true)));
                    var root = document.RootElement;
                    CheckRecordVersion(root, "Person", metadata.SchemaVersion);
                    CheckProperties(root, ["schemaVersion", "email", "directoryName", "preferredName", "alias", "jobTitle", "department",
                        "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl",
                        "managerEmail", "archived", "photoPath", "heading"], Path.GetFileName(file));
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
                using var batchDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(generation, "batches.json")));
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
        var notesRevision = memory ? null : PersonNotesFingerprint();
        var otherRevision = memory ? null : WorkspaceFingerprint(includePersonNotes: false);
        if (!memory && WorkspaceFingerprint() != current)
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
        revision = current;
        workspaceCurrent = true;
        personNotesRevision = notesRevision;
        personNotesWorkspaceRevision = otherRevision;
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            RejectWorkspaceLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (directory == root && Path.GetFileName(entry).Equals("recovery", StringComparison.OrdinalIgnoreCase)) continue;
                RejectWorkspaceLink(entry);
                if (Directory.Exists(entry)) directories.Push(entry);
                else yield return entry;
            }
        }
    }

    private static void ValidateAcceptedSource(JsonElement source)
    {
        RequireVersion(source, "accepted batch source", [1]);
        CheckProperties(source, ["schemaVersion", "batchId", "observedAt", "producer", "people"], "Accepted batch");
        var version = Required(source, "schemaVersion", "Accepted batch");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetDecimal(out var number) || number != 1)
            throw new FormatException("Unsupported accepted batch source version; upgrade the app.");
        var observed = Text(Required(source, "observedAt", "Accepted batch"), "Accepted batch.observedAt", false)!;
        if (!Regex.IsMatch(observed, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z$") ||
            !DateTimeOffset.TryParseExact(observed, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            throw new FormatException("Accepted batch.observedAt: expected a real UTC calendar timestamp.");
        if (source.TryGetProperty("producer", out var producer)) Text(producer, "Accepted batch.producer", false);
        var records = Required(source, "people", "Accepted batch");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
            throw new FormatException("Accepted batch.people: expected a nonempty array.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.EnumerateArray())
        {
            CheckProperties(record, ["email", "displayName", "preferredName", "alias", "jobTitle", "department",
                "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl",
                "managerEmail", "archived", "photoPath"], "Accepted batch Person");
            var email = Email(Required(record, "email", "Accepted batch Person"), "Accepted batch Person.email");
            if (!keys.Add(email)) throw new FormatException("Accepted batch: duplicate Person identity.");
            foreach (var (field, property, _) in TextFields)
                if (record.TryGetProperty(property, out var text)) ValidateText(text, field, $"{email}.{property}");
            if (record.TryGetProperty("managerEmail", out var manager) && manager.ValueKind != JsonValueKind.Null)
                Email(manager, $"{email}.managerEmail");
            if (record.TryGetProperty("archived", out var archived)) Boolean(archived, $"{email}.archived");
            if (record.TryGetProperty("photoPath", out var photo) && photo.ValueKind != JsonValueKind.Null)
            {
                var relative = Text(photo, $"{email}.photoPath", false)!;
                if (Path.IsPathRooted(relative) || relative.Contains(':') ||
                    relative.Split(['\\', '/']).Any(segment => segment == ".."))
                    throw new FormatException($"{email}.photoPath: expected a safe local relative source path.");
            }
        }
    }

    private static void RejectWorkspaceLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Workspace data must be ordinary local files, not links: " + Path.GetFileName(path));
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
