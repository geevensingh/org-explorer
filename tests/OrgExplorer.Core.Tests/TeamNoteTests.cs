using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class TeamNoteTests
{
    public const string Population = """
        {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000001",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"root@example.com","displayName":"Root Example","managerEmail":null},
        {"email":"manager@example.com","displayName":"Manager Example","managerEmail":"root@example.com"},
        {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"manager@example.com"},
        {"email":"replacement@example.com","displayName":"Replacement Example","managerEmail":null},
        {"email":"lead@example.com","displayName":"Project Lead","jobTitle":"Informal lead","managerEmail":null}]}
        """;

    private static IApplicationWorkflow Open()
    {
        IApplicationWorkflow app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(Population).Id), app.Status);
        return app;
    }

    [Fact]
    public void Inherited_secondary_notes_reach_their_exact_owner_without_bypassing_retained_Person_writing()
    {
        using var app = (WorkspaceViewModel)Open();
        var root = app.CreateTeamNote("root@example.com", "Root ongoing", "Root writing")!;
        var secondary = app.CreateTeamNote("root@example.com", "Root secondary", "")!;
        var manager = app.CreateTeamNote("manager@example.com", "Manager ongoing", "Manager writing")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Team, root.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Team, root.Id));
        Assert.True(app.OpenOwnedNotes(NoteScope.Team, manager.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Team, manager.Id));
        Assert.True(app.EditPersonText(root.OwnerEmail, PersonTextField.Department, "Research"));
        Assert.True(app.NavigatePerson("avery@example.com"));
        var inherited = app.GetInheritedTeamNotes("avery@example.com");
        Assert.Equal(3, inherited.Count);
        Assert.Contains(inherited, n => n.Note.Id == root.Id && n.Owner.Email == root.OwnerEmail);
        Assert.Contains(inherited, n => n.Note.Id == secondary.Id && n.Note.NeedsReview);
        Assert.Contains(inherited, n => n.Note.Id == manager.Id && n.Owner.Email == manager.OwnerEmail);
        var before = app.GetTeamNotes(root.OwnerEmail).ToArray();
        var person = app.CreatePersonNote("avery@example.com", "Private", "Saved")!;
        Assert.True(app.OpenOwnedNote(NoteScope.Person, person.Id, editing: true));
        var basis = app.BeginNoteDraft(NoteScope.Person, person.OwnerEmail, person.Id)!;
        app.RetainNoteDraft(basis);

        Assert.False(app.NavigateInheritedTeamNote(secondary.Id));
        Assert.Equal(person.OwnerEmail, app.PeopleNavigation.SelectedEmail);
        Assert.Equal(person.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.Contains("retry or discard", app.Status);
        app.DiscardUnsavedChanges(person.Id);
        Assert.True(app.NavigateInheritedTeamNote(secondary.Id), app.Status);
        Assert.Equal(root.OwnerEmail, app.PeopleNavigation.SelectedEmail);
        Assert.Equal(root.OwnerEmail, app.TeamNotesView.OwnerEmail);
        Assert.Equal(secondary.Id, app.TeamNotesView.SelectedNote!.Id);
        Assert.False(app.TeamNotesView.IsOngoing);
        Assert.False(app.TeamNotesView.IsEditing);
        Assert.Equal("", app.TeamNotesView.SelectedNote.Markdown);
        Assert.Equal(before, app.GetTeamNotes(root.OwnerEmail));
        Assert.True(app.ConfirmTeamNote(secondary.Id));
        Assert.False(app.GetTeamNotes(root.OwnerEmail).Single(n => n.Id == secondary.Id).NeedsReview);
        Assert.True(app.GetTeamNotes(root.OwnerEmail).Single(n => n.Id == root.Id).NeedsReview);
        Assert.False(app.NavigateInheritedTeamNote(manager.Id));
        Assert.Equal(secondary.Id, app.TeamNotesView.SelectedNote.Id);
        Assert.Contains("unavailable", app.Status);
    }

    [Fact]
    public void Owned_Team_writing_uses_shared_ongoing_interactions_without_redirecting_secondary_or_frozen_search()
    {
        using var app = (WorkspaceViewModel)Open();
        var notifications = new List<string?>();
        app.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Assert.True(app.NavigatePerson("manager@example.com"));
        Assert.Empty(app.TeamNotesView.Notes);
        Assert.Contains("Write a new note", app.TeamNotesView.Feedback);
        Assert.False(app.TeamNotesView.OtherNotesOpen);
        var ongoing = app.CreateTeamNote("manager@example.com", "Continuing", "Ordinary Team writing")!;
        Assert.True(app.OpenOwnedNote(NoteScope.Team, ongoing.Id, editing: true));
        Assert.True(app.TeamNotesView.IsEditing);
        var secondary = app.CreateTeamNote(ongoing.OwnerEmail, "Orchid secondary", "")!;
        Assert.False(app.OpenOngoingNote(NoteScope.Team));
        Assert.Null(app.TeamNotesView.SelectedNote);
        Assert.Contains("Choose", app.TeamNotesView.Feedback);
        app.ShowOtherNotes(NoteScope.Team, true);
        Assert.True(app.TeamNotesView.OtherNotesOpen);
        Assert.True(app.DesignateOngoingNote(NoteScope.Team, ongoing.Id));
        Assert.False(app.TeamNotesView.OtherNotesOpen);
        Assert.False(app.TeamNotesView.IsEditing);
        Assert.True(app.BeginProfileEdit(ongoing.OwnerEmail));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Retained profile"));
        Assert.True(app.OpenOwnedNote(NoteScope.Team, secondary.Id, editing: true));
        Assert.True(app.TeamNotesView.IsEditing);
        Assert.NotNull(app.ProfileEdit);
        app.CancelProfileEdit();
        app.ChangePeopleQuery("Orchid");
        Assert.True(app.NavigateSearchResult(Assert.Single(app.Search("Orchid"), r => r.Source == SearchSource.OwnedTeamNote)));
        Assert.Equal(secondary.Id, app.TeamNotesView.SelectedNote!.Id);
        Assert.False(app.TeamNotesView.IsEditing);
        Assert.False(app.TeamNotesView.IsOngoing);
        Assert.True(app.EditPersonManager("avery@example.com", "replacement@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Contains(capture.TeamNotes, n => n.Id == ongoing.Id);
        Assert.Contains(capture.TeamNotes, n => n.Id == secondary.Id && n.Markdown == "");
        Assert.True(app.DesignateOngoingNote(NoteScope.Team, secondary.Id));
        Assert.True(app.EditTeamNote(secondary.Id, "Later title", "Later current writing"));
        Assert.True(app.EditPreferredName(ongoing.OwnerEmail, "Later owner name"));
        Assert.True(app.EditPersonArchived(ongoing.OwnerEmail, true));
        Assert.True(app.NavigateSearchResult(Assert.Single(app.Search("Orchid"), r => r.Source == SearchSource.HistoricalTeamNote)));
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        var result = app.PeopleNavigation.SelectedResult!;
        Assert.Equal(capture.Id, result.HistoricalContextId);
        Assert.Equal(secondary.Id, result.NoteId);
        Assert.Equal("", result.MatchText);
        Assert.Equal("Manager Example", result.NoteOwner!.Heading);
        Assert.Equal("Orchid secondary", result.HistoricalContext!.TeamNotes.Single(n => n.Id == secondary.Id).Title);
        Assert.Contains(nameof(app.TeamNotesView), notifications);
    }

    [Fact]
    public void Rejected_Team_note_drafts_remain_observable_until_retry_or_note_specific_discard()
    {
        using var app = Open();
        var team = app.CreateTeamNote("manager@example.com", "Team", "Saved writing")!;
        var direct = app.CreatePersonNote("manager@example.com", "Person", "Direct writing")!;
        Assert.False(app.EditTeamNote(team.Id, " ", "Unsaved Team draft"));
        Assert.False(app.EditPersonNote(direct.Id, " ", "Unsaved Person draft"));
        Assert.False(app.CanSafelyClose);
        Assert.Contains("Team note title", app.LocalSaveError);
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.OfficeNumber, "007B"));
        Assert.False(app.CanSafelyClose);
        Assert.True(app.EditTeamNote(team.Id, team.Title, "Retried Team writing"));
        Assert.False(app.CanSafelyClose);
        Assert.DoesNotContain("Team note title", app.LocalSaveError);
        app.DiscardUnsavedChanges(direct.Id);
        Assert.True(app.CanSafelyClose);
        Assert.False(app.EditTeamNote(team.Id, "", "Rejected again"));
        app.DiscardUnsavedChanges(team.Id);
        Assert.True(app.CanSafelyClose);
        Assert.Equal("Retried Team writing", Assert.Single(app.GetTeamNotes("manager@example.com")).Markdown);
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("teamNotes")]
    [InlineData("personNotes")]
    [InlineData("noteOperations")]
    [InlineData("confirmTeamNote")]
    [InlineData("archiveTeamNote")]
    public void Imports_cannot_supply_either_private_note_kind_or_review_operations(string property)
    {
        var app = Open();
        var team = app.CreateTeamNote("manager@example.com", "Team", "Private Team writing")!;
        var direct = app.CreatePersonNote("manager@example.com", "Person", "Private Person writing")!;
        var invalid = Population.Replace("000000000001", "000000000009")
            .Replace("\"displayName\":\"Manager Example\"", $"\"displayName\":\"Manager Example\",\"{property}\":\"Rejected\"");
        var preview = app.PreviewImport(invalid);
        Assert.False(preview.CanApply);
        Assert.Contains(property, string.Join(" ", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal(team, Assert.Single(app.GetTeamNotes("manager@example.com")));
        Assert.Equal(direct, Assert.Single(app.GetPersonNotes("manager@example.com")));
    }

    [Fact]
    public void Reporting_moves_replace_current_inheritance_not_ownership_and_archived_profiles_have_no_current_context()
    {
        var app = Open();
        var former = app.CreateTeamNote("manager@example.com", "Former", "Owned by former Manager")!;
        app.CreateTeamNote("root@example.com", "Root", "Old chain");
        var replacement = app.CreateTeamNote("replacement@example.com", "New", "New chain")!;
        var direct = app.CreatePersonNote("avery@example.com", "Private", "Preserved")!;
        const string move = """
            {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"avery@example.com","managerEmail":"replacement@example.com"}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(move).Id));
        Assert.Equal(replacement.Id, Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.Id);
        Assert.Equal(former, Assert.Single(app.GetTeamNotes("manager@example.com")));
        Assert.Equal(direct, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Assert.True(app.ApplyImport(app.PreviewImport(move.Replace("000000000002", "000000000003")
            .Replace("\"managerEmail\":\"replacement@example.com\"", "\"archived\":true")).Id));
        Assert.Empty(app.GetInheritedTeamNotes("avery@example.com"));
        Assert.Equal(direct, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Assert.Equal(former.OwnerEmail, Assert.Single(app.GetTeamNotes("manager@example.com")).OwnerEmail);
    }

    [Fact]
    public void Role_and_ancestor_reporting_changes_keep_context_visible_for_explicit_review_even_after_reversion()
    {
        var app = Open();
        var note = app.CreateTeamNote("manager@example.com", "Team", "Still useful")!;
        Assert.False(note.NeedsReview);
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.OfficeNumber, "007B"));
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.JobTitle, "Director"));
        Assert.True(Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.NeedsReview);
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.JobTitle, null));
        Assert.True(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.True(app.ConfirmTeamNote(note.Id), app.Status);
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        const string ancestorMove = """
            {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"root@example.com","managerEmail":"replacement@example.com"}]}
            """;
        var preview = app.PreviewImport(ancestorMove);
        Assert.True(preview.CanApply, app.Status);
        Assert.Contains(preview.Changes, c => c.Contains("Team") && c.Contains("manager@example.com") && c.Contains("review"));
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        var stale = Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note;
        Assert.True(stale.NeedsReview);
        Assert.Equal("Still useful", stale.Markdown);
        Assert.True(app.EditTeamNote(note.Id, "Confirmed by editing", "Current context"), app.Status);
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.Department, "Research"));
        Assert.True(app.ArchiveTeamNote(note.Id), app.Status);
        Assert.Empty(app.GetInheritedTeamNotes("avery@example.com"));
        var archived = Assert.Single(app.GetTeamNotes("manager@example.com", true));
        Assert.Equal("Current context", archived.Markdown);
        Assert.True(archived.NeedsReview);
        Assert.True(app.ArchiveTeamNote(note.Id, false));
        Assert.True(Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.NeedsReview);
        Assert.True(app.ConfirmTeamNote(note.Id));
        var retry = app.PreviewImport(ancestorMove);
        Assert.True(app.ApplyImport(retry.Id));
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
    }

    [Fact]
    public void Explicit_Team_notes_inherit_from_every_formal_ancestor_with_owner_attribution_not_Person_notes_or_informal_leads()
    {
        var app = Open();
        var person = app.CreatePersonNote("manager@example.com", "About the Manager", "Not inherited")!;
        var direct = app.CreatePersonNote("avery@example.com", "Private", "Direct writing")!;
        var root = app.CreateTeamNote("ROOT@example.com", "Root context", "# Organization")!;
        var manager = app.CreateTeamNote("manager@example.com", "Manager context", "**Team**")!;
        app.CreateTeamNote("lead@example.com", "Project", "Not formal context");
        var inherited = app.GetInheritedTeamNotes("AVERY@example.com");
        Assert.Equal(new[] { manager.Id, root.Id }, inherited.Select(n => n.Note.Id));
        Assert.Equal(new[] { "Manager Example", "Root Example" }, inherited.Select(n => n.Owner.Heading));
        Assert.Equal("root@example.com", root.OwnerEmail);
        Assert.Empty(app.GetTeamNotes("avery@example.com"));
        Assert.Equal(direct, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Assert.Equal(person, Assert.Single(app.GetPersonNotes("manager@example.com")));
        Assert.Equal(manager.Id, Assert.Single(app.GetTeamNotes("manager@example.com")).Id);
        Assert.Equal(root.Id, Assert.Single(app.GetInheritedTeamNotes("manager@example.com")).Note.Id);
    }
}
