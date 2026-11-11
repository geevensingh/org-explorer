using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class WorkspaceVersionTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Older_Workspace_rejection_never_converts_repairs_or_retries_into_a_writable_state(int version)
    {
        WorkspaceFixtures.CopyVersion(version, folder);
        var before = WorkspaceFixtures.Bytes(folder);
        var directories = Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).Order().ToArray();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        using (File.Open(Path.Combine(folder, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(app.OpenWorkspace(folder));
        Assert.False(app.RefreshWorkspace());
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        Assert.Contains("does not migrate", app.Compatibility.Guidance);
        Assert.Contains("Settings", app.Compatibility.Guidance);
        Assert.Empty(app.People);
        Assert.Null(app.Compatibility.RecoveryPath);
        AssertBytesUnchanged(before);
        Assert.Equal(directories, Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).Order());
        Assert.False(File.Exists(Path.Combine(folder, ".workspace.lock")));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("people")]
    [InlineData("profileHistory")]
    [InlineData("notes")]
    [InlineData("team-notes")]
    [InlineData("historicalTeamContext")]
    [InlineData("acceptedBatches")]
    [InlineData("photos")]
    [InlineData("teamContext")]
    public void Every_data_family_blocks_unsupported_external_versions_without_writes_and_recovers(string family)
    {
        WorkspaceFixtures.CopyCurrent(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var note = Assert.Single(app.GetPersonNotes("avery@example.com"));
        var team = Assert.Single(app.GetTeamNotes("morgan@example.com"));
        var pointer = Path.Combine(folder, "workspace.json");
        var documents = WorkspaceFixtures.IndexedDocuments(folder);
        var file = family switch
        {
            "workspace" or "photos" or "teamContext" => pointer,
            "notes" => Path.Combine(folder, "notes", note.Id + ".md"),
            "team-notes" => Path.Combine(folder, "team-notes", team.Id + ".md"),
            _ => documents.First(pair => pair.Key.StartsWith(family + ":")).Value
        };
        var original = File.ReadAllBytes(file);
        if (family is "notes" or "team-notes")
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));
        else
        {
            var data = JsonNode.Parse(original)!;
            if (family is "photos" or "teamContext") data["recordVersions"]![family] = 99;
            else if (family == "acceptedBatches") data.AsObject().First().Value!["source"]!["schemaVersion"] = 99;
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
        File.WriteAllBytes(file, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(WorkspaceCompatibilityState.Compatible, app.Compatibility.State);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose);
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal("# Person writing\n**Preserved** café.", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Apply_checks_older_delivery_before_any_write_even_before_refresh()
    {
        WorkspaceFixtures.CopyCurrent(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var preview = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"14000000-0000-4000-8000-000000000004",
            "observedAt":"2026-11-04T17:00:00Z","people":[{"email":"avery@example.com","preferredName":"Do not apply"}]}
            """);
        var pointer = Path.Combine(folder, "workspace.json");
        var data = JsonNode.Parse(File.ReadAllText(pointer))!;
        data["schemaVersion"] = 2;
        File.WriteAllText(pointer, data.ToJsonString());
        var before = WorkspaceFixtures.Bytes(folder);
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        Assert.Contains("does not migrate", app.Compatibility.Guidance);
        AssertBytesUnchanged(before);
    }

    [Fact]
    public void Duplicate_family_versions_cannot_hide_an_unsupported_encoding()
    {
        WorkspaceFixtures.CopyCurrent(folder);
        var pointer = Path.Combine(folder, "workspace.json");
        File.WriteAllText(pointer, File.ReadAllText(pointer).Replace("\"people\":2", "\"people\":99,\"people\":2"));
        var before = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(folder));
        Assert.Contains("duplicate", app.Status);
        AssertBytesUnchanged(before);
    }

    [Fact]
    public void Missing_recording_provenance_is_not_guessed_or_repaired()
    {
        WorkspaceFixtures.CopyCurrent(folder);
        var history = WorkspaceFixtures.IndexedDocuments(folder)["profileHistory:avery@example.com"];
        var data = JsonNode.Parse(File.ReadAllText(history))!;
        data["observations"]![0]!.AsObject().Remove("recordedAt");
        File.WriteAllText(history, data.ToJsonString());
        var before = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(folder));
        Assert.False(app.IsWorkspaceCurrent);
        AssertBytesUnchanged(before);
    }

    [Fact]
    public async Task Incompatible_clients_do_not_hold_read_leases_that_block_external_delivery()
    {
        WorkspaceFixtures.CopyCurrent(folder);
        var pointer = Path.Combine(folder, "workspace.json");
        var supported = File.ReadAllBytes(pointer);
        var data = JsonNode.Parse(supported)!;
        data["schemaVersion"] = 99;
        File.WriteAllText(pointer, data.ToJsonString());
        var original = File.ReadAllBytes(pointer);
        var before = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(folder));
        using var start = new ManualResetEventSlim();
        var reads = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 500; i++) app.RefreshWorkspace();
        });
        start.Set();
        try { for (var i = 0; i < 500; i++) File.WriteAllBytes(pointer, original); }
        finally { await reads.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.False(app.RefreshWorkspace());
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        AssertBytesUnchanged(before);
        File.WriteAllBytes(pointer, supported);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    private void AssertBytesUnchanged(Dictionary<string, byte[]> expected)
    {
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(expected.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, after[path]);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
