using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Workflow;

internal sealed record PassiveFiniteCounts(long Attempted, long Emitted, long Filtered, long Dropped,
    long Overflow, long OutputFailures);
internal sealed record PassiveFiniteEnvelope(PassiveSaveRecord Record, string ScopeKey, long ScopedSequence,
    PassiveFiniteCounts Counts, bool NaturalClosure, PassiveFiniteCounts ProcessCounts,
    Dictionary<string, PassiveFiniteCounts>? StageAccounting);
internal sealed record PassiveFiniteLimits(int ScopeRecords = 32768, int ProcessRecords = 262144,
    int Scopes = 256, int Packets = 8192, long FileBytes = 32 * 1024 * 1024,
    long ProcessBytes = 128 * 1024 * 1024);
internal sealed record PassiveFinitePacket(int Schema, string ScopeKey, long FirstSequence, long LastSequence,
    PassiveFiniteCounts Counts, bool Complete, JsonElement[] Identity, PassiveSaveRecord[] Records,
    PassiveFiniteCounts ProcessCounts, Dictionary<string, PassiveFiniteCounts>? StageAccounting);
internal enum PassiveFiniteOutcome { Incomplete, PrefixFactsKnown, ObservationCovered }
internal sealed record PassiveFiniteResult(PassiveFiniteOutcome Outcome, PassiveCommitLink? PrefixCommit,
    bool WholeProcessComplete, bool SupportOnly, string Reason);
internal sealed record PassiveFiniteRequest(Guid Note, NoteScope Scope, string OwnerDigest, string RequestDigest,
    string SourceRevision, string RunId, string RunAttempt, string ClockDomain, string TargetRole,
    string HarnessRole, bool SupportOnly = false);

// D-chain only. A closed finite packet certifies its exact range, NEVER the remaining process lifetime.
internal sealed class PassiveFiniteCapture(string directory, object identity, PassiveFiniteLimits? limits = null) : IDisposable
{
    private sealed class OpenScope
    {
        internal long LastClosed, Written, LastSequence;
        internal int Part, Records;
        internal bool OutOfOrder;
        internal string? Path;
        internal StreamWriter? Writer;
        internal IncrementalHash? Hash;
        internal long Bytes;
    }
    private readonly PassiveFiniteLimits bounds = limits ?? new();
    private readonly Dictionary<string, OpenScope> scopes = [];
    private readonly List<JsonElement> identities = [JsonSerializer.SerializeToElement(identity, Json)];
    private long totalBytes;
    private int packetCount;
    private int totalRecords;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    internal void WholeStreamBytes(int bytes) => Charge(bytes);

    internal void Identity(object value)
    {
        identities.Add(JsonSerializer.SerializeToElement(value, Json));
        var key = "identity";
        var scope = GetScope(key);
        Open(key, scope);
        Close(key, scope, 0, new(0, 0, 0, 0, 0, 0), complete: true,
            new(0, 0, 0, 0, 0, 0), null);
    }

    internal void Append(PassiveFiniteEnvelope value)
    {
        if (++totalRecords > bounds.ProcessRecords) throw new IOException("Finite process record limit reached.");
        var scope = GetScope(value.ScopeKey);
        Open(value.ScopeKey, scope);
        if (value.ScopedSequence != scope.LastSequence + 1) scope.OutOfOrder = true;
        scope.LastSequence = value.ScopedSequence;
        scope.Written++;
        scope.Records++;
        if (scope.Written > bounds.ScopeRecords) throw new IOException("Finite scope record limit reached.");
        Write(scope, new { sequence = value.ScopedSequence, record = value.Record });
        if (value.NaturalClosure)
            Close(value.ScopeKey, scope, value.ScopedSequence, value.Counts,
                !scope.OutOfOrder && scope.Written == value.Counts.Attempted &&
                value.Counts.Attempted == value.Counts.Emitted && value.Counts.Dropped == 0 &&
                value.Counts.Overflow == 0 && value.Counts.OutputFailures == 0 &&
                value.ProcessCounts.Dropped == 0 && value.ProcessCounts.Overflow == 0 &&
                value.ProcessCounts.OutputFailures == 0 &&
                value.ProcessCounts.Attempted == value.ProcessCounts.Emitted + value.ProcessCounts.Filtered + value.ProcessCounts.Dropped,
                value.ProcessCounts, value.StageAccounting);
    }

    private OpenScope GetScope(string key)
    {
        if (scopes.TryGetValue(key, out var scope)) return scope;
        if (scopes.Count >= bounds.Scopes) throw new IOException("Finite process scope limit reached.");
        scopes.Add(key, scope = new());
        return scope;
    }

    private void Open(string key, OpenScope scope)
    {
        if (scope.Writer is not null) return;
        if (++packetCount > bounds.Packets) throw new IOException("Finite process packet limit reached.");
        Directory.CreateDirectory(directory);
        var name = PassiveSaveCapture.Digest(key);
        scope.Path = System.IO.Path.Combine(directory, $"{name}-{scope.Part++:D4}.packet.jsonl.open");
        scope.Writer = new(new FileStream(scope.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));
        scope.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        scope.Bytes = 0;
        scope.Records = 0;
        Write(scope, new
        {
            schema = 2,
            scopeKey = key,
            firstSequence = scope.LastClosed + 1,
            identity = identities.ToArray(),
            wholeProcessComplete = false
        });
    }

    private void Close(string key, OpenScope scope, long last, PassiveFiniteCounts counts, bool complete,
        PassiveFiniteCounts processCounts, Dictionary<string, PassiveFiniteCounts>? stageAccounting)
    {
        var digest = Convert.ToHexString(scope.Hash!.GetHashAndReset());
        Write(scope, new
        {
            schema = 2,
            scopeKey = key,
            lastSequence = last,
            counts,
            complete,
            processCounts,
            stageAccounting,
            contentSha256 = digest,
            naturalClosure = true
        }, hash: false);
        scope.Writer!.Flush();
        scope.Writer.Dispose();
        scope.Writer = null;
        scope.Hash.Dispose();
        scope.Hash = null;
        File.Move(scope.Path!, scope.Path![..^5], overwrite: false);
        scope.LastClosed = last;
    }

    private void Write(OpenScope scope, object value, bool hash = true)
    {
        var line = JsonSerializer.Serialize(value, Json) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        if (scope.Bytes + bytes.Length > bounds.FileBytes) throw new IOException("Finite packet file limit reached.");
        Charge(bytes.Length);
        scope.Bytes += bytes.Length;
        if (hash) scope.Hash!.AppendData(bytes);
        scope.Writer!.Write(line);
    }

    private void Charge(int bytes)
    {
        totalBytes += bytes;
        if (totalBytes > bounds.ProcessBytes) throw new IOException("Finite process output-byte limit reached.");
    }

    public void Dispose()
    {
        // Unclosed files remain .open and cannot be consumed as certified ranges.
        foreach (var scope in scopes.Values)
        {
            scope.Writer?.Dispose();
            scope.Hash?.Dispose();
        }
    }

    internal static PassiveFinitePacket ReadClosed(string path, PassiveFiniteLimits? limits = null)
    {
        var bounds = limits ?? new();
        if (!path.EndsWith(".packet.jsonl", StringComparison.Ordinal) || new FileInfo(path).Length > bounds.FileBytes)
            throw new InvalidDataException("Missing closed finite packet or file limit exceeded.");
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length < 2 || lines.Length > bounds.ScopeRecords + 2) throw new InvalidDataException("Invalid finite packet size.");
        using var first = JsonDocument.Parse(lines[0]);
        using var last = JsonDocument.Parse(lines[^1]);
        var header = first.RootElement;
        var footer = last.RootElement;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines[..^1]) + "\n")));
        if (header.GetProperty("schema").GetInt32() != 2 || footer.GetProperty("schema").GetInt32() != 2 ||
            header.GetProperty("scopeKey").GetString() != footer.GetProperty("scopeKey").GetString() ||
            !footer.GetProperty("naturalClosure").GetBoolean() || footer.GetProperty("contentSha256").GetString() != digest)
            throw new InvalidDataException("Finite packet closure/hash/schema mismatch.");
        var records = lines[1..^1].Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var low = header.GetProperty("firstSequence").GetInt64();
        var high = footer.GetProperty("lastSequence").GetInt64();
        if (high > bounds.ScopeRecords || low < 1 || high == 0 && records.Length != 0 || high != 0 && (high - low + 1 != records.Length ||
            !records.Select(value => value.GetProperty("sequence").GetInt64())
                .SequenceEqual(Enumerable.Range(0, records.Length).Select(index => low + index))))
            throw new InvalidDataException("Finite scoped sequence range is incomplete.");
        return new(2, header.GetProperty("scopeKey").GetString()!, low, high,
            footer.GetProperty("counts").Deserialize<PassiveFiniteCounts>(Json)!,
            footer.GetProperty("complete").GetBoolean(),
            header.GetProperty("identity").EnumerateArray().Select(value => value.Clone()).ToArray(),
            records.Select(value => value.GetProperty("record").Deserialize<PassiveSaveRecord>(Json)!).ToArray(),
            footer.GetProperty("processCounts").Deserialize<PassiveFiniteCounts>(Json)!,
            footer.GetProperty("stageAccounting").Deserialize<Dictionary<string, PassiveFiniteCounts>>(Json));
    }

    internal static PassiveFiniteResult Evaluate(IReadOnlyList<PassiveFinitePacket> packets, PassiveFiniteRequest request)
    {
        PassiveFiniteResult Unknown(string reason, PassiveCommitLink? positive = null) =>
            new(positive is null ? PassiveFiniteOutcome.Incomplete : PassiveFiniteOutcome.PrefixFactsKnown,
                positive, false, request.SupportOnly, reason);
        var bounds = new PassiveFiniteLimits();
        if (packets.Count == 0 || packets.Count > bounds.Packets ||
            packets.Sum(packet => packet.Records.Length) > bounds.ProcessRecords ||
            packets.Any(packet => packet.Schema != 2 || packet.LastSequence > bounds.ScopeRecords))
            return Unknown("Missing/incomplete finite packet or explicit output/drop/limit failure.");
        var clean = packets.Where(packet => packet.Complete && packet.Counts.Dropped == 0 &&
            packet.Counts.Overflow == 0 && packet.Counts.OutputFailures == 0 &&
            packet.Counts.Filtered == 0 &&
            packet.ProcessCounts.Dropped == 0 && packet.ProcessCounts.Overflow == 0 &&
            packet.ProcessCounts.OutputFailures == 0 &&
            packet.ProcessCounts.Attempted == packet.ProcessCounts.Emitted + packet.ProcessCounts.Filtered + packet.ProcessCounts.Dropped).ToArray();
        var records = clean.SelectMany(packet => packet.Records).ToArray();
        var positive = PassiveSaveCapture.FindPrefixCommitFacts(records, request.TargetRole, request.Note, request.RequestDigest);
        if (clean.Length != packets.Count)
            return Unknown("Later packet/output/drop/limit failure; only earlier certified facts may be known.", positive);
        foreach (var group in clean.Where(packet => packet.LastSequence != 0).GroupBy(packet =>
            (packet.ScopeKey, packet.Records.FirstOrDefault()?.Pid, packet.Records.FirstOrDefault()?.ProcessStartUtcTicks)))
        {
            long next = 1;
            foreach (var packet in group.OrderBy(packet => packet.FirstSequence))
            {
                if (packet.FirstSequence != next || packet.Counts.Attempted != packet.LastSequence ||
                    packet.Counts.Emitted != packet.LastSequence)
                    return Unknown("Missing/duplicate/ambiguous natural-prefix range.");
                next = packet.LastSequence + 1;
            }
        }
        if (records.Length == 0 || records.Any(record => !record.DiagnosticOnly || record.Schema != 1 ||
            record.Pid <= 0 || record.ProcessStartUtcTicks <= 0 || record.Thread <= 0 || record.QpcTicks <= 0 ||
            record.QpcFrequency <= 0 || record.ClockDomain != request.ClockDomain) ||
            records.Select(record => record.QpcFrequency).Distinct().Count() != 1)
            return Unknown("Missing identity or cross-runner/invalid clock domain.");
        var identities = packets.SelectMany(packet => packet.Identity).ToArray();
        bool Text(JsonElement value, string property, string expected) =>
            value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == expected;
        bool Number(JsonElement value, string property, long expected) =>
            value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetInt64(out var actual) && actual == expected;
        bool Bound(string role)
        {
            var owners = records.Where(record => record.Role == role)
                .Select(record => (record.Pid, record.ProcessStartUtcTicks, record.QpcFrequency)).Distinct().ToArray();
            return owners.Length == 1 && owners[0].Pid > 0 && owners[0].ProcessStartUtcTicks > 0 &&
                identities.Any(value => Text(value, "stage", "diagnostic-process-identity") && Text(value, "role", role) &&
                    Text(value, "revision", request.SourceRevision) && Text(value, "runId", request.RunId) &&
                    Text(value, "runAttempt", request.RunAttempt) && Text(value, "clockDomain", request.ClockDomain) &&
                    Number(value, "pid", owners[0].Pid) && Number(value, "processStartUtcTicks", owners[0].ProcessStartUtcTicks) &&
                    Number(value, "qpcFrequency", owners[0].QpcFrequency));
        }
        if (!Bound(request.TargetRole) || !Bound(request.HarnessRole)) return Unknown("Unbound finite source/run/process role.");
        var targetOwner = records.First(record => record.Role == request.TargetRole);
        if (!request.SupportOnly && !identities.Any(value => Text(value, "stage", "diagnostic-loaded-sqlite") &&
            Text(value, "role", request.TargetRole) && Number(value, "pid", targetOwner.Pid) &&
            Number(value, "processStartUtcTicks", targetOwner.ProcessStartUtcTicks) &&
            Text(value, "clockDomain", request.ClockDomain) && Text(value, "nativeVersion", "3.54.1") &&
            Text(value, "nativeSourceId", "2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76") &&
            Text(value, "moduleSha256", "922AB7FF7E47EF2694CA5F62ADBEFCC44C637BF5D082488C286816ED5D06363F") &&
            value.TryGetProperty("matchesPin", out var matched) && matched.ValueKind == JsonValueKind.True &&
            value.TryGetProperty("outsideProtectedInterval", out var outside) && outside.ValueKind == JsonValueKind.True))
            return Unknown("Actual target native identity is missing/unverified.");
        if (positive is null) return Unknown("Prefix Commit/submission/accepted lineage is missing or ambiguous.");
        var sample = records.Where(record => record.Role == request.HarnessRole && record.Fact.Note == request.Note &&
            record.Fact.Scope == request.Scope && record.Fact.OwnerDigest == request.OwnerDigest).ToArray();
        var closure = sample.Where(record => record.Fact.Stage == PassiveSaveStage.HarnessSampleClosed).ToArray();
        if (closure.Length != 1 || closure[0].Fact.Outcome != PassiveSaveOutcome.Completed ||
            !sample.Any(record => record.Fact.Stage == PassiveSaveStage.RequestEnd && record.Fact.InputReturned == true) ||
            !sample.Any(record => record.Fact.Stage == PassiveSaveStage.NativePredicate && record.Fact.Predicate == true))
            return Unknown("Later harness sample/outcome/native range remains unknown.", positive);
        foreach (var stage in new[] { PassiveSaveStage.ContinuedInputBegin, PassiveSaveStage.ContinuedInputContent,
            PassiveSaveStage.ContinuedInputEnd })
            if (!sample.Where(record => record.Fact.Stage == stage).Select(record => record.Fact.ContinuedInput)
                .Order().SequenceEqual(Enumerable.Range(0, 6).Select(index => (int?)index)))
                return Unknown("Original six continued inputs are missing/ambiguous.", positive);
        if (!records.Any(record => record.Role == request.TargetRole && record.Fact.Stage == PassiveSaveStage.FeedbackWrite &&
            record.Fact.Scope == request.Scope && record.Fact.SelectedNote == request.Note &&
            record.Fact.DisplayedNote == request.Note && record.Fact.OwnerDigest == request.OwnerDigest &&
            record.Fact.DisplayedOwnerDigest == request.OwnerDigest))
            return Unknown("Required actual target presentation prefix is missing.", positive);
        var acknowledgedAt = sample.Where(record => record.Fact.Stage == PassiveSaveStage.NativePredicate &&
            record.Fact.Predicate == true).Min(record => record.QpcTicks);
        if (!records.Any(record => record.Role == request.TargetRole && record.QpcTicks >= acknowledgedAt &&
            (record.Fact.Stage == PassiveSaveStage.FeedbackWrite && record.Fact.Scope == request.Scope ||
             record.Fact.Stage == PassiveSaveStage.SaveAccepted && record.Fact.Note == request.Note)))
            return Unknown("Required later natural target boundary through native acknowledgment remains unknown.", positive);
        return new(PassiveFiniteOutcome.ObservationCovered, positive, false, request.SupportOnly,
            "Only certified finite natural prefixes and the explicit harness closure; later target behavior/whole process unknown.");
    }
}
