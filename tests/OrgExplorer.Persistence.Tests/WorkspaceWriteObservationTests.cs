using System.Collections.Concurrent;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class WorkspaceWriteObservationTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Public_reopen_pairs_a_known_held_snapshot_with_completed_physical_disposal()
    {
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            Assert.True(seed.OpenWorkspace(folder), seed.Status);
        var observer = new RecordingObserver();
        using var reader = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.True(reader.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), reader.Status);
        var phases = observer.Phases.Where(phase => phase.Name == "local-sqlite-read-snapshot").ToArray();
        Assert.Equal(2, phases.Length);
        Assert.NotEqual(Guid.Empty, phases[0].Id);
        Assert.Equal(phases[0].Id, phases[1].Id);
        Assert.Equal(WorkspaceStoragePhaseKind.Begin, phases[0].Kind);
        Assert.Equal(WorkspaceStoragePhaseKind.End, phases[1].Kind);
    }

    [Theory]
    [InlineData(WorkspaceStoragePhaseKind.Begin)]
    [InlineData(WorkspaceStoragePhaseKind.End)]
    public void Read_observer_faults_do_not_change_real_open_or_committed_data(WorkspaceStoragePhaseKind fault)
    {
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            Assert.True(seed.OpenWorkspace(folder), seed.Status);
        var observed = new List<WorkspaceStoragePhase>();
        var observer = new CallbackObserver(phase =>
        {
            if (phase.Name != "local-sqlite-read-snapshot") return;
            observed.Add(phase);
            if (phase.Kind == fault) throw new IOException("Controlled read observation failure.");
        });
        using var reader = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.True(reader.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), reader.Status);
        Assert.True(reader.CanSafelyClose);
        Assert.Null(reader.LocalSaveError);
        Assert.Equal(2, observed.Count);
        Assert.Equal(observed[0].Id, observed[1].Id);
        Assert.Equal(WorkspaceStoragePhaseKind.End, observed[1].Kind);
    }

    [Fact]
    public async Task Public_read_End_callback_can_be_held_without_blocking_a_real_independent_commit()
    {
        using var writer = ApplicationWorkflow.CreateFileBacked();
        Assert.True(writer.OpenWorkspace(folder), writer.Status);
        Assert.True(writer.ApplyImport(writer.PreviewImport("""
            {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000092",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
            """).Id), writer.Status);
        var note = Assert.IsType<PersonNote>(writer.CreatePersonNote("avery@example.com", "One exact note", "Before disposal"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = false;
        var observer = new CallbackObserver(phase =>
        {
            if (phase.Name != "local-sqlite-read-snapshot" || phase.Kind != WorkspaceStoragePhaseKind.End) return;
            entered.TrySetResult();
            timedOut = !release.Wait(TimeSpan.FromSeconds(10));
        });
        using var reader = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        var reading = Task.Run(() => reader.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(writer.EditPersonNote(note.Id, "One exact note", "After physical read disposal"), writer.Status);
            Assert.False(release.IsSet);
        }
        finally { release.Set(); Assert.True(await reading.WaitAsync(TimeSpan.FromSeconds(5)), reader.Status); }
        Assert.False(timedOut);
        using var independent = ApplicationWorkflow.CreateFileBacked();
        Assert.True(independent.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), independent.Status);
        Assert.Equal("After physical read disposal", independent.GetPersonNotes("avery@example.com").Single().Markdown);
    }

    [Fact]
    public void Storage_phases_bracket_the_real_local_commit_and_reopen_its_saved_data()
    {
        var observer = new RecordingObserver();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000004",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
            """).Id), app.Status);
        observer.Phases.Clear();
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        Assert.Contains(observer.Phases, phase => phase.Name == "local-sqlite-commit" && phase.Kind == WorkspaceStoragePhaseKind.Begin);
        Assert.All(observer.Phases.GroupBy(phase => phase.Id), phases =>
        {
            Assert.Equal(WorkspaceStoragePhaseKind.Begin, phases.First().Kind);
            Assert.Equal(WorkspaceStoragePhaseKind.End, phases.Last().Kind);
            Assert.True(phases.Last().Milliseconds >= 0);
            Assert.True(phases.Last().CpuMilliseconds >= 0);
        });
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Ave", Assert.Single(reopened.People).Heading);
        Assert.Equal("Ave", Assert.Single(reopened.GetProfileTimeline("avery@example.com")!.Changes).NewValue.GetString());
    }

    [Fact]
    public void Timing_marks_the_active_phase_before_failure_and_preserves_the_original_error()
    {
        var lines = new List<string>();
        var active = false;
        var expected = new IOException("Controlled measured failure.");
        var actual = Assert.Throws<IOException>(() => ScaleFixture.Measure("controlled-operation", () =>
        {
            active = lines.Any(line => line.StartsWith("SCALE BEGIN controlled-operation", StringComparison.Ordinal));
            throw expected;
        }, lines.Add));
        Assert.Same(expected, actual);
        Assert.True(active);
        Assert.Contains(lines, line => line.StartsWith("SCALE END controlled-operation", StringComparison.Ordinal) &&
            line.Contains("Failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BEGIN")]
    [InlineData("END")]
    public void Scale_phase_logging_failures_do_not_change_real_save_outcomes(string eventKind)
    {
        var workspace = Path.Combine(folder, "workspace");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(seed.OpenWorkspace(workspace), seed.Status);
            Assert.True(seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000005",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """).Id), seed.Status);
        }
        var loggingError = new IOException("Controlled diagnostic write failure.");
        var fixture = new ScaleFixture(folder)
        {
            Log = message =>
            {
                if (message.StartsWith($"SCALE {eventKind} local-sqlite-commit;", StringComparison.Ordinal))
                    throw loggingError;
            }
        };
        Exception? completionError = null;
        try
        {
            var app = fixture.Open();
            Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
            Assert.Null(app.LocalSaveError);
            Assert.True(app.CanSafelyClose);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(workspace), reopened.Status);
            Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        }
        finally { completionError = Record.Exception(fixture.Dispose); }
        var diagnostics = Assert.IsType<AggregateException>(completionError);
        Assert.Same(loggingError, Assert.Single(diagnostics.InnerExceptions));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(99)]
    public void Unsupported_readable_Workspace_rejection_never_writes_copies_or_publishes_any_payload(int version)
    {
        WorkspaceFixtures.CopyVersion(version, folder);
        var originals = WorkspaceFixtures.Bytes(folder);
        var observer = new RecordingObserver();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.False(app.OpenWorkspace(folder));
        Assert.False(app.RefreshWorkspace());
        Assert.Contains("does not migrate", app.Status);
        Assert.Empty(observer.Effects);
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(originals.Keys.Order(), after.Keys.Order());
        foreach (var (relative, bytes) in originals) Assert.Equal(bytes, after[relative]);
    }

    private sealed class RecordingObserver : IWorkspaceFileObserver
    {
        public ConcurrentQueue<WorkspaceFileEffect> Effects { get; } = new();
        public ConcurrentQueue<WorkspaceStoragePhase> Phases { get; } = new();
        public void Observe(WorkspaceFileEffect effect) => Effects.Enqueue(effect);
        public void ObservePhase(WorkspaceStoragePhase phase) => Phases.Enqueue(phase);
    }

    private sealed class CallbackObserver(Action<WorkspaceStoragePhase> callback) : IWorkspaceFileObserver
    {
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase) => callback(phase);
    }
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
