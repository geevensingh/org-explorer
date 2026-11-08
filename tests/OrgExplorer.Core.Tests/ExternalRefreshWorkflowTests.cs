using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ExternalRefreshWorkflowTests
{
    [Fact]
    public void Shared_refresh_seam_preserves_a_review_and_private_writing_when_nothing_external_changed()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.IsWorkspaceCurrent);
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var note = app.CreatePersonNote("avery@example.com", "Context", "# Private writing")!;
        var preview = app.PreviewImport(WorkflowTests.Batch);
        Assert.True(app.RefreshWorkspace());
        Assert.True(app.ApplyImport(preview.Id));
        Assert.Equal(note, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Assert.Contains("no changes", app.Status);
        Assert.True(app.CanSafelyClose);
        Assert.Null(app.LocalSaveError);
    }

    [Fact]
    public void Rejected_import_validation_does_not_claim_a_failed_local_save_or_block_safe_close()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        app.OpenWorkspace("fictitious");
        var invalid = app.PreviewImport(WorkflowTests.Batch.Replace("\"managerEmail\":null", "\"managerEmail\":\"missing@example.com\""));
        Assert.False(invalid.CanApply);
        Assert.True(app.IsWorkspaceCurrent);
        Assert.True(app.CanSafelyClose);
        Assert.Null(app.LocalSaveError);
        Assert.Contains("unknown Manager", app.Status);
    }
}
