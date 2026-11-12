using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class PersonNoteTests
{
    [Fact]
    public void Independent_profile_draft_does_not_prevent_reading_or_creating_notes_for_the_same_owner()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var first = app.CreatePersonNote("avery@example.com", "First", "Saved")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
        Assert.True(app.BeginProfileEdit(first.OwnerEmail));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained profile draft"));
        var profile = app.ProfileEdit;
        var created = app.CreatePersonNote(first.OwnerEmail, "Independent new writing", "")!;
        Assert.True(app.OpenOwnedNote(NoteScope.Person, created.Id, editing: true));
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, first.Id));
        Assert.Same(profile, app.ProfileEdit);
        Assert.False(app.CanSafelyClose);
    }

    [Fact]
    public void A_second_existing_note_does_not_turn_the_first_listed_note_into_an_implicit_designation()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var first = app.CreatePersonNote("avery@example.com", "First", "Saved")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
        Assert.Equal(first.Id, app.PersonNotesView.SelectedNote!.Id);
        app.CreatePersonNote(first.OwnerEmail, "Another existing note", "");
        Assert.Null(app.PersonNotesView.OngoingNoteId);
        Assert.Null(app.PersonNotesView.SelectedNote);
        Assert.Contains("Choose", app.PersonNotesView.Feedback);
    }

    [Fact]
    public void Empty_invitation_new_writing_and_secondary_access_are_observable_without_a_permanent_picker()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var notifications = new List<string?>();
        app.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
        Assert.Contains("Write a new note", app.PersonNotesView.Feedback);
        Assert.Empty(app.PersonNotesView.Notes);
        Assert.False(app.PersonNotesView.OtherNotesOpen);
        var created = app.CreatePersonNote("avery@example.com", "New Person note", "")!;
        Assert.True(app.OpenOwnedNote(NoteScope.Person, created.Id, editing: true));
        Assert.True(app.PersonNotesView.IsEditing);
        app.ShowOtherNotes(NoteScope.Person, true);
        Assert.True(app.PersonNotesView.OtherNotesOpen);
        Assert.Equal(created.Id, Assert.Single(app.PersonNotesView.Notes).Id);
        app.ShowOtherNotes(NoteScope.Person, false);
        Assert.True(app.OpenOngoingNote(NoteScope.Person));
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.False(app.PersonNotesView.OtherNotesOpen);
        Assert.Contains(nameof(app.PersonNotesView), notifications);
    }

    [Fact]
    public void Retained_note_draft_rejects_secondary_ongoing_and_owner_navigation_without_changing_the_editor()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var first = app.CreatePersonNote("avery@example.com", "Continuing", "Saved")!;
        var other = app.CreatePersonNote("avery@example.com", "Other", "")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, first.Id));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, first.Id, editing: true));
        var basis = app.BeginNoteDraft(NoteScope.Person, first.OwnerEmail, first.Id)!;
        app.RetainNoteDraft(basis);

        Assert.False(app.OpenOwnedNote(NoteScope.Person, other.Id));
        Assert.False(app.OpenOwnedNotes(NoteScope.Person, null));
        Assert.False(app.DesignateOngoingNote(NoteScope.Person, other.Id));
        Assert.False(app.OpenOngoingNote(NoteScope.Person));
        Assert.Equal(first.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal(first.OwnerEmail, app.PersonNotesView.OwnerEmail);
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.Contains("Retry or discard", app.PersonNotesView.Feedback);
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.SaveNoteDraft(basis, first.Title, "Deliberately saved"));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, other.Id));
    }

    [Fact]
    public void Ambiguous_ongoing_choice_is_explicit_and_other_notes_and_search_open_the_exact_document()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var first = app.CreatePersonNote("avery@example.com", "Continuing context", "Private writing")!;
        var other = app.CreatePersonNote("avery@example.com", "Separately titled empty note", "")!;
        var before = app.GetPersonNotes("avery@example.com").ToArray();

        Assert.True(app.OpenOwnedNotes(NoteScope.Person, "AVERY@example.com"));
        Assert.Null(app.PersonNotesView.SelectedNote);
        Assert.Contains("Choose", app.PersonNotesView.Feedback);
        Assert.Equal(2, app.PersonNotesView.Notes.Count);
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, first.Id));
        Assert.Equal(first.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.False(app.PersonNotesView.OtherNotesOpen);

        Assert.True(app.OpenOwnedNote(NoteScope.Person, other.Id));
        Assert.Equal(other.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal("", app.PersonNotesView.SelectedNote.Markdown);
        Assert.False(app.PersonNotesView.IsOngoing);
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
        Assert.Equal(other.Id, app.PersonNotesView.SelectedNote.Id);
        Assert.True(app.OpenOngoingNote(NoteScope.Person));
        Assert.Equal(first.Id, app.PersonNotesView.SelectedNote!.Id);

        var match = Assert.Single(app.Search("Separately titled"), r => r.Source == SearchSource.PersonNote);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, match.NoteId!.Value));
        Assert.Equal(other.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, other.Id));
        Assert.Equal(other.Id, app.PersonNotesView.OngoingNoteId);
        Assert.Equal(before, app.GetPersonNotes("avery@example.com"));
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("personNotes")]
    [InlineData("teamNotes")]
    [InlineData("noteOperations")]
    [InlineData("createPersonNote")]
    [InlineData("archiveTeamNote")]
    public void Import_note_payloads_and_operations_are_rejected_without_changing_private_Person_notes(string property)
    {
        IApplicationWorkflow app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id);
        var note = app.CreatePersonNote("avery@example.com", "Private context", "Workspace-owned writing")!;
        var invalid = WorkflowTests.Batch.Replace("000000000001", "000000000002")
            .Replace("\"managerEmail\":null", $"\"{property}\":\"Rejected\", \"managerEmail\":null");
        var preview = app.PreviewImport(invalid);
        Assert.False(preview.CanApply);
        Assert.Contains(property, string.Join(" ", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal(note, Assert.Single(app.GetPersonNotes("avery@example.com")));
    }

    [Fact]
    public void Edit_and_archive_preserve_identity_ownership_and_writing_without_inheritance()
    {
        IApplicationWorkflow app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id);
        var original = app.CreatePersonNote("avery@example.com", "Draft", "Before")!;
        Assert.True(app.EditPersonNote(original.Id, "Context", "After **editing**"), app.Status);
        Assert.True(app.ArchivePersonNote(original.Id), app.Status);
        Assert.Empty(app.GetPersonNotes("avery@example.com"));
        var archived = Assert.Single(app.GetPersonNotes("avery@example.com", includeArchived: true));
        Assert.True(archived.Archived);
        Assert.Equal(original.Id, archived.Id);
        Assert.Equal(original.OwnerEmail, archived.OwnerEmail);
        Assert.Equal(original.CreatedAt, archived.CreatedAt);
        Assert.Equal("After **editing**", archived.Markdown);
        Assert.True(app.ArchivePersonNote(original.Id, archived: false));
        Assert.Equal("Context", Assert.Single(app.GetPersonNotes("avery@example.com")).Title);
        Assert.False(app.EditPersonNote(original.Id, " ", "Unsaved"));
        Assert.Contains("nonblank", app.Status);
        Assert.Equal("After **editing**", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Multiple_private_Person_notes_have_stable_ownership_and_do_not_inherit_to_reports()
    {
        IApplicationWorkflow app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        var batch = WorkflowTests.Batch.Replace("\"managerEmail\":null}",
            "\"managerEmail\":null},{\"email\":\"report@example.com\",\"displayName\":\"Report Example\",\"managerEmail\":\"avery@example.com\"}");
        Assert.True(app.ApplyImport(app.PreviewImport(batch).Id));
        var first = app.CreatePersonNote("AVERY@example.com", "Meeting", "# Context\nPrivate writing");
        var second = app.CreatePersonNote("avery@example.com", "Follow-up", "Another document");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("avery@example.com", first.OwnerEmail);
        Assert.Equal(2, app.GetPersonNotes("AVERY@example.com").Count);
        Assert.Empty(app.GetPersonNotes("report@example.com"));
    }
}
