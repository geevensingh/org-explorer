using OrgExplorer.Workflow;
using Microsoft.Data.Sqlite;
using OrgExplorer.Desktop.Smoke;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed partial class SqliteWorkspaceTests : IDisposable
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
    public void Normal_creation_can_reopen_the_committed_empty_SQLite_Workspace()
    {
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.True(app.OpenWorkspace(Workspace), app.Status);
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(99)]
    public void Normal_open_rejects_retired_file_formats_with_recreation_guidance_and_without_mutation(int version)
    {
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Path.Combine(Workspace, "workspace.json"), $$"""{"schemaVersion":{{version}}}""");
        File.WriteAllText(Path.Combine(Workspace, "owner-writing.md"), "# Preserve the existing folder");
        var before = OrgExplorer.TestFixtures.WorkspaceFixtures.Bytes(Workspace);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.False(app.OpenWorkspace(Workspace));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        Assert.Contains("new", app.Compatibility.Guidance);
        Assert.Contains("does not migrate", app.Compatibility.Guidance);
        Assert.Contains("Preserve", app.Compatibility.Guidance);
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Empty(app.People);
        Assert.False(app.RefreshWorkspace());
        var after = OrgExplorer.TestFixtures.WorkspaceFixtures.Bytes(Workspace);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void Normal_open_refuses_incompatible_SQLite_versions_without_upgrading_or_repairing_them(int version)
    {
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            Assert.True(seed.OpenWorkspace(Workspace), seed.Status);
        using (var fixture = new SqliteConnection($"Data Source={Path.Combine(Workspace, "workspace.sqlite")};Pooling=False"))
        {
            fixture.Open();
            using var command = fixture.CreateCommand();
            command.CommandText = "UPDATE workspace SET format_version=$version;";
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
        }
        var before = OrgExplorer.TestFixtures.WorkspaceFixtures.Bytes(Workspace);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(Workspace));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        Assert.False(app.IsWorkspaceCurrent);
        Assert.False(app.Compatibility.SupportsOrdinaryEdits);
        Assert.Empty(app.People);
        Assert.Contains(version < 3 ? "new empty Workspace" : "Upgrade", app.Compatibility.Guidance);
        var after = OrgExplorer.TestFixtures.WorkspaceFixtures.Bytes(Workspace);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
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
            Assert.True(settings.OpenWorkspace(app, Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
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
    public void Reviewed_partial_Imports_commit_resources_evidence_and_frozen_history_and_replay_without_duplication()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        const string manager = "manager@example.com";
        const string report = "report@example.com";
        Assert.True(app.BeginProfileEdit(manager), app.Status);
        Assert.True(app.StageProfileTeamName("Private platform"), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        var note = app.CreateTeamNote(manager, "Original context", "# Locally known before Import")!;
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        var source = Path.Combine(root, "reviewed.json");
        var photo = Path.Combine(root, "portrait.png");
        File.WriteAllBytes(photo, image);
        const string update = """
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-03T17:00:00Z","producer":"Reviewed update","people":[
             {"email":"report@example.com","preferredName":null,"alias":null,"jobTitle":"Senior engineer",
              "officeNumber":"009D","managerEmail":"new@example.com","isFavorite":false,"photoPath":"portrait.png"},
             {"email":"manager@example.com","jobTitle":"Director","photoPath":"portrait.png"},
             {"email":"new@example.com","displayName":"New Manager","managerEmail":null}]}
            """;
        File.WriteAllText(source, update);
        var review = app.PreviewImportFile(source);
        Assert.True(review.CanApply, app.Status);
        Assert.Contains(review.Changes, change => change.Contains("needs review"));
        Assert.Equal("Reference", app.GetRelationships(report)!.Person.PreferredName);
        Assert.Equal(2, app.People.Count);
        Assert.True(app.ApplyImport(review.Id), app.Status);
        var expected = new PersonProfile(report, "Report", null, "new@example.com")
        {
            Alias = null, JobTitle = "Senior engineer", Department = "Research", OfficeBuilding = "Building A",
            OfficeNumber = "009D", OfficeState = "Washington", OfficeCountry = "United States",
            LinkedInProfileUrl = "https://www.linkedin.com/in/report", IsFavorite = false,
            PhotoPath = app.GetRelationships(report)!.Person.PhotoPath
        };
        Assert.NotNull(expected.PhotoPath);
        Assert.Equal(expected, app.GetRelationships(report)!.Person);
        Assert.Equal(image, app.GetPhoto(report)!.Bytes.ToArray());
        Assert.Equal(image, app.GetPhoto(manager)!.Bytes.ToArray());
        Assert.Empty(app.GetTeamNames(manager));
        Assert.Equal("Private platform", app.GetRelationships(manager)!.Person.TeamName);
        Assert.True(Assert.Single(app.GetTeamNotes(manager)).NeedsReview);
        var capture = Assert.Single(app.GetHistoricalTeamContexts(report));
        Assert.Equal(manager, Assert.Single(capture.ReportingChain).Email);
        Assert.Null(Assert.Single(capture.ReportingChain).TeamName);
        Assert.Equal(note.Id, Assert.Single(capture.TeamNotes).Id);
        Assert.Equal("# Locally known before Import", Assert.Single(capture.TeamNotes).Markdown);
        var transition = Assert.Single(capture.Transitions);
        Assert.Equal(report, transition.PersonEmail);
        Assert.Equal(manager, transition.Change.OldValue.GetString());
        Assert.Equal("new@example.com", transition.Change.NewValue.GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), transition.Change.ReportingInterval!.After);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T17:00:00Z"), transition.Change.ReportingInterval.By);
        var timeline = app.GetProfileTimeline(report)!;
        Assert.Equal(new[] { "alias", "isFavorite", "jobTitle", "managerEmail", "officeNumber", "photoPath", "preferredName" },
            timeline.Changes.Select(change => change.Field).Order());
        Assert.Equal(20, timeline.Observations.Count);
        Assert.All(timeline.Observations.Skip(13), evidence =>
        {
            Assert.Equal(ProfileChangeSource.Import, evidence.Source);
            Assert.Equal(Guid.Parse("94000000-0000-4000-8000-000000000001"), evidence.BatchId);
            Assert.Equal("Reviewed update", evidence.Producer);
            Assert.Equal(DateTimeOffset.Parse("2026-11-03T17:00:00Z"), evidence.ObservedAt);
        });
        Assert.True(app.EditTeamNote(note.Id, "Later context", "# Later current-only writing"), app.Status);
        Assert.True(app.EditPreferredName(report, "Later private name"), app.Status);
        string State(IApplicationWorkflow workflow) => JsonSerializer.Serialize(new
        {
            workflow.People,
            Timelines = workflow.People.Select(person => workflow.GetProfileTimeline(person.Email)).ToArray(),
            Captures = workflow.GetHistoricalTeamContexts(report),
            Notes = workflow.GetTeamNotes(manager)
        });
        var saved = State(app);
        var originalReplay = app.PreviewImport(Initial);
        Assert.True(originalReplay.CanApply, app.Status);
        Assert.Contains("no-op", Assert.Single(originalReplay.Changes));
        Assert.True(app.ApplyImport(originalReplay.Id), app.Status);
        var updateReplay = app.PreviewImportFile(source);
        Assert.True(updateReplay.CanApply, app.Status);
        Assert.Contains("no-op", Assert.Single(updateReplay.Changes));
        Assert.True(app.ApplyImport(updateReplay.Id), app.Status);
        Assert.Equal(saved, State(app));
        File.WriteAllText(source, update.Replace("Senior engineer", "Conflicting replay"));
        Assert.False(app.PreviewImportFile(source).CanApply);
        Assert.Contains("already accepted with different logical data", app.Status);
        Assert.Equal(saved, State(app));
        File.Delete(photo);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(saved, State(reopened));
        Assert.Equal(expected with { PreferredName = "Later private name" }, reopened.GetRelationships(report)!.Person);
        Assert.Equal(image, reopened.GetPhoto(report)!.Bytes.ToArray());
        Assert.Equal(capture.TeamNotes, Assert.Single(reopened.GetHistoricalTeamContexts(report)).TeamNotes);
        Assert.DoesNotContain(reopened.GetProfileTimeline(manager)!.Observations, item => item.Field == "teamName");
    }

    [Fact]
    public void Rejected_local_Import_transaction_rolls_back_People_resources_evidence_and_captures_before_retry()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var note = app.CreateTeamNote("manager@example.com", "Known before failure", "# Frozen writing")!;
        var photo = Path.Combine(root, "rollback.png");
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        File.WriteAllBytes(photo, image);
        var import = Path.Combine(root, "rollback.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-04T17:00:00Z","people":[
             {"email":"report@example.com","managerEmail":"new@example.com","photoPath":"rollback.png","department":null},
             {"email":"manager@example.com","jobTitle":"Changed role"},
             {"email":"new@example.com","displayName":"New Manager","managerEmail":null,"photoPath":"rollback.png"}]}
            """);
        var before = app.People.ToArray();
        var histories = JsonSerializer.Serialize(before.Select(person => app.GetProfileTimeline(person.Email)));
        var preview = app.PreviewImportFile(import);
        Assert.True(preview.CanApply, app.Status);
        using var refusing = new SqliteConnection($"Data Source={Path.Combine(Workspace, "workspace.sqlite")};Pooling=False");
        refusing.Open();
        using var refusal = refusing.CreateCommand();
        // Refuse the final revision write, after the real transaction has written every logical family.
        refusal.CommandText = """
            CREATE TRIGGER reject_import_commit BEFORE UPDATE ON workspace
            BEGIN SELECT RAISE(ABORT, 'Controlled final local commit refusal'); END;
            """;
        refusal.ExecuteNonQuery();
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Contains("Controlled final local commit refusal", app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        Assert.Equal(before, app.People);
        using (var unchanged = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(unchanged.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), unchanged.Status);
            Assert.Equal(before, unchanged.People);
            Assert.Equal(histories, JsonSerializer.Serialize(before.Select(person => unchanged.GetProfileTimeline(person.Email))));
            Assert.Empty(unchanged.GetHistoricalTeamContexts("report@example.com"));
            Assert.Null(unchanged.GetPhoto("report@example.com"));
            Assert.Equal(note, Assert.Single(unchanged.GetTeamNotes("manager@example.com")));
            var unaccepted = unchanged.PreviewImportFile(import);
            Assert.True(unaccepted.CanApply, unchanged.Status);
            Assert.DoesNotContain(unaccepted.Changes, change => change.Contains("no-op"));
        }
        refusal.CommandText = "DROP TRIGGER reject_import_commit;";
        refusal.ExecuteNonQuery();
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(LocalSavePhase.Failed, app.LocalSave.Phase);
        Assert.False(app.CanEditLocalWorkspace);
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.Equal(LocalSavePhase.Recovered, app.LocalSave.Phase);
        Assert.True(app.ApplyImport(app.PreviewImportFile(import).Id), app.Status);
        Assert.True(app.CanSafelyClose, app.Status);
        File.Delete(photo);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(3, reopened.People.Count);
        Assert.Null(reopened.GetRelationships("report@example.com")!.Person.Department);
        Assert.Equal("new@example.com", reopened.GetRelationships("report@example.com")!.Manager!.Email);
        Assert.Equal(image, reopened.GetPhoto("report@example.com")!.Bytes.ToArray());
        Assert.Equal(image, reopened.GetPhoto("new@example.com")!.Bytes.ToArray());
        Assert.Single(reopened.GetHistoricalTeamContexts("report@example.com"));
        Assert.Single(reopened.GetProfileTimeline("manager@example.com")!.Changes);
        Assert.Equal(3, reopened.GetProfileTimeline("report@example.com")!.Changes.Count);
    }

    [Theory]
    [InlineData("\"teamName\":\"Imported label\"")]
    [InlineData("\"teamName\":null")]
    [InlineData("\"teamName\":{\"operation\":\"clear\"}")]
    public void Import_never_sets_or_clears_private_labels_and_reporting_updates_retain_dormant_duplicate_owners(string field)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        const string initial = """
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000003",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com"},
             {"email":"manager@example.com","displayName":"Manager","managerEmail":"gap@example.com"},
             {"email":"gap@example.com","displayName":"Gap","managerEmail":"ancestor@example.com"},
             {"email":"ancestor@example.com","displayName":"Ancestor","managerEmail":null}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(initial).Id), app.Status);
        foreach (var owner in new[] { "manager@example.com", "ancestor@example.com" })
        {
            Assert.True(app.BeginProfileEdit(owner), app.Status);
            Assert.True(app.StageProfileTeamName("Platform"), app.Status);
            Assert.True(app.SaveProfileEdit(), app.Status);
        }
        var rejected = app.PreviewImport($$"""
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000004",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"report@example.com","jobTitle":"Must not commit"},
             {"email":"manager@example.com",{{field}}}]}
            """);
        Assert.False(rejected.CanApply);
        Assert.Contains("teamName", string.Join("\n", rejected.Errors));
        Assert.Null(app.GetRelationships("report@example.com")!.Person.JobTitle);
        const string archived = """
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000004",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"report@example.com","managerEmail":"gap@example.com"},
             {"email":"manager@example.com","archived":true}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(archived).Id), app.Status);
        Assert.Equal("Platform", app.GetRelationships("manager@example.com")!.Person.TeamName);
        Assert.Empty(app.GetTeamNames("manager@example.com"));
        Assert.Equal("ancestor@example.com", Assert.Single(app.GetTeamNames("report@example.com")).Owner.Email);
        var capture = Assert.Single(app.GetHistoricalTeamContexts("report@example.com"));
        Assert.All(capture.ReportingChain, person => Assert.Null(person.TeamName));
        Assert.Empty(capture.TeamNotes);
        const string restored = """
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000005",
             "observedAt":"2026-11-03T17:00:00Z","people":[
             {"email":"report@example.com","managerEmail":"manager@example.com"},
             {"email":"manager@example.com","archived":false}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(restored).Id), app.Status);
        var state = JsonSerializer.Serialize(app.GetHistoricalTeamContexts("report@example.com"));
        foreach (var replay in new[] { initial, archived, restored })
            Assert.True(app.ApplyImport(app.PreviewImport(replay).Id), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(new[] { ("Platform", "manager@example.com"), ("Platform", "ancestor@example.com") },
            reopened.GetTeamNames("report@example.com").Select(segment => (segment.Name, segment.Owner.Email)));
        Assert.Equal(state, JsonSerializer.Serialize(reopened.GetHistoricalTeamContexts("report@example.com")));
        Assert.All(reopened.People, person =>
        {
            Assert.DoesNotContain(reopened.GetProfileTimeline(person.Email)!.Observations, item => item.Field == "teamName");
            Assert.All(reopened.GetHistoricalTeamContexts(person.Email), item =>
                Assert.All(item.ReportingChain, former => Assert.Null(former.TeamName)));
        });
    }

    [Fact]
    public void Stale_review_and_changed_import_resources_reject_the_whole_partial_batch()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var import = Path.Combine(root, "stale.json");
        const string update = """
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000006",
             "observedAt":"2026-11-04T17:00:00Z","people":[
             {"email":"report@example.com","department":"Unaccepted department","managerEmail":null},
             {"email":"manager@example.com","officeNumber":"Unaccepted room"}]}
            """;
        File.WriteAllText(import, update);
        var preview = app.PreviewImportFile(import);
        Assert.True(preview.CanApply, app.Status);
        using (var other = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(other.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), other.Status);
            Assert.True(other.EditPersonText("manager@example.com", PersonTextField.OfficeNumber, "Other saved room"), other.Status);
        }
        Assert.False(app.ApplyImport(preview.Id));
        Assert.True(app.RefreshWorkspace(), app.Status);
        preview = app.PreviewImportFile(import);
        File.WriteAllText(import, update.Replace("Unaccepted department", "Changed after review"));
        Assert.False(app.ApplyImport(preview.Id));
        var photo = Path.Combine(root, "stale.png");
        File.WriteAllBytes(photo, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC"));
        File.WriteAllText(import, update.Replace("\"department\":\"Unaccepted department\"", "\"photoPath\":\"stale.png\""));
        preview = app.PreviewImportFile(import);
        Assert.True(preview.CanApply, app.Status);
        File.Delete(photo);
        Assert.False(app.ApplyImport(preview.Id));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Research", reopened.GetRelationships("report@example.com")!.Person.Department);
        Assert.Equal("manager@example.com", reopened.GetRelationships("report@example.com")!.Manager!.Email);
        Assert.Equal("Other saved room", reopened.GetRelationships("manager@example.com")!.Person.OfficeNumber);
        Assert.Null(reopened.GetPhoto("report@example.com"));
        Assert.Empty(reopened.GetHistoricalTeamContexts("report@example.com"));
        Assert.Empty(reopened.GetProfileTimeline("report@example.com")!.Changes);
        Assert.Single(reopened.GetProfileTimeline("manager@example.com")!.Changes);
        var unaccepted = reopened.PreviewImport(update);
        Assert.True(unaccepted.CanApply, reopened.Status);
        Assert.DoesNotContain(unaccepted.Changes, change => change.Contains("no-op"));
    }

    [Fact]
    public void Older_partial_observations_warn_without_backdating_changes_or_duplicating_confirmations_on_replay()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        const string later = """
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000007",
             "observedAt":"2026-11-06T17:00:00Z","people":[{"email":"report@example.com","officeNumber":"010E"}]}
            """;
        const string earlier = """
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000008",
             "observedAt":"2026-11-02T17:00:00Z","people":[{"email":"report@example.com","officeNumber":"010E"}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(later).Id), app.Status);
        var review = app.PreviewImport(earlier);
        Assert.True(review.CanApply, app.Status);
        Assert.Contains(review.Warnings, warning => warning.Contains("older evidence"));
        Assert.True(app.ApplyImport(review.Id), app.Status);
        var timeline = app.GetProfileTimeline("report@example.com")!;
        Assert.Single(timeline.Changes);
        Assert.Equal(15, timeline.Observations.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"),
            timeline.Confirmations.Single(item => item.Field == "officeNumber").ObservedAt);
        var staleChange = earlier.Replace("000000000008", "000000000009").Replace("010E", "Older intentional value");
        review = app.PreviewImport(staleChange);
        Assert.Contains(review.Warnings, warning => warning.Contains("older evidence"));
        Assert.True(app.ApplyImport(review.Id), app.Status);
        timeline = app.GetProfileTimeline("report@example.com")!;
        var change = timeline.Changes.Last();
        Assert.True(change.IsStaleEvidence);
        Assert.Equal("010E", change.OldValue.GetString());
        Assert.Equal("Older intentional value", change.NewValue.GetString());
        Assert.True(change.Evidence.RecordedAt >= change.PreviousEvidence!.RecordedAt);
        var saved = JsonSerializer.Serialize(timeline);
        foreach (var replay in new[] { Initial, later, earlier, staleChange })
            Assert.True(app.ApplyImport(app.PreviewImport(replay).Id), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(saved, JsonSerializer.Serialize(reopened.GetProfileTimeline("report@example.com")));
        Assert.Equal("Older intentional value", reopened.GetRelationships("report@example.com")!.Person.OfficeNumber);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Held_bulk_Import_keeps_browsing_and_newer_owner_drafts_independent_of_its_commit(NoteScope scope)
    {
        var clock = new SaveClock();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(timeProvider: clock), timeProvider: clock);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        const string owner = "manager@example.com";
        var id = scope == NoteScope.Person
            ? app.CreatePersonNote(owner, "Existing note", "Before bulk Import")!.Id
            : app.CreateTeamNote(owner, "Existing note", "Before bulk Import")!.Id;
        var review = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000010",
             "observedAt":"2026-11-05T17:00:00Z","people":[
             {"email":"report@example.com","officeNumber":"Imported room"},
             {"email":"manager@example.com","department":"Imported department"}]}
            """);
        Assert.True(review.CanApply, app.Status);
        Assert.True(app.NavigatePerson(owner), app.Status);
        Assert.True(app.OpenOwnedNote(scope, id, editing: true), app.Status);
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeBuilding, "Unsubmitted owner draft"), app.Status);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Workspace, "workspace.sqlite")};Pooling=False");
        connection.Open();
        using var held = connection.BeginTransaction();
        using var admitted = new ManualResetEventSlim();
        var applying = Task.Run(() => { admitted.Set(); return app.ApplyImport(review.Id); });
        Assert.True(admitted.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.False(app.LocalSave.EditsPaused);
            Assert.False(app.LocalSave.HasUnresolvedOutcome);
            Assert.True(app.CanEditLocalWorkspace);
            app.UpdateNoteDraft(scope, id, "Retained note", "Newer writing during bulk Import");
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            Assert.True(app.NavigatePerson(owner), app.Status);
            Assert.False(applying.IsCompleted, "Browsing only returned after the held Import finished.");
            Assert.Equal(owner, app.PeopleNavigation.SelectedEmail);
            Assert.Equal(id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
            Assert.Equal("Newer writing during bulk Import", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
            Assert.Equal("Unsubmitted owner draft", app.ProfileEdit!.Draft.OfficeBuilding);
            Assert.False(app.CanSafelyClose);
            using var uncommitted = ApplicationWorkflow.CreateFileBacked();
            Assert.True(uncommitted.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), uncommitted.Status);
            Assert.Equal("007B", uncommitted.GetRelationships("report@example.com")!.Person.OfficeNumber);
            Assert.Null(uncommitted.GetRelationships(owner)!.Person.Department);
        }
        finally { held.Commit(); }
        Assert.True(await applying.WaitAsync(TimeSpan.FromSeconds(10)), app.Status);
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        Assert.Equal("Unsubmitted owner draft", app.ProfileEdit!.Draft.OfficeBuilding);
        Assert.False(app.CanSafelyClose);
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Equal("Imported room", reopened.GetRelationships("report@example.com")!.Person.OfficeNumber);
            Assert.Equal("Imported department", reopened.GetRelationships(owner)!.Person.Department);
            Assert.Null(reopened.GetRelationships(owner)!.Person.OfficeBuilding);
            Assert.Equal("Newer writing during bulk Import", reopened.BeginNoteDraft(scope, owner, id)!.Markdown);
            Assert.Null(reopened.BeginNoteDraft(scope, "report@example.com", id));
        }
        Assert.False(app.SaveProfileEdit());
        Assert.Contains("Profile changed", app.LocalSaveError);
        Assert.Equal("Unsubmitted owner draft", app.ProfileEdit!.Draft.OfficeBuilding);
        app.CancelProfileEdit();
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Fact]
    public void Complete_local_profile_Save_and_Cancel_preserve_exact_committed_values_on_reopen()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        const string owner = "report@example.com";
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "008C"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.Department, "Applied research"), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.Equal("Profile saved locally.", app.Status);
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Must not commit"), app.Status);
        app.CancelProfileEdit();
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        var person = reopened.GetRelationships(owner)!.Person;
        Assert.Equal("008C", person.OfficeNumber);
        Assert.Equal("Applied research", person.Department);
        Assert.Equal("Reference", person.PreferredName);
        Assert.Equal(new[] { "department", "officeNumber" },
            reopened.GetProfileTimeline(owner)!.Changes.Select(change => change.Field).Order());
        Assert.Empty(reopened.GetHistoricalTeamContexts(owner));
        Assert.False(File.Exists(Path.Combine(Workspace, "workspace.json")));
    }

    [Fact]
    public void Manager_transition_commits_photos_history_and_frozen_Team_notes_as_one_reopenable_state()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        const string owner = "manager@example.com";
        const string report = "report@example.com";
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        var path = Path.Combine(root, "portrait.png");
        File.WriteAllBytes(path, image);
        Assert.True(app.EditPhoto(owner, path), app.Status);
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfileTeamName("Private platform"), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        var note = app.CreateTeamNote(owner, "Former context", "# Frozen manager writing")!;
        Assert.True(app.BeginProfileEdit(report), app.Status);
        Assert.True(app.StageProfileManager(null), app.Status);
        Assert.True(app.StageProfilePhoto(path), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Independent engineer"), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        var frozen = Assert.Single(app.GetHistoricalTeamContexts(report));
        Assert.Equal(owner, Assert.Single(frozen.ReportingChain).Email);
        Assert.Null(Assert.Single(frozen.ReportingChain).TeamName);
        Assert.Equal("# Frozen manager writing", Assert.Single(frozen.TeamNotes).Markdown);
        Assert.Equal(note.Id, Assert.Single(frozen.TeamNotes).Id);
        Assert.Equal("managerEmail", Assert.Single(frozen.Transitions).Change.Field);
        Assert.True(app.EditTeamNote(note.Id, "Current context", "# Later writing"), app.Status);
        Assert.True(app.EditPhoto(owner, null), app.Status);
        Assert.True(app.EditPhoto(report, null), app.Status);
        File.Delete(path);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Null(reopened.GetRelationships(report)!.Manager);
        Assert.Equal("Independent engineer", reopened.GetRelationships(report)!.Person.JobTitle);
        Assert.Null(reopened.GetPhoto(report));
        Assert.Equal("Private platform", reopened.GetRelationships(owner)!.Person.TeamName);
        Assert.Empty(reopened.GetTeamNames(owner));
        var capture = Assert.Single(reopened.GetHistoricalTeamContexts(report));
        Assert.Equal(frozen.Id, capture.Id);
        Assert.Equal(frozen.TeamNotes, capture.TeamNotes);
        Assert.Equal(frozen.DisplayProvenance, capture.DisplayProvenance);
        var transition = Assert.Single(capture.Transitions);
        Assert.Equal(report, transition.PersonEmail);
        Assert.Equal(owner, transition.Change.OldValue.GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, transition.Change.NewValue.ValueKind);
        Assert.Equal(ProfileChangeSource.LocalEdit, transition.Change.Evidence.Source);
        Assert.NotNull(Assert.Single(capture.ReportingChain).PhotoPath);
        Assert.Contains("effective date unknown", capture.DisplayProvenance);
        Assert.Equal("# Later writing", Assert.Single(reopened.GetTeamNotes(owner)).Markdown);
        Assert.False(reopened.EditPhoto(report, Path.Combine(root, "missing.png")));
        Assert.Null(reopened.GetPhoto(report));
        Assert.True(reopened.EditPersonManager(report, owner), reopened.Status);
        Assert.Equal("Private platform", Assert.Single(reopened.GetTeamNames(report)).Name);
    }

    [Fact]
    public void Ineligible_Team_name_and_unavailable_photo_reject_the_complete_profile_without_partial_history()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        const string owner = "manager@example.com";
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfileTeamName("Saved label"), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.Department, "Uncommitted department"), app.Status);
        Assert.True(app.StageProfileTeamName("Uncommitted label"), app.Status);
        using (var other = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(other.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), other.Status);
            Assert.True(other.EditPersonManager("report@example.com", null), other.Status);
        }
        Assert.False(app.SaveProfileEdit());
        Assert.Contains("current direct report", app.LocalSaveError);
        Assert.True(app.CanEditLocalWorkspace, "Eligibility rejection must not be reported as a local storage fault.");
        Assert.Equal("Uncommitted label", app.ProfileEdit!.Draft.TeamName);
        Assert.Equal("Uncommitted department", app.ProfileEdit.Draft.Department);
        using (var unchanged = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(unchanged.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), unchanged.Status);
            Assert.Equal("Saved label", unchanged.GetRelationships(owner)!.Person.TeamName);
            Assert.Null(unchanged.GetRelationships(owner)!.Person.Department);
            Assert.Empty(unchanged.GetProfileTimeline(owner)!.Changes);
            Assert.Empty(unchanged.GetHistoricalTeamContexts(owner));
        }
        app.CancelProfileEdit();
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfilePhoto(Path.Combine(root, "unavailable.png")), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Not committed"), app.Status);
        Assert.False(app.SaveProfileEdit());
        Assert.Contains("photo", app.LocalSaveError);
        Assert.True(app.CanEditLocalWorkspace, "An unavailable selected photo is a rejected draft, not failed Workspace storage.");
        Assert.True(app.ProfileEdit!.PhotoChanged);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Null(reopened.GetRelationships(owner)!.Person.OfficeNumber);
        Assert.Null(reopened.GetPhoto(owner));
        Assert.Empty(reopened.GetProfileTimeline(owner)!.Changes);
    }

    [Fact]
    public void Person_navigation_retains_distinct_unsubmitted_profiles_and_Cancel_is_owner_scoped()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.True(app.BeginProfileEdit("manager@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Manager draft"), app.Status);
        Assert.True(app.StageProfileTeamName("Retained label"), app.Status);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        Assert.Null(app.ProfileEdit);
        Assert.False(app.CanSafelyClose);
        Assert.True(app.BeginProfileEdit("report@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.Department, "Report draft"), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.Equal("manager@example.com", app.ProfileEdit!.Basis.Email);
        Assert.Equal("Manager draft", app.ProfileEdit.Draft.OfficeNumber);
        Assert.Equal("Retained label", app.ProfileEdit.Draft.TeamName);
        using (var committed = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(committed.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), committed.Status);
            Assert.Null(committed.GetRelationships("manager@example.com")!.Person.OfficeNumber);
            Assert.Null(committed.GetRelationships("manager@example.com")!.Person.TeamName);
            Assert.Equal("Research", committed.GetRelationships("report@example.com")!.Person.Department);
        }
        app.CancelProfileEdit();
        Assert.False(app.CanSafelyClose);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        Assert.Equal("Report draft", app.ProfileEdit!.Draft.Department);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Report draft", reopened.GetRelationships("report@example.com")!.Person.Department);
        Assert.Null(reopened.GetRelationships("manager@example.com")!.Person.OfficeNumber);
    }

    [Fact]
    public async Task Held_profile_commit_never_blocks_browsing_or_overwrites_newer_owner_drafts()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.True(app.BeginProfileEdit("manager@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Submitted value"), app.Status);
        var submitted = app.ProfileEdit!;
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Workspace, "workspace.sqlite")};Pooling=False");
        connection.Open();
        using var held = connection.BeginTransaction();
        using var entered = new ManualResetEventSlim();
        var saving = Task.Run(() => { entered.Set(); return app.SaveProfileEdit(submitted); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(50);
        Assert.False(saving.IsCompleted);
        Assert.False(app.CanSafelyClose);
        var navigation = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Newer manager writing"), app.Status);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        Assert.True(app.BeginProfileEdit("report@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.Department, "Other owner writing"), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.True(navigation.Elapsed < TimeSpan.FromSeconds(1), "Draft/browse operations waited behind SQLite I/O.");
        held.Commit();
        Assert.True(await saving, app.Status);
        Assert.Equal("Submitted value", app.ProfileEdit!.Basis.OfficeNumber);
        Assert.Equal("Newer manager writing", app.ProfileEdit.Draft.OfficeNumber);
        Assert.False(app.CanSafelyClose);
        using (var committed = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(committed.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), committed.Status);
            Assert.Equal("Submitted value", committed.GetRelationships("manager@example.com")!.Person.OfficeNumber);
            Assert.Equal("Research", committed.GetRelationships("report@example.com")!.Person.Department);
        }
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        Assert.Equal("Other owner writing", app.ProfileEdit!.Draft.Department);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Newer manager writing", reopened.GetRelationships("manager@example.com")!.Person.OfficeNumber);
        Assert.Equal("Other owner writing", reopened.GetRelationships("report@example.com")!.Person.Department);
    }

    [Fact]
    public void Independent_Preferred_name_favorite_and_archival_actions_preserve_profiles_notes_and_exact_reopen()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        const string report = "report@example.com";
        var note = app.CreatePersonNote(report, "Private", "Retained person writing")!;
        var basis = app.GetRelationships(report)!.Person;
        Assert.True(app.BeginProfileEdit(report), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Profile draft"), app.Status);
        Assert.NotNull(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Preferred report"));
        Assert.True(app.EditFavorite(report, false), app.Status);
        Assert.Equal("Preferred report", app.ProfileEdit!.Draft.PreferredName);
        Assert.False(app.ProfileEdit.Draft.IsFavorite);
        Assert.False(app.EditPersonArchived(report, true, confirmed: false));
        Assert.False(app.EditPersonArchived(report, true, profileDecision: ProfileLeaveDecision.Stay));
        Assert.Equal("Profile draft", app.ProfileEdit.Draft.OfficeNumber);
        Assert.True(app.EditPersonArchived(report, true, profileDecision: ProfileLeaveDecision.Discard), app.Status);
        using (var archived = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(archived.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), archived.Status);
            var person = Assert.Single(archived.ArchivedPeople);
            Assert.Equal("Preferred report", person.PreferredName);
            Assert.False(person.IsFavorite);
            Assert.Equal("007B", person.OfficeNumber);
            Assert.Equal(note.Id, Assert.Single(archived.GetPersonNotes(report)).Id);
            Assert.Single(archived.GetHistoricalTeamContexts(report));
            Assert.True(archived.EditPersonArchived(report, false), archived.Status);
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Empty(app.ArchivedPeople);
        Assert.Equal("Retained person writing", Assert.Single(app.GetPersonNotes(report)).Markdown);
        Assert.Equal(new[] { "archived", "archived", "isFavorite", "preferredName" },
            app.GetProfileTimeline(report)!.Changes.Select(change => change.Field).Order());
    }

    [Fact]
    public void Changed_field_stale_Preferred_name_is_rejected_while_unrelated_fields_are_retained()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        const string report = "report@example.com";
        var basis = app.GetRelationships(report)!.Person;
        using var other = ApplicationWorkflow.CreateFileBacked();
        Assert.True(other.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), other.Status);
        Assert.True(other.EditPersonText(report, PersonTextField.OfficeNumber, "Delivered room"), other.Status);
        Assert.NotNull(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Local preferred"));
        Assert.Equal("Delivered room", app.GetRelationships(report)!.Person.OfficeNumber);
        Assert.True(other.RefreshWorkspace(), other.Status);
        var stale = app.GetRelationships(report)!.Person;
        Assert.True(other.EditPreferredName(report, "Other preferred"), other.Status);
        Assert.Null(app.SavePersonTextDraft(stale, PersonTextField.PreferredName, "Must not overwrite"));
        Assert.Contains("changed externally", app.LocalSaveError);
        app.DiscardPersonTextDraft(stale, PersonTextField.PreferredName);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Other preferred", reopened.GetRelationships(report)!.Person.PreferredName);
        Assert.Equal("Delivered room", reopened.GetRelationships(report)!.Person.OfficeNumber);
    }

    [Fact]
    public void Preferred_name_drafts_remain_owner_correct_and_unsaved_until_explicit_Save_or_Cancel()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var manager = app.GetRelationships("manager@example.com")!.Person;
        app.RetainPersonTextDraft(manager, PersonTextField.PreferredName, "Unsubmitted manager");
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        var report = app.GetRelationships("report@example.com")!.Person;
        app.RetainPersonTextDraft(report, PersonTextField.PreferredName, "Unsubmitted report");
        Assert.False(app.CanSafelyClose);
        Assert.True(app.NavigatePerson(manager.Email), app.Status);
        Assert.Equal("Unsubmitted manager", app.GetPersonTextDraft(manager.Email, PersonTextField.PreferredName)!.Value);
        using (var committed = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(committed.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), committed.Status);
            Assert.Null(committed.GetRelationships(manager.Email)!.Person.PreferredName);
            Assert.Equal("Reference", committed.GetRelationships(report.Email)!.Person.PreferredName);
        }
        Assert.NotNull(app.SavePersonTextDraft(manager, PersonTextField.PreferredName, "Unsubmitted manager"));
        Assert.Null(app.GetPersonTextDraft(manager.Email, PersonTextField.PreferredName));
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Unsubmitted report", app.GetPersonTextDraft(report.Email, PersonTextField.PreferredName)!.Value);
        app.DiscardPersonTextDraft(report, PersonTextField.PreferredName);
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Unsubmitted manager", reopened.GetRelationships(manager.Email)!.Person.PreferredName);
        Assert.Equal("Reference", reopened.GetRelationships(report.Email)!.Person.PreferredName);
    }

    [Fact]
    public void Owned_duplicate_Team_names_skip_unnamed_gaps_and_label_only_saves_leave_note_review_and_history_unchanged()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"93000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com"},
             {"email":"manager@example.com","displayName":"Manager","managerEmail":"gap@example.com"},
             {"email":"gap@example.com","displayName":"Gap","managerEmail":"ancestor@example.com"},
             {"email":"ancestor@example.com","displayName":"Ancestor","managerEmail":null}]}
            """).Id), app.Status);
        var note = app.CreateTeamNote("manager@example.com", "Team", "Original team context")!;
        void Label(string email, string? value)
        {
            Assert.True(app.BeginProfileEdit(email), app.Status);
            Assert.True(app.StageProfileTeamName(value), app.Status);
            Assert.True(app.SaveProfileEdit(), app.Status);
        }
        Label("manager@example.com", "Platform");
        Label("ancestor@example.com", "Platform");
        Label("manager@example.com", "Renamed");
        Label("manager@example.com", null);
        Label("manager@example.com", "Platform");
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(new[] { ("Platform", "manager@example.com"), ("Platform", "ancestor@example.com") },
            reopened.GetTeamNames("report@example.com").Select(segment => (segment.Name, segment.Owner.Email)));
        Assert.Equal(new[] { ("Platform", "manager@example.com"), ("Platform", "ancestor@example.com") },
            reopened.GetTeamNames("manager@example.com").Select(segment => (segment.Name, segment.Owner.Email)));
        Assert.False(Assert.Single(reopened.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.Equal(note.UpdatedAt, Assert.Single(reopened.GetTeamNotes("manager@example.com")).UpdatedAt);
        Assert.Empty(reopened.GetProfileTimeline("manager@example.com")!.Changes);
        Assert.Empty(reopened.GetProfileTimeline("ancestor@example.com")!.Changes);
        Assert.Empty(reopened.GetHistoricalTeamContexts("report@example.com"));
        Assert.False(reopened.PreviewImport("""
            {"schemaVersion":1,"batchId":"93000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"manager@example.com","teamName":"Not importable"}]}
            """).CanApply);
        Assert.Equal("Platform", reopened.GetRelationships("manager@example.com")!.Person.TeamName);
    }

    [Fact]
    public void Failed_profile_save_retains_owner_draft_and_retry_does_not_discard_independent_note_writing()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var note = app.BeginNoteDraft(NoteScope.Person, "manager@example.com")!;
        app.RetainNoteDraft(note);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.True(app.BeginProfileEdit("manager@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Retained room"), app.Status);
        using (File.Open(Path.Combine(Workspace, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(app.SaveProfileEdit());
        Assert.Contains("NOT saved", app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.Equal("Retained room", app.ProfileEdit!.Draft.OfficeNumber);
        using (var unchanged = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(unchanged.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), unchanged.Status);
            Assert.Null(unchanged.GetRelationships("manager@example.com")!.Person.OfficeNumber);
            Assert.Empty(unchanged.GetProfileTimeline("manager@example.com")!.Changes);
        }
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.SaveNoteDraft(note, "Independent", "Retained note writing"));
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Retained room", reopened.GetRelationships("manager@example.com")!.Person.OfficeNumber);
        Assert.Equal("Retained note writing", Assert.Single(reopened.GetPersonNotes("manager@example.com")).Markdown);
        Assert.Single(reopened.GetProfileTimeline("manager@example.com")!.Changes);
    }

    [Fact]
    public void Normal_Workspace_rejects_live_database_synchronization_without_a_file_authority_fallback()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var before = app.GetRelationships("report@example.com")!.Person;
        Assert.False(app.ConfigureSynchronizationFolder(Workspace));
        Assert.Contains("separate, non-nested", app.Status);
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

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Typing_during_a_successful_save_return_uses_the_accepted_base_for_the_next_commit(NoteScope scope)
    {
        var proxy = DispatchProxy.Create<IApplicationWorkflow, NoteSaveBoundary>();
        var boundary = (NoteSaveBoundary)proxy;
        var inner = boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var committed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var app = new WorkspaceViewModel(proxy);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var paused = 0;
        boundary.AfterSuccessfulSave = _ =>
        {
            if (Interlocked.Exchange(ref paused, 1) != 0) return;
            committed.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The successful note save return was not released.");
        };
        try
        {
            var creation = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
            app.UpdateNoteDraft(scope, creation.Id, "Initial context", "First committed writing");
            Assert.True(committed.Wait(TimeSpan.FromSeconds(10)), "The actual SQLite commit did not reach its return boundary.");
            Assert.False(inner.HasUnsavedNoteDraft(scope, creation.Id));
            Assert.False(app.CanSafelyClose);
            using (var reading = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
                var first = Assert.IsType<NoteDraft>(reading.BeginNoteDraft(scope, "manager@example.com", creation.Id));
                Assert.Equal("First committed writing", first.Markdown);
                Assert.Null(reading.BeginNoteDraft(scope, "report@example.com", creation.Id));
            }
            app.UpdateNoteDraft(scope, creation.Id, "Latest context", "Newer writing during the successful return");
            var retained = Assert.IsType<RetainedNoteDraft>(app.GetRetainedNoteDraft(scope, creation.Id));
            Assert.Same(creation, retained.Basis);
            Assert.True(retained.IsSaving);
            Assert.Equal("Newer writing during the successful return", retained.Markdown);
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
            Assert.Equal(creation.Id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
            Assert.Equal("Newer writing during the successful return", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
            Assert.False(app.CanSafelyClose);
            release.Set();
            Assert.True(await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10)), app.Status);
            Assert.Null(app.GetRetainedNoteDraft(scope, creation.Id));
            Assert.Null(app.LocalSaveError);
            Assert.True(app.CanSafelyClose, app.Status);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            var latest = Assert.IsType<NoteDraft>(reopened.BeginNoteDraft(scope, "manager@example.com", creation.Id));
            Assert.Equal("Latest context", latest.Title);
            Assert.Equal("Newer writing during the successful return", latest.Markdown);
            Assert.Null(reopened.BeginNoteDraft(scope, "report@example.com", creation.Id));
        }
        finally
        {
            release.Set();
            await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task A_genuine_different_base_rejects_an_admitted_autosave_without_losing_either_writing(NoteScope scope)
    {
        var proxy = DispatchProxy.Create<IApplicationWorkflow, NoteSaveBoundary>();
        var boundary = (NoteSaveBoundary)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var admitted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var app = new WorkspaceViewModel(proxy);
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var original = Assert.IsType<NoteDraft>(app.SaveNoteDraft(
            app.BeginNoteDraft(scope, "manager@example.com")!, "Original context", "Original committed writing"));
        Assert.True(app.OpenOwnedNotes(scope, original.OwnerEmail), app.Status);
        Assert.True(app.OpenOwnedNote(scope, original.Id, editing: true), app.Status);
        boundary.BeforeSave = () =>
        {
            admitted.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The admitted note save was not released.");
        };
        try
        {
            app.UpdateNoteDraft(scope, original.Id, "Local context", "Retained local writing");
            Assert.True(admitted.Wait(TimeSpan.FromSeconds(10)), "The autosave did not reach its actual public workflow boundary.");
            using (var other = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(other.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), other.Status);
                var basis = Assert.IsType<NoteDraft>(other.BeginNoteDraft(scope, original.OwnerEmail, original.Id));
                Assert.NotNull(other.SaveNoteDraft(basis, "Other context", "Other committed writing"));
            }
            Assert.True(app.RefreshWorkspace(), app.Status);
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            Assert.True(app.NavigatePerson(original.OwnerEmail), app.Status);
            Assert.Equal("Retained local writing", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
            release.Set();
            Assert.False(await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("changed externally", app.LocalSaveError);
            var retained = Assert.IsType<RetainedNoteDraft>(app.GetRetainedNoteDraft(scope, original.Id));
            Assert.Equal("Retained local writing", retained.Markdown);
            Assert.NotNull(retained.Error);
            Assert.False(app.CanSafelyClose);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Equal("Other committed writing", reopened.BeginNoteDraft(scope, original.OwnerEmail, original.Id)!.Markdown);
            Assert.Null(reopened.BeginNoteDraft(scope, "report@example.com", original.Id));
        }
        finally
        {
            release.Set();
            await app.FlushNoteDraftsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Local_notes_preserve_identity_writing_archival_ongoing_choice_and_scope_on_reopen(NoteScope scope)
    {
        var preferences = Path.Combine(root, "settings", "notes.json");
        Guid id;
        DateTimeOffset created;
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(preferences)))
        {
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
            var basis = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, "manager@example.com"));
            var saved = Assert.IsType<NoteDraft>(app.SaveNoteDraft(basis, "Manager context", "# Original\nPrivate writing"));
            id = saved.Id;
            created = scope == NoteScope.Person
                ? Assert.Single(app.GetPersonNotes(saved.OwnerEmail)).CreatedAt
                : Assert.Single(app.GetTeamNotes(saved.OwnerEmail)).CreatedAt;
            Assert.True(app.OpenOwnedNotes(scope, saved.OwnerEmail), app.Status);
            Assert.True(app.DesignateOngoingNote(scope, id), app.Status);
            Assert.NotNull(app.SaveNoteDraft(saved, "Continuing context", "# Updated\nExact latest writing"));
            Assert.True(scope == NoteScope.Person ? app.ArchivePersonNote(id) : app.ArchiveTeamNote(id), app.Status);
            Assert.Empty(scope == NoteScope.Person
                ? app.GetPersonNotes(saved.OwnerEmail).Select(n => n.Id)
                : app.GetTeamNotes(saved.OwnerEmail).Select(n => n.Id));
            Assert.True(scope == NoteScope.Person ? app.ArchivePersonNote(id, false) : app.ArchiveTeamNote(id, false), app.Status);
            if (scope == NoteScope.Team) Assert.True(app.ConfirmTeamNote(id), app.Status);
            Assert.True(app.CanSafelyClose, app.Status);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(preferences));
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.True(reopened.OpenOwnedNotes(scope, "manager@example.com"), reopened.Status);
        var note = reopened.GetOwnedNotesView(scope).SelectedNote!;
        Assert.Equal(id, note.Id);
        Assert.Equal("manager@example.com", note.OwnerEmail);
        Assert.Equal("Continuing context", note.Title);
        Assert.Equal("# Updated\nExact latest writing", note.Markdown);
        Assert.False(note.Archived);
        Assert.Equal(id, reopened.GetOwnedNotesView(scope).OngoingNoteId);
        if (scope == NoteScope.Team)
        {
            var inherited = Assert.Single(reopened.GetInheritedTeamNotes("report@example.com"));
            Assert.Equal(id, inherited.Note.Id);
            Assert.Equal("manager@example.com", inherited.Owner.Email);
            Assert.False(inherited.Note.NeedsReview);
            Assert.Equal(created, inherited.Note.CreatedAt);
            Assert.Empty(reopened.GetPersonNotes("manager@example.com"));
        }
        else
        {
            Assert.Empty(reopened.GetInheritedTeamNotes("report@example.com"));
            Assert.Equal(created, Assert.Single(reopened.GetPersonNotes("manager@example.com")).CreatedAt);
        }
        Assert.Empty(reopened.GetHistoricalTeamContexts("report@example.com"));
        Assert.Empty(reopened.GetProfileTimeline("manager@example.com")!.Changes);
        Assert.False(File.Exists(Path.Combine(Workspace, "workspace.json")));
        Assert.False(Directory.Exists(Path.Combine(Workspace, "notes")));
        Assert.False(Directory.Exists(Path.Combine(Workspace, "team-notes")));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Pending_creation_and_newer_typing_survive_actual_Person_switches_and_late_commit(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        using var held = new SqliteConnection($"Data Source={Path.Combine(Workspace, "workspace.sqlite")};Pooling=False");
        held.Open();
        using var transaction = held.BeginTransaction();
        var first = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.Equal(first.Id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        app.UpdateNoteDraft(scope, first.Id, "First request", "First body");
        await WaitUntil(() => app.GetRetainedNoteDraft(scope, first.Id)?.IsSaving == true);
        app.UpdateNoteDraft(scope, first.Id, "Newest title", "Typed while the first commit was waiting");
        Assert.False(app.CanSafelyClose);
        var restarted = false;
        Assert.False(app.RestartWhenSafe(_ => restarted = true));
        Assert.False(restarted);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        var second = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "report@example.com"));
        app.UpdateNoteDraft(scope, second.Id, "Report context", "Distinct report writing");
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.Equal(first.Id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.Equal("Newest title", app.GetOwnedNotesView(scope).SelectedNote!.Title);
        Assert.Equal("Typed while the first commit was waiting", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.True(app.GetOwnedNotesView(scope).HasUnsavedChanges);
        transaction.Commit();
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        Assert.False(app.GetOwnedNotesView(scope).HasUnsavedChanges);
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Typed while the first commit was waiting", reopened.BeginNoteDraft(scope, "manager@example.com", first.Id)!.Markdown);
        Assert.Equal("Distinct report writing", reopened.BeginNoteDraft(scope, "report@example.com", second.Id)!.Markdown);
        Assert.Single(scope == NoteScope.Person ? reopened.GetPersonNotes("manager@example.com").Select(n => n.Id) :
            reopened.GetTeamNotes("manager@example.com").Select(n => n.Id));
        Assert.Null(reopened.BeginNoteDraft(scope, "report@example.com", first.Id));
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Failed_creation_retains_latest_draft_browsing_and_one_identity_through_explicit_retry(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
        app.UpdateNoteDraft(scope, draft.Id, "Retained title", "Private failed creation");
        using (File.Open(Path.Combine(Workspace, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(await app.FlushNoteDraftsAsync());
        var retained = Assert.IsType<RetainedNoteDraft>(app.GetRetainedNoteDraft(scope, draft.Id));
        Assert.NotNull(retained.Error);
        Assert.Equal("Private failed creation", retained.Markdown);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("NOT saved", app.Status);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.Equal(draft.Id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.Equal("Private failed creation", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.True(await app.RetryNoteDraftAsync(scope, draft.Id), app.Status);
        Assert.Null(app.GetRetainedNoteDraft(scope, draft.Id));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Private failed creation", reopened.BeginNoteDraft(scope, "manager@example.com", draft.Id)!.Markdown);
        Assert.Single(scope == NoteScope.Person ? reopened.GetPersonNotes("manager@example.com").Select(n => n.Id) :
            reopened.GetTeamNotes("manager@example.com").Select(n => n.Id));
    }

    [Fact]
    public async Task Discarding_an_uncommitted_creation_restores_empty_reading_without_discarding_another_owner()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var kept = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(NoteScope.Team, "report@example.com"));
        var discarded = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(NoteScope.Team, "manager@example.com"));
        app.DiscardUnsavedChanges(discarded.Id);
        Assert.True(app.TeamNotesView.IsEmpty);
        Assert.False(app.CanSafelyClose);
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Empty(reopened.GetTeamNotes("manager@example.com"));
        Assert.Equal(kept.Id, Assert.Single(reopened.GetTeamNotes("report@example.com")).Id);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Continuous_typing_becomes_durable_before_idle_and_finishes_with_exact_latest_writing(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var observed = new HashSet<string>();
        TimeSpan? firstCommit = null;
        for (var index = 0; index < 25; index++)
        {
            app.UpdateNoteDraft(scope, draft.Id, "Continuous context", $"Continuous writing {index:D2}");
            await Task.Delay(60);
            using var reading = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
            var committed = reading.BeginNoteDraft(scope, "manager@example.com", draft.Id);
            if (committed?.Markdown.StartsWith("Continuous writing ", StringComparison.Ordinal) == true)
            {
                firstCommit ??= elapsed.Elapsed;
                observed.Add(committed.Markdown);
            }
        }
        Assert.NotNull(firstCommit);
        Assert.True(firstCommit <= TimeSpan.FromSeconds(1), $"First ordinary local commit took {firstCommit}.");
        Assert.True(observed.Count >= 3, "Continuous typing must make repeated durable progress, not wait until idle.");
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Continuous writing 24", reopened.BeginNoteDraft(scope, "manager@example.com", draft.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Durable_note_observation_runs_during_typing_not_after_unrelated_navigation_observations(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
        const string prefix = "# Independently observed ongoing writing";
        var readNumber = 0;
        await NoteCommitObservation.RunAsync(
            () => app.UpdateNoteDraft(scope, draft.Id, "Observed context", prefix),
            () =>
            {
                if (Interlocked.Increment(ref readNumber) == 1) Thread.Sleep(1100);
                using var reading = ApplicationWorkflow.CreateFileBacked();
                Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
                var committed = reading.BeginNoteDraft(scope, "manager@example.com", draft.Id);
                return committed is not null && committed.Id == draft.Id && committed.Scope == scope &&
                    committed.OwnerEmail == "manager@example.com" && committed.Title == "Observed context" &&
                    committed.Markdown.StartsWith(prefix, StringComparison.Ordinal);
            }, async observation =>
        {
            for (var index = 0; index < 6; index++)
            {
                await Task.Delay(60);
                app.UpdateNoteDraft(scope, draft.Id, "Observed context", prefix + $"\nContinued entry {index}");
                Assert.True(app.NavigatePerson("report@example.com"), app.Status);
                Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
            }
            var observedMs = Assert.IsType<double>(await observation.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
            await Task.Delay(1100);
            var collectedMs = observation.ElapsedMs;
            var commitMs = observation.Complete();
            Console.WriteLine("NOTE-OBSERVATION-TIMING " + System.Text.Json.JsonSerializer.Serialize(new
            {
                scope = scope.ToString(), firstReadDelayMs = 1100, observation.RequestMs, observation.WorkerAdmittedMs,
                observation.FirstReadStartedMs, observation.SuccessfulReadStartedMs,
                observation.ReadCount, observation.ReadMs, observedMs, collectedMs, returnedMs = commitMs,
                safelyClose = app.CanSafelyClose, hasSaveError = app.LocalSaveError is not null
            }));
            Assert.Equal(observedMs, commitMs);
            Assert.True(collectedMs - observedMs >= 1000,
                "Collection must be deliberately delayed after the actual coherent read, not used as a local latency SLA.");
            Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Equal(prefix + "\nContinued entry 5", reopened.BeginNoteDraft(scope, "manager@example.com", draft.Id)!.Markdown);
        });
    }

    [Fact]
    public async Task Predetermined_continuous_typing_reopens_exact_final_writing_within_the_existing_observation_window()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var drafts = new[] { NoteScope.Person, NoteScope.Team };
        foreach (var scope in drafts)
        {
            var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
            const string prefix = "# Sustained predetermined writing";
            var expectedFinal = prefix + string.Concat(Enumerable.Range(0, 20).Select(index => $"\nContinued entry {index}"));
            var firstRead = 0;
            var typingFinished = false;
            await NoteCommitObservation.RunAsync(
                () => app.UpdateNoteDraft(scope, draft.Id, "Sustained context", prefix),
                () =>
                {
                    if (Interlocked.Increment(ref firstRead) == 1) Thread.Sleep(450);
                    if (!Volatile.Read(ref typingFinished)) return false;
                    var localSave = app.LocalSave;
                    var safelyClose = app.CanSafelyClose;
                    if (!safelyClose || localSave.OutstandingRequests != 0 ||
                        localSave.EditsPaused || localSave.HasUnresolvedOutcome) return false;
                    using var reading = ApplicationWorkflow.CreateFileBacked();
                    Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly),
                        $"{reading.Status}; final-reopen readiness: safelyClose={safelyClose}, " +
                        $"localSave={localSave.Phase}/{localSave.Outcome}, outstanding={localSave.OutstandingRequests}; " +
                        $"currentLocalSave={app.LocalSave.Phase}/{app.LocalSave.Outcome}.");
                    var committed = reading.BeginNoteDraft(scope, "manager@example.com", draft.Id);
                    return committed is not null && committed.Id == draft.Id && committed.Scope == scope &&
                        committed.Title == "Sustained context" &&
                        committed.OwnerEmail == "manager@example.com" &&
                        committed.Markdown == expectedFinal &&
                        reading.BeginNoteDraft(scope, "report@example.com", draft.Id) is null;
                }, async observation =>
            {
                var latest = prefix;
                var typing = observation.ContinueTyping(index =>
                {
                    latest += $"\nContinued entry {index}";
                    app.UpdateNoteDraft(scope, draft.Id, "Sustained context", latest);
                    Assert.True(app.NavigatePerson("report@example.com"), app.Status);
                    Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
                });
                Volatile.Write(ref typingFinished, true);
                var observedMs = observation.Complete();
                Console.WriteLine("NOTE-SUSTAINED-TYPING-REOPEN " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    scope = scope.ToString(), readDelayMs = 450, observedMs,
                    continuedEditRequestStartedMs = typing.RequestStartedMs,
                    continuedEditRequestCompletedMs = typing.RequestCompletedMs,
                    typingCompletedMs = typing.RequestCompletedMs[^1],
                    observationCompletedDuringTyping = observedMs <= typing.RequestCompletedMs[^1],
                    timingUse = "Diagnostic only; final exact writing must reopen within the existing observation window, not before typing ends."
                }));
                Assert.True(typing.RequestStartedMs[^1] - typing.RequestCompletedMs[0] >= 1000,
                    "The fixed typing workload ended before the approved one-second observation window could be exercised.");
                Assert.Equal(typing.RequestStartedMs.Length, typing.RequestCompletedMs.Length);
                Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
                using var reopened = ApplicationWorkflow.CreateFileBacked();
                Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
                var final = Assert.IsType<NoteDraft>(reopened.BeginNoteDraft(scope, "manager@example.com", draft.Id));
                Assert.Equal(draft.Id, final.Id);
                Assert.Equal(scope, final.Scope);
                Assert.Equal("manager@example.com", final.OwnerEmail);
                Assert.Equal("Sustained context", final.Title);
                Assert.Equal(expectedFinal, final.Markdown);
                Assert.Null(reopened.BeginNoteDraft(scope, "report@example.com", draft.Id));
            });
        }
    }

    [Fact]
    public void Note_input_failure_stays_primary_when_the_observer_also_faults()
    {
        var primary = new InvalidOperationException("Controlled input failure");
        var secondary = new IOException("Controlled concurrent observer failure");
        using var reading = new ManualResetEventSlim();
        var actual = Record.Exception((Action)(() =>
        {
            NoteCommitObservation.Run(
                () =>
                {
                    Assert.True(reading.Wait(TimeSpan.FromSeconds(5)));
                    throw primary;
                },
                () => { reading.Set(); throw secondary; }, _ => { });
        }));
        Assert.Same(primary, actual);
        Assert.Same(secondary, primary.Data["NoteCommitObservationSecondaryFailure"]);
    }

    [Fact]
    public void Note_assertion_failure_stays_primary_when_the_observer_also_faults()
    {
        var primary = new InvalidOperationException("Controlled assertion failure");
        var secondary = new IOException("Controlled concurrent observer failure");
        using var reading = new ManualResetEventSlim();
        var actual = Record.Exception((Action)(() =>
        {
            NoteCommitObservation.Run(() => { }, () => { reading.Set(); throw secondary; }, _ =>
            {
                Assert.True(reading.Wait(TimeSpan.FromSeconds(5)));
                throw primary;
            });
        }));
        Assert.Same(primary, actual);
        Assert.Same(secondary, primary.Data["NoteCommitObservationSecondaryFailure"]);
    }

    [Fact]
    public void Note_observer_failure_still_rejects_an_otherwise_successful_observation()
    {
        var secondary = new IOException("Controlled observer failure");
        using var reading = new ManualResetEventSlim();
        var actual = Record.Exception((Action)(() =>
        {
            NoteCommitObservation.Run(() => { }, () => { reading.Set(); throw secondary; },
                _ => Assert.True(reading.Wait(TimeSpan.FromSeconds(5))));
        }));
        Assert.Same(secondary, actual);
    }

    [Fact]
    public async Task Cancelling_observation_of_a_pending_real_write_never_confirms_it_saved()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Workspace, "workspace.sqlite")};Pooling=False");
        connection.Open();
        using var held = connection.BeginTransaction();
        var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(NoteScope.Team, "manager@example.com"));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => NoteCommitObservation.RunAsync(
            () => app.UpdateNoteDraft(NoteScope.Team, draft.Id, "Pending context", "Not yet committed"),
            () =>
            {
                using var reading = ApplicationWorkflow.CreateFileBacked();
                Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
                return reading.BeginNoteDraft(NoteScope.Team, "manager@example.com", draft.Id)?.Markdown == "Not yet committed";
            }, async observation =>
            {
                await WaitUntil(() => app.GetRetainedNoteDraft(NoteScope.Team, draft.Id)?.IsSaving == true);
                observation.Cancel();
                observation.Complete();
            }));
        Assert.Contains("cancelled", failure.Message);
        Assert.False(app.CanSafelyClose);
        held.Commit();
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
    }

    [Fact]
    public void Normal_reopen_preserves_existing_SQLite_data_instead_of_recreating_it()
    {
        using (var old = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(old.OpenWorkspace(Workspace), old.Status);
            Assert.True(old.ApplyImport(old.PreviewImport(Initial).Id), old.Status);
        }
        var before = Directory.GetFiles(Workspace, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        Assert.Equal(2, app.People.Count);
        Assert.Equal("Reference", app.GetRelationships("report@example.com")!.Person.PreferredName);
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
            Assert.True(seed.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), seed.Status);
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
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
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
        Assert.True(first.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), first.Status);
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
    [InlineData("\"alias\":\"report\"", "\"alias\":\"report\",\"photoPath\":\"local.png\"", "containing directory")]
    public void Rejected_initial_Import_preserves_domain_validation_and_never_discards_unsupported_fields(
        string before, string after, string error)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.OpenOrCreate), app.Status);
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

    public class NoteSaveBoundary : DispatchProxy
    {
        public IApplicationWorkflow Inner { get; set; } = null!;
        public Action? BeforeSave { get; set; }
        public Action<NoteDraft>? AfterSuccessfulSave { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) throw new InvalidOperationException("A workflow method is required.");
            if (targetMethod.Name == nameof(IApplicationWorkflow.SaveNoteDraft)) BeforeSave?.Invoke();
            object? result;
            try { result = targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            if (targetMethod.Name == nameof(IApplicationWorkflow.SaveNoteDraft) && result is NoteDraft accepted)
                AfterSuccessfulSave?.Invoke(accepted);
            return result;
        }
    }
}
