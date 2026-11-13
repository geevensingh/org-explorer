using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class WorkspaceStartupTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "startup-" + Guid.NewGuid().ToString("N")));
    private string Preferences => Path.Combine(root, "app-settings", "workspace-settings.json");

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Validated_reopen_restores_the_exact_secondary_note_by_identity_in_Read_mode(NoteScope scope)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        Guid ongoing;
        Guid secondary;
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(app, workspace));
            ongoing = scope == NoteScope.Person
                ? app.CreatePersonNote("avery@example.com", "Continuing", "Ongoing writing")!.Id
                : app.CreateTeamNote("avery@example.com", "Continuing", "Ongoing writing")!.Id;
            secondary = scope == NoteScope.Person
                ? app.CreatePersonNote("avery@example.com", "Secondary", "")!.Id
                : app.CreateTeamNote("avery@example.com", "Secondary", "")!.Id;
            Assert.True(app.NavigatePerson("avery@example.com"));
            Assert.True(app.DesignateOngoingNote(scope, ongoing));
            Assert.True(app.OpenOwnedNote(scope, secondary, editing: true));
            Assert.True(scope == NoteScope.Person
                ? app.EditPersonNote(secondary, "Renamed secondary", "")
                : app.EditTeamNote(secondary, "Renamed secondary", ""));
            Assert.True(app.NavigatePerson("avery@example.com"));
            Assert.True(app.SavePaneProportions(28, 38));
            Assert.True(new WorkspaceSettings(Preferences).SetOngoingNote(workspace, scope, "avery@example.com", ongoing));
        }
        var before = WorkspaceFixtures.Bytes(workspace);
        var reopenedSettings = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), reopenedSettings);
        Assert.True(reopenedSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.True(reopened.IsWorkspaceCurrent);
        Assert.Equal("avery@example.com", reopened.PeopleNavigation.SelectedEmail);
        var view = reopened.GetOwnedNotesView(scope);
        Assert.Equal(secondary, view.SelectedNote!.Id);
        Assert.Equal("Renamed secondary", view.SelectedNote.Title);
        Assert.Equal("", view.SelectedNote.Markdown);
        Assert.Equal(ongoing, view.OngoingNoteId);
        Assert.False(view.IsOngoing);
        Assert.False(view.IsEditing);
        Assert.Equal(new PaneProportions(28, 38), reopened.PaneProportions);
        AssertWorkspaceBytes(workspace, before);
        Assert.True(reopened.OpenOngoingNote(scope));
        Assert.Equal(ongoing, reopened.GetOwnedNotesView(scope).SelectedNote!.Id);
    }

    [Theory]
    [InlineData("archived-person")]
    [InlineData("missing-person")]
    [InlineData("archived-note")]
    [InlineData("missing-note")]
    public void Unavailable_exact_view_retains_preferences_and_writing_until_deliberate_reselection(string failure)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        RememberedView remembered;
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(app, workspace));
            var ongoing = app.CreatePersonNote("avery@example.com", "Continuing", "Never a replacement")!;
            var other = app.CreatePersonNote("avery@example.com", "Secondary", "Retained secondary writing")!;
            Assert.True(app.NavigatePerson(other.OwnerEmail));
            Assert.True(app.DesignateOngoingNote(NoteScope.Person, ongoing.Id));
            Assert.True(app.OpenOwnedNote(NoteScope.Person, other.Id));
            remembered = settings.GetLastView(workspace)!;
            if (failure == "archived-person") Assert.True(app.EditPersonArchived(other.OwnerEmail, true));
            if (failure == "archived-note") Assert.True(app.ArchivePersonNote(other.Id));
            if (failure == "missing-person")
                remembered = remembered with { PersonEmail = "missing@example.com", NoteOwnerEmail = "missing@example.com" };
            if (failure == "missing-note") remembered = remembered with { NoteId = Guid.NewGuid() };
            Assert.True(settings.SetLastView(workspace, remembered));
        }
        var bytes = WorkspaceFixtures.Bytes(workspace);
        var reopenedSettings = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), reopenedSettings);
        Assert.True(reopenedSettings.RestoreWorkspace(reopened));
        Assert.True(reopened.IsWorkspaceCurrent);
        Assert.Null(reopened.PeopleNavigation.SelectedEmail);
        Assert.Null(reopened.PersonNotesView.SelectedNote);
        Assert.Null(reopened.TeamNotesView.SelectedNote);
        Assert.Contains("unavailable or archived", reopened.ViewRecoveryFeedback);
        Assert.Contains("search, Favorites or People", reopened.ViewRecoveryFeedback);
        Assert.Equal(remembered, new WorkspaceSettings(Preferences).GetLastView(workspace));
        AssertWorkspaceBytes(workspace, bytes);
        Assert.True(reopened.NavigatePerson("casey@example.com"));
        Assert.Null(reopened.ViewRecoveryFeedback);
        Assert.Equal("casey@example.com", new WorkspaceSettings(Preferences).GetLastView(workspace)!.PersonEmail);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("corrupt")]
    [InlineData("version")]
    [InlineData("identity")]
    public void Failed_exact_view_write_keeps_the_prior_durable_view_and_explains_the_in_session_choice(string failure)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        var first = app.CreateTeamNote("avery@example.com", "Continuing", "Unchanged")!;
        var other = app.CreateTeamNote("avery@example.com", "Secondary", "")!;
        Assert.True(app.NavigatePerson(first.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Team, first.Id));
        var prior = settings.GetLastView(workspace);
        var valid = File.ReadAllBytes(Preferences);
        var before = WorkspaceFixtures.Bytes(workspace);
        if (failure != "locked")
        {
            var json = JsonNode.Parse(valid)!;
            if (failure == "version") json["schemaVersion"] = 99;
            if (failure == "identity") json["workspaces"]![0]!["lastView"]!["noteId"] = Guid.Empty.ToString();
            File.WriteAllText(Preferences, failure == "corrupt" ? "{broken" : json.ToJsonString());
        }
        var rejected = File.ReadAllBytes(Preferences);
        using (var held = failure == "locked" ? File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            Assert.True(app.OpenOwnedNote(NoteScope.Team, other.Id));
            Assert.Equal(other.Id, app.TeamNotesView.SelectedNote!.Id);
            Assert.Contains("NOT remembered", app.ViewPreferenceFeedback);
            Assert.Contains("retry", app.ViewPreferenceFeedback);
            Assert.Equal(prior, settings.GetLastView(workspace));
            Assert.Equal(rejected, File.ReadAllBytes(Preferences));
            AssertWorkspaceBytes(workspace, before);
        }
        File.WriteAllBytes(Preferences, valid);
        Assert.True(app.OpenOwnedNote(NoteScope.Team, other.Id));
        Assert.Null(app.ViewPreferenceFeedback);
        Assert.Equal(other.Id, new WorkspaceSettings(Preferences).GetLastView(workspace)!.NoteId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reopened_search_note_keeps_its_inherited_or_frozen_owner_context_not_current_owned_writing(bool historical)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        Guid noteId;
        Guid? captureId = null;
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(app, workspace));
            noteId = app.CreateTeamNote("casey@example.com", "Cedar context", "Cedar original")!.Id;
            app.CreateTeamNote("avery@example.com", "Own continuing", "Not the ancestor");
            if (historical)
            {
                Assert.True(app.EditPersonManager("avery@example.com", null));
                captureId = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"),
                    c => c.TeamNotes.Any(n => n.Id == noteId)).Id;
                Assert.True(app.EditTeamNote(noteId, "Later title", "Later current writing"));
                Assert.True(app.ArchiveTeamNote(noteId));
            }
            app.ChangePeopleQuery("Cedar");
            var result = Assert.Single(app.Search("Cedar"), r => r.Person.Email == "avery@example.com");
            Assert.True(app.NavigateSearchResult(result));
        }
        var before = WorkspaceFixtures.Bytes(workspace);
        var restoredSettings = new WorkspaceSettings(Preferences);
        using var restored = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), restoredSettings);
        Assert.True(restoredSettings.RestoreWorkspace(restored));
        Assert.Equal("avery@example.com", restored.PeopleNavigation.SelectedEmail);
        var destination = restored.PeopleNavigation.SelectedResult!;
        Assert.Equal(historical ? SearchSource.HistoricalTeamNote : SearchSource.InheritedTeamNote, destination.Source);
        Assert.Equal(noteId, destination.NoteId);
        Assert.Equal("casey@example.com", destination.NoteOwner!.Email);
        Assert.Equal(captureId, destination.HistoricalContextId);
        Assert.Equal("Cedar original", destination.MatchText);
        Assert.NotEqual(noteId, restored.TeamNotesView.SelectedNote!.Id);
        Assert.Null(restored.ViewRecoveryFeedback);
        AssertWorkspaceBytes(workspace, before);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Existing_only_reopen_cannot_replace_independent_retained_writing_or_the_saved_view(NoteScope scope)
    {
        var workspace = Path.Combine(root, "workspace");
        var alternate = Path.Combine(root, "alternate");
        WorkspaceFixtures.CopyVersionOne(workspace);
        WorkspaceFixtures.CopyVersionOne(alternate);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        var note = scope == NoteScope.Person
            ? app.CreatePersonNote("avery@example.com", "Exact reading", "Saved")!.Id
            : app.CreateTeamNote("avery@example.com", "Exact reading", "Saved")!.Id;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.OpenOwnedNote(scope, note, editing: true));
        var basis = app.BeginNoteDraft(scope, "avery@example.com", note)!;
        app.RetainNoteDraft(basis);
        var preferences = File.ReadAllBytes(Preferences);
        var before = WorkspaceFixtures.Bytes(workspace);
        Assert.False(settings.RestoreWorkspace(app, alternate));
        Assert.Equal(workspace, app.Compatibility.WorkspacePath);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(note, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.Contains("draft", app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => throw new InvalidOperationException("Unsafe restart")));
        Assert.Equal(preferences, File.ReadAllBytes(Preferences));
        AssertWorkspaceBytes(workspace, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_view_preference_preserves_sole_reading_only_when_preferences_were_valid(bool unreadable)
    {
        var workspace = Path.Combine(root, "workspace");
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"62000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"fresh@example.com","displayName":"Fresh","managerEmail":null}]}
            """).Id));
        var note = app.CreatePersonNote("fresh@example.com", "Sole reading", "Existing writing")!;
        if (unreadable)
        {
            File.WriteAllText(Preferences, "{broken");
            var invalid = new WorkspaceSettings(Preferences);
            using var manual = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), invalid);
            Assert.True(invalid.OpenWorkspace(manual, workspace));
            Assert.True(manual.NavigatePerson(note.OwnerEmail));
            Assert.Null(manual.PersonNotesView.OngoingNoteId);
            Assert.Null(manual.PersonNotesView.SelectedNote);
            Assert.Contains("NOT remembered", manual.ViewPreferenceFeedback);
            Assert.Equal("{broken", File.ReadAllText(Preferences));
            return;
        }
        var before = File.ReadAllBytes(Preferences);
        using (File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(app.NavigatePerson(note.OwnerEmail));
            Assert.Equal(note.OwnerEmail, app.PeopleNavigation.SelectedEmail);
            Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
            Assert.False(app.PersonNotesView.IsEditing);
            Assert.Contains("NOT remembered", app.ViewPreferenceFeedback);
            Assert.Null(settings.GetOngoingNote(workspace, NoteScope.Person, note.OwnerEmail));
            Assert.Equal(before, File.ReadAllBytes(Preferences));
        }
    }

    [Fact]
    public void A_later_owner_can_record_a_Team_choice_after_preference_failure_and_retain_it_through_archival()
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        var first = app.CreateTeamNote("avery@example.com", "Continuing Team", "Saved writing")!;
        var other = app.CreateTeamNote(first.OwnerEmail, "Secondary Team", "")!;
        Assert.True(app.NavigatePerson(first.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Team, first.Id));
        Assert.True(app.OpenOwnedNote(NoteScope.Team, other.Id));
        using (File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(app.DesignateOngoingNote(NoteScope.Team, other.Id));
        Assert.Contains("NOT saved", app.TeamNotesView.Feedback);
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"61000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null}]}
            """).Id));
        Assert.True(app.NavigatePerson("morgan@example.com"));
        var later = app.CreateTeamNote("morgan@example.com", "Morgan context", "# Frozen direct\nOriginal direct")!;
        Assert.True(app.OpenOwnedNote(NoteScope.Team, later.Id));
        Assert.Null(settings.GetOngoingNote(workspace, NoteScope.Team, later.OwnerEmail));
        Assert.Null(app.TeamNotesView.OngoingNoteId);
        Assert.Contains("NOT saved", app.TeamNotesView.Feedback);
        var before = WorkspaceFixtures.Bytes(workspace);
        Assert.True(app.DesignateOngoingNote(NoteScope.Team, later.Id), app.TeamNotesView.Feedback);
        Assert.True(app.TeamNotesView.IsOngoing);
        Assert.Null(settings.PreferenceError);
        AssertWorkspaceBytes(workspace, before);
        var durable = new WorkspaceSettings(Preferences);
        Assert.Equal(first.Id, durable.GetOngoingNote(workspace, NoteScope.Team, first.OwnerEmail));
        Assert.Equal(later.Id, durable.GetOngoingNote(workspace, NoteScope.Team, later.OwnerEmail));
        Assert.True(app.ArchiveTeamNote(later.Id));
        Assert.False(app.OpenOngoingNote(NoteScope.Team));
        Assert.Null(app.TeamNotesView.SelectedNote);
        Assert.Contains("unavailable or archived", app.TeamNotesView.Feedback);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(Preferences));
        Assert.True(reopened.OpenWorkspace(workspace));
        Assert.True(reopened.NavigatePerson(later.OwnerEmail));
        Assert.Null(reopened.TeamNotesView.SelectedNote);
        Assert.Equal(later.Id, reopened.TeamNotesView.OngoingNoteId);
        Assert.Contains("unavailable or archived", reopened.TeamNotesView.Feedback);
        Assert.True(reopened.OpenOwnedNote(NoteScope.Team, later.Id));
        Assert.True(reopened.TeamNotesView.SelectedNote!.Archived);
        Assert.True(reopened.ArchiveTeamNote(later.Id, false));
        Assert.True(reopened.OpenOngoingNote(NoteScope.Team));
        Assert.Equal(later.Markdown, reopened.TeamNotesView.SelectedNote!.Markdown);
    }

    [Fact]
    public void Pane_percentages_reopen_per_Workspace_and_survive_interleaved_ongoing_and_startup_updates()
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        WorkspaceFixtures.CopyVersionOne(first);
        WorkspaceFixtures.CopyVersionOne(second);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, first));
        var note = app.CreatePersonNote("avery@example.com", "Continuing", "Unchanged writing")!;
        var before = WorkspaceFixtures.Bytes(first);
        Assert.True(app.SavePaneProportions(28, 38));
        Assert.True(app.NavigatePerson(note.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, note.Id));
        var firstView = settings.GetLastView(first);
        Assert.True(settings.OpenWorkspace(app, second));
        Assert.Equal(new PaneProportions(25, 35), app.PaneProportions);
        Assert.True(app.SavePaneProportions(31, 40));
        Assert.True(app.NavigatePerson("casey@example.com"));
        var secondView = settings.GetLastView(second);
        Assert.True(settings.RestoreWorkspace(app, first));
        Assert.Equal(new PaneProportions(28, 38), app.PaneProportions);
        Assert.True(app.SavePaneProportions(27, 37));
        var reopened = new WorkspaceSettings(Preferences);
        Assert.Equal(new PaneProportions(27, 37), reopened.GetPaneProportions(first.ToUpperInvariant() + "\\"));
        Assert.Equal(new PaneProportions(31, 40), reopened.GetPaneProportions(second));
        Assert.Equal(note.Id, reopened.GetOngoingNote(first, NoteScope.Person, note.OwnerEmail));
        Assert.Equal(firstView, reopened.GetLastView(first.ToUpperInvariant() + "\\"));
        Assert.Equal(secondView, reopened.GetLastView(second));
        Assert.Equal(Path.GetFullPath(first), reopened.LastWorkspacePath);
        AssertWorkspaceBytes(first, before);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("corrupt")]
    [InlineData("version")]
    [InlineData("percent")]
    [InlineData("missing-percent")]
    public void Rejected_pane_preferences_retain_durable_ratios_and_other_entries_until_explicit_retry(string failure)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        Assert.True(app.SavePaneProportions(28, 38));
        var valid = File.ReadAllBytes(Preferences);
        var before = WorkspaceFixtures.Bytes(workspace);
        if (failure != "locked")
        {
            var json = JsonNode.Parse(valid)!;
            if (failure == "version") json["schemaVersion"] = 99;
            if (failure == "percent") json["workspaces"]![0]!["paneProportions"]!["peoplePercent"] = 100;
            if (failure == "missing-percent")
                json["workspaces"]![0]!["paneProportions"]!.AsObject().Remove("profilePercent");
            File.WriteAllText(Preferences, failure == "corrupt" ? "{broken" : json.ToJsonString());
            var invalid = new WorkspaceSettings(Preferences);
            Assert.Contains("pane proportions", invalid.PreferenceError);
            Assert.Null(invalid.GetPaneProportions(workspace));
        }
        var rejected = File.ReadAllBytes(Preferences);
        using (var held = failure == "locked" ? File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            Assert.False(app.SavePaneProportions(30, 40));
            Assert.Contains("NOT saved", app.LayoutPreferenceFeedback);
            Assert.Contains("retry", app.LayoutPreferenceFeedback);
            Assert.Equal(new PaneProportions(28, 38), app.PaneProportions);
            Assert.Equal(rejected, File.ReadAllBytes(Preferences));
            AssertWorkspaceBytes(workspace, before);
        }
        File.WriteAllBytes(Preferences, valid);
        Assert.True(app.SavePaneProportions(30, 40));
        Assert.Null(app.LayoutPreferenceFeedback);
        Assert.Equal(new PaneProportions(30, 40), new WorkspaceSettings(Preferences).GetPaneProportions(workspace));
        Assert.False(app.SavePaneProportions(double.NaN, 35));
        Assert.Equal(new PaneProportions(30, 40), new WorkspaceSettings(Preferences).GetPaneProportions(workspace));
    }

    [Fact]
    public void Unreadable_preferences_keep_manual_Workspace_access_without_claiming_an_ongoing_choice_was_restored()
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using (var seed = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(seed, workspace));
            var first = seed.CreatePersonNote("avery@example.com", "First", "Saved")!;
            seed.CreatePersonNote("avery@example.com", "Other", "");
            Assert.True(seed.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
            Assert.True(seed.DesignateOngoingNote(NoteScope.Person, first.Id));
        }
        var preference = File.ReadAllBytes(Preferences);
        var notes = WorkspaceFixtures.Bytes(workspace);
        using (File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var unreadable = new WorkspaceSettings(Preferences);
            Assert.Contains("could not be read", unreadable.PreferenceError);
            Assert.Null(unreadable.LastWorkspacePath);
            using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), unreadable);
            Assert.False(unreadable.RestoreWorkspace(app));
            Assert.True(unreadable.OpenWorkspace(app, workspace));
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
            Assert.Null(app.PersonNotesView.OngoingNoteId);
            Assert.Null(app.PersonNotesView.SelectedNote);
            Assert.Contains("NOT saved", app.PersonNotesView.Feedback);
            Assert.True(app.OpenOwnedNote(NoteScope.Person, app.PersonNotesView.Notes[0].Id));
        }
        Assert.Equal(preference, File.ReadAllBytes(Preferences));
        AssertWorkspaceBytes(workspace, notes);
    }

    [Theory]
    [InlineData("locked", NoteScope.Person)]
    [InlineData("corrupt", NoteScope.Person)]
    [InlineData("version", NoteScope.Person)]
    [InlineData("invalid-entry", NoteScope.Person)]
    [InlineData("owner", NoteScope.Person)]
    [InlineData("locked", NoteScope.Team)]
    [InlineData("corrupt", NoteScope.Team)]
    [InlineData("version", NoteScope.Team)]
    [InlineData("invalid-entry", NoteScope.Team)]
    [InlineData("owner", NoteScope.Team)]
    public void Failed_designation_keeps_the_previous_durable_choice_and_note_bytes_with_actionable_feedback(string failure, NoteScope scope)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        var first = scope == NoteScope.Person
            ? app.CreatePersonNote("avery@example.com", "Continuing", "Saved")!.Id
            : app.CreateTeamNote("avery@example.com", "Continuing", "Saved")!.Id;
        var other = scope == NoteScope.Person
            ? app.CreatePersonNote("avery@example.com", "Other", "")!.Id
            : app.CreateTeamNote("avery@example.com", "Other", "")!.Id;
        Assert.True(app.SavePaneProportions(28, 38));
        Assert.True(app.OpenOwnedNotes(scope, "avery@example.com"));
        Assert.True(app.DesignateOngoingNote(scope, first));
        Assert.True(app.OpenOwnedNote(scope, other));
        var valid = File.ReadAllBytes(Preferences);
        var workspaceBytes = WorkspaceFixtures.Bytes(workspace);
        if (failure != "locked")
        {
            var json = JsonNode.Parse(valid)!;
            if (failure == "version") json["schemaVersion"] = 99;
            if (failure == "invalid-entry") json["workspaces"]![0]!["ongoingNotes"]![0]!["noteId"] = Guid.Empty.ToString();
            if (failure == "owner") json["workspaces"]![0]!["ongoingNotes"]![0]!["ownerEmail"] = "Someone <avery@example.com>";
            File.WriteAllText(Preferences, failure == "corrupt" ? "{broken" : json.ToJsonString());
        }
        var rejected = File.ReadAllBytes(Preferences);
        using (var held = failure == "locked" ? File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            Assert.False(app.DesignateOngoingNote(scope, other));
            Assert.Equal(other, app.GetOwnedNotesView(scope).SelectedNote!.Id);
            Assert.Equal(first, app.GetOwnedNotesView(scope).OngoingNoteId);
            Assert.Contains("NOT saved", app.GetOwnedNotesView(scope).Feedback);
            Assert.Equal(rejected, File.ReadAllBytes(Preferences));
            AssertWorkspaceBytes(workspace, workspaceBytes);
        }
        File.WriteAllBytes(Preferences, valid);
        using (var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(Preferences)))
        {
            Assert.True(reopened.OpenWorkspace(workspace));
            Assert.True(reopened.OpenOwnedNotes(scope, "avery@example.com"));
            Assert.Equal(first, reopened.GetOwnedNotesView(scope).SelectedNote!.Id);
        }
        Assert.True(app.DesignateOngoingNote(scope, other), app.GetOwnedNotesView(scope).Feedback);
        var durable = new WorkspaceSettings(Preferences);
        Assert.Equal(other, durable.GetOngoingNote(workspace, scope, "avery@example.com"));
        Assert.Equal(new PaneProportions(28, 38), durable.GetPaneProportions(workspace));
    }

    [Fact]
    public void Team_designation_reopens_by_owner_scope_and_GUID_without_mutating_writing_or_pane_preferences()
    {
        var workspace = Path.Combine(root, "workspace");
        var second = Path.Combine(root, "second");
        WorkspaceFixtures.CopyVersionOne(workspace);
        WorkspaceFixtures.CopyVersionOne(second);
        var settings = new WorkspaceSettings(Preferences);
        Guid ongoing;
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(app, workspace));
            var person = app.CreatePersonNote("avery@example.com", "Person", "Not Team writing")!;
            var team = app.CreateTeamNote("avery@example.com", "Continuing", "Unchanged Team writing")!;
            app.CreateTeamNote("avery@example.com", "Secondary empty note", "");
            ongoing = team.Id;
            Assert.True(app.SavePaneProportions(28, 38));
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, person.OwnerEmail));
            Assert.True(app.DesignateOngoingNote(NoteScope.Person, person.Id));
            Assert.True(app.OpenOwnedNotes(NoteScope.Team, "AVERY@example.com"));
            Assert.Null(app.TeamNotesView.SelectedNote);
            var before = WorkspaceFixtures.Bytes(workspace);
            Assert.True(app.DesignateOngoingNote(NoteScope.Team, team.Id));
            AssertWorkspaceBytes(workspace, before);
            Assert.True(settings.OpenWorkspace(app, second));
            var secondNote = app.CreateTeamNote(team.OwnerEmail, "Second Workspace", "")!;
            Assert.True(app.OpenOwnedNotes(NoteScope.Team, team.OwnerEmail));
            Assert.True(app.DesignateOngoingNote(NoteScope.Team, secondNote.Id));
            Assert.True(settings.RestoreWorkspace(app, workspace));
            Assert.True(app.EditTeamNote(team.Id, "Renamed continuing note", team.Markdown));
            Assert.Equal(person, Assert.Single(app.GetPersonNotes(person.OwnerEmail), n => n.Id == person.Id));
        }
        var durable = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), durable);
        Assert.True(durable.RestoreWorkspace(reopened));
        Assert.True(reopened.OpenOwnedNotes(NoteScope.Team, "avery@example.com"));
        Assert.Equal(ongoing, reopened.TeamNotesView.SelectedNote!.Id);
        Assert.Equal("Renamed continuing note", reopened.TeamNotesView.SelectedNote.Title);
        Assert.False(reopened.TeamNotesView.IsEditing);
        Assert.Equal(ongoing, durable.GetOngoingNote(workspace.ToUpperInvariant() + "\\", NoteScope.Team, "AVERY@example.com"));
        Assert.NotEqual(ongoing, durable.GetOngoingNote(workspace, NoteScope.Person, "avery@example.com"));
        Assert.Null(durable.GetOngoingNote(workspace, NoteScope.Team, "casey@example.com"));
        Assert.Equal(new PaneProportions(28, 38), reopened.PaneProportions);
        Assert.True(reopened.ArchiveTeamNote(ongoing));
        Assert.True(reopened.OpenOwnedNotes(NoteScope.Team, "casey@example.com"));
        Assert.True(reopened.OpenOwnedNotes(NoteScope.Team, "avery@example.com"));
        Assert.Null(reopened.TeamNotesView.SelectedNote);
        Assert.Equal(ongoing, reopened.TeamNotesView.OngoingNoteId);
        Assert.Contains("unavailable or archived", reopened.TeamNotesView.Feedback);
        Assert.True(reopened.OpenOwnedNote(NoteScope.Team, ongoing));
        Assert.True(reopened.TeamNotesView.SelectedNote!.Archived);
        Assert.True(reopened.ArchiveTeamNote(ongoing, false));
        Assert.True(reopened.OpenOngoingNote(NoteScope.Team));
        Assert.Equal("Unchanged Team writing", reopened.TeamNotesView.SelectedNote!.Markdown);
    }

    [Theory]
    [InlineData(false, NoteScope.Person)]
    [InlineData(true, NoteScope.Person)]
    [InlineData(false, NoteScope.Team)]
    [InlineData(true, NoteScope.Team)]
    public void Unavailable_ongoing_choice_is_retained_without_selecting_or_creating_replacement_writing(bool archived, NoteScope scope)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        var note = scope == NoteScope.Person
            ? app.CreatePersonNote("avery@example.com", "Continuing", "Retained")!.Id
            : app.CreateTeamNote("avery@example.com", "Continuing", "Retained")!.Id;
        var other = scope == NoteScope.Person
            ? app.CreatePersonNote("avery@example.com", "Other", "")!.Id
            : app.CreateTeamNote("avery@example.com", "Other", "")!.Id;
        Assert.True(app.OpenOwnedNotes(scope, "avery@example.com"));
        Assert.True(app.DesignateOngoingNote(scope, note));
        var preference = File.ReadAllBytes(Preferences);
        bool Archive(IApplicationWorkflow workflow, bool value) => scope == NoteScope.Person
            ? workflow.ArchivePersonNote(note, value) : workflow.ArchiveTeamNote(note, value);
        if (archived) Assert.True(Archive(app, true));
        else
        {
            File.Delete(Path.Combine(workspace, scope == NoteScope.Person ? "notes" : "team-notes", note + ".md"));
            Assert.True(app.RefreshWorkspace(), app.Status);
        }
        Assert.Null(app.GetOwnedNotesView(scope).SelectedNote);
        Assert.Contains("unavailable or archived", app.GetOwnedNotesView(scope).Feedback);
        Assert.Equal(note, settings.GetOngoingNote(workspace, scope, "avery@example.com"));
        Assert.Equal(preference, File.ReadAllBytes(Preferences));
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(Preferences));
        Assert.True(reopened.OpenWorkspace(workspace), reopened.Status);
        Assert.True(reopened.OpenOwnedNotes(scope, "avery@example.com"));
        Assert.Null(reopened.GetOwnedNotesView(scope).SelectedNote);
        Assert.Contains("unavailable or archived", reopened.GetOwnedNotesView(scope).Feedback);
        Assert.True(reopened.OpenOwnedNote(scope, other));
        Assert.False(reopened.GetOwnedNotesView(scope).IsOngoing);
        if (archived)
        {
            Assert.True(reopened.OpenOwnedNote(scope, note));
            Assert.True(reopened.GetOwnedNotesView(scope).SelectedNote!.Archived);
            Assert.True(Archive(reopened, false));
            Assert.True(reopened.OpenOngoingNote(scope));
            Assert.Equal(note, reopened.GetOwnedNotesView(scope).SelectedNote!.Id);
        }
        var retained = reopened.GetOwnedNotesView(scope).Notes.Single(n => n.Id == other);
        Assert.True(reopened.DesignateOngoingNote(scope, other));
        Assert.Equal(retained, reopened.GetOwnedNotesView(scope).SelectedNote);
    }

    [Fact]
    public void Ongoing_designation_extends_legacy_preferences_and_reopens_by_identity_without_changing_Workspace_writing()
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        WorkspaceFixtures.CopyVersionOne(first);
        WorkspaceFixtures.CopyVersionOne(second);
        Directory.CreateDirectory(Path.GetDirectoryName(Preferences)!);
        File.WriteAllText(Preferences, new JsonObject { ["schemaVersion"] = 1, ["lastWorkspacePath"] = first }.ToJsonString());
        var settings = new WorkspaceSettings(Preferences);
        Guid ongoing;
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.RestoreWorkspace(app), app.Status);
            var note = app.CreatePersonNote("avery@example.com", "Ongoing", "Retained **writing**")!;
            var other = app.CreatePersonNote("avery@example.com", "Other", "")!;
            ongoing = note.Id;
            var before = WorkspaceFixtures.Bytes(first);
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
            Assert.True(app.DesignateOngoingNote(NoteScope.Person, note.Id), app.PersonNotesView.Feedback);
            AssertWorkspaceBytes(first, before);
            Assert.True(settings.OpenWorkspace(app, second), app.Status);
            var secondNote = app.CreatePersonNote("avery@example.com", "Different Workspace", "")!;
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
            Assert.True(app.DesignateOngoingNote(NoteScope.Person, secondNote.Id));
            Assert.True(settings.RestoreWorkspace(app, first), app.Status);
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, "AVERY@example.com"));
            Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
            Assert.True(app.EditPersonNote(note.Id, "Renamed without re-designation", note.Markdown));
            Assert.Equal(other, Assert.Single(app.GetPersonNotes("avery@example.com"), n => n.Id == other.Id));
        }
        var reopenedSettings = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), reopenedSettings);
        Assert.True(reopenedSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.True(reopened.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
        Assert.Equal(ongoing, reopened.PersonNotesView.SelectedNote!.Id);
        Assert.Equal("Renamed without re-designation", reopened.PersonNotesView.SelectedNote.Title);
        Assert.Equal(ongoing, reopenedSettings.GetOngoingNote(first.ToUpperInvariant() + Path.DirectorySeparatorChar,
            NoteScope.Person, "AVERY@example.com"));
        Assert.Null(reopenedSettings.GetOngoingNote(first, NoteScope.Team, "avery@example.com"));
        Assert.Null(reopenedSettings.GetOngoingNote(first, NoteScope.Person, "casey@example.com"));
    }

    [Fact]
    public void First_run_stays_unopened_and_successful_open_is_restored_after_settings_and_app_reopen()
    {
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.Null(settings.LastWorkspacePath);
            Assert.Null(settings.PreferenceError);
            Assert.False(settings.RestoreWorkspace(app));
            Assert.Equal(WorkspaceCompatibilityState.NotOpened, app.Compatibility.State);
            Assert.Empty(app.People);
            Assert.False(Directory.Exists(root));

            var workspace = Path.Combine(root, "workspace");
            WorkspaceFixtures.CopyVersionOne(workspace);
            Assert.True(settings.OpenWorkspace(app, workspace), app.Status);
            Assert.Equal(workspace, settings.LastWorkspacePath);
            Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        }

        var reopenedSettings = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), reopenedSettings);
        Assert.True(reopenedSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.Null(reopened.PeopleNavigation.SelectedEmail);
        Assert.Null(reopened.PersonNotesView.SelectedNote);
        Assert.Null(reopened.TeamNotesView.SelectedNote);
        Assert.Equal(WorkspaceCompatibilityState.Compatible, reopened.Compatibility.State);
        Assert.Equal(Path.Combine(root, "workspace"), reopened.Compatibility.WorkspacePath);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Missing_workspace_is_not_recreated_and_retry_can_reopen_the_original()
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(app, workspace), app.Status);
            Assert.True(app.NavigatePerson("avery@example.com"));
        }
        var moved = Path.Combine(root, "temporarily-moved");
        Directory.Move(workspace, moved);

        var reopenedSettings = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), reopenedSettings);
        Assert.False(reopenedSettings.RestoreWorkspace(reopened));
        Assert.False(reopened.IsWorkspaceCurrent);
        Assert.Empty(reopened.People);
        Assert.Null(reopened.PeopleNavigation.SelectedEmail);
        Assert.Null(reopened.PersonNotesView.SelectedNote);
        Assert.Contains("could not be opened", reopened.Status);
        Assert.False(Directory.Exists(workspace));
        Assert.Equal(workspace, new WorkspaceSettings(Preferences).LastWorkspacePath);

        Directory.Move(moved, workspace);
        Assert.True(reopenedSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal("avery@example.com", reopened.PeopleNavigation.SelectedEmail);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_or_inaccessible_restore_keeps_the_app_empty_and_preserves_the_successful_preference(bool locked)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(app, workspace), app.Status);
            Assert.True(app.NavigatePerson("avery@example.com"));
        }
        var pointer = Path.Combine(workspace, "workspace.json");
        var original = File.ReadAllBytes(pointer);
        if (!locked)
        {
            var json = JsonNode.Parse(original)!;
            json["schemaVersion"] = 99;
            File.WriteAllText(pointer, json.ToJsonString());
        }
        var bytes = WorkspaceFixtures.Bytes(workspace);
        using (var held = locked ? File.Open(pointer, FileMode.Open, FileAccess.Read, FileShare.None) : null)
        using (var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.False(settings.RestoreWorkspace(reopened));
            Assert.False(reopened.IsWorkspaceCurrent);
            Assert.Empty(reopened.People);
            Assert.Null(reopened.PeopleNavigation.SelectedEmail);
            Assert.Null(reopened.PersonNotesView.SelectedNote);
            Assert.Contains(locked ? "being used" : "Unsupported", reopened.Status);
            Assert.False(settings.OpenWorkspace(reopened, workspace));
            Assert.Equal(workspace, new WorkspaceSettings(Preferences).LastWorkspacePath);
        }
        AssertWorkspaceBytes(workspace, bytes);
        File.WriteAllBytes(pointer, original);
        using var retry = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.RestoreWorkspace(retry), retry.Status);
        Assert.Equal("avery@example.com", retry.PeopleNavigation.SelectedEmail);
        Assert.Equal("Ave", retry.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Only_successful_alternate_opens_replace_the_default_and_manual_folder_creation_still_works()
    {
        var first = Path.Combine(root, "first");
        var invalid = Path.Combine(root, "not-a-folder");
        var alternate = Path.Combine(root, "alternate");
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.True(settings.OpenWorkspace(app, first), app.Status);
            Assert.True(Directory.Exists(first));
            File.WriteAllText(invalid, "Not a Workspace directory");
            Assert.False(settings.OpenWorkspace(app, invalid));
            Assert.Equal(first, new WorkspaceSettings(Preferences).LastWorkspacePath);
            WorkspaceFixtures.CopyVersionOne(alternate);
            Assert.True(settings.OpenWorkspace(app, alternate), app.Status);
            Assert.Equal(alternate, new WorkspaceSettings(Preferences).LastWorkspacePath);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(new WorkspaceSettings(Preferences).RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal(alternate, reopened.Compatibility.WorkspacePath);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"schemaVersion":99,"lastWorkspacePath":"C:\\untrusted"}""")]
    [InlineData("""{"schemaVersion":1,"lastWorkspacePath":"relative-folder"}""")]
    public void Corrupt_or_unwritable_preferences_are_visible_without_claiming_the_new_default_was_saved(string corrupt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Preferences)!);
        File.WriteAllText(Preferences, corrupt);
        var settings = new WorkspaceSettings(Preferences);
        Assert.Null(settings.LastWorkspacePath);
        Assert.Contains("could not be read", settings.PreferenceError);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.False(settings.RestoreWorkspace(app));
        Assert.Equal(WorkspaceCompatibilityState.NotOpened, app.Compatibility.State);
        Assert.Equal(corrupt, File.ReadAllText(Preferences));

        var first = Path.Combine(root, "first");
        Assert.True(settings.OpenWorkspace(app, first), app.Status);
        Assert.Contains("NOT saved", settings.PreferenceError);
        Assert.Equal(corrupt, File.ReadAllText(Preferences));
        File.Delete(Preferences);
        Assert.True(settings.OpenWorkspace(app, first), app.Status);
        Assert.Null(settings.PreferenceError);
        var accepted = File.ReadAllBytes(Preferences);
        var alternate = Path.Combine(root, "alternate");
        using (File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(settings.OpenWorkspace(app, alternate), app.Status);
            Assert.True(app.IsWorkspaceCurrent);
            Assert.Equal(alternate, app.Compatibility.WorkspacePath);
            Assert.Equal(first, settings.LastWorkspacePath);
            Assert.Contains("NOT saved", settings.PreferenceError);
            Assert.Equal(accepted, File.ReadAllBytes(Preferences));
            Assert.Equal(first, new WorkspaceSettings(Preferences).LastWorkspacePath);
        }
        Assert.True(settings.OpenWorkspace(app, alternate), app.Status);
        Assert.Null(settings.PreferenceError);
        Assert.Equal(alternate, new WorkspaceSettings(Preferences).LastWorkspacePath);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Preferences)!, "*.pending"));
    }

    [Fact]
    public void An_unusable_settings_location_does_not_prevent_local_open_or_claim_a_saved_preference()
    {
        var settings = new WorkspaceSettings(Path.Combine(root, "invalid\0.json"));
        Assert.Contains("could not be read", settings.PreferenceError);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(settings.OpenWorkspace(app, Path.Combine(root, "workspace")), app.Status);
        Assert.True(app.IsWorkspaceCurrent);
        Assert.Null(settings.LastWorkspacePath);
        Assert.Contains("NOT saved", settings.PreferenceError);
    }

    [Fact]
    public void Explicit_restart_workspace_restore_uses_existing_only_and_updates_the_default_only_on_success()
    {
        var first = Path.Combine(root, "first");
        var restartWorkspace = Path.Combine(root, "restart-workspace");
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.True(settings.OpenWorkspace(app, first), app.Status);
            Assert.False(settings.RestoreWorkspace(app, restartWorkspace));
            Assert.False(Directory.Exists(restartWorkspace));
            Assert.Equal(first, new WorkspaceSettings(Preferences).LastWorkspacePath);
            WorkspaceFixtures.CopyVersionOne(restartWorkspace);
            Assert.True(settings.RestoreWorkspace(app, restartWorkspace), app.Status);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(new WorkspaceSettings(Preferences).RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal(restartWorkspace, reopened.Compatibility.WorkspacePath);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manual_and_existing_only_settings_opens_retain_dirty_profiles_and_the_successful_default(bool restore)
    {
        var first = Path.Combine(root, "first");
        var alternate = Path.Combine(root, "alternate");
        WorkspaceFixtures.CopyVersionOne(first);
        WorkspaceFixtures.CopyVersionOne(alternate);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(settings.OpenWorkspace(app, first), app.Status);
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained on open rejection"), app.Status);
        Assert.True(app.EditPreferredName("avery@example.com", "Independent quick name"), app.Status);
        var draft = app.ProfileEdit;
        var preference = File.ReadAllBytes(Preferences);
        var saved = WorkspaceFixtures.Bytes(first);

        bool OpenAlternate() => restore ? settings.RestoreWorkspace(app, alternate) : settings.OpenWorkspace(app, alternate);
        Assert.False(OpenAlternate());
        Assert.Same(draft, app.ProfileEdit);
        Assert.Equal("Retained on open rejection", app.ProfileEdit!.Draft.JobTitle);
        Assert.Equal("Independent quick name", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal(first, app.Compatibility.WorkspacePath);
        Assert.Contains("Save, Discard or Stay", app.Status);
        Assert.Equal(preference, File.ReadAllBytes(Preferences));
        Assert.Equal(first, new WorkspaceSettings(Preferences).LastWorkspacePath);
        AssertWorkspaceBytes(first, saved);

        Assert.True(app.TryLeaveProfileEdit(ProfileLeaveDecision.Discard), app.Status);
        Assert.True(OpenAlternate(), app.Status);
        Assert.Null(app.ProfileEdit);
        Assert.Equal(alternate, new WorkspaceSettings(Preferences).LastWorkspacePath);
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    private static void AssertWorkspaceBytes(string workspace, Dictionary<string, byte[]> expected)
    {
        var actual = WorkspaceFixtures.Bytes(workspace);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, actual[path]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
