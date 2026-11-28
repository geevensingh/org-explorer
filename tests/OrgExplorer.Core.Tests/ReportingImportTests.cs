using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public class ReportingImportTests
{
    private static IApplicationWorkflow Open()
    {
        var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious"));
        return app;
    }

    private static string Batch(string people, int id = 1) => $$"""
        {"schemaVersion":1,"batchId":"50000000-0000-4000-8000-{{id:000000000000}}",
        "observedAt":"2026-11-01T17:00:00Z","people":[{{people}}]}
        """;

    [Fact]
    public void Accepted_Import_selects_a_branching_Manager_without_leaving_Import_or_remembering_navigation()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.Null(app.PeopleNavigation.SelectedEmail);
        Assert.True(app.TryOpenImport());
        var preview = app.PreviewImport(Batch("""
            {"email":"root@example.com","displayName":"Ancestor","managerEmail":null},
            {"email":"single@example.com","displayName":"Single report","managerEmail":"root@example.com"},
            {"email":"useful@example.com","displayName":"Useful Manager","managerEmail":"single@example.com"},
            {"email":"first@example.com","displayName":"First","managerEmail":"useful@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"useful@example.com"},
            {"email":"former@example.com","displayName":"Former","managerEmail":"root@example.com","archived":true}
            """));
        Assert.True(preview.CanApply, app.Status);
        Assert.Null(app.PeopleNavigation.SelectedEmail);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.Equal("useful@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal("useful@example.com", app.PersonNotesView.OwnerEmail);
        Assert.Equal("useful@example.com", app.TeamNotesView.OwnerEmail);
        Assert.Null(app.ProfileEdit);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.False(app.TeamNotesView.IsEditing);
        Assert.Null(app.PersonNotesView.SelectedNote);
        Assert.Null(app.TeamNotesView.SelectedNote);
        Assert.Null(app.PeopleNavigation.LastValidView);
        Assert.True(app.IsImportOpen);
        Assert.Contains("root@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Contains("single@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Contains("useful@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Equal(6, app.People.Count);
        Assert.Empty(app.GetPersonNotes("useful@example.com", true));
        Assert.Empty(app.GetTeamNotes("useful@example.com", true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automatic_Import_compares_depth_across_all_reporting_branches(bool reverse)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        string[] records =
        [
            """{"email":"a-root@example.com","displayName":"A root","managerEmail":null}""",
            """{"email":"a-one@example.com","displayName":"A one","managerEmail":"a-root@example.com"}""",
            """{"email":"a-two@example.com","displayName":"A two","managerEmail":"a-one@example.com"}""",
            """{"email":"deep@example.com","displayName":"A deep Manager","managerEmail":"a-two@example.com"}""",
            """{"email":"deep-one@example.com","displayName":"Deep one","managerEmail":"deep@example.com"}""",
            """{"email":"deep-two@example.com","displayName":"Deep two","managerEmail":"deep@example.com"}""",
            """{"email":"z-root@example.com","displayName":"Z root","managerEmail":null}""",
            """{"email":"shallow@example.com","displayName":"Z shallow Manager","managerEmail":"z-root@example.com"}""",
            """{"email":"shallow-one@example.com","displayName":"Shallow one","managerEmail":"shallow@example.com"}""",
            """{"email":"shallow-two@example.com","displayName":"Shallow two","managerEmail":"shallow@example.com"}"""
        ];
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(string.Join(",", reverse ? records.Reverse() : records))).Id), app.Status);
        Assert.Equal("shallow@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal("shallow@example.com", app.PersonNotesView.OwnerEmail);
        Assert.Null(app.PeopleNavigation.LastValidView);
    }

    [Theory]
    [InlineData(false, false, "Z@example.com")]
    [InlineData(false, true, "Z@example.com")]
    [InlineData(true, false, "a@example.com")]
    [InlineData(true, true, "a@example.com")]
    public void Automatic_Import_uses_displayed_Preferred_names_then_email_not_larger_team_or_input_order(
        bool sameName, bool reverse, string expected)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        var first = sameName ? "Same" : "Zulu";
        var second = sameName ? "Same" : "Alpha";
        string[] records =
        [
            $$"""{"email":"Z@example.com","displayName":"Zebra","preferredName":"{{second}}","managerEmail":null}""",
            $$"""{"email":"a@example.com","displayName":"Aardvark","preferredName":"{{first}}","managerEmail":null}""",
            """{"email":"a-one@example.com","displayName":"A one","managerEmail":"a@example.com"}""",
            """{"email":"a-two@example.com","displayName":"A two","managerEmail":"a@example.com"}""",
            """{"email":"a-three@example.com","displayName":"A three","managerEmail":"a@example.com"}""",
            """{"email":"z-one@example.com","displayName":"Z one","managerEmail":"Z@example.com"}""",
            """{"email":"z-two@example.com","displayName":"Z two","managerEmail":"Z@example.com"}"""
        ];
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(string.Join(",", reverse ? records.Reverse() : records))).Id), app.Status);
        Assert.Equal(expected, app.PeopleNavigation.SelectedEmail);
        Assert.Equal(expected, app.TeamNotesView.OwnerEmail);
        Assert.Null(app.ProfileEdit);
    }

    [Theory]
    [InlineData("""{"email":"only@example.com","displayName":"Only","managerEmail":null}""", "only@example.com")]
    [InlineData("""
        {"email":"leaf@example.com","displayName":"A leaf","managerEmail":"middle@example.com"},
        {"email":"middle@example.com","displayName":"Middle","managerEmail":"root@example.com"},
        {"email":"root@example.com","displayName":"Z root","managerEmail":null}
        """, "root@example.com")]
    [InlineData("""
        {"email":"z@example.com","displayName":"Same","managerEmail":null},
        {"email":"A@example.com","displayName":"Same","managerEmail":null},
        {"email":"former@example.com","displayName":"A former","managerEmail":null,"archived":true}
        """, "A@example.com")]
    [InlineData("""{"email":"former@example.com","displayName":"Former","managerEmail":null,"archived":true}""", null)]
    [InlineData("", null)]
    public void Automatic_Import_uses_the_shallowest_current_Person_when_no_Manager_qualifies(string records, string? expected)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        if (records.Length > 0)
            Assert.True(app.ApplyImport(app.PreviewImport(Batch(records)).Id), app.Status);
        Assert.Equal(expected, app.PeopleNavigation.SelectedEmail);
        Assert.Equal(expected, app.PersonNotesView.OwnerEmail);
        Assert.Equal(expected, app.TeamNotesView.OwnerEmail);
        Assert.Null(app.ProfileEdit);
        Assert.Null(app.PeopleNavigation.LastValidView);
    }

    [Fact]
    public void Later_accepted_Import_prepares_selection_without_activating_search_or_losing_Import_context()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"former@example.com","displayName":"Needle","managerEmail":null,"archived":true}
            """)).Id));
        Assert.Null(app.PeopleNavigation.SelectedEmail);
        app.ChangePeopleQuery("Needle");
        app.ShowPeopleOverlay(true);
        var highlight = app.PeopleNavigation.HighlightedResult;
        Assert.Equal("former@example.com", highlight!.Person.Email);
        Assert.True(app.TryOpenImport());
        var preview = app.PreviewImport(Batch("""
            {"email":"manager@example.com","displayName":"Useful","managerEmail":null},
            {"email":"first@example.com","displayName":"First","managerEmail":"manager@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"manager@example.com"}
            """, 2));
        Assert.Null(app.PeopleNavigation.SelectedEmail);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.Equal("manager@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal("Needle", app.PeopleNavigation.Query);
        Assert.Same(highlight, app.PeopleNavigation.HighlightedResult);
        Assert.True(app.PeopleNavigation.OverlayOpen);
        Assert.True(app.IsImportOpen);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.Null(app.PeopleNavigation.LastValidView);
        app.ShowPeople();
        Assert.Equal("Needle", app.PeopleNavigation.Query);
        app.ChangePeopleQuery("");
        Assert.Contains("manager@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Equal("manager@example.com", app.PersonNotesView.OwnerEmail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepted_Import_preserves_deliberate_current_or_Archived_Person_reading(bool archived)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"reader@example.com","displayName":"Reader","managerEmail":null},
            {"email":"former@example.com","displayName":"Former","managerEmail":null,"archived":true}
            """)).Id));
        var email = archived ? "former@example.com" : "reader@example.com";
        var note = app.CreatePersonNote(email, "Exact reading", "Owner's writing")!;
        Assert.True(app.NavigatePerson(email));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id));
        var view = app.PeopleNavigation.LastValidView;
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"higher@example.com","displayName":"A higher Manager","managerEmail":null},
            {"email":"first@example.com","displayName":"First","managerEmail":"higher@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"higher@example.com"}
            """, 2)).Id), app.Status);
        Assert.Equal(email, app.PeopleNavigation.SelectedEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal("Owner's writing", app.PersonNotesView.SelectedNote.Markdown);
        Assert.Equal(view, app.PeopleNavigation.LastValidView);
        Assert.Equal(archived, app.GetRelationships(email)!.Person.Archived);
    }

    [Fact]
    public void Rejected_or_cancelled_Import_does_not_select_a_preview_Person()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        var preview = app.PreviewImport(Batch("""{"email":"preview@example.com","displayName":"Preview","managerEmail":null}"""));
        Assert.True(preview.CanApply);
        app.DiscardImportChanges();
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Null(app.PeopleNavigation.SelectedEmail);
        var invalid = app.PreviewImport(Batch("""{"email":"invalid@example.com","managerEmail":"missing@example.com"}""", 2));
        Assert.False(invalid.CanApply);
        Assert.False(app.ApplyImport(invalid.Id));
        Assert.Null(app.PeopleNavigation.SelectedEmail);
        Assert.Empty(app.People);
    }

    [Fact]
    public void Reporting_changes_archival_and_replay_preserve_complete_textual_profiles()
    {
        var app = Open();
        var initial = PersonAttributeTests.CompleteBatch.Replace("\"managerEmail\":null}", """
            "managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null}
            """);
        Assert.True(app.ApplyImport(app.PreviewImport(initial).Id), app.Status);
        Assert.True(app.EditPreferredName("avery@example.com", "My Avery"), app.Status);
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.LinkedInProfileUrl,
            "https://www.linkedin.com/in/avery-local"), app.Status);
        var before = app.GetRelationships("avery@example.com")!.Person;
        var rejected = app.PreviewImport(Batch("""
            {"email":"morgan@example.com","archived":true},
            {"email":"avery@example.com","officeBuilding":"Unsaved Building"}
            """, 2));
        Assert.False(rejected.CanApply);
        Assert.Contains("avery@example.com.managerEmail", string.Join("\n", rejected.Errors));
        Assert.False(app.ApplyImport(rejected.Id));
        Assert.Equal(before, app.GetRelationships("avery@example.com")!.Person);
        var valid = app.PreviewImport(Batch("""
            {"email":"morgan@example.com","archived":true},
            {"email":"AVERY@example.com","managerEmail":null,"officeNumber":"0009C","department":null}
            """, 3));
        Assert.True(valid.CanApply, app.Status);
        Assert.Contains(valid.Changes, c => c.Contains("Manager") && c.Contains("AVERY"));
        Assert.Contains(valid.Changes, c => c.Contains("Office number") && c.Contains("0009C"));
        Assert.Contains(valid.Changes, c => c.Contains("Department") && c.Contains("(clear)"));
        Assert.True(app.ApplyImport(valid.Id), app.Status);
        var expected = before with { ManagerEmail = null, OfficeNumber = "0009C", Department = null };
        Assert.Equal(expected, Assert.Single(app.ReportingRoots).Person);
        var replay = app.PreviewImport(initial);
        Assert.True(replay.CanApply, app.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(app.ApplyImport(replay.Id), app.Status);
        Assert.Equal(expected, app.GetRelationships("avery@example.com")!.Person);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(
            """{"email":"avery@example.com","archived":true}""", 4)).Id), app.Status);
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeCountry, "Canada"), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(
            """{"email":"avery@example.com","officeState":"British Columbia"}""", 5)).Id), app.Status);
        Assert.Empty(app.ReportingRoots);
        expected = expected with { Archived = true, OfficeCountry = "Canada", OfficeState = "British Columbia" };
        Assert.Equal(expected, app.GetRelationships("avery@example.com")!.Person);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(
            """{"email":"avery@example.com","archived":false}""", 6)).Id), app.Status);
        Assert.Equal(expected with { Archived = false }, Assert.Single(app.ReportingRoots).Person);
    }

    [Fact]
    public void Second_record_resolves_fifth_record_Manager_and_archival_requires_explicit_report_changes()
    {
        var app = Open();
        var initial = Batch("""
            {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
            {"email":"priya@example.com","displayName":"Priya","managerEmail":"MORGAN@example.com"},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":"casey@example.com"},
            {"email":"jordan@example.com","displayName":"Jordan","managerEmail":"alex@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"casey@example.com"}
            """);
        var preview = app.PreviewImport(initial);
        Assert.True(preview.CanApply, app.Status);
        Assert.Empty(app.People);
        Assert.True(app.ApplyImport(preview.Id));
        var invalid = app.PreviewImport(Batch("""{"email":"morgan@example.com","archived":true}""", 2));
        Assert.False(invalid.CanApply);
        Assert.Contains("priya@example.com.managerEmail", string.Join("\n", invalid.Errors));
        Assert.False(app.ApplyImport(invalid.Id));
        Assert.Equal(5, app.People.Count);
        var valid = app.PreviewImport(Batch("""
            {"email":"morgan@example.com","archived":true},
            {"email":"priya@example.com","managerEmail":"alex@example.com"}
            """, 3));
        Assert.True(valid.CanApply, app.Status);
        Assert.Contains(valid.Changes, c => c.Contains("Archive"));
        Assert.Contains(valid.Changes, c => c.Contains("Manager") && c.Contains("priya"));
        Assert.True(app.ApplyImport(valid.Id));
        Assert.Equal("alex@example.com", app.People.Single(p => p.Email == "priya@example.com").ManagerEmail);
        Assert.Equal(5, app.People.Count);
    }

    [Fact]
    public void Accepted_replay_is_no_op_even_when_later_changes_make_its_old_graph_invalid()
    {
        var app = Open();
        var initial = Batch("""
            {"email":"manager@example.com","displayName":"Manager","managerEmail":null},
            {"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com"}
            """);
        Assert.True(app.ApplyImport(app.PreviewImport(initial).Id));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"manager@example.com","archived":true},
            {"email":"report@example.com","managerEmail":null}
            """, 2)).Id));
        var replay = app.PreviewImport(initial);
        Assert.True(replay.CanApply, app.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(app.ApplyImport(replay.Id));
        Assert.Null(app.People.Single(p => p.Email == "report@example.com").ManagerEmail);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1,", "", "schemaVersion")]
    [InlineData("\"observedAt\":\"2026-11-01T17:00:00Z\"", "\"observedAt\":42", "observedAt")]
    [InlineData("50000000-0000-4000-8000-000000000001", "not-a-uuid", "batchId")]
    [InlineData("\"email\":\"avery@example.com\"", "\"email\":\"avery@-example.com\"", "email")]
    [InlineData("\"email\":\"avery@example.com\",", "", "email")]
    public void Invalid_scalar_or_missing_required_field_has_actionable_field_error(string from, string to, string field)
    {
        var app = Open();
        var input = Batch("""{"email":"avery@example.com","displayName":"Avery","managerEmail":null}""");
        var preview = app.PreviewImport(input.Replace(from, to));
        Assert.False(preview.CanApply);
        Assert.Contains(field, string.Join("\n", preview.Errors));
        Assert.Empty(app.People);
    }

    [Theory]
    [InlineData("2024-02-29T23:59:59.1234567Z", true)]
    [InlineData("0001-01-01T00:00:00Z", true)]
    [InlineData("9999-12-31T23:59:59Z", true)]
    [InlineData("2026-02-29T00:00:00Z", false)]
    [InlineData("2024-04-31T00:00:00Z", false)]
    [InlineData("2024-01-01T24:00:00Z", false)]
    [InlineData("2024-01-01T00:00:60Z", false)]
    [InlineData("2024-01-01T00:00:00+00:00", false)]
    [InlineData("0000-01-01T00:00:00Z", false)]
    [InlineData("2024-01-01T00:00:00.12345678Z", false)]
    public void Observation_is_real_Gregorian_UTC_in_contract_precision(string timestamp, bool valid)
    {
        var app = Open();
        var preview = app.PreviewImport(Batch("""{"email":"avery@example.com","displayName":"Avery","managerEmail":null}""")
            .Replace("2026-11-01T17:00:00Z", timestamp));
        Assert.Equal(valid, preview.CanApply);
        if (!valid) Assert.Contains("observedAt", string.Join("\n", preview.Errors));
    }

    [Fact]
    public void Logical_replay_is_independent_of_array_order_whitespace_and_machine_culture()
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var app = Open();
            var first = """{"email":"a@example.com","displayName":"Åke","managerEmail":null}""";
            var second = """{"email":"z@example.com","displayName":"Zed","managerEmail":null}""";
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.True(app.ApplyImport(app.PreviewImport(Batch(first + "," + second)).Id));
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            var replay = app.PreviewImport(Batch(second + ",\n " + first));
            Assert.True(replay.CanApply, app.Status);
            Assert.Contains("no-op", Assert.Single(replay.Changes));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public void Reporting_projection_excludes_Archived_people_and_navigation_counts_only_imported_reports()
    {
        var app = Open();
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"manager@example.com","displayName":"Manager","managerEmail":null},
            {"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com"},
            {"email":"former@example.com","displayName":"Former","managerEmail":"manager@example.com","archived":true}
            """)).Id));
        var root = Assert.Single(app.ReportingRoots);
        Assert.Equal("manager@example.com", root.Person.Email);
        Assert.Equal("report@example.com", Assert.Single(root.Reports).Person.Email);
        var context = app.GetRelationships("REPORT@example.com")!;
        Assert.Equal("manager@example.com", context.Manager!.Email);
        Assert.Empty(context.ImportedReports);
        Assert.Single(app.GetRelationships("manager@example.com")!.ImportedReports);
        Assert.NotNull(app.GetRelationships("former@example.com"));
    }

    [Fact]
    public void Email_only_partial_update_has_visible_no_change_preview()
    {
        var app = Open();
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(
            """{"email":"avery@example.com","displayName":"Avery","managerEmail":null}""")).Id));
        var preview = app.PreviewImport(Batch("""{"email":"AVERY@example.com"}""", 2));
        Assert.True(preview.CanApply);
        Assert.Contains("No explicit field changes", Assert.Single(preview.Changes));
        Assert.True(app.ApplyImport(preview.Id));
        Assert.Equal("Avery", Assert.Single(app.People).DirectoryName);
    }

    [Theory]
    [InlineData("""
        {"email":"a@example.com","displayName":"A","managerEmail":"b@example.com"},
        {"email":"b@example.com","displayName":"B","managerEmail":"a@example.com"}
        """, "cycle")]
    [InlineData("""{"email":"a@example.com","displayName":"A","managerEmail":"A@example.com"}""", "self-reporting")]
    [InlineData("""{"email":"a@example.com","displayName":"A","managerEmail":"missing@example.com"}""", "unknown Manager")]
    [InlineData("""
        {"email":"a@example.com","displayName":"A","managerEmail":null},
        {"email":"A@example.com","displayName":"Duplicate","managerEmail":null}
        """, "duplicate Person")]
    [InlineData("""{"email":"a@example.com","displayName":"A","managerEmail":null,"archived":null}""", "archived")]
    [InlineData("""{"email":"a@example.com","displayName":"A","managerEmail":null,"teamNotes":[]}""", "teamNotes")]
    [InlineData("""{"email":"a@example.com","displayName":"A","managerEmail":null,"operations":[]}""", "operations")]
    [InlineData("""{"email":"a@example.com","displayName":"A","managerEmail":null,"email":"b@example.com"}""", "duplicate property")]
    public void Invalid_graph_or_note_operation_blocks_every_record(string records, string error)
    {
        var app = Open();
        var preview = app.PreviewImport(Batch(records));
        Assert.False(preview.CanApply);
        Assert.Contains(error, string.Join("\n", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Empty(app.People);
    }

    [Fact]
    public void Archived_people_stay_archived_on_partial_updates_and_reactivate_with_same_batch_Manager()
    {
        var app = Open();
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"manager@example.com","displayName":"Manager","managerEmail":null,"archived":true},
            {"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com","archived":true}
            """)).Id));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(
            """{"email":"report@example.com","preferredName":"Reference"}""", 2)).Id));
        Assert.Empty(app.ReportingRoots);
        Assert.True(app.GetRelationships("report@example.com")!.Person.Archived);
        Assert.False(app.PreviewImport(Batch("""{"email":"report@example.com","archived":false}""", 3)).CanApply);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"report@example.com","archived":false},
            {"email":"manager@example.com","archived":false}
            """, 4)).Id));
        Assert.Equal("Reference", Assert.Single(Assert.Single(app.ReportingRoots).Reports).Person.Heading);
    }

    [Fact]
    public void Ten_thousand_people_and_a_long_reporting_chain_have_no_artificial_import_cap()
    {
        var app = Open();
        var records = Enumerable.Range(0, 10001).Select(i => new
        {
            email = $"person{i}@example.com",
            displayName = $"Fictitious Person {i}",
            managerEmail = i == 10000 ? null : $"person{i + 1}@example.com"
        }).ToArray();
        var input = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            batchId = "50000000-0000-4000-8000-000000000010",
            observedAt = "2026-11-01T17:00:00Z",
            people = records
        });
        var preview = app.PreviewImport(input);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id));
        Assert.Equal(10001, app.People.Count);
        var root = Assert.Single(app.ReportingRoots);
        Assert.Equal("person10000@example.com", root.Person.Email);
        Assert.Equal("person1@example.com", app.GetRelationships("person0@example.com")!.Manager!.Email);
    }
}
