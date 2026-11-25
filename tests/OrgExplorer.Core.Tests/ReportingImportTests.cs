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
    public void Batch_impact_counts_each_Person_once_and_existing_actions_overlap_without_counting_Add_baselines()
    {
        using var app = Open();
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"manager@example.com","displayName":"Manager","managerEmail":null},
            {"email":"changed@example.com","displayName":"Changed","managerEmail":"manager@example.com","isFavorite":true},
            {"email":"former@example.com","displayName":"Former","managerEmail":null,"archived":true},
            {"email":"archived@example.com","displayName":"Archived","managerEmail":null,"archived":true}
            """)).Id));
        var preview = app.PreviewImport(Batch("""
            {"email":"archived@example.com","archived":true},
            {"email":"changed@example.com","managerEmail":null,"archived":true,"isFavorite":false},
            {"email":"manager@example.com"},
            {"email":"new@example.com","displayName":"New","managerEmail":"manager@example.com","archived":true,"isFavorite":true},
            {"email":"former@example.com","archived":false,"isFavorite":true}
            """, 2));
        Assert.True(preview.CanApply, app.Status);
        var impact = Assert.IsType<ImportBatchImpact>(preview.Impact);
        Assert.Equal(new ImportBatchImpact(1, 2, 2, 1, 1, 1, 0, 2), impact);
        Assert.Equal(new[] { "changed@example.com", "new@example.com", "former@example.com" },
            preview.People.Where(p => p.IsNew || p.Changes.Count > 0).Select(p => p.Email));
        Assert.Equal(new[] { "archived@example.com", "manager@example.com" },
            preview.People.Where(p => !p.IsNew && p.Changes.Count == 0).Select(p => p.Email));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.True(app.GetRelationships("changed@example.com")!.Person.Archived);
        Assert.False(app.GetRelationships("former@example.com")!.Person.Archived);
    }

    [Fact]
    public void Ordinary_unchanged_batch_has_zero_Person_changes_but_retains_Apply_and_supplied_Observations_unlike_replay()
    {
        using var app = Open();
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"same@example.com","displayName":"Same","managerEmail":null},
            {"email":"email-only@example.com","displayName":"Email only","managerEmail":null}
            """)).Id));
        var emailOnlyBefore = app.GetProfileTimeline("email-only@example.com")!.Observations.ToArray();
        var input = Batch("""
            {"email":"same@example.com","displayName":"Same","managerEmail":null,"photoPath":null,"isFavorite":false,"archived":false},
            {"email":"email-only@example.com"}
            """, 2);
        var preview = app.PreviewImport(input);
        Assert.True(preview.CanApply);
        Assert.False(preview.IsReplay);
        Assert.Equal(new ImportBatchImpact(0, 0, 2, 0, 0, 0, 0, 0), preview.Impact);
        Assert.All(preview.People, p => Assert.Empty(p.Changes));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        var timeline = app.GetProfileTimeline("same@example.com")!;
        Assert.Empty(timeline.Changes);
        Assert.Equal(new[] { "displayName", "managerEmail", "photoPath", "isFavorite", "archived" },
            timeline.Observations.Where(o => o.BatchId == Guid.Parse("50000000-0000-4000-8000-000000000002")).Select(o => o.Field));
        Assert.Equal(emailOnlyBefore, app.GetProfileTimeline("email-only@example.com")!.Observations);
        var replay = app.PreviewImport(input);
        Assert.True(replay.CanApply);
        Assert.True(replay.IsReplay);
        Assert.Null(replay.Impact);
        Assert.Empty(replay.People);
        Assert.True(app.ApplyImport(replay.Id));
        var afterReplay = app.GetProfileTimeline("same@example.com")!;
        Assert.Equal(timeline.Changes, afterReplay.Changes);
        Assert.Equal(timeline.Observations, afterReplay.Observations);
        Assert.Equal(timeline.Confirmations, afterReplay.Confirmations);
    }

    [Theory]
    [InlineData("""{"email":"missing-name@example.com","managerEmail":null}""")]
    [InlineData("""{"email":"broken-graph@example.com","displayName":"Broken","managerEmail":"missing@example.com"}""")]
    public void Invalid_batch_never_reports_complete_impact_or_accepts_a_valid_prefix(string invalidPerson)
    {
        using var app = Open();
        var preview = app.PreviewImport(Batch("""
            {"email":"valid-prefix@example.com","displayName":"Valid","managerEmail":null},
            """ + invalidPerson));
        Assert.False(preview.CanApply);
        Assert.NotEmpty(preview.Errors);
        Assert.Null(preview.Impact);
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Empty(app.People);
    }

    [Fact]
    public void Preview_groups_complete_changed_fields_once_per_Person_in_input_order()
    {
        using var app = Open();
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"avery@example.com","displayName":"Avery","managerEmail":null,"officeState":"Oregon"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null}
            """)).Id));
        var preview = app.PreviewImport(Batch("""
            {"email":"MORGAN@example.com","officeCountry":"USA"},
            {"email":"avery@example.com","displayName":"Avery","officeState":"Washington","officeCountry":"USA"}
            """, 2));
        Assert.True(preview.CanApply, app.Status);
        Assert.Equal(new[] { "MORGAN@example.com", "avery@example.com" }, preview.People.Select(p => p.Email));
        var person = preview.People[1];
        Assert.False(person.IsNew);
        Assert.Equal(2, person.Changes.Count);
        Assert.Equal(new[] { "officeState", "officeCountry" }, person.Changes.Select(c => c.Field));
        Assert.Equal(new string?[] { "Oregon", null }, person.Changes.Select(c => c.Before));
        Assert.Equal(new[] { "Washington", "USA" }, person.Changes.Select(c => c.After));
        Assert.Empty(person.InitialValues);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.Equal("Washington", app.GetRelationships("avery@example.com")!.Person.OfficeState);
    }

    [Fact]
    public void New_Person_preview_discloses_supplied_baseline_without_existing_value_changes()
    {
        using var app = Open();
        var preview = app.PreviewImport(Batch("""
            {"email":"new@example.com","displayName":"New Person","managerEmail":null,
             "officeCountry":"USA","department":null,"photoPath":null,"isFavorite":false,"archived":true}
            """));
        Assert.True(preview.CanApply, app.Status);
        var person = Assert.Single(preview.People);
        Assert.True(person.IsNew);
        Assert.Empty(person.Changes);
        Assert.Equal(new[] { "photoPath", "displayName", "department", "officeCountry", "managerEmail", "archived", "isFavorite" },
            person.InitialValues.Select(v => v.Field));
        Assert.Equal(new string?[] { "No photo", "New Person", null, "USA", null, "Archived", "Unmarked" },
            person.InitialValues.Select(v => v.Value));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.True(Assert.Single(app.People).Archived);
        Assert.False(Assert.Single(app.People).IsFavorite);
    }

    [Fact]
    public void Existing_Person_preview_counts_lifecycle_and_preferences_once_and_retains_full_values_and_clears()
    {
        using var app = Open();
        Assert.True(app.ApplyImport(app.PreviewImport(Batch("""
            {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com",
             "department":"Research","officeBuilding":"Old building","isFavorite":true},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"former@example.com","displayName":"Former","managerEmail":null,"archived":true}
            """)).Id));
        var longValue = new string('X', 600) + "\nSecond line with complete details";
        var input = Batch($$"""
            {"email":"avery@example.com","department":null,"officeBuilding":{{System.Text.Json.JsonSerializer.Serialize(longValue)}},
             "managerEmail":null,"archived":true,"isFavorite":false,"displayName":"Avery"},
            {"email":"former@example.com","archived":false},
            {"email":"morgan@example.com","displayName":"Morgan"}
            """, 2);
        var preview = app.PreviewImport(input);
        Assert.True(preview.CanApply, app.Status);
        var changes = preview.People[0].Changes;
        Assert.Equal(5, changes.Count);
        Assert.Equal(new[] { "department", "officeBuilding", "managerEmail", "archived", "isFavorite" },
            changes.Select(c => c.Field));
        Assert.Equal("Research", changes[0].Before);
        Assert.Null(changes[0].After);
        Assert.Equal(longValue, changes[1].After);
        Assert.Equal("morgan@example.com", changes[2].Before);
        Assert.Null(changes[2].After);
        Assert.Equal(ImportChangeKind.Archive, changes[3].Kind);
        Assert.Equal(ImportChangeKind.Favorite, changes[4].Kind);
        Assert.Equal("Marked", changes[4].Before);
        Assert.Equal("Unmarked", changes[4].After);
        Assert.Equal(ImportChangeKind.Reactivate, Assert.Single(preview.People[1].Changes).Kind);
        Assert.Empty(preview.People[2].Changes);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.Equal(longValue, app.GetRelationships("avery@example.com")!.Person.OfficeBuilding);
        var timeline = app.GetProfileTimeline("avery@example.com")!;
        Assert.Equal(5, timeline.Changes.Count);
        var replay = app.PreviewImport(input);
        Assert.True(replay.CanApply);
        Assert.True(replay.IsReplay);
        Assert.Empty(replay.People);
        Assert.Equal("Already applied: exact replay is a no-op.", Assert.Single(replay.Messages));
        Assert.True(app.ApplyImport(replay.Id));
        Assert.Equal(timeline.Changes, app.GetProfileTimeline("avery@example.com")!.Changes);
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
