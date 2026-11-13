using OrgExplorer.Workflow;
using System.Text.Json;
using Xunit;

namespace OrgExplorer.Core.Tests;

public class PersonAttributeTests
{
    internal const string CompleteBatch = """
        {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery Example","preferredName":"Ave",
          "alias":"avery","jobTitle":"Engineer","department":"Research",
          "officeBuilding":"Building A","officeNumber":"007B","officeState":"Washington",
          "officeCountry":"United States","linkedInProfileUrl":"https://www.linkedin.com/in/avery-example",
          "managerEmail":null}]}
        """;

    [Fact]
    public void Cancelling_a_Preferred_name_draft_clears_only_that_field_failure()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        var basis = Assert.Single(app.People);
        var note = app.CreatePersonNote(basis.Email, "Private context", "Saved writing")!;
        Assert.False(app.EditPersonNote(note.Id, "", "Retained note writing"));
        Assert.True(app.BeginProfileEdit(basis.Email));
        Assert.True(app.StageProfileText(PersonTextField.Department, " "));
        Assert.False(app.SaveProfileEdit());
        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.OfficeNumber, " "));
        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, " "));

        app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);

        Assert.Equal(basis, Assert.Single(app.People));
        Assert.DoesNotContain("preferredName", app.LocalSaveError!);
        Assert.Contains("officeNumber", app.LocalSaveError!);
        Assert.Contains("Person note", app.LocalSaveError!);
        Assert.Contains("Profile NOT saved", app.LocalSaveError!);
        Assert.Equal(" ", app.ProfileEdit!.Draft.Department);
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => Assert.Fail("Independent writing must prevent restart.")));
        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, " "));
        Assert.Contains("preferredName", app.LocalSaveError!);
    }

    [Fact]
    public void Complete_textual_profile_is_reviewed_and_imported_without_an_override_layer()
    {
        var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious"));
        var preview = app.PreviewImport(CompleteBatch);
        Assert.True(preview.CanApply, app.Status);
        Assert.Empty(app.People);
        Assert.Contains("Building A", string.Join("\n", preview.Changes));
        Assert.Contains("007B", string.Join("\n", preview.Changes));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.Equal(new PersonProfile("avery@example.com", "Avery Example", "Ave", null)
        {
            Alias = "avery", JobTitle = "Engineer", Department = "Research",
            OfficeBuilding = "Building A", OfficeNumber = "007B", OfficeState = "Washington",
            OfficeCountry = "United States", LinkedInProfileUrl = "https://www.linkedin.com/in/avery-example"
        }, Assert.Single(app.People));
    }

    [Fact]
    public void Every_textual_attribute_is_locally_editable_through_the_shared_workflow()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        (PersonTextField Field, string Value)[] edits =
        [
            (PersonTextField.DirectoryName, "Avery Directory"),
            (PersonTextField.PreferredName, "A"),
            (PersonTextField.Alias, "aexample"),
            (PersonTextField.JobTitle, "Principal Engineer"),
            (PersonTextField.Department, "Platform"),
            (PersonTextField.OfficeBuilding, "North A"),
            (PersonTextField.OfficeNumber, "0009C"),
            (PersonTextField.OfficeState, "British Columbia"),
            (PersonTextField.OfficeCountry, "Canada"),
            (PersonTextField.LinkedInProfileUrl, "https://www.linkedin.com/in/avery-local")
        ];
        foreach (var (field, value) in edits)
            Assert.True(app.EditPersonText("AVERY@EXAMPLE.COM", field, value), app.Status);
        Assert.Equal(new PersonProfile("avery@example.com", "Avery Directory", "A", null)
        {
            Alias = "aexample", JobTitle = "Principal Engineer", Department = "Platform",
            OfficeBuilding = "North A", OfficeNumber = "0009C", OfficeState = "British Columbia",
            OfficeCountry = "Canada", LinkedInProfileUrl = "https://www.linkedin.com/in/avery-local"
        }, Assert.Single(app.People));
        Assert.Equal("Saved locally.", app.Status);
    }

    [Fact]
    public void Profile_projection_never_opens_LinkedIn_until_the_user_deliberately_requests_it()
    {
        var browser = new RecordingBrowser();
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory(browser));
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        Assert.Equal("Ave", Assert.Single(app.People).Heading);
        Assert.Empty(browser.Opened);
        Assert.True(app.OpenLinkedInProfile("AVERY@example.com"), app.Status);
        Assert.Equal("https://www.linkedin.com/in/avery-example", Assert.Single(browser.Opened).AbsoluteUri);
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.LinkedInProfileUrl, null));
        Assert.False(app.OpenLinkedInProfile("avery@example.com"));
        Assert.Contains("No LinkedIn", app.Status);
        Assert.Single(browser.Opened);
    }

    [Fact]
    public void Office_only_import_preserves_other_text_people_and_local_values_until_explicitly_replaced()
    {
        var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch.Replace("\"managerEmail\":null}", """
            "managerEmail":null},{"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null}
            """)).Id));
        Assert.True(app.EditPreferredName("avery@example.com", "My Avery"));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.LinkedInProfileUrl, "https://www.linkedin.com/in/avery-local"));
        var before = app.People.Single(p => p.Email == "avery@example.com");
        var omitted = app.People.Single(p => p.Email == "morgan@example.com");
        const string update = """
            {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"AVERY@example.com","officeNumber":"0009C","department":null}]}
            """;
        var preview = app.PreviewImport(update);
        Assert.True(preview.CanApply, app.Status);
        Assert.Contains("Department Research → (clear)", string.Join("\n", preview.Changes));
        Assert.Equal(before, app.People.Single(p => p.Email == "avery@example.com"));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.Equal(before with { OfficeNumber = "0009C", Department = null },
            app.People.Single(p => p.Email == "avery@example.com"));
        Assert.Equal(omitted, app.People.Single(p => p.Email == "morgan@example.com"));
        var explicitUpdate = update.Replace("000000000002", "000000000003").Replace("""
            "officeNumber":"0009C","department":null
            """, """
            "preferredName":"Imported Avery","linkedInProfileUrl":"https://www.linkedin.com/in/avery-imported"
            """);
        Assert.True(app.ApplyImport(app.PreviewImport(explicitUpdate).Id), app.Status);
        var imported = app.People.Single(p => p.Email == "avery@example.com");
        Assert.Equal("Imported Avery", imported.Heading);
        Assert.Equal("Avery Example", imported.DirectoryName);
        Assert.Equal("https://www.linkedin.com/in/avery-imported", imported.LinkedInProfileUrl);
    }

    [Theory]
    [InlineData(PersonTextField.DirectoryName, "displayName")]
    [InlineData(PersonTextField.PreferredName, "preferredName")]
    [InlineData(PersonTextField.Alias, "alias")]
    [InlineData(PersonTextField.JobTitle, "jobTitle")]
    [InlineData(PersonTextField.Department, "department")]
    [InlineData(PersonTextField.OfficeBuilding, "officeBuilding")]
    [InlineData(PersonTextField.OfficeNumber, "officeNumber")]
    [InlineData(PersonTextField.OfficeState, "officeState")]
    [InlineData(PersonTextField.OfficeCountry, "officeCountry")]
    [InlineData(PersonTextField.LinkedInProfileUrl, "linkedInProfileUrl")]
    public void Each_nullable_text_field_rejects_blank_values_and_can_be_explicitly_cleared(PersonTextField field, string property)
    {
        var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        var saved = Assert.Single(app.People);
        foreach (var invalid in new[] { "", " ", "\r\n\t" })
        {
            Assert.False(app.EditPersonText(saved.Email, field, invalid));
            Assert.Contains(property, app.Status);
            Assert.Equal(saved, Assert.Single(app.People));
            var invalidImport = Partial(property, JsonSerializer.Serialize(invalid));
            var preview = app.PreviewImport(invalidImport);
            Assert.False(preview.CanApply);
            Assert.Contains(property, string.Join("\n", preview.Errors));
            Assert.False(app.ApplyImport(preview.Id));
            Assert.Equal(saved, Assert.Single(app.People));
        }
        foreach (var invalid in new[] { "42", "false", "{}", "[]" })
        {
            var preview = app.PreviewImport(Partial(property, invalid));
            Assert.False(preview.CanApply);
            Assert.Contains(property, string.Join("\n", preview.Errors));
            Assert.Equal(saved, Assert.Single(app.People));
        }
        Assert.True(app.EditPersonText(saved.Email, field, null), app.Status);
        var cleared = Assert.Single(app.People);
        Assert.NotEqual(saved, cleared);
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch.Replace("000000000001", "000000000003")).Id), app.Status);
        Assert.Equal(saved, Assert.Single(app.People));
        Assert.True(app.ApplyImport(app.PreviewImport(Partial(property, "null")).Id), app.Status);
        Assert.Equal(cleared, Assert.Single(app.People));
    }

    [Theory]
    [InlineData("http://www.linkedin.com/in/avery")]
    [InlineData("mailto:avery@example.com")]
    [InlineData("file:///C:/profile")]
    [InlineData("https://")]
    [InlineData("https://www.linkedin.com/in/avery example")]
    [InlineData("https://www.linkedin.com:invalid/in/avery")]
    [InlineData("https://www.linkedin.com\\in\\avery")]
    public void Invalid_links_are_rejected_for_local_edits_and_imports(string url)
    {
        var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        var saved = Assert.Single(app.People);
        Assert.False(app.EditPersonText(saved.Email, PersonTextField.LinkedInProfileUrl, url));
        Assert.Contains("HTTPS", app.Status);
        var preview = app.PreviewImport(Partial("linkedInProfileUrl", JsonSerializer.Serialize(url)));
        Assert.False(preview.CanApply);
        Assert.Contains("linkedInProfileUrl", string.Join("\n", preview.Errors));
        Assert.Equal(saved, Assert.Single(app.People));
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("personNotes")]
    [InlineData("teamNotes")]
    [InlineData("noteOperations")]
    [InlineData("attributes")]
    public void Imported_notes_and_unknown_operations_block_all_other_changes(string property)
    {
        var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        var saved = Assert.Single(app.People);
        var invalid = Partial("officeNumber", "\"0009C\"").Replace("\"officeNumber\"", $"\"{property}\":\"private\", \"officeNumber\"");
        var preview = app.PreviewImport(invalid);
        Assert.False(preview.CanApply);
        Assert.Contains(property, string.Join("\n", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal(saved, Assert.Single(app.People));
    }

    [Fact]
    public void Local_text_edit_invalidates_a_prior_import_preview()
    {
        var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        var preview = app.PreviewImport(Partial("officeNumber", "\"0009C\""));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "0008A"));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal("0008A", Assert.Single(app.People).OfficeNumber);
        Assert.Contains("stale", app.Status);
    }

    [Fact]
    public void Browser_failure_is_visible_without_changing_saved_profile()
    {
        var app = ApplicationWorkflow.CreateInMemory(new UnavailableBrowser());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        var saved = Assert.Single(app.People);
        Assert.False(app.OpenLinkedInProfile(saved.Email));
        Assert.Contains("Browser unavailable", app.Status);
        Assert.Equal(saved, Assert.Single(app.People));
    }

    private static string Partial(string property, string jsonValue) => $$"""
        {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000002",
         "observedAt":"2026-11-02T17:00:00Z","people":[
         {"email":"avery@example.com","{{property}}":{{jsonValue}}}]}
        """;

    private sealed class UnavailableBrowser : IProfileLinkOpener
    {
        public void Open(Uri profileUrl) => throw new InvalidOperationException("Browser unavailable.");
    }

    private sealed class RecordingBrowser : IProfileLinkOpener
    {
        public List<Uri> Opened { get; } = [];
        public void Open(Uri profileUrl) => Opened.Add(profileUrl);
    }
}
