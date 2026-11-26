using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class ExternalRefreshTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_snapshot_supplies_a_Person_without_changing_search_Import_or_saved_destinations(bool asynchronous)
    {
        var workspace = Path.Combine(root, "receiver");
        var preferencesPath = Path.Combine(root, "settings.json");
        var settings = new WorkspaceSettings(preferencesPath);
        var remembered = new RememberedView("missing@example.com", SearchSource.PersonNote, Guid.NewGuid(), "missing@example.com");
        Assert.True(settings.SetLastView(workspace, remembered));
        Assert.True(settings.SetPaneProportions(workspace, new(28, 38)));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(source.ApplyImport(source.PreviewImport(Batch("""
            {"email":"former@example.com","displayName":"Needle","managerEmail":null,"archived":true,"isFavorite":true}
            """)).Id), source.Status);
        var retained = source.CreatePersonNote("former@example.com", "Private context", "Exact retained writing")!;
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        Assert.Null(app.PeopleNavigation.SelectedEmail);
        var recovery = app.ViewRecoveryFeedback;
        Assert.Contains("unavailable", recovery);
        app.ChangePeopleQuery("Needle");
        app.ShowPeopleOverlay(true);
        Assert.True(app.TryOpenImport());
        var highlight = app.PeopleNavigation.HighlightedResult;
        Assert.Equal("former@example.com", highlight!.Person.Email);
        var preferences = File.ReadAllBytes(preferencesPath);
        Assert.True(source.ApplyImport(source.PreviewImport(Batch("""
            {"email":"a-root@example.com","displayName":"A root","managerEmail":null},
            {"email":"a-chain@example.com","displayName":"A chain","managerEmail":"a-root@example.com"},
            {"email":"deep@example.com","displayName":"A deep","managerEmail":"a-chain@example.com"},
            {"email":"deep-one@example.com","displayName":"Deep one","managerEmail":"deep@example.com"},
            {"email":"deep-two@example.com","displayName":"Deep two","managerEmail":"deep@example.com"},
            {"email":"z-root@example.com","displayName":"Z root","managerEmail":null},
            {"email":"shallow@example.com","displayName":"Z shallow","managerEmail":"z-root@example.com"},
            {"email":"shallow-one@example.com","displayName":"Shallow one","managerEmail":"shallow@example.com"},
            {"email":"shallow-two@example.com","displayName":"Shallow two","managerEmail":"shallow@example.com"}
            """)).Id), source.Status);
        var ongoing = source.CreateTeamNote("shallow@example.com", "Team reading", "Accepted Team writing")!;
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        Assert.Null(app.PeopleNavigation.SelectedEmail);

        Assert.True(asynchronous ? await app.SynchronizeAsync() : app.SyncNow(), app.Synchronization.Error);

        Assert.Equal(source.Synchronization.LocalRevision, app.Synchronization.LocalRevision);
        Assert.Equal("shallow@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal("shallow@example.com", app.PersonNotesView.OwnerEmail);
        Assert.Equal(ongoing.Id, app.TeamNotesView.SelectedNote!.Id);
        Assert.Equal("Accepted Team writing", app.TeamNotesView.SelectedNote.Markdown);
        Assert.False(app.TeamNotesView.IsEditing);
        Assert.Null(app.ProfileEdit);
        Assert.Empty(app.GetPersonNotes("shallow@example.com", true));
        Assert.Equal(retained, Assert.Single(app.GetPersonNotes("former@example.com", true)));
        Assert.Equal("Needle", app.PeopleNavigation.Query);
        Assert.Same(highlight, app.PeopleNavigation.HighlightedResult);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.True(app.PeopleNavigation.OverlayOpen);
        Assert.True(app.IsImportOpen);
        Assert.Equal(recovery, app.ViewRecoveryFeedback);
        Assert.Null(app.PeopleNavigation.LastValidView);
        Assert.Equal(new PaneProportions(28, 38), app.PaneProportions);
        Assert.Equal(preferences, File.ReadAllBytes(preferencesPath));
        Assert.Equal(remembered, new WorkspaceSettings(preferencesPath).GetLastView(workspace));
        app.ShowPeople();
        Assert.Equal("Needle", app.PeopleNavigation.Query);
        app.ChangePeopleQuery("");
        Assert.Contains("z-root@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Contains("shallow@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Equal(preferences, File.ReadAllBytes(preferencesPath));
    }

    private static string Batch(string people) => $$"""
        {"schemaVersion":1,"batchId":"{{Guid.NewGuid()}}",
         "observedAt":"2026-11-01T17:00:00Z","people":[{{people}}]}
        """;

    [Theory]
    [InlineData("invalid")]
    [InlineData("incomplete")]
    [InlineData("deferred")]
    [InlineData("unavailable")]
    public void Unaccepted_delivery_does_not_select_incoming_people_or_hide_recovery(string refusal)
    {
        var workspace = Path.Combine(root, "receiver");
        var preferencesPath = Path.Combine(root, "settings.json");
        var settings = new WorkspaceSettings(preferencesPath);
        Assert.True(settings.SetLastView(workspace, new("missing@example.com")));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        var transport = Path.Combine(root, "transport");
        WorkspaceFixtures.JoinSnapshot(source, app, transport);
        var revision = app.Synchronization.LocalRevision;
        var recovery = app.ViewRecoveryFeedback;
        var preferences = File.ReadAllBytes(preferencesPath);
        Assert.True(source.ApplyImport(source.PreviewImport(Batch("""
            {"email":"manager@example.com","displayName":"Manager","managerEmail":null},
            {"email":"first@example.com","displayName":"First","managerEmail":"manager@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"manager@example.com"}
            """)).Id), source.Status);
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        var incoming = WorkspaceFixtures.LatestSnapshot(source);
        var complete = File.ReadAllBytes(incoming);
        using (refusal == "deferred" ? app.DeferSnapshotAdoption() : null)
        {
            if (refusal == "invalid")
                WorkspaceFixtures.RewriteSnapshot(incoming, content => content["people"] = new System.Text.Json.Nodes.JsonArray());
            if (refusal == "incomplete") File.WriteAllBytes(incoming, complete[..(complete.Length / 2)]);
            if (refusal == "unavailable") Directory.Move(transport, transport + "-held");
            try
            {
                Assert.False(app.SyncNow());
                Assert.NotNull(app.Synchronization.Error);
                Assert.Equal(revision, app.Synchronization.LocalRevision);
                Assert.Empty(app.People);
                Assert.Null(app.PeopleNavigation.SelectedEmail);
                Assert.Null(app.PersonNotesView.OwnerEmail);
                Assert.Null(app.TeamNotesView.OwnerEmail);
                Assert.Equal(recovery, app.ViewRecoveryFeedback);
                Assert.Equal(preferences, File.ReadAllBytes(preferencesPath));
            }
            finally
            {
                if (refusal == "unavailable") Directory.Move(transport + "-held", transport);
                else File.WriteAllBytes(incoming, complete);
            }
        }
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Equal("manager@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(recovery, app.ViewRecoveryFeedback);
        Assert.Equal(preferences, File.ReadAllBytes(preferencesPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepted_delivery_keeps_valid_current_or_Archived_exact_reading_and_saved_destination(bool archived)
    {
        var workspace = Path.Combine(root, "receiver");
        var preferencesPath = Path.Combine(root, "settings.json");
        var settings = new WorkspaceSettings(preferencesPath);
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(source.ApplyImport(source.PreviewImport(Batch($$"""
            {"email":"reader@example.com","displayName":"Reader","managerEmail":null,"archived":{{archived.ToString().ToLowerInvariant()}}}
            """)).Id), source.Status);
        var ongoing = source.CreatePersonNote("reader@example.com", "Ongoing", "Continuing writing")!;
        var secondary = source.CreatePersonNote("reader@example.com", "Exact secondary", "Exact secondary writing")!;
        var team = source.CreateTeamNote("reader@example.com", "Team", "Exact Team writing")!;
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        Assert.True(app.NavigatePerson("reader@example.com"));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, ongoing.Id));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id));
        var preferences = File.ReadAllBytes(preferencesPath);
        var destination = new WorkspaceSettings(preferencesPath).GetLastView(workspace);
        Assert.True(source.ApplyImport(source.PreviewImport(Batch("""
            {"email":"higher@example.com","displayName":"A higher Manager","managerEmail":null},
            {"email":"first@example.com","displayName":"First","managerEmail":"higher@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"higher@example.com"}
            """)).Id), source.Status);
        WorkspaceFixtures.DeliverSnapshot(source, app);
        Assert.Equal("reader@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(archived, app.GetRelationships("reader@example.com")!.Person.Archived);
        Assert.Equal(secondary.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal("Exact secondary writing", app.PersonNotesView.SelectedNote.Markdown);
        Assert.Equal(team.Id, app.TeamNotesView.SelectedNote!.Id);
        Assert.Equal(preferences, File.ReadAllBytes(preferencesPath));
        Assert.Equal(destination, new WorkspaceSettings(preferencesPath).GetLastView(workspace));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(2, reopened.GetPersonNotes("reader@example.com", true).Count);
        Assert.Equal(secondary, reopened.GetPersonNotes("reader@example.com", true).Single(n => n.Id == secondary.Id));
        Assert.Equal(team, Assert.Single(reopened.GetTeamNotes("reader@example.com", true)));
    }

    [Fact]
    public void Failed_local_refresh_does_not_select_from_retained_stale_data()
    {
        var workspace = Path.Combine(root, "workspace");
        var workflow = ApplicationWorkflow.CreateFileBacked();
        Assert.True(workflow.OpenWorkspace(workspace), workflow.Status);
        Assert.True(workflow.ApplyImport(workflow.PreviewImport(Batch("""
            {"email":"reader@example.com","displayName":"Reader","managerEmail":null}
            """)).Id), workflow.Status);
        using var app = new WorkspaceViewModel(workflow);
        var marker = Path.Combine(workspace, "local-workspace.format");
        var original = File.ReadAllBytes(marker);
        File.WriteAllText(marker, "Unrecognized Workspace format");
        try
        {
            Assert.False(app.RefreshWorkspace());
            Assert.False(app.IsWorkspaceCurrent);
            Assert.Contains("STALE", app.Status);
            Assert.Single(app.People);
            Assert.Null(app.PeopleNavigation.SelectedEmail);
            Assert.Null(app.PersonNotesView.OwnerEmail);
            Assert.Null(app.TeamNotesView.OwnerEmail);
            Assert.Null(app.ProfileEdit);
        }
        finally { File.WriteAllBytes(marker, original); }
    }

    [Fact]
    public void Validated_local_refresh_selects_from_another_clients_commit_without_remembering_the_fallback()
    {
        var workspace = Path.Combine(root, "workspace");
        var preferencesPath = Path.Combine(root, "settings.json");
        var settings = new WorkspaceSettings(preferencesPath);
        Assert.True(settings.SetLastView(workspace, new("missing@example.com")));
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        var recovery = app.ViewRecoveryFeedback;
        var preferences = File.ReadAllBytes(preferencesPath);
        using var writer = ApplicationWorkflow.CreateFileBacked();
        Assert.True(writer.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly), writer.Status);
        Assert.True(writer.ApplyImport(writer.PreviewImport(Batch("""
            {"email":"manager@example.com","displayName":"Manager","managerEmail":null},
            {"email":"first@example.com","displayName":"First","managerEmail":"manager@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"manager@example.com"}
            """)).Id), writer.Status);
        var note = writer.CreatePersonNote("manager@example.com", "Reading", "Exact committed writing")!;
        Assert.Empty(app.People);
        Assert.Null(app.PeopleNavigation.SelectedEmail);

        Assert.True(app.RefreshWorkspace(), app.Status);

        Assert.Equal("manager@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.Null(app.PeopleNavigation.LastValidView);
        Assert.Equal(recovery, app.ViewRecoveryFeedback);
        Assert.Equal(preferences, File.ReadAllBytes(preferencesPath));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(note, Assert.Single(reopened.GetPersonNotes("manager@example.com")));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Real_refresh_and_deferred_delivery_preserve_exact_failed_notes_profile_and_Preferred_name_drafts(NoteScope scope)
    {
        var workspace = Path.Combine(root, "receiver");
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(source.ApplyImport(source.PreviewImport(Batch("""
            {"email":"reader@example.com","displayName":"Reader","managerEmail":null}
            """)).Id), source.Status);
        var person = source.CreatePersonNote("reader@example.com", "Person", "Saved Person writing")!;
        var team = source.CreateTeamNote("reader@example.com", "Team", "Saved Team writing")!;
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        Assert.True(app.NavigatePerson("reader@example.com"));
        var note = scope == NoteScope.Person ? person.Id : team.Id;
        Assert.True(app.OpenOwnedNote(scope, note, editing: true));
        Assert.True(app.BeginProfileEdit("reader@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Exact retained room"));
        var profile = app.ProfileEdit;
        app.RetainPersonTextDraft(app.GetRelationships("reader@example.com")!.Person,
            PersonTextField.PreferredName, "Exact retained Preferred name");
        var preferred = app.GetPersonTextDraft("reader@example.com", PersonTextField.PreferredName);
        app.UpdateNoteDraft(scope, note, "", "Exact rejected writing");
        Assert.False(await app.FlushNoteDraftsAsync());
        var draft = app.GetRetainedNoteDraft(scope, note)!;
        Assert.Contains("NOT saved", draft.Error);
        var error = app.LocalSaveError;
        Assert.NotNull(error);
        var revision = app.Synchronization.LocalRevision;
        Assert.True(source.ApplyImport(source.PreviewImport(Batch("""
            {"email":"higher@example.com","displayName":"A higher Manager","managerEmail":null},
            {"email":"first@example.com","displayName":"First","managerEmail":"higher@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"higher@example.com"}
            """)).Id), source.Status);
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        Assert.False(app.SyncNow());
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(revision, app.Synchronization.LocalRevision);
        Assert.Equal("reader@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(draft, app.GetRetainedNoteDraft(scope, note));
        Assert.Equal(note, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.Equal("Exact rejected writing", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.Same(profile, app.ProfileEdit);
        Assert.Same(preferred, app.GetPersonTextDraft("reader@example.com", PersonTextField.PreferredName));
        Assert.Equal(error, app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(person, Assert.Single(reopened.GetPersonNotes("reader@example.com")));
        Assert.Equal(team, Assert.Single(reopened.GetTeamNotes("reader@example.com")));
        Assert.Null(reopened.GetRelationships("reader@example.com")!.Person.OfficeNumber);
        Assert.Null(reopened.GetRelationships("reader@example.com")!.Person.PreferredName);
        Assert.Null(reopened.GetRelationships("higher@example.com"));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Accepted_same_Person_refresh_retains_missing_exact_note_recovery_without_substitute_writing(NoteScope scope)
    {
        var workspace = Path.Combine(root, "receiver");
        var preferencesPath = Path.Combine(root, "settings.json");
        using var source = ApplicationWorkflow.CreateFileBacked();
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(source.ApplyImport(source.PreviewImport(Batch("""
            {"email":"reader@example.com","displayName":"Reader","managerEmail":null}
            """)).Id), source.Status);
        var person = source.CreatePersonNote("reader@example.com", "Person ongoing", "Exact Person writing")!;
        var team = source.CreateTeamNote("reader@example.com", "Team ongoing", "Exact Team writing")!;
        using (var receiver = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(receiver.OpenWorkspace(workspace), receiver.Status);
            WorkspaceFixtures.JoinSnapshot(source, receiver, Path.Combine(root, "transport"));
        }
        var settings = new WorkspaceSettings(preferencesPath);
        var remembered = new RememberedView("reader@example.com",
            scope == NoteScope.Person ? SearchSource.PersonNote : SearchSource.OwnedTeamNote,
            Guid.NewGuid(), "reader@example.com");
        Assert.True(settings.SetLastView(workspace, remembered));
        Assert.True(settings.SetPaneProportions(workspace, new(28, 38)));
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        Assert.Equal("reader@example.com", app.PeopleNavigation.SelectedEmail);
        var recovery = app.ViewRecoveryFeedback;
        Assert.Contains("exact note is unavailable", recovery);
        var preferences = File.ReadAllBytes(preferencesPath);
        Assert.True(source.EditPersonText("reader@example.com", PersonTextField.JobTitle, "Accepted refreshed role"));
        Assert.True(source.ApplyImport(source.PreviewImport(Batch("""
            {"email":"higher@example.com","displayName":"A higher Manager","managerEmail":null},
            {"email":"first@example.com","displayName":"First","managerEmail":"higher@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"higher@example.com"}
            """)).Id), source.Status);

        WorkspaceFixtures.DeliverSnapshot(source, app);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.RefreshPersonNotes(), app.Status);

        Assert.Equal("Accepted refreshed role", app.GetRelationships("reader@example.com")!.Person.JobTitle);
        Assert.Equal("reader@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(recovery, app.ViewRecoveryFeedback);
        foreach (var noteScope in Enum.GetValues<NoteScope>())
        {
            var view = app.GetOwnedNotesView(noteScope);
            Assert.Equal("reader@example.com", view.OwnerEmail);
            Assert.Null(view.SelectedNote);
            Assert.False(view.IsEditing);
            Assert.False(view.IsEmpty);
            Assert.Equal(recovery, view.Feedback);
        }
        Assert.Null(app.PeopleNavigation.LastValidView);
        Assert.Equal(preferences, File.ReadAllBytes(preferencesPath));
        Assert.Equal(remembered, new WorkspaceSettings(preferencesPath).GetLastView(workspace));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(person, Assert.Single(reopened.GetPersonNotes("reader@example.com")));
        Assert.Equal(team with { NeedsReview = true }, Assert.Single(reopened.GetTeamNotes("reader@example.com")));
    }

    [Fact]
    public void Complete_snapshot_refreshes_open_profile_photo_history_and_private_writing_without_replaying_imports()
    {
        WorkspaceFixtures.CopyCurrent(Path.Combine(root, "source"));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(app.OpenWorkspace(Path.Combine(root, "receiver")), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        var note = Assert.Single(source.GetPersonNotes("avery@example.com"));
        var capture = Assert.Single(source.GetHistoricalTeamContexts("avery@example.com"));
        var notifications = 0;
        app.WorkspaceChanged += (_, _) => notifications++;
        Assert.True(source.EditPreferredName("avery@example.com", "Snapshot Avery"), source.Status);
        Assert.True(source.EditPersonNote(note.Id, "Revised", "# Revised café writing"), source.Status);
        WorkspaceFixtures.DeliverSnapshot(source, app);
        Assert.Equal("Snapshot Avery", app.GetRelationships("avery@example.com")!.Person.Heading);
        Assert.Equal("# Revised café writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.NotNull(app.GetPhoto("avery@example.com"));
        Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.True(notifications > 0);
        var revision = app.Synchronization.LocalRevision;
        for (var i = 0; i < 3; i++) Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Equal(revision, app.Synchronization.LocalRevision);
        Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Single(app.GetProfileTimeline("avery@example.com")!.Changes, c => c.Field == "preferredName");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_or_incomplete_snapshot_photo_never_replaces_the_last_decoded_photo(bool incomplete)
    {
        WorkspaceFixtures.CopyCurrent(Path.Combine(root, "source"));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(app.OpenWorkspace(Path.Combine(root, "receiver")), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        var photo = app.GetPhoto("avery@example.com")!;
        Assert.True(source.EditPreferredName("avery@example.com", "Next revision"));
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        var path = WorkspaceFixtures.LatestSnapshot(source);
        var complete = File.ReadAllBytes(path);
        if (incomplete) File.WriteAllBytes(path, complete[..(complete.Length / 2)]);
        else WorkspaceFixtures.RewriteSnapshot(path, content =>
            content["photos"]![0]!["content"] = Convert.ToBase64String("Not a decoded image"u8));
        var rejected = File.ReadAllBytes(path);
        Assert.False(app.SyncNow());
        Assert.Equal(photo.Bytes.ToArray(), app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.Heading);
        Assert.Equal(rejected, File.ReadAllBytes(path));
        File.WriteAllBytes(path, complete);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Equal("Next revision", app.GetRelationships("avery@example.com")!.Person.Heading);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
