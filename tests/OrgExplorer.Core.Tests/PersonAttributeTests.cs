using OrgExplorer.Workflow;
using System.Text.Json;
using Xunit;

namespace OrgExplorer.Core.Tests;

public class PersonAttributeTests
{
    private sealed class PreferredNameContractClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    internal const string CompleteBatch = """
        {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery Example","preferredName":"Ave",
          "alias":"avery","jobTitle":"Engineer","department":"Research",
          "officeBuilding":"Building A","officeNumber":"007B","officeState":"Washington",
          "officeCountry":"United States","linkedInProfileUrl":"https://www.linkedin.com/in/avery-example",
          "managerEmail":null}]}
        """;

    [Theory]
    [InlineData("Avery Example", null, "Avery Example", null)]
    [InlineData("Avery Example", "Ave", "Avery Example", null)]
    [InlineData("Avery Example", "Avery Example", "AVERY EXAMPLE", null)]
    [InlineData("Avery Example", "Ave", "  aVeRy ExAmPlE\t", null)]
    [InlineData(" \tAvery Example  ", "Ave", "AVERY EXAMPLE", null)]
    [InlineData("Avery Example", "Ave", "Avery  Example", "Avery  Example")]
    [InlineData("Avery Example", "Ave", "Averie Example", "Averie Example")]
    [InlineData("Avery Example", "Ave", "  My AVERY  ", "  My AVERY  ")]
    [InlineData("Andre Example", "Ave", "André Example", "André Example")]
    [InlineData("Avery Example", "Ave", null, null)]
    [InlineData(null, "Ave", "  My AVERY  ", "  My AVERY  ")]
    [InlineData(null, null, "Avery", "Avery")]
    [InlineData(null, "Ave", null, null)]
    public void Explicit_Preferred_name_submission_clears_only_Directory_equivalent_or_empty_text(
        string? directoryName, string? preferredName, string? submitted, string? expected)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        var batch = CompleteBatch.Replace("\"preferredName\":\"Ave\"", "\"preferredName\":" + JsonSerializer.Serialize(preferredName));
        Assert.True(app.ApplyImport(app.PreviewImport(batch).Id), app.Status);
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.DirectoryName, directoryName), app.Status);
        var basis = Assert.Single(app.People);
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, submitted);
        Assert.Equal(basis, Assert.Single(app.People));

        var accepted = Assert.IsType<PersonProfile>(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, submitted));

        Assert.Equal(basis with { PreferredName = expected }, accepted);
        Assert.Equal(accepted, Assert.Single(app.People));
        Assert.Null(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Theory]
    [InlineData("Alex Smith", "Alex Smith")]
    [InlineData("  aLeXaNdEr SmItH  ", null)]
    public void Retained_Preferred_name_submission_compares_the_current_Directory_name_without_resetting_its_base(
        string submitted, string? expected)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.DirectoryName, "Alex Smith"));
        Assert.True(app.NavigatePerson("avery@example.com"));
        var basis = Assert.Single(app.People);
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, submitted);
        var draft = Assert.IsType<RetainedPersonTextDraft>(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        var note = app.CreatePersonNote(basis.Email, "Private context", "Saved writing")!;
        Assert.False(app.EditPersonNote(note.Id, "", "Independent failed writing"));
        Assert.True(app.BeginProfileEdit(basis.Email));
        Assert.True(app.StageProfileText(PersonTextField.Department, "Independent profile draft"));

        Assert.True(app.EditPersonText(basis.Email, PersonTextField.DirectoryName, "Alexander Smith"), app.Status);
        Assert.Equal(draft, app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        var accepted = Assert.IsType<PersonProfile>(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, submitted));

        Assert.Equal(basis with { DirectoryName = "Alexander Smith", PreferredName = expected }, accepted);
        Assert.Null(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        Assert.Equal("Independent profile draft", app.ProfileEdit!.Draft.Department);
        Assert.True(app.HasUnsavedNoteDraft(NoteScope.Person, note.Id));
        Assert.Contains("Person note", app.LocalSaveError!);
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => Assert.Fail("Independent writing must still prevent restart.")));
    }

    [Fact]
    public void Explicit_original_Preferred_name_submission_retains_rejection_without_prior_typing()
    {
        var inner = ApplicationWorkflow.CreateInMemory();
        using var app = new WorkspaceViewModel(inner);
        Assert.True(app.OpenWorkspace("fictitious"));
        var batch = CompleteBatch.Replace("\"Avery Example\"", "\"Avery\"").Replace("\"Ave\"", "\"Avery\"");
        Assert.True(app.ApplyImport(app.PreviewImport(batch).Id));
        var basis = Assert.Single(app.People);
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, basis.PreferredName);
        Assert.Null(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        Assert.True(inner.EditPreferredName(basis.Email, "Delivered Preferred name"));

        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, basis.PreferredName));

        var retained = Assert.IsType<RetainedPersonTextDraft>(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        Assert.Equal(basis, retained.Basis);
        Assert.Equal("Avery", retained.Value);
        Assert.Contains("changed externally", retained.Error!);
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Delivered Preferred name", Assert.Single(app.People).PreferredName);
        app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        Assert.Null(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restoring_the_original_Preferred_name_retires_only_clean_retained_writing(bool useViewModel)
    {
        using IApplicationWorkflow app = useViewModel
            ? new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory())
            : ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        var basis = Assert.Single(app.People);
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, "Unsubmitted");
        Assert.False(app.CanSafelyClose);

        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, basis.PreferredName);

        Assert.True(app.CanSafelyClose, app.Status);
        Assert.True(app.CanEnterImport, app.Status);
        if (app is WorkspaceViewModel model)
        {
            Assert.True(model.NavigatePerson(basis.Email), model.Status);
            Assert.Null(model.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        }
        Assert.Equal(basis, Assert.Single(app.People));
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, "Explicitly saved");
        var accepted = Assert.IsType<PersonProfile>(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Explicitly saved"));
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, basis.PreferredName);
        Assert.False(app.CanSafelyClose);
        app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        basis = accepted;
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, " ");
        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, " "));
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, basis.PreferredName);
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.LocalSaveError);
        app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Original_Preferred_name_typing_during_admitted_work_retires_only_without_a_newer_saved_base(bool saveName) =>
        VerifyOriginalPreferredNameTyping(saveName, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Original_Preferred_name_typing_during_admitted_work_survives_a_delayed_continuation(bool saveName) =>
        VerifyOriginalPreferredNameTyping(saveName, TimeSpan.FromMilliseconds(3200));

    private static async Task VerifyOriginalPreferredNameTyping(bool saveName, TimeSpan continuationDelay)
    {
        var inner = ApplicationWorkflow.CreateInMemory();
        // This contract holds admitted work; real stall behavior is covered separately.
        using var app = new WorkspaceViewModel(inner, timeProvider: new PreferredNameContractClock());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(CompleteBatch).Id));
        var basis = Assert.Single(app.People);
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, "Explicitly submitted");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var saving = app.RunLocalSaveAsync(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The owning test did not release its admitted operation.");
            return saveName
                ? app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Explicitly submitted") is not null
                : app.EditFavorite(basis.Email, true);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (continuationDelay != TimeSpan.Zero) await Task.Delay(continuationDelay);
            Assert.Equal(LocalSavePhase.Pending, app.LocalSave.Phase);
            app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, basis.PreferredName);
            if (saveName)
            {
                var typing = Assert.IsType<RetainedPersonTextDraft>(
                    app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
                Assert.Equal(basis.PreferredName, typing.Value);
                Assert.Equal(basis, typing.Basis);
            }
            Assert.False(app.CanSafelyClose);
        }
        finally
        {
            release.Set();
            Assert.True(await saving.WaitAsync(TimeSpan.FromSeconds(5)), app.Status);
        }
        if (saveName)
        {
            var retained = Assert.IsType<RetainedPersonTextDraft>(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
            Assert.Equal(basis.PreferredName, retained.Value);
            Assert.Equal("Explicitly submitted", retained.Basis.PreferredName);
            Assert.False(app.CanSafelyClose);
            Assert.False(inner.CanSafelyClose);
            app.DiscardPersonTextDraft(retained.Basis, PersonTextField.PreferredName);
        }
        Assert.Null(app.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName));
        Assert.True(app.CanSafelyClose, app.Status);
        Assert.True(app.CanEnterImport, app.Status);
    }

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
