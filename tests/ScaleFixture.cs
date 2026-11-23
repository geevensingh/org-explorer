using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

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
    internal string SynchronizationFolder => Path.Combine(Root, "snapshots");
    internal string Producer => Path.Combine(Root, "producer");
    internal static string Email(int index) => $"person{index:00000}@example.com";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly List<IApplicationWorkflow> clients = [];
    private readonly ConcurrentQueue<Exception> diagnosticFailures = new();
    private Action<string> writeLog = Console.WriteLine;
    internal Action<string> Log { get => WriteLog; init => writeLog = value; }

    private void WriteLog(string message)
    {
        try { writeLog(message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            diagnosticFailures.Enqueue(error);
        }
    }

    internal IApplicationWorkflow Open(bool memory = false, IWorkspaceFileObserver? fileObserver = null,
        IWorkspaceFilePublisher? filePublisher = null)
    {
        var app = new WorkspaceViewModel(memory ? ApplicationWorkflow.CreateInMemory() :
            ApplicationWorkflow.CreateFileBacked(filePublisher: filePublisher, fileObserver: new PhaseObserver(Log, fileObserver)),
            synchronizationTimeProvider: new SnapshotClock());
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
            using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: new PhaseObserver(Log));
            Check(app.OpenWorkspace(Workspace), app.Status);
            var import = Path.Combine(Producer, "initial.json");
            File.WriteAllText(import, Batch(Enumerable.Range(0, Population).Select(i => new
            {
                email = Email(i),
                displayName = $"Fictitious Person {i:00000}",
                preferredName = $"Reference {i:00000}",
                alias = $"handle{i:00000}",
                jobTitle = i < 111 ? "Manager" : "Research engineer",
                department = $"Division {i % 10}",
                officeBuilding = $"Building {i % 20}",
                officeNumber = $"{i % 1000:0000}B",
                officeState = i % 2 == 0 ? "Ontario" : "Washington",
                officeCountry = i % 2 == 0 ? "Canada" : "United States",
                linkedInProfileUrl = $"https://www.linkedin.com/in/fictitious-{i:00000}",
                managerEmail = i == 0 ? null : Email(i <= 10 ? 0 : i <= 110 ? 1 + (i - 11) % 10 :
                    i is >= 112 and <= 150 ? i - 1 : 11 + (i - 111) % 100),
                photoPath = i % 4 == 0 ? $"photo{i % 16}.png" : null,
                isFavorite = i % 1000 == 0
            }), 1));
            ApplyFile(app, import);
            for (var i = 1000; i < 2000; i++)
                Check(app.CreatePersonNote(Email(i), $"Individual {i}", $"# Individual {i}\nOrchid private writing {i}.") is not null, app.Status);
            for (var i = 0; i < 111; i++)
                Check(app.CreateTeamNote(Email(i), $"Team {i}", $"# Team {i}\nCedar former context {i}.") is not null, app.Status);
            var transfer = Batch(Enumerable.Range(1000, 100).Select(i => new
            {
                email = Email(i),
                managerEmail = Email(i == 1011 ? 12 : 11),
                officeNumber = $"new-{i:0000}B"
            }), 2);
            Apply(app, transfer);
            var archival = Batch(Enumerable.Range(9900, 100).Select(i => new { email = Email(i), archived = true }), 3);
            Apply(app, archival);
            Check(app.EditTeamNote(app.GetTeamNotes(Email(100))[0].Id, "Current Team 100", "# Pine current context 100."), app.Status);
            RequireRichPopulation(app);
            Measure("fixture-export", () =>
            {
                Check(app.ConfigureSynchronizationFolder(SynchronizationFolder), app.Status);
                Check(app.SyncNow(), app.Status);
            }, Log);
        }, Log);
    }

    internal static string Batch<T>(IEnumerable<T> people, int id) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        batchId = $"15000000-0000-4000-8000-{id:000000000000}",
        observedAt = $"2026-11-{id:00}T17:00:00Z",
        producer = "fictitious-scale-fixture",
        people
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
        var write = log ?? Console.WriteLine;
        var timer = Stopwatch.StartNew();
        write($"SCALE BEGIN {operation}; utc={DateTimeOffset.UtcNow:O}");
        var outcome = "Failed";
        try
        {
            var result = action();
            outcome = "Passed";
            write($"SCALE {operation}: {timer.Elapsed.TotalMilliseconds:F1} ms");
            return result;
        }
        finally { write($"SCALE END {operation}; outcome={outcome}; ms={timer.Elapsed.TotalMilliseconds:F1}; utc={DateTimeOffset.UtcNow:O}"); }
    }

    internal static void Measure(string operation, Action action, Action<string>? log = null) =>
        Measure(operation, () => { action(); return true; }, log);
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static object RequireRichPopulation(IApplicationWorkflow app, int additionalPersonNotes = 0,
        int additionalTeamNotes = 0, int historicalContexts = 200)
    {
        var people = app.People;
        var archived = app.ArchivedPeople.Count;
        var photoReferences = people.Count(person => person.PhotoPath is not null);
        var personNotes = people.Sum(person => app.GetPersonNotes(person.Email, true).Count);
        var teamNotes = people.Sum(person => app.GetTeamNotes(person.Email, true).Count);
        var captures = people.Sum(person => app.GetHistoricalTeamContexts(person.Email).Count);
        var observations = people.Sum(person => app.GetProfileTimeline(person.Email)!.Observations.Count);
        var changes = people.Sum(person => app.GetProfileTimeline(person.Email)!.Changes.Count);
        var chain = 0;
        for (var current = DeepTarget; app.GetRelationships(current)!.Manager is { } manager; current = manager.Email) chain++;
        Check(people.Count == 10000 && archived == 100 && photoReferences == 2500 &&
            personNotes == 1000 + additionalPersonNotes && teamNotes == 111 + additionalTeamNotes &&
            captures == historicalContexts && chain == 42 && observations >= 130300 && changes >= 300,
            "The complete rich population, retained notes, photos, 42-edge chain or Profile evidence was truncated.");
        return new { people = people.Count, active = people.Count - archived, archived, chainEdges = chain,
            photoReferences, personNotes, teamNotes, historicalContexts = captures,
            profileObservations = observations, profileChanges = changes };
    }

    private sealed class PhaseObserver(Action<string> log, IWorkspaceFileObserver? next = null) : IWorkspaceFileObserver
    {
        private readonly record struct IoCounts(long Writes, long Bytes, long Flushes, long Publications, long Deletions);
        private readonly ConcurrentDictionary<Guid, IoCounts> beginnings = new();
        private long writes, bytes, flushes, publications, deletions;

        public void Observe(WorkspaceFileEffect effect)
        {
            if (effect.Kind == WorkspaceFileEffectKind.Write)
            {
                Interlocked.Increment(ref writes);
                Interlocked.Add(ref bytes, effect.Bytes);
            }
            if (effect.Kind == WorkspaceFileEffectKind.Flush) Interlocked.Increment(ref flushes);
            if (effect.Kind == WorkspaceFileEffectKind.Publish) Interlocked.Increment(ref publications);
            if (effect.Kind == WorkspaceFileEffectKind.Delete) Interlocked.Increment(ref deletions);
            next?.Observe(effect);
        }

        private IoCounts Counts() => new(Interlocked.Read(ref writes), Interlocked.Read(ref bytes),
            Interlocked.Read(ref flushes), Interlocked.Read(ref publications), Interlocked.Read(ref deletions));

        private string Difference(IoCounts before)
        {
            var after = Counts();
            return $"writes={after.Writes - before.Writes}; bytes={after.Bytes - before.Bytes}; " +
                $"durable-flushes={after.Flushes - before.Flushes}; publications={after.Publications - before.Publications}; " +
                $"deletions={after.Deletions - before.Deletions}; inclusive=true";
        }

        public void Report(string name) => log($"SCALE COUNTS {name}; {Difference(default)}");

        public void Measure(string name, Action action)
        {
            var before = Counts();
            try { ScaleFixture.Measure(name, action, log); }
            finally { log($"SCALE COUNTS {name}; {Difference(before)}"); }
        }

        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            next?.ObservePhase(phase);
            if (phase.Kind == WorkspaceStoragePhaseKind.Begin)
            {
                beginnings[phase.Id] = Counts();
                log($"SCALE BEGIN {phase.Name}; id={phase.Id:N}; utc={DateTimeOffset.UtcNow:O}");
                return;
            }
            if (!beginnings.TryRemove(phase.Id, out var before))
                throw new InvalidOperationException("Storage phase completed without its beginning.");
            log($"SCALE END {phase.Name}; id={phase.Id:N}; outcome={phase.Kind}; ms={phase.Milliseconds:F1}; cpu-ms={phase.CpuMilliseconds:F1}; " +
                $"{Difference(before)}; utc={DateTimeOffset.UtcNow:O}");
        }
    }

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
        if (clients.Count != 0)
            Measure("fixture-close-clients", () =>
            {
                foreach (var app in clients) app.Dispose();
                foreach (var viewModel in clients.OfType<WorkspaceViewModel>())
                    while (viewModel.IsSynchronizing) Thread.Sleep(10);
            }, Log);
        if (ownsRoot && Directory.Exists(Root))
        {
            Measure("fixture-cleanup", () =>
            {
                Parallel.ForEach(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories),
                    new ParallelOptions { MaxDegreeOfParallelism = 8 }, File.Delete);
                Directory.Delete(Root, true);
            }, Log);
        }
        if (!diagnosticFailures.IsEmpty)
            throw new AggregateException("Scale diagnostic logging failed; see retained errors. Storage outcomes are unchanged.",
                diagnosticFailures.ToArray());
    }
}
