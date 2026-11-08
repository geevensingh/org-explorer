using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class ReportingPersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Manager = """{"email":"manager@example.com","displayName":"Manager","managerEmail":null}""";
    private const string Report = """{"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com"}""";
    private static string Batch(string records, int id = 1) => $$"""
        {"schemaVersion":1,"batchId":"50000000-0000-4000-8000-{{id:000000000000}}",
        "observedAt":"2026-11-01T17:00:00Z","people":[{{records}}]}
        """;

    [Fact]
    public void Whole_graph_apply_is_durable_and_invalid_batch_never_changes_reopened_state()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(Report + "," + Manager)).Id), app.Status);
        var invalid = app.PreviewImport(Batch("""
            {"email":"manager@example.com","archived":true},
            {"email":"new@example.com","displayName":"New","managerEmail":null}
            """, 2));
        Assert.False(invalid.CanApply);
        Assert.False(app.ApplyImport(invalid.Id));
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(2, reopened.People.Count);
        Assert.Equal("manager@example.com", reopened.GetRelationships("report@example.com")!.Manager!.Email);
        Assert.Single(reopened.ReportingRoots);
        var valid = app.PreviewImport(Batch("""
            {"email":"manager@example.com","archived":true},
            {"email":"report@example.com","managerEmail":null}
            """, 3));
        Assert.True(valid.CanApply, app.Status);
        Assert.True(app.ApplyImport(valid.Id), app.Status);
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(2, reopened.People.Count);
        Assert.True(reopened.GetRelationships("manager@example.com")!.Person.Archived);
        Assert.Equal("report@example.com", Assert.Single(reopened.ReportingRoots).Person.Email);
    }

    [Fact]
    public void Accepted_batch_identity_survives_reopen_and_logical_retry_does_not_undo_later_edit()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(Report + "," + Manager)).Id));
        Assert.True(app.EditPreferredName("report@example.com", "Reference name"));
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder));
        var retry = reopened.PreviewImport(Batch(Manager + ",\n   " + Report));
        Assert.True(retry.CanApply, reopened.Status);
        Assert.Contains("no-op", Assert.Single(retry.Changes));
        Assert.True(reopened.ApplyImport(retry.Id), reopened.Status);
        Assert.Contains("no changes", reopened.Status);
        Assert.Equal("Reference name", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
        var conflict = reopened.PreviewImport(Batch(Manager + "," + Report.Replace("Report", "Different")));
        Assert.False(conflict.CanApply);
        Assert.Contains("batchId", string.Join("\n", conflict.Errors));
        Assert.True(app.OpenWorkspace(folder));
        Assert.Equal("Reference name", app.GetRelationships("report@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Successful_local_mutation_requires_a_fresh_import_review()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch(Manager)).Id));
        var preview = app.PreviewImport(Batch("""{"email":"manager@example.com","displayName":"Changed"}""", 2));
        Assert.True(app.EditPreferredName("manager@example.com", "My reference"));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Contains("stale", app.Status);
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder));
        Assert.Equal("Manager", Assert.Single(reopened.People).DirectoryName);
        Assert.Equal("My reference", Assert.Single(reopened.People).PreferredName);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
