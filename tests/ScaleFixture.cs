using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

// Fixture construction uses the workflow; export uses the documented readable Workspace contract.
// This source is linked only into test projects, never the desktop product.
internal sealed class ScaleFixture : IDisposable
{
    internal const int Population = 10000;
    internal const string Target = "person01000@example.com";
    internal const string DeepTarget = "person00150@example.com";
    internal string Root { get; }
    private readonly bool ownsRoot;
    internal ScaleFixture(string? root = null)
    {
        ownsRoot = root is null;
        Root = root ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", "scale-" + Guid.NewGuid().ToString("N")));
    }
    internal string Workspace => Path.Combine(Root, "workspace");
    internal string Producer => Path.Combine(Root, "producer");
    internal static string Email(int index) => $"person{index:00000}@example.com";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly List<IApplicationWorkflow> clients = [];
    private readonly List<string> acceptedSources = [];
    internal Action<string> Log { get; init; } = Console.WriteLine;

    internal IApplicationWorkflow Open(bool memory = false)
    {
        var app = new WorkspaceViewModel(memory ? ApplicationWorkflow.CreateInMemory() : ApplicationWorkflow.CreateFileBacked());
        clients.Add(app);
        Check(app.OpenWorkspace(Workspace), app.Status);
        return app;
    }

    internal void Build()
    {
        Measure("fixture", () =>
        {
            Directory.CreateDirectory(Producer);
            for (var i = 0; i < 16; i++) File.WriteAllBytes(Path.Combine(Producer, $"photo{i}.png"), Png(i));
            using var app = ApplicationWorkflow.CreateInMemory();
            Check(app.OpenWorkspace(Workspace), app.Status);
            var import = Path.Combine(Producer, "initial.json");
            File.WriteAllText(import, Batch(Enumerable.Range(0, Population).Select(i => new
            {
                email = Email(i), displayName = $"Fictitious Person {i:00000}", preferredName = $"Reference {i:00000}",
                alias = $"handle{i:00000}", jobTitle = i < 111 ? "Manager" : "Research engineer",
                department = $"Division {i % 10}", officeBuilding = $"Building {i % 20}",
                officeNumber = $"{i % 1000:0000}B", officeState = i % 2 == 0 ? "Ontario" : "Washington",
                officeCountry = i % 2 == 0 ? "Canada" : "United States",
                linkedInProfileUrl = $"https://www.linkedin.com/in/fictitious-{i:00000}",
                managerEmail = i == 0 ? null : Email(i <= 10 ? 0 : i <= 110 ? 1 + (i - 11) % 10 :
                    i is >= 112 and <= 150 ? i - 1 : 11 + (i - 111) % 100),
                photoPath = i % 4 == 0 ? $"photo{i % 16}.png" : null,
                isFavorite = i % 1000 == 0
            }), 1));
            acceptedSources.Add(File.ReadAllText(import));
            ApplyFile(app, import);
            for (var i = 1000; i < 2000; i++)
                Check(app.CreatePersonNote(Email(i), $"Individual {i}", $"# Individual {i}\nOrchid private writing {i}.") is not null, app.Status);
            for (var i = 0; i < 111; i++)
                Check(app.CreateTeamNote(Email(i), $"Team {i}", $"# Team {i}\nCedar former context {i}.") is not null, app.Status);
            var transfer = Batch(Enumerable.Range(1000, 100).Select(i => new
            {
                email = Email(i), managerEmail = Email(i == 1011 ? 12 : 11), officeNumber = $"new-{i:0000}B"
            }), 2);
            Apply(app, transfer);
            acceptedSources.Add(transfer);
            var archival = Batch(Enumerable.Range(9900, 100).Select(i => new { email = Email(i), archived = true }), 3);
            Apply(app, archival);
            acceptedSources.Add(archival);
            Check(app.EditTeamNote(app.GetTeamNotes(Email(100))[0].Id, "Current Team 100", "# Pine current context 100."), app.Status);
            Export(app);
        }, Log);
    }

    internal static string Batch<T>(IEnumerable<T> people, int id) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, batchId = $"15000000-0000-4000-8000-{id:000000000000}",
        observedAt = $"2026-11-{id:00}T17:00:00Z", producer = "fictitious-scale-fixture", people
    });

    internal static void Apply(IApplicationWorkflow app, string json)
    {
        var preview = app.PreviewImport(json);
        Check(preview.CanApply, app.Status);
        Check(app.ApplyImport(preview.Id), app.Status);
    }

    internal static void ApplyFile(IApplicationWorkflow app, string path)
    {
        var preview = app.PreviewImportFile(path);
        Check(preview.CanApply, app.Status);
        Check(app.ApplyImport(preview.Id), app.Status);
    }

    internal static T Measure<T>(string operation, Func<T> action, Action<string>? log = null)
    {
        var timer = Stopwatch.StartNew();
        var result = action();
        (log ?? Console.WriteLine)($"SCALE {operation}: {timer.Elapsed.TotalMilliseconds:F1} ms");
        return result;
    }

    internal static void Measure(string operation, Action action, Action<string>? log = null) =>
        Measure(operation, () => { action(); return true; }, log);
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Export(IApplicationWorkflow app)
    {
        const string generationId = "15000000000040008000000000000001";
        var generation = Path.Combine(Workspace, "generations", generationId);
        foreach (var name in new[] { "people", "profile-history", "historical-team-context", "photos" })
            Directory.CreateDirectory(Path.Combine(generation, name));
        Directory.CreateDirectory(Path.Combine(Workspace, "notes"));
        Directory.CreateDirectory(Path.Combine(Workspace, "team-notes"));
        foreach (var person in app.People.Where(p => p.PhotoPath is not null).DistinctBy(p => p.PhotoPath))
            File.WriteAllBytes(Path.Combine(generation, "photos", person.PhotoPath!), app.GetPhoto(person.Email)!.Bytes.ToArray());
        Parallel.ForEach(app.People, new ParallelOptions { MaxDegreeOfParallelism = 8 }, person =>
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(person.Email))).ToLowerInvariant() + ".json";
            var record = JsonSerializer.SerializeToNode(person, Json)!.AsObject();
            record["schemaVersion"] = 2;
            Write(Path.Combine(generation, "people", key), record);
            var timeline = app.GetProfileTimeline(person.Email)!;
            Write(Path.Combine(generation, "profile-history", key), new
            {
                schemaVersion = 2, person.Email, changes = timeline.Changes, observations = timeline.Observations
            });
            var captures = app.GetHistoricalTeamContexts(person.Email);
            if (captures.Count > 0) Write(Path.Combine(generation, "historical-team-context", key),
                new { schemaVersion = 1, person.Email, contexts = captures });
            foreach (var note in app.GetPersonNotes(person.Email, true))
                WriteNote("notes", note.Id, new { schemaVersion = 1, kind = "person", note.Id, note.OwnerEmail,
                    note.Title, note.Archived, note.CreatedAt, note.UpdatedAt }, note.Markdown);
            foreach (var note in app.GetTeamNotes(person.Email, true))
                WriteNote("team-notes", note.Id, new { schemaVersion = 1, kind = "team", note.Id, note.OwnerEmail,
                    note.Title, note.Archived, note.CreatedAt, note.UpdatedAt, confirmedContextRevision = Guid.Empty }, note.Markdown);
        });
        Write(Path.Combine(generation, "team-context.json"), new Dictionary<string, Guid>());
        Write(Path.Combine(generation, "batches.json"), AcceptedLedger(app));
        Write(Path.Combine(Workspace, "workspace.json"), new
        {
            schemaVersion = 2, generation = generationId,
            files = Directory.GetFiles(generation, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(generation, p)).ToArray(),
            recordVersions = new { people = 2, profileHistory = 2, acceptedBatches = 1, personNotes = 1,
                teamNotes = 1, photos = 1, teamContext = 1, historicalTeamContext = 1 }
        });
    }

    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));

    private Dictionary<string, object> AcceptedLedger(IApplicationWorkflow app)
    {
        var ledger = new Dictionary<string, object>();
        var imageHashes = new Dictionary<string, string>();
        foreach (var json in acceptedSources)
        {
            using var document = JsonDocument.Parse(json);
            var source = document.RootElement;
            var id = source.GetProperty("batchId").GetGuid();
            var records = new List<string>();
            foreach (var person in source.GetProperty("people").EnumerateArray())
            {
                var record = string.Join("|", person.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Name) + ":" +
                        (p.Value.ValueKind == JsonValueKind.String
                            ? JsonSerializer.Serialize(p.Name is "email" or "managerEmail" ? p.Value.GetString()!.ToLowerInvariant() : p.Value.GetString())
                            : p.Value.GetRawText())));
                if (person.TryGetProperty("photoPath", out var photo) && photo.ValueKind == JsonValueKind.String)
                {
                    var path = photo.GetString()!;
                    if (!imageHashes.TryGetValue(path, out var hash))
                        imageHashes[path] = hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Producer, path))));
                    record += "|photoBytes:" + hash;
                }
                records.Add(record);
            }
            var logical = source.GetProperty("observedAt").GetString() + "|" + source.GetProperty("producer").GetString() +
                "|" + string.Join("\n", records.Order(StringComparer.Ordinal));
            var owner = source.GetProperty("people")[0].GetProperty("email").GetString()!;
            ledger[id.ToString()] = new
            {
                fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logical))),
                source = source.Clone(),
                appliedAt = app.GetProfileTimeline(owner)!.Observations.First(e => e.BatchId == id).RecordedAt
            };
        }
        return ledger;
    }
    private void WriteNote<T>(string folder, Guid id, T metadata, string markdown) =>
        File.WriteAllText(Path.Combine(Workspace, folder, id + ".md"), "---\n" + JsonSerializer.Serialize(metadata, Json) + "\n---\n" + markdown);

    private static byte[] Png(int seed)
    {
        using var image = new MemoryStream();
        image.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 128);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 128);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var pixels = new MemoryStream();
        using (var compressed = new ZLibStream(pixels, CompressionLevel.Fastest, true))
            for (var y = 0; y < 128; y++)
            {
                compressed.WriteByte(0);
                for (var x = 0; x < 128; x++) compressed.Write(new[] { (byte)(x + seed * 13), (byte)(y + seed * 7), (byte)(x + y) });
            }
        Chunk("IDAT", pixels.ToArray()); Chunk("IEND", []);
        return image.ToArray();

        void Chunk(string type, byte[] data)
        {
            var count = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(count, data.Length);
            image.Write(count);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            image.Write(body);
            uint crc = uint.MaxValue;
            foreach (var b in body)
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            }
            BinaryPrimitives.WriteUInt32BigEndian(count, ~crc); image.Write(count);
        }
    }

    public void Dispose()
    {
        foreach (var app in clients) app.Dispose();
        if (ownsRoot && Directory.Exists(Root))
        {
            Parallel.ForEach(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories),
                new ParallelOptions { MaxDegreeOfParallelism = 8 }, File.Delete);
            Directory.Delete(Root, true);
        }
    }
}
