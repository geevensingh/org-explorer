using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public class WorkflowTests
{
    internal const string Batch = """
        {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
        """;

    [Fact]
    public void Person_is_created_only_after_deliberate_apply_and_preferred_name_retains_directory_name()
    {
        IApplicationWorkflow app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        var preview = app.PreviewImport(Batch);
        Assert.True(preview.CanApply);
        Assert.Empty(app.People);
        Assert.True(app.ApplyImport(preview.Id));
        Assert.True(app.EditPreferredName("AVERY@EXAMPLE.COM", "Ave"));
        var person = Assert.Single(app.People);
        Assert.Equal("Ave", person.Heading);
        Assert.Equal("Avery Example", person.DirectoryName);
        Assert.Null(person.ManagerEmail);
    }

    [Fact]
    public void View_model_exposes_saved_heading_and_visible_rejected_edit_status()
    {
        var viewModel = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        viewModel.OpenWorkspace("fictitious");
        viewModel.ApplyImport(viewModel.PreviewImport(Batch).Id);
        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        Assert.True(viewModel.EditPreferredName("avery@example.com", "Ave"));
        Assert.Equal("Ave", Assert.Single(viewModel.People).Heading);
        Assert.Contains(nameof(viewModel.People), notified);
        Assert.False(viewModel.EditPreferredName("avery@example.com", " "));
        Assert.Contains("nonblank", viewModel.Status);
        Assert.Equal("Ave", Assert.Single(viewModel.People).PreferredName);
    }

    [Fact]
    public void Rejected_operations_remain_explicit_while_success_cannot_clear_independent_failed_writing()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.False(app.PreviewImport("{invalid").CanApply);
        Assert.Contains("Import rejected", app.OperationError);
        Assert.Null(app.LocalSaveError);
        Assert.True(app.CanSafelyClose);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id));
        Assert.Null(app.OperationError);
        var note = app.CreatePersonNote("avery@example.com", "Private context", "Saved writing")!;
        Assert.False(app.EditPersonNote(note.Id, "", "Retained failed writing"));
        Assert.Contains("Person note", app.OperationError);

        Assert.True(app.EditFavorite("avery@example.com", true));
        Assert.Null(app.OperationError);
        Assert.Contains("Person note", app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        app.DiscardPersonTextDraft(Assert.Single(app.People), PersonTextField.PreferredName);
        Assert.Contains("Person note", app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(note.Id);
        Assert.Null(app.LocalSaveError);
        Assert.True(app.CanSafelyClose);
    }

    [Fact]
    public void Dedicated_import_view_reviews_without_mutating_and_returns_to_people_after_explicit_apply()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.False(app.IsImportOpen);
        Assert.True(app.OpenWorkspace("fictitious"));
        var notified = new List<string?>();
        app.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        Assert.True(app.TryOpenImport(), app.Status);
        Assert.True(app.IsImportOpen);
        var preview = app.PreviewImport(Batch);
        Assert.True(preview.CanApply, app.Status);
        Assert.Empty(app.People);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.True(app.IsImportOpen);
        app.ShowPeople();
        Assert.False(app.IsImportOpen);
        Assert.Null(app.ProfileEdit);
        Assert.Equal("Avery Example", Assert.Single(app.People).DirectoryName);
        Assert.Contains(nameof(app.IsImportOpen), notified);
        Assert.True(app.TryOpenImport());
        Assert.True(app.OpenWorkspace("another-fictitious"));
        Assert.False(app.IsImportOpen);
    }

    [Theory]
    [InlineData("2026-11-01T17:00:00Z", "2026-02-29T17:00:00Z", "observedAt")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2", "upgrade")]
    [InlineData("\"managerEmail\":null", "\"managerEmail\":\"missing@example.com\"", "unknown Manager")]
    [InlineData("\"managerEmail\":null", "\"managerEmail\":\"AVERY@example.com\"", "cycle")]
    [InlineData("\"managerEmail\":null", "\"notes\":\"private\", \"managerEmail\":null", "notes")]
    [InlineData("\"managerEmail\":null", "\"attributes\":{\"department\":\"Engineering\"}, \"managerEmail\":null", "attributes")]
    [InlineData(",\"managerEmail\":null", "", "creation requires")]
    [InlineData("\"displayName\":\"Avery Example\"", "\"displayName\":null", "creation requires")]
    [InlineData("\"email\":\"avery@example.com\"", "\"email\":\"Avery <avery@example.com>\"", "mailbox")]
    public void Invalid_imports_report_concrete_errors_and_do_not_mutate(string oldValue, string newValue, string error)
    {
        var app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        var preview = app.PreviewImport(Batch.Replace(oldValue, newValue));
        Assert.False(preview.CanApply);
        Assert.Contains(error, string.Join(" ", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Empty(app.People);
    }

    [Fact]
    public void Case_equivalent_partial_update_preserves_omissions_and_explicit_null_clears()
    {
        var app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(Batch).Id);
        app.EditPreferredName("avery@example.com", "Ave");
        const string update = """
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"AVERY@EXAMPLE.COM","displayName":"Avery Directory"}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(update).Id));
        Assert.Equal("Ave", Assert.Single(app.People).PreferredName);
        Assert.True(app.EditPreferredName("avery@example.com", null));
        Assert.Equal("Avery Directory", Assert.Single(app.People).Heading);
        var clear = update.Replace("000000000002", "000000000003").Replace("\"displayName\":\"Avery Directory\"", "\"displayName\":null");
        Assert.True(app.ApplyImport(app.PreviewImport(clear).Id));
        Assert.Null(Assert.Single(app.People).DirectoryName);
    }

    [Fact]
    public void Replay_does_not_overwrite_a_local_edit_and_conflicting_batch_id_is_rejected()
    {
        var app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(Batch).Id);
        app.EditPreferredName("avery@example.com", "Ave");
        var retry = app.PreviewImport(Batch);
        Assert.Contains("no-op", Assert.Single(retry.Changes));
        Assert.True(app.ApplyImport(retry.Id));
        Assert.Equal("Ave", Assert.Single(app.People).PreferredName);
        Assert.False(app.PreviewImport(Batch.Replace("Avery Example", "Different Example")).CanApply);
    }

    [Fact]
    public void Duplicate_case_equivalent_people_block_the_whole_batch()
    {
        var app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        var duplicate = Batch.Replace("\"managerEmail\":null}", "\"managerEmail\":null},{\"email\":\"AVERY@example.com\"}");
        var preview = app.PreviewImport(duplicate);
        Assert.False(preview.CanApply);
        Assert.Contains("duplicate", string.Join(" ", preview.Errors));
        Assert.Empty(app.People);
    }

    [Fact]
    public void Equivalent_json_string_encoding_is_an_exact_logical_replay()
    {
        var app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(Batch).Id);
        var preview = app.PreviewImport(Batch.Replace("Avery Example", @"\u0041very Example"));
        Assert.True(preview.CanApply, app.Status);
        Assert.Contains("no-op", Assert.Single(preview.Changes));
    }
}
