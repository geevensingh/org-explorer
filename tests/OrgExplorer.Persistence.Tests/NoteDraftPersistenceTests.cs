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

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Failed_secondary_draft_retains_exact_owner_and_editor_when_ongoing_navigation_is_rejected(NoteScope scope)
    {
        using var app = Open();
        var ongoing = scope == NoteScope.Person
            ? app.CreatePersonNote(Owner, "Ongoing", "Continuing writing")!.Id
            : app.CreateTeamNote(Owner, "Ongoing", "Continuing writing")!.Id;
        var secondary = scope == NoteScope.Person
            ? app.CreatePersonNote(Owner, "Secondary", "Original secondary writing")!.Id
            : app.CreateTeamNote(Owner, "Secondary", "Original secondary writing")!.Id;
        Assert.True(app.OpenOwnedNotes(scope, Owner));
        Assert.True(app.DesignateOngoingNote(scope, ongoing));
        Assert.True(app.OpenOwnedNote(scope, secondary, editing: true));
        var draft = app.BeginNoteDraft(scope, Owner, secondary)!;
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", secondary + ".md");
        app.RetainNoteDraft(draft);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Null(app.SaveNoteDraft(draft, "Retained secondary draft", "Unsaved writing"));
        Assert.False(app.OpenOngoingNote(scope));
        Assert.Equal(secondary, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.Equal(Owner, app.GetOwnedNotesView(scope).OwnerEmail);
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.Contains("Retry or discard", app.GetOwnedNotesView(scope).Feedback);
        File.WriteAllText(file, File.ReadAllText(file).Replace("Original secondary writing", "Delivered secondary writing"));
        var delivered = File.ReadAllBytes(file);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Retained secondary draft", "Unsaved writing"));
        Assert.Equal(delivered, File.ReadAllBytes(file));
        Assert.Equal("Original secondary writing", draft.Markdown);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(secondary);
        Assert.True(app.OpenOwnedNote(scope, secondary));
        Assert.Equal("Delivered secondary writing", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.True(app.OpenOngoingNote(scope));
        Assert.Equal(ongoing, app.GetOwnedNotesView(scope).SelectedNote!.Id);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void First_autosave_and_retry_reject_delivered_revision_without_losing_either_version(NoteScope scope)
    {
        using var app = Open();
        var id = scope == NoteScope.Person
            ? app.CreatePersonNote(Owner, "Context", "Original writing")!.Id
            : app.CreateTeamNote(Owner, "Context", "Original writing")!.Id;
        var draft = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner, id));
        const string localWriting = "Still typing my private draft";
        app.RetainNoteDraft(draft);
        Assert.False(app.CanSafelyClose);
        var restarted = false;
        Assert.False(app.RestartWhenSafe(_ => restarted = true));
        Assert.False(restarted);
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", id + ".md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Original writing", "Delivered writing"));
        var delivered = File.ReadAllBytes(file);
        Assert.True(app.RefreshWorkspace(), app.Status);

        Assert.Null(app.SaveNoteDraft(draft, "Local title", localWriting));
        Assert.Contains("changed externally", app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Original writing", draft.Markdown);
        Assert.Equal(delivered, File.ReadAllBytes(file));
        Assert.Null(app.SaveNoteDraft(draft, "Local title", localWriting));
        Assert.Equal(delivered, File.ReadAllBytes(file));

        app.DiscardUnsavedChanges(id);
        Assert.True(app.CanSafelyClose);
        var accepted = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner, id));
        Assert.Equal("Delivered writing", accepted.Markdown);
        Assert.NotNull(app.SaveNoteDraft(accepted, "Deliberate edit", "Intentional delivered-version edit"));
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Intentional delivered-version edit",
            reopened.BeginNoteDraft(scope, Owner, id)!.Markdown);
    }

    private WorkspaceViewModel Open(IWorkspaceFilePublisher? publisher = null, IWorkspaceFileObserver? observer = null)
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer));
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Population).Id), app.Status);
        return app;
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void New_note_draft_requires_absence_until_publication_and_discard_accepts_external_creation(NoteScope scope)
    {
        using var app = Open();
        var draft = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner));
        var directory = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, draft.Id + ".md");
        var confirmation = scope == NoteScope.Team
            ? ",\"confirmedContextRevision\":\"00000000-0000-0000-0000-000000000000\"" : "";
        var delivered = "---\r\n" + $$"""
            {"schemaVersion":1,"kind":"{{scope.ToString().ToLowerInvariant()}}","id":"{{draft.Id}}",
            "ownerEmail":"avery@example.com","title":"External creation","archived":false,
            "createdAt":"2026-11-01T17:00:00Z","updatedAt":"2026-11-01T17:00:00Z"{{confirmation}}}
            """ + "\r\n---\r\n# Delivered new document\r\n";
        File.WriteAllText(file, delivered);
        var bytes = File.ReadAllBytes(file);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Local creation", "Unsaved new writing"));
        Assert.Contains("changed externally", app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Local creation", "Unsaved new writing"));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(draft.Id);
        var accepted = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner, draft.Id));
        Assert.Equal("# Delivered new document\r\n", accepted.Markdown);
        Assert.NotNull(app.SaveNoteDraft(accepted, accepted.Title, "Deliberate edit of delivered creation"));
        var separate = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner));
        Assert.NotNull(app.SaveNoteDraft(separate, "Intentional new note", "Independent writing"));
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Independent writing", reopened.BeginNoteDraft(scope, Owner, separate.Id)!.Markdown);
        Assert.Equal("Deliberate edit of delivered creation", reopened.BeginNoteDraft(scope, Owner, draft.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Plain_IO_failure_retries_the_same_base_and_success_returns_the_base_for_newer_typing(NoteScope scope)
    {
        using var app = Open();
        var empty = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner));
        var original = Assert.IsType<NoteDraft>(app.SaveNoteDraft(empty, "Context", "Original"));
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", original.Id + ".md");
        var bytes = File.ReadAllBytes(file);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Null(app.SaveNoteDraft(original, "First edit", "First writing"));
        Assert.False(app.CanSafelyClose);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        var retry = app.SaveNoteDraft(original, "First edit", "First writing");
        Assert.True(retry is not null, app.Status);
        var accepted = Assert.IsType<NoteDraft>(retry);
        Assert.True(app.CanSafelyClose);
        Assert.Equal("First writing", accepted.Markdown);
        var newer = Assert.IsType<NoteDraft>(app.SaveNoteDraft(accepted, "Newer edit", "Typed while saving"));
        Assert.Equal("Typed while saving", newer.Markdown);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Typed while saving", reopened.BeginNoteDraft(scope, Owner, original.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Delivered_deletion_before_first_autosave_is_not_recreated_by_the_retained_draft(NoteScope scope)
    {
        using var app = Open();
        var empty = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner));
        var original = Assert.IsType<NoteDraft>(app.SaveNoteDraft(empty, "Context", "Original"));
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", original.Id + ".md");
        File.Delete(file);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Null(app.SaveNoteDraft(original, "Retained title", "Retained local writing"));
        Assert.Null(app.SaveNoteDraft(original, "Retained title", "Retained local writing"));
        Assert.Contains("changed externally", app.Status);
        Assert.False(File.Exists(file));
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(original.Id);
        Assert.True(app.CanSafelyClose);
        Assert.Null(app.BeginNoteDraft(scope, Owner, original.Id));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Retaining_newer_typing_does_not_wait_behind_in_flight_IO_and_uses_the_successful_base(NoteScope scope)
    {
        var diagnostics = new SaveEntryDiagnostics();
        var publisher = new HeldNotePublisher();
        WorkspaceViewModel? app = null;
        Task<NoteDraft?>? saving = null;
        Guid? noteId = null;
        try
        {
            app = Open(publisher, diagnostics);
            var original = app.SaveNoteDraft(app.BeginNoteDraft(scope, Owner)!, "Context", "Original")!;
            noteId = original.Id;
            app.RetainNoteDraft(original);
            publisher.HoldNextNote = true;
            diagnostics.Arm();
            saving = Task.Run(() =>
            {
                diagnostics.Admitted();
                try { return app.SaveNoteDraft(original, "First edit", "First writing"); }
                finally { diagnostics.Exited(); }
            });
            var entered = publisher.Entered.Wait(TimeSpan.FromSeconds(5));
            Assert.True(entered, "The real note publication did not start.\n" +
                EntryDiagnosticMarker + diagnostics.Snapshot(scope, noteId, saving, app, publisher));
            await Task.Run(() => app.RetainNoteDraft(original)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(app.CanSafelyClose);
            publisher.Release.Set();
            var accepted = Assert.IsType<NoteDraft>(await saving);
            app.RetainNoteDraft(accepted);
            Assert.False(app.CanSafelyClose);
            Assert.NotNull(app.SaveNoteDraft(accepted, "Newer edit", "Typed while the first save was held"));
            Assert.True(app.CanSafelyClose);
            using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Typed while the first save was held", reopened.BeginNoteDraft(scope, Owner, original.Id)!.Markdown);
        }
        finally
        {
            publisher.Release.Set();
            void Cleanup(Task<NoteDraft?>? completed)
            {
                if (completed is { IsFaulted: true } or { IsCanceled: true })
                    Console.Error.WriteLine("NOTE-SAVE-TASK-OUTCOME " +
                        diagnostics.Snapshot(scope, noteId, completed, app, publisher));
                app?.Dispose();
                publisher.Dispose();
            }
            if (saving is { IsCompleted: false })
            {
                // An entry refusal cannot wait indefinitely for an unknown write or dispose its live resources.
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
    public void Delivered_Team_confirmation_change_alone_invalidates_the_first_dirty_base()
    {
        using var app = Open();
        var original = app.SaveNoteDraft(app.BeginNoteDraft(NoteScope.Team, Owner)!, "Context", "Original")!;
        app.RetainNoteDraft(original);
        var file = Path.Combine(folder, "team-notes", original.Id + ".md");
        File.WriteAllText(file, File.ReadAllText(file).Replace(
            "\"confirmedContextRevision\": \"00000000-0000-0000-0000-000000000000\"",
            "\"confirmedContextRevision\": \"12345678-0000-4000-8000-000000000001\""));
        var bytes = File.ReadAllBytes(file);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(Assert.Single(app.GetTeamNotes(Owner)).NeedsReview);
        Assert.Null(app.SaveNoteDraft(original, original.Title, "Retained local writing"));
        Assert.Contains("changed externally", app.Status);
        Assert.Equal(bytes, File.ReadAllBytes(file));
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
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Person writing", reopened.BeginNoteDraft(NoteScope.Person, Owner, person.Id)!.Markdown);
        Assert.Equal("Team writing", reopened.BeginNoteDraft(NoteScope.Team, Owner, team.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void New_note_draft_cannot_publish_after_valid_delivery_removes_its_owner(NoteScope scope)
    {
        using var app = Open();
        Assert.True(app.OpenOwnedNotes(scope, Owner));
        var draft = app.BeginNoteDraft(scope, Owner)!;
        app.RetainNoteDraft(draft);
        var delivery = folder + "-delivery";
        try
        {
            using (var source = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(source.OpenWorkspace(delivery), source.Status);
                var preview = source.PreviewImport(Population.Replace("avery@example.com", "casey@example.com"));
                Assert.True(source.ApplyImport(preview.Id), source.Status);
            }
            foreach (var source in Directory.GetFiles(delivery, "*", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p) != ".workspace.lock")
                .OrderBy(p => Path.GetFileName(p) == "workspace.json"))
            {
                var target = Path.Combine(folder, Path.GetRelativePath(delivery, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }
            Assert.True(app.RefreshWorkspace(), app.Status);
            Assert.Equal("casey@example.com", Assert.Single(app.People).Email);
            Assert.False(app.GetOwnedNotesView(scope).IsEmpty);
            Assert.Contains("Person is unavailable", app.GetOwnedNotesView(scope).Feedback);
            Assert.Null(app.SaveNoteDraft(draft, "Retained title", "Retained local writing"));
            Assert.False(File.Exists(Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", draft.Id + ".md")));
            Assert.Contains("changed externally", app.Status);
            Assert.False(app.CanSafelyClose);
            app.DiscardUnsavedChanges(draft.Id);
            Assert.True(app.CanSafelyClose);
        }
        finally { if (Directory.Exists(delivery)) Directory.Delete(delivery, true); }
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
            WorkspaceViewModel? app, HeldNotePublisher publisher)
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
                publicationHookSetAtSnapshot = publisher.Entered.IsSet,
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
                workers = new { availableWorkers, minimumWorkers, maximumWorkers, availableIo, minimumIo, maximumIo,
                    threadCount = ThreadPool.ThreadCount, pendingWorkItems = ThreadPool.PendingWorkItemCount },
                progress = new { phaseLimit = PhaseLimit, droppedPhases = dropped, phases = observed,
                    completedEffects = new { writes = Volatile.Read(ref effects[0]), flushes = Volatile.Read(ref effects[1]),
                        publications = Volatile.Read(ref effects[2]), deletes = Volatile.Read(ref effects[3]) } },
                snapshotLimit = "Non-atomic observations after the request, including concurrent workflow callbacks. " +
                    "Absent phase/admission timestamps are not proof that a stage never ran. Progress contains no note body or file path; " +
                    "workflow strings are unmodified operation snapshots."
            });
        }
    }
}
