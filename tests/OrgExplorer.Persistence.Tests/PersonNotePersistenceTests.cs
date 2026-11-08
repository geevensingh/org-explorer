using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class PersonNotePersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Batch = """
        {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
        """;

    private IApplicationWorkflow Open()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        return app;
    }

    [Fact]
    public void Failed_note_save_and_archive_never_publish_unsaved_writing_and_can_be_retried()
    {
        var app = Open();
        var original = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        var file = Assert.Single(Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories));
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(app.EditPersonNote(original.Id, "Unsaved title", "Unsaved writing"));
            Assert.Contains("NOT saved", app.Status);
            Assert.Equal(original, Assert.Single(app.GetPersonNotes("avery@example.com")));
            Assert.False(app.ArchivePersonNote(original.Id));
            Assert.Contains("NOT saved", app.Status);
            Assert.False(Assert.Single(app.GetPersonNotes("avery@example.com")).Archived);
        }
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(original, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        Assert.True(app.EditPersonNote(original.Id, "Recovered title", "Recovered writing"), app.Status);
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Recovered writing", Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Single(Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public void Concurrent_profile_change_is_not_acknowledged_by_a_note_only_refresh_or_overwritten()
    {
        var first = Open();
        var note = first.CreatePersonNote("avery@example.com", "Context", "Original writing")!;
        var second = ApplicationWorkflow.CreateFileBacked();
        Assert.True(second.OpenWorkspace(folder), second.Status);
        Assert.True(second.EditPreferredName("avery@example.com", "Ave"), second.Status);
        var file = Assert.Single(Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories));
        File.WriteAllText(file, File.ReadAllText(file).Replace("Original writing", "Externally edited writing"));
        Assert.False(first.RefreshPersonNotes());
        Assert.Contains("Other Workspace files changed", first.Status);
        Assert.False(first.EditPersonNote(note.Id, "Context", "Must not overwrite"));
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Ave", Assert.Single(reopened.People).PreferredName);
        Assert.Equal("Externally edited writing", Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Theory]
    [InlineData("\"kind\": \"person\"", "\"kind\": \"team\"")]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 99")]
    [InlineData("avery@example.com", "unknown@example.com")]
    [InlineData("\"archived\": false,", "")]
    public void Invalid_or_unsupported_note_metadata_blocks_open_without_repairing_external_documents(string oldValue, string newValue)
    {
        var app = Open();
        app.CreatePersonNote("avery@example.com", "Context", "Preserved writing");
        var file = Assert.Single(Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories));
        var invalid = File.ReadAllText(file).Replace(oldValue, newValue);
        File.WriteAllText(file, invalid);
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(folder));
        Assert.Contains("could not be opened", reopened.Status);
        Assert.Equal(invalid, File.ReadAllText(file));
    }

    [Fact]
    public void Synced_note_document_refreshes_without_import_and_invalid_delivery_keeps_last_good_writing()
    {
        var app = Open();
        var note = app.CreatePersonNote("avery@example.com", "Context", "Original writing")!;
        var file = Assert.Single(Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories));
        var original = File.ReadAllText(file);
        File.WriteAllText(file, original.Replace("Original writing", "Externally edited writing"));
        Assert.True(app.RefreshPersonNotes(), app.Status);
        Assert.Equal("Externally edited writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        File.WriteAllText(file, "Incomplete delivery");
        Assert.False(app.RefreshPersonNotes());
        Assert.Contains("could not be refreshed", app.Status);
        Assert.Equal("Externally edited writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.False(app.EditPersonNote(note.Id, "Context", "Must not overwrite"));
        Assert.Equal("Incomplete delivery", File.ReadAllText(file));
        File.WriteAllText(file, original.Replace("Original writing", "Recovered external writing"));
        Assert.True(app.RefreshPersonNotes(), app.Status);
        Assert.Equal("Recovered external writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Editing_and_archiving_retain_exact_Markdown_and_stable_metadata_after_reopening()
    {
        var app = Open();
        var note = app.CreatePersonNote("avery@example.com", "Draft", "Before")!;
        Assert.True(app.EditPersonNote(note.Id, "Context", "# Heading\r\nAfter **editing**.\r\n"), app.Status);
        Assert.True(app.ArchivePersonNote(note.Id), app.Status);
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Empty(reopened.GetPersonNotes("avery@example.com"));
        var archived = Assert.Single(reopened.GetPersonNotes("avery@example.com", true));
        Assert.Equal("# Heading\r\nAfter **editing**.\r\n", archived.Markdown);
        Assert.Equal(note.Id, archived.Id);
        Assert.Equal(note.OwnerEmail, archived.OwnerEmail);
        Assert.Equal(note.CreatedAt, archived.CreatedAt);
        Assert.True(archived.Archived);
        Assert.True(reopened.ArchivePersonNote(note.Id, false), reopened.Status);
        Assert.Equal("Context", Assert.Single(reopened.GetPersonNotes("avery@example.com")).Title);
    }

    [Fact]
    public void Separate_readable_Markdown_documents_and_stable_ownership_survive_reopening_and_profile_edits()
    {
        var app = Open();
        var first = app.CreatePersonNote("AVERY@example.com", "Meeting context", "# Meeting\nPrivate **writing**.");
        var second = app.CreatePersonNote("avery@example.com", "Follow-up", "Second document.");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(first, reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == first.Id));
        Assert.Equal(second, reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == second.Id));
        var documents = Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories);
        Assert.Equal(2, documents.Length);
        Assert.Contains(documents, p => File.ReadAllText(p).Contains("# Meeting\nPrivate **writing**."));
        Assert.Contains(documents, p => File.ReadAllText(p).Contains("avery@example.com"));
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
