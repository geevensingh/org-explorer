using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class WorkspaceReadLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refreshing_client_does_not_block_another_clients_local_autosave(bool favorite)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", Guid.NewGuid().ToString("N")));
        using var cancellation = new CancellationTokenSource();
        using var writer = ApplicationWorkflow.CreateFileBacked();
        using var reader = ApplicationWorkflow.CreateFileBacked();
        Task? refreshing = null;
        try
        {
            Assert.True(writer.OpenWorkspace(folder), writer.Status);
            var preview = writer.PreviewImport("""
                {"schemaVersion":1,"batchId":"20000000-0013-4000-8000-999999999991",
                 "observedAt":"2026-11-06T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """);
            Assert.True(writer.ApplyImport(preview.Id), writer.Status);
            var text = new string('x', 64 * 1024);
            var note = writer.CreateTeamNote("avery@example.com", "Context", text)!;
            Assert.NotNull(note);
            Assert.True(reader.OpenWorkspace(folder), reader.Status);
            using var started = new ManualResetEventSlim();
            refreshing = Task.Factory.StartNew(() =>
            {
                started.Set();
                while (!cancellation.IsCancellationRequested) reader.RefreshWorkspace();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 20; i++)
                Assert.True(favorite ? writer.EditFavorite("avery@example.com", i % 2 == 0) :
                    writer.EditTeamNote(note.Id, "Context", text + i), writer.Status);
            cancellation.Cancel();
            await refreshing;
            Assert.True(writer.CanSafelyClose, writer.Status);
            Assert.True(reader.RefreshWorkspace(), reader.Status);
            Assert.Equal(favorite ? text : text + "19", Assert.Single(reader.GetTeamNotes("avery@example.com")).Markdown);
            if (favorite) Assert.Empty(reader.FavoritePeople);
        }
        finally
        {
            cancellation.Cancel();
            if (refreshing is not null) await refreshing;
            reader.Dispose();
            writer.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_disposed_client_does_not_read_or_notify_after_its_Workspace_is_removed()
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", Guid.NewGuid().ToString("N")));
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var notifications = 0;
        app.WorkspaceChanged += (_, _) => notifications++;
        app.Dispose();
        Directory.Delete(folder, true);
        Assert.False(app.RefreshWorkspace());
        Assert.Contains("closed", app.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData("workspace.next")]
    [InlineData("workspace.previous.json")]
    [InlineData("notes\\20000000-0013-4000-8000-999999999992.next")]
    [InlineData("team-notes\\20000000-0013-4000-8000-999999999993.next")]
    public void Noncanonical_publication_and_recovery_files_do_not_invalidate_the_committed_Workspace(string relative)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", Guid.NewGuid().ToString("N")));
        using var app = ApplicationWorkflow.CreateFileBacked();
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        try
        {
            Assert.True(app.OpenWorkspace(folder), app.Status);
            var preview = app.PreviewImport("""
                {"schemaVersion":1,"batchId":"20000000-0013-4000-8000-999999999994",
                 "observedAt":"2026-11-06T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """);
            Assert.True(app.ApplyImport(preview.Id), app.Status);
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var staging = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            staging.Write("Uncommitted partial document, not a Person or note"u8);
            staging.Flush(true);
            Assert.True(app.RefreshWorkspace(), app.Status);
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Avery", Assert.Single(reopened.People).Heading);
            Assert.Empty(reopened.GetPersonNotes("avery@example.com"));
            Assert.Empty(reopened.GetTeamNotes("avery@example.com"));
            Assert.True(app.IsWorkspaceCurrent);
        }
        finally
        {
            reopened.Dispose();
            app.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
