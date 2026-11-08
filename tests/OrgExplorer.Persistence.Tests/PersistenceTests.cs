using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".test-workspaces", Guid.NewGuid().ToString()));
    private const string Batch = """
        {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
        """;

    [Fact]
    public void Applied_Person_and_local_preferred_name_survive_reopening()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        Assert.True(app.EditPreferredName("AVERY@example.com", "Ave"), app.Status);
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var person = Assert.Single(reopened.People);
        Assert.Equal("Ave", person.Heading);
        Assert.Equal("Avery Example", person.DirectoryName);
    }

    [Fact]
    public void Changed_import_file_invalidates_preview_without_creating_a_Person()
    {
        Directory.CreateDirectory(folder);
        var import = Path.Combine(folder, "import.json");
        File.WriteAllText(import, Batch);
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Path.Combine(folder, "workspace")));
        var preview = app.PreviewImportFile(import);
        File.WriteAllText(import, Batch.Replace("Avery Example", "Changed Example"));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Empty(app.People);
        Assert.Contains("changed", app.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Utf8_import_with_byte_order_mark_is_accepted()
    {
        Directory.CreateDirectory(folder);
        var import = Path.Combine(folder, "import.json");
        File.WriteAllText(import, Batch, new System.Text.UTF8Encoding(true));
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Path.Combine(folder, "workspace")));
        var preview = app.PreviewImportFile(import);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    [Fact]
    public void Save_failure_is_visible_and_reopening_retains_last_successfully_saved_name()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id));
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"));
        var recovery = folder + ".recovery";
        Directory.Move(folder, recovery);
        File.WriteAllText(folder, "Simulated unavailable Workspace path");
        try
        {
            Assert.False(app.EditPreferredName("avery@example.com", "Unsaved"));
            Assert.Contains("NOT saved", app.Status);
            Assert.Equal("Ave", Assert.Single(app.People).PreferredName);
        }
        finally
        {
            File.Delete(folder);
            Directory.Move(recovery, folder);
        }
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Ave", Assert.Single(reopened.People).PreferredName);
    }

    [Fact]
    public void Another_client_save_invalidates_old_preview_and_is_not_overwritten()
    {
        var first = ApplicationWorkflow.CreateFileBacked();
        Assert.True(first.OpenWorkspace(folder));
        Assert.True(first.ApplyImport(first.PreviewImport(Batch).Id));
        var second = ApplicationWorkflow.CreateFileBacked();
        Assert.True(second.OpenWorkspace(folder));
        var preview = second.PreviewImport(Batch.Replace("000000000001", "000000000002").Replace("Avery Example", "Changed Directory"));
        Assert.True(first.EditPreferredName("avery@example.com", "Ave"));
        Assert.False(second.ApplyImport(preview.Id));
        Assert.Contains("changed", second.Status);
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder));
        Assert.Equal("Ave", Assert.Single(reopened.People).PreferredName);
        Assert.Equal("Avery Example", Assert.Single(reopened.People).DirectoryName);
    }

    [Fact]
    public void Invalid_import_does_not_change_durable_state()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder));
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id));
        var invalid = app.PreviewImport(Batch.Replace("000000000001", "000000000002").Replace("\"managerEmail\":null", "\"notes\":\"not allowed\",\"managerEmail\":null"));
        Assert.False(invalid.CanApply);
        Assert.False(app.ApplyImport(invalid.Id));
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder));
        Assert.Equal("Avery Example", Assert.Single(reopened.People).DirectoryName);
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
