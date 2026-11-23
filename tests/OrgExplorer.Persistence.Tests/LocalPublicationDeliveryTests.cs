using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class LocalPublicationDeliveryTests
{
    [Fact]
    public void Accepted_import_source_reopens_with_the_same_numeric_version_rules_as_preview()
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", Guid.NewGuid().ToString("N")));
        try
        {
            using var app = ApplicationWorkflow.CreateFileBacked();
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(app.OpenWorkspace(folder), app.Status);
            const string batch = """
                {"schemaVersion":1.0,"batchId":"20000000-0013-4000-8000-999999999996",
                 "observedAt":"2026-11-06T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """;
            var preview = app.PreviewImport(batch);
            Assert.True(preview.CanApply, app.Status);
            Assert.True(app.ApplyImport(preview.Id), app.Status);
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Avery", Assert.Single(reopened.People).DirectoryName);
            Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(batch).Changes));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
