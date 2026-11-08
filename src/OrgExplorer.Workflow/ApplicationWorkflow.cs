using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed record PersonProfile(string Email, string? DirectoryName, string? PreferredName, string? ManagerEmail,
    bool Archived = false)
{
    public string? Alias { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public string? OfficeBuilding { get; init; }
    public string? OfficeNumber { get; init; }
    public string? OfficeState { get; init; }
    public string? OfficeCountry { get; init; }
    public string? LinkedInProfileUrl { get; init; }
    public string Heading => PreferredName ?? DirectoryName ?? Email;
    public string? PhotoPath { get; init; }
}

public sealed record ImportPreview(Guid Id, bool CanApply, IReadOnlyList<string> Changes, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record ReportingNode(PersonProfile Person, IReadOnlyList<ReportingNode> Reports);
public sealed record PersonRelationships(PersonProfile Person, PersonProfile? Manager, IReadOnlyList<PersonProfile> ImportedReports);

public partial interface IApplicationWorkflow : IDisposable
{
    event EventHandler? WorkspaceChanged;
    bool IsWorkspaceCurrent { get; }
    bool CanSafelyClose { get; }
    string? LocalSaveError { get; }
    void DiscardUnsavedChanges(Guid? noteId = null);
    bool RefreshWorkspace();
    IReadOnlyList<PersonProfile> People { get; }
    IReadOnlyList<ReportingNode> ReportingRoots { get; }
    PersonRelationships? GetRelationships(string email);
    ProfileTimeline? GetProfileTimeline(string email);
    string Status { get; }
    bool OpenWorkspace(string path);
    ImportPreview PreviewImport(string json);
    ImportPreview PreviewImportFile(string path);
    bool ApplyImport(Guid previewId);
    bool EditPreferredName(string email, string? preferredName);
    PersonPhoto? GetPhoto(string email);
    bool EditPhoto(string email, string? localPath);
    bool EditPersonText(string email, PersonTextField field, string? value);
    bool OpenLinkedInProfile(string email);
}

public sealed partial class ApplicationWorkflow : IApplicationWorkflow
{
    private readonly bool memory;
    private Dictionary<string, PersonProfile> people = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AcceptedBatch> batches = [];
    private string? workspace;
    private string? revision;
    private Pending? pending;
    private string? importPath;
    private string? importHash;
    private sealed record Pending(ImportPreview Preview, Dictionary<string, PersonProfile> People,
        string BatchId, string Fingerprint, JsonElement Source, Dictionary<string, byte[]> Photos,
        IReadOnlyList<PhotoResource> Resources);
    private sealed record AcceptedBatch(string Fingerprint, JsonElement Source, DateTimeOffset AppliedAt);
    private sealed record WorkspaceMetadata(
        [property: System.Text.Json.Serialization.JsonRequired] int SchemaVersion,
        [property: System.Text.Json.Serialization.JsonRequired] string Generation,
        IReadOnlyList<string>? Files = null);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private ApplicationWorkflow(bool memory, IProfileLinkOpener? profileLinkOpener, TimeProvider? timeProvider)
    { this.memory = memory; this.profileLinkOpener = profileLinkOpener; this.timeProvider = timeProvider ?? TimeProvider.System; }
    public static IApplicationWorkflow CreateInMemory(IProfileLinkOpener? profileLinkOpener = null, TimeProvider? timeProvider = null) =>
        new ApplicationWorkflow(true, profileLinkOpener, timeProvider);
    public static IApplicationWorkflow CreateFileBacked(IProfileLinkOpener? profileLinkOpener = null, TimeProvider? timeProvider = null) =>
        new ApplicationWorkflow(false, profileLinkOpener, timeProvider);
    public IReadOnlyList<PersonProfile> People
    { get { lock (workspaceGate) return people.Values.OrderBy(p => p.Heading).ToArray(); } }
    public IReadOnlyList<ReportingNode> ReportingRoots
    {
        get
        {
            lock (workspaceGate)
            {
            var children = new Dictionary<string, List<ReportingNode>>(StringComparer.OrdinalIgnoreCase);
            var nodes = new Dictionary<string, ReportingNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var person in People.Where(p => !p.Archived))
            {
                var reports = new List<ReportingNode>();
                children.Add(person.Email, reports);
                nodes.Add(person.Email, new ReportingNode(person, reports.AsReadOnly()));
            }
            var roots = new List<ReportingNode>();
            foreach (var node in nodes.Values)
                if (node.Person.ManagerEmail is { } manager) children[manager].Add(node);
                else roots.Add(node);
            return roots.AsReadOnly();
            }
        }
    }
    public PersonRelationships? GetRelationships(string email)
    { lock (workspaceGate) return people.TryGetValue(email, out var person)
        ? new PersonRelationships(person, person.ManagerEmail is { } manager ? people.GetValueOrDefault(manager) : null,
            People.Where(p => !p.Archived && string.Equals(p.ManagerEmail, person.Email, StringComparison.OrdinalIgnoreCase)).ToArray())
        : null; }
    private string status = "Choose a Workspace.";
    public string Status
    {
        get
        {
            lock (workspaceGate)
                return LocalSaveError is { } error && !status.Contains(error, StringComparison.Ordinal) ? status + "\n" + error : status;
        }
        private set { status = value; }
    }

    public bool OpenWorkspace(string path)
    { lock (workspaceGate) return OpenWorkspaceCore(path); }

    private bool OpenWorkspaceCore(string path)
    {
        if (disposed) return Fail("The Workspace workflow is closed.");
        StopWatching();
        workspaceCurrent = false;
        pending = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Choose an ordinary local Workspace, not a network share.");
            if (!memory && fullPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a Workspace outside the application installation.");
            if (!memory) Directory.CreateDirectory(fullPath);
            workspace = fullPath;
            ReadAndAcceptWorkspace();
            if (!memory) StartWatching();
            Status = "Workspace opened.";
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            workspace = null;
            people.Clear();
            batches.Clear();
            profileHistory.Clear();
            historicalTeamContexts.Clear();
            personNotes.Clear();
            teamNotes.Clear();
            teamNoteConfirmations.Clear();
            teamContextRevisions.Clear();
            photos.Clear();
            return Fail("Workspace could not be opened: " + ex.Message);
        }
    }

    public ImportPreview PreviewImport(string json)
    { lock (workspaceGate) return PreviewImport(json, null); }

    private ImportPreview PreviewImport(string json, string? importDirectory)
    {
        importPath = null;
        importHash = null;
        pending = null;
        var errors = new List<string>();
        var changes = new List<string>();
        var warnings = new List<string>();
        var id = Guid.NewGuid();
        if (workspace is null) errors.Add("Open a Workspace first.");
        else if (!workspaceCurrent) errors.Add("Workspace unavailable; retained information is STALE. Wait for valid local delivery before importing.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            CheckProperties(root, ["schemaVersion", "batchId", "observedAt", "producer", "people"], "batch");
            var version = Required(root, "schemaVersion", "batch");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetDecimal(out var number) || number != 1)
                throw new FormatException("schemaVersion: unsupported import version; upgrade the app.");
            var batchId = Text(Required(root, "batchId", "batch"), "batchId", false)!;
            if (!Guid.TryParseExact(batchId, "D", out var parsedId)) throw new FormatException("batchId: expected UUID.");
            batchId = parsedId.ToString();
            var observed = Text(Required(root, "observedAt", "batch"), "observedAt", false)!;
            if (!Regex.IsMatch(observed, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z$") ||
                !DateTimeOffset.TryParseExact(observed, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observedAt))
                throw new FormatException("observedAt: expected a real UTC calendar timestamp.");
            if (root.TryGetProperty("producer", out var producer)) Text(producer, "producer", false);
            var records = Required(root, "people", "batch");
            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
                throw new FormatException("people: expected a nonempty array.");
            var proposed = new Dictionary<string, PersonProfile>(people, StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var canonical = new List<string>();
            var recordIndex = 0;
            var proposedPhotos = new Dictionary<string, byte[]>(photos);
            var resources = new List<PhotoResource>();
            foreach (var record in records.EnumerateArray())
            {
                var scope = $"people[{recordIndex++}]";
                var email = Email(Required(record, "email", scope), $"{scope}.email");
                var priorChanges = changes.Count;
                CheckProperties(record, ["email", "displayName", "preferredName", "alias", "jobTitle", "department",
                    "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl", "managerEmail", "archived", "photoPath"], email);
                if (!keys.Add(email)) throw new FormatException($"{email}: duplicate Person identity.");
                var existing = proposed.GetValueOrDefault(email);
                var directoryName = record.TryGetProperty("displayName", out var name) ? Text(name, $"{email}.displayName") : existing?.DirectoryName;
                var preferredName = record.TryGetProperty("preferredName", out var preferred) ? Text(preferred, $"{email}.preferredName") : existing?.PreferredName;
                var manager = record.TryGetProperty("managerEmail", out var managerValue)
                    ? managerValue.ValueKind == JsonValueKind.Null ? null : Email(managerValue, $"{email}.managerEmail")
                    : existing?.ManagerEmail;
                if (existing is null && (directoryName is null || !record.TryGetProperty("managerEmail", out _)))
                    throw new FormatException($"{email}: creation requires displayName and explicit managerEmail (null means no Manager).");
                var archived = record.TryGetProperty("archived", out var archival)
                    ? Boolean(archival, $"{email}.archived") : existing?.Archived ?? false;
                var photoPath = existing?.PhotoPath;
                string? photoHash = null;
                if (record.TryGetProperty("photoPath", out var photo))
                {
                    if (photo.ValueKind == JsonValueKind.Null) photoPath = null;
                    else
                    {
                        var relativePath = Text(photo, $"{email}.photoPath", false)!;
                        if (importDirectory is null)
                            throw new FormatException($"{email}.photoPath: select an import JSON file so local photos have a containing directory.");
                        var resource = ReadPhotoResource(importDirectory, relativePath, $"{email}.photoPath");
                        photoHash = resource.Hash;
                        photoPath = photoHash.ToLowerInvariant() + resource.Extension;
                        proposedPhotos[photoPath] = resource.Bytes;
                        resources.Add(resource);
                    }
                    if (photoPath != existing?.PhotoPath)
                        changes.Add($"{email}: Photo {(photoPath is null ? "→ (clear)" : "→ supplied local image")}");
                }
                var next = (existing ?? new PersonProfile(email, directoryName, preferredName, manager)) with
                { DirectoryName = directoryName, PreferredName = preferredName, ManagerEmail = manager, Archived = archived, PhotoPath = photoPath };
                foreach (var (field, property, _) in TextFields)
                    if (record.TryGetProperty(property, out var value))
                        next = WithText(next, field, ValidateText(value, field, $"{email}.{property}"));
                proposed[email] = next;
                AddOlderEvidenceWarnings(email, record, observedAt, warnings);
                if (existing is null) changes.Add($"Add {email}: Manager = {manager ?? "(none)"}");
                foreach (var (field, _, label) in TextFields)
                {
                    var previous = existing is null ? null : ReadText(existing, field);
                    var current = ReadText(next, field);
                    if (previous != current)
                        changes.Add($"{email}: {label} {previous ?? "(none)"} → {current ?? "(clear)"}");
                }
                if (existing is not null && !string.Equals(existing.ManagerEmail, manager, StringComparison.OrdinalIgnoreCase))
                    changes.Add($"{email}: Manager {existing.ManagerEmail ?? "(none)"} → {manager ?? "(clear)"}");
                if (existing?.Archived != archived && archived) changes.Add($"Archive {email}: retain profile and local context.");
                if (existing?.Archived == true && !archived) changes.Add($"Reactivate {email} in the current reporting tree.");
                if (changes.Count == priorChanges)
                {
                    var supplied = record.EnumerateObject().Where(p => p.Name != "email")
                        .Select(p => ProfileFieldLabel(p.Name)).ToArray();
                    changes.Add($"{email}: No explicit field changes; omitted values and people stay unchanged." +
                        (supplied.Length == 0 ? "" : $" Record explicit evidence for {string.Join(", ", supplied)} without a Profile change."));
                }
                canonical.Add(string.Join("|", record.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Name) + ":" + (p.Name is "email" or "managerEmail" && p.Value.ValueKind == JsonValueKind.String
                        ? JsonSerializer.Serialize(p.Value.GetString()!.ToLowerInvariant())
                        : p.Value.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(p.Value.GetString()) : p.Value.GetRawText())))
                    + (photoHash is null ? "" : "|photoBytes:" + photoHash));
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                observed + "|" + (root.TryGetProperty("producer", out producer) ? producer.GetString() : "") + "|" + string.Join("\n", canonical.Order(StringComparer.Ordinal)))));
            if (batches.TryGetValue(batchId, out var accepted))
            {
                if (accepted.Fingerprint != fingerprint) throw new FormatException("batchId: already accepted with different logical data.");
                changes.Clear();
                warnings.Clear();
                changes.Add("Already applied: exact replay is a no-op.");
            }
            else
            {
                ValidateGraph(proposed);
                PreviewTeamNoteReview(proposed, changes);
            }
            var preview = new ImportPreview(id, errors.Count == 0, changes, errors) { Warnings = warnings };
            if (preview.CanApply) pending = new Pending(preview, proposed, batchId, fingerprint, root.Clone(), proposedPhotos, resources);
            Status = preview.CanApply ? "Preview ready. Review changes before Apply." : string.Join("\n", errors);
            return preview;
        }
        catch (Exception ex) when (StorageError(ex) || ex is InvalidOperationException or KeyNotFoundException)
        {
            errors.Add(ex.Message);
            Status = "Import rejected: " + string.Join("\n", errors);
            return new ImportPreview(id, false, changes, errors);
        }
    }

    public bool ApplyImport(Guid previewId)
    { lock (workspaceGate) return ApplyImportCore(previewId); }

    private bool ApplyImportCore(Guid previewId)
    {
        if (!workspaceCurrent) return Fail("Workspace unavailable; retained information is STALE. No import can apply.");
        if (pending is null || pending.Preview.Id != previewId) return Fail("Preview is missing or stale. Preview again.");
        try
        {
            if ((importPath is not null && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(importPath))) != importHash) ||
                (!memory && WorkspaceFingerprint() != revision) || !PhotoResourcesUnchanged(pending.Resources))
            {
                pending = null;
                return Fail("Workspace or import file changed. Reopen and preview again before applying.");
            }
        }
        catch (Exception ex) when (StorageError(ex))
        {
            pending = null;
            return Fail("Preview could not be revalidated: " + ex.Message);
        }
        if (batches.ContainsKey(pending.BatchId))
        {
            pending = null;
            Status = "Already applied: no changes.";
            return true;
        }
        var appliedAt = timeProvider.GetUtcNow();
        var history = CaptureImportHistory(pending.Source, pending.People, appliedAt);
        var accepted = new Dictionary<string, AcceptedBatch>(batches)
        { [pending.BatchId] = new AcceptedBatch(pending.Fingerprint, pending.Source, appliedAt) };
        if (!Save(pending.People, accepted, pending.Photos, history)) { pending = null; return false; }
        photos = pending.Photos;
        people = pending.People;
        batches = accepted;
        profileHistory = history;
        pending = null;
        Status = "Import applied and saved locally.";
        return true;
    }

    public bool EditPreferredName(string email, string? preferredName) =>
        EditPersonText(email, PersonTextField.PreferredName, preferredName);

    private bool Fail(string message) { Status = message; return false; }
    public ImportPreview PreviewImportFile(string path)
    { lock (workspaceGate) return PreviewImportFileCore(path); }

    private ImportPreview PreviewImportFileCore(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            var preview = PreviewImport(new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset),
                Path.GetDirectoryName(Path.GetFullPath(path)));
            importPath = Path.GetFullPath(path);
            importHash = Convert.ToHexString(SHA256.HashData(bytes));
            return preview;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            pending = null;
            Fail("Import file could not be read: " + ex.Message);
            return new ImportPreview(Guid.NewGuid(), false, [], [Status]);
        }
    }
    private static bool StorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException;
    private static void ValidateGraph(Dictionary<string, PersonProfile> proposed)
    {
        foreach (var person in proposed.Values)
        {
            if (person.ManagerEmail is { } manager)
            {
                if (!proposed.TryGetValue(manager, out var ancestor))
                    throw new FormatException($"{person.Email}.managerEmail: unknown Manager {manager}.");
                if (!person.Archived && ancestor.Archived)
                    throw new FormatException($"{person.Email}.managerEmail: active reports require an active Manager; {manager} is Archived. " +
                        "Explicitly reassign, clear the Manager relationship, or archive each affected active report. No cascade or reassignment is inferred.");
            }
        }
        var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in proposed.Values.Where(p => !p.Archived))
        {
            var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = person;
            while (!complete.Contains(current.Email))
            {
                if (!path.Add(current.Email))
                    throw new FormatException($"{person.Email}.managerEmail: active reporting cycle or self-reporting.");
                if (current.ManagerEmail is null) break;
                current = proposed[current.ManagerEmail];
            }
            complete.UnionWith(path);
        }
    }
    private string WorkspaceFingerprint(bool includePersonNotes = true)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in EnumerateWorkspaceFiles(workspace!)
            .Where(p => !p.EndsWith(".workspace.lock", StringComparison.OrdinalIgnoreCase) &&
                (includePersonNotes || !p.StartsWith(Path.Combine(workspace!, "notes") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) && !p.StartsWith(Path.Combine(workspace!, "team-notes") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))).Order())
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(workspace!, path)));
            hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private bool Save(Dictionary<string, PersonProfile> proposed, Dictionary<string, AcceptedBatch> accepted,
        Dictionary<string, byte[]>? proposedPhotos = null, Dictionary<string, PersonHistory>? history = null,
        string operation = "import")
    {
        var nextTeamContexts = NextTeamContextRevisions(proposed);
        var nextHistoricalContexts = CaptureReportingHistory(proposed, history ?? profileHistory);
        if (memory)
        {
            teamContextRevisions = nextTeamContexts;
            historicalTeamContexts = nextHistoricalContexts;
            failedSaves.Remove(operation);
            return true;
        }
        try
        {
            if (!workspaceCurrent) throw new IOException("Workspace unavailable; retained information is STALE. No changes saved.");
            using var lease = new FileStream(Path.Combine(workspace!, ".workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (WorkspaceFingerprint() != revision)
                throw new IOException("Workspace changed since opening or preview. Reopen and preview again; no changes saved.");
            var generation = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(workspace!, "generations", generation);
            Directory.CreateDirectory(Path.Combine(directory, "people"));
            foreach (var person in proposed.Values)
            {
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(person.Email.ToLowerInvariant()))).ToLowerInvariant();
                WriteDurably(Path.Combine(directory, "people", key + ".json"), person);
            }
            SavePhotos(directory, proposed, proposedPhotos ?? photos);
            WriteDurably(Path.Combine(directory, "team-context.json"), nextTeamContexts);
            WriteDurably(Path.Combine(directory, "batches.json"), accepted);
            WriteProfileHistory(directory, history ?? profileHistory);
            WriteHistoricalTeamContexts(directory, nextHistoricalContexts);
            var staged = Path.Combine(workspace!, "workspace.next");
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path)).Order(StringComparer.Ordinal).ToArray();
            WriteDurably(staged, new WorkspaceMetadata(1, generation, files));
            var destination = Path.Combine(workspace!, "workspace.json");
            if (File.Exists(destination)) File.Replace(staged, destination, Path.Combine(workspace!, "workspace.previous.json"));
            else File.Move(staged, destination);
            revision = WorkspaceFingerprint();
            CapturePersonNoteRevision();
            teamContextRevisions = nextTeamContexts;
            historicalTeamContexts = nextHistoricalContexts;
            failedSaves.Remove(operation);
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            return SaveFailed(operation, "Save failed; changes are NOT saved locally: " + ex.Message);
        }
    }
    private static void WriteDurably<T>(string path, T value)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value, JsonOptions);
        file.Flush(true);
    }
    private static string? Text(JsonElement value, string field, bool nullable = true)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"{field}: expected nonblank text{(nullable ? " or null" : "")}.");
        return value.GetString();
    }
    private static string Email(JsonElement value, string field)
    {
        var email = Text(value, field, false)!;
        if (!Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+$") || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email ||
            !email[(email.LastIndexOf('@') + 1)..].Split('.').All(label =>
                Regex.IsMatch(label, @"^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$")))
            throw new FormatException($"{field}: expected a plain email mailbox.");
        return email;
    }
    private static bool Boolean(JsonElement value, string field) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean()
            : throw new FormatException($"{field}: expected Boolean true or false; omission preserves the value.");
    private static void CheckProperties(JsonElement value, string[] allowed, string scope)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException($"{scope}: expected an object.");
        var seen = new HashSet<string>();
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new FormatException($"{scope}.{property.Name}: duplicate property.");
            if (!allowed.Contains(property.Name))
                throw new FormatException($"{scope}.{property.Name}: unsupported field in this app slice; nothing will be discarded or applied.");
        }
    }
    private static JsonElement Required(JsonElement value, string property, string scope) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) ? result
            : throw new FormatException($"{scope}.{property}: required field is missing or its record is not an object.");
}
