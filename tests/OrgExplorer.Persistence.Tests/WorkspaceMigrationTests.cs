using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class WorkspaceMigrationTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Complete_version_one_Workspace_converts_automatically_and_reopens_with_recoverable_originals()
    {
        WorkspaceFixtures.CopyVersionOne(folder);
        var originals = WorkspaceFixtures.Bytes(folder);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "workspace.json")))!["schemaVersion"]!.GetValue<int>());
        Assert.Contains("automatically converted", app.Status);
        Assert.NotNull(app.Compatibility.RecoveryPath);
        var backup = WorkspaceFixtures.Bytes(app.Compatibility.RecoveryPath!);
        Assert.Equal(originals.Keys.Order(), backup.Keys.Order());
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, backup[path]);
        VerifyCompleteContext(app);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        VerifyCompleteContext(reopened);
        Assert.True(reopened.EditPreferredName("avery@example.com", "Converted local edit"), reopened.Status);
        Assert.True(reopened.EditFavorite("former@example.com", false), reopened.Status);
        using var edited = ApplicationWorkflow.CreateFileBacked();
        Assert.True(edited.OpenWorkspace(folder), edited.Status);
        Assert.Equal("Converted local edit", edited.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.False(edited.GetRelationships("former@example.com")!.Person.IsFavorite);
        Assert.Equal("Old team writing.", Assert.Single(edited.GetHistoricalTeamContexts("avery@example.com"))
            .TeamNotes.Single(n => n.OwnerEmail == "morgan@example.com").Markdown.Split('\n')[1]);
    }

    private static void VerifyCompleteContext(IApplicationWorkflow app)
    {
        var person = app.GetRelationships("avery@example.com")!.Person;
        Assert.Equal("Avery Example", person.DirectoryName);
        Assert.Equal("Ave", person.PreferredName);
        Assert.Equal("avery", person.Alias);
        Assert.Equal("Engineer", person.JobTitle);
        Assert.Equal("Research", person.Department);
        Assert.Equal("North", person.OfficeBuilding);
        Assert.Equal("0009C", person.OfficeNumber);
        Assert.Equal("WA", person.OfficeState);
        Assert.Equal("US", person.OfficeCountry);
        Assert.Equal("https://www.linkedin.com/in/fictitious-avery", person.LinkedInProfileUrl);
        Assert.Equal("alex@example.com", person.ManagerEmail);
        Assert.True(person.IsFavorite);
        Assert.Equal(new[] { "avery@example.com", "former@example.com", "morgan@example.com" },
            app.FavoritePeople.Select(p => p.Email));
        Assert.Equal(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC"),
            app.GetPhoto(person.Email)!.Bytes.ToArray());
        Assert.True(app.GetRelationships("former@example.com")!.Person.Archived);
        Assert.True(Assert.Single(app.GetPersonNotes("former@example.com", true)).Archived);
        Assert.Equal("# Person writing\n**Preserved** café.", Assert.Single(app.GetPersonNotes(person.Email)).Markdown);
        Assert.Equal("Later writing must not rewrite frozen history.", Assert.Single(app.GetTeamNotes("morgan@example.com")).Markdown);
        var capture = Assert.Single(app.GetHistoricalTeamContexts(person.Email));
        Assert.Equal(new[] { "morgan@example.com", "casey@example.com" }, capture.ReportingChain.Select(p => p.Email));
        Assert.True(capture.ReportingChain[0].IsFavorite);
        Assert.Equal("# Frozen team\nOld team writing.", capture.TeamNotes.Single(n => n.OwnerEmail == "morgan@example.com").Markdown);
        Assert.Equal("Fictitious transfer", Assert.Single(capture.Transitions).Change.Evidence.Producer);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), Assert.Single(capture.Transitions).Change.ReportingInterval!.After);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T17:00:00Z"), Assert.Single(capture.Transitions).Change.ReportingInterval!.By);
        var timeline = app.GetProfileTimeline(person.Email)!;
        Assert.Equal(3, timeline.Changes.Count);
        Assert.Contains(timeline.Changes, c => c.Field == "photoPath" && c.Evidence.Source == ProfileChangeSource.LocalEdit);
        Assert.Equal("0009C", timeline.Confirmations.Single(e => e.Field == "officeNumber").Value.GetString());
        Assert.True(timeline.Confirmations.Single(e => e.Field == "isFavorite").Value.GetBoolean());
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"),
            timeline.Confirmations.Single(e => e.Field == "isFavorite").ObservedAt);
        var replay = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"14000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-03T17:00:00Z","producer":"Fictitious transfer","people":[
            {"email":"avery@example.com","managerEmail":"alex@example.com","officeNumber":"0009C"}]}
            """);
        Assert.True(replay.CanApply, app.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(app.ApplyImport(replay.Id), app.Status);
        Assert.Equal(3, app.GetProfileTimeline(person.Email)!.Changes.Count);
    }

    [Fact]
    public void Unsupported_accepted_Observation_source_is_an_upgrade_block_not_a_repaired_migration()
    {
        WorkspaceFixtures.CopyVersionOne(folder);
        var file = Directory.GetFiles(folder, "batches.json", SearchOption.AllDirectories).Single();
        var data = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        data.First().Value!["source"]!["schemaVersion"] = 99;
        File.WriteAllText(file, data.ToJsonString());
        var before = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(folder));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        Assert.Contains("Upgrade", app.Compatibility.Guidance);
        Assert.Null(app.Compatibility.RecoveryPath);
        Assert.False(app.RefreshWorkspace());
        AssertBytesUnchanged(before);
    }

    private void AssertBytesUnchanged(Dictionary<string, byte[]> expected)
    {
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(expected.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, after[path]);
    }

    [Fact]
    public void Failed_pointer_publication_keeps_originals_and_retry_converts_without_losing_context()
    {
        WorkspaceFixtures.CopyVersionOne(folder);
        var original = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        string backup;
        using (File.Open(Path.Combine(folder, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(app.OpenWorkspace(folder));
            Assert.False(app.IsWorkspaceCurrent);
            Assert.Empty(app.People);
            Assert.Equal(WorkspaceCompatibilityState.RecoveryRequired, app.Compatibility.State);
            backup = app.Compatibility.RecoveryPath!;
            Assert.NotNull(backup);
            Assert.Contains("not replaced", app.Status);
            foreach (var (path, bytes) in original)
            {
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, path)));
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(backup, path)));
            }
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(WorkspaceCompatibilityState.Compatible, app.Compatibility.State);
        VerifyCompleteContext(app);
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(backup, path)));
    }

    [Fact]
    public void Legacy_empty_generation_converts_without_inventing_people_or_history()
    {
        var generation = Path.Combine(folder, "generations", "14000000000000000000000000000000");
        Directory.CreateDirectory(Path.Combine(generation, "people"));
        File.WriteAllText(Path.Combine(generation, "batches.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "workspace.json"),
            """{"schemaVersion":1,"generation":"14000000000000000000000000000000"}""");
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.Empty(app.People);
        Assert.Equal(2, app.Compatibility.DataVersion);
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"14000000-0000-4000-8000-000000000003",
            "observedAt":"2026-11-04T17:00:00Z","people":[
            {"email":"new@example.com","displayName":"New Example","managerEmail":null}]}
            """).Id), app.Status);
        Assert.Empty(app.GetProfileTimeline("new@example.com")!.Changes);
        Assert.Empty(app.GetHistoricalTeamContexts("new@example.com"));
    }

    [Fact]
    public void Preview_apply_checks_incompatible_delivery_before_any_write_even_before_refresh()
    {
        WorkspaceFixtures.CopyVersionOne(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var preview = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"14000000-0000-4000-8000-000000000004",
            "observedAt":"2026-11-04T17:00:00Z","people":[{"email":"avery@example.com","preferredName":"Do not apply"}]}
            """);
        var pointer = Path.Combine(folder, "workspace.json");
        var data = JsonNode.Parse(File.ReadAllText(pointer))!;
        data["schemaVersion"] = 99;
        data["futureRequiredField"] = "Must not discard";
        File.WriteAllText(pointer, data.ToJsonString());
        var before = WorkspaceFixtures.Bytes(folder);
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        Assert.Contains("Upgrade", app.Compatibility.Guidance);
        AssertBytesUnchanged(before);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("people")]
    [InlineData("profile-history")]
    [InlineData("notes")]
    [InlineData("team-notes")]
    [InlineData("historical-team-context")]
    [InlineData("accepted-batches")]
    [InlineData("photos")]
    [InlineData("teamContext")]
    public void Every_data_family_blocks_unsupported_external_versions_without_writes_and_recovers(string family)
    {
        WorkspaceFixtures.CopyVersion(2, folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var note = Assert.Single(app.GetPersonNotes("avery@example.com"));
        var team = Assert.Single(app.GetTeamNotes("morgan@example.com"));
        var pointer = Path.Combine(folder, "workspace.json");
        var generation = Path.Combine(folder, "generations",
            JsonNode.Parse(File.ReadAllText(pointer))!["generation"]!.GetValue<string>());
        var file = family switch
        {
            "workspace" or "photos" or "teamContext" => pointer,
            "accepted-batches" => Path.Combine(generation, "batches.json"),
            "notes" => Path.Combine(folder, "notes", note.Id + ".md"),
            "team-notes" => Path.Combine(folder, "team-notes", team.Id + ".md"),
            _ => Directory.GetFiles(Path.Combine(generation, family), "*.json").First()
        };
        var original = File.ReadAllBytes(file);
        if (family is "notes" or "team-notes")
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));
        else
        {
            var data = JsonNode.Parse(original)!;
            if (family is "photos" or "teamContext") data["recordVersions"]![family] = 99;
            else if (family == "accepted-batches") data.AsObject().First().Value!["source"]!["schemaVersion"] = 99;
            else data["schemaVersion"] = 99;
            File.WriteAllText(file, data.ToJsonString());
        }
        var before = WorkspaceFixtures.Bytes(folder);
        Assert.False(app.RefreshWorkspace());
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        Assert.Contains("Upgrade", app.Status);
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.Heading);
        Assert.False(app.EditPreferredName("avery@example.com", "Never saved"));
        Assert.False(app.EditPhoto("avery@example.com", null));
        Assert.False(app.EditPersonArchived("former@example.com", false));
        Assert.False(app.EditFavorite("avery@example.com", false));
        Assert.False(app.EditPersonNote(note.Id, note.Title, "Never saved"));
        Assert.False(app.ArchivePersonNote(note.Id));
        Assert.False(app.EditTeamNote(team.Id, team.Title, "Never saved"));
        Assert.False(app.ConfirmTeamNote(team.Id));
        Assert.False(app.ArchiveTeamNote(team.Id));
        Assert.False(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"14000000-0000-4000-8000-000000000005",
            "observedAt":"2026-11-04T17:00:00Z","people":[{"email":"avery@example.com","preferredName":"Never saved"}]}
            """).CanApply);
        AssertBytesUnchanged(before);
        Assert.False(File.Exists(Path.Combine(folder, ".workspace.lock")));
        File.WriteAllBytes(file, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(WorkspaceCompatibilityState.Compatible, app.Compatibility.State);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose);
        VerifyCompleteContext(app);
    }

    [Fact]
    public void Incomplete_supported_external_delivery_is_not_repaired_or_converted_until_fully_valid()
    {
        WorkspaceFixtures.CopyVersionOne(folder);
        var originalPointer = File.ReadAllBytes(Path.Combine(folder, "workspace.json"));
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.EditPreferredName("avery@example.com", "Current local name"), app.Status);
        var oldGeneration = JsonNode.Parse(originalPointer)!["generation"]!.GetValue<string>();
        var history = Directory.GetFiles(Path.Combine(folder, "generations", oldGeneration, "profile-history"), "*.json").First();
        var bytes = File.ReadAllBytes(history);
        File.Delete(history);
        File.WriteAllBytes(Path.Combine(folder, "workspace.json"), originalPointer);
        var before = WorkspaceFixtures.Bytes(folder);
        Assert.False(app.RefreshWorkspace());
        Assert.Equal(WorkspaceCompatibilityState.Unavailable, app.Compatibility.State);
        Assert.Equal("Current local name", app.GetRelationships("avery@example.com")!.Person.Heading);
        AssertBytesUnchanged(before);
        File.WriteAllBytes(history, bytes);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Contains("automatically converted", app.Status);
        VerifyCompleteContext(app);
    }

    [Fact]
    public void Legacy_Observation_missing_recording_provenance_is_not_guessed_or_converted()
    {
        WorkspaceFixtures.CopyVersionOne(folder);
        var history = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
            .First(p => p.Contains("profile-history") && JsonNode.Parse(File.ReadAllText(p))!["email"]!.GetValue<string>() == "avery@example.com");
        var data = JsonNode.Parse(File.ReadAllText(history))!;
        data["observations"]![0]!.AsObject().Remove("recordedAt");
        File.WriteAllText(history, data.ToJsonString());
        var before = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(folder));
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Null(app.Compatibility.RecoveryPath);
        AssertBytesUnchanged(before);
        Assert.False(File.Exists(Path.Combine(folder, ".workspace.lock")));
    }

    [Fact]
    public void Duplicate_family_versions_cannot_hide_an_unsupported_encoding()
    {
        WorkspaceFixtures.CopyVersion(2, folder);
        var pointer = Path.Combine(folder, "workspace.json");
        File.WriteAllText(pointer, File.ReadAllText(pointer).Replace("\"people\": 2", "\"people\": 99, \"people\": 2"));
        var before = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(folder));
        Assert.Contains("duplicate", app.Status);
        AssertBytesUnchanged(before);
        Assert.False(File.Exists(Path.Combine(folder, ".workspace.lock")));
    }

    [Fact]
    public async Task Incompatible_clients_do_not_hold_read_leases_that_block_external_delivery()
    {
        WorkspaceFixtures.CopyVersion(99, folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(folder));
        var pointer = Path.Combine(folder, "workspace.json");
        var original = File.ReadAllBytes(pointer);
        var before = WorkspaceFixtures.Bytes(folder);
        using var start = new ManualResetEventSlim();
        var reads = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 500; i++) app.RefreshWorkspace();
        });
        start.Set();
        try
        {
            for (var i = 0; i < 500; i++) File.WriteAllBytes(pointer, original);
        }
        finally { await reads.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.False(app.RefreshWorkspace());
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        AssertBytesUnchanged(before);
        Assert.False(File.Exists(Path.Combine(folder, ".workspace.lock")));
        var supported = JsonNode.Parse(original)!.AsObject();
        supported["schemaVersion"] = 1;
        supported.Remove("futureRequiredField");
        File.WriteAllText(pointer, supported.ToJsonString());
        Assert.True(app.RefreshWorkspace(), app.Status);
        VerifyCompleteContext(app);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
