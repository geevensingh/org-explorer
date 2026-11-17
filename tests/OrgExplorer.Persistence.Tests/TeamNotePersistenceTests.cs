using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class TeamNotePersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Population = """
        {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000001",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"root@example.com","displayName":"Root Example","managerEmail":null},
        {"email":"manager@example.com","displayName":"Manager Example","managerEmail":"root@example.com"},
        {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"manager@example.com"},
        {"email":"replacement@example.com","displayName":"Replacement Example","managerEmail":null}]}
        """;

    private IApplicationWorkflow Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Population).Id), app.Status);
        return app;
    }

    [Fact]
    public void Failed_graph_commit_preserves_Team_review_and_writing_until_recovery_then_complete_public_refresh()
    {
        using var app = Open();
        var note = app.CreateTeamNote("manager@example.com", "Team", "Saved writing")!;
        const string role = """
            {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"manager@example.com","jobTitle":"Director"}]}
            """;
        var preview = app.PreviewImport(role);
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            Assert.False(app.ApplyImport(preview.Id));
            Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        }
        Assert.NotNull(app.LocalSaveError);
        Assert.True(app.HasFailedImport);
        Assert.Null(app.GetRelationships("manager@example.com")!.Person.JobTitle);
        using var other = ApplicationWorkflow.CreateFileBacked();
        Assert.True(other.OpenWorkspace(folder), other.Status);
        var durable = Assert.Single(other.GetTeamNotes("manager@example.com"));
        Assert.Equal(note.Id, durable.Id);
        Assert.Equal("Saved writing", durable.Markdown);
        Assert.False(durable.NeedsReview);
        Assert.True(other.EditTeamNote(note.Id, note.Title, "Synced writing"), other.Status);
        Assert.True(other.EditPersonText("manager@example.com", PersonTextField.JobTitle, "Director"), other.Status);
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.Equal("Director", app.GetRelationships("manager@example.com")!.Person.JobTitle);
        var loaded = Assert.Single(app.GetTeamNotes("manager@example.com"));
        Assert.True(loaded.NeedsReview);
        Assert.Equal("Synced writing", loaded.Markdown);
        Assert.False(app.CanSafelyClose);
        Assert.True(app.HasFailedImport);
        app.DiscardImportChanges();
        Assert.False(app.HasFailedImport);
        Assert.True(app.ConfirmTeamNote(note.Id), app.Status);
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
    }

    [Fact]
    public void Failed_review_actions_retain_saved_writing_and_flags_and_cannot_overwrite_a_new_committed_note()
    {
        using var app = Open();
        var note = app.CreateTeamNote("manager@example.com", "Team", "Saved writing")!;
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.JobTitle, "Director"));
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            Assert.False(app.ConfirmTeamNote(note.Id));
            Assert.Contains("NOT saved", app.Status);
            Assert.False(app.EditTeamNote(note.Id, "Draft", "Unsaved writing"));
            Assert.False(app.ArchiveTeamNote(note.Id));
            var retained = Assert.Single(app.GetTeamNotes("manager@example.com"));
            Assert.True(retained.NeedsReview);
            Assert.Equal("Saved writing", retained.Markdown);
        }
        using (var provider = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(provider.OpenWorkspace(folder), provider.Status);
            Assert.True(provider.EditTeamNote(note.Id, note.Title, "New committed writing"), provider.Status);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.Equal("New committed writing", Assert.Single(app.GetInheritedTeamNotes("avery@example.com"),
            n => n.Owner.Email == "manager@example.com").Note.Markdown);
        Assert.False(app.EditTeamNote(note.Id, "Reviewed", "Confirmed writing"));
        Assert.Contains("changed externally", app.Status);
        app.DiscardUnsavedChanges(note.Id);
        Assert.True(app.EditTeamNote(note.Id, "Reviewed", "Confirmed writing"), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.False(Assert.Single(reopened.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.Equal("Confirmed writing", Assert.Single(reopened.GetTeamNotes("manager@example.com")).Markdown);
    }

    [Fact]
    public void Notes_and_sticky_context_review_survive_real_reopen_moves_and_replacement_without_transferring_ownership()
    {
        using var app = Open();
        var direct = app.CreatePersonNote("avery@example.com", "Direct", "Private Avery")!;
        app.CreatePersonNote("manager@example.com", "About Manager", "Must not inherit");
        var root = app.CreateTeamNote("root@example.com", "Organization", "# Root")!;
        var manager = app.CreateTeamNote("manager@example.com", "Team", "**Manager**")!;
        var replacement = app.CreateTeamNote("replacement@example.com", "New context", "Replacement")!;
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.JobTitle, "Director"));
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.JobTitle, null));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var inherited = reopened.GetInheritedTeamNotes("avery@example.com");
        Assert.Equal(new[] { manager.Id, root.Id }, inherited.Select(n => n.Note.Id));
        Assert.True(inherited[0].Note.NeedsReview);
        Assert.Equal("**Manager**", inherited[0].Note.Markdown);
        Assert.True(reopened.ConfirmTeamNote(manager.Id), reopened.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        const string move = """
            {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"avery@example.com","managerEmail":"replacement@example.com"},
            {"email":"manager@example.com","archived":true}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(move).Id), app.Status);
        Assert.True(reopened.RefreshWorkspace(), reopened.Status);
        Assert.Equal(replacement.Id, Assert.Single(reopened.GetInheritedTeamNotes("avery@example.com")).Note.Id);
        Assert.Equal(direct, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        Assert.Equal(manager.Id, Assert.Single(reopened.GetTeamNotes("manager@example.com")).Id);
        Assert.Equal("manager@example.com", Assert.Single(reopened.GetTeamNotes("manager@example.com")).OwnerEmail);
        Assert.True(Assert.Single(reopened.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.True(reopened.ArchiveTeamNote(manager.Id), reopened.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal("**Manager**", Assert.Single(app.GetTeamNotes("manager@example.com", true)).Markdown);
        Assert.Empty(app.GetTeamNotes("manager@example.com"));
        Assert.Single(app.GetTeamNotes("root@example.com"));
        Assert.Single(app.GetTeamNotes("replacement@example.com"));
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
