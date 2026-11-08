using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class WorkspaceCompatibilityTests
{
    [Fact]
    public void Global_compatibility_guidance_is_observable_without_a_loaded_Workspace()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.Equal(WorkspaceCompatibilityState.NotOpened, app.Compatibility.State);
        Assert.Contains("Settings", app.Compatibility.Guidance);
        var notifications = new List<string?>();
        app.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.Equal(WorkspaceCompatibilityState.Compatible, app.Compatibility.State);
        Assert.Equal(2, app.Compatibility.SupportedWorkspaceVersion);
        Assert.Contains(nameof(app.Compatibility), notifications);
    }

    [Fact]
    public void View_model_reports_future_data_globally_without_a_compatible_Workspace_or_any_writes()
    {
        var folder = FixturePath();
        try
        {
            WorkspaceFixtures.CopyVersion(99, folder);
            var before = WorkspaceFixtures.Bytes(folder);
            using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
            var notifications = new List<string?>();
            app.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            Assert.False(app.OpenWorkspace(folder));
            Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
            Assert.Equal(99, app.Compatibility.DataVersion);
            Assert.Contains("Upgrade", app.Compatibility.Guidance);
            Assert.Contains("Settings", app.Compatibility.Guidance);
            Assert.Contains(nameof(app.Compatibility), notifications);
            Assert.Empty(app.People);
            Assert.False(app.RefreshWorkspace());
            Assert.False(app.PreviewImport(WorkflowTests.Batch).CanApply);
            Assert.False(File.Exists(Path.Combine(folder, ".workspace.lock")));
            var after = WorkspaceFixtures.Bytes(folder);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void View_model_reports_failed_conversion_and_validated_retry_without_fabricating_loaded_state()
    {
        var folder = FixturePath();
        try
        {
            WorkspaceFixtures.CopyVersionOne(folder);
            using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
            using (File.Open(Path.Combine(folder, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(app.OpenWorkspace(folder));
                Assert.Equal(WorkspaceCompatibilityState.RecoveryRequired, app.Compatibility.State);
                Assert.NotNull(app.Compatibility.RecoveryPath);
                Assert.False(app.IsWorkspaceCurrent);
                Assert.Empty(app.People);
            }
            Assert.True(app.RefreshWorkspace(), app.Status);
            Assert.Equal(WorkspaceCompatibilityState.Compatible, app.Compatibility.State);
            Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.Heading);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string FixturePath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
}
