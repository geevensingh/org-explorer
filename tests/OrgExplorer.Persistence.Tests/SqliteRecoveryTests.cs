using System.Reflection;
using System.Diagnostics;
using OrgExplorer.Storage.ProcessFixture;
using Microsoft.Data.Sqlite;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed partial class SqliteWorkspaceTests
{
    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Canceling_exit_during_a_stalled_real_transaction_preserves_exact_drafts_fault_and_browsing(NoteScope scope)
    {
        var clock = new SaveClock();
        using var boundary = new CommitBoundary();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(fileObserver: boundary), timeProvider: clock);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.ConfigureSynchronizationFolder(Path.Combine(root, "sync")), app.Synchronization.Error);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        boundary.Hold = true;
        var note = app.CreateOwnedNoteDraft(scope, "manager@example.com")!;
        app.UpdateNoteDraft(scope, note.Id, "Exact close title", "Exact close writing");
        using var cancel = new CancellationTokenSource();
        var closing = app.PrepareForExitAsync(cancel.Token);
        try
        {
            Assert.True(boundary.Entered.Wait(TimeSpan.FromSeconds(5)));
            app.UpdateNoteDraft(scope, note.Id, "Exact close title", "Exact newer close writing");
            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(LocalSavePhase.Stalled, app.LocalSave.Phase);
            Assert.True(app.NavigatePerson("report@example.com"));
            var selection = app.PeopleNavigation;
            var draft = app.GetRetainedNoteDraft(scope, note.Id);
            var fault = app.LocalSave;
            Assert.True(app.ExitStatus.HasUnsavedChanges);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(selection, app.PeopleNavigation);
            Assert.Equal(draft, app.GetRetainedNoteDraft(scope, note.Id));
            Assert.Equal(fault, app.LocalSave);
            Assert.True(app.NavigatePerson("manager@example.com"));
            Assert.Equal("Exact newer close writing", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
            boundary.Hold = false;
            boundary.Release.Set();
            Assert.True(SpinWait.SpinUntil(() => app.GetRetainedNoteDraft(scope, note.Id)?.IsSaving != true &&
                !app.LocalSave.EditsPaused, TimeSpan.FromSeconds(5)));
            Assert.True(await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10)), app.Status);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.PrepareForExitAsync(timeout.Token);
            using var reading = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
            Assert.Equal("Exact newer close writing", reading.BeginNoteDraft(scope, "manager@example.com", note.Id)!.Markdown);
            Assert.False(reading.Synchronization.HasUnpublishedChanges);
        }
        finally
        {
            boundary.Hold = false;
            boundary.Release.Set();
            await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await app.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Reconciliation_resolves_expired_queued_requests_without_losing_or_replaying_independent_drafts()
    {
        var clock = new SaveClock();
        var proxy = DispatchProxy.Create<IApplicationWorkflow, NoteSaveBoundary>();
        var boundary = (NoteSaveBoundary)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var notificationEntered = new ManualResetEventSlim();
        using var releaseNotification = new ManualResetEventSlim();
        using var retryEntered = new ManualResetEventSlim();
        using var releaseRetry = new ManualResetEventSlim();
        using var app = new WorkspaceViewModel(proxy, timeProvider: clock);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var calls = 0;
        var savingThread = 0;
        var observeNotification = 0;
        var observedNotification = 0;
        app.WorkspaceChanged += (_, _) =>
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref savingThread) ||
                Volatile.Read(ref observeNotification) == 0 || app.LocalSave.Phase != LocalSavePhase.Indeterminate ||
                Interlocked.Exchange(ref observedNotification, 1) != 0) return;
            notificationEntered.Set();
            if (!releaseNotification.Wait(TimeSpan.FromSeconds(10)))
                throw new IOException("Fixture completed-request notification was not released.");
        };
        boundary.BeforeSave = () =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                Volatile.Write(ref savingThread, Environment.CurrentManagedThreadId);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Fixture admission was not released.");
                throw new IOException("The admitted request ended without an acknowledgment.");
            }
            if (call != 2) return;
            retryEntered.Set();
            if (!releaseRetry.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Fixture explicit retry was not released.");
        };
        var first = (await Task.Run(() => app.CreateOwnedNoteDraft(NoteScope.Person, "manager@example.com")))!;
        Task<bool>? retry = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var queued = await Task.Run(() =>
            {
                var draft = app.CreateOwnedNoteDraft(NoteScope.Team, "report@example.com")!;
                app.UpdateNoteDraft(NoteScope.Team, draft.Id, "Independent queued title", "Exact independent queued writing");
                return draft;
            });
            clock.Advance(TimeSpan.FromSeconds(4));
            Volatile.Write(ref observeNotification, 1);
            release.Set();
            Assert.True(notificationEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => app.GetRetainedNoteDraft(NoteScope.Person, first.Id)?.IsSaving != true,
                TimeSpan.FromSeconds(5)));
            Assert.Equal(LocalSavePhase.Indeterminate, app.LocalSave.Phase);
            Assert.True(app.ReconcileLocalSaves(), app.Status);
            Assert.Equal(LocalSavePhase.Recovered, app.LocalSave.Phase);
            Assert.True(app.CanEditLocalWorkspace);
            Assert.False(app.CanSafelyClose);
            retry = app.RetryNoteDraftAsync(NoteScope.Person, first.Id);
            Assert.True(retryEntered.Wait(TimeSpan.FromSeconds(5)),
                "A completed older worker still vetoed the recovered explicit retry.");
            releaseNotification.Set();
            releaseRetry.Set();
            Assert.True(await retry, app.Status);
            Assert.True(await app.RetryNoteDraftAsync(NoteScope.Team, queued.Id), app.Status);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Equal("Exact independent queued writing", Assert.Single(reopened.GetTeamNotes("report@example.com")).Markdown);
            Assert.Single(reopened.GetPersonNotes("manager@example.com"));
        }
        finally
        {
            release.Set();
            releaseNotification.Set();
            releaseRetry.Set();
            if (retry is not null) await retry.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Independent_profile_and_Preferred_name_Cancel_and_Import_eligibility_do_not_wait_for_a_stalled_note_writer()
    {
        using var observer = new CommitBoundary();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(fileObserver: observer));
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.BeginProfileEdit("manager@example.com"), app.Status);
        Assert.True(app.StageProfileTeamName("Cancelled independent label"), app.Status);
        var basis = app.GetRelationships("manager@example.com")!.Person;
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, "Cancelled independent name");
        observer.Hold = true;
        var note = app.CreateOwnedNoteDraft(NoteScope.Person, basis.Email)!;
        app.UpdateNoteDraft(NoteScope.Person, note.Id, "Retained note", "Writing survives independent Cancel");
        Task? cancellation = null;
        try
        {
            Assert.True(observer.Entered.Wait(TimeSpan.FromSeconds(5)));
            cancellation = Task.Run(() =>
            {
                Assert.False(app.CanEnterImport);
                app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
                app.CancelProfileEdit();
            });
            await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(observer.Release.IsSet);
            Assert.Null(app.ProfileEdit);
            Assert.Null(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
            Assert.NotNull(app.GetRetainedNoteDraft(NoteScope.Person, note.Id));
            Assert.False(app.CanSafelyClose);
        }
        finally
        {
            observer.Release.Set();
            if (cancellation is not null) await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(SpinWait.SpinUntil(() => app.GetRetainedNoteDraft(NoteScope.Person, note.Id)?.IsSaving != true,
                TimeSpan.FromSeconds(5)));
        }
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Null(reopened.GetRelationships(basis.Email)!.Person.TeamName);
        Assert.Null(reopened.GetRelationships(basis.Email)!.Person.PreferredName);
        Assert.Equal("Writing survives independent Cancel", Assert.Single(reopened.GetPersonNotes(basis.Email)).Markdown);
    }

    [Fact]
    public void Actual_commit_refusal_reconciles_rollback_before_retrying_the_retained_complete_profile()
    {
        using var observer = new WorkspaceFixtures.WalCommitRefusal(Workspace);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(fileObserver: observer));
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.BeginProfileEdit("manager@example.com"), app.Status);
        Assert.True(app.StageProfileTeamName("Retained after commit refusal"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.Department, "One committed history entry"), app.Status);
        var priorAttempts = observer.CommitAttempts;
        observer.Armed = true;
        Assert.False(app.SaveProfileEdit());
        Assert.True(observer.CommitAttempts > priorAttempts, "The real transaction must reach Commit, not fail at writer admission.");
        Assert.True(observer.ActualCommitFailed);
        Assert.Contains("SqliteException", app.LocalSaveError);
        Assert.Equal(LocalSaveOutcome.NotCommitted, app.LocalSave.Outcome);
        Assert.Equal(LocalSavePhase.Failed, app.LocalSave.Phase);
        Assert.Equal("Retained after commit refusal", app.ProfileEdit!.Draft.TeamName);
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Retained after commit refusal", reopened.GetRelationships("manager@example.com")!.Person.TeamName);
        Assert.Single(reopened.GetProfileTimeline("manager@example.com")!.Changes);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Lost_note_acknowledgment_reconciles_real_committed_content_before_retry_without_duplicate_creation(NoteScope scope)
    {
        var proxy = DispatchProxy.Create<IApplicationWorkflow, NoteSaveBoundary>();
        var boundary = (NoteSaveBoundary)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var app = new WorkspaceViewModel(proxy);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var once = 0;
        boundary.AfterSuccessfulSave = _ =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0) throw new IOException("The acknowledgment boundary was unavailable.");
        };
        var note = app.CreateOwnedNoteDraft(scope, "manager@example.com")!;
        app.UpdateNoteDraft(scope, note.Id, "One exact title", "One exact committed body");
        Assert.False(await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(LocalSavePhase.Indeterminate, app.LocalSave.Phase);
        Assert.Equal("One exact committed body", app.GetRetainedNoteDraft(scope, note.Id)!.Markdown);
        Assert.False(app.CanEditLocalWorkspace);
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.Equal(LocalSavePhase.Recovered, app.LocalSave.Phase);
        Assert.Null(app.GetRetainedNoteDraft(scope, note.Id));
        Assert.True(app.CanSafelyClose);
        Assert.True(await app.RetryNoteDraftAsync(scope, note.Id), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("One exact committed body", reopened.BeginNoteDraft(scope, "manager@example.com", note.Id)!.Markdown);
        Assert.Equal(note.Id, Assert.Single(scope == NoteScope.Person
            ? reopened.GetPersonNotes("manager@example.com").Select(n => n.Id)
            : reopened.GetTeamNotes("manager@example.com").Select(n => n.Id)));
    }

    [Fact]
    public async Task Queued_Person_save_stalls_without_competing_retry_and_accounts_for_the_exact_submitted_profile()
    {
        var clock = new SaveClock();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), timeProvider: clock);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.True(app.BeginProfileEdit("manager@example.com"), app.Status);
        Assert.True(app.StageProfileTeamName("Submitted label"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.Department, "Submitted department"), app.Status);
        var submitted = app.ProfileEdit!;
        using var admitted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var saving = app.RunLocalSaveAsync(() =>
        {
            admitted.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Queued profile was not released.");
            return app.SaveProfileEdit(submitted);
        });
        try
        {
            Assert.True(admitted.Wait(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(app.StageProfileTeamName("Newer unsubmitted label"), app.Status);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(LocalSavePhase.Stalled, app.LocalSave.Phase);
            Assert.False(app.SaveProfileEdit(submitted));
            Assert.False(app.StageProfileText(PersonTextField.Department, "Must not replace the retained department"));
            Assert.False(app.ReconcileLocalSaves());
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
            Assert.Equal("Newer unsubmitted label", app.ProfileEdit!.Draft.TeamName);
            release.Set();
            Assert.True(await saving.WaitAsync(TimeSpan.FromSeconds(10)), app.Status);
            Assert.Equal(LocalSavePhase.Recovered, app.LocalSave.Phase);
            Assert.Equal("Submitted label", app.ProfileEdit!.Basis.TeamName);
            Assert.Equal("Newer unsubmitted label", app.ProfileEdit.Draft.TeamName);
            Assert.False(app.CanSafelyClose);
            using (var reading = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
                Assert.Equal("Submitted label", reading.GetRelationships("manager@example.com")!.Person.TeamName);
                Assert.Equal("Submitted department", reading.GetRelationships("manager@example.com")!.Person.Department);
            }
            app.CancelProfileEdit();
            Assert.True(app.CanSafelyClose, app.Status);
        }
        finally { release.Set(); await saving.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData("before", "Person", false, false)]
    [InlineData("after", "Person", false, false)]
    [InlineData("before", "Team", false, false)]
    [InlineData("after", "Team", false, false)]
    [InlineData("before", "profile", false, false)]
    [InlineData("after", "profile", false, false)]
    [InlineData("before", "profile", true, false)]
    [InlineData("before", "profile", false, true)]
    [InlineData("after", "profile", false, true)]
    public async Task Abrupt_process_death_at_actual_SQLite_commit_boundaries_recovers_only_committed_writing(
        string boundary, string mutation, bool unresolvedEvidence, bool receiptHeld)
        => await AssertAbruptCommitRecovery(boundary, mutation, unresolvedEvidence, receiptHeld);

    private async Task AssertAbruptCommitRecovery(
        string boundary, string mutation, bool unresolvedEvidence, bool receiptHeld, bool retainShmMapping = false,
        bool restoreStaleShmIndex = false, bool retainResources = false)
    {
        Guid? retainedTeamNote = null;
        string? retainedHistory = null;
        string? retainedContexts = null;
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
            Assert.NotNull(app.CreatePersonNote("report@example.com", "Previously committed", "Survives every interruption"));
            if (retainResources)
            {
                retainedTeamNote = app.CreateTeamNote("manager@example.com", "Retained Team context", "Exact frozen Team writing")!.Id;
                var photoPath = Path.Combine(root, "mapped-shm-photo.png");
                File.WriteAllBytes(photoPath, ShmRecoveryPhoto);
                Assert.True(app.EditPhoto("report@example.com", photoPath), app.Status);
                Assert.True(app.EditPersonManager("report@example.com", null), app.Status);
                Assert.True(app.EditPersonManager("report@example.com", "manager@example.com"), app.Status);
                retainedHistory = System.Text.Json.JsonSerializer.Serialize(app.GetProfileTimeline("report@example.com"));
                retainedContexts = System.Text.Json.JsonSerializer.Serialize(app.GetHistoricalTeamContexts("report@example.com"));
                Assert.NotEmpty(app.GetHistoricalTeamContexts("report@example.com"));
            }
        }
        var readyName = @"Local\OrgExplorer-storage-crash-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig",
            Path.ChangeExtension(typeof(SqliteWorkspaceTests).Assembly.Location, ".runtimeconfig.json"),
            typeof(StorageCrash).Assembly.Location, Workspace, readyName, boundary, mutation })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var oldReadBoundary = restoreStaleShmIndex ? new ShmReadBoundary() : null;
        using var oldReader = restoreStaleShmIndex
            ? ApplicationWorkflow.CreateFileBacked(fileObserver: oldReadBoundary) : null;
        Task<bool>? oldOpening = null;
        byte[]? staleShmIndex = null;
        var processStarted = false;
        FileStream? receiptHolder = null;
        OwnedShmMapping? shmMapping = null;
        try
        {
            if (oldReader is not null)
            {
                oldOpening = Task.Run(() => oldReader.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly));
                await oldReadBoundary!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                shmMapping = new OwnedShmMapping(Path.Combine(Workspace, "workspace.sqlite-shm"));
                staleShmIndex = shmMapping.ReadIndex();
                Assert.False(oldReadBoundary.Release.IsSet);
            }
            Assert.True(process.Start());
            processStarted = true;
            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(10)),
                process.HasExited ? await process.StandardError.ReadToEndAsync() : "The real commit boundary was not reached.");
            Assert.True(File.Exists(Path.Combine(Workspace, "workspace.sqlite-wal")));
            if (boundary == "after")
                Assert.True(new FileInfo(Path.Combine(Workspace, "workspace.sqlite-wal")).Length > 32,
                    "The committed child must still own its uncheckpointed WAL before abrupt death.");
            var admission = await process.StandardOutput.ReadLineAsync();
            Console.WriteLine($"SQLITE-CRASH-ADMISSION launcherProcessId={process.Id}; observed={admission}");
            string expectedRevision;
            using (var observed = System.Text.Json.JsonDocument.Parse(admission
                ?? throw new IOException("The admitted writer did not identify its actual process.")))
            {
                Assert.Equal(process.Id, observed.RootElement.GetProperty("writerProcessId").GetInt32());
                Assert.Equal(boundary == "before" ? "Begin" : "End", observed.RootElement.GetProperty("phase").GetString());
                expectedRevision = Assert.Single(observed.RootElement.GetProperty("evidence").EnumerateArray())
                    .GetProperty(boundary == "after" ? "nextRevision" : "baseRevision").GetString()
                    ?? throw new IOException("The admitted writer did not identify its expected committed revision.");
            }
            if (receiptHeld)
            {
                // Recovery's FileShare.None must exclude this reader, not the reader exclude existing writers.
                receiptHolder = new FileStream(Assert.Single(Directory.GetFiles(Workspace, "local-write-*.json")),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var writerExited = process.HasExited;
                Assert.False(writerExited);
                Console.WriteLine($"SQLITE-CRASH-RECEIPT-READER writerProcessId={process.Id}; writerExited={writerExited}; readerProcessId={Environment.ProcessId}; access=Read; share=ReadWrite,Delete");
            }
            if (retainShmMapping && shmMapping is null)
                shmMapping = new OwnedShmMapping(Path.Combine(Workspace, "workspace.sqlite-shm"));
            if (oldOpening is not null)
            {
                oldReadBoundary!.Release.Set();
                Assert.True(await oldOpening.WaitAsync(TimeSpan.FromSeconds(5)), oldReader!.Status);
                Assert.Null(oldReader.GetRelationships("manager@example.com")!.Person.TeamName);
                oldReader.Dispose();
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            shmMapping?.AssertHeld();
            if (staleShmIndex is not null)
            {
                var committedFiles = new[] { "workspace.sqlite", "workspace.sqlite-wal" }
                    .Select(name => Path.Combine(Workspace, name)).ToArray();
                var before = committedFiles.Select(HashCommittedFile).ToArray();
                shmMapping!.RestoreIndex(staleShmIndex);
                Assert.Equal(before, committedFiles.Select(HashCommittedFile).ToArray());
            }
            string? evidence = null;
            if (unresolvedEvidence)
            {
                Assert.Single(Directory.GetFiles(Workspace, "local-write-*.json"));
                evidence = Path.Combine(Workspace, $"local-write-{Guid.NewGuid():N}.json");
                File.WriteAllText(evidence, "Interrupted invalid transaction evidence");
            }
            using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
            var reopenThread = Environment.CurrentManagedThreadId;
            var sqliteFailures = new List<(int Code, int ExtendedCode, int HResult, DateTimeOffset ObservedAt, StackTrace Stack)>(8);
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe = (_, observation) =>
            {
                if (Environment.CurrentManagedThreadId != reopenThread ||
                    observation.Exception is not SqliteException error || sqliteFailures.Count >= 8) return;
                sqliteFailures.Add((error.SqliteErrorCode, error.SqliteExtendedErrorCode, error.HResult,
                    DateTimeOffset.UtcNow, new StackTrace(error, fNeedFileInfo: false)));
            };
            bool opened;
            AppDomain.CurrentDomain.FirstChanceException += observe;
            try { opened = reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
            var reopenStatus = reopened.Status;
            string? emissionFault = null;
            if (!opened)
                try
                {
                    Console.WriteLine("SQLITE-CRASH-REOPEN-FAILURE " + System.Text.Json.JsonSerializer.Serialize(new
                    {
                        boundary, mutation, unresolvedEvidence, receiptHeld,
                        writerProcessId = process.Id, writerExited = process.HasExited, writerExitCode = process.ExitCode,
                        testProcessId = Environment.ProcessId, reopenThread, admission, expectedRevision,
                        phase = "After public Open returned false and its cleanup attempt; non-atomic, not original I/O or lock-holder proof.",
                        observedAt = DateTimeOffset.UtcNow, status = reopenStatus,
                        sqliteFailures = sqliteFailures.Select(failure => new
                        {
                            failure.Code, failure.ExtendedCode, failure.HResult, failure.ObservedAt,
                            frames = (failure.Stack.GetFrames() ?? []).Take(16)
                                .Select(frame => frame.GetMethod() is { } method
                                    ? $"{method.DeclaringType?.FullName}.{method.Name}" : "<unavailable>").ToArray()
                        }).ToArray(),
                        files = new[] { "workspace.sqlite", "workspace.sqlite-wal", "workspace.sqlite-shm" }.Select(name =>
                        {
                            var path = Path.Combine(Workspace, name);
                            var observedAt = DateTimeOffset.UtcNow;
                            bool? exists = null;
                            long? length = null;
                            string? metadataFault = null;
                            try
                            {
                                var file = new FileInfo(path);
                                exists = file.Exists;
                                if (file.Exists) length = file.Length;
                            }
                            catch (Exception fault) when (fault is IOException or UnauthorizedAccessException)
                            {
                                metadataFault = $"{fault.GetType().Name}; hresult=0x{fault.HResult:X8}; {fault.Message}";
                            }
                            string share;
                            try
                            {
                                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete);
                                share = "Read/share-ReadWrite,Delete opened; no bytes read or written";
                            }
                            catch (Exception fault) when (fault is IOException or UnauthorizedAccessException)
                            {
                                share = $"{fault.GetType().Name}; hresult=0x{fault.HResult:X8}; {fault.Message}";
                            }
                            return new { name, observedAt, exists, length, metadataFault, share };
                        }).ToArray()
                    }));
                }
                catch (Exception fault) when (fault is IOException or UnauthorizedAccessException or
                    InvalidOperationException or ArgumentException or NotSupportedException or TypeLoadException or
                    System.Security.SecurityException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
                {
                    emissionFault = $"\nSQLITE-CRASH-REOPEN-DIAGNOSTIC-FAULT {fault.GetType().Name}; hresult=0x{fault.HResult:X8}.";
                }
            Assert.True(opened, reopenStatus + emissionFault);
            Assert.Equal(expectedRevision, reopened.LocalSave.CommittedRevision);
            if (!unresolvedEvidence && reopened.LocalSave.Phase != LocalSavePhase.Recovered)
                Console.WriteLine("SQLITE-CRASH-UNRESOLVED " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    boundary, mutation, testProcessId = Environment.ProcessId, launcherProcessId = process.Id,
                    launcherExited = process.HasExited, launcherExitCode = process.ExitCode, admission,
                    observedAt = DateTimeOffset.UtcNow,
                    state = reopened.LocalSave, receipts = Directory.GetFiles(Workspace, "local-write-*.json")
                        .Select(path => new { file = Path.GetFileName(path), users = LeaseOwners.Read([path]) }).ToArray()
                }));
            if (evidence is not null)
            {
                Assert.Equal(LocalSavePhase.Indeterminate, reopened.LocalSave.Phase);
                Assert.Contains("receipt-stage=read", reopened.LocalSave.Error);
                Assert.Contains("managed-receipt-handle=closed", reopened.LocalSave.Error);
                Assert.False(reopened.CanEditLocalWorkspace);
                Assert.True(reopened.NavigatePerson("report@example.com"), reopened.Status);
                Assert.True(reopened.NavigatePerson("manager@example.com"), reopened.Status);
                Assert.Null(reopened.GetRelationships("manager@example.com")!.Person.TeamName);
                Assert.False(reopened.ReconcileLocalSaves());
                Assert.Equal("Interrupted invalid transaction evidence", File.ReadAllText(evidence));
                return;
            }
            if (receiptHeld) Assert.Equal(LocalSavePhase.Indeterminate, reopened.LocalSave.Phase);
            if (reopened.LocalSave.Phase == LocalSavePhase.Indeterminate)
            {
                Assert.Equal(LocalSaveOutcome.Unknown, reopened.LocalSave.Outcome);
                Assert.Contains("receipt-stage=acquire", reopened.LocalSave.Error);
                Assert.Contains("managed-receipt-handle=not-acquired", reopened.LocalSave.Error);
                Assert.Contains("hresult=0x80070020", reopened.LocalSave.Error);
                Assert.Single(Directory.GetFiles(Workspace, "local-write-*.json"));
                Assert.False(reopened.CanEditLocalWorkspace);
                Assert.False(reopened.CanSafelyClose);
                Assert.True(reopened.NavigatePerson("report@example.com"), reopened.Status);
                Assert.True(reopened.NavigatePerson("manager@example.com"), reopened.Status);
                Assert.False(reopened.EditPreferredName("manager@example.com", "Must not bypass unresolved evidence"));
                Assert.Null(reopened.GetRelationships("manager@example.com")!.Person.PreferredName);
            }
            else
            {
                Assert.Equal(LocalSavePhase.Recovered, reopened.LocalSave.Phase);
                Assert.True(reopened.CanEditLocalWorkspace);
            }
            Assert.Equal(expectedRevision, reopened.LocalSave.CommittedRevision);
            AssertCommittedWriting();
            shmMapping?.AssertHeld();
            if (receiptHeld)
            {
                var receipt = Assert.Single(Directory.GetFiles(Workspace, "local-write-*.json"));
                Assert.False(reopened.ReconcileLocalSaves());
                Assert.Equal(LocalSavePhase.Indeterminate, reopened.LocalSave.Phase);
                Assert.Equal(expectedRevision, reopened.LocalSave.CommittedRevision);
                AssertCommittedWriting();
                Assert.NotNull(receiptHolder);
                receiptHolder.Dispose();
                receiptHolder = null;
                using (new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                Assert.True(reopened.ReconcileLocalSaves(), reopened.Status);
                Assert.Equal(LocalSavePhase.Recovered, reopened.LocalSave.Phase);
                Assert.True(reopened.CanEditLocalWorkspace);
                Assert.True(reopened.CanSafelyClose, reopened.Status);
                Assert.Equal(expectedRevision, reopened.LocalSave.CommittedRevision);
                Assert.Empty(Directory.GetFiles(Workspace, "local-write-*.json"));
                AssertCommittedWriting();
            }
            Console.WriteLine($"SQLITE-CRASH-RECOVERY boundary={boundary}; mutation={mutation}; pid={process.Id}; outcome={reopened.LocalSave.Phase}; validatedPeople={reopened.People.Count}; controlledReceiptHeld={receiptHeld}");

            void AssertCommittedWriting()
            {
                Assert.Equal("Survives every interruption", Assert.Single(reopened.GetPersonNotes("report@example.com")).Markdown);
                if (retainedTeamNote is { } retainedId)
                {
                    var retained = reopened.BeginNoteDraft(NoteScope.Team, "manager@example.com", retainedId)!;
                    Assert.Equal("manager@example.com", retained.OwnerEmail);
                    Assert.Equal("Retained Team context", retained.Title);
                    Assert.Equal("Exact frozen Team writing", retained.Markdown);
                    Assert.Null(reopened.BeginNoteDraft(NoteScope.Team, "report@example.com", retainedId));
                    Assert.Equal(ShmRecoveryPhoto, reopened.GetPhoto("report@example.com")!.Bytes.ToArray());
                    Assert.Equal(retainedHistory, System.Text.Json.JsonSerializer.Serialize(reopened.GetProfileTimeline("report@example.com")));
                    Assert.Equal(retainedContexts, System.Text.Json.JsonSerializer.Serialize(reopened.GetHistoricalTeamContexts("report@example.com")));
                }
                if (mutation == "profile")
                {
                    var manager = reopened.GetRelationships("manager@example.com")!.Person;
                    Assert.Equal(boundary == "after" ? "Crash boundary label" : null, manager.TeamName);
                    Assert.Equal(boundary == "after" ? "Crash boundary department" : null, manager.Department);
                    Assert.Equal(boundary == "after" ? 1 : 0, reopened.GetProfileTimeline(manager.Email)!.Changes.Count);
                }
                else
                {
                    var notes = mutation == "Person" ? reopened.GetPersonNotes("manager@example.com").Select(n => n.Markdown) :
                        reopened.GetTeamNotes("manager@example.com").Where(n => n.Id != retainedTeamNote).Select(n => n.Markdown);
                    Assert.Equal(boundary == "after" ? ["Exact crash boundary writing"] : Array.Empty<string>(), notes);
                }
            }
        }
        finally
        {
            oldReadBoundary?.Release.Set();
            try
            {
                if (processStarted && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                if (oldOpening is not null)
                    Assert.True(await oldOpening.WaitAsync(TimeSpan.FromSeconds(5)), oldReader!.Status);
            }
            finally
            {
                receiptHolder?.Dispose();
                shmMapping?.Dispose();
            }
            var receipts = Directory.GetFiles(Workspace, "local-write-*.json");
            if (receipts.Length != 0 && processStarted)
                Console.WriteLine("SQLITE-CRASH-REMAINING " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    boundary, mutation, unresolvedEvidence, testProcessId = Environment.ProcessId,
                    launcherProcessId = process.Id, launcherExited = process.HasExited,
                    observedAt = DateTimeOffset.UtcNow,
                    receipts = receipts.Select(path => new { file = Path.GetFileName(path), users = LeaseOwners.Read([path]) }).ToArray()
                }));
        }

        static string HashCommittedFile(string path)
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
        }
    }

    [Fact]
    public async Task Committed_Team_writing_stays_read_only_until_its_actual_owned_receipt_lease_is_released()
    {
        using var boundary = new CommitBoundary { Boundary = WorkspaceStoragePhaseKind.End };
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: boundary);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        boundary.Hold = true;
        var saving = Task.Run(() => app.CreateTeamNote("manager@example.com", "Committed but unacknowledged",
            "Exact committed Team writing under the owned receipt lease"));
        try
        {
            Assert.True(boundary.Entered.Wait(TimeSpan.FromSeconds(5)));
            using var reading = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
            Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
            Assert.Equal(LocalSavePhase.Indeterminate, reading.LocalSave.Phase);
            Assert.Equal(LocalSaveOutcome.Unknown, reading.LocalSave.Outcome);
            Assert.False(reading.CanEditLocalWorkspace);
            var committed = Assert.Single(reading.GetTeamNotes("manager@example.com"));
            Assert.Equal("Exact committed Team writing under the owned receipt lease", committed.Markdown);
            var revision = reading.LocalSave.CommittedRevision;
            var receipt = Assert.Single(Directory.GetFiles(Workspace, "local-write-*.json"));
            Assert.Throws<IOException>(() =>
            {
                using var unavailable = new FileStream(receipt, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            Assert.Contains("receipt-stage=acquire", reading.LocalSave.Error);
            Assert.Contains("managed-receipt-handle=not-acquired", reading.LocalSave.Error);
            Assert.Contains("hresult=0x80070020", reading.LocalSave.Error);
            Assert.True(reading.NavigatePerson("report@example.com"), reading.Status);
            Assert.True(reading.NavigatePerson("manager@example.com"), reading.Status);
            Assert.False(reading.ReconcileLocalSaves());
            boundary.Release.Set();
            var acknowledged = await saving.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(acknowledged);
            Assert.Equal(committed.Id, acknowledged.Id);
            Assert.True(reading.ReconcileLocalSaves(), reading.Status);
            Assert.Equal(LocalSavePhase.Recovered, reading.LocalSave.Phase);
            Assert.True(reading.CanEditLocalWorkspace);
            Assert.Equal(revision, reading.LocalSave.CommittedRevision);
            Assert.Equal(committed.Markdown, Assert.Single(reading.GetTeamNotes("manager@example.com")).Markdown);
        }
        finally { boundary.Release.Set(); await saving.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Reopening_during_an_unresolved_real_transaction_browses_the_validated_commit_but_cannot_replace_it()
    {
        using var boundary = new CommitBoundary();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: boundary);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        boundary.Hold = true;
        var saving = Task.Run(() => app.EditPreferredName("manager@example.com", "Committed after release"));
        try
        {
            Assert.True(boundary.Entered.Wait(TimeSpan.FromSeconds(5)));
            using var reading = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
            Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
            Assert.Equal(LocalSavePhase.Indeterminate, reading.LocalSave.Phase);
            Assert.False(reading.CanEditLocalWorkspace);
            Assert.True(reading.NavigatePerson("report@example.com"), reading.Status);
            Assert.True(reading.NavigatePerson("manager@example.com"), reading.Status);
            Assert.Null(reading.GetRelationships("manager@example.com")!.Person.PreferredName);
            Assert.False(reading.EditPreferredName("manager@example.com", "Competing mutation"));
            Assert.False(reading.ReconcileLocalSaves());
            boundary.Release.Set();
            Assert.True(await saving.WaitAsync(TimeSpan.FromSeconds(10)), app.Status);
            Assert.True(reading.ReconcileLocalSaves(), reading.Status);
            Assert.Equal("Committed after release", reading.GetRelationships("manager@example.com")!.Person.PreferredName);
            Assert.True(reading.CanEditLocalWorkspace);
        }
        finally { boundary.Release.Set(); await saving.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void Storage_failure_pauses_edits_but_retains_complete_Team_profile_and_independent_note_for_safe_retry()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.BeginProfileEdit("manager@example.com"), app.Status);
        Assert.True(app.StageProfileTeamName("Exact retained label"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.Department, "Exact retained department"), app.Status);
        var note = app.BeginNoteDraft(NoteScope.Person, "report@example.com")!;
        app.RetainNoteDraft(note);
        using (File.Open(Path.Combine(Workspace, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(app.SaveProfileEdit());
        Assert.Equal(LocalSavePhase.Failed, app.LocalSave.Phase);
        Assert.Equal(LocalSaveOutcome.NotCommitted, app.LocalSave.Outcome);
        Assert.False(app.CanEditLocalWorkspace);
        Assert.False(app.StageProfileTeamName("Must not replace retained writing"));
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.Equal("Exact retained label", app.ProfileEdit!.Draft.TeamName);
        Assert.Equal("Exact retained department", app.ProfileEdit.Draft.Department);
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.Equal(LocalSavePhase.Recovered, app.LocalSave.Phase);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.HasUnsavedNoteDraft(NoteScope.Person, note.Id));
        Assert.NotNull(app.SaveNoteDraft(note, "Independent title", "Exact independent writing"));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Exact retained label", reopened.GetRelationships("manager@example.com")!.Person.TeamName);
        Assert.Equal("Exact independent writing", Assert.Single(reopened.GetPersonNotes("report@example.com")).Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Oldest_note_request_stalls_including_queueing_and_late_commit_preserves_newer_writing(NoteScope scope)
    {
        var clock = new SaveClock();
        var proxy = DispatchProxy.Create<IApplicationWorkflow, NoteSaveBoundary>();
        var boundary = (NoteSaveBoundary)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var admitted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var app = new WorkspaceViewModel(proxy, timeProvider: clock);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        boundary.BeforeSave = () =>
        {
            admitted.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Save return not released.");
        };
        try
        {
            var note = app.CreateOwnedNoteDraft(scope, "manager@example.com")!;
            app.UpdateNoteDraft(scope, note.Id, "Retained title", "First writing");
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(LocalSavePhase.Pending, app.LocalSave.Phase);
            Assert.True(admitted.Wait(TimeSpan.FromSeconds(5)));
            app.UpdateNoteDraft(scope, note.Id, "Retained title", "Newer exact writing");
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(LocalSavePhase.Stalled, app.LocalSave.Phase);
            Assert.Equal(LocalSaveOutcome.Unknown, app.LocalSave.Outcome);
            Assert.False(app.CanEditLocalWorkspace);
            Assert.False(app.ReconcileLocalSaves());
            Assert.False(await app.RetryNoteDraftAsync(scope, note.Id).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
            Assert.Equal("Newer exact writing", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
            Assert.False(app.CanSafelyClose);
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => app.GetRetainedNoteDraft(scope, note.Id)?.IsSaving != true &&
                !app.LocalSave.EditsPaused, TimeSpan.FromSeconds(5)));
            Assert.True(await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10)), app.Status);
            Assert.Equal(LocalSavePhase.Recovered, app.LocalSave.Phase);
            Assert.True(app.CanEditLocalWorkspace);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Equal("Newer exact writing", reopened.BeginNoteDraft(scope, "manager@example.com", note.Id)!.Markdown);
        }
        finally { release.Set(); await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class SaveClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }

    private sealed class CommitBoundary : IWorkspaceFileObserver, IDisposable
    {
        public bool Hold;
        public WorkspaceStoragePhaseKind Boundary = WorkspaceStoragePhaseKind.Begin;
        public int CommitAttempts;
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name == "local-sqlite-commit" && phase.Kind == WorkspaceStoragePhaseKind.Begin)
                Interlocked.Increment(ref CommitAttempts);
            if (!Hold || phase.Name != "local-sqlite-commit" || phase.Kind != Boundary) return;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Commit fixture was not released.");
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
