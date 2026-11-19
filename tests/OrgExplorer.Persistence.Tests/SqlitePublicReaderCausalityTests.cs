using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OrgExplorer.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Persistence.Tests;

public sealed class SqlitePublicReaderCausalityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Owner = "manager@example.com";
    private const string Initial = """
        {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000091",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"manager@example.com","displayName":"Manager","managerEmail":null},
         {"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com"}]}
        """;

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Public_full_reader_does_not_fail_a_later_same_owner_note_generation(NoteScope scope)
    {
        using var gate = new ReaderGate();
        using var model = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(model.OpenWorkspace(folder, WorkspaceOpenMode.OpenOrCreate), model.Status);
        Assert.True(model.ApplyImport(model.PreviewImport(Initial).Id), model.Status);
        Assert.True(model.NavigatePerson(Owner), model.Status);
        var created = Assert.IsType<NoteDraft>(model.CreateOwnedNoteDraft(scope, Owner));
        model.UpdateNoteDraft(scope, created.Id, "One exact note", "Committed prefix");
        Assert.True(await model.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10)), model.Status);
        using (var prefix = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(prefix.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), prefix.Status);
            Assert.Equal("Committed prefix", prefix.BeginNoteDraft(scope, Owner, created.Id)!.Markdown);
        }
        using var reader = ApplicationWorkflow.CreateFileBacked(fileObserver: gate);
        var read = Task.Run(() => reader.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gate.Release.IsSet);
            Assert.DoesNotContain(gate.ReaderPhases, phase => phase.Kind != WorkspaceStoragePhaseKind.Begin);
            model.UpdateNoteDraft(scope, created.Id, "One exact note", "Later retained generation");
            Assert.True(await model.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10)), model.Status);
            Assert.Null(model.GetRetainedNoteDraft(scope, created.Id));
            Assert.True(model.CanEditLocalWorkspace);
            Assert.True(model.CanSafelyClose);
            Assert.True(model.GetOwnedNotesView(scope).IsEditing);
            Assert.Equal(created.Id, model.GetOwnedNotesView(scope).SelectedNote!.Id);
            var successor = model.BeginNoteDraft(scope, Owner, created.Id)!;
            Assert.Equal("Later retained generation", successor.Markdown);
            using var fresh = ApplicationWorkflow.CreateFileBacked();
            Assert.True(fresh.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), fresh.Status);
            var saved = fresh.BeginNoteDraft(scope, Owner, created.Id)!;
            Assert.Equal(created.Id, saved.Id);
            Assert.Equal(Owner, saved.OwnerEmail);
            Assert.Equal(successor.Title, saved.Title);
            Assert.Equal(successor.Markdown, saved.Markdown);
            Assert.Null(fresh.BeginNoteDraft(scope, "report@example.com", created.Id));
            Assert.Single(scope == NoteScope.Person
                ? fresh.GetPersonNotes(Owner).Select(note => note.Id)
                : fresh.GetTeamNotes(Owner).Select(note => note.Id));
            Assert.False(gate.Release.IsSet);
            Assert.False(gate.CallbackExited);
            Assert.DoesNotContain(gate.ReaderPhases, phase => phase.Kind != WorkspaceStoragePhaseKind.Begin);
        }
        finally
        {
            gate.ReleaseRequestedAt = Stopwatch.GetTimestamp();
            gate.Release.Set();
            Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(10)), reader.Status);
        }
        var phases = gate.ReaderPhases.ToArray();
        Assert.Equal(2, phases.Length);
        Assert.Equal(phases[0].Id, phases[1].Id);
        Assert.Equal(WorkspaceStoragePhaseKind.End, phases[1].Kind);
        Assert.True(gate.CallbackExited);
        Assert.False(gate.TimedOut);
        Assert.Equal("Committed prefix", reader.BeginNoteDraft(scope, Owner, created.Id)!.Markdown);
        output.WriteLine("WAL-PUBLIC-READER " + JsonSerializer.Serialize(new
        {
            scope = scope.ToString(), owner = Owner, noteId = created.Id,
            readerOperation = phases[0].Id, readerPhases = phases,
            gate.KnownHeldAt, gate.ReleaseRequestedAt, gate.DisposedObservedAt,
            laterCompleteWritingIndependentlyVisibleBeforeRelease = true,
            oldReaderRetainsCoherentPrefix = true, noRetryOrReconciliation = true,
            timestampFrequency = Stopwatch.Frequency, historicalHostedActorAttributed = false,
            boundary = "Known-held is not acquisition time. Actual public reader held throughout Commit and fresh independent reopen; no native/performance acceptance claim."
        }));
    }

    private sealed class ReaderGate : IWorkspaceFileObserver, IDisposable
    {
        internal ConcurrentQueue<WorkspaceStoragePhase> ReaderPhases { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal long KnownHeldAt, ReleaseRequestedAt, DisposedObservedAt;
        internal bool TimedOut, CallbackExited;
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name != "local-sqlite-read-snapshot") return;
            ReaderPhases.Enqueue(phase);
            if (phase.Kind == WorkspaceStoragePhaseKind.Begin)
            {
                KnownHeldAt = Stopwatch.GetTimestamp();
                Entered.TrySetResult();
                TimedOut = !Release.Wait(TimeSpan.FromSeconds(15));
                CallbackExited = true;
            }
            else DisposedObservedAt = Stopwatch.GetTimestamp();
        }
        public void Dispose() => Release.Dispose();
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
