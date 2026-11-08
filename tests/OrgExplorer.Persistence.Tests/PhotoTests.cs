using OrgExplorer.Workflow;
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

    [Fact]
    public void Decoded_PNG_is_available_on_the_profile_after_producer_removal_and_Workspace_reopen()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var import = Import("\"avery.data\"");
        File.WriteAllBytes(Path.Combine(Producer, "avery.data"), Png);
        var preview = app.PreviewImportFile(import);
        Assert.True(preview.CanApply, string.Join("\n", preview.Errors));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Directory.Delete(Producer, true);

        var reopened = ApplicationWorkflow.CreateFileBacked();
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
        var app = ApplicationWorkflow.CreateFileBacked();
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
        var app = ApplicationWorkflow.CreateFileBacked();
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

    private IApplicationWorkflow ImportOriginalPhoto()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        var import = Import("\"avery.png\"");
        File.WriteAllBytes(Path.Combine(Producer, "avery.png"), Png);
        Assert.True(app.ApplyImport(app.PreviewImportFile(import).Id), app.Status);
        return app;
    }

    [Fact]
    public void Omitted_photo_preserves_image_and_null_clears_it_durably_without_clearing_other_fields()
    {
        var app = ImportOriginalPhoto();
        Assert.True(app.ApplyImport(app.PreviewImportFile(Import(null, 2, "Avery Updated")).Id), app.Status);
        Assert.Equal(Png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        var clear = app.PreviewImportFile(Import("null", 3, "Avery Updated"));
        Assert.Contains(clear.Changes, change => change.Contains("Photo") && change.Contains("clear"));
        Assert.True(app.ApplyImport(clear.Id), app.Status);
        var reopened = ApplicationWorkflow.CreateFileBacked();
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
        var app = ImportOriginalPhoto();
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
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        Assert.Equal("Avery Example", Assert.Single(reopened.People).DirectoryName);
        Assert.Equal(Png, reopened.GetPhoto("avery@example.com")!.Bytes.ToArray());
    }

    [Fact]
    public void Changed_image_bytes_invalidate_preview_even_when_JSON_path_and_timestamps_are_unchanged()
    {
        var app = ImportOriginalPhoto();
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

    [Fact]
    public void Accepted_batch_replay_is_a_no_op_but_same_path_with_different_image_bytes_conflicts_after_reopen()
    {
        var app = ImportOriginalPhoto();
        Assert.True(app.EditPreferredName("avery@example.com", "Local name"));
        var reopened = ApplicationWorkflow.CreateFileBacked();
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
    public void Directory_links_are_resolved_and_only_contained_targets_are_accepted(bool escaping)
    {
        var app = ImportOriginalPhoto();
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
        var app = ImportOriginalPhoto();
        var target = Path.Combine(escaping ? root : Producer, "target.png");
        File.WriteAllBytes(target, Png);
        File.CreateSymbolicLink(Path.Combine(Producer, "link.png"), target);
        var preview = app.PreviewImportFile(Import("\"link.png\"", 2, "Avery Updated"));
        Assert.Equal(!escaping, preview.CanApply);
        Assert.Equal(!escaping, app.ApplyImport(preview.Id));
        Assert.Equal(escaping ? "Avery Example" : "Avery Updated", Assert.Single(app.People).DirectoryName);
    }

    [Fact]
    public void A_local_photo_edit_is_copied_and_saved_but_invalid_replacement_does_not_erase_it()
    {
        var app = ImportOriginalPhoto();
        var selected = Path.Combine(Producer, "selected.jpg");
        File.WriteAllBytes(selected, Jpeg);
        Assert.True(app.EditPhoto("avery@example.com", selected), app.Status);
        File.WriteAllText(selected, "Corrupt replacement");
        Assert.False(app.EditPhoto("avery@example.com", selected));
        Directory.Delete(Producer, true);
        var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace), reopened.Status);
        Assert.Equal("image/jpeg", reopened.GetPhoto("avery@example.com")!.ContentType);
        Assert.True(reopened.EditPhoto("avery@example.com", null), reopened.Status);
        Assert.Null(reopened.GetPhoto("avery@example.com"));
    }

    [Fact]
    public void Distinct_images_stay_with_the_intended_Person_and_reordered_replay_remains_a_no_op()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
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
        var app = ImportOriginalPhoto();
        var preview = app.PreviewImportFile(Import("\"avery.png\"", 2, "Must not apply"));
        File.Delete(Path.Combine(Producer, "avery.png"));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal("Avery Example", Assert.Single(app.People).DirectoryName);
        Assert.Equal(Png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
