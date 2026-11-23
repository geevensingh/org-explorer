using System.Text.Json;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class IncrementalProfileStorageTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private string folder => Path.Combine(root, "workspace");
    private const string Import = """
        {"schemaVersion":1,"batchId":"42000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"}]}
        """;

    [Fact]
    public void New_workspace_saves_profile_text_and_favorite_and_reopens_evidence_and_relationships()
    {
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(app.OpenWorkspace(folder), app.Status);
            Assert.Empty(app.People);
            Assert.True(app.Synchronization.Supported);
            Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
            Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
            Assert.True(app.EditPersonText("avery@example.com", PersonTextField.JobTitle, "Principal Engineer"), app.Status);
            Assert.True(app.EditFavorite("avery@example.com", true), app.Status);
            Assert.True(app.CanSafelyClose);
            Assert.True(app.ConfigureSynchronizationFolder(Path.Combine(root, "transport")), app.Synchronization.Error);
            Assert.True(app.SyncNow(), app.Synchronization.Error);
            using var envelope = JsonDocument.Parse(File.ReadAllBytes(WorkspaceFixtures.LatestSnapshot(app)));
            using var content = JsonDocument.Parse(Convert.FromBase64String(envelope.RootElement.GetProperty("content").GetString()!));
            var row = Assert.Single(content.RootElement.GetProperty("acceptedImports").EnumerateArray());
            using var accepted = JsonDocument.Parse(row.GetProperty("value").GetString()!);
            using var original = JsonDocument.Parse(Import);
            Assert.True(JsonElement.DeepEquals(original.RootElement, accepted.RootElement.GetProperty("source")));
            Assert.Equal(app.GetProfileTimeline("avery@example.com")!.Confirmations[0].RecordedAt,
                accepted.RootElement.GetProperty("appliedAt").GetDateTimeOffset());
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var relationships = reopened.GetRelationships("avery@example.com")!;
        Assert.Equal("Ave", relationships.Person.Heading);
        Assert.Equal("Principal Engineer", relationships.Person.JobTitle);
        Assert.True(relationships.Person.IsFavorite);
        Assert.Equal("morgan@example.com", relationships.Manager!.Email);
        Assert.Equal("Morgan", relationships.Manager.DirectoryName);
        var timeline = reopened.GetProfileTimeline("avery@example.com")!;
        Assert.Collection(timeline.Changes,
            c => { Assert.Equal("preferredName", c.Field); Assert.Null(c.OldValue.GetString()); Assert.Equal("Ave", c.NewValue.GetString()); },
            c => { Assert.Equal("jobTitle", c.Field); Assert.Equal("Engineer", c.OldValue.GetString()); Assert.Equal("Principal Engineer", c.NewValue.GetString()); },
            c => { Assert.Equal("isFavorite", c.Field); Assert.False(c.OldValue.GetBoolean()); Assert.True(c.NewValue.GetBoolean()); });
        Assert.All(timeline.Changes, c => Assert.Equal(ProfileChangeSource.LocalEdit, c.Evidence.Source));
        var imported = Assert.Single(timeline.Confirmations, e => e.Field == "displayName");
        Assert.Equal(ProfileChangeSource.Import, imported.Source);
        Assert.Equal(Guid.Parse("42000000-0000-4000-8000-000000000001"), imported.BatchId);
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(Import).Changes));
    }

    [Fact]
    public void Reviewed_population_import_and_update_reopen_the_whole_graph_and_accepted_evidence()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var import = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, batchId = "43000000-0000-4000-8000-000000000001",
            observedAt = "2026-11-03T17:00:00Z",
            people = Enumerable.Range(0, 128).Select(i => new
            {
                email = $"report{i:D3}@example.com", displayName = $"Report {i:D3}",
                managerEmail = i == 0 ? "morgan@example.com" : "report000@example.com"
            })
        });
        var preview = app.PreviewImport(import);
        Assert.True(preview.CanApply, string.Join("\n", preview.Errors));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(130, reopened.People.Count);
        Assert.Equal(127, reopened.GetRelationships("report000@example.com")!.ImportedReports.Count);
        Assert.Equal("morgan@example.com", reopened.GetRelationships("report000@example.com")!.Manager!.Email);
        Assert.Equal("report000@example.com", reopened.GetRelationships("report127@example.com")!.Manager!.Email);
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(import).Changes));
        var update = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, batchId = "43000000-0000-4000-8000-000000000004",
            observedAt = "2026-11-04T17:00:00Z",
            people = Enumerable.Range(0, 128).Select(i => new { email = $"report{i:D3}@example.com", officeNumber = "007B" })
        });
        Assert.True(app.ApplyImport(app.PreviewImport(update).Id), app.Status);
        using var updated = ApplicationWorkflow.CreateFileBacked();
        Assert.True(updated.OpenWorkspace(folder), updated.Status);
        Assert.Equal(127, updated.GetRelationships("report000@example.com")!.ImportedReports.Count);
        Assert.All(updated.People.Where(p => p.Email.StartsWith("report")), p => Assert.Equal("007B", p.OfficeNumber));
        Assert.Equal("Engineer", updated.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Contains("no-op", Assert.Single(updated.PreviewImport(import).Changes));
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("archive")]
    [InlineData("reactivate")]
    [InlineData("favorite")]
    public void Relationship_archival_and_favorite_changes_reopen_expected_context_without_touching_unrelated_people(string mutation)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        const string extra = """
            {"schemaVersion":1,"batchId":"43000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"alex@example.com","displayName":"Alex","managerEmail":null},
             {"email":"unrelated@example.com","displayName":"Unrelated","managerEmail":null}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(extra).Id), app.Status);
        var unrelated = app.GetRelationships("unrelated@example.com")!.Person;
        var note = app.CreatePersonNote("avery@example.com", "Private", "Private writing")!;
        app.CreateTeamNote("morgan@example.com", "Former team", "Original context");
        if (mutation == "reactivate") Assert.True(app.EditPersonArchived("avery@example.com", true), app.Status);
        Assert.True(mutation switch
        {
            "manager" => app.EditPersonManager("avery@example.com", "alex@example.com"),
            "archive" => app.EditPersonArchived("avery@example.com", true),
            "reactivate" => app.EditPersonArchived("avery@example.com", false),
            _ => app.EditFavorite("avery@example.com", true)
        }, app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var person = reopened.GetRelationships("avery@example.com")!.Person;
        Assert.Equal(mutation == "manager" ? "alex@example.com" : "morgan@example.com", person.ManagerEmail);
        Assert.Equal(mutation == "archive", person.Archived);
        Assert.Equal(mutation == "favorite", person.IsFavorite);
        Assert.Equal(unrelated, reopened.GetRelationships(unrelated.Email)!.Person);
        Assert.Equal(note, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        if (mutation == "favorite") Assert.Empty(reopened.GetHistoricalTeamContexts("avery@example.com"));
        else Assert.Equal("Original context", Assert.Single(Assert.Single(
            reopened.GetHistoricalTeamContexts("avery@example.com")).TeamNotes).Markdown);
        Assert.Equal(mutation == "reactivate" ? 2 : 1, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(extra).Changes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_transaction_failure_retains_profile_draft_and_prior_commit_until_recovery_then_retry_or_discard(bool discard)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained draft"), app.Status);
        using (WorkspaceFixtures.LockLocalStore(folder))
            Assert.False(app.SaveProfileEdit());
        Assert.Equal("Retained draft", app.ProfileEdit!.Draft.JobTitle);
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.LocalSaveError);
        using (var prior = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(prior.OpenWorkspace(folder), prior.Status);
            Assert.Equal("Engineer", prior.GetRelationships("avery@example.com")!.Person.JobTitle);
            Assert.Empty(prior.GetProfileTimeline("avery@example.com")!.Changes);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        if (discard) app.CancelProfileEdit();
        else Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.Null(app.ProfileEdit);
        Assert.True(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(discard ? "Engineer" : "Retained draft", reopened.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Equal(discard ? 0 : 1, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
    }

    [Fact]
    public void Text_draft_retry_and_complete_profile_text_save_use_the_existing_view_model_seam()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var basis = app.GetRelationships("avery@example.com")!.Person;
        using (WorkspaceFixtures.LockLocalStore(folder))
            Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Ave"));
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => Assert.Fail("Unsaved writing must block restart.")));
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.NotNull(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Ave"));
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        var edits = new Dictionary<PersonTextField, string>
        {
            [PersonTextField.DirectoryName] = "Avery Directory",
            [PersonTextField.Alias] = "avery",
            [PersonTextField.JobTitle] = "Principal Engineer",
            [PersonTextField.Department] = "Research",
            [PersonTextField.OfficeBuilding] = "Building 4",
            [PersonTextField.OfficeNumber] = "009",
            [PersonTextField.OfficeState] = "WA",
            [PersonTextField.OfficeCountry] = "US",
            [PersonTextField.LinkedInProfileUrl] = "https://www.linkedin.com/in/avery"
        };
        foreach (var (field, value) in edits) Assert.True(app.StageProfileText(field, value), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var person = reopened.GetRelationships("avery@example.com")!.Person;
        Assert.Equal("Ave", person.PreferredName);
        Assert.Equal("Avery Directory", person.DirectoryName);
        Assert.Equal("avery", person.Alias);
        Assert.Equal("Principal Engineer", person.JobTitle);
        Assert.Equal("Research", person.Department);
        Assert.Equal("Building 4", person.OfficeBuilding);
        Assert.Equal("009", person.OfficeNumber);
        Assert.Equal("WA", person.OfficeState);
        Assert.Equal("US", person.OfficeCountry);
        Assert.Equal("https://www.linkedin.com/in/avery", person.LinkedInProfileUrl);
        Assert.Equal(10, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
