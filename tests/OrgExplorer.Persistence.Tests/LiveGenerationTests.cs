using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class LiveGenerationTests
{
    [Fact]
    public void Recovery_only_files_do_not_invalidate_review_of_unchanged_current_People_and_notes()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".test-workspaces", Guid.NewGuid().ToString("N")));
        try
        {
            using var app = ApplicationWorkflow.CreateFileBacked();
            Assert.True(app.OpenWorkspace(root));
            var initial = app.PreviewImport("""
                {"schemaVersion":1,"batchId":"15000000-0000-4000-8000-000000000090","observedAt":"2026-11-01T17:00:00Z",
                "people":[{"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """);
            Assert.True(app.ApplyImport(initial.Id), app.Status);
            var preview = app.PreviewImport("""
                {"schemaVersion":1,"batchId":"15000000-0000-4000-8000-000000000091","observedAt":"2026-11-02T17:00:00Z",
                "people":[{"email":"avery@example.com","preferredName":"Ave"}]}
                """);
            var recovery = Path.Combine(root, "recovery", "15000000000040008000000000000099");
            Directory.CreateDirectory(recovery);
            File.WriteAllText(Path.Combine(recovery, "original.json"), "Recovery-only bytes, not current Person data.");
            Assert.True(app.ApplyImport(preview.Id), app.Status);
            Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.Heading);
            Assert.True(app.RefreshWorkspace(), app.Status);
            Assert.Equal("Recovery-only bytes, not current Person data.", File.ReadAllText(Path.Combine(recovery, "original.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
