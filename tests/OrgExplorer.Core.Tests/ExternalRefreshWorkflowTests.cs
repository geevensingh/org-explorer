using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ExternalRefreshWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepted_refresh_prepares_the_cross_branch_Person_without_activating_search_or_leaving_Import(bool notesRefresh)
    {
        var workflow = ApplicationWorkflow.CreateInMemory();
        using var app = new WorkspaceViewModel(workflow);
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"former@example.com","displayName":"Needle","managerEmail":null,"archived":true}
            """)).Id));
        app.ChangePeopleQuery("Needle");
        app.ShowPeopleOverlay(true);
        Assert.True(app.TryOpenImport());
        var highlight = app.PeopleNavigation.HighlightedResult;
        Assert.Equal("former@example.com", highlight!.Person.Email);
        Assert.Null(app.PeopleNavigation.SelectedEmail);
        var incoming = workflow.PreviewImport(Batch("""
            {"email":"a-root@example.com","displayName":"A root","managerEmail":null},
            {"email":"a-chain@example.com","displayName":"A chain","managerEmail":"a-root@example.com"},
            {"email":"deep@example.com","displayName":"A deep","managerEmail":"a-chain@example.com"},
            {"email":"deep-one@example.com","displayName":"Deep one","managerEmail":"deep@example.com"},
            {"email":"deep-two@example.com","displayName":"Deep two","managerEmail":"deep@example.com"},
            {"email":"z-root@example.com","displayName":"Z root","managerEmail":null},
            {"email":"shallow@example.com","displayName":"Z shallow","managerEmail":"z-root@example.com"},
            {"email":"shallow-one@example.com","displayName":"Shallow one","managerEmail":"shallow@example.com"},
            {"email":"shallow-two@example.com","displayName":"Shallow two","managerEmail":"shallow@example.com"}
            """));
        Assert.True(incoming.CanApply, string.Join("\n", incoming.Errors));
        Assert.True(workflow.ApplyImport(incoming.Id), workflow.Status);
        Assert.Null(app.PeopleNavigation.SelectedEmail);

        Assert.True(notesRefresh ? app.RefreshPersonNotes() : app.RefreshWorkspace(), app.Status);

        Assert.Equal("shallow@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal("shallow@example.com", app.PersonNotesView.OwnerEmail);
        Assert.Equal("shallow@example.com", app.TeamNotesView.OwnerEmail);
        Assert.Null(app.ProfileEdit);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.False(app.TeamNotesView.IsEditing);
        Assert.Null(app.PersonNotesView.SelectedNote);
        Assert.Null(app.TeamNotesView.SelectedNote);
        Assert.Null(app.PeopleNavigation.LastValidView);
        Assert.Equal("Needle", app.PeopleNavigation.Query);
        Assert.Same(highlight, app.PeopleNavigation.HighlightedResult);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.True(app.PeopleNavigation.OverlayOpen);
        Assert.True(app.IsImportOpen);
        app.ShowPeople();
        Assert.Equal("Needle", app.PeopleNavigation.Query);
        app.ChangePeopleQuery("");
        Assert.Contains("z-root@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Contains("shallow@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Empty(app.GetPersonNotes("shallow@example.com", true));
        Assert.Empty(app.GetTeamNotes("shallow@example.com", true));
    }

    private static string Batch(string people) => $$"""
        {"schemaVersion":1,"batchId":"{{Guid.NewGuid()}}",
         "observedAt":"2026-11-01T17:00:00Z","people":[{{people}}]}
        """;

    [Theory]
    [InlineData(false, NoteScope.Person)]
    [InlineData(false, NoteScope.Team)]
    [InlineData(true, NoteScope.Person)]
    [InlineData(true, NoteScope.Team)]
    public async Task Accepted_refresh_keeps_valid_current_or_Archived_reading_and_exact_independent_failed_writing(
        bool archived, NoteScope scope)
    {
        var workflow = ApplicationWorkflow.CreateInMemory();
        using var app = new WorkspaceViewModel(workflow);
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch($$"""
            {"email":"reader@example.com","displayName":"Reader","managerEmail":null,"archived":{{archived.ToString().ToLowerInvariant()}}}
            """)).Id), app.Status);
        var person = app.CreatePersonNote("reader@example.com", "Person reading", "Saved Person writing")!;
        var team = app.CreateTeamNote("reader@example.com", "Team reading", "Saved Team writing")!;
        Assert.True(app.NavigatePerson("reader@example.com"), app.Status);
        var note = scope == NoteScope.Person ? person.Id : team.Id;
        Assert.True(app.OpenOwnedNote(scope, note, editing: true));
        var incoming = workflow.PreviewImport(Batch("""
            {"email":"higher@example.com","displayName":"A higher Manager","managerEmail":null},
            {"email":"first@example.com","displayName":"First","managerEmail":"higher@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"higher@example.com"}
            """));
        Assert.True(incoming.CanApply, string.Join("\n", incoming.Errors));
        Assert.True(workflow.ApplyImport(incoming.Id), workflow.Status);
        Assert.True(app.BeginProfileEdit("reader@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Exact profile draft"));
        var profile = app.ProfileEdit;
        app.RetainPersonTextDraft(app.GetRelationships("reader@example.com")!.Person,
            PersonTextField.PreferredName, "Exact Preferred-name draft");
        var preferred = app.GetPersonTextDraft("reader@example.com", PersonTextField.PreferredName);
        app.UpdateNoteDraft(scope, note, "", "Exact unsaved note writing");
        Assert.False(await app.FlushNoteDraftsAsync());
        var writing = app.GetRetainedNoteDraft(scope, note);
        Assert.NotNull(writing);
        Assert.Contains("NOT saved", writing.Error);
        var error = app.LocalSaveError;
        Assert.NotNull(error);
        var destination = app.PeopleNavigation.LastValidView;

        Assert.True(app.RefreshWorkspace(), app.Status);

        Assert.Equal("reader@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(archived, app.GetRelationships("reader@example.com")!.Person.Archived);
        Assert.Equal(note, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.Equal("Exact unsaved note writing", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.Equal(writing, app.GetRetainedNoteDraft(scope, note));
        Assert.Same(profile, app.ProfileEdit);
        Assert.Equal("Exact profile draft", app.ProfileEdit!.Draft.OfficeNumber);
        Assert.Same(preferred, app.GetPersonTextDraft("reader@example.com", PersonTextField.PreferredName));
        Assert.Equal(error, app.LocalSaveError);
        Assert.Equal(destination, app.PeopleNavigation.LastValidView);
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Saved Person writing", Assert.Single(app.GetPersonNotes("reader@example.com", true)).Markdown);
        Assert.Equal("Saved Team writing", Assert.Single(app.GetTeamNotes("reader@example.com", true)).Markdown);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("""{"email":"former@example.com","displayName":"Former","managerEmail":null,"archived":true}""", null)]
    [InlineData("""
        {"email":"root@example.com","displayName":"Z root","managerEmail":null},
        {"email":"leaf@example.com","displayName":"A leaf","managerEmail":"root@example.com"}
        """, "root@example.com")]
    public void Accepted_refresh_uses_only_current_people_and_the_shared_single_chain_fallback(string people, string? expected)
    {
        var workflow = ApplicationWorkflow.CreateInMemory();
        using var app = new WorkspaceViewModel(workflow);
        Assert.True(app.OpenWorkspace("fictitious"));
        if (people.Length != 0)
            Assert.True(workflow.ApplyImport(workflow.PreviewImport(Batch(people)).Id), workflow.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(expected, app.PeopleNavigation.SelectedEmail);
        Assert.Equal(expected, app.PersonNotesView.OwnerEmail);
        Assert.Null(app.ProfileEdit);
        Assert.Null(app.PeopleNavigation.LastValidView);
    }

    [Fact]
    public void Shared_refresh_seam_preserves_a_review_and_private_writing_when_nothing_external_changed()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.IsWorkspaceCurrent);
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var note = app.CreatePersonNote("avery@example.com", "Context", "# Private writing")!;
        var preview = app.PreviewImport(WorkflowTests.Batch);
        Assert.True(app.RefreshWorkspace());
        Assert.True(app.ApplyImport(preview.Id));
        Assert.Equal(note, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Assert.Contains("no changes", app.Status);
        Assert.True(app.CanSafelyClose);
        Assert.Null(app.LocalSaveError);
    }

    [Fact]
    public void Rejected_import_validation_does_not_claim_a_failed_local_save_or_block_safe_close()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        app.OpenWorkspace("fictitious");
        var invalid = app.PreviewImport(WorkflowTests.Batch.Replace("\"managerEmail\":null", "\"managerEmail\":\"missing@example.com\""));
        Assert.False(invalid.CanApply);
        Assert.True(app.IsWorkspaceCurrent);
        Assert.True(app.CanSafelyClose);
        Assert.Null(app.LocalSaveError);
        Assert.Contains("unknown Manager", app.Status);
    }

    [Fact]
    public void Rejected_visible_local_drafts_remain_unsafe_until_corrected_or_deliberately_discarded()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id);
        var note = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        Assert.False(app.EditPersonNote(note.Id, "", "Unsaved writing"));
        Assert.False(app.CanSafelyClose);
        Assert.True(app.EditPersonNote(note.Id, "Corrected title", "Retried writing"));
        Assert.True(app.CanSafelyClose);
        Assert.False(app.EditPreferredName("avery@example.com", " "));
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose);
        Assert.Equal("Avery Example", Assert.Single(app.People).Heading);
    }
}
