using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class ExternalRefreshTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Complete_snapshot_refreshes_open_profile_photo_history_and_private_writing_without_replaying_imports()
    {
        WorkspaceFixtures.CopyCurrent(Path.Combine(root, "source"));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(app.OpenWorkspace(Path.Combine(root, "receiver")), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        var note = Assert.Single(source.GetPersonNotes("avery@example.com"));
        var capture = Assert.Single(source.GetHistoricalTeamContexts("avery@example.com"));
        var notifications = 0;
        app.WorkspaceChanged += (_, _) => notifications++;
        Assert.True(source.EditPreferredName("avery@example.com", "Snapshot Avery"), source.Status);
        Assert.True(source.EditPersonNote(note.Id, "Revised", "# Revised café writing"), source.Status);
        WorkspaceFixtures.DeliverSnapshot(source, app);
        Assert.Equal("Snapshot Avery", app.GetRelationships("avery@example.com")!.Person.Heading);
        Assert.Equal("# Revised café writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.NotNull(app.GetPhoto("avery@example.com"));
        Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.True(notifications > 0);
        var revision = app.Synchronization.LocalRevision;
        for (var i = 0; i < 3; i++) Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Equal(revision, app.Synchronization.LocalRevision);
        Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Single(app.GetProfileTimeline("avery@example.com")!.Changes, c => c.Field == "preferredName");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_or_incomplete_snapshot_photo_never_replaces_the_last_decoded_photo(bool incomplete)
    {
        WorkspaceFixtures.CopyCurrent(Path.Combine(root, "source"));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(app.OpenWorkspace(Path.Combine(root, "receiver")), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "transport"));
        var photo = app.GetPhoto("avery@example.com")!;
        Assert.True(source.EditPreferredName("avery@example.com", "Next revision"));
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        var path = WorkspaceFixtures.LatestSnapshot(source);
        var complete = File.ReadAllBytes(path);
        if (incomplete) File.WriteAllBytes(path, complete[..(complete.Length / 2)]);
        else WorkspaceFixtures.RewriteSnapshot(path, content =>
            content["photos"]![0]!["content"] = Convert.ToBase64String("Not a decoded image"u8));
        var rejected = File.ReadAllBytes(path);
        Assert.False(app.SyncNow());
        Assert.Equal(photo.Bytes.ToArray(), app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.Heading);
        Assert.Equal(rejected, File.ReadAllBytes(path));
        File.WriteAllBytes(path, complete);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Equal("Next revision", app.GetRelationships("avery@example.com")!.Person.Heading);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
