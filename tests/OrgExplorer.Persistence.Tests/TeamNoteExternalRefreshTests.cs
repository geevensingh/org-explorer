using System.Text.Json;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class TeamNoteExternalRefreshTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Snapshot_delivery_refreshes_owned_and_inherited_Team_writing_review_and_history_together()
    {
        WorkspaceFixtures.CopyCurrent(Path.Combine(root, "source"));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(app.OpenWorkspace(Path.Combine(root, "receiver")), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        var team = Assert.Single(source.GetTeamNotes("casey@example.com"));
        var direct = Assert.Single(source.GetPersonNotes("avery@example.com"));
        var frozen = JsonSerializer.Serialize(Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")));
        Assert.True(source.EditTeamNote(team.Id, team.Title, "External Team writing"), source.Status);
        Assert.True(source.EditPersonNote(direct.Id, direct.Title, "External Person writing"), source.Status);
        Assert.True(source.EditPersonText("casey@example.com", PersonTextField.JobTitle, "Director"), source.Status);
        WorkspaceFixtures.DeliverSnapshot(source, app);
        var inherited = Assert.Single(app.GetInheritedTeamNotes("avery@example.com"));
        Assert.Equal("casey@example.com", inherited.Owner.Email);
        Assert.Equal("Casey Example", inherited.Owner.Heading);
        Assert.Equal("External Team writing", inherited.Note.Markdown);
        Assert.True(inherited.Note.NeedsReview);
        Assert.Equal("External Person writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Equal("Director", app.GetRelationships("casey@example.com")!.Person.JobTitle);
        Assert.Equal(frozen, JsonSerializer.Serialize(Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"))));
        Assert.Single(app.GetProfileTimeline("casey@example.com")!.Changes);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(Assert.Single(app.GetTeamNotes("casey@example.com")).NeedsReview);
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("metadata")]
    [InlineData("confirmation")]
    public void Invalid_snapshot_Team_delivery_preserves_all_last_valid_writing_and_review_flags(string invalid)
    {
        WorkspaceFixtures.CopyCurrent(Path.Combine(root, "source"));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(source.EditPersonText("casey@example.com", PersonTextField.JobTitle, "Director"));
        Assert.True(app.OpenWorkspace(Path.Combine(root, "receiver")), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        var team = Assert.Single(source.GetTeamNotes("casey@example.com"));
        var direct = Assert.Single(source.GetPersonNotes("avery@example.com"));
        Assert.True(source.EditPersonNote(direct.Id, direct.Title, "Pending Person writing"));
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        var file = WorkspaceFixtures.LatestSnapshot(source);
        var valid = File.ReadAllBytes(file);
        WorkspaceFixtures.RewriteSnapshot(file, content =>
        {
            var row = content["notes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == team.Id.ToString())!;
            if (invalid == "owner") row["owner"] = "missing@example.com";
            else if (invalid == "metadata") row["metadata"] = "{";
            else
            {
                var metadata = System.Text.Json.Nodes.JsonNode.Parse(row["metadata"]!.GetValue<string>())!;
                metadata["confirmedContextRevision"] = "not-a-revision";
                row["metadata"] = metadata.ToJsonString();
            }
        });
        var rejected = File.ReadAllBytes(file);
        Assert.False(app.SyncNow());
        Assert.Equal("# Person writing\n**Preserved** café.", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Equal(team.Markdown, Assert.Single(app.GetTeamNotes("casey@example.com")).Markdown);
        Assert.True(Assert.Single(app.GetTeamNotes("casey@example.com")).NeedsReview);
        Assert.Equal(rejected, File.ReadAllBytes(file));
        File.WriteAllBytes(file, valid);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Equal("Pending Person writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.True(Assert.Single(app.GetTeamNotes("casey@example.com")).NeedsReview);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
