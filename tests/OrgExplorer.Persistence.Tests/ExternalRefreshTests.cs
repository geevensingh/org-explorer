using System.Diagnostics;
using System.Text.Json.Nodes;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class ExternalRefreshTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Batch = """
        {"schemaVersion":1,"batchId":"80000000-0000-4000-8000-000000000001",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"casey@example.com"},
        {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null}]}
        """;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
    private static readonly byte[] OtherPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII=");

    [Fact]
    public async Task Same_path_photo_replacement_is_validated_and_invalid_bytes_never_become_a_placeholder()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var producer = Path.Combine(folder, "producer.png");
        File.WriteAllBytes(producer, Png);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.EditPhoto("avery@example.com", producer), app.Status);
        var reference = app.GetRelationships("avery@example.com")!.Person.PhotoPath!;
        var image = Directory.GetFiles(folder, reference, SearchOption.AllDirectories).Single();
        var timestamp = File.GetLastWriteTimeUtc(image);
        File.WriteAllBytes(image, OtherPng);
        File.SetLastWriteTimeUtc(image, timestamp);
        await Eventually(() => app.GetPhoto("avery@example.com")!.Bytes.Span.SequenceEqual(OtherPng));
        Assert.Equal(reference, app.GetRelationships("avery@example.com")!.Person.PhotoPath);
        File.WriteAllText(image, "Incomplete image delivery");
        await Eventually(() => app.Status.Contains("STALE"));
        Assert.Equal(OtherPng, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal("Incomplete image delivery", File.ReadAllText(image));
        File.WriteAllBytes(image, OtherPng);
        await Eventually(() => !app.Status.Contains("STALE"));
        Assert.Equal(OtherPng, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
    }

    [Fact]
    public async Task External_Person_bytes_refresh_the_open_workflow_without_reload()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var file = PersonFile("avery@example.com");
        var timestamp = File.GetLastWriteTimeUtc(file);
        var person = JsonNode.Parse(File.ReadAllText(file))!;
        person["preferredName"] = "Externally updated";
        File.WriteAllText(file, person.ToJsonString());
        File.SetLastWriteTimeUtc(file, timestamp);
        await Eventually(() => app.GetRelationships("avery@example.com")?.Person.Heading == "Externally updated");
        Assert.Equal("Casey Example", app.GetRelationships("avery@example.com")!.Manager!.Heading);
    }

    [Fact]
    public async Task A_failed_local_note_save_blocks_safe_close_even_after_external_refresh_and_other_successful_saves()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        var noteFile = Path.Combine(folder, "notes", note.Id + ".md");
        using (File.Open(noteFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(app.EditPersonNote(note.Id, note.Title, "Unsaved draft"));
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.LocalSaveError);
        var file = PersonFile("avery@example.com");
        var person = JsonNode.Parse(File.ReadAllText(file))!;
        person["preferredName"] = "External name";
        File.WriteAllText(file, person.ToJsonString());
        await Eventually(() => app.GetRelationships("avery@example.com")!.Person.Heading == "External name");
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "0009B"), app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("NOT saved", app.Status);
        Assert.True(app.EditPersonNote(note.Id, note.Title, "Retried draft"), app.Status);
        Assert.True(app.CanSafelyClose);
        Assert.Null(app.LocalSaveError);
        Assert.Equal("Retried draft", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public async Task Folder_events_publish_observable_view_model_state_but_own_saves_do_not_loop_or_duplicate_batches()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var notifications = 0;
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.WorkspaceChanged += (_, _) => { Interlocked.Increment(ref notifications); refreshed.TrySetResult(); };
        var file = PersonFile("avery@example.com");
        var person = JsonNode.Parse(File.ReadAllText(file))!;
        person["preferredName"] = "External name";
        File.WriteAllText(file, person.ToJsonString());
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("External name", app.GetRelationships("avery@example.com")!.Person.Heading);
        Assert.True(app.IsWorkspaceCurrent);
        Assert.True(app.EditPreferredName("avery@example.com", "Local name"), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        Assert.True(app.EditPersonNote(note.Id, note.Title, "Local writing"), app.Status);
        var count = Volatile.Read(ref notifications);
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMilliseconds(600))
        {
            Assert.Equal(count, Volatile.Read(ref notifications));
            Assert.Equal("Local name", app.GetRelationships("avery@example.com")!.Person.Heading);
            Assert.Equal("Local writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
            await Task.Delay(25);
        }
        foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(path));
        var replay = app.PreviewImport(Batch);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(app.ApplyImport(replay.Id), app.Status);
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task A_new_generation_waits_for_all_declared_Profile_history_files_before_acceptance()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        using var producer = ApplicationWorkflow.CreateFileBacked();
        var producerFolder = folder + "-producer";
        try
        {
            Assert.True(app.OpenWorkspace(folder), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
            Assert.True(producer.OpenWorkspace(producerFolder), producer.Status);
            Assert.True(producer.ApplyImport(producer.PreviewImport(Batch).Id), producer.Status);
            Assert.True(producer.EditPersonText("avery@example.com", PersonTextField.DirectoryName, "Delivered Avery"), producer.Status);
            var pointer = File.ReadAllText(Path.Combine(producerFolder, "workspace.json"));
            var generation = JsonNode.Parse(pointer)!["generation"]!.GetValue<string>();
            var source = Path.Combine(producerFolder, "generations", generation);
            var target = Path.Combine(folder, "generations", generation);
            var deferred = Directory.GetFiles(Path.Combine(source, "profile-history"), "*.json");
            foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Except(deferred))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination);
            }
            File.WriteAllText(Path.Combine(folder, "workspace.json"), pointer);
            await Eventually(() => !app.IsWorkspaceCurrent);
            Assert.Equal("Avery Example", app.GetRelationships("avery@example.com")!.Person.DirectoryName);
            Assert.Contains("unavailable", app.Status);
            Assert.Equal(pointer, File.ReadAllText(Path.Combine(folder, "workspace.json")));
            foreach (var path in deferred)
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination);
            }
            await Eventually(() => app.IsWorkspaceCurrent &&
                app.GetRelationships("avery@example.com")!.Person.DirectoryName == "Delivered Avery");
            Assert.Single(app.GetProfileTimeline("avery@example.com")!.Changes);
            var replay = app.PreviewImport(Batch);
            Assert.True(app.ApplyImport(replay.Id), app.Status);
            Assert.Single(app.GetProfileTimeline("avery@example.com")!.Changes);
        }
        finally
        {
            producer.Dispose();
            if (Directory.Exists(producerFolder)) Directory.Delete(producerFolder, true);
        }
    }

    [Theory]
    [InlineData("workspace.json")]
    [InlineData("person")]
    [InlineData("note")]
    public async Task Missing_current_documents_are_unavailable_or_validated_removals_without_false_roots(string missing)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        var path = missing switch
        {
            "person" => PersonFile("casey@example.com"),
            "note" => Path.Combine(folder, "notes", note.Id + ".md"),
            _ => Path.Combine(folder, missing)
        };
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        if (missing == "note")
        {
            await Eventually(() => app.GetPersonNotes("avery@example.com").Count == 0);
            Assert.True(app.IsWorkspaceCurrent);
        }
        else
        {
            await Eventually(() => !app.IsWorkspaceCurrent);
            Assert.Contains("STALE", app.Status);
            Assert.Equal("Casey Example", app.GetRelationships("avery@example.com")!.Manager!.Heading);
        }
        File.WriteAllBytes(path, bytes);
        await Eventually(() => app.IsWorkspaceCurrent && app.GetPersonNotes("avery@example.com").Count == 1);
    }

    [Theory]
    [InlineData("missing-manager")]
    [InlineData("omitted-manager")]
    [InlineData("cycle")]
    [InlineData("archived-manager")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("unsupported-version")]
    [InlineData("batch-source")]
    public async Task Invalid_whole_Workspace_is_stale_without_partial_acceptance_or_repair_and_recovers(string invalidity)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!;
        var noteFile = Path.Combine(folder, "notes", note.Id + ".md");
        var originalNote = File.ReadAllText(noteFile);
        var file = invalidity switch
        {
            "unsupported-version" => Path.Combine(folder, "workspace.json"),
            "batch-source" => Directory.GetFiles(folder, "batches.json", SearchOption.AllDirectories).Single(),
            _ => PersonFile("casey@example.com")
        };
        var original = File.ReadAllText(file);
        var data = JsonNode.Parse(original)!;
        switch (invalidity)
        {
            case "missing-manager": data["managerEmail"] = "missing@example.com"; break;
            case "omitted-manager": data.AsObject().Remove("managerEmail"); break;
            case "cycle": data["managerEmail"] = "avery@example.com"; break;
            case "archived-manager": data["archived"] = true; break;
            case "unknown-field": data["notes"] = "Must not import"; break;
            case "unsupported-version": data["schemaVersion"] = 99; break;
            case "batch-source": data.AsObject().First().Value!["source"]!["observedAt"] = "2026-02-29T17:00:00Z"; break;
        }
        var invalid = invalidity == "duplicate-field"
            ? original.Replace("\"managerEmail\": null", "\"managerEmail\": null, \"managerEmail\": null")
            : data.ToJsonString();
        File.WriteAllText(file, invalid);
        File.WriteAllText(noteFile, originalNote.Replace("Saved writing", "Not yet accepted"));
        await Eventually(() => app.Status.Contains("STALE"));
        Assert.Equal("Saved writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Equal("Casey Example", app.GetRelationships("avery@example.com")!.Manager!.Heading);
        Assert.Equal(invalid, File.ReadAllText(file));
        Assert.False(app.EditPreferredName("avery@example.com", "Must not overwrite"));
        File.WriteAllText(file, original);
        await Eventually(() => Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown == "Not yet accepted");
        Assert.True(app.IsWorkspaceCurrent);
        Assert.False(app.CanSafelyClose);
    }

    private string PersonFile(string email) => Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
        .Single(p => p.Contains(Path.DirectorySeparatorChar + "people" + Path.DirectorySeparatorChar)
            && JsonNode.Parse(File.ReadAllText(p))?["email"]?.GetValue<string>() == email);

    private static async Task Eventually(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "Folder-event outcome did not arrive within five seconds.");
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
