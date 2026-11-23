using Microsoft.Data.Sqlite;
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
    [InlineData(3)]
    [InlineData(99)]
    public void Readable_file_Workspaces_are_refused_without_conversion_repair_or_mutation(int version)
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
        Assert.Contains("empty folder", app.Compatibility.Guidance);
        Assert.Contains("Preserve", app.Compatibility.Guidance);
        Assert.Empty(app.People);
        Assert.Null(app.Compatibility.RecoveryPath);
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        Assert.Equal(directories, Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).Order());
        Assert.False(File.Exists(Path.Combine(folder, "workspace.sqlite")));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Future_local_note_version_is_unsupported_with_upgrade_guidance_and_without_mutation(NoteScope scope)
    {
        WorkspaceFixtures.CopyCurrent(folder);
        using (var fixture = new SqliteConnection($"Data Source={Path.Combine(folder, "workspace.sqlite")};Pooling=False"))
        {
            fixture.Open();
            using var command = fixture.CreateCommand();
            command.CommandText = "UPDATE notes SET metadata=json_set(metadata,'$.schemaVersion',99) WHERE scope=$scope;";
            command.Parameters.AddWithValue("$scope", scope == NoteScope.Person ? "person" : "team");
            Assert.True(command.ExecuteNonQuery() > 0);
        }
        var before = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();

        Assert.False(app.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        Assert.Equal(99, app.Compatibility.DataVersion);
        Assert.Contains($"Unsupported {scope} note data version 99", app.Compatibility.Guidance);
        Assert.Contains("Upgrade the app", app.Compatibility.Guidance);
        Assert.Contains("Workspace writes are blocked", app.Compatibility.Guidance);
        Assert.DoesNotContain("expected kind", app.Compatibility.Guidance);
        Assert.False(app.IsWorkspaceCurrent);
        Assert.False(app.Compatibility.SupportsNoteEdits);
        Assert.Empty(app.People);
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    [Theory]
    [InlineData("person")]
    [InlineData("team")]
    public void Supported_local_note_version_with_wrong_kind_remains_a_format_failure(string kind)
    {
        WorkspaceFixtures.CopyCurrent(folder);
        using (var fixture = new SqliteConnection($"Data Source={Path.Combine(folder, "workspace.sqlite")};Pooling=False"))
        {
            fixture.Open();
            using var command = fixture.CreateCommand();
            command.CommandText = "UPDATE notes SET metadata=json_set(metadata,'$.kind','unknown') WHERE scope=$scope;";
            command.Parameters.AddWithValue("$scope", kind);
            Assert.True(command.ExecuteNonQuery() > 0);
        }
        var before = WorkspaceFixtures.Bytes(folder);
        using var app = ApplicationWorkflow.CreateFileBacked();

        Assert.False(app.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly));
        Assert.Equal(WorkspaceCompatibilityState.Unavailable, app.Compatibility.State);
        Assert.Null(app.Compatibility.DataVersion);
        Assert.Contains($"expected kind '{kind}'", app.Compatibility.Guidance);
        Assert.DoesNotContain("Upgrade the app", app.Compatibility.Guidance);
        Assert.False(app.IsWorkspaceCurrent);
        Assert.False(app.Compatibility.SupportsNoteEdits);
        Assert.Empty(app.People);
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Future_delivered_note_version_preserves_current_writing_and_reports_upgrade_guidance(NoteScope scope)
    {
        var source = Path.Combine(folder, "source");
        var receiver = Path.Combine(folder, "receiver");
        WorkspaceFixtures.CopyCurrent(source);
        using var provider = ApplicationWorkflow.CreateFileBacked();
        Assert.True(provider.OpenWorkspace(source), provider.Status);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(receiver), app.Status);
        WorkspaceFixtures.JoinSnapshot(provider, app, Path.Combine(folder, "delivery"));
        var owner = scope == NoteScope.Person ? "avery@example.com" : "morgan@example.com";
        var original = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, owner,
            scope == NoteScope.Person ? Assert.Single(app.GetPersonNotes(owner)).Id :
                Assert.Single(app.GetTeamNotes(owner)).Id));
        Assert.NotNull(provider.SaveNoteDraft(provider.BeginNoteDraft(scope, owner, original.Id)!,
            original.Title, "New delivered writing"));
        Assert.True(provider.SyncNow(), provider.Synchronization.Error);
        var artifact = WorkspaceFixtures.LatestSnapshot(provider);
        var validArtifact = File.ReadAllBytes(artifact);
        WorkspaceFixtures.RewriteSnapshot(artifact, content =>
        {
            var note = content["notes"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == original.Id.ToString("D"));
            var metadata = JsonNode.Parse(note!["metadata"]!.GetValue<string>())!;
            metadata["schemaVersion"] = 99;
            note["metadata"] = metadata.ToJsonString();
        });
        var before = WorkspaceFixtures.Bytes(receiver);

        Assert.False(app.SyncNow());
        Assert.Contains($"Unsupported {scope} note data version 99", app.Synchronization.Error);
        Assert.Contains("Upgrade the app", app.Synchronization.Error);
        Assert.DoesNotContain("expected kind", app.Synchronization.Error);
        Assert.True(app.IsWorkspaceCurrent);
        Assert.True(app.CanEditLocalWorkspace);
        Assert.Equal(WorkspaceCompatibilityState.Compatible, app.Compatibility.State);
        Assert.Equal(original.Markdown, app.BeginNoteDraft(scope, owner, original.Id)!.Markdown);
        var after = WorkspaceFixtures.Bytes(receiver);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);

        File.WriteAllBytes(artifact, validArtifact);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(receiver), reopened.Status);
        Assert.Equal("New delivered writing", reopened.BeginNoteDraft(scope, owner, original.Id)!.Markdown);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
