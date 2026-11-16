using OrgExplorer.Workflow;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Runtime.ExceptionServices;
using OrgExplorer.Desktop.Smoke;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class WorkspaceSnapshotTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".test-workspaces",
        "snapshots-" + Guid.NewGuid().ToString("N")));
    private string Local(string name) => Path.Combine(root, name);
    private const string Initial = """
        {"schemaVersion":1,"batchId":"96000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","producer":"Snapshot acceptance","people":[
         {"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com"},
         {"email":"manager@example.com","displayName":"Manager","managerEmail":null}]}
        """;

    [Fact]
    public async Task Automatic_cadence_publishes_continuous_edits_without_waiting_for_idle()
    {
        var clock = new SnapshotClock();
        using var first = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), timeProvider: clock);
        Assert.True(first.OpenWorkspace(Local("first"), WorkspaceOpenMode.CreateLocalSqlite), first.Status);
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        for (var edit = 1; edit <= 5; edit++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(first.EditPreferredName("report@example.com", $"Continuous writing {edit}"), first.Status);
        }
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(Directory.GetFiles(Local("outgoing"), "*.oesnapshot"));
        Assert.True(first.EditPreferredName("report@example.com", "Writing at second 59"), first.Status);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AwaitSnapshot(() => !first.Synchronization.HasUnpublishedChanges);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("Writing at second 59", second.GetRelationships("report@example.com")!.Person.PreferredName);
        for (var edit = 1; edit <= 6; edit++)
        {
            Assert.True(first.EditPreferredName("report@example.com", $"Next minute {edit}"), first.Status);
            clock.Advance(TimeSpan.FromSeconds(10));
        }
        await AwaitSnapshot(() => !first.Synchronization.HasUnpublishedChanges);
        Deliver("outgoing", "incoming");
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("Next minute 6", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Contains("delivery elsewhere is unknown", first.Synchronization.Summary);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Periodic_and_manual_requests_share_export_while_newer_owner_writing_commits_independently(NoteScope scope)
    {
        var clock = new SnapshotClock();
        using var secondExport = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var publications = 0;
        var publisher = new SnapshotPublisher
        {
            Hold = true,
            BeforeSnapshot = () =>
            {
                if (Interlocked.Increment(ref publications) != 2) return;
                secondExport.Set();
                if (!releaseSecond.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Second export was not released.");
            }
        };
        using var first = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(filePublisher: publisher), timeProvider: clock);
        Assert.True(first.OpenWorkspace(Local("first"), WorkspaceOpenMode.CreateLocalSqlite), first.Status);
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        var original = first.Synchronization.LocalRevision;
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(publisher.Entered.Wait(TimeSpan.FromSeconds(5)));
        Task<bool>? manual = null;
        try
        {
            Assert.True(first.IsSynchronizing);
            Assert.Equal(WorkspaceSynchronizationPhase.Publishing, first.Synchronization.Phase);
            var owner = scope == NoteScope.Person ? "report@example.com" : "manager@example.com";
            var note = first.CreateOwnedNoteDraft(scope, owner)!;
            first.UpdateNoteDraft(scope, note.Id, "Writing during real export", "First owner generation");
            Assert.True(await first.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(1)), first.Status);
            first.UpdateNoteDraft(scope, note.Id, "Writing during real export", "Latest owner generation");
            Assert.True(first.NavigatePerson(owner == "report@example.com" ? "manager@example.com" : "report@example.com"), first.Status);
            Assert.True(await first.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(1)), first.Status);
            using (var reading = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(reading.OpenWorkspace(Local("first"), WorkspaceOpenMode.ExistingOnly), reading.Status);
                Assert.Equal("Latest owner generation", reading.BeginNoteDraft(scope, owner, note.Id)!.Markdown);
            }
            clock.Advance(TimeSpan.FromSeconds(120));
            manual = first.SynchronizeAsync();
            Assert.False(manual.IsCompleted);
            publisher.Release.Set();
            Assert.True(secondExport.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(original, first.Synchronization.PublishedRevision);
            Assert.True(first.Synchronization.HasUnpublishedChanges);
            Assert.True(first.CanEditLocalWorkspace);
            Deliver("outgoing", "incoming");
            using var second = Create("second");
            Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
            Assert.True(second.SyncNow(), second.Synchronization.Error);
            Assert.Null(second.BeginNoteDraft(scope, owner, note.Id));
            releaseSecond.Set();
            Assert.True(await manual.WaitAsync(TimeSpan.FromSeconds(10)), first.Synchronization.Error);
            Assert.False(first.IsSynchronizing);
            Assert.False(first.Synchronization.HasUnpublishedChanges);
            Deliver("outgoing", "incoming");
            Assert.True(second.SyncNow(), second.Synchronization.Error);
            Assert.Equal("Latest owner generation", second.BeginNoteDraft(scope, owner, note.Id)!.Markdown);
            Assert.False(first.Synchronization.Diverged);
        }
        finally
        {
            publisher.Release.Set();
            releaseSecond.Set();
            if (manual is not null) await manual.WaitAsync(TimeSpan.FromSeconds(10));
            await AwaitSnapshot(() => !first.IsSynchronizing);
        }
    }

    [Fact]
    public async Task Automatic_incoming_checks_keep_the_last_valid_root_and_retry_without_pausing_local_edits()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var clock = new SnapshotClock();
        using var second = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), timeProvider: clock);
        Assert.True(second.OpenWorkspace(Local("second"), WorkspaceOpenMode.CreateLocalSqlite), second.Status);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.NavigatePerson("report@example.com"), second.Status);
        second.ChangePeopleQuery("Report");
        var browsing = second.PeopleNavigation;
        Assert.True(first.EditPreferredName("report@example.com", "Automatically delivered"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var newest = Directory.GetFiles(Local("incoming"), "*.oesnapshot").Single(path =>
            Path.GetFileName(path).Contains(first.Synchronization.LocalRevision!.Value.ToString("D")));
        var valid = File.ReadAllBytes(newest);
        File.WriteAllBytes(newest, valid[..(valid.Length / 2)]);
        clock.Advance(TimeSpan.FromSeconds(60));
        await AwaitSnapshot(() => second.Synchronization.Error is not null && !second.IsSynchronizing);
        Assert.Null(second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.True(second.CanEditLocalWorkspace);
        Assert.Null(second.LocalSaveError);
        File.WriteAllBytes(newest, valid);
        clock.Advance(TimeSpan.FromSeconds(60));
        await AwaitSnapshot(() => second.GetRelationships("report@example.com")!.Person.PreferredName == "Automatically delivered" &&
            !second.IsSynchronizing);
        Assert.Null(second.Synchronization.Error);
        Assert.Equal(browsing, second.PeopleNavigation);
        Directory.Move(Local("incoming"), Local("provider-unavailable"));
        clock.Advance(TimeSpan.FromSeconds(60));
        await AwaitSnapshot(() => second.Synchronization.Error is not null && !second.IsSynchronizing);
        Assert.True(second.EditPreferredName("report@example.com", "Local writing during outage"), second.Status);
        Assert.True(second.CanEditLocalWorkspace);
        Assert.Null(second.LocalSaveError);
        Directory.Move(Local("provider-unavailable"), Local("incoming"));
        clock.Advance(TimeSpan.FromSeconds(60));
        await AwaitSnapshot(() => !second.Synchronization.HasUnpublishedChanges && !second.IsSynchronizing);
        Assert.Null(second.Synchronization.Error);
        Deliver("incoming", "outgoing");
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Assert.Equal("Local writing during outage", first.GetRelationships("report@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reopening_unpublished_work_resumes_on_the_cadence_without_replaying_mutations(bool alreadyOpen)
    {
        Guid? revision;
        using (var first = Create("first"))
        {
            Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
            Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
            Assert.True(first.EditPreferredName("report@example.com", "Committed before reopen"), first.Status);
            revision = first.Synchronization.LocalRevision;
        }
        var clock = new SnapshotClock();
        var core = ApplicationWorkflow.CreateFileBacked();
        if (alreadyOpen) Assert.True(core.OpenWorkspace(Local("first"), WorkspaceOpenMode.ExistingOnly), core.Status);
        using var reopened = new WorkspaceViewModel(core, timeProvider: clock);
        if (!alreadyOpen) Assert.True(reopened.OpenWorkspace(Local("first"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(revision, reopened.Synchronization.LocalRevision);
        clock.Advance(TimeSpan.FromSeconds(60));
        await AwaitSnapshot(() => !reopened.Synchronization.HasUnpublishedChanges && !reopened.IsSynchronizing);
        Assert.Equal(revision, reopened.Synchronization.LocalRevision);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("Committed before reopen", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Single(second.GetProfileTimeline("report@example.com")!.Changes);
        reopened.Dispose();
        Assert.True(second.EditFavorite("report@example.com", true), second.Status);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Deliver("incoming", "outgoing");
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(reopened.IsSynchronizing);
        using var reading = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reading.OpenWorkspace(Local("first"), WorkspaceOpenMode.ExistingOnly), reading.Status);
        Assert.False(reading.GetRelationships("report@example.com")!.Person.IsFavorite);
    }

    [Fact]
    public async Task A_lost_automatic_sync_return_is_visible_and_retry_does_not_replay_committed_writing()
    {
        var clock = new SnapshotClock();
        var proxy = DispatchProxy.Create<IApplicationWorkflow, SnapshotSaveBoundary>();
        var boundary = (SnapshotSaveBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var first = new WorkspaceViewModel(proxy, timeProvider: clock);
        Assert.True(first.OpenWorkspace(Local("first"), WorkspaceOpenMode.CreateLocalSqlite), first.Status);
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.EditPreferredName("report@example.com", "One committed edit"), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        var revision = first.Synchronization.LocalRevision;
        boundary.AfterSync = () => throw new IOException("Actual complete publication return was lost.");
        clock.Advance(TimeSpan.FromSeconds(60));
        await AwaitSnapshot(() => !first.IsSynchronizing);
        Assert.Contains("return was lost", first.Synchronization.Error);
        Assert.True(first.CanEditLocalWorkspace);
        Assert.Null(first.LocalSaveError);
        boundary.AfterSync = null;
        clock.Advance(TimeSpan.FromSeconds(60));
        await AwaitSnapshot(() => !first.IsSynchronizing);
        Assert.Null(first.Synchronization.Error);
        Assert.Equal(revision, first.Synchronization.LocalRevision);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("One committed edit", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Single(second.GetProfileTimeline("report@example.com")!.Changes);
    }

    [Fact]
    public void Explicit_publication_and_fresh_join_transfer_one_identity_and_complete_local_writing()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        var note = first.CreatePersonNote("report@example.com", "Private writing", "# Exact Markdown")!;
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        var identity = first.Synchronization.WorkspaceId;
        var revision = first.Synchronization.LocalRevision;
        Assert.NotNull(identity);
        Assert.True(first.Synchronization.HasUnpublishedChanges);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Assert.False(first.Synchronization.HasUnpublishedChanges);
        Assert.Contains("delivery elsewhere is unknown", first.Synchronization.Summary);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), joinExistingWorkspace: true),
            second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal(identity, second.Synchronization.WorkspaceId);
        Assert.Equal(revision, second.Synchronization.LocalRevision);
        Assert.Equal(2, second.People.Count);
        Assert.Equal("# Exact Markdown", Assert.Single(second.GetPersonNotes("report@example.com")).Markdown);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(identity, reopened.Synchronization.WorkspaceId);
        Assert.Equal(note, Assert.Single(reopened.GetPersonNotes("report@example.com")));
        var replay = reopened.PreviewImport(Initial);
        Assert.True(replay.CanApply, reopened.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clean_continuation_preserves_private_dormant_names_current_paths_photos_and_complete_frozen_history(bool reviewedPhotoImport)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.NavigatePerson("report@example.com"), second.Status);
        second.ChangePeopleQuery("Report");
        var navigation = second.PeopleNavigation;
        const string manager = "manager@example.com", report = "report@example.com";
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        var portrait = Local("portrait.png");
        File.WriteAllBytes(portrait, image);
        Assert.True(first.EditPhoto(manager, portrait), first.Status);
        Assert.True(first.BeginProfileEdit(manager), first.Status);
        Assert.True(first.StageProfileTeamName("Private platform"), first.Status);
        Assert.True(first.SaveProfileEdit(), first.Status);
        var team = first.CreateTeamNote(manager, "Former context", "# Frozen manager writing")!;
        Assert.True(first.EditPersonText(manager, PersonTextField.JobTitle, "Changed context"), first.Status);
        Assert.True(Assert.Single(first.GetTeamNotes(manager)).NeedsReview);
        string? importPath = null;
        if (reviewedPhotoImport)
        {
            importPath = Local("reviewed-continuation.json");
            File.WriteAllText(importPath, """
                {"schemaVersion":1,"batchId":"96000000-0000-4000-8000-000000000004",
                 "observedAt":"2026-11-02T17:00:00Z","producer":"Snapshot reviewed photo continuation","people":[
                 {"email":"manager@example.com","department":"Imported observation","photoPath":"portrait.png"},
                 {"email":"report@example.com","managerEmail":null}]}
                """);
            var preview = first.PreviewImportFile(importPath);
            Assert.True(preview.CanApply, first.Status);
            Assert.True(first.ApplyImport(preview.Id), first.Status);
        }
        else Assert.True(first.EditPersonManager(report, null), first.Status);
        var expected = Assert.Single(first.GetHistoricalTeamContexts(report));
        Assert.True(Assert.Single(expected.TeamNotes).NeedsReview);
        Assert.True(first.EditTeamNote(team.Id, "Current writing", "New current Markdown"), first.Status);
        Assert.True(first.EditPhoto(manager, null), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");

        // Even a hash-valid artifact is incomplete without resources retained only by history.
        var latest = Directory.GetFiles(Local("incoming"), "*.oesnapshot").Single(file =>
            Path.GetFileName(file).Contains(first.Synchronization.LocalRevision!.Value.ToString("D")));
        var original = File.ReadAllBytes(latest);
        RewriteSnapshot(latest, data => data["photos"] = new JsonArray());
        Assert.False(second.SyncNow());
        Assert.True(second.IsWorkspaceCurrent);
        Assert.Empty(second.GetHistoricalTeamContexts(report));
        Assert.Null(second.GetPhoto(report));
        Assert.Equal(navigation, second.PeopleNavigation);
        File.WriteAllBytes(latest, original);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal(navigation, second.PeopleNavigation);
        Assert.Equal("Private platform", second.GetRelationships(manager)!.Person.TeamName);
        Assert.Empty(second.GetTeamNames(manager));
        Assert.Null(second.GetPhoto(manager));
        Assert.Null(second.GetPhoto(report));
        if (importPath is not null)
        {
            var revision = second.Synchronization.LocalRevision;
            var replay = second.PreviewImportFile(importPath);
            Assert.True(replay.CanApply, second.Status);
            Assert.Contains("no-op", Assert.Single(replay.Changes));
            Assert.True(second.ApplyImport(replay.Id), second.Status);
            Assert.Equal(revision, second.Synchronization.LocalRevision);
            Assert.Null(second.GetPhoto(manager));
        }
        Assert.True(first.EditPhoto(report, portrait), first.Status);
        File.Delete(portrait);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal(navigation, second.PeopleNavigation);
        Assert.Equal(image, second.GetPhoto(report)!.Bytes.ToArray());
        var actual = Assert.Single(second.GetHistoricalTeamContexts(report));
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.CapturedAt, actual.CapturedAt);
        Assert.Equal(expected.DisplayProvenance, actual.DisplayProvenance);
        Assert.Equal(expected.TeamNotes, actual.TeamNotes);
        Assert.Null(Assert.Single(actual.ReportingChain).TeamName);
        Assert.NotNull(Assert.Single(actual.ReportingChain).PhotoPath);
        Assert.Equal(first.GetProfileTimeline(manager)!.DisplayChanges, second.GetProfileTimeline(manager)!.DisplayChanges);
        Assert.True(second.EditPersonManager(report, manager), second.Status);
        Assert.Equal("Private platform", Assert.Single(second.GetTeamNames(report)).Name);
        Assert.Equal(manager, Assert.Single(second.GetTeamNames(report)).Owner.Email);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(image, reopened.GetPhoto(report)!.Bytes.ToArray());
        Assert.Equal(expected.TeamNotes, reopened.GetHistoricalTeamContexts(report)[0].TeamNotes);
        Assert.Equal(first.GetTeamNotes(manager), reopened.GetTeamNotes(manager));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("hash")]
    [InlineData("format")]
    [InlineData("identity")]
    [InlineData("ancestry")]
    [InlineData("accepted-evidence")]
    [InlineData("missing-person-field")]
    [InlineData("null-record")]
    public void Invalid_delivery_keeps_the_last_valid_Workspace_and_explicit_error(string damage)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        var originalRevision = second.Synchronization.LocalRevision;
        Assert.True(first.EditPreferredName("report@example.com", "New delivered name"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var latest = Directory.GetFiles(Local("incoming"), "*.oesnapshot").Single(file =>
            Path.GetFileName(file).Contains(first.Synchronization.LocalRevision!.Value.ToString("D")));
        var original = File.ReadAllBytes(latest);
        switch (damage)
        {
            case "truncated": File.WriteAllBytes(latest, original[..(original.Length / 2)]); break;
            case "hash":
                var envelope = JsonNode.Parse(original)!.AsObject();
                envelope["sha256"] = new string('0', 64);
                File.WriteAllText(latest, envelope.ToJsonString());
                break;
            case "format":
                var unsupported = JsonNode.Parse(original)!.AsObject();
                unsupported["version"] = 999;
                File.WriteAllText(latest, unsupported.ToJsonString());
                break;
            default:
                RewriteSnapshot(latest, data =>
                {
                    if (damage == "identity") data["workspaceId"] = Guid.NewGuid().ToString("D");
                    if (damage == "ancestry") data["ancestry"] = new JsonArray(Guid.NewGuid().ToString("D"));
                    if (damage == "accepted-evidence") data["acceptedImports"] = new JsonArray();
                    if (damage == "missing-person-field")
                    {
                        var row = data["people"]![0]!;
                        var person = JsonNode.Parse(row["value"]!.GetValue<string>())!.AsObject();
                        person.Remove("managerEmail");
                        row["value"] = person.ToJsonString();
                    }
                    if (damage == "null-record") data["people"]![0] = null;
                });
                break;
        }
        Assert.False(second.SyncNow());
        Assert.NotNull(second.Synchronization.Error);
        Assert.True(second.IsWorkspaceCurrent);
        Assert.Null(second.LocalSaveError);
        Assert.Equal(originalRevision, second.Synchronization.LocalRevision);
        Assert.Null(second.GetRelationships("report@example.com")!.Person.PreferredName);
        File.WriteAllBytes(latest, original);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("New delivered name", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal(first.Synchronization.LocalRevision, second.Synchronization.LocalRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_publication_resumes_after_restart_without_replaying_the_committed_mutation(bool afterPublish)
    {
        var publisher = new SnapshotPublisher { Interrupt = true, AfterPublish = afterPublish };
        Guid? committed;
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(filePublisher: publisher)))
        {
            Assert.True(app.OpenWorkspace(Local("first"), WorkspaceOpenMode.CreateLocalSqlite), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
            Assert.True(app.ConfigureSynchronizationFolder(Local("outgoing")), app.Synchronization.Error);
            committed = app.Synchronization.LocalRevision;
            Assert.False(app.SyncNow());
            Assert.True(app.Synchronization.HasUnpublishedChanges);
            Assert.Null(app.LocalSaveError);
            var basis = app.GetRelationships("report@example.com")!.Person;
            app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, "Still editable during sync failure");
            Assert.NotNull(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Still editable during sync failure"));
            Assert.NotNull(app.Synchronization.Error);
            committed = app.Synchronization.LocalRevision;
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Local("first"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(committed, reopened.Synchronization.LocalRevision);
        Assert.True(reopened.Synchronization.HasUnpublishedChanges);
        Assert.True(reopened.SyncNow(), reopened.Synchronization.Error);
        Assert.False(reopened.Synchronization.HasUnpublishedChanges);
        Assert.Equal(committed, reopened.Synchronization.LocalRevision);
        Assert.Equal("Still editable during sync failure", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Single(reopened.GetProfileTimeline("report@example.com")!.Changes);
    }

    [Theory]
    [InlineData("preferred")]
    [InlineData("profile")]
    [InlineData("note")]
    public async Task Incoming_adoption_cannot_overwrite_a_draft_retained_during_actual_validation(string kind)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        var originalRevision = second.Synchronization.LocalRevision;
        Assert.True(first.EditPreferredName("report@example.com", "Incoming"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        Assert.True(second.NavigatePerson("report@example.com"), second.Status);
        if (kind == "profile") Assert.True(second.BeginProfileEdit("report@example.com"), second.Status);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Local("second"), "workspace.sqlite")};Pooling=False");
        connection.Open();
        using var heldWriter = connection.BeginTransaction();
        using var validationStarted = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(Local("second")) { NotifyFilter = NotifyFilters.DirectoryName };
        watcher.Created += (_, args) => { if (args.Name?.StartsWith("snapshot-") == true) validationStarted.Set(); };
        watcher.EnableRaisingEvents = true;
        var synchronizing = Task.Run(second.SyncNow);
        NoteDraft? note = null;
        try
        {
            Assert.True(validationStarted.Wait(TimeSpan.FromSeconds(5)), "Actual snapshot validation never started.");
            if (kind == "preferred")
                second.RetainPersonTextDraft(second.GetRelationships("report@example.com")!.Person,
                    PersonTextField.PreferredName, "Retained private name");
            else if (kind == "profile")
                Assert.True(second.StageProfileText(PersonTextField.JobTitle, "Retained private title"), second.Status);
            else
            {
                note = second.BeginNoteDraft(NoteScope.Person, "report@example.com")!;
                second.RetainNoteDraft(note);
            }
        }
        finally { heldWriter.Commit(); }
        Assert.False(await synchronizing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(originalRevision, second.Synchronization.LocalRevision);
        Assert.Null(second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.False(second.CanSafelyClose);
        Assert.True(second.NavigatePerson("manager@example.com"), second.Status);
        if (kind == "preferred")
            Assert.Equal("Retained private name", second.GetPersonTextDraft("report@example.com", PersonTextField.PreferredName)!.Value);
        else if (kind == "profile")
        {
            Assert.True(second.NavigatePerson("report@example.com"), second.Status);
            Assert.Equal("Retained private title", second.ProfileEdit!.Draft.JobTitle);
        }
        else Assert.True(second.HasUnsavedNoteDraft(NoteScope.Person, note!.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_local_commit_during_export_stays_unpublished_and_does_not_change_the_exported_version(bool reviewedImport)
    {
        var publisher = new SnapshotPublisher { Hold = true };
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(filePublisher: publisher));
        Assert.True(app.OpenWorkspace(Local("first"), WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.ConfigureSynchronizationFolder(Local("outgoing")), app.Synchronization.Error);
        var exporting = app.Synchronization.LocalRevision;
        var sync = Task.Run(app.SyncNow);
        Assert.True(publisher.Entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            if (reviewedImport)
            {
                var preview = app.PreviewImport("""
                    {"schemaVersion":1,"batchId":"96000000-0000-4000-8000-000000000003",
                     "observedAt":"2026-11-02T17:00:00Z","people":[
                     {"email":"report@example.com","preferredName":"Committed during export"}]}
                    """);
                Assert.True(preview.CanApply, app.Status);
                Assert.True(app.ApplyImport(preview.Id), app.Status);
            }
            else Assert.True(app.EditPreferredName("report@example.com", "Committed during export"), app.Status);
            Assert.NotEqual(exporting, app.Synchronization.LocalRevision);
        }
        finally { publisher.Release.Set(); }
        Assert.True(await sync.WaitAsync(TimeSpan.FromSeconds(10)), app.Synchronization.Error);
        Assert.Equal(exporting, app.Synchronization.PublishedRevision);
        Assert.True(app.Synchronization.HasUnpublishedChanges);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Null(second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Deliver("outgoing", "incoming");
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("Committed during export", second.GetRelationships("report@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Reordered_duplicates_and_stale_delivery_cannot_roll_back_a_clean_newer_local_version()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        var old = Assert.Single(Directory.GetFiles(Local("outgoing"), "*.oesnapshot"));
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(first.EditPreferredName("report@example.com", "Latest"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        File.Copy(old, Path.Combine(Local("incoming"), "provider conflict duplicate.oesnapshot"));
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        var newest = second.Synchronization.LocalRevision;
        foreach (var file in Directory.GetFiles(Local("incoming"), "*.oesnapshot")) File.Delete(file);
        File.Copy(old, Path.Combine(Local("incoming"), "late delivery.oesnapshot"));
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal(newest, second.Synchronization.LocalRevision);
        Assert.Equal("Latest", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.False(second.Synchronization.HasUnpublishedChanges);
    }

    [Fact]
    public void Genuine_divergence_preserves_both_complete_versions_and_pauses_new_edits_across_restart()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using (var second = Create("second"))
        {
            Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
            Assert.True(second.SyncNow(), second.Synchronization.Error);
            Assert.True(second.EditPreferredName("report@example.com", "Second private version"), second.Status);
            Assert.True(first.EditPreferredName("report@example.com", "First private version"), first.Status);
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Deliver("outgoing", "incoming");
            Assert.False(second.SyncNow());
            Assert.True(second.Synchronization.Diverged);
            Assert.False(second.Compatibility.SupportsOrdinaryEdits);
            Assert.Equal("Second private version", second.GetRelationships("report@example.com")!.Person.PreferredName);
            Assert.True(second.NavigatePerson("manager@example.com"), second.Status);
            Assert.False(second.EditPreferredName("report@example.com", "Must not overwrite"));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(Local("second"), "snapshot-conflicts"), "*.oesnapshot").Length);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.True(reopened.Synchronization.Diverged);
        Assert.True(reopened.NavigatePerson("report@example.com"), reopened.Status);
        Assert.Equal("Second private version", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.False(reopened.SyncNow());
    }

    [Fact]
    public void Deliberate_complete_version_selection_retains_the_other_version_for_recovery_after_restart()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        Guid localVersion, incomingVersion;
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        File.WriteAllBytes(Local("conflict-photo.png"), image);
        HistoricalTeamContext localCapture;
        using (var second = Create("second"))
        {
            Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
            Assert.True(second.SyncNow(), second.Synchronization.Error);
            Assert.True(second.EditPreferredName("report@example.com", "Second complete version"), second.Status);
            Assert.NotNull(second.CreatePersonNote("report@example.com", "Local only", "Preserve all local writing"));
            Assert.True(second.EditPhoto("manager@example.com", Local("conflict-photo.png")), second.Status);
            Assert.NotNull(second.CreateTeamNote("manager@example.com", "Local history", "Frozen unchosen writing"));
            Assert.True(second.EditPersonManager("report@example.com", null), second.Status);
            localCapture = Assert.Single(second.GetHistoricalTeamContexts("report@example.com"));
            localVersion = second.Synchronization.LocalRevision!.Value;
            Assert.True(first.EditPreferredName("report@example.com", "First complete version"), first.Status);
            Assert.NotNull(first.CreateTeamNote("manager@example.com", "Incoming only", "Preserve all incoming writing"));
            incomingVersion = first.Synchronization.LocalRevision!.Value;
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Deliver("outgoing", "incoming");
            Assert.False(second.SyncNow());
            var review = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
            Assert.Equal(2, review.Versions.Count);
            Assert.True(second.NavigatePerson("manager@example.com"), second.Status);
            var navigation = second.PeopleNavigation;
            Assert.True(second.ChooseWorkspaceVersion(review.Id, incomingVersion), second.Synchronization.Error);
            Assert.Equal(navigation, second.PeopleNavigation);
            Assert.False(second.Synchronization.Diverged);
            Assert.True(second.CanEditLocalWorkspace);
            Assert.Equal("First complete version", second.GetRelationships("report@example.com")!.Person.PreferredName);
            Assert.Empty(second.GetPersonNotes("report@example.com"));
            Assert.Null(second.GetPhoto("manager@example.com"));
            Assert.Empty(second.GetHistoricalTeamContexts("report@example.com"));
            Assert.Equal("Preserve all incoming writing", Assert.Single(second.GetTeamNotes("manager@example.com")).Markdown);
            Assert.True(second.SyncNow(), second.Synchronization.Error);
            for (var index = 0; index < 3; index++)
            {
                Assert.True(second.EditPersonText("report@example.com", PersonTextField.OfficeNumber, "Routine after resolution " + index), second.Status);
                Assert.True(second.SyncNow(), second.Synchronization.Error);
            }
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        var recovery = Assert.IsType<WorkspaceVersionReview>(reopened.GetWorkspaceVersionReview());
        Assert.Contains(recovery.Versions, item => item.Revision == localVersion && item.Retained);
        Assert.Contains(recovery.Versions, item => item.Revision == incomingVersion && item.Retained);
        Assert.True(reopened.ChooseWorkspaceVersion(recovery.Id, localVersion), reopened.Synchronization.Error);
        Assert.Equal("Second complete version", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Equal("Preserve all local writing", Assert.Single(reopened.GetPersonNotes("report@example.com")).Markdown);
        Assert.Equal("Frozen unchosen writing", Assert.Single(reopened.GetTeamNotes("manager@example.com")).Markdown);
        Assert.Equal(image, reopened.GetPhoto("manager@example.com")!.Bytes.ToArray());
        var restoredCapture = Assert.Single(reopened.GetHistoricalTeamContexts("report@example.com"));
        Assert.Equal(localCapture.Id, restoredCapture.Id);
        Assert.Equal(localCapture.TeamNotes, restoredCapture.TeamNotes);
        Assert.Equal(localCapture.DisplayProvenance, restoredCapture.DisplayProvenance);
        Assert.True(reopened.SyncNow(), reopened.Synchronization.Error);
        Deliver("incoming", "outgoing");
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Assert.Equal("Second complete version", first.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Equal("Preserve all local writing", Assert.Single(first.GetPersonNotes("report@example.com")).Markdown);
        Assert.Equal(image, first.GetPhoto("manager@example.com")!.Bytes.ToArray());
        Assert.Equal(localCapture.TeamNotes, Assert.Single(first.GetHistoricalTeamContexts("report@example.com")).TeamNotes);
    }

    [Fact]
    public void Arrival_during_the_real_selection_commit_is_retained_and_requires_another_deliberate_choice()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var observer = new SelectionArrivalObserver();
        using var second = Create("second", observer);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.EditPreferredName("report@example.com", "Chosen local complete version"), second.Status);
        var local = second.Synchronization.LocalRevision!.Value;
        Assert.True(first.EditPreferredName("report@example.com", "Initially shown incoming version"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        Assert.False(second.SyncNow());
        var review = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
        Assert.True(first.EditPreferredName("report@example.com", "Unseen later writing"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        var arriving = first.Synchronization.LocalRevision!.Value;
        observer.Arrive = () => Deliver("outgoing", "incoming");
        Assert.False(second.ChooseWorkspaceVersion(review.Id, local));
        Assert.True(observer.ObservedCommit);
        Assert.True(second.Synchronization.Diverged);
        Assert.False(second.CanEditLocalWorkspace);
        Assert.True(second.NavigatePerson("manager@example.com"), second.Status);
        var refreshed = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
        Assert.Contains(refreshed.Versions, item => item.Revision == arriving && item.Retained);
        Assert.False(second.ChooseWorkspaceVersion(review.Id, local));
        Assert.True(second.ChooseWorkspaceVersion(refreshed.Id, arriving), second.Synchronization.Error);
        Assert.Equal("Unseen later writing", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
    }

    private sealed class SelectionArrivalObserver : IWorkspaceFileObserver
    {
        public Action? Arrive;
        public Action? AfterCommit;
        public bool ObservedCommit;
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name == "snapshot.adopt-commit" && phase.Kind == WorkspaceStoragePhaseKind.End)
            {
                var action = AfterCommit;
                AfterCommit = null;
                action?.Invoke();
            }
            if (phase.Name != "snapshot.adopt-commit" || phase.Kind != WorkspaceStoragePhaseKind.Begin || Arrive is not { } arrive)
                return;
            Arrive = null;
            ObservedCommit = true;
            arrive();
        }
    }

    [Fact]
    public void Selection_remains_read_only_across_restart_when_postcommit_arrival_validation_is_unavailable()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var observer = new SelectionArrivalObserver();
        using var second = Create("second", observer);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.EditPreferredName("report@example.com", "Selected local version"), second.Status);
        var local = second.Synchronization.LocalRevision!.Value;
        Assert.True(first.EditPreferredName("report@example.com", "Other complete version"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        Assert.False(second.SyncNow());
        var review = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
        FileStream? held = null;
        observer.AfterCommit = () => held = new FileStream(
            Directory.GetFiles(Local("incoming"), "*.oesnapshot").Single(path =>
                path.Contains(first.Synchronization.LocalRevision!.Value.ToString("D"))),
            FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Assert.False(second.ChooseWorkspaceVersion(review.Id, local));
            Assert.NotNull(held);
            Assert.True(second.Synchronization.Diverged);
            Assert.False(second.CanEditLocalWorkspace);
            Assert.Equal("Selected local version", second.GetRelationships("report@example.com")!.Person.PreferredName);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.True(reopened.Synchronization.Diverged);
            Assert.False(reopened.CanEditLocalWorkspace);
            Assert.Equal("Selected local version", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
        }
        finally { held?.Dispose(); }
        var current = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
        Assert.True(second.ChooseWorkspaceVersion(current.Id, second.Synchronization.LocalRevision!.Value), second.Synchronization.Error);
        Assert.True(second.CanEditLocalWorkspace);
    }

    [Fact]
    public void Routine_retention_keeps_two_complete_versions_without_pruning_resources_history_or_provider_files()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        File.WriteAllBytes(Local("retained.png"), image);
        Assert.True(first.EditPhoto("manager@example.com", Local("retained.png")), first.Status);
        var note = first.CreateTeamNote("manager@example.com", "Captured", "# Original frozen writing")!;
        Assert.True(first.EditPersonManager("report@example.com", null), first.Status);
        var capture = Assert.Single(first.GetHistoricalTeamContexts("report@example.com"));
        Assert.True(first.EditTeamNote(note.Id, "Current", "Current writing"), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        var original = Assert.Single(Directory.GetFiles(Local("outgoing"), "*.oesnapshot"));
        var providerCopy = Path.Combine(Local("outgoing"), "provider conflict copy.oesnapshot");
        File.Copy(original, providerCopy);
        var providerBytes = File.ReadAllBytes(providerCopy);
        File.WriteAllText(Path.Combine(Local("outgoing"), "provider notes.txt"), "Do not remove unknown data");
        File.WriteAllText(Path.Combine(Local("outgoing"), ".unknown.pending"), "Unknown interrupted provider bytes");
        Guid? previous = null;
        for (var index = 0; index < 5; index++)
        {
            previous = first.Synchronization.LocalRevision;
            Assert.True(first.EditPreferredName("report@example.com", "Routine " + index), first.Status);
            Assert.True(first.SyncNow(), first.Synchronization.Error);
        }
        var owned = Directory.GetFiles(Local("outgoing"), "*.oesnapshot").Where(file => file != providerCopy).ToArray();
        Assert.Equal(2, owned.Length);
        Assert.Contains(owned, file => file.Contains(previous!.Value.ToString("D")));
        Assert.Contains(owned, file => file.Contains(first.Synchronization.LocalRevision!.Value.ToString("D")));
        Assert.Equal(providerBytes, File.ReadAllBytes(providerCopy));
        Assert.Equal("Do not remove unknown data", File.ReadAllText(Path.Combine(Local("outgoing"), "provider notes.txt")));
        Assert.Equal("Unknown interrupted provider bytes", File.ReadAllText(Path.Combine(Local("outgoing"), ".unknown.pending")));
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Routine 4", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Equal(image, reopened.GetPhoto("manager@example.com")!.Bytes.ToArray());
        var actual = Assert.Single(reopened.GetHistoricalTeamContexts("report@example.com"));
        Assert.Equal(capture.Id, actual.Id);
        Assert.Equal(capture.TeamNotes, actual.TeamNotes);
        Assert.Equal(capture.DisplayProvenance, actual.DisplayProvenance);
        Assert.Equal(first.GetProfileTimeline("report@example.com")!.DisplayChanges,
            reopened.GetProfileTimeline("report@example.com")!.DisplayChanges);
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(Initial).Changes));
    }

    [Fact]
    public void Only_an_explicit_fresh_deletion_removes_retained_recovery_not_current_data_or_provider_copies()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.EditPreferredName("report@example.com", "Kept local writing"), second.Status);
        var local = second.Synchronization.LocalRevision!.Value;
        Assert.True(first.EditPreferredName("report@example.com", "Explicitly deleted recovery"), first.Status);
        var incoming = first.Synchronization.LocalRevision!.Value;
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        Assert.False(second.SyncNow());
        var conflict = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
        Assert.False(second.DeleteRetainedWorkspaceVersion(conflict.Id, incoming));
        Assert.True(second.ChooseWorkspaceVersion(conflict.Id, local), second.Synchronization.Error);
        Assert.False(second.DeleteRetainedWorkspaceVersion(conflict.Id, incoming));
        var recovery = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
        var current = second.Synchronization.LocalRevision;
        Assert.False(second.DeleteRetainedWorkspaceVersion(recovery.Id, current!.Value));
        Assert.True(second.DeleteRetainedWorkspaceVersion(recovery.Id, incoming), second.Synchronization.Error);
        var after = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
        Assert.DoesNotContain(after.Versions, item => item.Revision == incoming && item.Retained);
        Assert.Contains(after.Versions, item => item.Revision == local && item.Retained);
        Assert.Equal(current, second.Synchronization.LocalRevision);
        Assert.Equal("Kept local writing", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
    }

    [Theory]
    [InlineData(NoteScope.Person, false)]
    [InlineData(NoteScope.Team, false)]
    [InlineData(NoteScope.Person, true)]
    [InlineData(NoteScope.Team, true)]
    public async Task Whole_version_choice_cannot_bypass_an_accepted_note_handoff_or_unknown_outcome_with_newer_writing(
        NoteScope scope, bool loseAcknowledgment)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        var owner = scope == NoteScope.Team ? "manager@example.com" : "report@example.com";
        var note = first.SaveNoteDraft(first.BeginNoteDraft(scope, owner)!, "Exact owner", "Original note")!;
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var proxy = DispatchProxy.Create<IApplicationWorkflow, SnapshotSaveBoundary>();
        var boundary = (SnapshotSaveBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var second = new WorkspaceViewModel(proxy);
        Assert.True(second.OpenWorkspace(Local("second"), WorkspaceOpenMode.CreateLocalSqlite), second.Status);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.NavigatePerson(owner), second.Status);
        Assert.True(second.OpenOwnedNote(scope, note.Id, editing: true), second.Status);
        using var committed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        boundary.AfterSave = () =>
        {
            committed.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled selection handoff expired.");
            if (loseAcknowledgment) throw new IOException("Controlled loss of actual committed note acknowledgment.");
        };
        second.UpdateNoteDraft(scope, note.Id, note.Title, "Actual committed note before acknowledgment");
        try
        {
            Assert.True(committed.Wait(TimeSpan.FromSeconds(5)));
            second.UpdateNoteDraft(scope, note.Id, note.Title, "Newer exact owner writing");
            Assert.True(first.EditPreferredName("report@example.com", "Divergent other computer"), first.Status);
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Deliver("outgoing", "incoming");
            Assert.False(second.SyncNow());
            var review = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
            Assert.False(second.ChooseWorkspaceVersion(review.Id, first.Synchronization.LocalRevision!.Value));
            var draft = Assert.IsType<RetainedNoteDraft>(second.GetRetainedNoteDraft(scope, note.Id));
            Assert.True(draft.IsSaving);
            Assert.Equal(owner, draft.Basis.OwnerEmail);
            Assert.Equal("Newer exact owner writing", draft.Markdown);
            Assert.True(second.NavigatePerson(scope == NoteScope.Team ? "report@example.com" : "manager@example.com"), second.Status);
        }
        finally { release.Set(); }
        Assert.False(await second.FlushNoteDraftsAsync());
        var currentReview = Assert.IsType<WorkspaceVersionReview>(second.GetWorkspaceVersionReview());
        Assert.False(second.ChooseWorkspaceVersion(currentReview.Id, first.Synchronization.LocalRevision!.Value));
        Assert.Equal("Newer exact owner writing", second.GetRetainedNoteDraft(scope, note.Id)!.Markdown);
        Assert.False(second.CanSafelyClose);
    }

    [Fact]
    public void Adoption_preserves_the_previous_known_good_complete_version_even_if_the_provider_removed_its_export()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        var previous = second.Synchronization.LocalRevision!.Value;
        Assert.True(first.EditPreferredName("report@example.com", "Only delivered continuation"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        foreach (var file in Directory.GetFiles(Local("incoming"), "*.oesnapshot")) File.Delete(file);
        var latest = Directory.GetFiles(Local("outgoing"), "*.oesnapshot").Single(file =>
            file.Contains(first.Synchronization.LocalRevision!.Value.ToString("D")));
        File.Copy(latest, Path.Combine(Local("incoming"), Path.GetFileName(latest)));
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        var retained = Directory.GetFiles(Local("incoming"), "*.oesnapshot");
        Assert.Equal(2, retained.Length);
        Assert.Contains(retained, file => file.Contains(previous.ToString("D")));
        Assert.Equal("Only delivered continuation", second.GetRelationships("report@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Fresh_join_intent_does_not_authorize_overwriting_a_later_commit_from_another_local_client()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        var seedIdentity = second.Synchronization.WorkspaceId;
        using (var independent = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(independent.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), independent.Status);
            Assert.True(independent.ApplyImport(independent.PreviewImport(Initial).Id), independent.Status);
            Assert.True(independent.EditPreferredName("report@example.com", "New independent local commit"), independent.Status);
        }
        Assert.False(second.SyncNow());
        Assert.Equal(seedIdentity, second.Synchronization.WorkspaceId);
        Assert.Equal("New independent local commit", second.GetRelationships("report@example.com")!.Person.PreferredName);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(seedIdentity, reopened.Synchronization.WorkspaceId);
        Assert.Equal("New independent local commit", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Actual_folder_unavailability_does_not_pause_local_writing_and_latest_publication_resumes()
    {
        using var app = Create("first");
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.ConfigureSynchronizationFolder(Local("outgoing")), app.Synchronization.Error);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Directory.Move(Local("outgoing"), Local("provider-unavailable"));
        Assert.False(app.SyncNow());
        Assert.NotNull(app.Synchronization.Error);
        Assert.Equal(WorkspaceSynchronizationPhase.Idle, app.Synchronization.Phase);
        var basis = app.GetRelationships("report@example.com")!.Person;
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, "Durable while provider is unavailable");
        Assert.NotNull(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Durable while provider is unavailable"));
        var note = app.CreatePersonNote("report@example.com", "Independent local note", "Writing does not require the provider")!;
        Assert.NotNull(app.Synchronization.Error);
        Assert.Null(app.LocalSaveError);
        Assert.True(app.IsWorkspaceCurrent);
        Assert.True(app.Synchronization.HasUnpublishedChanges);
        var committed = app.Synchronization.LocalRevision;
        using (var reading = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(reading.OpenWorkspace(Local("first"), WorkspaceOpenMode.ExistingOnly), reading.Status);
            Assert.Equal("Durable while provider is unavailable", reading.GetRelationships("report@example.com")!.Person.PreferredName);
            Assert.Equal(note, Assert.Single(reading.GetPersonNotes("report@example.com")));
            Assert.True(reading.Synchronization.HasUnpublishedChanges);
        }
        Directory.Move(Local("provider-unavailable"), Local("outgoing"));
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Null(app.Synchronization.Error);
        Assert.Equal(committed, app.Synchronization.PublishedRevision);
        Assert.False(app.Synchronization.HasUnpublishedChanges);
    }

    [Theory]
    [InlineData(NoteScope.Person, false)]
    [InlineData(NoteScope.Team, false)]
    [InlineData(NoteScope.Person, true)]
    [InlineData(NoteScope.Team, true)]
    public async Task Published_note_commit_with_an_unacknowledged_newer_generation_still_blocks_incoming_adoption(NoteScope scope, bool automatic)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        var owner = scope == NoteScope.Person ? "report@example.com" : "manager@example.com";
        var note = first.SaveNoteDraft(first.BeginNoteDraft(scope, owner)!, "Private writing", "Original committed writing")!;
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var proxy = DispatchProxy.Create<IApplicationWorkflow, SnapshotSaveBoundary>();
        var boundary = (SnapshotSaveBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        var clock = new UnadvancedSaveClock();
        using var second = new WorkspaceViewModel(proxy, timeProvider: clock);
        Assert.True(second.OpenWorkspace(Local("second"), WorkspaceOpenMode.CreateLocalSqlite), second.Status);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.NavigatePerson(owner), second.Status);
        Assert.True(second.OpenOwnedNote(scope, note.Id, editing: true), second.Status);
        using var committed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        boundary.AfterSave = () =>
        {
            committed.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled accepted-return wait expired.");
        };
        second.UpdateNoteDraft(scope, note.Id, note.Title, "Submitted committed writing");
        try
        {
            Assert.True(committed.Wait(TimeSpan.FromSeconds(5)), "Real note commit never reached its accepted-return boundary.");
            second.UpdateNoteDraft(scope, note.Id, note.Title, "Newer retained writing before acknowledgment");
            using (var independent = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(independent.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), independent.Status);
                Assert.True(independent.SyncNow(), independent.Synchronization.Error);
            }
            Deliver("incoming", "outgoing");
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Assert.NotNull(first.SaveNoteDraft(first.BeginNoteDraft(scope, owner, note.Id)!, note.Title, "Incoming subsequent version"));
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Deliver("outgoing", "incoming");
            if (automatic)
            {
                clock.Advance(TimeSpan.FromSeconds(60));
                await AwaitSnapshot(() => second.Synchronization.Error is not null && !second.IsSynchronizing);
            }
            else Assert.False(second.SyncNow());
            Assert.Equal("Submitted committed writing", second.BeginNoteDraft(scope, owner, note.Id)!.Markdown);
            Assert.Equal("Newer retained writing before acknowledgment",
                second.GetRetainedNoteDraft(scope, note.Id)!.Markdown);
        }
        finally { boundary.AfterSave = null; release.Set(); }
        Assert.True(await second.FlushNoteDraftsAsync(), second.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Newer retained writing before acknowledgment", reopened.BeginNoteDraft(scope, owner, note.Id)!.Markdown);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("person-note")]
    [InlineData("team-note")]
    public async Task A_real_reader_held_adoption_commit_cannot_block_browsing_or_discard_a_later_draft(string kind)
    {
        using var installing = new ManualResetEventSlim();
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        var scope = kind == "team-note" ? NoteScope.Team : NoteScope.Person;
        var owner = kind == "team-note" ? "manager@example.com" : "report@example.com";
        var original = first.SaveNoteDraft(first.BeginNoteDraft(scope, owner)!, "Exact owner note", "Original committed note")!;
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second", new SnapshotPhaseObserver(installing));
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.NavigatePerson(owner), second.Status);
        if (kind == "profile") Assert.True(second.BeginProfileEdit(owner), second.Status);
        else Assert.True(second.OpenOwnedNote(scope, original.Id, editing: true), second.Status);
        Assert.True(first.EditPreferredName("report@example.com", "Incoming before later draft"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Local("second"), "workspace.sqlite")};Pooling=False");
        connection.Open();
        using var heldReader = connection.BeginTransaction(deferred: true);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = heldReader;
            read.CommandText = "PRAGMA schema_version;";
            read.ExecuteScalar();
        }
        installing.Reset();
        var sync = Task.Run(second.SyncNow);
        var stillInstalling = false;
        NoteDraft? retainedBasis = null;
        try
        {
            Assert.True(installing.Wait(TimeSpan.FromSeconds(5)), "Actual adoption never reached its sealed local commit boundary.");
            await Task.Run(() =>
            {
                if (kind == "profile")
                    Assert.True(second.StageProfileText(PersonTextField.JobTitle, "Later private draft"), second.Status);
                else
                {
                    second.UpdateNoteDraft(scope, original.Id, original.Title, "Later exact owner writing");
                    retainedBasis = second.GetRetainedNoteDraft(scope, original.Id)!.Basis;
                    Assert.Equal(owner, retainedBasis.OwnerEmail);
                    Assert.Equal("Original committed note", retainedBasis.Markdown);
                }
                Assert.True(second.NavigatePerson(owner == "report@example.com" ? "manager@example.com" : "report@example.com"), second.Status);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            stillInstalling = !sync.IsCompleted && second.Synchronization.Error is null;
            if (retainedBasis is not null)
                Assert.Same(retainedBasis, second.GetRetainedNoteDraft(scope, original.Id)!.Basis);
        }
        finally { heldReader.Rollback(); }
        var synchronized = await sync.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(stillInstalling, "Browsing/draft admission waited for the reader-held adoption to finish or fail.");
        Assert.True(synchronized, second.Synchronization.Error);
        Assert.True(second.NavigatePerson(owner), second.Status);
        Assert.Equal("Incoming before later draft", second.GetRelationships("report@example.com")!.Person.PreferredName);
        if (kind == "profile")
        {
            Assert.Equal("Later private draft", second.ProfileEdit!.Draft.JobTitle);
            Assert.False(second.CanSafelyClose);
        }
        else
        {
            Assert.True(await second.FlushNoteDraftsAsync(), second.Status);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Equal("Later exact owner writing", reopened.BeginNoteDraft(scope, owner, original.Id)!.Markdown);
            Assert.True(second.SyncNow(), second.Synchronization.Error);
            Deliver("incoming", "outgoing");
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Assert.Equal("Later exact owner writing", first.BeginNoteDraft(scope, owner, original.Id)!.Markdown);
        }
    }

    private sealed class SnapshotPhaseObserver(ManualResetEventSlim committing) : IWorkspaceFileObserver
    {
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name == "snapshot.adopt-commit" && phase.Kind == WorkspaceStoragePhaseKind.Begin) committing.Set();
        }
    }

    [Theory]
    [InlineData(NoteScope.Person, false)]
    [InlineData(NoteScope.Person, true)]
    [InlineData(NoteScope.Team, false)]
    [InlineData(NoteScope.Team, true)]
    public async Task Unknown_note_reconciliation_does_not_block_browsing_behind_snapshot_IO_or_lose_the_latest_generation(
        NoteScope scope, bool newerGeneration)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        var owner = scope == NoteScope.Person ? "report@example.com" : "manager@example.com";
        var note = first.SaveNoteDraft(first.BeginNoteDraft(scope, owner)!, "Exact recovery owner", "Original committed writing")!;
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var readEntered = new ManualResetEventSlim();
        var observer = new SnapshotReadObserver(readEntered);
        var proxy = DispatchProxy.Create<IApplicationWorkflow, SnapshotSaveBoundary>();
        var boundary = (SnapshotSaveBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        using var second = new WorkspaceViewModel(proxy);
        Assert.True(second.OpenWorkspace(Local("second"), WorkspaceOpenMode.CreateLocalSqlite), second.Status);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.NavigatePerson(owner), second.Status);
        Assert.True(second.OpenOwnedNote(scope, note.Id, editing: true), second.Status);
        using var committed = new ManualResetEventSlim();
        using var loseReturn = new ManualResetEventSlim();
        boundary.AfterSave = () =>
        {
            committed.Set();
            if (!loseReturn.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled accepted return was not released.");
            boundary.AfterSave = null;
            throw new IOException("Actual committed note return was lost.");
        };
        second.UpdateNoteDraft(scope, note.Id, note.Title, "Committed before lost acknowledgment");
        Assert.True(committed.Wait(TimeSpan.FromSeconds(5)));
        if (newerGeneration) second.UpdateNoteDraft(scope, note.Id, note.Title, "Latest retained owner generation");
        loseReturn.Set();
        Assert.True(SpinWait.SpinUntil(() => second.GetRetainedNoteDraft(scope, note.Id) is { IsSaving: false } &&
            second.LocalSave.HasUnresolvedOutcome, TimeSpan.FromSeconds(5)));
        using (var independent = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(independent.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), independent.Status);
            Assert.True(independent.SyncNow(), independent.Synchronization.Error);
        }
        Deliver("incoming", "outgoing");
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Assert.NotNull(first.SaveNoteDraft(first.BeginNoteDraft(scope, owner, note.Id)!, note.Title, "Incoming child writing"));
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");

        using var reconciledCore = new ManualResetEventSlim();
        using var continueReconcile = new ManualResetEventSlim();
        using var discarding = new ManualResetEventSlim();
        boundary.AfterReconcile = () =>
        {
            reconciledCore.Set();
            if (!continueReconcile.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled reconciliation was not released.");
        };
        boundary.BeforeDiscard = () => discarding.Set();
        var recovery = Task.Run(second.ReconcileLocalSaves);
        Assert.True(reconciledCore.Wait(TimeSpan.FromSeconds(5)));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Local("second"), "workspace.sqlite")};Pooling=False");
        connection.Open();
        using var exclusive = connection.CreateCommand();
        exclusive.CommandText = "BEGIN EXCLUSIVE;";
        exclusive.ExecuteNonQuery();
        observer.Armed = true;
        Task<bool>? sync = null;
        var browsedWhileHeld = false;
        try
        {
            sync = Task.Run(second.SyncNow);
            Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)), "Actual snapshot local read never entered.");
            boundary.AfterReconcile = null;
            continueReconcile.Set();
            Assert.True(discarding.Wait(TimeSpan.FromSeconds(5)), "Reconciliation never reached the actual core draft acknowledgment.");
            Assert.True(await Task.Run(() => second.NavigatePerson(owner == "report@example.com"
                ? "manager@example.com" : "report@example.com")).WaitAsync(TimeSpan.FromSeconds(5)), second.Status);
            browsedWhileHeld = observer.Active && !sync.IsCompleted &&
                second.Synchronization.Phase == WorkspaceSynchronizationPhase.Checking;
        }
        finally
        {
            continueReconcile.Set();
            exclusive.CommandText = "ROLLBACK;";
            exclusive.ExecuteNonQuery();
            if (sync is not null) await sync.WaitAsync(TimeSpan.FromSeconds(10));
            await recovery.WaitAsync(TimeSpan.FromSeconds(10));
            observer.Armed = false;
        }
        Assert.True(browsedWhileHeld, "Draft acknowledgment held the view-model gate behind actual snapshot storage I/O.");
        Assert.True(await recovery, second.Status);
        Assert.False(second.LocalSave.HasUnresolvedOutcome);
        if (newerGeneration)
        {
            var retained = second.GetRetainedNoteDraft(scope, note.Id)!;
            Assert.Equal(owner, retained.Basis.OwnerEmail);
            Assert.Equal("Committed before lost acknowledgment", retained.Basis.Markdown);
            Assert.Equal("Latest retained owner generation", retained.Markdown);
            Assert.False(second.SyncNow());
            Assert.True(await second.RetryNoteDraftAsync(scope, note.Id), second.Status);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Equal("Latest retained owner generation", reopened.BeginNoteDraft(scope, owner, note.Id)!.Markdown);
            Assert.False(second.SyncNow());
            Assert.True(second.Synchronization.Diverged);
        }
        else
        {
            Assert.Null(second.GetRetainedNoteDraft(scope, note.Id));
            Assert.True(second.SyncNow(), second.Synchronization.Error);
            Assert.Equal("Incoming child writing", second.BeginNoteDraft(scope, owner, note.Id)!.Markdown);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discarding_a_note_buffer_does_not_authorize_adoption_while_its_actual_acknowledgment_is_unknown(bool automatic)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        var note = first.SaveNoteDraft(first.BeginNoteDraft(NoteScope.Person, "report@example.com")!, "Actual unknown return", "Original")!;
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var proxy = DispatchProxy.Create<IApplicationWorkflow, SnapshotSaveBoundary>();
        var boundary = (SnapshotSaveBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        var clock = new SnapshotClock();
        using var second = new WorkspaceViewModel(proxy, timeProvider: clock);
        Assert.True(second.OpenWorkspace(Local("second"), WorkspaceOpenMode.CreateLocalSqlite), second.Status);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.NavigatePerson("report@example.com"), second.Status);
        Assert.True(second.OpenOwnedNote(NoteScope.Person, note.Id, editing: true), second.Status);
        boundary.AfterSave = () =>
        {
            boundary.AfterSave = null;
            throw new IOException("Actual committed note return was lost.");
        };
        second.UpdateNoteDraft(NoteScope.Person, note.Id, note.Title, "Committed unknown writing");
        Assert.False(await second.FlushNoteDraftsAsync());
        Assert.True(SpinWait.SpinUntil(() => second.GetRetainedNoteDraft(NoteScope.Person, note.Id) is { IsSaving: false } &&
            second.LocalSave.HasUnresolvedOutcome, TimeSpan.FromSeconds(5)));
        second.DiscardUnsavedChanges(note.Id);
        Assert.Null(second.GetRetainedNoteDraft(NoteScope.Person, note.Id));
        Assert.True(second.LocalSave.HasUnresolvedOutcome);
        using (var independent = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(independent.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), independent.Status);
            Assert.True(independent.SyncNow(), independent.Synchronization.Error);
        }
        Deliver("incoming", "outgoing");
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Assert.True(first.EditPreferredName("report@example.com", "Incoming after unknown return"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        if (automatic)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            await AwaitSnapshot(() => second.Synchronization.Error is not null && !second.IsSynchronizing);
        }
        else Assert.False(second.SyncNow());
        Assert.True(second.LocalSave.HasUnresolvedOutcome);
        Assert.True(second.ReconcileLocalSaves(), second.Status);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("Incoming after unknown return", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Equal("Committed unknown writing", second.BeginNoteDraft(NoteScope.Person, "report@example.com", note.Id)!.Markdown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_already_admitted_sync_cannot_adopt_after_a_later_lost_note_return_and_buffer_discard(bool automatic)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        var note = first.SaveNoteDraft(first.BeginNoteDraft(NoteScope.Person, "report@example.com")!, "Exact owner", "Original")!;
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var proxy = DispatchProxy.Create<IApplicationWorkflow, SnapshotSaveBoundary>();
        var boundary = (SnapshotSaveBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        var clock = new UnadvancedSaveClock();
        using var second = new WorkspaceViewModel(proxy, timeProvider: clock);
        Assert.True(second.OpenWorkspace(Local("second"), WorkspaceOpenMode.CreateLocalSqlite), second.Status);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.NavigatePerson("report@example.com"), second.Status);
        Assert.True(second.OpenOwnedNote(NoteScope.Person, note.Id, editing: true), second.Status);
        using var admitted = new ManualResetEventSlim();
        using var continueSync = new ManualResetEventSlim();
        using var favoriteCommitted = new ManualResetEventSlim();
        using var releaseFavorite = new ManualResetEventSlim();
        boundary.BeforeSync = () =>
        {
            admitted.Set();
            if (!continueSync.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Admitted synchronization was not released.");
        };
        Task<bool>? sync = null;
        Task<bool>? favorite = null;
        if (automatic) clock.Advance(TimeSpan.FromSeconds(60));
        else sync = second.SynchronizeAsync();
        try
        {
            Assert.True(admitted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(second.BeginProfileEdit("manager@example.com"), second.Status);
            Assert.True(second.StageProfileText(PersonTextField.JobTitle, "Independent profile draft"), second.Status);
            var fieldBasis = second.GetRelationships("report@example.com")!.Person;
            second.RetainPersonTextDraft(fieldBasis, PersonTextField.PreferredName, "Independent field draft");
            boundary.AfterFavorite = () =>
            {
                favoriteCommitted.Set();
                if (!releaseFavorite.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Independent successful return was not released.");
            };
            favorite = second.RunLocalSaveAsync(() => second.EditFavorite("report@example.com", true));
            Assert.True(favoriteCommitted.Wait(TimeSpan.FromSeconds(5)));
            boundary.AfterSave = () => throw new IOException("The later actual committed note return was lost.");
            second.UpdateNoteDraft(NoteScope.Person, note.Id, note.Title, "Committed after synchronization admission");
            await AwaitSnapshot(() => second.GetRetainedNoteDraft(NoteScope.Person, note.Id) is { IsSaving: false } &&
                second.LocalSave.Phase == LocalSavePhase.Indeterminate);
            Assert.False(second.ReconcileLocalSaves());
            second.CancelProfileEdit();
            second.DiscardPersonTextDraft(fieldBasis, PersonTextField.PreferredName);
            releaseFavorite.Set();
            Assert.True(await favorite.WaitAsync(TimeSpan.FromSeconds(5)), second.Status);
            Assert.Equal(LocalSavePhase.Indeterminate, second.LocalSave.Phase);
            using (var independent = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(independent.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), independent.Status);
                Assert.True(independent.SyncNow(), independent.Synchronization.Error);
            }
            Deliver("incoming", "outgoing");
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Assert.Equal(second.Synchronization.LocalRevision, first.Synchronization.LocalRevision);
            Assert.True(first.EditPreferredName("report@example.com", "Incoming after later unknown outcome"), first.Status);
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Deliver("outgoing", "incoming");
            second.DiscardUnsavedChanges(note.Id);
            Assert.Null(second.GetRetainedNoteDraft(NoteScope.Person, note.Id));
            Assert.True(second.LocalSave.HasUnresolvedOutcome);
        }
        finally
        {
            boundary.BeforeSync = null;
            boundary.AfterSave = null;
            boundary.AfterFavorite = null;
            releaseFavorite.Set();
            continueSync.Set();
            if (favorite is not null) await favorite.WaitAsync(TimeSpan.FromSeconds(10));
            if (sync is not null) await sync.WaitAsync(TimeSpan.FromSeconds(10));
            await AwaitSnapshot(() => !second.IsSynchronizing);
        }
        if (sync is not null) Assert.False(await sync.WaitAsync(TimeSpan.FromSeconds(10)));
        await AwaitSnapshot(() => !second.IsSynchronizing);
        Assert.False(second.Synchronization.Diverged, second.Synchronization.Summary);
        Assert.Null(second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.NotNull(second.Synchronization.Error);
        Assert.True(second.LocalSave.HasUnresolvedOutcome);
        Assert.True(second.ReconcileLocalSaves(), second.Status);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("Incoming after later unknown outcome", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.True(second.GetRelationships("report@example.com")!.Person.IsFavorite);
        Assert.Equal("Committed after synchronization admission",
            second.BeginNoteDraft(NoteScope.Person, "report@example.com", note.Id)!.Markdown);
    }

    [Fact]
    public async Task A_failed_actual_local_adoption_commit_preserves_the_valid_root_and_requires_local_recovery()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        var oldRevision = second.Synchronization.LocalRevision;
        Assert.True(first.EditPreferredName("report@example.com", "Complete incoming local transaction"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(Local("second"), "workspace.sqlite")};Pooling=False"))
        {
            connection.Open();
            using var held = connection.BeginTransaction(deferred: true);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = held;
                read.CommandText = "PRAGMA schema_version;";
                read.ExecuteScalar();
            }
            Assert.False(await Task.Run(second.SyncNow).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(oldRevision, second.Synchronization.LocalRevision);
            Assert.Null(second.GetRelationships("report@example.com")!.Person.PreferredName);
            Assert.True(second.IsWorkspaceCurrent);
            Assert.True(second.LocalSave.EditsPaused);
            Assert.Equal(LocalSaveOutcome.NotCommitted, second.LocalSave.Outcome);
            Assert.True(second.NavigatePerson("manager@example.com"), second.Status);
            held.Rollback();
        }
        Assert.True(second.ReconcileLocalSaves(), second.Status);
        Assert.False(second.LocalSave.HasUnresolvedOutcome);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.Equal("Complete incoming local transaction", second.GetRelationships("report@example.com")!.Person.PreferredName);
        Assert.Equal(second.Synchronization.LocalRevision!.Value.ToString("D"), second.LocalSave.CommittedRevision);
    }

    [Fact]
    public async Task A_published_Person_save_still_in_its_actual_VM_return_lifetime_defers_adoption_until_acknowledged()
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        var proxy = DispatchProxy.Create<IApplicationWorkflow, SnapshotSaveBoundary>();
        var boundary = (SnapshotSaveBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var second = new WorkspaceViewModel(proxy, timeProvider: new UnadvancedSaveClock());
        Assert.True(second.OpenWorkspace(Local("second"), WorkspaceOpenMode.CreateLocalSqlite), second.Status);
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        using var syncAdmitted = new ManualResetEventSlim();
        using var startSync = new ManualResetEventSlim();
        boundary.BeforeSync = () =>
        {
            syncAdmitted.Set();
            if (!startSync.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled synchronization admission was not released.");
        };
        var synchronizing = Task.Run(second.SyncNow);
        Assert.True(syncAdmitted.Wait(TimeSpan.FromSeconds(5)));
        using var committed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        boundary.AfterFavorite = () =>
        {
            committed.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled Person accepted return was not released.");
        };
        var saving = second.RunLocalSaveAsync(() => second.EditFavorite("report@example.com", true));
        try
        {
            Assert.True(committed.Wait(TimeSpan.FromSeconds(5)));
            using (var independent = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(independent.OpenWorkspace(Local("second"), WorkspaceOpenMode.ExistingOnly), independent.Status);
                Assert.True(independent.SyncNow(), independent.Synchronization.Error);
            }
            Deliver("incoming", "outgoing");
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Assert.True(first.EditPreferredName("report@example.com", "Incoming after Person commit"), first.Status);
            Assert.True(first.SyncNow(), first.Synchronization.Error);
            Deliver("outgoing", "incoming");
            Assert.True(second.LocalSave.OutstandingRequests > 0);
            Assert.Equal(LocalSavePhase.Pending, second.LocalSave.Phase);
            boundary.BeforeSync = null;
            startSync.Set();
            Assert.False(await synchronizing.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            boundary.BeforeSync = null;
            boundary.AfterFavorite = null;
            startSync.Set();
            release.Set();
            await Task.WhenAll(saving, synchronizing).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(await saving.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(second.GetRelationships("report@example.com")!.Person.IsFavorite);
        Assert.Equal("Incoming after Person commit", second.GetRelationships("report@example.com")!.Person.PreferredName);
    }

    private sealed class UnadvancedSaveClock : SnapshotClock
    {
        public override long GetTimestamp() => 0;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Held_adoption_driver_excludes_slow_preparation_but_still_rejects_late_actual_actions(bool lateAction)
    {
        using var first = Create("first");
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.True(first.ConfigureSynchronizationFolder(Local("outgoing")), first.Synchronization.Error);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        using var second = Create("second");
        Assert.True(second.ConfigureSynchronizationFolder(Local("incoming"), true), second.Synchronization.Error);
        Assert.True(second.SyncNow(), second.Synchronization.Error);
        Assert.True(first.EditPreferredName("report@example.com", "Incoming after prepared controls"), first.Status);
        Assert.True(first.SyncNow(), first.Synchronization.Error);
        Deliver("outgoing", "incoming");
        SnapshotAdoptionObservation? observed = null;
        PersonProfile? draftBasis = null;
        Task<bool>? sync = null;
        Exception? failure = null;
        var synchronized = false;
        try
        {
            HeldSnapshotAdoption.Run(Local("second"),
                () =>
                {
                    Thread.Sleep(1200);
                    return new(
                        () =>
                        {
                            Assert.True(second.BeginProfileEdit("report@example.com"), second.Status);
                            draftBasis = second.ProfileEdit!.Basis;
                        },
                        () =>
                        {
                            if (lateAction) Thread.Sleep(1200);
                            second.StageProfileText(PersonTextField.JobTitle, "Exact prepared owner draft");
                        },
                        () => Assert.True(second.NavigatePerson("manager@example.com"), second.Status),
                        () => Assert.Equal("manager@example.com", second.PeopleNavigation.SelectedEmail));
                },
                () => sync = Task.Run(second.SyncNow),
                () => Assert.True(SpinWait.SpinUntil(() => second.Synchronization.Phase == WorkspaceSynchronizationPhase.Adopting &&
                    File.Exists(Path.Combine(Local("second"), "workspace.sqlite-journal")), TimeSpan.FromSeconds(5))),
                () => { },
                value => observed = value);
        }
        catch (InvalidOperationException error) { failure = error; }
        finally
        {
            if (sync is not null) synchronized = await sync.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.NotNull(observed);
        Assert.All(observed.Milestones.Where(value => value.Stage.StartsWith("prepare-controls", StringComparison.Ordinal)),
            value => Assert.False(value.ReaderHeld));
        if (lateAction)
        {
            Assert.Equal("Native browsing/draft admission outlasted the actual held adoption transaction.", failure?.Message);
            Assert.Equal("Failed", observed.Outcome);
            Assert.Contains(observed.Milestones, value => value.Stage == "held-verdict" && value.ReaderHeld && !value.JournalPresent);
            Assert.False(synchronized);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal("Passed", observed.Outcome);
            Assert.Contains(observed.Milestones, value => value.Stage == "held-verdict" && value.ReaderHeld && value.JournalPresent);
            Assert.True(synchronized, second.Synchronization.Error);
            Assert.True(second.NavigatePerson("report@example.com"), second.Status);
            Assert.Equal("Exact prepared owner draft", second.ProfileEdit!.Draft.JobTitle);
            Assert.Same(draftBasis, second.ProfileEdit.Basis);
            Assert.Equal("Incoming after prepared controls", second.GetRelationships("report@example.com")!.Person.PreferredName);
        }
    }

    private sealed class SnapshotReadObserver(ManualResetEventSlim reading) : IWorkspaceFileObserver
    {
        public volatile bool Armed;
        public volatile bool Active;
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (!Armed || phase.Name != "snapshot.local-read") return;
            Active = phase.Kind == WorkspaceStoragePhaseKind.Begin;
            if (Active) reading.Set();
        }
    }

    public class SnapshotSaveBoundary : DispatchProxy
    {
        public IApplicationWorkflow Inner { get; set; } = null!;
        public Action? AfterSave { get; set; }
        public Action? AfterReconcile { get; set; }
        public Action? BeforeDiscard { get; set; }
        public Action? AfterFavorite { get; set; }
        public Action? BeforeSync { get; set; }
        public Action? AfterSync { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IApplicationWorkflow.DiscardUnsavedChanges)) BeforeDiscard?.Invoke();
            if (targetMethod.Name == nameof(IApplicationWorkflow.SyncNow)) BeforeSync?.Invoke();
            object? result;
            try { result = targetMethod!.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
            if (targetMethod.Name == nameof(IApplicationWorkflow.SaveNoteDraft) && result is NoteDraft)
                AfterSave?.Invoke();
            if (targetMethod.Name == nameof(IApplicationWorkflow.ReconcileLocalSaves) && result is true)
                AfterReconcile?.Invoke();
            if (targetMethod.Name == nameof(IApplicationWorkflow.EditFavorite) && result is true)
                AfterFavorite?.Invoke();
            if (targetMethod.Name == nameof(IApplicationWorkflow.SyncNow) && result is true)
                AfterSync?.Invoke();
            return result;
        }
    }

    private sealed class SnapshotPublisher : IWorkspaceFilePublisher
    {
        public bool Interrupt, AfterPublish, Hold;
        public Action? BeforeSnapshot;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public void Publish(string staged, string destination, string? backup,
            WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
        {
            if (destination.EndsWith(".oesnapshot", StringComparison.Ordinal))
            {
                BeforeSnapshot?.Invoke();
                Entered.Set();
                if (Hold && !Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled publication wait expired.");
                if (Interrupt && !AfterPublish) throw new IOException("Interrupted before publication.");
            }
            new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
            if (Interrupt && destination.EndsWith(".oesnapshot", StringComparison.Ordinal))
                throw new IOException("Interrupted after publication; outcome not acknowledged.");
        }
    }

    private static async Task AwaitSnapshot(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static void RewriteSnapshot(string path, Action<JsonObject> change)
    {
        var envelope = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var content = JsonNode.Parse(Convert.FromBase64String(envelope["content"]!.GetValue<string>()))!.AsObject();
        change(content);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content);
        envelope["content"] = Convert.ToBase64String(bytes);
        envelope["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllText(path, envelope.ToJsonString());
    }

    private WorkspaceViewModel Create(string name, IWorkspaceFileObserver? observer = null)
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(fileObserver: observer));
        Assert.True(app.OpenWorkspace(Local(name), WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        return app;
    }

    private void Deliver(string from, string to)
    {
        Directory.CreateDirectory(Local(to));
        foreach (var path in Directory.GetFiles(Local(from), "*.oesnapshot"))
            File.Copy(path, Path.Combine(Local(to), Path.GetFileName(path)), true);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
