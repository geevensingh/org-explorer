using System.Diagnostics;
using System.Text.Json;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

[Collection("Passive save capture")]
public sealed class PassiveFiniteCaptureTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "finite-" + Guid.NewGuid().ToString("N")));
    private readonly Guid note = Guid.NewGuid(), incarnation = Guid.NewGuid(), call = Guid.NewGuid(), phase = Guid.NewGuid();
    private readonly string digest = PassiveSaveCapture.Digest("Fictitious prefix");
    private readonly string owner = PassiveSaveCapture.OwnerDigest("support@example.com");
    private object Identity => new
    {
        stage = "diagnostic-process-identity", role = "support-test", revision = new string('d', 40),
        runId = "run", runAttempt = "1", pid = 1, processStartUtcTicks = 1, qpcFrequency = Stopwatch.Frequency,
        clockDomain = "domain", diagnosticOnly = true
    };
    private PassiveFiniteRequest Request => new(note, NoteScope.Team, owner, digest, new string('d', 40),
        "run", "1", "domain", "support-test", "support-test", true);
    private PassiveSaveFact Fact(PassiveSaveStage stage) => new(stage, NoteScope.Team, note, owner,
        incarnation, RequestDigest: digest, Sample: 0, PrefixDigest: digest);

    private PassiveFinitePacket[] Build()
    {
        using var writer = new PassiveFiniteCapture(root, Identity);
        var next = new Dictionary<string, long>();
        long global = 0;
        void Add(PassiveSaveFact fact, string scope, bool close = false)
        {
            var sequence = next.GetValueOrDefault(scope) + 1;
            next[scope] = sequence;
            global++;
            var record = new PassiveSaveRecord(1, global, 1, 1, 1, Stopwatch.Frequency,
                global * 10, "support-test", "domain", true, fact);
            writer.Append(new(record, scope, sequence, new(sequence, sequence, 0, 0, 0, 0), close,
                new(global, global, 0, 0, 0, 0), new()));
        }
        Add(Fact(PassiveSaveStage.RequestBegin), "sample");
        Add(Fact(PassiveSaveStage.RequestEnd) with { InputReturned = true }, "sample");
        Add(Fact(PassiveSaveStage.DraftUpdated) with { Generation = 2 }, "note");
        Add(Fact(PassiveSaveStage.SaveSubmitted) with { Generation = 2, SubmittedGeneration = 2, SaveCall = call }, "note");
        Add(new(PassiveSaveStage.ActualCommit, SaveCall: call, Phase: phase, PhaseKind: WorkspaceStoragePhaseKind.Begin), "note");
        Add(new(PassiveSaveStage.ActualCommit, SaveCall: call, Phase: phase, PhaseKind: WorkspaceStoragePhaseKind.End), "note");
        Add(Fact(PassiveSaveStage.SaveAccepted) with
        { SaveCall = call, SubmittedGeneration = 2, LatestGeneration = 2, Accepted = true,
            Retained = false, Removed = true, SuccessorBasis = false }, "note", true);
        for (var index = 0; index < 6; index++)
            foreach (var stage in new[] { PassiveSaveStage.ContinuedInputBegin, PassiveSaveStage.ContinuedInputContent,
                PassiveSaveStage.ContinuedInputEnd })
                Add(Fact(stage) with { ContinuedInput = index, Predicate = true }, "sample");
        Add(Fact(PassiveSaveStage.NativePredicate) with { Predicate = true }, "sample");
        Add(Fact(PassiveSaveStage.FeedbackWrite) with
        { SelectedNote = note, DisplayedNote = note, DisplayedOwnerDigest = owner,
            Feedback = PassiveSaveFeedback.ScopedLocalSave }, "presentation", true);
        Add(Fact(PassiveSaveStage.HarnessSampleClosed) with { Outcome = PassiveSaveOutcome.Completed }, "sample", true);
        // Remaining later work is open; finite proof must never claim a completed process lifetime.
        Add(Fact(PassiveSaveStage.NativePredicate), "later-unclosed");
        return Directory.GetFiles(root, "*.packet.jsonl").Select(path => PassiveFiniteCapture.ReadClosed(path)).ToArray();
    }

    [Fact]
    public void Natural_closed_packets_cover_finite_observation_while_process_and_later_work_remain_incomplete()
    {
        var packets = Build();
        Assert.True(Directory.GetFiles(root, "*.open").Length > 0);
        var result = PassiveFiniteCapture.Evaluate(packets, Request);
        Assert.Equal(PassiveFiniteOutcome.ObservationCovered, result.Outcome);
        Assert.False(result.WholeProcessComplete);
        Assert.True(result.SupportOnly);
        Assert.NotNull(result.PrefixCommit);
        Assert.Null(PassiveSaveCapture.LinkCommittedPrefix(packets.SelectMany(packet => packet.Records).ToArray(),
            "support-test", note, digest, diagnosticsComplete: false));
    }

    [Theory]
    [InlineData("sample")]
    [InlineData("presentation")]
    public void Missing_later_scope_is_unknown_without_erasing_positive_earlier_commit_facts(string missing)
    {
        var result = PassiveFiniteCapture.Evaluate(Build().Where(packet => packet.ScopeKey != missing).ToArray(), Request);
        Assert.NotEqual(PassiveFiniteOutcome.ObservationCovered, result.Outcome);
        if (missing == "presentation") Assert.Equal(PassiveFiniteOutcome.PrefixFactsKnown, result.Outcome);
        Assert.False(result.WholeProcessComplete);
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("output")]
    [InlineData("sequence")]
    [InlineData("role")]
    [InlineData("clock")]
    public void Drop_output_range_role_and_clock_refusals_never_become_observation_coverage(string kind)
    {
        var packets = Build();
        var changed = packets.Select(packet => kind switch
        {
            "drop" => packet with { Counts = packet.Counts with { Dropped = 1 }, Complete = false },
            "output" => packet with { ProcessCounts = packet.ProcessCounts with { OutputFailures = 1 }, Complete = false },
            "sequence" => packet with { FirstSequence = packet.FirstSequence + 1 },
            "role" => packet with { Records = packet.Records.Select(record => record with { Role = "another-process-role" }).ToArray() },
            _ => packet with { Records = packet.Records.Select(record => record with { ClockDomain = "another-worker" }).ToArray() }
        }).ToArray();
        Assert.NotEqual(PassiveFiniteOutcome.ObservationCovered, PassiveFiniteCapture.Evaluate(changed, Request).Outcome);
    }

    [Fact]
    public void Partial_corrupted_or_open_packet_files_are_refused()
    {
        Build();
        Assert.Throws<InvalidDataException>(() => PassiveFiniteCapture.ReadClosed(Directory.GetFiles(root, "*.open")[0]));
        var path = Directory.GetFiles(root, "*.packet.jsonl")[0];
        var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("\"schema\":2", "\"schema\":3", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => PassiveFiniteCapture.ReadClosed(path));
        File.WriteAllText(path, original[..(original.Length / 2)]);
        Assert.ThrowsAny<Exception>(() => PassiveFiniteCapture.ReadClosed(path));
    }

    [Theory]
    [InlineData("ambiguous-request")]
    [InlineData("later-target-boundary")]
    [InlineData("failed-harness-outcome")]
    [InlineData("accounting")]
    public void Ambiguous_requests_missing_later_boundaries_failed_outcomes_and_accounting_remain_unknown(string kind)
    {
        var packets = Build();
        var changed = packets.Select(packet => packet with
        {
            ProcessCounts = kind == "accounting" ? packet.ProcessCounts with { Filtered = 1 } : packet.ProcessCounts,
            Records = packet.Records.SelectMany(record => kind == "ambiguous-request" &&
                record.Fact.Stage == PassiveSaveStage.RequestBegin ? new[] { record, record } : new[]
            {
                kind == "later-target-boundary" && record.Fact.Stage == PassiveSaveStage.FeedbackWrite
                    ? record with { QpcTicks = 1 }
                    : kind == "failed-harness-outcome" && record.Fact.Stage == PassiveSaveStage.HarnessSampleClosed
                        ? record with { Fact = record.Fact with { Outcome = PassiveSaveOutcome.Failed } } : record
            }).ToArray()
        }).ToArray();
        Assert.NotEqual(PassiveFiniteOutcome.ObservationCovered, PassiveFiniteCapture.Evaluate(changed, Request).Outcome);
    }

    [Theory]
    [InlineData("scope-records")]
    [InlineData("scope-count")]
    [InlineData("process-records")]
    [InlineData("packet-count")]
    [InlineData("file-bytes")]
    [InlineData("process-bytes")]
    public void Finite_scope_process_and_file_limits_surface_refusal_and_leave_no_accepting_packet(string kind)
    {
        var limits = kind switch
        {
            "scope-records" => new PassiveFiniteLimits(ScopeRecords: 0),
            "scope-count" => new PassiveFiniteLimits(Scopes: 0),
            "process-records" => new PassiveFiniteLimits(ProcessRecords: 0),
            "packet-count" => new PassiveFiniteLimits(Packets: 0),
            "file-bytes" => new PassiveFiniteLimits(FileBytes: 32),
            _ => new PassiveFiniteLimits(ProcessBytes: 32)
        };
        using var writer = new PassiveFiniteCapture(root, Identity, limits);
        var record = new PassiveSaveRecord(1, 1, 1, 1, 1, Stopwatch.Frequency, 1, "support-test", "domain", true,
            Fact(PassiveSaveStage.RequestBegin));
        Assert.Throws<IOException>(() => writer.Append(new(record, "sample", 1, new(1, 1, 0, 0, 0, 0), true,
            new(1, 1, 0, 0, 0, 0), new())));
        Assert.True(!Directory.Exists(root) || Directory.GetFiles(root, "*.packet.jsonl").Length == 0);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
