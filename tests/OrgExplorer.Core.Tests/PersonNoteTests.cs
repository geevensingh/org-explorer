using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class PersonNoteTests
{
    [Theory]
    [InlineData("notes")]
    [InlineData("personNotes")]
    [InlineData("teamNotes")]
    [InlineData("noteOperations")]
    [InlineData("createPersonNote")]
    [InlineData("archiveTeamNote")]
    public void Import_note_payloads_and_operations_are_rejected_without_changing_private_Person_notes(string property)
    {
        IApplicationWorkflow app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id);
        var note = app.CreatePersonNote("avery@example.com", "Private context", "Workspace-owned writing")!;
        var invalid = WorkflowTests.Batch.Replace("000000000001", "000000000002")
            .Replace("\"managerEmail\":null", $"\"{property}\":\"Rejected\", \"managerEmail\":null");
        var preview = app.PreviewImport(invalid);
        Assert.False(preview.CanApply);
        Assert.Contains(property, string.Join(" ", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal(note, Assert.Single(app.GetPersonNotes("avery@example.com")));
    }

    [Fact]
    public void Edit_and_archive_preserve_identity_ownership_and_writing_without_inheritance()
    {
        IApplicationWorkflow app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id);
        var original = app.CreatePersonNote("avery@example.com", "Draft", "Before")!;
        Assert.True(app.EditPersonNote(original.Id, "Context", "After **editing**"), app.Status);
        Assert.True(app.ArchivePersonNote(original.Id), app.Status);
        Assert.Empty(app.GetPersonNotes("avery@example.com"));
        var archived = Assert.Single(app.GetPersonNotes("avery@example.com", includeArchived: true));
        Assert.True(archived.Archived);
        Assert.Equal(original.Id, archived.Id);
        Assert.Equal(original.OwnerEmail, archived.OwnerEmail);
        Assert.Equal(original.CreatedAt, archived.CreatedAt);
        Assert.Equal("After **editing**", archived.Markdown);
        Assert.True(app.ArchivePersonNote(original.Id, archived: false));
        Assert.Equal("Context", Assert.Single(app.GetPersonNotes("avery@example.com")).Title);
        Assert.False(app.EditPersonNote(original.Id, " ", "Unsaved"));
        Assert.Contains("nonblank", app.Status);
        Assert.Equal("After **editing**", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Multiple_private_Person_notes_have_stable_ownership_and_do_not_inherit_to_reports()
    {
        IApplicationWorkflow app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        var batch = WorkflowTests.Batch.Replace("\"managerEmail\":null}",
            "\"managerEmail\":null},{\"email\":\"report@example.com\",\"displayName\":\"Report Example\",\"managerEmail\":\"avery@example.com\"}");
        Assert.True(app.ApplyImport(app.PreviewImport(batch).Id));
        var first = app.CreatePersonNote("AVERY@example.com", "Meeting", "# Context\nPrivate writing");
        var second = app.CreatePersonNote("avery@example.com", "Follow-up", "Another document");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("avery@example.com", first.OwnerEmail);
        Assert.Equal(2, app.GetPersonNotes("AVERY@example.com").Count);
        Assert.Empty(app.GetPersonNotes("report@example.com"));
    }
}
