using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class PhotoTests : IDisposable
{
    // A known 1x1 PNG, not a byte string accepted by a fake image decoder.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
    private static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDgqKKK/uA/0dP/2Q==");
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private string Producer => Path.Combine(root, "producer");
    private string Workspace => Path.Combine(root, "workspace");

    private static IApplicationWorkflow CreateWorkflow(bool incremental) => incremental
        ? ApplicationWorkflow.CreateIncrementalFileBacked() : ApplicationWorkflow.CreateFileBacked();

    private string Import(string? photoJson, int batch = 1, string name = "Avery Example")
    {
        Directory.CreateDirectory(Producer);
        var path = Path.Combine(Producer, "import.json");
        File.WriteAllText(path, $$"""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-{{batch:D12}}",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"{{name}}","managerEmail":null{{(photoJson is null ? "" : ",\"photoPath\":" + photoJson)}}}]}
            """);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decoded_PNG_is_available_on_the_profile_after_producer_removal_and_Workspace_reopen(bool incremental)
    {
        using var app = CreateWorkflow(incremental);
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var import = Import("\"avery.data\"");
        File.WriteAllBytes(Path.Combine(Producer, "avery.data"), Png);
        var preview = app.PreviewImportFile(import);
        Assert.True(preview.CanApply, string.Join("\n", preview.Errors));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Directory.Delete(Producer, true);

        using var reopened = CreateWorkflow(incremental);
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        var photo = reopened.GetPhoto("AVERY@example.com");
        Assert.NotNull(photo);
        Assert.Equal("image/png", photo.ContentType);
        Assert.Equal(1, photo.Width);
        Assert.Equal(1, photo.Height);
        Assert.Equal(Png, photo.Bytes.ToArray());
    }

    [Fact]
    public void Truncated_PNG_is_a_whole_batch_error_not_a_successful_placeholder()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var import = Import("\"truncated.png\"");
        File.WriteAllBytes(Path.Combine(Producer, "truncated.png"), Png[..^12]);
        var preview = app.PreviewImportFile(import);
        Assert.False(preview.CanApply);
        Assert.Contains("avery@example.com.photoPath", string.Join("\n", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Empty(app.People);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JPEG_content_is_decoded_but_a_truncated_JPEG_is_rejected(bool truncated)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var import = Import("\"avery.png\"");
        File.WriteAllBytes(Path.Combine(Producer, "avery.png"), truncated ? Jpeg[..^2] : Jpeg);
        var preview = app.PreviewImportFile(import);
        Assert.Equal(!truncated, preview.CanApply);
        if (truncated)
        {
            Assert.False(app.ApplyImport(preview.Id));
            Assert.Empty(app.People);
        }
        else
        {
            Assert.True(app.ApplyImport(preview.Id), app.Status);
            var photo = app.GetPhoto("avery@example.com");
            Assert.NotNull(photo);
            Assert.Equal("image/jpeg", photo.ContentType);
            Assert.Equal(2, photo.Width);
            Assert.Equal(2, photo.Height);
        }
    }

    private IApplicationWorkflow ImportOriginalPhoto(bool incremental = false)
    {
        var app = CreateWorkflow(incremental);
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var import = Import("\"avery.png\"");
        File.WriteAllBytes(Path.Combine(Producer, "avery.png"), Png);
        Assert.True(app.ApplyImport(app.PreviewImportFile(import).Id), app.Status);
        return app;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Photo_changes_join_the_structured_timeline_with_content_references_and_local_import_provenance(bool incremental)
    {
        using var app = ImportOriginalPhoto(incremental);
        var originalReference = Assert.Single(app.People).PhotoPath;
        Assert.Empty(app.GetProfileTimeline("avery@example.com")!.Changes);
        var local = Path.Combine(Producer, "local.jpg");
        File.WriteAllBytes(local, Jpeg);
        Assert.True(app.EditPhoto("avery@example.com", local), app.Status);
        var localReference = Assert.Single(app.People).PhotoPath;
        Assert.True(app.EditPhoto("avery@example.com", null), app.Status);
        Assert.True(app.EditPhoto("avery@example.com", null), app.Status);
        var import = Import("\"avery.png\"", 2);
        Assert.True(app.ApplyImport(app.PreviewImportFile(import).Id), app.Status);
        using var reopened = CreateWorkflow(incremental);
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        var changes = reopened.GetProfileTimeline("avery@example.com")!.Changes;
        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.Equal("photoPath", c.Field));
        Assert.Equal(originalReference, changes[0].OldValue.GetString());
        Assert.Equal(localReference, changes[0].NewValue.GetString());
        Assert.Equal(ProfileChangeSource.LocalEdit, changes[0].Evidence.Source);
        Assert.Null(changes[0].Evidence.ObservedAt);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, changes[1].NewValue.ValueKind);
        Assert.Equal(originalReference, changes[2].NewValue.GetString());
        Assert.Equal(ProfileChangeSource.Import, changes[2].Evidence.Source);
        Assert.Equal(Png, reopened.GetPhoto("avery@example.com")!.Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Omitted_photo_preserves_image_and_null_clears_it_durably_without_clearing_other_fields(bool incremental)
    {
        using var app = ImportOriginalPhoto(incremental);
        Assert.True(app.ApplyImport(app.PreviewImportFile(Import(null, 2, "Avery Updated")).Id), app.Status);
        Assert.Equal(Png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        var clear = app.PreviewImportFile(Import("null", 3, "Avery Updated"));
        Assert.Contains(clear.Changes, change => change.Contains("Photo") && change.Contains("clear"));
        Assert.True(app.ApplyImport(clear.Id), app.Status);
        using var reopened = CreateWorkflow(incremental);
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        Assert.Null(reopened.GetPhoto("avery@example.com"));
        Assert.Equal("Avery Updated", Assert.Single(reopened.People).DirectoryName);
    }

    [Theory]
    [InlineData("\"missing.png\"", "missing")]
    [InlineData("\"invalid.jpg\"", "text")]
    [InlineData("\"unsupported.png\"", "gif")]
    [InlineData("\"corrupt.png\"", "crc")]
    [InlineData("\"C:\\\\outside.png\"", "path")]
    [InlineData("\"C:outside.png\"", "path")]
    [InlineData("\"\\\\\\\\server\\\\share\\\\photo.png\"", "path")]
    [InlineData("\"https://example.com/photo.png\"", "path")]
    [InlineData("\"file:///C:/photo.png\"", "path")]
    [InlineData("\"..\\\\outside.png\"", "path")]
    [InlineData("\"folder\\\\..\\\\avery.png\"", "path")]
    [InlineData("\"avery.png:stream\"", "path")]
    public void Bad_supplied_photo_prevents_the_whole_batch_and_preserves_the_existing_profile(string value, string kind)
    {
        using var app = ImportOriginalPhoto();
        if (kind == "text") File.WriteAllText(Path.Combine(Producer, "invalid.jpg"), "not an image");
        if (kind == "gif") File.WriteAllBytes(Path.Combine(Producer, "unsupported.png"),
            Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));
        if (kind == "crc")
        {
            var corrupt = Png.ToArray();
            corrupt[^13] ^= 1;
            File.WriteAllBytes(Path.Combine(Producer, "corrupt.png"), corrupt);
        }
        var path = Import(value, 2, "Must not apply");
        File.WriteAllText(path, File.ReadAllText(path).Replace("}]}", "},{\"email\":\"new@example.com\",\"displayName\":\"New Person\",\"managerEmail\":null}]}"));
        var preview = app.PreviewImportFile(path);
        Assert.False(preview.CanApply);
        Assert.Contains("avery@example.com.photoPath", string.Join("\n", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        Assert.Equal("Avery Example", Assert.Single(reopened.People).DirectoryName);
        Assert.Equal(Png, reopened.GetPhoto("avery@example.com")!.Bytes.ToArray());
    }

    [Fact]
    public void Changed_image_bytes_invalidate_preview_even_when_JSON_path_and_timestamps_are_unchanged()
    {
        using var app = ImportOriginalPhoto();
        var path = Import("\"avery.png\"", 2, "Must not apply");
        var preview = app.PreviewImportFile(path);
        var image = Path.Combine(Producer, "avery.png");
        var timestamp = File.GetLastWriteTimeUtc(image);
        File.WriteAllBytes(image, Jpeg);
        File.SetLastWriteTimeUtc(image, timestamp);
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal("Avery Example", Assert.Single(app.People).DirectoryName);
        Assert.Equal(Png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepted_batch_replay_is_a_no_op_but_same_path_with_different_image_bytes_conflicts_after_reopen(bool incremental)
    {
        using var app = ImportOriginalPhoto(incremental);
        Assert.True(app.EditPreferredName("avery@example.com", "Local name"));
        using var reopened = CreateWorkflow(incremental);
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        var path = Import("\"avery.png\"");
        var replay = reopened.PreviewImportFile(path);
        Assert.True(replay.CanApply, reopened.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(reopened.ApplyImport(replay.Id), reopened.Status);
        Assert.Equal("Local name", Assert.Single(reopened.People).PreferredName);
        File.WriteAllBytes(Path.Combine(Producer, "avery.png"), Jpeg);
        var conflict = reopened.PreviewImportFile(path);
        Assert.False(conflict.CanApply);
        Assert.Contains("batchId", string.Join("\n", conflict.Errors));
        Assert.Equal(Png, reopened.GetPhoto("avery@example.com")!.Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Photos_reporting_changes_and_replay_preserve_private_notes_and_note_refresh_revisions(bool incremental)
    {
        using var app = new WorkspaceViewModel(CreateWorkflow(incremental));
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var path = Import("\"avery.png\"");
        File.WriteAllBytes(Path.Combine(Producer, "avery.png"), Png);
        var localPhoto = Path.Combine(Producer, "local.jpg");
        File.WriteAllBytes(localPhoto, Jpeg);
        const string initial = """
            {"schemaVersion":1,"batchId":"70000000-0000-4000-8000-000000000001",
            "observedAt":"2026-11-01T17:00:00Z","people":[
            {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"casey@example.com",
             "alias":"avery","officeNumber":"007B","department":"Research","photoPath":"avery.png"},
            {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null}]}
            """;
        File.WriteAllText(path, initial);
        Assert.True(app.ApplyImport(app.PreviewImportFile(path).Id), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Private context", "# Avery\nPrivate writing")!;
        var managerNote = app.CreatePersonNote("casey@example.com", "Manager context", "Not inherited")!;
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        Assert.True(app.EditPhoto("avery@example.com", localPhoto), app.Status);
        Assert.True(app.EditPersonNote(note.Id, note.Title, "Writing after a local photo edit"), app.Status);
        var noteFile = Path.Combine(Workspace, "notes", note.Id.ToString("D") + ".md");
        var originalDocument = File.ReadAllText(noteFile);
        File.WriteAllText(path, """
            {"schemaVersion":1,"batchId":"70000000-0000-4000-8000-000000000002",
            "observedAt":"2026-11-02T17:00:00Z","people":[
            {"email":"casey@example.com","archived":true},
            {"email":"avery@example.com","photoPath":"avery.png","officeNumber":"Must not apply"}]}
            """);
        var rejected = app.PreviewImportFile(path);
        Assert.False(rejected.CanApply);
        Assert.Contains("avery@example.com.managerEmail", string.Join("\n", rejected.Errors));
        Assert.False(app.ApplyImport(rejected.Id));
        Assert.Equal(Jpeg, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal("007B", app.GetRelationships("avery@example.com")!.Person.OfficeNumber);
        File.WriteAllText(path, """
            {"schemaVersion":1,"batchId":"70000000-0000-4000-8000-000000000003",
            "observedAt":"2026-11-03T17:00:00Z","people":[
            {"email":"casey@example.com","archived":true},
            {"email":"avery@example.com","managerEmail":null,"officeNumber":"0009C","department":null}]}
            """);
        Assert.True(app.ApplyImport(app.PreviewImportFile(path).Id), app.Status);
        File.WriteAllText(path, initial);
        var replay = app.PreviewImportFile(path);
        Assert.True(replay.CanApply, app.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(app.ApplyImport(replay.Id), app.Status);
        Assert.Equal(Jpeg, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal(originalDocument, File.ReadAllText(noteFile));
        File.WriteAllText(noteFile, originalDocument + "\nExternal writing.");
        Assert.True(app.RefreshPersonNotes(), app.Status);
        Assert.Equal("Writing after a local photo edit\nExternal writing.",
            Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.True(app.EditPhoto("avery@example.com", Path.Combine(Producer, "avery.png")), app.Status);
        Assert.True(app.EditPersonNote(note.Id, note.Title, "Writing after external refresh"), app.Status);
        Directory.Delete(Producer, true);
        using var reopened = new WorkspaceViewModel(CreateWorkflow(incremental));
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        var person = Assert.Single(reopened.ReportingRoots).Person;
        Assert.Equal("avery@example.com", person.Email);
        Assert.Null(person.ManagerEmail);
        Assert.Equal("Ave", person.PreferredName);
        Assert.Equal("avery", person.Alias);
        Assert.Equal("0009C", person.OfficeNumber);
        Assert.Null(person.Department);
        Assert.True(reopened.GetRelationships("casey@example.com")!.Person.Archived);
        Assert.Equal(Png, reopened.GetPhoto(person.Email)!.Bytes.ToArray());
        Assert.Equal("Writing after external refresh", Assert.Single(reopened.GetPersonNotes(person.Email)).Markdown);
        Assert.Equal(managerNote, Assert.Single(reopened.GetPersonNotes("casey@example.com")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Directory_links_are_resolved_and_only_contained_targets_are_accepted(bool escaping)
    {
        using var app = ImportOriginalPhoto();
        var target = escaping ? Path.Combine(root, "outside") : Path.Combine(Producer, "inside");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "linked.png"), Png);
        var link = Path.Combine(Producer, "link");
        using var create = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Assert.True(create.WaitForExit(10000), "Junction creation did not finish.");
        Assert.True(create.ExitCode == 0, create.StandardError.ReadToEnd());
        try
        {
            var preview = app.PreviewImportFile(Import("\"link\\\\linked.png\"", 2, "Avery Updated"));
            Assert.Equal(!escaping, preview.CanApply);
            Assert.Equal(!escaping, app.ApplyImport(preview.Id));
            Assert.Equal(escaping ? "Avery Example" : "Avery Updated", Assert.Single(app.People).DirectoryName);
            Assert.Equal(Png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        }
        finally
        {
            using var remove = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c rmdir \"{link}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            Assert.True(remove.WaitForExit(10000), "Junction removal did not finish.");
            Assert.Equal(0, remove.ExitCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void File_symlinks_are_resolved_and_only_contained_targets_are_accepted(bool escaping)
    {
        using var app = ImportOriginalPhoto();
        var target = Path.Combine(escaping ? root : Producer, "target.png");
        File.WriteAllBytes(target, Png);
        File.CreateSymbolicLink(Path.Combine(Producer, "link.png"), target);
        var preview = app.PreviewImportFile(Import("\"link.png\"", 2, "Avery Updated"));
        Assert.Equal(!escaping, preview.CanApply);
        Assert.Equal(!escaping, app.ApplyImport(preview.Id));
        Assert.Equal(escaping ? "Avery Example" : "Avery Updated", Assert.Single(app.People).DirectoryName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_photo_edit_is_copied_and_saved_but_invalid_replacement_does_not_erase_it(bool incremental)
    {
        using var app = ImportOriginalPhoto(incremental);
        var selected = Path.Combine(Producer, "selected.jpg");
        File.WriteAllBytes(selected, Jpeg);
        Assert.True(app.EditPhoto("avery@example.com", selected), app.Status);
        File.WriteAllText(selected, "Corrupt replacement");
        Assert.False(app.EditPhoto("avery@example.com", selected));
        Directory.Delete(Producer, true);
        using var reopened = CreateWorkflow(incremental);
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        Assert.Equal("image/jpeg", reopened.GetPhoto("avery@example.com")!.ContentType);
        Assert.True(reopened.EditPhoto("avery@example.com", null), reopened.Status);
        Assert.Null(reopened.GetPhoto("avery@example.com"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Distinct_images_stay_with_the_intended_Person_and_reordered_replay_remains_a_no_op(bool incremental)
    {
        using var app = CreateWorkflow(incremental);
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var path = Import("\"avery.png\"");
        File.WriteAllBytes(Path.Combine(Producer, "avery.png"), Png);
        File.WriteAllBytes(Path.Combine(Producer, "blake.jpg"), Jpeg);
        const string avery = """{"email":"avery@example.com","displayName":"Avery Example","managerEmail":null,"photoPath":"avery.png"}""";
        const string blake = """{"email":"blake@example.com","displayName":"Blake Example","managerEmail":null,"photoPath":"blake.jpg"}""";
        string Batch(string records) => $$"""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
            "observedAt":"2026-11-01T17:00:00Z","people":[{{records}}]}
            """;
        File.WriteAllText(path, Batch(avery + "," + blake));
        Assert.True(app.ApplyImport(app.PreviewImportFile(path).Id), app.Status);
        Assert.Equal("image/png", app.GetPhoto("avery@example.com")!.ContentType);
        Assert.Equal("image/jpeg", app.GetPhoto("blake@example.com")!.ContentType);
        File.WriteAllText(path, Batch(blake + ",\n" + avery));
        var replay = app.PreviewImportFile(path);
        Assert.True(replay.CanApply, app.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
    }

    [Fact]
    public void Removing_a_supplied_photo_after_preview_invalidates_apply_without_erasing_existing_data()
    {
        using var app = ImportOriginalPhoto();
        var preview = app.PreviewImportFile(Import("\"avery.png\"", 2, "Must not apply"));
        File.Delete(Path.Combine(Producer, "avery.png"));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal("Avery Example", Assert.Single(app.People).DirectoryName);
        Assert.Equal(Png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
    }

    [Fact]
    public void Incremental_graph_and_images_publish_together_and_retained_photo_evidence_keeps_resources_after_clear()
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateIncrementalFileBacked(fileObserver: observer, filePublisher: publisher);
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var path = Import("\"avery.png\"");
        File.WriteAllBytes(Path.Combine(Producer, "avery.png"), Png);
        File.WriteAllBytes(Path.Combine(Producer, "blake.jpg"), Jpeg);
        Assert.True(app.ApplyImport(app.PreviewImportFile(path).Id), app.Status);
        var originalPhoto = app.GetRelationships("avery@example.com")!.Person.PhotoPath!;
        var note = app.CreatePersonNote("avery@example.com", "Private", "Unchanged writing")!;
        var before = WorkspaceFixtures.IndexedDocuments(Workspace);
        var bytes = before.ToDictionary(e => e.Key, e => File.ReadAllBytes(e.Value));
        var pointer = File.ReadAllBytes(Path.Combine(Workspace, "workspace.json"));
        File.WriteAllText(path, """
            {"schemaVersion":1,"batchId":"43000000-0000-4000-8000-000000000003",
             "observedAt":"2026-11-03T17:00:00Z","producer":"resource-test","people":[
             {"email":"avery@example.com","managerEmail":"blake@example.com","photoPath":"blake.jpg","isFavorite":true},
             {"email":"blake@example.com","displayName":"Blake","managerEmail":null,"photoPath":"avery.png"}]}
            """);
        var preview = app.PreviewImportFile(path);
        Assert.True(preview.CanApply, string.Join("\n", preview.Errors));
        FileStream? held = null;
        publisher.BeforePublish = (_, destination) =>
        {
            if (destination == Path.Combine(Workspace, "workspace.json"))
                held = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        observer.Effects.Clear();
        try { Assert.False(app.ApplyImport(preview.Id)); }
        finally { held?.Dispose(); }
        publisher.BeforePublish = null;
        Assert.False(app.CanSafelyClose);
        Assert.Contains("NOT saved", app.LocalSaveError);
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Workspace, "workspace.json")));
        foreach (var (key, file) in before) Assert.Equal(bytes[key], File.ReadAllBytes(file));
        Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Write &&
            e.Path.StartsWith(Path.Combine(Workspace, "documents", "photos")));
        using (var prior = ApplicationWorkflow.CreateIncrementalFileBacked())
        {
            Assert.True(prior.OpenWorkspace(Workspace), prior.Status);
            Assert.Single(prior.People);
            Assert.Null(prior.GetRelationships("avery@example.com")!.Manager);
            Assert.Equal(Png, prior.GetPhoto("avery@example.com")!.Bytes.ToArray());
            Assert.Empty(prior.GetProfileTimeline("avery@example.com")!.Changes);
        }
        observer.Effects.Clear();
        Assert.True(app.ApplyImport(app.PreviewImportFile(path).Id), app.Status);
        Assert.True(app.CanSafelyClose);
        var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        Assert.NotEmpty(writes);
        Assert.True(writes.Sum(e => e.Bytes) < 131072, $"Two-Person resource import wrote {writes.Sum(e => e.Bytes)} bytes.");
        var publications = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Publish)
            .ToDictionary(e => e.Path, e => e.Destination!);
        var current = WorkspaceFixtures.IndexedDocuments(Workspace);
        foreach (var key in new[] { "photos:" + originalPhoto, "acceptedBatches:10000000-0000-4000-8000-000000000001" })
        {
            Assert.Equal(before[key], current[key]);
            Assert.Equal(bytes[key], File.ReadAllBytes(current[key]));
            Assert.DoesNotContain(writes, e => e.Path.StartsWith(Path.Combine(Workspace, "documents")) &&
                File.ReadAllBytes(publications.GetValueOrDefault(e.Path, e.Path)).AsSpan().SequenceEqual(bytes[key]));
        }
        using (var reopened = ApplicationWorkflow.CreateIncrementalFileBacked())
        {
            Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
            Assert.Equal("blake@example.com", reopened.GetRelationships("avery@example.com")!.Manager!.Email);
            Assert.Equal("avery@example.com", Assert.Single(reopened.GetRelationships("blake@example.com")!.ImportedReports).Email);
            Assert.Equal(Jpeg, reopened.GetPhoto("avery@example.com")!.Bytes.ToArray());
            Assert.Equal(Png, reopened.GetPhoto("blake@example.com")!.Bytes.ToArray());
            Assert.Equal(note, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
            var change = Assert.Single(reopened.GetProfileTimeline("avery@example.com")!.Changes, c => c.Field == "photoPath");
            Assert.Equal(originalPhoto, change.OldValue.GetString());
            Assert.Equal("resource-test", change.Evidence.Producer);
            Assert.Equal(DateTimeOffset.Parse("2026-11-03T17:00:00Z"), change.Evidence.ObservedAt);
            Assert.True(reopened.GetRelationships("avery@example.com")!.Person.IsFavorite);
            var saved = WorkspaceFixtures.Bytes(Workspace);
            Assert.True(reopened.ApplyImport(reopened.PreviewImportFile(path).Id), reopened.Status);
            var replayed = WorkspaceFixtures.Bytes(Workspace);
            Assert.Equal(saved.Keys.Order(), replayed.Keys.Order());
            foreach (var (file, value) in saved) Assert.Equal(value, replayed[file]);
        }
        observer.Effects.Clear();
        Assert.True(app.EditPhoto("avery@example.com", null), app.Status);
        Assert.True(app.EditPhoto("blake@example.com", null), app.Status);
        var clearWrites = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        Assert.NotEmpty(clearWrites);
        Assert.True(clearWrites.Sum(e => e.Bytes) < 65536, $"Clearing two photos wrote {clearWrites.Sum(e => e.Bytes)} bytes.");
        Assert.DoesNotContain(clearWrites, e => e.Path.StartsWith(Path.Combine(Workspace, "documents", "photos")) ||
            e.Path.StartsWith(Path.Combine(Workspace, "documents", "acceptedBatches")) || e.Path.EndsWith(".md"));
        Directory.Delete(Producer, true);
        observer.Effects.Clear();
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Write &&
            e.Path.StartsWith(Path.Combine(Workspace, "documents", "photos")));
        using var cleared = ApplicationWorkflow.CreateIncrementalFileBacked();
        Assert.True(cleared.OpenWorkspace(Workspace), cleared.Status);
        Assert.Null(cleared.GetPhoto("avery@example.com"));
        Assert.Null(cleared.GetPhoto("blake@example.com"));
        var retained = WorkspaceFixtures.IndexedDocuments(Workspace);
        foreach (var (key, file) in current.Where(e => e.Key.StartsWith("photos:") || e.Key.StartsWith("acceptedBatches:")))
            Assert.Equal(file, retained[key]);
        Assert.Equal(2, retained.Keys.Count(k => k.StartsWith("photos:")));
        Assert.Equal(Png, File.ReadAllBytes(retained["photos:" + originalPhoto]));
        var replacedPhoto = cleared.GetProfileTimeline("avery@example.com")!.Changes.First(c => c.Field == "photoPath").NewValue.GetString()!;
        Assert.Equal(Jpeg, File.ReadAllBytes(retained["photos:" + replacedPhoto]));
    }

    private sealed class RecordingObserver : IWorkspaceFileObserver
    {
        public System.Collections.Concurrent.ConcurrentQueue<WorkspaceFileEffect> Effects { get; } = new();
        public void Observe(WorkspaceFileEffect effect) => Effects.Enqueue(effect);
    }

    private sealed class InspectingPublisher : IWorkspaceFilePublisher
    {
        public Action<string, string>? BeforePublish { get; set; }
        public void Publish(string staged, string destination, string? backup)
        {
            BeforePublish?.Invoke(staged, destination);
            if (File.Exists(destination)) File.Replace(staged, destination, backup);
            else File.Move(staged, destination);
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
