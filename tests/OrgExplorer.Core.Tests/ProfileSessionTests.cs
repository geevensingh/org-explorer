using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ProfileSessionTests
{
    [Fact]
    public void Current_Team_names_follow_the_reporting_chain_with_the_selected_managers_own_label_first()
    {
        using var app = OpenTeamNameChain();
        SetTeamName(app, "bob@example.com", "XXX");
        SetTeamName(app, "charlie@example.com", "YYY");

        Assert.Equal(new[] { ("XXX", "bob@example.com"), ("YYY", "charlie@example.com") },
            app.GetTeamNames("alice@example.com").Select(s => (s.Name, s.Owner.Email)));
        Assert.Equal(new[] { ("XXX", "bob@example.com"), ("YYY", "charlie@example.com") },
            app.GetTeamNames("bob@example.com").Select(s => (s.Name, s.Owner.Email)));
        Assert.Equal("XXX > YYY", string.Join(" > ", app.GetTeamNames("alice@example.com").Select(s => s.Name)));
        Assert.Equal("XXX > YYY", string.Join(" > ", app.GetTeamNames("bob@example.com").Select(s => s.Name)));
        var root = Assert.Single(app.GetTeamNames("charlie@example.com"));
        Assert.Equal(("YYY", "charlie@example.com"), (root.Name, root.Owner.Email));
        Assert.False(app.CanEditTeamName("alice@example.com"));
        Assert.True(app.BeginProfileEdit("alice@example.com"));
        Assert.Null(app.ProfileEdit!.Draft.TeamName);
        Assert.False(app.StageProfileTeamName("Cannot replace inherited names"));
        app.CancelProfileEdit();
        Assert.Empty(app.GetTeamNames("absent@example.com"));
    }

    [Fact]
    public void Duplicate_Team_names_keep_distinct_owners_and_clearing_an_unnamed_gap_keeps_higher_context()
    {
        using var app = OpenTeamNameChain();
        SetTeamName(app, "bob@example.com", "Platform");
        SetTeamName(app, "charlie@example.com", "Platform");
        var people = new[] { "alice@example.com", "bob@example.com", "charlie@example.com" };
        var histories = people.Select(email => app.GetProfileTimeline(email)!.Changes.ToArray()).ToArray();
        Assert.Equal(new[] { ("Platform", "bob@example.com", "Bob"), ("Platform", "charlie@example.com", "Charlie") },
            app.GetTeamNames("alice@example.com").Select(s => (s.Name, s.Owner.Email, s.Owner.Heading)));

        Assert.True(app.BeginProfileEdit("bob@example.com"));
        Assert.Equal("Platform", app.ProfileEdit!.Draft.TeamName);
        Assert.True(app.StageProfileTeamName("Cancelled"));
        app.CancelProfileEdit();
        SetTeamName(app, "bob@example.com", "Renamed");
        Assert.Equal(new[] { ("Renamed", "bob@example.com"), ("Platform", "charlie@example.com") },
            app.GetTeamNames("alice@example.com").Select(s => (s.Name, s.Owner.Email)));
        Assert.Equal("Platform", app.GetRelationships("charlie@example.com")!.Person.TeamName);
        SetTeamName(app, "bob@example.com", null);
        foreach (var email in new[] { "alice@example.com", "bob@example.com" })
        {
            var segment = Assert.Single(app.GetTeamNames(email));
            Assert.Equal(("Platform", "charlie@example.com"), (segment.Name, segment.Owner.Email));
        }
        SetTeamName(app, "charlie@example.com", null);
        for (var index = 0; index < people.Length; index++)
        {
            Assert.Empty(app.GetTeamNames(people[index]));
            Assert.Equal(histories[index], app.GetProfileTimeline(people[index])!.Changes);
            Assert.Empty(app.GetHistoricalTeamContexts(people[index]));
        }
    }

    [Fact]
    public void Dormant_owned_names_omit_only_the_owner_segment_and_restore_from_the_final_imported_graph()
    {
        using var app = OpenTeamNameChain();
        SetTeamName(app, "bob@example.com", "XXX");
        SetTeamName(app, "charlie@example.com", "YYY");
        Assert.True(app.EditPersonArchived("alice@example.com", true), app.Status);
        Assert.False(app.CanEditTeamName("bob@example.com"));
        var inherited = Assert.Single(app.GetTeamNames("bob@example.com"));
        Assert.Equal(("YYY", "charlie@example.com"), (inherited.Name, inherited.Owner.Email));
        Assert.Equal("XXX", app.GetRelationships("bob@example.com")!.Person.TeamName);
        Assert.Empty(app.GetTeamNames("alice@example.com"));
        Assert.True(app.EditPersonArchived("bob@example.com", true), app.Status);
        Assert.Empty(app.GetTeamNames("bob@example.com"));
        Assert.True(app.EditPersonArchived("bob@example.com", false), app.Status);
        Assert.Equal("YYY", Assert.Single(app.GetTeamNames("bob@example.com")).Name);
        ApplyContextImport(app, """
            {"email":"bob@example.com","managerEmail":"dana@example.com"},
            {"email":"alice@example.com","archived":false,"managerEmail":"dana@example.com"}
            """);
        Assert.Empty(app.GetTeamNames("bob@example.com"));
        Assert.Empty(app.GetTeamNames("charlie@example.com"));
        Assert.Equal("YYY", app.GetRelationships("charlie@example.com")!.Person.TeamName);
        ApplyContextImport(app, """
            {"email":"alice@example.com","managerEmail":"bob@example.com"},
            {"email":"bob@example.com","managerEmail":"charlie@example.com"}
            """);
        Assert.Equal(new[] { ("XXX", "bob@example.com"), ("YYY", "charlie@example.com") },
            app.GetTeamNames("alice@example.com").Select(s => (s.Name, s.Owner.Email)));
    }

    [Fact]
    public void Higher_ancestor_and_direct_moves_recompute_paths_without_changing_frozen_Team_note_evidence()
    {
        using var app = OpenTeamNameChain();
        SetTeamName(app, "bob@example.com", "XXX");
        SetTeamName(app, "charlie@example.com", "YYY");
        var note = app.CreateTeamNote("charlie@example.com", "Context", "# Before move")!;
        Assert.True(app.EditPersonManager("charlie@example.com", "dana@example.com"), app.Status);
        SetTeamName(app, "dana@example.com", "ZZZ");
        Assert.Equal("bob@example.com", app.GetRelationships("alice@example.com")!.Person.ManagerEmail);
        Assert.Equal(new[] { ("XXX", "bob@example.com"), ("YYY", "charlie@example.com"), ("ZZZ", "dana@example.com") },
            app.GetTeamNames("alice@example.com").Select(s => (s.Name, s.Owner.Email)));
        Assert.True(Assert.Single(app.GetTeamNotes("charlie@example.com")).NeedsReview);
        Assert.Equal(note.Id, Assert.Single(app.GetInheritedTeamNotes("alice@example.com")).Note.Id);
        var frozen = Assert.Single(app.GetHistoricalTeamContexts("alice@example.com"));
        Assert.Equal(new[] { "bob@example.com", "charlie@example.com" }, frozen.ReportingChain.Select(p => p.Email));
        Assert.All(frozen.ReportingChain, owner => Assert.Null(owner.TeamName));
        Assert.Equal("# Before move", Assert.Single(frozen.TeamNotes).Markdown);
        SetTeamName(app, "charlie@example.com", "New current name");
        Assert.True(app.EditTeamNote(note.Id, "Context", "# After move"), app.Status);
        var retained = Assert.Single(app.GetHistoricalTeamContexts("alice@example.com"));
        Assert.Equal(frozen.Id, retained.Id);
        Assert.Equal(frozen.ReportingChain, retained.ReportingChain);
        Assert.Equal(frozen.TeamNotes, retained.TeamNotes);
        Assert.Equal(frozen.Transitions, retained.Transitions);
        Assert.True(app.EditPersonManager("alice@example.com", "dana@example.com"), app.Status);
        var current = Assert.Single(app.GetTeamNames("alice@example.com"));
        Assert.Equal(("ZZZ", "dana@example.com"), (current.Name, current.Owner.Email));
        Assert.All(app.GetHistoricalTeamContexts("alice@example.com").SelectMany(c => c.ReportingChain),
            owner => Assert.Null(owner.TeamName));
    }

    private static WorkspaceViewModel OpenTeamNameChain()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        ApplyContextImport(app, """
            {"email":"alice@example.com","displayName":"Alice","managerEmail":"bob@example.com"},
            {"email":"bob@example.com","displayName":"Bob","managerEmail":"charlie@example.com"},
            {"email":"charlie@example.com","displayName":"Charlie","managerEmail":null},
            {"email":"dana@example.com","displayName":"Dana","managerEmail":null}
            """);
        return app;
    }

    private static void SetTeamName(WorkspaceViewModel app, string owner, string? name)
    {
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfileTeamName(name), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
    }

    internal const string Batch = """
        {"schemaVersion":1,"batchId":"29000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"},
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
         {"email":"riley@example.com","displayName":"Riley","managerEmail":null}]}
        """;

    [Fact]
    public void Eligible_owner_can_stage_cancel_set_rename_and_clear_one_private_Team_name_without_history()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        var history = app.GetProfileTimeline(owner)!.Changes.ToArray();
        Assert.True(app.CanEditTeamName(owner));
        Assert.Empty(app.GetTeamNames(owner));
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Platform"));
        Assert.Empty(app.GetTeamNames(owner));
        Assert.False(app.CanSafelyClose);
        app.CancelProfileEdit();
        Assert.Empty(app.GetTeamNames(owner));
        foreach (var name in new[] { "Platform", "Developer experience", null })
        {
            Assert.True(app.BeginProfileEdit(owner));
            Assert.True(app.StageProfileTeamName(name));
            Assert.True(app.SaveProfileEdit(), app.Status);
            if (name is null) Assert.Empty(app.GetTeamNames(owner));
            else
            {
                var segment = Assert.Single(app.GetTeamNames(owner));
                Assert.Equal(name, segment.Name);
                Assert.Equal(owner, segment.Owner.Email);
                Assert.Equal("Morgan", segment.Owner.Heading);
            }
            Assert.Equal(name is null ? [] : new[] { (name, owner) },
                app.GetTeamNames("avery@example.com").Select(s => (s.Name, s.Owner.Email)));
            Assert.Equal(history, app.GetProfileTimeline(owner)!.Changes);
            Assert.Empty(app.GetHistoricalTeamContexts(owner));
            Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
        }
    }

    [Theory]
    [InlineData("Unsaved replacement")]
    [InlineData(null)]
    public void Losing_the_last_report_rejects_a_Team_name_save_and_retains_saved_label_and_complete_draft(string? draft)
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Platform"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName(draft));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "009"));
        Assert.True(app.EditPersonManager("avery@example.com", "riley@example.com"));
        Assert.False(app.CanEditTeamName(owner));
        Assert.Empty(app.GetTeamNames(owner));
        Assert.False(app.SaveProfileEdit());
        Assert.Contains("current direct report", app.LocalSaveError);
        Assert.Equal(draft, app.ProfileEdit!.Draft.TeamName);
        Assert.Equal("009", app.ProfileEdit.Draft.OfficeNumber);
        Assert.Equal("Platform", app.GetRelationships(owner)!.Person.TeamName);
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => throw new InvalidOperationException("Must retain draft")));
        app.CancelProfileEdit();
        Assert.True(app.EditPersonManager("avery@example.com", owner));
        Assert.Equal("Platform", Assert.Single(app.GetTeamNames(owner)).Name);
        Assert.Empty(app.GetTeamNames("riley@example.com"));
    }

    [Fact]
    public void Reporting_history_freezes_Team_notes_but_never_the_private_Team_name()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        Assert.NotNull(app.CreateTeamNote(owner, "Context", "# Before transfer"));
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Private platform label"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.EditPersonManager("avery@example.com", "riley@example.com"));
        var historical = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        var manager = Assert.Single(historical.ReportingChain);
        Assert.Equal(owner, manager.Email);
        Assert.Null(manager.TeamName);
        Assert.Equal("# Before transfer", Assert.Single(historical.TeamNotes).Markdown);
        Assert.Equal("managerEmail", Assert.Single(app.GetProfileTimeline("avery@example.com")!.Changes).Field);
        Assert.Empty(app.GetProfileTimeline(owner)!.Changes);
        Assert.False(Assert.Single(app.GetTeamNotes(owner)).NeedsReview);
    }

    [Fact]
    public void Team_name_eligibility_follows_current_direct_reports_and_archival_not_imported_titles()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        ApplyContextImport(app, """
            {"email":"riley@example.com","jobTitle":"Team Manager","department":"Platform"},
            {"email":"avery@example.com","department":"Platform"}
            """);
        Assert.False(app.CanEditTeamName("riley@example.com"));
        Assert.False(app.CanEditTeamName("avery@example.com"));
        Assert.True(app.BeginProfileEdit("riley@example.com"));
        Assert.False(app.StageProfileTeamName("Not allowed"));
        app.CancelProfileEdit();
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Platform"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        ApplyContextImport(app, """{"email":"avery@example.com","officeNumber":"004"}""");
        Assert.Equal("Platform", Assert.Single(app.GetTeamNames(owner)).Name);
        ApplyContextImport(app, """
            {"email":"avery@example.com","archived":true},
            {"email":"morgan@example.com","archived":true}
            """);
        Assert.False(app.CanEditTeamName(owner));
        Assert.Empty(app.GetTeamNames(owner));
        Assert.Equal("Platform", app.GetRelationships(owner)!.Person.TeamName);
        ApplyContextImport(app, """{"email":"morgan@example.com","archived":false}""");
        Assert.Empty(app.GetTeamNames(owner));
        Assert.False(app.CanEditTeamName(owner));
        ApplyContextImport(app, """{"email":"avery@example.com","archived":false}""");
        Assert.True(app.CanEditTeamName(owner));
        Assert.Equal("Platform", Assert.Single(app.GetTeamNames(owner)).Name);
        var inherited = Assert.Single(app.GetTeamNames("avery@example.com"));
        Assert.Equal(("Platform", owner), (inherited.Name, inherited.Owner.Email));
        Assert.Empty(app.GetTeamNames("riley@example.com"));
    }

    [Theory]
    [InlineData("\"teamName\":\"Imported\"")]
    [InlineData("\"teamName\":null")]
    [InlineData("\"teamNameOperation\":\"clear\"")]
    [InlineData("\"teamNames\":[\"Imported\"]")]
    public void Import_rejects_private_Team_name_values_and_operations_without_changing_owned_context(string property)
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit("morgan@example.com"));
        Assert.True(app.StageProfileTeamName("Private"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        var review = app.PreviewImport($$"""
            {"schemaVersion":1,"batchId":"29000000-0000-4000-8000-000000000099",
             "observedAt":"2026-11-02T17:00:00Z","people":[{"email":"morgan@example.com",{{property}}}]}
            """);
        Assert.False(review.CanApply);
        Assert.Contains(review.Errors, error => error.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
        Assert.False(app.ApplyImport(review.Id));
        Assert.Equal("Private", Assert.Single(app.GetTeamNames("morgan@example.com")).Name);
    }

    [Fact]
    public void Invalid_Team_name_save_retains_the_profile_and_cancel_does_not_discard_independent_note_writing()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        var note = app.CreatePersonNote(owner, "Context", "Saved")!;
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName(" "));
        Assert.False(app.SaveProfileEdit());
        Assert.Equal(" ", app.ProfileEdit!.Draft.TeamName);
        Assert.Contains("teamName", app.LocalSaveError);
        Assert.False(app.EditPersonNote(note.Id, "", "Retained note"));
        app.CancelProfileEdit();
        Assert.Empty(app.GetTeamNames(owner));
        Assert.False(app.CanSafelyClose);
        Assert.Contains("note", app.LocalSaveError!, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyContextImport(WorkspaceViewModel app, string records)
    {
        var review = app.PreviewImport($$"""
            {"schemaVersion":1,"batchId":"{{Guid.NewGuid()}}",
             "observedAt":"2026-11-02T17:00:00Z","people":[{{records}}]}
            """);
        Assert.True(review.CanApply, app.Status);
        Assert.True(app.ApplyImport(review.Id), app.Status);
    }

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

    [Theory]
    [InlineData(ProfileLeaveDecision.Stay, false, false, "Engineer")]
    [InlineData(ProfileLeaveDecision.Discard, false, true, "Engineer")]
    [InlineData(ProfileLeaveDecision.Save, false, true, "Principal")]
    [InlineData(ProfileLeaveDecision.Save, true, false, "Engineer")]
    public void Import_navigation_requires_the_shared_leave_decision_and_retains_rejected_drafts(
        ProfileLeaveDecision decision, bool invalidManager, bool canLeave, string savedTitle)
    {
        using var app = Open();
        const string email = "avery@example.com";
        Assert.True(app.BeginProfileEdit(email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        if (invalidManager) Assert.True(app.StageProfileManager(email));
        Assert.False(app.TryOpenImport());
        Assert.False(app.IsImportOpen);
        Assert.Equal(email, app.ProfileEdit!.Basis.Email);
        Assert.Equal("Principal", app.ProfileEdit.Draft.JobTitle);
        Assert.Contains("Stayed", app.Status);
        Assert.Equal(canLeave, app.TryLeaveProfileEdit(decision));
        Assert.Equal(canLeave, app.TryOpenImport());
        Assert.Equal(canLeave, app.IsImportOpen);
        Assert.Equal(savedTitle, app.GetRelationships(email)!.Person.JobTitle);
        if (!canLeave)
        {
            Assert.Equal(email, app.ProfileEdit!.Basis.Email);
            Assert.Equal("Principal", app.ProfileEdit.Draft.JobTitle);
        }
        else
        {
            app.ShowPeople();
            Assert.Null(app.ProfileEdit);
            Assert.False(app.IsImportOpen);
        }
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Import_navigation_preserves_pending_and_failed_independent_note_writing(NoteScope scope)
    {
        using var app = Open();
        var draft = app.BeginNoteDraft(scope, "avery@example.com")!;
        app.RetainNoteDraft(draft);
        Assert.False(app.TryOpenImport());
        Assert.False(app.IsImportOpen);
        Assert.Contains("Retry or discard", app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "", "Retained private writing"));
        Assert.False(app.TryOpenImport());
        Assert.False(app.IsImportOpen);
        Assert.NotNull(app.SaveNoteDraft(draft, "Context", "Retained private writing"));
        Assert.True(app.TryOpenImport(), app.Status);
        Assert.Equal("Retained private writing", scope == NoteScope.Person
            ? Assert.Single(app.GetPersonNotes(draft.OwnerEmail)).Markdown
            : Assert.Single(app.GetTeamNotes(draft.OwnerEmail)).Markdown);
    }

    [Fact]
    public void Import_navigation_does_not_clear_failed_quick_changes()
    {
        using var app = Open();
        Assert.False(app.EditPreferredName("avery@example.com", " "));
        Assert.False(app.TryOpenImport());
        Assert.False(app.IsImportOpen);
        Assert.NotNull(app.LocalSaveError);
        Assert.Contains("Retry or discard", app.Status);
        Assert.True(app.EditPreferredName("avery@example.com", "My Avery"));
        Assert.True(app.TryOpenImport(), app.Status);
        Assert.Equal("My Avery", app.GetRelationships("avery@example.com")!.Person.PreferredName);
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
        var retained = app.ProfileEdit;
        Assert.True(app.NavigatePerson("riley@example.com"), app.Status);
        Assert.Null(app.ProfileEdit);
        Assert.True(app.NavigatePerson(email), app.Status);
        Assert.Same(retained, app.ProfileEdit);
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
