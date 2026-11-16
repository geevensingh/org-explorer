using OrgExplorer.Workflow;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public void Clean_continuation_preserves_private_dormant_names_current_paths_photos_and_complete_frozen_history()
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
        Assert.True(first.EditPersonManager(report, null), first.Status);
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

    [Fact]
    public async Task A_new_local_commit_during_export_stays_unpublished_and_does_not_change_the_exported_version()
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
            Assert.True(app.EditPreferredName("report@example.com", "Committed during export"), app.Status);
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

    private sealed class SnapshotPublisher : IWorkspaceFilePublisher
    {
        public bool Interrupt, AfterPublish, Hold;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public void Publish(string staged, string destination, string? backup,
            WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
        {
            if (destination.EndsWith(".oesnapshot", StringComparison.Ordinal))
            {
                Entered.Set();
                if (Hold && !Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled publication wait expired.");
                if (Interrupt && !AfterPublish) throw new IOException("Interrupted before publication.");
            }
            new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
            if (Interrupt && destination.EndsWith(".oesnapshot", StringComparison.Ordinal))
                throw new IOException("Interrupted after publication; outcome not acknowledged.");
        }
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

    private WorkspaceViewModel Create(string name)
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
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
