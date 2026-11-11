using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ProfileSessionTests
{
    internal const string Batch = """
        {"schemaVersion":1,"batchId":"29000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"},
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
         {"email":"riley@example.com","displayName":"Riley","managerEmail":null}]}
        """;

    [Fact]
    public void Profile_session_stages_the_complete_change_and_cancel_leaves_saved_data_and_history_unchanged()
    {
        using var app = Open();
        Assert.Null(app.ProfileEdit);
        var saved = app.GetRelationships("avery@example.com")!.Person;
        var observations = app.GetProfileTimeline(saved.Email)!.Observations.Count;
        Assert.True(app.BeginProfileEdit(saved.Email));
        Assert.False(app.ProfileEdit!.IsDirty);
        Assert.True(app.StageProfileText(PersonTextField.DirectoryName, "Draft Avery"));
        Assert.True(app.StageProfileManager("riley@example.com"));
        Assert.True(app.StageProfilePhoto(Path.GetFullPath("unavailable.png")));
        Assert.True(app.ProfileEdit!.IsDirty);
        Assert.Equal(saved.Email, app.ProfileEdit.Basis.Email);
        Assert.Equal("Draft Avery", app.ProfileEdit.Draft.DirectoryName);
        Assert.Equal("riley@example.com", app.ProfileEdit.Draft.ManagerEmail);
        Assert.Equal(saved, app.GetRelationships(saved.Email)!.Person);
        Assert.Equal(observations, app.GetProfileTimeline(saved.Email)!.Observations.Count);
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => throw new InvalidOperationException("Must not restart")));
        app.CancelProfileEdit();
        Assert.Null(app.ProfileEdit);
        Assert.True(app.CanSafelyClose);
        Assert.Equal(saved, app.GetRelationships(saved.Email)!.Person);
        Assert.Equal(observations, app.GetProfileTimeline(saved.Email)!.Observations.Count);
    }

    [Fact]
    public void Save_publishes_all_fields_and_reporting_history_without_reverting_independent_quick_changes()
    {
        using var app = Open();
        const string email = "avery@example.com";
        Assert.True(app.BeginProfileEdit(email));
        (PersonTextField Field, string? Value)[] edits =
        [
            (PersonTextField.DirectoryName, "Avery Updated"), (PersonTextField.Alias, "avery"),
            (PersonTextField.JobTitle, null), (PersonTextField.Department, "Research"),
            (PersonTextField.OfficeBuilding, "Building A"), (PersonTextField.OfficeNumber, "007"),
            (PersonTextField.OfficeState, "Washington"), (PersonTextField.OfficeCountry, "United States"),
            (PersonTextField.LinkedInProfileUrl, "https://www.linkedin.com/in/avery")
        ];
        foreach (var (field, value) in edits) Assert.True(app.StageProfileText(field, value));
        Assert.True(app.StageProfileManager("riley@example.com"));
        Assert.True(app.EditPreferredName(email, "My Avery"));
        Assert.True(app.EditFavorite(email, true));
        var note = app.CreatePersonNote(email, "Private context", "");
        Assert.NotNull(note);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.Null(app.ProfileEdit);
        Assert.Equal(new PersonProfile(email, "Avery Updated", "My Avery", "riley@example.com")
        {
            Alias = "avery", Department = "Research", OfficeBuilding = "Building A", OfficeNumber = "007",
            OfficeState = "Washington", OfficeCountry = "United States",
            LinkedInProfileUrl = "https://www.linkedin.com/in/avery", IsFavorite = true
        }, app.GetRelationships(email)!.Person);
        Assert.Equal("Private context", Assert.Single(app.GetPersonNotes(email)).Title);
        Assert.Contains(app.GetProfileTimeline(email)!.Changes, c => c.Field == "managerEmail");
        Assert.Equal("morgan@example.com", Assert.Single(Assert.Single(app.GetHistoricalTeamContexts(email)).ReportingChain).Email);
        Assert.True(app.CanSafelyClose);
    }

    [Theory]
    [InlineData(ProfileLeaveDecision.Stay, false, "Engineer")]
    [InlineData(ProfileLeaveDecision.Discard, true, "Engineer")]
    [InlineData(ProfileLeaveDecision.Save, true, "Principal")]
    public void Leave_decision_is_explicit_and_only_success_allows_the_requested_exit(
        ProfileLeaveDecision decision, bool canLeave, string savedTitle)
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.Equal(canLeave, app.TryLeaveProfileEdit(decision));
        Assert.Equal(savedTitle, app.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Equal(!canLeave, app.ProfileEdit is not null);
    }

    [Fact]
    public void Rejected_leave_save_retains_the_owner_and_complete_draft_and_cancel_preserves_note_failures()
    {
        using var app = Open();
        const string email = "avery@example.com";
        Assert.True(app.BeginProfileEdit(email));
        Assert.True(app.StageProfileText(PersonTextField.DirectoryName, "Draft Avery"));
        Assert.True(app.StageProfileManager(email));
        Assert.False(app.TryLeaveProfileEdit(ProfileLeaveDecision.Save));
        Assert.Equal(email, app.ProfileEdit!.Basis.Email);
        Assert.Equal("Draft Avery", app.ProfileEdit.Draft.DirectoryName);
        Assert.Equal("Avery", app.GetRelationships(email)!.Person.DirectoryName);
        Assert.False(app.BeginProfileEdit("riley@example.com"));
        var note = app.CreatePersonNote(email, "Note", "")!;
        Assert.False(app.EditPersonNote(note.Id, " ", "Retained note"));
        app.CancelProfileEdit();
        Assert.False(app.CanSafelyClose);
        Assert.Contains("note", app.LocalSaveError!, StringComparison.OrdinalIgnoreCase);
        app.DiscardPersonChanges(email);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("note", app.LocalSaveError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workspace_switch_cannot_bypass_a_dirty_session_and_a_clean_open_returns_to_view()
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained"));
        Assert.False(app.OpenWorkspace("another-fictitious"));
        Assert.Equal("Retained", app.ProfileEdit!.Draft.JobTitle);
        Assert.Equal(3, app.People.Count);
        app.CancelProfileEdit();
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.OpenWorkspace("another-fictitious"));
        Assert.Null(app.ProfileEdit);
    }

    private static WorkspaceViewModel Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        return app;
    }
}
