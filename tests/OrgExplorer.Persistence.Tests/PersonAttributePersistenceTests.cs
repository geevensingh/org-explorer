using System.Text.Json.Nodes;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class PersonAttributePersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Batch = """
        {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery Example","preferredName":"Ave",
          "alias":"avery","jobTitle":"Engineer","department":"Research",
          "officeBuilding":"Building A","officeNumber":"007B","officeState":"Washington",
          "officeCountry":"United States","linkedInProfileUrl":"https://www.linkedin.com/in/avery-example",
          "managerEmail":null}]}
        """;

    [Fact]
    public void All_textual_edits_and_an_office_only_import_survive_reopening_without_losing_omissions()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var initial = Batch.Replace("\"managerEmail\":null}", """
            "managerEmail":null},{"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null}
            """);
        Assert.True(app.ApplyImport(app.PreviewImport(initial).Id), app.Status);
        (PersonTextField Field, string Value)[] edits =
        [
            (PersonTextField.DirectoryName, "Avery Directory"), (PersonTextField.PreferredName, "My Avery"),
            (PersonTextField.Alias, "aexample"), (PersonTextField.JobTitle, "Principal Engineer"),
            (PersonTextField.Department, "Platform"), (PersonTextField.OfficeBuilding, "North A"),
            (PersonTextField.OfficeNumber, "0009C"), (PersonTextField.OfficeState, "British Columbia"),
            (PersonTextField.OfficeCountry, "Canada"),
            (PersonTextField.LinkedInProfileUrl, "https://www.linkedin.com/in/avery-local")
        ];
        foreach (var (field, value) in edits)
            Assert.True(app.EditPersonText("AVERY@example.com", field, value), app.Status);
        var expected = new PersonProfile("avery@example.com", "Avery Directory", "My Avery", null)
        {
            Alias = "aexample", JobTitle = "Principal Engineer", Department = "Platform",
            OfficeBuilding = "North A", OfficeNumber = "0009C", OfficeState = "British Columbia",
            OfficeCountry = "Canada", LinkedInProfileUrl = "https://www.linkedin.com/in/avery-local"
        };
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(expected, reopened.People.Single(p => p.Email == "avery@example.com"));
        const string update = """
            {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"AVERY@example.com","officeNumber":"0002B","department":null}]}
            """;
        Assert.True(reopened.ApplyImport(reopened.PreviewImport(update).Id), reopened.Status);
        using var durable = ApplicationWorkflow.CreateFileBacked();
        Assert.True(durable.OpenWorkspace(folder), durable.Status);
        Assert.Equal(expected with { OfficeNumber = "0002B", Department = null },
            durable.People.Single(p => p.Email == "avery@example.com"));
        Assert.Equal(new PersonProfile("morgan@example.com", "Morgan Example", null, null),
            durable.People.Single(p => p.Email == "morgan@example.com"));
        var explicitValues = update.Replace("000000000002", "000000000003").Replace("""
            "officeNumber":"0002B","department":null
            """, """
            "preferredName":"Imported Avery","linkedInProfileUrl":"https://www.linkedin.com/in/avery-imported"
            """);
        Assert.True(durable.ApplyImport(durable.PreviewImport(explicitValues).Id), durable.Status);
        using var final = ApplicationWorkflow.CreateFileBacked();
        Assert.True(final.OpenWorkspace(folder), final.Status);
        Assert.Equal("Imported Avery", final.People.Single(p => p.Email == "avery@example.com").Heading);
        Assert.Equal("https://www.linkedin.com/in/avery-imported",
            final.People.Single(p => p.Email == "avery@example.com").LinkedInProfileUrl);
    }

    [Fact]
    public void Explicit_Preferred_name_save_retries_its_displayed_base_without_reverting_other_quick_context()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var basis = Assert.Single(app.People);
        Assert.True(app.EditFavorite(basis.Email, true), app.Status);
        var personNote = Assert.IsType<PersonNote>(app.CreatePersonNote(basis.Email, "Empty Person context", ""));
        var teamNote = Assert.IsType<TeamNote>(app.CreateTeamNote(basis.Email, "Empty owned Team context", ""));

        using (File.Open(Path.Combine(folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "My Avery"));
        Assert.Contains("NOT saved", app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Ave", Assert.Single(app.People).Heading);
        var accepted = Assert.IsType<PersonProfile>(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "My Avery"));
        Assert.True(app.CanSafelyClose);
        Assert.Equal("My Avery", accepted.Heading);
        Assert.Equal("Avery Example", accepted.DirectoryName);
        Assert.True(accepted.IsFavorite);

        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(accepted, Assert.Single(reopened.People));
        Assert.Equal(personNote, Assert.Single(reopened.GetPersonNotes(basis.Email)));
        Assert.Equal(teamNote, Assert.Single(reopened.GetTeamNotes(basis.Email)));
        Assert.Equal("", reopened.BeginNoteDraft(NoteScope.Person, basis.Email, personNote.Id)!.Markdown);
        Assert.Equal("", reopened.BeginNoteDraft(NoteScope.Team, basis.Email, teamNote.Id)!.Markdown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_Preferred_name_save_and_retry_do_not_overwrite_delivered_quick_context(bool emptyBase)
    {
        using var app = new WorkspaceViewModel(CreateWorkflow());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        if (emptyBase) Assert.True(app.EditPreferredName("avery@example.com", null), app.Status);
        var basis = Assert.Single(app.People);
        using var provider = CreateWorkflow();
        Assert.True(provider.OpenWorkspace(folder), provider.Status);
        Assert.True(provider.EditPreferredName(basis.Email, "Delivered preferred name"), provider.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Retained inline draft"));
        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Retained inline draft"));
        Assert.Contains("changed externally", app.Status);
        Assert.False(app.CanSafelyClose);
        using (var reopened = CreateWorkflow())
        {
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Delivered preferred name", Assert.Single(reopened.People).Heading);
            Assert.Equal("Avery Example", Assert.Single(reopened.People).DirectoryName);
        }
        app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        Assert.NotNull(app.SavePersonTextDraft(Assert.Single(app.People), PersonTextField.PreferredName, null));
        Assert.Equal("Avery Example", Assert.Single(app.People).Heading);
    }

    [Fact]
    public void Cancelling_Preferred_name_preserves_saved_bytes_and_independent_failed_writing()
    {
        using var app = new WorkspaceViewModel(CreateWorkflow());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var basis = Assert.Single(app.People);
        var person = app.CreatePersonNote(basis.Email, "Person writing", "Saved Person")!;
        var team = app.CreateTeamNote(basis.Email, "Team writing", "Saved Team")!;
        var personDraft = app.BeginNoteDraft(NoteScope.Person, basis.Email, person.Id)!;
        var teamDraft = app.BeginNoteDraft(NoteScope.Team, basis.Email, team.Id)!;
        var preview = app.PreviewImport(Batch.Replace("000000000001", "000000000002").Replace("Research", "Reviewed"));
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.BeginProfileEdit(basis.Email));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Retained office draft"));
        using (File.Open(Path.Combine(folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.ApplyImport(preview.Id));
            Assert.Null(app.SaveNoteDraft(personDraft, "Person writing", "Retained Person"));
            Assert.Null(app.SaveNoteDraft(teamDraft, "Team writing", "Retained Team"));
            Assert.False(app.EditFavorite(basis.Email, true));
            Assert.False(app.SaveProfileEdit());
            Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Retained name"));
        }
        var before = WorkspaceFixtures.Bytes(folder);
        app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        Assert.Equal(basis, Assert.Single(app.People));
        Assert.True(app.HasFailedImport);
        Assert.True(app.HasUnsavedNoteDraft(NoteScope.Person, person.Id));
        Assert.True(app.HasUnsavedNoteDraft(NoteScope.Team, team.Id));
        Assert.Equal("Retained office draft", app.ProfileEdit!.Draft.OfficeNumber);
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => Assert.Fail("Cancellation must not authorize restart.")));
        app.CancelProfileEdit();
        app.DiscardImportChanges();
        app.DiscardUnsavedChanges(person.Id);
        app.DiscardUnsavedChanges(team.Id);
        Assert.False(app.CanSafelyClose); // The independent Favorite failure still needs its own decision.
        Assert.False(app.GetRelationships(basis.Email)!.Person.IsFavorite);
        app.DiscardPersonChanges(basis.Email);
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = CreateWorkflow();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(basis, Assert.Single(reopened.People));
        Assert.Equal("Saved Person", Assert.Single(reopened.GetPersonNotes(basis.Email)).Markdown);
        Assert.Equal("Saved Team", Assert.Single(reopened.GetTeamNotes(basis.Email)).Markdown);
    }

    [Fact]
    public void Explicit_null_clears_all_nullable_text_attributes_durably_without_changing_identity()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var clear = """
            {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"AVERY@example.com","displayName":null,"preferredName":null,"alias":null,
              "jobTitle":null,"department":null,"officeBuilding":null,"officeNumber":null,
              "officeState":null,"officeCountry":null,"linkedInProfileUrl":null}]}
            """;
        var preview = app.PreviewImport(clear);
        Assert.True(preview.CanApply, app.Status);
        Assert.Contains("LinkedIn URL", string.Join("\n", preview.Changes));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(new PersonProfile("avery@example.com", null, null, null), Assert.Single(reopened.People));
        Assert.Equal("avery@example.com", Assert.Single(reopened.People).Heading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void First_profile_autosave_and_retry_preserve_a_changed_delivered_field_until_explicit_discard(bool emptyBase)
    {
        using var app = new WorkspaceViewModel(CreateWorkflow());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        if (emptyBase) Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, null), app.Status);
        var basis = Assert.Single(app.People);
        using var provider = CreateWorkflow();
        Assert.True(provider.OpenWorkspace(folder), provider.Status);
        Assert.True(provider.EditPersonText(basis.Email, PersonTextField.OfficeNumber, "Delivered"), provider.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);

        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.OfficeNumber, "Local draft"));
        Assert.Contains("changed externally", app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Delivered", Assert.Single(app.People).OfficeNumber);
        Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.OfficeNumber, "Local draft"));
        using (var reopened = CreateWorkflow())
        {
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Delivered", Assert.Single(reopened.People).OfficeNumber);
        }
        app.DiscardUnsavedChanges();
        var accepted = Assert.Single(app.People);
        Assert.NotNull(app.SavePersonTextDraft(accepted, PersonTextField.OfficeNumber, "Intentional edit"));
        Assert.True(app.CanSafelyClose);
    }

    [Fact]
    public void Profile_draft_accepts_other_field_delivery_and_returns_the_exact_saved_base_for_newer_typing()
    {
        using var app = new WorkspaceViewModel(CreateWorkflow());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var basis = Assert.Single(app.People);
        using var provider = CreateWorkflow();
        Assert.True(provider.OpenWorkspace(folder), provider.Status);
        Assert.True(provider.EditPersonText(basis.Email, PersonTextField.Department, "Delivered department"), provider.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);
        using (File.Open(Path.Combine(folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.OfficeNumber, "First"));
        var saved = Assert.IsType<PersonProfile>(app.SavePersonTextDraft(basis, PersonTextField.OfficeNumber, "First"));
        Assert.Equal("Delivered department", saved.Department);
        Assert.Equal("First", saved.OfficeNumber);
        var newer = Assert.IsType<PersonProfile>(app.SavePersonTextDraft(saved, PersonTextField.OfficeNumber, "Newer"));
        Assert.Equal("Newer", newer.OfficeNumber);
        using var reopened = CreateWorkflow();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(newer, Assert.Single(reopened.People));
    }

    private static IApplicationWorkflow CreateWorkflow() => ApplicationWorkflow.CreateFileBacked();

    [Fact]
    public void Rejected_batch_and_local_save_failure_retain_the_last_durable_complete_profile()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var saved = Assert.Single(app.People);
        var invalid = Batch.Replace("000000000001", "000000000002")
            .Replace("\"officeNumber\":\"007B\"", "\"officeNumber\":\"009A\",\"notes\":\"must not import\"");
        var preview = app.PreviewImport(invalid);
        Assert.False(preview.CanApply);
        Assert.False(app.ApplyImport(preview.Id));
        using (var lease = new FileStream(Path.Combine(folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.EditPersonText(saved.Email, PersonTextField.OfficeNumber, "Unsaved 009A"));
            Assert.Contains("NOT saved", app.Status);
            Assert.Equal(saved, Assert.Single(app.People));
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(saved, Assert.Single(reopened.People));
        Assert.Equal("007B", Assert.Single(reopened.People).OfficeNumber);
    }

    [Theory]
    [InlineData("alias", " ")]
    [InlineData("jobTitle", "")]
    [InlineData("department", "\t")]
    [InlineData("officeBuilding", " ")]
    [InlineData("officeNumber", "")]
    [InlineData("officeState", "\t")]
    [InlineData("officeCountry", "")]
    [InlineData("linkedInProfileUrl", "http://www.linkedin.com/in/avery")]
    public void Invalid_external_textual_data_blocks_opening_and_writes(string property, string invalidValue)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var path = WorkspaceFixtures.IndexedDocuments(folder)["people:avery@example.com"];
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json[property] = invalidValue;
        File.WriteAllText(path, json.ToJsonString());
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(folder));
        Assert.Contains(property, reopened.Status);
        Assert.Empty(reopened.People);
        Assert.False(reopened.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "008"));
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
