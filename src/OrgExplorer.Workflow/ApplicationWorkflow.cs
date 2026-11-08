using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed record PersonProfile(string Email, string? DirectoryName, string? PreferredName, string? ManagerEmail)
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
}

public sealed record ImportPreview(Guid Id, bool CanApply, IReadOnlyList<string> Changes, IReadOnlyList<string> Errors);

public partial interface IApplicationWorkflow
{
    IReadOnlyList<PersonProfile> People { get; }
    string Status { get; }
    bool OpenWorkspace(string path);
    ImportPreview PreviewImport(string json);
    ImportPreview PreviewImportFile(string path);
    bool ApplyImport(Guid previewId);
    bool EditPreferredName(string email, string? preferredName);
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
        string BatchId, string Fingerprint, JsonElement Source);
    private sealed record AcceptedBatch(string Fingerprint, JsonElement Source, DateTimeOffset AppliedAt);
    private sealed record WorkspaceMetadata(int SchemaVersion, string Generation);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private ApplicationWorkflow(bool memory, IProfileLinkOpener? profileLinkOpener)
    { this.memory = memory; this.profileLinkOpener = profileLinkOpener; }
    public static IApplicationWorkflow CreateInMemory(IProfileLinkOpener? profileLinkOpener = null) => new ApplicationWorkflow(true, profileLinkOpener);
    public static IApplicationWorkflow CreateFileBacked(IProfileLinkOpener? profileLinkOpener = null) => new ApplicationWorkflow(false, profileLinkOpener);
    public IReadOnlyList<PersonProfile> People => people.Values.OrderBy(p => p.Heading).ToArray();
    public string Status { get; private set; } = "Choose a Workspace.";

    public bool OpenWorkspace(string path)
    {
        pending = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var loaded = new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);
            var accepted = new Dictionary<string, AcceptedBatch>();
            if (!memory)
            {
                if (fullPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Choose a Workspace outside the application installation.");
                Directory.CreateDirectory(fullPath);
                var metadataPath = Path.Combine(fullPath, "workspace.json");
                if (File.Exists(metadataPath))
                {
                    var metadata = JsonSerializer.Deserialize<WorkspaceMetadata>(File.ReadAllText(metadataPath), JsonOptions)
                        ?? throw new FormatException("Invalid Workspace metadata.");
                    if (metadata.SchemaVersion != 1) throw new FormatException("Unsupported Workspace version. Upgrade the app; writes are blocked.");
                    if (!Regex.IsMatch(metadata.Generation, @"^[a-f0-9]{32}$")) throw new FormatException("Invalid Workspace generation.");
                    var generation = Path.Combine(fullPath, "generations", metadata.Generation);
                    foreach (var file in Directory.GetFiles(Path.Combine(generation, "people"), "*.json"))
                    {
                        var person = JsonSerializer.Deserialize<PersonProfile>(File.ReadAllText(file), JsonOptions)
                            ?? throw new FormatException("Invalid Person data.");
                        Email(JsonSerializer.SerializeToElement(person.Email), "email");
                        foreach (var (field, property, _) in TextFields)
                            ValidateText(JsonSerializer.SerializeToElement(ReadText(person, field)), field, $"{person.Email}.{property}");
                        if (!loaded.TryAdd(person.Email, person)) throw new FormatException("Duplicate Person identity in Workspace.");
                    }
                    ValidateGraph(loaded);
                    accepted = JsonSerializer.Deserialize<Dictionary<string, AcceptedBatch>>(File.ReadAllText(Path.Combine(generation, "batches.json")), JsonOptions)
                        ?? throw new FormatException("Invalid accepted batch data.");
                }
                else if (Directory.EnumerateFileSystemEntries(fullPath).Any())
                    throw new IOException("Folder is not an empty or recognized Workspace. Existing files will not be overwritten.");
            }
            var loadedNotes = memory ? [] : LoadPersonNotes(fullPath, loaded);
            workspace = fullPath;
            people = loaded;
            batches = accepted;
            personNotes = loadedNotes;
            revision = memory ? null : WorkspaceFingerprint();
            CapturePersonNoteRevision();
            Status = "Workspace opened.";
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            workspace = null;
            people.Clear();
            batches.Clear();
            personNotes.Clear();
            return Fail("Workspace could not be opened: " + ex.Message);
        }
    }

    public ImportPreview PreviewImport(string json)
    {
        importPath = null;
        importHash = null;
        pending = null;
        var errors = new List<string>();
        var changes = new List<string>();
        var id = Guid.NewGuid();
        if (workspace is null) errors.Add("Open a Workspace first.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            CheckProperties(root, ["schemaVersion", "batchId", "observedAt", "producer", "people"], "batch");
            if (root.GetProperty("schemaVersion").GetRawText() != "1")
                throw new FormatException("schemaVersion: unsupported import version; upgrade the app.");
            var batchId = root.GetProperty("batchId").GetString()!;
            if (!Guid.TryParseExact(batchId, "D", out var parsedId)) throw new FormatException("batchId: expected UUID.");
            batchId = parsedId.ToString();
            var observed = root.GetProperty("observedAt").GetString()!;
            if (!Regex.IsMatch(observed, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z$") ||
                !DateTimeOffset.TryParseExact(observed, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                throw new FormatException("observedAt: expected a real UTC calendar timestamp.");
            if (root.TryGetProperty("producer", out var producer)) Text(producer, "producer", false);
            var records = root.GetProperty("people");
            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
                throw new FormatException("people: expected a nonempty array.");
            var proposed = new Dictionary<string, PersonProfile>(people, StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var canonical = new List<string>();
            foreach (var record in records.EnumerateArray())
            {
                var email = Email(record.GetProperty("email"), "email");
                CheckProperties(record, ["email", "displayName", "preferredName", "alias", "jobTitle", "department",
                    "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl", "managerEmail"], email);
                if (!keys.Add(email)) throw new FormatException($"{email}: duplicate Person identity.");
                var existing = proposed.GetValueOrDefault(email);
                var directoryName = record.TryGetProperty("displayName", out var name) ? Text(name, $"{email}.displayName") : existing?.DirectoryName;
                var preferredName = record.TryGetProperty("preferredName", out var preferred) ? Text(preferred, $"{email}.preferredName") : existing?.PreferredName;
                var manager = record.TryGetProperty("managerEmail", out var managerValue)
                    ? managerValue.ValueKind == JsonValueKind.Null ? null : Email(managerValue, $"{email}.managerEmail")
                    : existing?.ManagerEmail;
                if (existing is null && (directoryName is null || !record.TryGetProperty("managerEmail", out _)))
                    throw new FormatException($"{email}: creation requires displayName and explicit managerEmail (null means no Manager).");
                var next = (existing ?? new PersonProfile(email, directoryName, preferredName, manager)) with
                { DirectoryName = directoryName, PreferredName = preferredName, ManagerEmail = manager };
                foreach (var (field, property, _) in TextFields)
                    if (record.TryGetProperty(property, out var value))
                        next = WithText(next, field, ValidateText(value, field, $"{email}.{property}"));
                proposed[email] = next;
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
                canonical.Add(string.Join("|", record.EnumerateObject().OrderBy(p => p.Name)
                    .Select(p => JsonSerializer.Serialize(p.Name) + ":" + (p.Name is "email" or "managerEmail" && p.Value.ValueKind == JsonValueKind.String
                        ? JsonSerializer.Serialize(p.Value.GetString()!.ToLowerInvariant())
                        : p.Value.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(p.Value.GetString()) : p.Value.GetRawText()))));
            }
            ValidateGraph(proposed);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                observed + "|" + (root.TryGetProperty("producer", out producer) ? producer.GetString() : "") + "|" + string.Join("\n", canonical.Order()))));
            if (batches.TryGetValue(batchId, out var accepted))
            {
                if (accepted.Fingerprint != fingerprint) throw new FormatException("batchId: already accepted with different logical data.");
                changes.Clear();
                changes.Add("Already applied: exact replay is a no-op.");
            }
            var preview = new ImportPreview(id, errors.Count == 0, changes, errors);
            if (preview.CanApply) pending = new Pending(preview, proposed, batchId, fingerprint, root.Clone());
            Status = preview.CanApply ? "Preview ready. Review changes before Apply." : string.Join("\n", errors);
            return preview;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            errors.Add(ex.Message);
            Status = "Import rejected: " + string.Join("\n", errors);
            return new ImportPreview(id, false, changes, errors);
        }
    }

    public bool ApplyImport(Guid previewId)
    {
        if (pending is null || pending.Preview.Id != previewId) return Fail("Preview is missing or stale. Preview again.");
        try
        {
            if ((importPath is not null && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(importPath))) != importHash) ||
                (!memory && WorkspaceFingerprint() != revision))
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
        var accepted = new Dictionary<string, AcceptedBatch>(batches)
        { [pending.BatchId] = new AcceptedBatch(pending.Fingerprint, pending.Source, DateTimeOffset.UtcNow) };
        if (!Save(pending.People, accepted)) { pending = null; return false; }
        people = pending.People;
        batches = accepted;
        pending = null;
        Status = "Import applied and saved locally.";
        return true;
    }

    public bool EditPreferredName(string email, string? preferredName) =>
        EditPersonText(email, PersonTextField.PreferredName, preferredName);

    private bool Fail(string message) { Status = message; return false; }
    public ImportPreview PreviewImportFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            var preview = PreviewImport(new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset));
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
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { person.Email };
            var manager = person.ManagerEmail;
            while (manager is not null)
            {
                if (!proposed.TryGetValue(manager, out var ancestor)) throw new FormatException($"{person.Email}.managerEmail: unknown Manager {manager}.");
                if (!visited.Add(manager)) throw new FormatException($"{person.Email}.managerEmail: reporting cycle.");
                manager = ancestor.ManagerEmail;
            }
        }
    }
    private string WorkspaceFingerprint(bool includePersonNotes = true)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(workspace!, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".workspace.lock", StringComparison.OrdinalIgnoreCase) &&
                (includePersonNotes || !p.StartsWith(Path.Combine(workspace!, "notes") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))).Order())
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(workspace!, path)));
            hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private bool Save(Dictionary<string, PersonProfile> proposed, Dictionary<string, AcceptedBatch> accepted)
    {
        if (memory) return true;
        try
        {
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
            WriteDurably(Path.Combine(directory, "batches.json"), accepted);
            var staged = Path.Combine(workspace!, "workspace.next");
            WriteDurably(staged, new WorkspaceMetadata(1, generation));
            var destination = Path.Combine(workspace!, "workspace.json");
            if (File.Exists(destination)) File.Replace(staged, destination, Path.Combine(workspace!, "workspace.previous.json"));
            else File.Move(staged, destination);
            revision = WorkspaceFingerprint();
            CapturePersonNoteRevision();
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            return Fail("Save failed; changes are NOT saved locally: " + ex.Message);
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
        if (!Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+$") || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            throw new FormatException($"{field}: expected a plain email mailbox.");
        return email;
    }
    private static void CheckProperties(JsonElement value, string[] allowed, string scope)
    {
        var seen = new HashSet<string>();
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new FormatException($"{scope}.{property.Name}: duplicate property.");
            if (!allowed.Contains(property.Name))
                throw new FormatException($"{scope}.{property.Name}: unsupported field in this app slice; nothing will be discarded or applied.");
        }
    }
}
