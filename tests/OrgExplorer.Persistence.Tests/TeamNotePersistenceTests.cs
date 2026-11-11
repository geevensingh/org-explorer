using OrgExplorer.Workflow;
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

    private IApplicationWorkflow Open(bool incremental = false)
    {
        var app = new WorkspaceViewModel(incremental ? ApplicationWorkflow.CreateIncrementalFileBacked() : ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Population).Id), app.Status);
        return app;
    }

    [Theory]
    [InlineData("\"kind\": \"team\"", "\"kind\": \"person\"")]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 99")]
    [InlineData("\"confirmedContextRevision\": \"00000000-0000-0000-0000-000000000000\"", "\"unknown\": true")]
    [InlineData("manager@example.com", "missing@example.com")]
    public void Invalid_Team_documents_block_open_and_never_repair_or_drop_writing(string original, string replacement)
    {
        using var app = Open();
        app.CreateTeamNote("manager@example.com", "Team", "Retained writing");
        var file = Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories).Single();
        var invalid = File.ReadAllText(file).Replace(original, replacement);
        File.WriteAllText(file, invalid);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(folder));
        Assert.Contains("could not be opened", reopened.Status);
        Assert.Equal(invalid, File.ReadAllText(file));
    }

    [Fact]
    public void Failed_graph_commit_does_not_publish_review_and_note_refresh_accepts_complete_valid_profile_context()
    {
        using var app = Open();
        var note = app.CreateTeamNote("manager@example.com", "Team", "Saved writing")!;
        const string role = """
            {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"manager@example.com","jobTitle":"Director"}]}
            """;
        var preview = app.PreviewImport(role);
        using (File.Open(Path.Combine(folder, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(app.ApplyImport(preview.Id));
            Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        }
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.Null(app.GetRelationships("manager@example.com")!.Person.JobTitle);
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        using var other = ApplicationWorkflow.CreateFileBacked();
        Assert.True(other.OpenWorkspace(folder));
        Assert.True(other.EditPersonText("manager@example.com", PersonTextField.JobTitle, "Director"));
        var file = Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("Saved writing", "Synced writing"));
        Assert.True(app.RefreshTeamNotes(), app.Status);
        Assert.Equal("Director", app.GetRelationships("manager@example.com")!.Person.JobTitle);
        var loaded = Assert.Single(app.GetTeamNotes("manager@example.com"));
        Assert.True(loaded.NeedsReview);
        Assert.Equal("Synced writing", loaded.Markdown);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        Assert.True(app.ConfirmTeamNote(note.Id), app.Status);
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
    }

    [Fact]
    public void Failed_review_actions_and_invalid_external_delivery_retain_saved_writing_and_flags_until_retry()
    {
        using var app = Open();
        var note = app.CreateTeamNote("manager@example.com", "Team", "Saved writing")!;
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.JobTitle, "Director"));
        var file = Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories).Single();
        var saved = File.ReadAllText(file);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(app.ConfirmTeamNote(note.Id));
            Assert.Contains("NOT saved", app.Status);
            Assert.False(app.EditTeamNote(note.Id, "Draft", "Unsaved writing"));
            Assert.False(app.ArchiveTeamNote(note.Id));
            var retained = Assert.Single(app.GetTeamNotes("manager@example.com"));
            Assert.True(retained.NeedsReview);
            Assert.Equal("Saved writing", retained.Markdown);
        }
        File.WriteAllText(file, saved.Replace("Saved writing", "Synced writing"));
        Assert.True(app.RefreshTeamNotes(), app.Status);
        Assert.Equal("Synced writing", Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.Markdown);
        Assert.True(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        File.WriteAllText(file, "Incomplete delivery");
        Assert.False(app.RefreshTeamNotes());
        Assert.Equal("Synced writing", Assert.Single(app.GetTeamNotes("manager@example.com")).Markdown);
        Assert.False(app.ConfirmTeamNote(note.Id));
        Assert.Equal("Incomplete delivery", File.ReadAllText(file));
        File.WriteAllText(file, saved.Replace("Saved writing", "Recovered writing"));
        Assert.True(app.RefreshTeamNotes(), app.Status);
        Assert.False(app.EditTeamNote(note.Id, "Reviewed", "Confirmed writing"));
        Assert.Contains("changed externally", app.Status);
        app.DiscardUnsavedChanges(note.Id);
        Assert.True(app.EditTeamNote(note.Id, "Reviewed", "Confirmed writing"), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.False(Assert.Single(reopened.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.Equal("Confirmed writing", Assert.Single(reopened.GetTeamNotes("manager@example.com")).Markdown);
        Assert.Empty(Directory.GetFiles(folder, "*.next", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Notes_and_sticky_context_review_survive_real_reopen_moves_and_replacement_without_transferring_ownership(bool incremental)
    {
        using var app = Open(incremental);
        var direct = app.CreatePersonNote("avery@example.com", "Direct", "Private Avery")!;
        app.CreatePersonNote("manager@example.com", "About Manager", "Must not inherit");
        var root = app.CreateTeamNote("root@example.com", "Organization", "# Root")!;
        var manager = app.CreateTeamNote("manager@example.com", "Team", "**Manager**")!;
        var replacement = app.CreateTeamNote("replacement@example.com", "New context", "Replacement")!;
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.JobTitle, "Director"));
        Assert.True(app.EditPersonText("manager@example.com", PersonTextField.JobTitle, null));
        using var reopened = incremental ? ApplicationWorkflow.CreateIncrementalFileBacked() : ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var inherited = reopened.GetInheritedTeamNotes("avery@example.com");
        Assert.Equal(new[] { manager.Id, root.Id }, inherited.Select(n => n.Note.Id));
        Assert.True(inherited[0].Note.NeedsReview);
        Assert.Equal("**Manager**", inherited[0].Note.Markdown);
        Assert.True(reopened.ConfirmTeamNote(manager.Id), reopened.Status);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.False(Assert.Single(app.GetTeamNotes("manager@example.com")).NeedsReview);
        const string move = """
            {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"avery@example.com","managerEmail":"replacement@example.com"},
            {"email":"manager@example.com","archived":true}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(move).Id), app.Status);
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(replacement.Id, Assert.Single(reopened.GetInheritedTeamNotes("avery@example.com")).Note.Id);
        Assert.Equal(direct, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        Assert.Equal(manager.Id, Assert.Single(reopened.GetTeamNotes("manager@example.com")).Id);
        Assert.Equal("manager@example.com", Assert.Single(reopened.GetTeamNotes("manager@example.com")).OwnerEmail);
        Assert.True(Assert.Single(reopened.GetTeamNotes("manager@example.com")).NeedsReview);
        Assert.True(reopened.ArchiveTeamNote(manager.Id), reopened.Status);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.Equal("**Manager**", Assert.Single(app.GetTeamNotes("manager@example.com", true)).Markdown);
        Assert.Empty(app.GetTeamNotes("manager@example.com"));
        Assert.Equal(3, Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories)
            .Count(path => File.ReadAllText(path).Contains("\"kind\": \"team\"")));
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
