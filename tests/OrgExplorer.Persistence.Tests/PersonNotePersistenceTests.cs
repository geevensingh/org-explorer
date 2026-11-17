using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
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
    public void Reporting_move_archive_and_replay_preserve_text_and_private_notes_and_allow_public_refresh()
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
        Assert.Equal(personNote, Assert.Single(app.GetPersonNotes("avery@example.com")));
        using (var provider = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(provider.OpenWorkspace(folder), provider.Status);
            Assert.True(provider.EditPersonNote(personNote.Id, personNote.Title,
                personNote.Markdown + "\nEdited after reporting move."), provider.Status);
        }
        Assert.True(app.RefreshPersonNotes(), app.Status);
        Assert.Equal(personNote.Markdown + "\nEdited after reporting move.",
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
    public void Failed_note_save_and_archive_retain_durable_writing_until_storage_recovery_and_retry()
    {
        using var app = Open();
        var original = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            Assert.False(app.EditPersonNote(original.Id, "Unsaved title", "Unsaved writing"));
            Assert.Contains("NOT saved", app.Status);
            Assert.Equal(original, Assert.Single(app.GetPersonNotes("avery@example.com")));
            Assert.False(app.ArchivePersonNote(original.Id));
            Assert.False(Assert.Single(app.GetPersonNotes("avery@example.com")).Archived);
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(original, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.True(app.EditPersonNote(original.Id, "Recovered title", "Recovered writing"), app.Status);
        Assert.True(reopened.RefreshWorkspace(), reopened.Status);
        Assert.Equal("Recovered writing", Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Failed_Person_draft_retry_cannot_overwrite_other_committed_writing_and_discard_accepts_the_new_version()
    {
        using var app = Open();
        var note = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        using (WorkspaceFixtures.LockLocalStore(folder))
            Assert.False(app.EditPersonNote(note.Id, note.Title, "Unsaved competing draft"));
        Assert.False(app.CanSafelyClose);
        using (var provider = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(provider.OpenWorkspace(folder), provider.Status);
            Assert.True(provider.EditPersonNote(note.Id, note.Title, "Delivered writing"), provider.Status);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.Equal("Delivered writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.False(app.EditPersonNote(note.Id, note.Title, "Unsaved competing draft"));
        Assert.Contains("changed externally", app.Status);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(note.Id);
        Assert.True(app.CanSafelyClose, $"{app.LocalSave.Phase}/{app.LocalSave.Outcome}: {app.LocalSave.Error} | {app.LocalSaveError}");
        Assert.True(app.EditPersonNote(note.Id, note.Title, "Deliberate delivered-version edit"), app.Status);
        using var edited = ApplicationWorkflow.CreateFileBacked();
        Assert.True(edited.OpenWorkspace(folder), edited.Status);
        Assert.Equal("Deliberate delivered-version edit", Assert.Single(edited.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Concurrent_profile_and_note_changes_refresh_as_one_complete_Workspace()
    {
        using var first = Open();
        var note = first.CreatePersonNote("avery@example.com", "Context", "Original writing")!;
        using var second = ApplicationWorkflow.CreateFileBacked();
        Assert.True(second.OpenWorkspace(folder), second.Status);
        Assert.True(second.EditPreferredName("avery@example.com", "Ave"), second.Status);
        Assert.True(second.EditPersonNote(note.Id, note.Title, "New writing"), second.Status);
        Assert.True(first.RefreshPersonNotes(), first.Status);
        Assert.Equal("Ave", Assert.Single(first.People).PreferredName);
        Assert.Equal("New writing", Assert.Single(first.GetPersonNotes("avery@example.com")).Markdown);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Ave", Assert.Single(reopened.People).PreferredName);
        Assert.Equal("New writing", Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
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
    public void Independent_notes_and_stable_ownership_survive_reopening_and_profile_edits()
    {
        using var app = Open();
        var first = app.CreatePersonNote("AVERY@example.com", "Meeting context", "# Meeting\nPrivate **writing**.");
        var second = app.CreatePersonNote("avery@example.com", "Follow-up", "Second document.");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(2, reopened.GetPersonNotes("avery@example.com").Count);
        Assert.Equal(first, reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == first.Id));
        Assert.Equal(second, reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == second.Id));
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
