using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class UiScaleHandoffTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "handoff-" + Guid.NewGuid().ToString("N")));
    private static UiScaleHandoff.Binding Binding => new(new string('a', 40), "123", "2",
        new string('b', 40), new string('c', 40), new string('d', 40), new string('e', 40),
        new() { ["product.dll"] = new string('A', 64) }, new() { ["harness.dll"] = new string('B', 64) },
        "fictitious-native-version", "fictitious-native-source", new string('C', 64));

    private UiScaleHandoff.Manifest Capture()
    {
        var workspace = Path.Combine(root, "source");
        var settingsPath = Path.Combine(root, "source-settings", "workspace-settings.json");
        using var source = ApplicationWorkflow.CreateFileBacked();
        Assert.True(source.OpenWorkspace(workspace), source.Status);
        ScaleFixture.Apply(source, ScaleFixture.Batch(new[] {
            new { email = "owner@example.com", displayName = "Owner", managerEmail = (string?)null },
            new { email = "report@example.com", displayName = "Report", managerEmail = (string?)"owner@example.com" }
        }, 1));
        Assert.True(source.EditFavorite("report@example.com", true), source.Status);
        Assert.True(source.EditFavorite("owner@example.com", true), source.Status);
        var note = source.CreateTeamNote("owner@example.com", "Actual note", "# Unchanged captured writing")!;
        var settings = new WorkspaceSettings(settingsPath);
        Assert.True(settings.OpenWorkspace(source, workspace), settings.PreferenceError);
        Assert.True(settings.SetLastView(workspace,
            new("report@example.com", SearchSource.InheritedTeamNote, note.Id, "owner@example.com")));
        Assert.True(settings.SetOngoingNote(workspace, NoteScope.Team, "owner@example.com", note.Id));
        Assert.True(settings.SetPaneProportions(workspace, new(33, 67)));
        Assert.True(source.ConfigureSynchronizationFolder(Path.Combine(root, "published")) && source.SyncNow(), source.Status);
        UiScaleHandoff.Capture(Path.Combine(root, "bundle"), workspace, Path.Combine(root, "published"), settingsPath,
            Binding, "run-" + new string('a', 32));
        return System.Text.Json.JsonSerializer.Deserialize<UiScaleHandoff.Manifest>(
            File.ReadAllText(Path.Combine(root, "bundle", "handoff.json")),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;
    }

    [Fact]
    [Trait("Category", "Filesystem")]
    public void Actual_published_snapshot_joins_a_fresh_store_after_original_files_are_gone_with_exact_selection_and_ongoing_notes()
    {
        var manifest = Capture();
        Directory.Delete(Path.Combine(root, "source"), true);
        Directory.Delete(Path.Combine(root, "published"), true);
        Directory.Delete(Path.Combine(root, "source-settings"), true);
        var workspace = Path.Combine(root, "receiver");
        var preference = Path.Combine(root, "receiver-settings", "workspace-settings.json");
        UiScaleHandoff.Receive(Path.Combine(root, "bundle"), workspace, Path.Combine(root, "incoming"),
            preference, Binding, Path.Combine(root, "receipt.json"));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        var settings = new WorkspaceSettings(preference);
        Assert.True(settings.RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal(manifest.WorkspaceId, reopened.Synchronization.WorkspaceId);
        Assert.Equal(manifest.Head, reopened.Synchronization.LocalRevision);
        Assert.Equal(manifest.Selection, settings.GetLastView(workspace));
        Assert.Equal(manifest.Selection.NoteId, settings.GetOngoingNote(workspace, NoteScope.Team, "owner@example.com"));
        Assert.Equal("# Unchanged captured writing", Assert.Single(reopened.GetTeamNotes("owner@example.com")).Markdown);
        Assert.Equal(["report@example.com", "owner@example.com"], reopened.FavoritePeople.Select(person => person.Email));
        Assert.Equal(new PaneProportions(33, 67), settings.GetPaneProportions(workspace));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("attempt")]
    [InlineData("product")]
    [InlineData("native")]
    [InlineData("payload")]
    [InlineData("selection")]
    [InlineData("extra-database")]
    [Trait("Category", "Filesystem")]
    public void Mismatched_or_corrupt_handoff_is_refused_before_receiver_creation(string defect)
    {
        var manifest = Capture();
        var binding = Binding;
        if (defect == "candidate") binding = binding with { Revision = new string('f', 40) };
        if (defect == "attempt") binding = binding with { RunAttempt = "3" };
        if (defect == "product") binding = binding with { ProductFiles = new() { ["product.dll"] = new string('D', 64) } };
        if (defect == "native") binding = binding with { NativeSourceId = "different-native-source" };
        var bundle = Path.Combine(root, "bundle");
        if (defect == "payload") File.AppendAllText(Path.Combine(bundle, manifest.SnapshotFile), "truncated delivery");
        if (defect == "extra-database") File.WriteAllText(Path.Combine(bundle, "workspace.db"), "forbidden raw copy");
        if (defect == "selection")
        {
            var path = Path.Combine(bundle, "handoff.json");
            var metadata = JsonNode.Parse(File.ReadAllText(path))!;
            metadata["selection"]!["noteId"] = Guid.NewGuid();
            File.WriteAllText(path, metadata.ToJsonString());
        }
        var receiver = Path.Combine(root, "receiver");
        Assert.ThrowsAny<Exception>(() => UiScaleHandoff.Receive(bundle, receiver, Path.Combine(root, "incoming"),
            Path.Combine(root, "receiver-settings", "workspace-settings.json"), binding, Path.Combine(root, "receipt.json")));
        Assert.False(Directory.Exists(receiver));
        Assert.False(File.Exists(Path.Combine(root, "receipt.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
