using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

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
            }
            else
            {
                failedSaves.Clear();
                failedNoteBases.Clear();
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
            Status = "Workspace refreshed from validated local files.";
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            pending = null;
            workspaceCurrent = false;
            return Fail("Workspace unavailable; last valid information is STALE: " + ex.Message);
        }
    }

    private void ReadAndAcceptWorkspace()
    {
        var current = memory ? null : WorkspaceFingerprint();
        var loaded = new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);
        var accepted = new Dictionary<string, AcceptedBatch>();
        var loadedPhotos = new Dictionary<string, byte[]>();
        if (!memory)
        {
            var metadataPath = Path.Combine(workspace!, "workspace.json");
            if (File.Exists(metadataPath))
            {
                var metadata = JsonSerializer.Deserialize<WorkspaceMetadata>(File.ReadAllText(metadataPath), JsonOptions)
                    ?? throw new FormatException("Invalid Workspace metadata.");
                if (metadata.SchemaVersion != 1) throw new FormatException("Unsupported Workspace version. Upgrade the app; writes are blocked.");
                if (!Regex.IsMatch(metadata.Generation, @"^[a-f0-9]{32}$")) throw new FormatException("Invalid Workspace generation.");
                var generation = Path.Combine(workspace!, "generations", metadata.Generation);
                foreach (var file in Directory.GetFiles(Path.Combine(generation, "people"), "*.json"))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file, new UTF8Encoding(false, true)));
                    var root = document.RootElement;
                    CheckProperties(root, ["email", "directoryName", "preferredName", "alias", "jobTitle", "department",
                        "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl",
                        "managerEmail", "archived", "photoPath", "heading"], Path.GetFileName(file));
                    var email = Email(Required(root, "email", "Person"), "Person.email");
                    Required(root, "directoryName", email);
                    var manager = Required(root, "managerEmail", email);
                    if (manager.ValueKind != JsonValueKind.Null) Email(manager, $"{email}.managerEmail");
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToLowerInvariant()))).ToLowerInvariant();
                    if (!Path.GetFileName(file).Equals(key + ".json", StringComparison.OrdinalIgnoreCase))
                        throw new FormatException($"{email}: conflicting Person filename; resolve provider copies outside the app.");
                    var person = root.Deserialize<PersonProfile>(JsonOptions)
                        ?? throw new FormatException("Invalid Person data.");
                    Email(JsonSerializer.SerializeToElement(person.Email), "email");
                    foreach (var (field, property, _) in TextFields)
                        ValidateText(JsonSerializer.SerializeToElement(ReadText(person, field)), field, $"{person.Email}.{property}");
                    if (!loaded.TryAdd(person.Email, person)) throw new FormatException("Duplicate Person identity in Workspace.");
                }
                ValidateGraph(loaded);
                using var batchDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(generation, "batches.json")));
                CheckProperties(batchDocument.RootElement, batchDocument.RootElement.ValueKind == JsonValueKind.Object
                    ? batchDocument.RootElement.EnumerateObject().Select(p => p.Name).ToArray() : [], "Accepted batches");
                accepted = batchDocument.RootElement.Deserialize<Dictionary<string, AcceptedBatch>>(JsonOptions)
                    ?? throw new FormatException("Invalid accepted batch data.");
                foreach (var (id, batch) in accepted)
                    if (!Guid.TryParseExact(id, "D", out _) || batch is null ||
                        batch.Fingerprint is null || !Regex.IsMatch(batch.Fingerprint, "^[A-F0-9]{64}$") ||
                        batch.Source.ValueKind != JsonValueKind.Object ||
                        !batch.Source.TryGetProperty("batchId", out var batchId) || batchId.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(batchId.GetString(), out var sourceId) || sourceId.ToString() != id ||
                        batch.AppliedAt == default || batch.AppliedAt.Offset != TimeSpan.Zero)
                        throw new FormatException("Invalid accepted batch identity, source evidence, fingerprint, or application time.");
                loadedPhotos = LoadPhotos(generation, loaded);
            }
            else if (Directory.EnumerateFileSystemEntries(workspace!).Any())
                throw new IOException("Missing Workspace pointer or unrecognized folder; current data is unavailable.");
        }
        var loadedNotes = memory ? [] : LoadPersonNotes(workspace!, loaded);
        var notesRevision = memory ? null : PersonNotesFingerprint();
        var otherRevision = memory ? null : WorkspaceFingerprint(includePersonNotes: false);
        if (!memory && WorkspaceFingerprint() != current)
            throw new IOException("Workspace delivery changed while reading; waiting for complete local files.");
        people = loaded;
        batches = accepted;
        personNotes = loadedNotes;
        photos = loadedPhotos;
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
                RejectWorkspaceLink(entry);
                if (Directory.Exists(entry)) directories.Push(entry);
                else yield return entry;
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
