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
    public void Reporting_move_archive_and_replay_preserve_text_and_private_notes_and_allow_external_refresh()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        const string initial = """
            {"schemaVersion":1,"batchId":"60000000-0000-4000-8000-000000000001",
            "observedAt":"2026-11-01T17:00:00Z","people":[
            {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"casey@example.com",
             "alias":"avery","officeNumber":"007B","department":"Research"},
            {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(initial).Id), app.Status);
        var personNote = app.CreatePersonNote("avery@example.com", "Private context", "# Avery\nPrivate writing")!;
        var managerNote = app.CreatePersonNote("casey@example.com", "Manager context", "Not inherited")!;
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        var noteFile = Path.Combine(folder, "notes", personNote.Id.ToString("D") + ".md");
        var originalDocument = File.ReadAllText(noteFile);
        const string move = """
            {"schemaVersion":1,"batchId":"60000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"casey@example.com","archived":true},
            {"email":"avery@example.com","managerEmail":null,"officeNumber":"0009C","department":null}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(move).Id), app.Status);
        var replay = app.PreviewImport(initial);
        Assert.True(replay.CanApply);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(app.ApplyImport(replay.Id), app.Status);
        Assert.Equal(originalDocument, File.ReadAllText(noteFile));
        File.WriteAllText(noteFile, originalDocument + "\nEdited externally after reporting move.");
        Assert.True(app.RefreshPersonNotes(), app.Status);
        Assert.Equal(personNote.Markdown + "\nEdited externally after reporting move.",
            Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var person = reopened.GetRelationships("avery@example.com")!;
        Assert.Null(person.Manager);
        Assert.Equal("Ave", person.Person.PreferredName);
        Assert.Equal("avery", person.Person.Alias);
        Assert.Equal("0009C", person.Person.OfficeNumber);
        Assert.Null(person.Person.Department);
        Assert.Equal("avery@example.com", Assert.Single(reopened.ReportingRoots).Person.Email);
        Assert.True(reopened.GetRelationships("casey@example.com")!.Person.Archived);
        Assert.Equal(managerNote, Assert.Single(reopened.GetPersonNotes("casey@example.com")));
        Assert.Equal(personNote.Id, Assert.Single(reopened.GetPersonNotes("avery@example.com")).Id);
    }

    [Fact]
    public void Failed_note_save_and_archive_never_publish_unsaved_writing_and_can_be_retried()
    {
        using var app = Open();
        var original = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        var file = Assert.Single(Directory.GetFiles(Path.Combine(folder, "notes"), "*.md"));
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(app.EditPersonNote(original.Id, "Unsaved title", "Unsaved writing"));
            Assert.Contains("NOT saved", app.Status);
            Assert.Equal(original, Assert.Single(app.GetPersonNotes("avery@example.com")));
            Assert.False(app.ArchivePersonNote(original.Id));
            Assert.Contains("NOT saved", app.Status);
            Assert.False(Assert.Single(app.GetPersonNotes("avery@example.com")).Archived);
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(original, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        Assert.True(app.EditPersonNote(original.Id, "Recovered title", "Recovered writing"), app.Status);
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Recovered writing", Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Single(Directory.GetFiles(Path.Combine(folder, "notes"), "*.md"));
    }

    [Fact]
    public void Failed_Person_draft_retry_cannot_overwrite_external_writing_and_discard_accepts_the_delivered_version()
    {
        using var app = Open();
        var note = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        var file = Path.Combine(folder, "notes", note.Id + ".md");
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(app.EditPersonNote(note.Id, note.Title, "Unsaved competing draft"));
        Assert.False(app.CanSafelyClose);
        File.WriteAllText(file, File.ReadAllText(file).Replace("Saved writing", "Delivered writing"));
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal("Delivered writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.False(app.EditPersonNote(note.Id, note.Title, "Unsaved competing draft"));
        Assert.Contains("changed externally", app.Status);
        Assert.False(app.CanSafelyClose);
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Delivered writing", Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
        }
        app.DiscardUnsavedChanges(note.Id);
        Assert.True(app.CanSafelyClose);
        Assert.True(app.EditPersonNote(note.Id, note.Title, "Deliberate delivered-version edit"), app.Status);
        using var edited = ApplicationWorkflow.CreateFileBacked();
        Assert.True(edited.OpenWorkspace(folder), edited.Status);
        Assert.Equal("Deliberate delivered-version edit", Assert.Single(edited.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Concurrent_profile_and_note_changes_are_accepted_as_one_validated_Workspace()
    {
        using var first = Open();
        var note = first.CreatePersonNote("avery@example.com", "Context", "Original writing")!;
        using var second = ApplicationWorkflow.CreateFileBacked();
        Assert.True(second.OpenWorkspace(folder), second.Status);
        Assert.True(second.EditPreferredName("avery@example.com", "Ave"), second.Status);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(folder, "notes"), "*.md"));
        File.WriteAllText(file, File.ReadAllText(file).Replace("Original writing", "Externally edited writing"));
        Assert.True(first.RefreshPersonNotes(), first.Status);
        Assert.Equal("Ave", Assert.Single(first.People).PreferredName);
        Assert.Equal("Externally edited writing", Assert.Single(first.GetPersonNotes("avery@example.com")).Markdown);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
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
        using var app = Open();
        app.CreatePersonNote("avery@example.com", "Context", "Preserved writing");
        var file = Assert.Single(Directory.GetFiles(Path.Combine(folder, "notes"), "*.md"));
        var invalid = File.ReadAllText(file).Replace(oldValue, newValue);
        File.WriteAllText(file, invalid);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(folder));
        Assert.Contains("could not be opened", reopened.Status);
        Assert.Equal(invalid, File.ReadAllText(file));
    }

    [Fact]
    public void Synced_note_document_refreshes_without_import_and_invalid_delivery_keeps_last_good_writing()
    {
        using var app = Open();
        var note = app.CreatePersonNote("avery@example.com", "Context", "Original writing")!;
        var file = Assert.Single(Directory.GetFiles(Path.Combine(folder, "notes"), "*.md"));
        var original = File.ReadAllText(file);
        File.WriteAllText(file, original.Replace("Original writing", "Externally edited writing"));
        Assert.True(app.RefreshPersonNotes(), app.Status);
        Assert.Equal("Externally edited writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        File.WriteAllText(file, "Incomplete delivery");
        Assert.False(app.RefreshPersonNotes());
        Assert.Contains("STALE", app.Status);
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
        using var app = Open();
        var note = app.CreatePersonNote("avery@example.com", "Draft", "Before")!;
        Assert.True(app.EditPersonNote(note.Id, "Context", "# Heading\r\nAfter **editing**.\r\n"), app.Status);
        Assert.True(app.ArchivePersonNote(note.Id), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
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
        using var app = Open();
        var first = app.CreatePersonNote("AVERY@example.com", "Meeting context", "# Meeting\nPrivate **writing**.");
        var second = app.CreatePersonNote("avery@example.com", "Follow-up", "Second document.");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(first, reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == first.Id));
        Assert.Equal(second, reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == second.Id));
        var documents = Directory.GetFiles(Path.Combine(folder, "notes"), "*.md");
        Assert.Equal(2, documents.Length);
        Assert.Contains(documents, p => File.ReadAllText(p).Contains("# Meeting\nPrivate **writing**."));
        Assert.Contains(documents, p => File.ReadAllText(p).Contains("avery@example.com"));
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
