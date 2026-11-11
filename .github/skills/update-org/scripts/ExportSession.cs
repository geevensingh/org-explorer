using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OrgExplorer.Workflow;

namespace OrgExplorer.OrgExport;

public sealed class ExportSession : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private const string Fields = "id,displayName,mail,userPrincipalName,jobTitle,department,officeLocation,city,state,country";
    private static readonly HashSet<string> EnrichedFields = new(StringComparer.Ordinal)
        { "jobTitle", "department", "officeBuilding", "officeNumber", "officeState", "officeCountry" };
    private readonly string path;
    private readonly Run run;
    private FileStream? lease;
    private Collection? complete;

    public sealed record Run(Guid BatchId, string Label, string RootId, string AccountId, DateTimeOffset StartedAt);
    public sealed record Receipt(string Path, DateTimeOffset ObservedAt, JsonObject Response);
    public sealed record Field(string State, string? Value, string Reason);
    public sealed record Observation(string DirectoryId, string Email, Dictionary<string, Field> Fields,
        string[] EvidenceFiles, string[] Warnings, string? Error, DateTimeOffset ObservedAt);
    public sealed record PhotoEvidence(string DirectoryId, string Etag, string ContentType, int SizeBytes,
        string Sha256, string PhotoPath, DateTimeOffset DownloadedAt, bool Reused);
    public sealed record Person(string Id, string Email, string Name, string? ManagerId, JsonObject Directory);
    public sealed record Collection(Person[] People, string[] Pending, string[] Errors, string[] Excluded,
        int SubtreeCount, Dictionary<string, Receipt> Receipts);

    private ExportSession(string path, Run run, FileStream lease)
        => (this.path, this.run, this.lease) = (path, run, lease);

    public static string Create(string label, string rootId, string accountId, string? importsRoot = null)
    {
        Require(!string.IsNullOrWhiteSpace(label), "A root display label is required.");
        Require(Guid.TryParse(rootId, out _) && Guid.TryParse(accountId, out _),
            "Use the resolved directory root ID and signed-in WorkIQ user ID.");
        var root = importsRoot ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgExplorer", "Imports");
        Local(root);
        var state = new Run(Guid.NewGuid(), label, rootId, accountId, DateTimeOffset.UtcNow);
        var staging = System.IO.Path.Combine(root, ".staging", state.BatchId.ToString());
        Directory.CreateDirectory(staging);
        foreach (var name in new[] { "receipts", "profiles", "photos", "sources", "workers" })
            Directory.CreateDirectory(System.IO.Path.Combine(staging, name));
        Save(System.IO.Path.Combine(staging, "state.json"), state);
        File.WriteAllBytes(System.IO.Path.Combine(staging, ".lease"), []);
        return staging;
    }

    public static ExportSession Open(string path, bool publishing = false)
    {
        path = System.IO.Path.GetFullPath(path);
        Local(path);
        var state = Read<Run>(System.IO.Path.Combine(path, "state.json"));
        Require(Guid.TryParse(state.RootId, out _) && Guid.TryParse(state.AccountId, out _),
            "The staging source identities are invalid.");
        Require(System.IO.Path.GetFileName(path) == state.BatchId.ToString()
            && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) == ".staging",
            "Only an unfinished staging run can be modified. Completed exports are immutable.");
        var lease = new FileStream(System.IO.Path.Combine(path, ".lease"), FileMode.Open,
            publishing ? FileAccess.ReadWrite : FileAccess.Read, publishing ? FileShare.None : FileShare.Read);
        if (File.Exists(System.IO.Path.Combine(path, ".sealed")))
        {
            lease.Dispose();
            throw new InvalidOperationException("Publication was sealed. Start a fresh run; verified photo bytes remain reusable.");
        }
        return new ExportSession(path, state, lease);
    }

    public object Ingest(string requestsFile, string responseFile)
    {
        complete = null;
        FreshPayload(responseFile);
        var requests = Read<string[]>(requestsFile);
        var body = Read<JsonObject>(responseFile);
        var results = body["results"] as JsonArray
            ?? throw new InvalidOperationException("Expected a complete WorkIQ fetch results envelope.");
        Require(requests.Length is > 0 and <= 50 && requests.Length == results.Count,
            "Requests and individual results must match; at most 50 paths per batch.");
        for (var index = 0; index < requests.Length; index++)
        {
            var request = Normalize(requests[index]);
            Require(request.StartsWith("/users/", StringComparison.Ordinal), "Only directory receipts belong here.");
            var response = results[index] as JsonObject
                ?? throw new InvalidOperationException("A result is missing its structured response.");
            Save(System.IO.Path.Combine(path, "receipts", Guid.NewGuid() + ".json"),
                new Receipt(request, DateTimeOffset.UtcNow, response.DeepClone().AsObject()));
        }
        return new { saved = requests.Length };
    }

    public Collection Plan()
    {
        var receipts = Directory.GetFiles(System.IO.Path.Combine(path, "receipts"), "*.json")
            .Select(Read<Receipt>).OrderBy(r => r.ObservedAt).ToArray();
        Require(receipts.All(r => r.ObservedAt >= run.StartedAt), "Old receipts cannot establish fresh collection.");
        var latest = receipts.GroupBy(r => r.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last());
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var users = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var managers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var subtree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        JsonObject? Get(string request, bool missingManager = false)
        {
            if (!latest.TryGetValue(request, out var receipt))
            {
                pending.Add(request);
                return null;
            }
            var status = receipt.Response["statusCode"]?.GetValue<int>();
            if (missingManager && status == 404 && ErrorCode(receipt.Response) == "Request_ResourceNotFound")
                return new JsonObject();
            if (status != 200 || receipt.Response["error"] is not null)
            {
                errors.Add($"{request}: {Diagnostic(receipt.Response)}");
                return null;
            }
            return receipt.Response["data"] as JsonObject
                ?? throw new InvalidOperationException($"Missing data for {request}.");
        }

        void User(JsonObject user)
        {
            var id = Text(user, "id");
            Require(Guid.TryParse(id, out _), "A directory user is missing its authoritative ID.");
            var type = Text(user, "@odata.type");
            Require(type.Length == 0 || type == "#microsoft.graph.user", "Unsupported directory object type: " + type);
            users[id] = user;
        }

        var root = Get(UserPath(run.RootId));
        if (root is not null)
        {
            Require(Text(root, "id") == run.RootId, "Root lookup returned a different directory identity.");
            User(root);
        }
        var queue = new Queue<string>();
        queue.Enqueue(run.RootId);
        while (queue.TryDequeue(out var id))
        {
            if (!subtree.Add(id)) continue;
            var request = ReportsPath(id);
            var pages = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                Require(pages.Add(request), "Cyclic reporting pagination.");
                var page = Get(request);
                if (page is null) break;
                var values = page["value"] as JsonArray
                    ?? throw new InvalidOperationException("Reporting collection has no value array.");
                foreach (var node in values)
                {
                    var child = node as JsonObject ?? throw new InvalidOperationException("Invalid report object.");
                    User(child);
                    var childId = Text(child, "id");
                    if (managers.TryGetValue(childId, out var oldManager) && oldManager != id)
                        errors.Add("Conflicting reporting parents for " + childId);
                    managers[childId] = id;
                    queue.Enqueue(childId);
                }
                var next = Text(page, "@odata.nextLink");
                if (next.Length == 0) break;
                request = Normalize(next);
                Require(request.Split('?')[0] == $"/users/{id}/directReports",
                    "Reporting continuation changed its source or relationship.");
            }
        }

        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = run.RootId;
        while (true)
        {
            Require(ancestors.Add(current), "Cyclic manager chain.");
            var manager = Get(ManagerPath(current), missingManager: true);
            if (manager is null) break;
            if (manager.Count == 0)
            {
                if (latest[ManagerPath(current)].Response["statusCode"]?.GetValue<int>() != 404)
                {
                    errors.Add("An empty manager payload does not establish a missing relationship: " + current);
                    break;
                }
                if (managers.TryGetValue(current, out var existing) && existing is not null)
                    errors.Add("Manager absence conflicts with reporting evidence for " + current);
                managers[current] = null;
                break;
            }
            User(manager);
            var id = Text(manager, "id");
            if (managers.TryGetValue(current, out var previous) && previous != id)
                errors.Add("Conflicting manager evidence for " + current);
            managers[current] = id;
            current = id;
        }

        var excluded = users.Where(pair => Alternate(Text(pair.Value, "displayName")))
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(!excluded.Contains(run.RootId), "Select the primary account, not an alternate account, as the root.");
        var people = new List<Person>();
        foreach (var (id, user) in users)
        {
            if (excluded.Contains(id)) continue;
            if (!managers.TryGetValue(id, out var managerId)) continue;
            if (managerId is not null && excluded.Contains(managerId))
                errors.Add("A retained person reports to an excluded alternate account: " + id);
            var email = Text(user, "mail");
            if (email.Length == 0) email = Text(user, "userPrincipalName");
            Require(email.Length > 0 && Text(user, "displayName").Length > 0, "Directory identity is incomplete: " + id);
            people.Add(new Person(id, email, Text(user, "displayName"), managerId, user));
        }
        Require(people.Select(p => p.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() == people.Count,
            "Different directory IDs resolve to the same email. Resolve this without merging by name.");
        return new Collection(people.ToArray(), pending.ToArray(), errors.ToArray(), excluded.ToArray(),
            people.Count(p => subtree.Contains(p.Id)), latest);
    }

    public object Jobs()
    {
        var collection = Plan();
        var jobs = new List<string>();
        if (collection.Pending.Length == 0 && collection.Errors.Length == 0)
            foreach (var group in collection.People.Chunk(16))
            {
                var name = System.IO.Path.Combine(path, "workers", group[0].Id + ".json");
                Immutable(name, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(group, Json) + "\n"));
                jobs.Add(name);
            }
        return new { people = collection.People.Length, collection.SubtreeCount, collection.Pending,
            collection.Errors, jobFiles = jobs,
            photoMetadataPaths = jobs.Count == 0 ? [] : collection.People.Select(p => PhotoPath(p.Id)).ToArray() };
    }

    public object Profiles(string inputsFile) => Read<string[]>(inputsFile)
        .Select(file => Profile(Read<Observation>(file).DirectoryId, file)).ToArray();

    public object CachedPhotos(string idsFile) => Read<string[]>(idsFile).Select(ReusePhoto).ToArray();

    public sealed record PhotoInput(string DirectoryId, string ResponseFile);
    public object Photos(string inputsFile) => Read<PhotoInput[]>(inputsFile)
        .Select(input => Photo(input.DirectoryId, input.ResponseFile)).ToArray();

    public object Profile(string id, string input)
    {
        var person = Complete().People.Single(p => p.Id == id);
        var observation = Read<Observation>(input);
        CheckObservation(observation, person);
        var evidence = observation.EvidenceFiles.Select(source =>
        {
            var full = System.IO.Path.GetFullPath(source);
            Require(full.StartsWith(path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase),
                "Evidence must be a staged source or a host-saved temporary tool result.");
            FreshPayload(full);
            var bytes = File.ReadAllBytes(full);
            var relative = "sources\\" + Hash(bytes) + ".json";
            Immutable(System.IO.Path.Combine(path, relative), bytes);
            return relative;
        }).ToArray();
        Save(System.IO.Path.Combine(path, "profiles", id + ".json"), observation with { EvidenceFiles = evidence });
        Require(observation.Error is null, person.Email + ": " + observation.Error);
        return new { directoryId = id, recorded = true };
    }

    private void CheckObservation(Observation observation, Person person)
    {
        var id = person.Id;
        Require(observation.DirectoryId == id && observation.Email.Equals(person.Email, StringComparison.OrdinalIgnoreCase),
            "Work-profile evidence does not match the assigned directory identity.");
        Require(observation.ObservedAt >= run.StartedAt && observation.ObservedAt <= DateTimeOffset.UtcNow.AddMinutes(5),
            "Work-profile evidence must be freshly queried in this run.");
        Require(observation.EvidenceFiles.Length > 0, "Retain the complete successful lookup evidence.");
        Require(observation.Fields.ContainsKey("officeState") && observation.Fields.ContainsKey("officeCountry"),
            "Account for both state and country, including explicit unknown observations.");
        foreach (var (name, field) in observation.Fields)
        {
            Require(EnrichedFields.Contains(name), "Not a producer-managed field: " + name);
            Require(field.State is "value" or "clear" or "unknown", "Invalid field observation state.");
            Require(!string.IsNullOrWhiteSpace(field.Reason), "A field needs source attribution or an uncertainty reason.");
            Require(field.State == "value" ? !string.IsNullOrWhiteSpace(field.Value) : field.Value is null,
                "Only a value observation may carry text.");
            if (field.State == "value")
                Require(!Placeholder(field.Value!), "Placeholder geography is unknown, not a value.");
        }
    }

    public object ReusePhoto(string id)
    {
        var metadata = PhotoMetadata(id);
        if (NoPhoto(metadata)) return new { reused = false, unavailable = true };
        Success(metadata, "Photo metadata");
        var etag = Text(metadata["data"]!.AsObject(), "@odata.mediaEtag");
        if (etag.Length == 0) return new { reused = false };
        var cache = CachePath(id, etag);
        PhotoEvidence? saved = null;
        string? bytesPath = null;
        if (File.Exists(cache))
        {
            Local(cache);
            saved = Read<PhotoEvidence>(cache);
            bytesPath = System.IO.Path.Combine(CacheRoot(), saved.Sha256 + Extension(saved.ContentType));
        }
        else
            foreach (var staging in Directory.GetDirectories(System.IO.Path.Combine(ImportsRoot(), ".staging")))
            {
                if (staging == path) continue;
                Local(staging);
                var previousFile = System.IO.Path.Combine(staging, "state.json");
                if (!File.Exists(previousFile)) continue;
                var previous = Read<Run>(previousFile);
                var record = System.IO.Path.Combine(staging, "photos", id + ".json");
                if (previous.AccountId != run.AccountId || !File.Exists(record)) continue;
                var candidate = Read<PhotoEvidence>(record);
                if (candidate.DirectoryId != id || candidate.Etag != etag) continue;
                saved = candidate;
                bytesPath = System.IO.Path.Combine(staging, "photos", id + Extension(candidate.ContentType));
                break;
            }
        if (saved is null || bytesPath is null) return new { reused = false };
        Require(saved.DirectoryId == id && saved.Etag == etag, "Photo cache identity mismatch.");
        Require(saved.Sha256.Length == 64 && saved.Sha256.All(char.IsAsciiHexDigit),
            "Invalid content-addressed photo cache key.");
        Local(bytesPath);
        var bytes = File.ReadAllBytes(bytesPath);
        Require(bytes.Length == saved.SizeBytes && Hash(bytes) == saved.Sha256, "Photo cache bytes changed.");
        WritePhoto(saved with { PhotoPath = "photos\\" + id + Extension(saved.ContentType), Reused = true }, bytes);
        return new { reused = true, directoryId = id };
    }

    public object Photo(string id, string input)
    {
        FreshPayload(input);
        var metadata = PhotoMetadata(id);
        Success(metadata, "Photo metadata");
        var body = Read<JsonObject>(input);
        Success(body, "Photo binary download");
        Require(body["sizeBytes"]?.GetValue<int>() is > 0 and <= 4 * 1024 * 1024,
            "Photo size is missing or exceeds the supported download limit.");
        var bytes = Convert.FromBase64String(Text(body, "base64Content"));
        Require(bytes.Length == body["sizeBytes"]!.GetValue<int>(), "Decoded photo length differs from source metadata.");
        var contentType = Text(body, "contentType");
        var extension = Extension(contentType);
        Require(contentType == "image/jpeg" ? bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 })
            : bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Photo MIME/magic mismatch.");
        var evidence = new PhotoEvidence(id, Text(metadata["data"]!.AsObject(), "@odata.mediaEtag"),
            contentType, bytes.Length, Hash(bytes), "photos\\" + id + extension, DateTimeOffset.UtcNow, false);
        WritePhoto(evidence, bytes);
        return new { directoryId = id, evidence.SizeBytes, evidence.Sha256 };
    }

    public object Publish()
    {
        var collection = Complete();
        var byId = collection.People.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var people = new List<Dictionary<string, object?>>();
        var warnings = new List<string>();
        var photos = new List<PhotoEvidence>();
        foreach (var person in collection.People)
        {
            var item = new Dictionary<string, object?>
            {
                ["email"] = person.Email, ["displayName"] = person.Name,
                ["managerEmail"] = person.ManagerId is null ? null : byId[person.ManagerId].Email
            };
            foreach (var field in new[] { "jobTitle", "department" })
                if (Text(person.Directory, field) is { Length: > 0 } value && !Placeholder(value)) item[field] = value;
                else warnings.Add(person.Email + ": directory " + field + " unknown; omitted unless work-profile evidence resolves it.");
            var observation = Read<Observation>(System.IO.Path.Combine(path, "profiles", person.Id + ".json"));
            CheckObservation(observation, person);
            Require(observation.Error is null, person.Email + ": " + observation.Error);
            Require(observation.DirectoryId == person.Id
                && observation.Email.Equals(person.Email, StringComparison.OrdinalIgnoreCase), "Profile receipt identity changed.");
            foreach (var source in observation.EvidenceFiles) Require(File.Exists(Within(source)), "Profile evidence is missing.");
            foreach (var (field, value) in observation.Fields)
            {
                if (value.State == "unknown") warnings.Add($"{person.Email}: {field}: {value.Reason}");
                else if (value.State == "clear") item[field] = null;
                else item[field] = field == "officeCountry"
                    && new[] { "US", "USA", "United States", "United States of America" }
                        .Contains(value.Value, StringComparer.OrdinalIgnoreCase) ? "USA" : value.Value;
            }
            warnings.AddRange(observation.Warnings.Select(w => person.Email + ": " + w));
            ApplyOffice(item, Text(person.Directory, "officeLocation"), warnings, person.Email);
            var metadata = PhotoMetadata(person.Id);
            if (NoPhoto(metadata)) warnings.Add(person.Email + ": directory photo unavailable; photoPath omitted.");
            else
            {
                Success(metadata, "Photo metadata");
                var photo = Read<PhotoEvidence>(System.IO.Path.Combine(path, "photos", person.Id + ".json"));
                Require(photo.DirectoryId == person.Id
                    && photo.Etag == Text(metadata["data"]!.AsObject(), "@odata.mediaEtag"), "Photo metadata changed.");
                var bytes = File.ReadAllBytes(Within(photo.PhotoPath));
                Require(bytes.Length == photo.SizeBytes && Hash(bytes) == photo.Sha256, "Photo changed after collection.");
                item["photoPath"] = photo.PhotoPath;
                photos.Add(photo);
            }
            people.Add(item);
        }
        var candidate = System.IO.Path.Combine(path, "updates.candidate.json");
        Save(candidate, new { schemaVersion = 1, batchId = run.BatchId,
            observedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            producer = "update-org local WorkIQ producer", people });
        using (var app = ApplicationWorkflow.CreateInMemory())
        {
            Require(app.OpenWorkspace("org-export-preview-" + run.BatchId), app.Status);
            var preview = app.PreviewImportFile(candidate);
            Require(preview.CanApply, string.Join(Environment.NewLine, preview.Errors));
            warnings.AddRange(preview.Warnings);
        }
        var hash = Hash(File.ReadAllBytes(candidate));
        Save(System.IO.Path.Combine(path, "coverage.json"), new
        {
            run.RootId, run.AccountId, run.StartedAt, completedAt = DateTimeOffset.UtcNow,
            people = people.Count, collection.SubtreeCount, ancestorCount = people.Count - collection.SubtreeCount,
            excludedAlternateIds = collection.Excluded, photos = photos.Count,
            unavailablePhotos = people.Count - photos.Count, updatesSha256 = hash,
            directoryUserIds = collection.People.ToDictionary(p => p.Email, p => p.Id),
            policy = "Refresh only; unknown fields and unavailable photos omitted; no archival or Workspace apply."
        });
        Save(System.IO.Path.Combine(path, "review-required.json"), warnings);
        Save(System.IO.Path.Combine(path, "photo-source.json"), photos);
        Save(System.IO.Path.Combine(path, "validation.json"), new
        {
            status = "passed", importSha256 = hash, checkedAt = DateTimeOffset.UtcNow,
            checks = new[] { "Complete reporting receipts and ancestor chain", "All profile and photo lookups accounted for",
                "Real app in-memory file preview, graph validation and image decoding", "Photo byte hashes" }
        });
        foreach (var photo in photos.Where(p => p.Etag.Length > 0))
        {
            Local(CacheRoot());
            Directory.CreateDirectory(CacheRoot());
            Immutable(System.IO.Path.Combine(CacheRoot(), photo.Sha256 + Extension(photo.ContentType)),
                File.ReadAllBytes(Within(photo.PhotoPath)));
            Save(CachePath(photo.DirectoryId, photo.Etag), photo);
        }
        var rootName = collection.People.Single(p => p.Id == run.RootId).Name;
        var slug = new string(rootName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').Take(48).ToArray());
        var destination = System.IO.Path.Combine(ImportsRoot(), $"Org-{slug}-{run.StartedAt:yyyyMMdd-HHmmss}-{run.BatchId:N}");
        Save(System.IO.Path.Combine(path, ".sealed"), new { importSha256 = hash });
        File.Move(candidate, System.IO.Path.Combine(path, "updates.json"));
        Dispose();
        Directory.Move(path, destination);
        return new { importPath = System.IO.Path.Combine(destination, "updates.json"), people = people.Count,
            photos = photos.Count, unavailablePhotos = people.Count - photos.Count, warningCount = warnings.Count };
    }

    public static string UserPath(string id) => $"/users/{id}?$select={Fields}";
    public static string ReportsPath(string id) => $"/users/{id}/directReports?$select={Fields}&$top=100";
    public static string ManagerPath(string id) => $"/users/{id}/manager?$select={Fields}";
    public static string PhotoPath(string id) => $"/users/{id}/photo?$select=id,width,height";
    public static bool Alternate(string name) => name.Contains("(NON EA SC ALT)", StringComparison.OrdinalIgnoreCase);

    private Collection Complete()
    {
        var collection = complete ?? Plan();
        Require(collection.Pending.Length == 0 && collection.Errors.Length == 0,
            "Directory collection is incomplete: " + JsonSerializer.Serialize(new { collection.Pending, collection.Errors }));
        return complete = collection;
    }

    private JsonObject PhotoMetadata(string id)
    {
        var collection = Complete();
        Require(Guid.TryParse(id, out _) && collection.People.Any(p => p.Id == id), "Photo is outside the retained population.");
        var receipts = collection.Receipts;
        Require(receipts.TryGetValue(PhotoPath(id), out _), "Fresh photo metadata is missing: " + id);
        return receipts[PhotoPath(id)].Response;
    }

    private void WritePhoto(PhotoEvidence evidence, byte[] bytes)
    {
        SaveBytes(Within(evidence.PhotoPath), bytes);
        Save(System.IO.Path.Combine(path, "photos", evidence.DirectoryId + ".json"), evidence);
    }

    private string ImportsRoot() => System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(path))!;
    private string CacheRoot() => System.IO.Path.Combine(ImportsRoot(), ".photo-cache", run.AccountId);
    private string CachePath(string id, string etag) => System.IO.Path.Combine(CacheRoot(), Hash(Encoding.UTF8.GetBytes(id + "\n" + etag)) + ".json");
    private string Within(string relative)
    {
        Require(!System.IO.Path.IsPathRooted(relative), "A local artifact reference must be relative.");
        var result = System.IO.Path.GetFullPath(System.IO.Path.Combine(path, relative));
        Require(result.StartsWith(path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Artifact escaped the staging folder.");
        Local(result);
        return result;
    }

    public static void ApplyOffice(Dictionary<string, object?> fields, string location, List<string> warnings, string email)
    {
        if (location.Equals("HOME OFFICE", StringComparison.OrdinalIgnoreCase)
            || location.Equals("NO WORKSPACE", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var field in new[] { "officeBuilding", "officeNumber" })
                if (!fields.TryAdd(field, null) && fields[field] is not null)
                    warnings.Add($"{email}: directory {location} conflicts with sourced {field}; retained work-profile observation.");
        }
        else if (!Placeholder(location))
        {
            var parts = location.Split('/');
            if (parts.Length > 2 || parts[0].Trim().Length == 0)
            {
                warnings.Add(email + ": ambiguous office location: " + location);
                return;
            }
            fields.TryAdd("officeBuilding", parts[0].Trim());
            if (parts.Length == 2 && parts[1].Trim() is { Length: > 0 } suffix
                && !suffix.Equals("Mobile", StringComparison.OrdinalIgnoreCase)
                && !suffix.Equals("REDMND", StringComparison.OrdinalIgnoreCase))
                fields.TryAdd("officeNumber", suffix);
            else if (!fields.ContainsKey("officeNumber")) warnings.Add(email + ": office number unknown: " + location);
        }
        else if (!fields.ContainsKey("officeBuilding")) warnings.Add(email + ": physical office unknown.");
    }

    private static bool NoPhoto(JsonObject body)
        => body["statusCode"]?.GetValue<int>() == 404 && ErrorCode(body) == "ImageNotFound";
    private static string ErrorCode(JsonObject body)
    {
        for (var error = body["error"] as JsonObject; error is not null; error = error["error"] as JsonObject)
            if (Text(error, "code") is { Length: > 0 } code) return code;
        return "";
    }
    private static void Success(JsonObject body, string operation)
        => Require(body["statusCode"]?.GetValue<int>() == 200 && body["error"] is null, operation + " failed: " + Diagnostic(body));
    private static string Diagnostic(JsonObject body)
        => $"HTTP {body["statusCode"]}: {body["error"]?.ToJsonString() ?? "missing structured data"}";
    private static bool Placeholder(string value) => string.IsNullOrWhiteSpace(value)
        || new[] { "Unassigned", "N/a", "Unknown", "Not available" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    private static string Text(JsonObject obj, string name) => obj[name]?.GetValue<string>()?.Trim() ?? "";
    private static string Extension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg", "image/png" => ".png",
        _ => throw new InvalidOperationException("Only JPEG and PNG photos are supported.")
    };
    public static string Normalize(string value)
    {
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(value);
            Require(uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
                && uri.AbsolutePath.StartsWith("/v1.0/users/", StringComparison.Ordinal), "Unexpected continuation source.");
            value = value[value.IndexOf('/', "https://".Length)..]["/v1.0".Length..];
        }
        Require(value.StartsWith("/users/", StringComparison.Ordinal) && !value.Contains('\\')
            && !value.Contains('#'), "Expected a supported server-relative directory path.");
        return value;
    }
    private static void Local(string file)
    {
        var full = System.IO.Path.GetFullPath(file);
        Require(!full.StartsWith(@"\\", StringComparison.Ordinal), "Use local, non-network storage.");
        foreach (var name in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } cloud)
                Require(!full.Equals(System.IO.Path.GetFullPath(cloud), StringComparison.OrdinalIgnoreCase)
                    && !full.StartsWith(System.IO.Path.GetFullPath(cloud).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase),
                    "Employee data must remain outside cloud-synced folders.");
        for (var current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Use paths without junctions or symbolic links.");
    }
    private static T Read<T>(string file)
    {
        Local(file);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json)
            ?? throw new InvalidOperationException("Missing JSON value: " + file);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Save(string file, object value) => SaveBytes(file, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n"));
    private static void SaveBytes(string file, byte[] bytes)
    {
        Local(file);
        var temp = file + "." + Guid.NewGuid();
        File.WriteAllBytes(temp, bytes);
        try { File.Move(temp, file, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Immutable(string file, byte[] bytes)
    {
        Local(file);
        if (File.Exists(file))
        {
            Require(File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes), "Existing evidence changed: " + file);
            return;
        }
        var temp = file + "." + Guid.NewGuid();
        File.WriteAllBytes(temp, bytes);
        try { File.Move(temp, file); }
        catch (IOException) when (File.Exists(file))
        {
            Require(File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes), "Concurrent evidence differs: " + file);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose()
    {
        lease?.Dispose();
        lease = null;
    }
    private void FreshPayload(string file)
    {
        Local(file);
        Require(File.Exists(file) && File.GetLastWriteTimeUtc(file) >= run.StartedAt.UtcDateTime,
            "Use the newly returned full tool payload, not a previous run's response.");
    }
}
