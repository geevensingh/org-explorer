using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class NoteDraftPersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Owner = "avery@example.com";
    private Task? deferredCleanup;
    private const string EntryDiagnosticMarker = "NOTE-SAVE-ENTRY-REFUSAL ";
    private const string Population = """
        {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
        """;

    private WorkspaceViewModel Open(IWorkspaceFileObserver? observer = null)
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(fileObserver: observer));
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Population).Id), app.Status);
        return app;
    }

    private static Guid Create(IApplicationWorkflow app, NoteScope scope, string title, string markdown) =>
        scope == NoteScope.Person ? app.CreatePersonNote(Owner, title, markdown)!.Id :
            app.CreateTeamNote(Owner, title, markdown)!.Id;

    private void Deliver(NoteScope scope, Guid id, string markdown)
    {
        using var provider = ApplicationWorkflow.CreateFileBacked();
        Assert.True(provider.OpenWorkspace(folder), provider.Status);
        var draft = provider.BeginNoteDraft(scope, Owner, id)!;
        Assert.NotNull(provider.SaveNoteDraft(draft, draft.Title, markdown));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Failed_secondary_draft_retains_exact_owner_and_writing_while_ongoing_browsing_remains_available(NoteScope scope)
    {
        using var app = Open();
        var ongoing = Create(app, scope, "Ongoing", "Continuing writing");
        var secondary = Create(app, scope, "Secondary", "Original secondary writing");
        Assert.True(app.OpenOwnedNotes(scope, Owner));
        Assert.True(app.DesignateOngoingNote(scope, ongoing));
        Assert.True(app.OpenOwnedNote(scope, secondary, editing: true));
        var draft = app.BeginNoteDraft(scope, Owner, secondary)!;
        app.RetainNoteDraft(draft);
        using (WorkspaceFixtures.LockLocalStore(folder))
            Assert.Null(app.SaveNoteDraft(draft, "Retained secondary draft", "Unsaved writing"));
        Assert.True(app.OpenOngoingNote(scope), app.Status);
        Assert.Equal(ongoing, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.Equal(Owner, app.GetOwnedNotesView(scope).OwnerEmail);
        Assert.True(app.HasUnsavedNoteDraft(scope, secondary));
        Assert.Equal("Original secondary writing", draft.Markdown);
        Deliver(scope, secondary, "Delivered secondary writing");
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Retained secondary draft", "Unsaved writing"));
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(secondary);
        Assert.True(app.OpenOwnedNote(scope, secondary));
        Assert.Equal("Delivered secondary writing", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void First_autosave_and_retry_reject_new_committed_revision_without_losing_either_version(NoteScope scope)
    {
        using var app = Open();
        var id = Create(app, scope, "Context", "Original writing");
        var draft = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner, id));
        app.RetainNoteDraft(draft);
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => Assert.Fail("Pending writing cannot authorize restart.")));
        Deliver(scope, id, "Delivered writing");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Local title", "Still typing my private draft"));
        Assert.Contains("changed externally", app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Local title", "Still typing my private draft"));
        Assert.Equal("Original writing", draft.Markdown);
        Assert.Equal("Delivered writing", app.BeginNoteDraft(scope, Owner, id)!.Markdown);
        app.DiscardUnsavedChanges(id);
        Assert.True(app.CanSafelyClose);
        var accepted = app.BeginNoteDraft(scope, Owner, id)!;
        Assert.NotNull(app.SaveNoteDraft(accepted, "Deliberate edit", "Intentional delivered-version edit"));
        using var reopened = Open();
        Assert.Equal("Intentional delivered-version edit", reopened.BeginNoteDraft(scope, Owner, id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Plain_IO_failure_requires_local_recovery_then_retries_the_same_base_and_returns_a_base_for_newer_typing(NoteScope scope)
    {
        using var app = Open();
        var original = Assert.IsType<NoteDraft>(app.SaveNoteDraft(app.BeginNoteDraft(scope, Owner)!, "Context", "Original"));
        using (WorkspaceFixtures.LockLocalStore(folder))
            Assert.Null(app.SaveNoteDraft(original, "First edit", "First writing"));
        Assert.False(app.CanSafelyClose);
        using (var prior = Open())
            Assert.Equal("Original", prior.BeginNoteDraft(scope, Owner, original.Id)!.Markdown);
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        var accepted = Assert.IsType<NoteDraft>(app.SaveNoteDraft(original, "First edit", "First writing"));
        Assert.True(app.CanSafelyClose);
        Assert.Equal("First writing", accepted.Markdown);
        var newer = Assert.IsType<NoteDraft>(app.SaveNoteDraft(accepted, "Newer edit", "Typed while saving"));
        Assert.Equal("Typed while saving", newer.Markdown);
        using var reopened = Open();
        Assert.Equal("Typed while saving", reopened.BeginNoteDraft(scope, Owner, original.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Retaining_newer_typing_does_not_wait_behind_in_flight_IO_and_uses_the_successful_base(NoteScope scope)
    {
        var diagnostics = new SaveEntryDiagnostics();
        var boundary = new HeldCommit(diagnostics);
        WorkspaceViewModel? app = null;
        Task<NoteDraft?>? saving = null;
        Guid? noteId = null;
        try
        {
            app = Open(boundary);
            var original = app.SaveNoteDraft(app.BeginNoteDraft(scope, Owner)!, "Context", "Original")!;
            noteId = original.Id;
            app.RetainNoteDraft(original);
            boundary.Hold = true;
            diagnostics.Arm();
            saving = Task.Run(() =>
            {
                diagnostics.Admitted();
                try { return app.SaveNoteDraft(original, "First edit", "First writing"); }
                finally { diagnostics.Exited(); }
            });
            Assert.True(boundary.Entered.Wait(TimeSpan.FromSeconds(5)),
                "The real SQLite note commit did not start within five seconds.\n" +
                EntryDiagnosticMarker + diagnostics.Snapshot(scope, noteId, saving, app, boundary));
            await Task.Run(() => app.RetainNoteDraft(original)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(app.CanSafelyClose);
            boundary.Release.Set();
            var accepted = Assert.IsType<NoteDraft>(await saving);
            app.RetainNoteDraft(accepted);
            Assert.False(app.CanSafelyClose);
            Assert.NotNull(app.SaveNoteDraft(accepted, "Newer edit", "Typed while the first save was held"));
            Assert.True(app.CanSafelyClose);
            using var reopened = Open();
            Assert.Equal("Typed while the first save was held", reopened.BeginNoteDraft(scope, Owner, original.Id)!.Markdown);
        }
        finally
        {
            boundary.Release.Set();
            void Cleanup(Task<NoteDraft?>? completed)
            {
                if (completed is { IsFaulted: true } or { IsCanceled: true })
                    Console.Error.WriteLine("NOTE-SAVE-TASK-OUTCOME " +
                        diagnostics.Snapshot(scope, noteId, completed, app, boundary));
                app?.Dispose();
                boundary.Dispose();
            }
            if (saving is { IsCompleted: false })
            {
                deferredCleanup = saving.ContinueWith(completed =>
                {
                    Cleanup(completed);
                    DeleteFolder();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _ = deferredCleanup.ContinueWith(completed => Console.Error.WriteLine(
                    "NOTE-SAVE-CLEANUP-FAULT " + JsonSerializer.Serialize(new
                    {
                        scope = scope.ToString(),
                        faults = SaveEntryDiagnostics.Faults(completed)
                    })), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else Cleanup(saving);
        }
    }

    [Fact]
    public void Team_confirmation_change_without_writing_changes_invalidates_the_first_dirty_base()
    {
        using var app = Open();
        var original = app.SaveNoteDraft(app.BeginNoteDraft(NoteScope.Team, Owner)!, "Context", "Original")!;
        app.RetainNoteDraft(original);
        using (var provider = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(provider.OpenWorkspace(folder), provider.Status);
            Assert.True(provider.EditPersonText(Owner, PersonTextField.JobTitle, "Director"), provider.Status);
            Assert.True(provider.ConfirmTeamNote(original.Id), provider.Status);
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.False(Assert.Single(app.GetTeamNotes(Owner)).NeedsReview);
        Assert.Null(app.SaveNoteDraft(original, original.Title, "Retained local writing"));
        Assert.Contains("changed externally", app.Status);
        app.DiscardUnsavedChanges(original.Id);
        var accepted = app.BeginNoteDraft(NoteScope.Team, Owner, original.Id)!;
        Assert.NotNull(app.SaveNoteDraft(accepted, accepted.Title, "Explicitly confirmed writing"));
        Assert.False(Assert.Single(app.GetTeamNotes(Owner)).NeedsReview);
    }

    [Fact]
    public void Discarding_one_pending_scope_does_not_discard_the_other_and_global_discard_writes_neither()
    {
        using var app = Open();
        var person = app.SaveNoteDraft(app.BeginNoteDraft(NoteScope.Person, Owner)!, "Person", "Person writing")!;
        var team = app.SaveNoteDraft(app.BeginNoteDraft(NoteScope.Team, Owner)!, "Team", "Team writing")!;
        app.RetainNoteDraft(person);
        app.RetainNoteDraft(team);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(person.Id);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose);
        using var reopened = Open();
        Assert.Equal("Person writing", reopened.BeginNoteDraft(NoteScope.Person, Owner, person.Id)!.Markdown);
        Assert.Equal("Team writing", reopened.BeginNoteDraft(NoteScope.Team, Owner, team.Id)!.Markdown);
    }

    private sealed class HeldCommit(IWorkspaceFileObserver? diagnostics = null) : IWorkspaceFileObserver, IDisposable
    {
        internal bool Hold;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        public void Observe(WorkspaceFileEffect effect) => diagnostics?.Observe(effect);
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            diagnostics?.ObservePhase(phase);
            if (!Hold || phase.Name != "local-sqlite-commit" || phase.Kind != WorkspaceStoragePhaseKind.Begin) return;
            Hold = false;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled local commit was not released.");
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private void DeleteFolder() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    public void Dispose()
    {
        if (deferredCleanup is not { IsCompleted: false }) DeleteFolder();
    }

    private sealed class SaveEntryDiagnostics : IWorkspaceFileObserver
    {
        private const int PhaseLimit = 24;
        private readonly object gate = new();
        private readonly Queue<object> phases = new();
        private long requestedAt;
        private long admittedAt;
        private long exitedAt;
        private int saveThread;
        private int droppedPhases;
        private readonly long[] effects = new long[4];

        public void Arm() => Volatile.Write(ref requestedAt, Stopwatch.GetTimestamp());
        public void Admitted()
        {
            Volatile.Write(ref saveThread, Environment.CurrentManagedThreadId);
            Volatile.Write(ref admittedAt, Stopwatch.GetTimestamp());
        }
        public void Exited() => Volatile.Write(ref exitedAt, Stopwatch.GetTimestamp());
        public void Observe(WorkspaceFileEffect effect)
        {
            if (Volatile.Read(ref requestedAt) != 0) Interlocked.Increment(ref effects[(int)effect.Kind]);
        }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            var requested = Volatile.Read(ref requestedAt);
            if (requested == 0) return;
            var elapsed = Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
            lock (gate)
            {
                if (phases.Count == PhaseLimit) { phases.Dequeue(); droppedPhases++; }
                phases.Enqueue(new
                {
                    id = phase.Id, name = phase.Name, kind = phase.Kind.ToString(),
                    elapsedMs = elapsed, phaseMs = phase.Milliseconds, cpuMs = phase.CpuMilliseconds,
                    threadId = Environment.CurrentManagedThreadId
                });
            }
        }
        public static object[]? Faults(Task task) => task.Exception?.Flatten().InnerExceptions
            .Select(error => (object)new { type = error.GetType().FullName, hResult = error.HResult }).ToArray();

        public string Snapshot(NoteScope scope, Guid? noteId, Task<NoteDraft?> task,
            WorkspaceViewModel? app, HeldCommit boundary)
        {
            ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
            ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
            ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumIo);
            var requested = Volatile.Read(ref requestedAt);
            var admitted = Volatile.Read(ref admittedAt);
            var exited = Volatile.Read(ref exitedAt);
            var taskStatus = task.Status;
            object[] observed;
            int dropped;
            lock (gate) { observed = phases.ToArray(); dropped = droppedPhases; }
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1, scope = scope.ToString(), noteId, entryDeadlineMs = 5000,
                elapsedMs = Stopwatch.GetElapsedTime(requested).TotalMilliseconds,
                delegateAdmitted = admitted != 0,
                admittedMs = admitted == 0 ? (double?)null : Stopwatch.GetElapsedTime(requested, admitted).TotalMilliseconds,
                exitedMs = exited == 0 ? (double?)null : Stopwatch.GetElapsedTime(requested, exited).TotalMilliseconds,
                saveThreadId = admitted == 0 ? (int?)null : Volatile.Read(ref saveThread),
                commitHookSetAtSnapshot = boundary.Entered.IsSet,
                task = new
                {
                    status = taskStatus.ToString(),
                    queued = taskStatus is TaskStatus.WaitingToRun or TaskStatus.WaitingForActivation,
                    running = taskStatus == TaskStatus.Running, completed = task.IsCompleted,
                    completedSuccessfully = task.IsCompletedSuccessfully, faulted = task.IsFaulted, cancelled = task.IsCanceled,
                    returnedDraft = task.IsCompletedSuccessfully ? (bool?)(task.Result is not null) : null,
                    faults = Faults(task)
                },
                workflow = new { status = app?.Status, operationError = app?.OperationError, localSaveError = app?.LocalSaveError },
                workers = new
                {
                    availableWorkers, minimumWorkers, maximumWorkers, availableIo, minimumIo, maximumIo,
                    threadCount = ThreadPool.ThreadCount, pendingWorkItems = ThreadPool.PendingWorkItemCount
                },
                progress = new
                {
                    phaseLimit = PhaseLimit, droppedPhases = dropped, phases = observed,
                    completedEffects = new
                    {
                        writes = Volatile.Read(ref effects[0]), flushes = Volatile.Read(ref effects[1]),
                        publications = Volatile.Read(ref effects[2]), deletes = Volatile.Read(ref effects[3])
                    }
                },
                snapshotLimit = "Non-atomic observations after the request, including concurrent workflow callbacks. " +
                    "Absent phase/admission timestamps are not proof that a stage never ran. Progress contains no note body or file path; " +
                    "workflow strings are unmodified operation snapshots."
            });
        }
    }
}
