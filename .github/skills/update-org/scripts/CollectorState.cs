using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.OrgExport;

public sealed partial class ExportSession
{
    public const int CollectorStateVersion = 1;
    public const int StructuredBatchLimit = 25;
    private const string StandingPolicyReason =
        "Standing /update-org policy: verified reporting, bounded per-target recovery, explicit unavailable enrichment, denial stops and returned delays.";

    public sealed record CollectorIdentity(string Version, string AssemblySha256, string ProtocolSha256);
    public sealed record CollectorStart(string RunPath, int StateVersion, string Policy, int WorkerLimit,
        int StructuredTargets, int ProfileTargets, CollectorIdentity Collector);
    private sealed record StateUpgrade(Guid BatchId, string Reason, DateTimeOffset StartedAt,
        string OriginalRunSha256, string OriginalSourcesSha256, string UpgradedRunSha256,
        string UpgradedSourcesSha256, DateTimeOffset? CompletedAt = null);

    public static CollectorStart Start(string label, string rootId, string accountId, int workerLimit = 2,
        string? importsRoot = null)
    {
        Require(OperatingSystem.IsWindows(), "Start this collector in the verified local Windows runtime.");
        Require(workerLimit is 1 or 2, "Use one serial worker or at most two concurrent workers.");
        var path = CreateRun(label, rootId, accountId, importsRoot, bestEffort: true, workerLimit);
        var state = ReadRunState(System.IO.Path.Combine(path, "state.json"));
        return new(path, CollectorStateVersion, "best-effort", workerLimit, StructuredBatchLimit, 4,
            state.Collector ?? throw new InvalidOperationException("The collector identity was not recorded."));
    }

    private static string CreateRun(string label, string rootId, string accountId, string? importsRoot,
        bool bestEffort, int workerLimit)
    {
        Require(!string.IsNullOrWhiteSpace(label), "A root display label is required.");
        Require(Guid.TryParse(rootId, out _) && Guid.TryParse(accountId, out _),
            "Use the resolved directory root ID and signed-in WorkIQ user ID.");
        var root = importsRoot ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgExplorer", "Imports");
        Local(root);
        var state = new Run(Guid.NewGuid(), label, rootId, accountId, DateTimeOffset.UtcNow)
        {
            StateVersion = CollectorStateVersion, Collector = CurrentCollector(), WorkerLimit = workerLimit
        };
        var staging = System.IO.Path.Combine(root, ".staging", state.BatchId.ToString());
        Directory.CreateDirectory(staging);
        foreach (var name in new[] { "receipts", "profiles", "photos", "sources", "workers", "requests" })
            Directory.CreateDirectory(System.IO.Path.Combine(staging, name));
        Save(System.IO.Path.Combine(staging, "state.json"), state);
        Save(System.IO.Path.Combine(staging, "source-coordination.json"), new SourceLedger(state.BatchId, [])
        {
            StateVersion = CollectorStateVersion,
            BestEffortEnrichment = bestEffort ? new(state.StartedAt, StandingPolicyReason) : null
        });
        File.WriteAllBytes(System.IO.Path.Combine(staging, ".lease"), []);
        return staging;
    }

    private static CollectorIdentity CurrentCollector()
    {
        var assembly = typeof(ExportSession).Assembly;
        var protocol = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assembly.Location)!, "collection.md");
        Require(File.Exists(protocol), "The bundled collection protocol is missing; rebuild the producer.");
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? throw new InvalidOperationException("The collector version is unavailable.");
        return new(version, Hash(File.ReadAllBytes(assembly.Location)), Hash(File.ReadAllBytes(protocol)));
    }

    private static int StoredVersion(JsonObject value)
        => value["stateVersion"] is null ? 0 : value["stateVersion"]!.GetValue<int>();

    private static void RequireVersion(int version, bool allowLegacy = false)
    {
        Require(version >= 0, "Invalid collector state version.");
        if (version == 0 && !allowLegacy)
            throw new InvalidOperationException("This run is unversioned. Use upgrade-run RUN REASON before resuming; never reset its policy or attempts.");
        if (version > CollectorStateVersion)
            throw new NotSupportedException($"Collector state version {version} requires a newer producer; no state was changed.");
    }

    private static Run ReadRunState(string file, bool allowLegacy = false)
        => ReadRunState(Read<JsonObject>(file), allowLegacy);

    private static Run ReadRunState(JsonObject value, bool allowLegacy)
    {
        RequireVersion(StoredVersion(value), allowLegacy);
        return value.Deserialize<Run>(Json) ?? throw new InvalidOperationException("The collector run is missing.");
    }

    public static object UpgradeRun(string path, string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record why the stopped run is being upgraded.");
        path = System.IO.Path.GetFullPath(path);
        Local(path);
        var stateFile = System.IO.Path.Combine(path, "state.json");
        var sourcesFile = System.IO.Path.Combine(path, "source-coordination.json");
        var state = ReadRunState(stateFile, allowLegacy: true);
        Require(System.IO.Path.GetFileName(path) == state.BatchId.ToString()
            && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) == ".staging",
            "Upgrade only an unfinished staging run; completed exports remain immutable.");
        using var lease = new FileStream(System.IO.Path.Combine(path, ".lease"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        Require(!File.Exists(System.IO.Path.Combine(path, ".sealed")), "A sealed publication cannot be upgraded.");
        var directory = System.IO.Path.Combine(path, "state-upgrade");
        var journalFile = System.IO.Path.Combine(directory, "upgrade.json");
        if (File.Exists(journalFile))
        {
            var previous = Read<StateUpgrade>(journalFile);
            Require(previous.BatchId == state.BatchId, "State upgrade belongs to another run.");
            if (previous.CompletedAt is not null)
            {
                RequireVersion(state.StateVersion);
                RequireVersion(StoredVersion(Read<JsonObject>(sourcesFile)));
                return new { stateVersion = CollectorStateVersion, alreadyUpgraded = true, journalFile };
            }
            return FinishStateUpgrade(path, directory, journalFile, previous);
        }
        var originalState = File.ReadAllBytes(stateFile);
        var originalSources = File.ReadAllBytes(sourcesFile);
        var sources = Read<JsonObject>(sourcesFile);
        RequireVersion(StoredVersion(sources), allowLegacy: true);
        if (state.StateVersion == CollectorStateVersion && StoredVersion(sources) == CollectorStateVersion)
            return new { stateVersion = CollectorStateVersion, alreadyUpgraded = true, journalFile = (string?)null };
        Require(state.StateVersion == 0 && StoredVersion(sources) == 0, "The run has mismatched state versions; do not overwrite either file.");
        var known = JsonSerializer.SerializeToNode(new SourceLedger(state.BatchId, []), Json)!.AsObject()
            .Select(property => property.Key).ToHashSet(StringComparer.Ordinal);
        var unsupported = sources.Select(property => property.Key).Where(name => !known.Contains(name)).ToArray();
        Require(unsupported.Length == 0, "Unsupported historical collector policy fields: " + string.Join(", ", unsupported)
            + ". Use that run's original producer; original state and attempt budgets were not changed.");
        var ledger = sources.Deserialize<SourceLedger>(Json) ?? throw new InvalidOperationException("Missing source ledger.");
        Require(ledger.BatchId == state.BatchId && ledger.Reads.All(read => read.State != "active"),
            "Confirm all original source work stopped before upgrading; active claims cannot be reassigned or reset.");
        var upgradedState = JsonSerializer.SerializeToNode(state with
        {
            StateVersion = CollectorStateVersion, Collector = CurrentCollector()
        }, Json)!;
        sources["stateVersion"] = CollectorStateVersion;
        var stateBytes = Encoding.UTF8.GetBytes(upgradedState.ToJsonString(Json) + "\n");
        var sourceBytes = Encoding.UTF8.GetBytes(sources.ToJsonString(Json) + "\n");
        Directory.CreateDirectory(directory);
        Immutable(System.IO.Path.Combine(directory, "state.original.json"), originalState);
        Immutable(System.IO.Path.Combine(directory, "sources.original.json"), originalSources);
        SaveBytes(System.IO.Path.Combine(directory, "state.current.json"), stateBytes);
        SaveBytes(System.IO.Path.Combine(directory, "sources.current.json"), sourceBytes);
        var upgrade = new StateUpgrade(state.BatchId, reason, DateTimeOffset.UtcNow,
            Hash(originalState), Hash(originalSources), Hash(stateBytes), Hash(sourceBytes));
        Save(journalFile, upgrade);
        return FinishStateUpgrade(path, directory, journalFile, upgrade);
    }

    private static object FinishStateUpgrade(string path, string directory, string journalFile, StateUpgrade upgrade)
    {
        Require(Hash(File.ReadAllBytes(System.IO.Path.Combine(directory, "state.original.json"))) == upgrade.OriginalRunSha256
            && Hash(File.ReadAllBytes(System.IO.Path.Combine(directory, "sources.original.json"))) == upgrade.OriginalSourcesSha256,
            "The original state-upgrade snapshots changed.");
        foreach (var (name, staged, before, after) in new[]
        {
            ("state.json", "state.current.json", upgrade.OriginalRunSha256, upgrade.UpgradedRunSha256),
            ("source-coordination.json", "sources.current.json", upgrade.OriginalSourcesSha256, upgrade.UpgradedSourcesSha256)
        })
        {
            var file = System.IO.Path.Combine(path, name);
            var current = Hash(File.ReadAllBytes(file));
            Require(current == before || current == after, "Run state changed outside the recorded upgrade; preserve it for reconciliation.");
            var bytes = File.ReadAllBytes(System.IO.Path.Combine(directory, staged));
            Require(Hash(bytes) == after, "The staged state upgrade changed.");
            if (current != after) SaveBytes(file, bytes);
        }
        Save(journalFile, upgrade with { CompletedAt = DateTimeOffset.UtcNow });
        return new { stateVersion = CollectorStateVersion, alreadyUpgraded = false, journalFile };
    }
}
