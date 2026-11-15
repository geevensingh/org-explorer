using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class SqliteWorkspaceTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "sqlite-" + Guid.NewGuid().ToString("N")));
    private string Workspace => Path.Combine(root, "local");
    private const string Initial = """
        {"schemaVersion":1,"batchId":"91000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","producer":"SQLite acceptance","people":[
         {"email":"report@example.com","displayName":"Report","preferredName":"Reference",
          "alias":"report","jobTitle":"Engineer","department":"Research",
          "officeBuilding":"Building A","officeNumber":"007B","officeState":"Washington",
          "officeCountry":"United States","linkedInProfileUrl":"https://www.linkedin.com/in/report",
          "managerEmail":"manager@example.com","archived":false,"isFavorite":true},
         {"email":"manager@example.com","displayName":"Manager","managerEmail":null}]}
        """;

    [Fact]
    public void Explicit_local_creation_can_reopen_the_committed_empty_Workspace()
    {
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
            Assert.True(app.IsWorkspaceCurrent);
            Assert.Empty(app.People);
            Assert.Contains("SQLite", app.Compatibility.Guidance);
            Assert.Contains("locally", app.Status);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Empty(reopened.People);
        Assert.Contains("SQLite", reopened.Compatibility.Guidance);
    }

    [Fact]
    public void Reviewed_initial_People_Import_and_evidence_survive_startup_reopening()
    {
        var preferences = Path.Combine(root, "settings", "workspace-settings.json");
        var expected = new PersonProfile("report@example.com", "Report", "Reference", "manager@example.com")
        {
            Alias = "report", JobTitle = "Engineer", Department = "Research", OfficeBuilding = "Building A",
            OfficeNumber = "007B", OfficeState = "Washington", OfficeCountry = "United States",
            LinkedInProfileUrl = "https://www.linkedin.com/in/report", IsFavorite = true
        };
        Directory.CreateDirectory(root);
        var import = Path.Combine(root, "initial.json");
        File.WriteAllText(import, Initial);
        var settings = new WorkspaceSettings(preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(app, Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
            Assert.True(app.TryOpenImport(), app.Status);
            var preview = app.PreviewImportFile(import);
            Assert.True(preview.CanApply, app.Status);
            Assert.Empty(app.People);
            Assert.True(app.ApplyImport(preview.Id), app.Status);
            Assert.Equal("Import applied and saved locally.", app.Status);
            app.ShowPeople();
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            Assert.Equal(expected, app.GetRelationships("report@example.com")!.Person);
        }
        var restoredSettings = new WorkspaceSettings(preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), restoredSettings);
        Assert.True(restoredSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal(expected, reopened.GetRelationships("report@example.com")!.Person);
        var manager = Assert.Single(reopened.ReportingRoots);
        Assert.Equal("manager@example.com", manager.Person.Email);
        Assert.Equal(expected, Assert.Single(manager.Reports).Person);
        Assert.Equal(expected, Assert.Single(reopened.FavoritePeople));
        var timeline = reopened.GetProfileTimeline(expected.Email)!;
        Assert.Empty(timeline.Changes);
        Assert.Equal(13, timeline.Observations.Count);
        Assert.All(timeline.Observations, evidence =>
        {
            Assert.Equal(ProfileChangeSource.Import, evidence.Source);
            Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), evidence.ObservedAt);
            Assert.Equal(Guid.Parse("91000000-0000-4000-8000-000000000001"), evidence.BatchId);
            Assert.Equal("SQLite acceptance", evidence.Producer);
        });
        var replay = reopened.PreviewImportFile(import);
        Assert.True(replay.CanApply, reopened.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(reopened.ApplyImport(replay.Id), reopened.Status);
        Assert.Equal(expected, reopened.GetRelationships(expected.Email)!.Person);
    }

    [Fact]
    public void Staged_Workspace_rejects_unimplemented_changes_without_legacy_fallback()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var before = app.GetRelationships("report@example.com")!.Person;
        var update = app.PreviewImport(Initial.Replace("000000000001", "000000000002"));
        Assert.False(update.CanApply);
        Assert.Contains("initial People-only Import", app.Status);
        Assert.False(app.EditPreferredName(before.Email, "Must not save"));
        Assert.Contains("not implemented", app.Status);
        Assert.False(app.BeginProfileEdit(before.Email));
        Assert.Null(app.CreatePersonNote(before.Email, "Must not save", "Private writing"));
        Assert.Null(app.CreateTeamNote(before.Email, "Must not save", "Private writing"));
        Assert.True(app.CanNavigatePeople);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(before, app.GetRelationships(before.Email)!.Person);
        Assert.False(File.Exists(Path.Combine(Workspace, "workspace.json")));
        Assert.False(Directory.Exists(Path.Combine(Workspace, "notes")));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(before, reopened.GetRelationships(before.Email)!.Person);
        Assert.Empty(reopened.GetPersonNotes(before.Email));
        Assert.Empty(reopened.GetTeamNotes(before.Email));
    }

    [Fact]
    public void Rejected_creation_preserves_the_existing_legacy_Workspace()
    {
        using (var old = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(old.OpenWorkspace(Workspace), old.Status);
            Assert.True(old.ApplyImport(old.PreviewImport(Initial).Id), old.Status);
        }
        var before = Directory.GetFiles(Workspace, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite));
        Assert.Contains("never converted or overwritten", app.Status);
        Assert.Empty(app.People);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Workspace, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(2, reopened.People.Count);
        Assert.Equal("Reference", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("mixed")]
    public void Incomplete_or_incompatible_local_storage_never_opens_an_empty_fallback(string failure)
    {
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(seed.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), seed.Status);
            Assert.True(seed.ApplyImport(seed.PreviewImport(Initial).Id), seed.Status);
        }
        var database = Path.Combine(Workspace, "workspace.sqlite");
        switch (failure)
        {
            case "missing": File.Delete(database); break;
            case "corrupt": File.WriteAllText(database, "Not a SQLite database"); break;
            case "mixed": File.WriteAllText(Path.Combine(Workspace, "workspace.json"), "{\"schemaVersion\":99}"); break;
        }
        var before = Directory.GetFiles(Workspace).ToDictionary(path => path, File.ReadAllBytes);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly));
        Assert.False(app.IsWorkspaceCurrent);
        Assert.NotNull(app.OperationError);
        Assert.Empty(app.People);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Workspace).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Failed_initial_Import_can_retry_without_a_partially_accepted_population()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        var preview = app.PreviewImport(Initial);
        Assert.True(preview.CanApply, app.Status);
        using (File.Open(Path.Combine(Workspace, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(app.ApplyImport(preview.Id));
            Assert.NotNull(app.OperationError);
            Assert.DoesNotContain("saved locally", app.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(app.People);
        }
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Empty(reopened.People);
            var retry = reopened.PreviewImport(Initial);
            Assert.True(retry.CanApply, reopened.Status);
            Assert.DoesNotContain("Already applied", string.Join("\n", retry.Changes));
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        using var saved = ApplicationWorkflow.CreateFileBacked();
        Assert.True(saved.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), saved.Status);
        Assert.Equal(2, saved.People.Count);
        Assert.Equal("manager@example.com", saved.GetRelationships("report@example.com")!.Manager!.Email);
    }

    [Fact]
    public void Stale_initial_review_cannot_overwrite_another_committed_population()
    {
        using var first = ApplicationWorkflow.CreateFileBacked();
        Assert.True(first.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), first.Status);
        using var other = ApplicationWorkflow.CreateFileBacked();
        Assert.True(other.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), other.Status);
        var stale = other.PreviewImport(Initial.Replace("Report", "Unaccepted name"));
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.False(other.ApplyImport(stale.Id));
        Assert.Empty(other.People);
        Assert.True(other.RefreshWorkspace(), other.Status);
        Assert.Equal("Report", other.GetRelationships("report@example.com")!.Person.DirectoryName);
        Assert.False(other.PreviewImport(Initial.Replace("Report", "Unaccepted name")).CanApply);
    }

    [Theory]
    [InlineData("\"managerEmail\":null", "\"managerEmail\":\"report@example.com\"", "cycle")]
    [InlineData("\"alias\":\"report\"", "\"alias\":\"report\",\"teamName\":\"Private label\"", "teamName")]
    [InlineData("\"alias\":\"report\"", "\"alias\":\"report\",\"photoPath\":\"local.png\"", "Photos")]
    public void Rejected_initial_Import_preserves_domain_validation_and_never_discards_unsupported_fields(
        string before, string after, string error)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        var preview = app.PreviewImport(Initial.Replace(before, after));
        Assert.False(preview.CanApply);
        Assert.Contains(error, string.Join("\n", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        using (var unchanged = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(unchanged.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), unchanged.Status);
            Assert.Empty(unchanged.People);
        }
        var valid = app.PreviewImport(Initial);
        Assert.True(valid.CanApply, app.Status);
        Assert.True(app.ApplyImport(valid.Id), app.Status);
        Assert.Equal(2, app.People.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
