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

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
