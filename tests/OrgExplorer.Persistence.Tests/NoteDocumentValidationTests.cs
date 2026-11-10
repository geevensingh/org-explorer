using System.Text.Json.Nodes;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class NoteDocumentValidationTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    public static IEnumerable<object[]> InvalidMetadata()
    {
        foreach (var scope in Enum.GetValues<NoteScope>())
            foreach (var (field, value) in new[]
            {
                ("id", "00000000-0000-0000-0000-000000000000"), ("title", " "),
                ("ownerEmail", "not an email"), ("createdAt", "0001-01-01T00:00:00Z"),
                ("updatedAt", "2020-01-01T00:00:00Z"), ("createdAt", "2026-01-01T01:00:00+01:00"),
                ("updatedAt", "2099-01-01T01:00:00+01:00"), ("unknownProperty", "unknown")
            })
                yield return [scope, field, value];
    }

    [Theory]
    [MemberData(nameof(InvalidMetadata))]
    public void Invalid_common_metadata_keeps_last_good_note_and_never_repairs_delivered_bytes(
        NoteScope scope, string field, string value)
    {
        using var app = Open();
        var empty = app.BeginNoteDraft(scope, "avery@example.com")!;
        var note = app.SaveNoteDraft(empty, "Context", "# Retained\r\nExact Markdown\r\n")!;
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", note.Id + ".md");
        var text = File.ReadAllText(file);
        var end = text.IndexOf("\n---\n", StringComparison.Ordinal);
        var metadata = JsonNode.Parse(text[4..end])!.AsObject();
        metadata[field] = value;
        var invalid = "---\r\n" + metadata.ToJsonString() + "\r\n---\r\n# Delivered writing\r\n";
        File.WriteAllText(file, invalid);
        var bytes = File.ReadAllBytes(file);
        Assert.False(app.RefreshWorkspace());
        Assert.Equal("# Retained\r\nExact Markdown\r\n", app.BeginNoteDraft(scope, "avery@example.com", note.Id)!.Markdown);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(folder));
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Filename_must_match_note_identity_without_repairing_provider_copies(NoteScope scope)
    {
        using var app = Open();
        var note = app.SaveNoteDraft(app.BeginNoteDraft(scope, "avery@example.com")!, "Context", "Writing")!;
        var directory = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes");
        var file = Path.Combine(directory, note.Id + ".md");
        var copy = Path.Combine(directory, "provider-conflict.md");
        File.Move(file, copy);
        var bytes = File.ReadAllBytes(copy);
        Assert.False(app.RefreshWorkspace());
        Assert.Contains("filename must match", app.Status);
        Assert.Equal(bytes, File.ReadAllBytes(copy));
        Assert.False(File.Exists(file));
    }

    private WorkspaceViewModel Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var preview = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
            "observedAt":"2026-11-01T17:00:00Z","people":[
            {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
            """);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        return app;
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
