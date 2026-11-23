using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrgExplorer.Workflow;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

[CollectionDefinition("Passive save capture", DisableParallelization = true)]
public sealed class PassiveSaveCaptureCollection;

[Collection("Passive save capture")]
public sealed class PassiveSaveCaptureTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() }
    };
    private static JsonElement[] Read(MemoryStream output) => Encoding.UTF8.GetString(output.ToArray())
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    private static PassiveSaveRecord[] Facts(MemoryStream output) => Read(output)
        .Where(value => value.TryGetProperty("fact", out _))
        .Select(value => value.Deserialize<PassiveSaveRecord>(Json)!).ToArray();

    [Fact]
    public async Task A_blocked_owned_drain_never_blocks_producers_and_overflow_refuses_complete_diagnosis()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var output = new MemoryStream();
        using var capture = PassiveSaveCapture.Install("support-test", output: () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Controlled support output refusal.");
            return output;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                for (var index = 0; index <= PassiveSaveCapture.Capacity; index++)
                    PassiveSaveCapture.Emit(new(PassiveSaveStage.NativePredicate, Sample: 0,
                        Note: Guid.Parse("10000000-0000-4000-8000-000000000001")));
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(release.IsSet);
            Assert.True(capture.Dropped > 0);
        }
        finally { release.Set(); capture.Dispose(); }
        Assert.True(capture.WaitForDrain(TimeSpan.FromSeconds(5)));
        var records = Read(output);
        var end = records.Single(value => value.TryGetProperty("stage", out var stage) && stage.GetString() == "diagnostic-stream-end");
        Assert.False(end.GetProperty("diagnosticsComplete").GetBoolean());
        Assert.True(end.GetProperty("overflow").GetInt64() > 0);
        Assert.Equal(PassiveSaveCapture.Capacity, Facts(output).Length);
    }

    [Fact]
    public void Private_text_is_refused_and_output_failure_does_not_replace_the_original_action_exception()
    {
        var primary = new IOException("Original action sentinel");
        using var capture = PassiveSaveCapture.Install("support-test", output: () =>
            throw new IOException("Private output error text must not be recorded"));
        PassiveSaveCapture.Emit(new(PassiveSaveStage.DraftUpdated, OwnerDigest: "PRIVATE-OWNER-SENTINEL"));
        var actual = Assert.Throws<IOException>(() => NoteCommitObservation.Run(() => throw primary,
            () => false, _ => throw new InvalidOperationException("The failed request must not enter its body.")));
        Assert.Same(primary, actual);
        capture.Dispose();
        Assert.True(capture.WaitForDrain(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, capture.OutputFailures);
        Assert.Equal(1, capture.Dropped);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Real_commit_correlation_retains_newer_generations_and_resets_incarnation_after_clean_removal(NoteScope scope)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", Guid.NewGuid().ToString("N")));
        var output = new MemoryStream();
        using var capture = PassiveSaveCapture.Install("support-test", output: () => output);
        using var boundary = new HeldCommit();
        var beforeCorrelation = Trace.CorrelationManager.ActivityId;
        var oldCorrelation = Trace.CorrelationManager.ActivityId = Guid.NewGuid();
        try
        {
            using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(fileObserver: boundary));
            Assert.True(app.OpenWorkspace(folder), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport("""
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000071",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """).Id), app.Status);
            boundary.Armed = true;
            var note = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "avery@example.com"));
            app.UpdateNoteDraft(scope, note.Id, "PRIVATE-TITLE-SENTINEL", "PRIVATE-BODY-SENTINEL");
            Assert.True(boundary.Entered.Wait(TimeSpan.FromSeconds(5)));
            app.UpdateNoteDraft(scope, note.Id, "PRIVATE-TITLE-SENTINEL", "PRIVATE-BODY-SENTINEL newer");
            boundary.Release.Set();
            Assert.True(await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(oldCorrelation, Trace.CorrelationManager.ActivityId);
            app.UpdateNoteDraft(scope, note.Id, "PRIVATE-TITLE-SENTINEL", "PRIVATE-BODY-SENTINEL next incarnation");
            Assert.True(await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Equal("PRIVATE-BODY-SENTINEL next incarnation", reopened.BeginNoteDraft(scope, "avery@example.com", note.Id)!.Markdown);
            capture.Dispose();
            Assert.True(capture.WaitForDrain(TimeSpan.FromSeconds(5)));
            var records = Facts(output);
            var notes = records.Where(record => record.Fact.Note == note.Id).Select(record => record.Fact).ToArray();
            Assert.Equal(2, notes.Where(fact => fact.Stage == PassiveSaveStage.DraftUpdated)
                .Select(fact => fact.Incarnation).Distinct().Count());
            Assert.Contains(notes, fact => fact.Stage == PassiveSaveStage.SaveAccepted && fact.Accepted == true &&
                fact.Retained == true && fact.Removed == false && fact.SuccessorBasis == true &&
                fact.LatestGeneration > fact.SubmittedGeneration);
            foreach (var submitted in notes.Where(fact => fact.Stage == PassiveSaveStage.SaveSubmitted))
            {
                var phases = records.Where(record => record.Fact.Stage == PassiveSaveStage.ActualCommit &&
                    record.Fact.SaveCall == submitted.SaveCall).Select(record => record.Fact).ToArray();
                Assert.Equal(2, phases.Length);
                Assert.NotEqual(Guid.Empty, phases[0].SaveCall);
                Assert.Equal(phases[0].Phase, phases[1].Phase);
                Assert.Equal(WorkspaceStoragePhaseKind.Begin, phases[0].PhaseKind);
                Assert.Equal(WorkspaceStoragePhaseKind.End, phases[1].PhaseKind);
            }

            var text = Encoding.UTF8.GetString(output.ToArray());
            Assert.DoesNotContain("PRIVATE-TITLE-SENTINEL", text);
            Assert.DoesNotContain("PRIVATE-BODY-SENTINEL", text);
            Assert.DoesNotContain("avery@example.com", text);
            Assert.DoesNotContain(folder, text);
            Assert.Contains(Read(output), value => value.TryGetProperty("stage", out var stage) &&
                stage.GetString() is "diagnostic-loaded-sqlite" or "diagnostic-native-incomplete");
        }
        finally
        {
            boundary.Release.Set();
            Trace.CorrelationManager.ActivityId = beforeCorrelation;
            capture.Dispose();
            Assert.True(capture.WaitForDrain(TimeSpan.FromSeconds(5)));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_footer_before_output_close_failure_is_not_complete_capture_evidence()
    {
        var output = new RefusedCloseStream();
        using var capture = PassiveSaveCapture.Install("support-test", output: () => output);
        PassiveSaveCapture.Emit(new(PassiveSaveStage.RequestBegin));
        capture.Dispose();
        Assert.True(capture.WaitForDrain(TimeSpan.FromSeconds(5)));
        Assert.Contains(Read(output), value => value.TryGetProperty("stage", out var stage) &&
            stage.GetString() == "diagnostic-stream-end" && value.GetProperty("diagnosticsComplete").GetBoolean());
        Assert.Equal(1, capture.OutputFailures);
    }

    [Fact]
    public void A_prefix_commit_can_cover_newer_retained_generations_but_ambiguous_or_incomplete_links_stay_unknown()
    {
        var note = Guid.NewGuid();
        var incarnation = Guid.NewGuid();
        var call = Guid.NewGuid();
        var phase = Guid.NewGuid();
        var digest = PassiveSaveCapture.Digest("prefix");
        var owner = PassiveSaveCapture.OwnerDigest("owner@example.com");
        PassiveSaveFact Note(PassiveSaveStage stage) => new(stage, NoteScope.Team, note, owner, incarnation);
        PassiveSaveRecord Record(int sequence, PassiveSaveFact fact, int pid = 1, string role = "rich-normal-entry-exe",
            string clock = "one-owned-worker") => new(1, sequence, pid, 1, 1, Stopwatch.Frequency,
                sequence * 10, role, clock, true, fact);
        var records = new[]
        {
            Record(1, Note(PassiveSaveStage.RequestBegin) with { RequestDigest = digest, Sample = 0 }, 2, "rich-harness"),
            Record(1, Note(PassiveSaveStage.DraftUpdated) with { Generation = 2, RequestDigest = digest }),
            Record(2, Note(PassiveSaveStage.SaveSubmitted) with { Generation = 2, SubmittedGeneration = 2, SaveCall = call, RequestDigest = digest }),
            Record(3, new(PassiveSaveStage.ActualCommit, SaveCall: call, Phase: phase, PhaseKind: WorkspaceStoragePhaseKind.Begin)),
            Record(4, new(PassiveSaveStage.ActualCommit, SaveCall: call, Phase: phase, PhaseKind: WorkspaceStoragePhaseKind.End)),
            Record(5, Note(PassiveSaveStage.SaveAccepted) with { SaveCall = call, SubmittedGeneration = 2, LatestGeneration = 3,
                Accepted = true, Retained = true, Removed = false, SuccessorBasis = true })
        };
        var linked = PassiveSaveCapture.LinkCommittedPrefix(records, "rich-normal-entry-exe", note, digest, true);
        Assert.NotNull(linked);
        Assert.Equal(phase, linked.Phase);
        Assert.True(linked.RetainedLatest);
        Assert.False(linked.Removed);
        Assert.Equal(3, linked.LatestGeneration);
        Assert.Null(PassiveSaveCapture.LinkCommittedPrefix(records, "rich-normal-entry-exe", note, digest, false));
        Assert.Null(PassiveSaveCapture.LinkCommittedPrefix(records.Select(record => record.Pid == 2
            ? record with { ClockDomain = "another-runner" } : record).ToArray(), "rich-normal-entry-exe", note, digest, true));
        Assert.Null(PassiveSaveCapture.LinkCommittedPrefix(records.Append(Record(6,
            Note(PassiveSaveStage.DraftUpdated) with { Generation = 2, RequestDigest = digest, Incarnation = Guid.NewGuid() })).ToArray(),
            "rich-normal-entry-exe", note, digest, true));
        Assert.Null(PassiveSaveCapture.LinkCommittedPrefix(records.Select(record => record.Fact.Stage == PassiveSaveStage.ActualCommit &&
            record.Fact.PhaseKind == WorkspaceStoragePhaseKind.End ? record with
            { Fact = record.Fact with { PhaseKind = WorkspaceStoragePhaseKind.Failed } } : record).ToArray(),
            "rich-normal-entry-exe", note, digest, true));
        Assert.Null(PassiveSaveCapture.LinkCommittedPrefix(records.Select(record => record with
            { QpcFrequency = 0 }).ToArray(), "rich-normal-entry-exe", note, digest, true));
        Assert.Null(PassiveSaveCapture.LinkCommittedPrefix(records.Where(record => record.Sequence != 3).ToArray(),
            "rich-normal-entry-exe", note, digest, true));
    }

    private sealed class RefusedCloseStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw new IOException("Controlled close refusal.");
        }
    }

    private sealed class HeldCommit : IWorkspaceFileObserver, IDisposable
    {
        internal bool Armed;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (!Armed || phase.Name != "local-sqlite-commit" || phase.Kind != WorkspaceStoragePhaseKind.Begin) return;
            Armed = false;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Support barrier expired.");
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
}
