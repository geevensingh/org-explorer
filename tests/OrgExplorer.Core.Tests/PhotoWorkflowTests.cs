using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class PhotoWorkflowTests
{
    [Fact]
    public void An_omitted_creation_photo_and_explicit_null_have_a_clear_unavailable_projection()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id), app.Status);
        Assert.Null(app.GetPhoto("avery@example.com"));
        var clear = WorkflowTests.Batch.Replace("000000000001", "000000000002")
            .Replace("\"managerEmail\":null", "\"managerEmail\":null,\"photoPath\":null");
        Assert.True(app.ApplyImport(app.PreviewImport(clear).Id), app.Status);
        Assert.Null(app.GetPhoto("AVERY@example.com"));
        Assert.Equal("Avery Example", Assert.Single(app.People).DirectoryName);
    }

    [Theory]
    [InlineData("\"https://example.com/photo.png\"")]
    [InlineData("\"file:///C:/photo.png\"")]
    [InlineData("\"local.png\"")]
    [InlineData("42")]
    [InlineData("\"\"")]
    public void A_supplied_photo_requires_valid_local_file_context_and_never_becomes_a_placeholder(string value)
    {
        var app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        var preview = app.PreviewImport(WorkflowTests.Batch.Replace("\"managerEmail\":null",
            "\"managerEmail\":null,\"photoPath\":" + value));
        Assert.False(preview.CanApply);
        Assert.Contains("avery@example.com.photoPath", string.Join("\n", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Empty(app.People);
    }
}
