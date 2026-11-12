using System.Diagnostics;
using System.Text.Json.Nodes;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class TeamNoteExternalRefreshTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Batch = """
        {"schemaVersion":1,"batchId":"80000000-0000-4000-8000-000000000091",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"casey@example.com"},
        {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null}]}
        """;

    private IApplicationWorkflow Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        return app;
    }

    [Fact]
    public async Task Folder_events_refresh_Team_writing_and_review_metadata_without_saves_or_confirmation()
    {
        using var app = Open();
        Assert.True(app.EditPersonText("casey@example.com", PersonTextField.JobTitle, "Original role"), app.Status);
        var timeline = app.GetProfileTimeline("casey@example.com")!.Changes.ToArray();
        var team = app.CreateTeamNote("casey@example.com", "Team", "Saved Team writing")!;
        var direct = app.CreatePersonNote("avery@example.com", "Private", "Saved Person writing")!;
        var file = Path.Combine(folder, "team-notes", team.Id + ".md");
        var directFile = Path.Combine(folder, "notes", direct.Id + ".md");
        var metadata = WorkspaceFixtures.IndexedDocuments(folder)["teamContext:casey@example.com"];
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.WorkspaceChanged += (_, _) =>
        {
            var inherited = Assert.Single(app.GetInheritedTeamNotes("avery@example.com"));
            if (inherited.Note.Markdown == "External Team writing" && inherited.Note.NeedsReview)
                refreshed.TrySetResult();
        };
        File.WriteAllText(file, File.ReadAllText(file).Replace("Saved Team writing", "External Team writing"));
        File.WriteAllText(directFile, File.ReadAllText(directFile).Replace("Saved Person writing", "External Person writing"));
        File.WriteAllText(metadata, new JsonObject { ["casey@example.com"] = Guid.NewGuid() }.ToJsonString());
        var bytes = WorkspaceFixtures.IndexedDocuments(folder).Values
            .Concat([file, directFile, Path.Combine(folder, "workspace.json")])
            .ToDictionary(p => p, File.ReadAllBytes);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually(() => Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown == "External Person writing");
        Assert.True(app.IsWorkspaceCurrent);
        Assert.True(Assert.Single(app.GetTeamNotes("casey@example.com")).NeedsReview);
        Assert.Equal("Casey Example", Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Owner.Heading);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(timeline),
            System.Text.Json.JsonSerializer.Serialize(app.GetProfileTimeline("casey@example.com")!.Changes));
        foreach (var (path, content) in bytes) Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal(bytes.Count, WorkspaceFixtures.IndexedDocuments(folder).Count + 3);
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Theory]
    [InlineData("team-note-incomplete")]
    [InlineData("team-note-version")]
    [InlineData("team-note-duplicate")]
    [InlineData("team-note-owner")]
    [InlineData("team-directory-file")]
    [InlineData("team-context-incomplete")]
    [InlineData("team-context-owner")]
    [InlineData("team-context-duplicate")]
    [InlineData("team-context-directory")]
    public async Task Invalid_Team_delivery_keeps_the_entire_Workspace_stale_without_repair_and_recovers(string invalidity)
    {
        using var app = Open();
        var team = app.CreateTeamNote("casey@example.com", "Team", "Saved Team writing")!;
        var direct = app.CreatePersonNote("avery@example.com", "Private", "Saved Person writing")!;
        Assert.True(app.EditPersonText("casey@example.com", PersonTextField.JobTitle, "Director"), app.Status);
        var teamFile = Path.Combine(folder, "team-notes", team.Id + ".md");
        var directFile = Path.Combine(folder, "notes", direct.Id + ".md");
        var file = invalidity.StartsWith("team-context") ?
            WorkspaceFixtures.IndexedDocuments(folder)["teamContext:casey@example.com"] : teamFile;
        var original = File.ReadAllText(file);
        var invalid = invalidity switch
        {
            "team-note-version" => original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"),
            "team-note-duplicate" => original.Replace("\"kind\": \"team\"", "\"kind\": \"team\", \"kind\": \"team\""),
            "team-note-owner" => original.Replace("casey@example.com", "missing@example.com"),
            "team-context-owner" => original.Replace("casey@example.com", "missing@example.com"),
            "team-context-duplicate" => "{\"casey@example.com\":\"" + Guid.NewGuid() + "\",\"casey@example.com\":\"" + Guid.NewGuid() + "\"}",
            _ => "Incomplete Team delivery"
        };
        var teamDirectory = Path.Combine(folder, "team-notes");
        if (invalidity == "team-directory-file")
        {
            File.Delete(teamFile);
            Directory.Delete(teamDirectory);
            File.WriteAllText(teamDirectory, invalid);
        }
        else if (invalidity == "team-context-directory")
        {
            File.Delete(file);
            Directory.CreateDirectory(file);
        }
        else File.WriteAllText(file, invalid);
        File.WriteAllText(directFile, File.ReadAllText(directFile).Replace("Saved Person writing", "Pending Person writing"));
        await Eventually(() => !app.IsWorkspaceCurrent);
        Assert.Contains("STALE", app.Status);
        Assert.Equal("Saved Person writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Equal("Saved Team writing", Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.Markdown);
        Assert.True(Assert.Single(app.GetTeamNotes("casey@example.com")).NeedsReview);
        Assert.False(app.ConfirmTeamNote(team.Id));
        Assert.False(app.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "Must not save"));
        if (invalidity == "team-directory-file")
        {
            Assert.Equal(invalid, File.ReadAllText(teamDirectory));
            File.Delete(teamDirectory);
            Directory.CreateDirectory(teamDirectory);
        }
        else if (invalidity == "team-context-directory") Directory.Delete(file);
        else Assert.Equal(invalid, File.ReadAllText(file));
        File.WriteAllText(file, original);
        await Eventually(() => app.IsWorkspaceCurrent &&
            Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown == "Pending Person writing");
        Assert.True(Assert.Single(app.GetTeamNotes("casey@example.com")).NeedsReview);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("NOT saved", app.LocalSaveError);
    }

    [Fact]
    public async Task Commit_delivery_waits_for_indexed_Team_context_before_publishing_profile_history_and_review()
    {
        using var app = Open();
        using var producer = ApplicationWorkflow.CreateFileBacked();
        var producerFolder = folder + "-producer";
        try
        {
            app.CreateTeamNote("casey@example.com", "Team", "Retained writing");
            Assert.True(producer.OpenWorkspace(producerFolder), producer.Status);
            Assert.True(producer.ApplyImport(producer.PreviewImport(Batch).Id), producer.Status);
            Assert.True(producer.EditPersonText("casey@example.com", PersonTextField.JobTitle, "Director"), producer.Status);
            var deferred = WorkspaceFixtures.IndexedDocuments(producerFolder)["teamContext:casey@example.com"];
            foreach (var path in Directory.GetFiles(Path.Combine(producerFolder, "documents"), "*", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(producerFolder, "index"))).Where(p => p != deferred))
            {
                var destination = Path.Combine(folder, Path.GetRelativePath(producerFolder, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination);
            }
            var pointer = File.ReadAllText(Path.Combine(producerFolder, "workspace.json"));
            File.WriteAllText(Path.Combine(folder, "workspace.json"), pointer);
            await Eventually(() => !app.IsWorkspaceCurrent);
            Assert.Null(app.GetRelationships("casey@example.com")!.Person.JobTitle);
            Assert.False(Assert.Single(app.GetTeamNotes("casey@example.com")).NeedsReview);
            Assert.Empty(app.GetProfileTimeline("casey@example.com")!.Changes);
            Assert.Equal(pointer, File.ReadAllText(Path.Combine(folder, "workspace.json")));
            var target = Path.Combine(folder, Path.GetRelativePath(producerFolder, deferred));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(deferred, target);
            await Eventually(() => app.IsWorkspaceCurrent &&
                app.GetRelationships("casey@example.com")!.Person.JobTitle == "Director");
            Assert.True(Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.NeedsReview);
            Assert.Single(app.GetProfileTimeline("casey@example.com")!.Changes);
            Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
            Assert.Single(app.GetProfileTimeline("casey@example.com")!.Changes);
            Assert.True(Assert.Single(app.GetTeamNotes("casey@example.com")).NeedsReview);
        }
        finally
        {
            producer.Dispose();
            if (Directory.Exists(producerFolder)) Directory.Delete(producerFolder, true);
        }
    }

    [Fact]
    public async Task Team_save_errors_survive_refresh_and_other_saves_and_retry_cannot_overwrite_a_delivered_note()
    {
        using var app = Open();
        var note = app.CreateTeamNote("casey@example.com", "Team", "Saved writing")!;
        var file = Path.Combine(folder, "team-notes", note.Id + ".md");
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(app.EditTeamNote(note.Id, note.Title, "Unsaved draft"));
        Assert.False(app.CanSafelyClose);
        var personFile = WorkspaceFixtures.IndexedDocuments(folder)["people:casey@example.com"];
        var person = JsonNode.Parse(File.ReadAllText(personFile))!;
        person["preferredName"] = "External Casey";
        File.WriteAllText(personFile, person.ToJsonString());
        await Eventually(() => app.GetRelationships("casey@example.com")!.Person.Heading == "External Casey");
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "007B"), app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("Team note save failed", app.Status);
        Assert.True(app.EditTeamNote(note.Id, note.Title, "Retried writing"), app.Status);
        Assert.True(app.CanSafelyClose);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(app.EditTeamNote(note.Id, note.Title, "Competing draft"));
        File.WriteAllText(file, File.ReadAllText(file).Replace("Retried writing", "Delivered writing"));
        await Eventually(() => Assert.Single(app.GetTeamNotes("casey@example.com")).Markdown == "Delivered writing");
        Assert.False(app.EditTeamNote(note.Id, note.Title, "Competing draft"));
        Assert.Contains("changed externally", app.Status);
        Assert.Contains("Delivered writing", File.ReadAllText(file));
        app.DiscardUnsavedChanges(note.Id);
        Assert.True(app.CanSafelyClose);
        Assert.True(app.EditTeamNote(note.Id, note.Title, "Deliberate delivered-version edit"), app.Status);
        Assert.True(app.CanSafelyClose);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "Team folder-event outcome did not arrive within five seconds.");
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
