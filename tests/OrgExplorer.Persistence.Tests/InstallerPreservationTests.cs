using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Persistence.Tests;

public sealed class InstallerPreservationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")[..12]));
    private string Workspace => Path.Combine(root, "workspace");
    private string Published => Path.Combine(root, "published");

    [Fact]
    [Trait("Category", "Filesystem")]
    public void Normal_publication_after_safe_local_handoff_changes_database_bytes_but_preserves_the_complete_installer_fixture()
    {
        Seed();
        var before = Capture("handoff");
        Assert.NotEqual(before.Head, before.PublishedRevision);
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), app.Status);
            Assert.True(app.CanSafelyClose);
            Assert.True(app.Synchronization.HasUnpublishedChanges);
            Assert.True(app.SyncNow(), app.Synchronization.Error);
            Assert.True(app.VerifyExitPublication(), app.Status);
        }
        var after = Capture("normal-close");
        Assert.NotEqual(before.Files["workspace.sqlite"], after.Files["workspace.sqlite"]);
        Assert.Equal(before.WorkspaceId, after.WorkspaceId);
        Assert.Equal(before.Head, after.Head);
        Assert.Equal(before.PublicStateSha256, after.PublicStateSha256);
        Assert.Equal(before.ImportReplays, after.ImportReplays);
        output.WriteLine($"Publication-only SQLite SHA256: {before.Files["workspace.sqlite"]} -> {after.Files["workspace.sqlite"]}");
        output.WriteLine($"Exact identity/head/public-state preserved: {after.WorkspaceId:D}/{after.Head:D}/{after.PublicStateSha256}");
        output.WriteLine("Replayed accepted Imports: " + string.Join(", ", after.ImportReplays.Select(replay => replay.BatchId)));
        before.RequirePreserved(after, "normal publication after locally committed installer handoff");
    }

    [Theory]
    [InlineData("person-note")]
    [InlineData("note-owner")]
    [InlineData("note-guid")]
    [InlineData("history")]
    [InlineData("frozen")]
    [InlineData("archived-person")]
    [InlineData("identity")]
    [InlineData("head")]
    [InlineData("import-1")]
    [InlineData("import-2")]
    [Trait("Category", "Filesystem")]
    public void Complete_fixture_oracle_rejects_committed_state_loss_without_relying_only_on_revision_changes(string damage)
    {
        Seed();
        Publish();
        var before = Capture("before");
        ReplaceFromSnapshot(data =>
        {
            if (damage.StartsWith("import-", StringComparison.Ordinal))
            {
                var index = damage == "import-1" ? 0 : 1;
                var key = before.ImportReplays[index].BatchId.ToString("D");
                WorkspaceFixtures.EditSnapshotRow(data, "acceptedImports", key,
                    batch => batch["fingerprint"] = new string('A', 64));
            }
            else if (damage == "identity") data["workspaceId"] = Guid.NewGuid().ToString("D");
            else if (damage == "head")
            {
                var next = Guid.NewGuid().ToString("D");
                data["revision"] = next;
                data["ancestry"]!.AsArray().Add(next);
            }
            else if (damage == "history")
                WorkspaceFixtures.EditSnapshotRow(data, "history", "avery@example.com", history => history["changes"] = new JsonArray());
            else if (damage == "frozen")
                RemoveRows(data["historicalContexts"]!.AsArray(), row => row["key"]!.GetValue<string>() == "avery@example.com");
            else if (damage == "archived-person")
            {
                foreach (var family in new[] { "people", "history", "teamContexts", "historicalContexts" })
                    RemoveRows(data[family]!.AsArray(), row => row["key"]!.GetValue<string>() == "morgan@example.com");
                RemoveRows(data["notes"]!.AsArray(), row => row["owner"]!.GetValue<string>() == "morgan@example.com");
            }
            else
            {
                var notes = data["notes"]!.AsArray();
                var note = notes.First(row => row!["scope"]!.GetValue<string>() == "person")!;
                if (damage == "person-note") notes.Remove(note);
                else
                {
                    var metadata = JsonNode.Parse(note["metadata"]!.GetValue<string>())!;
                    if (damage == "note-owner")
                    {
                        metadata["ownerEmail"] = "casey@example.com";
                        note["owner"] = "casey@example.com";
                    }
                    else
                    {
                        var id = Guid.NewGuid().ToString("D");
                        metadata["id"] = id;
                        note["id"] = id;
                    }
                    note["metadata"] = metadata.ToJsonString();
                }
            }
        });
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), app.Status);
            if (damage.StartsWith("import-", StringComparison.Ordinal))
            {
                Assert.Equal(before.WorkspaceId, app.Synchronization.WorkspaceId);
                Assert.Equal(before.Head, app.Synchronization.LocalRevision);
                Assert.Equal(before.PublicStateSha256, WorkspacePublicState.Hash(app));
                Assert.Throws<InvalidOperationException>(() => Capture("after"));
                return;
            }
            if (damage == "archived-person")
            {
                Assert.Null(app.GetRelationships("morgan@example.com"));
                Assert.Equal(before.WorkspaceId, app.Synchronization.WorkspaceId);
                Assert.Equal(before.Head, app.Synchronization.LocalRevision);
                Assert.NotEqual(before.PublicStateSha256, WorkspacePublicState.Hash(app));
                Assert.Throws<InvalidOperationException>(() => Capture("after"));
                return;
            }
        }
        var after = Capture("after");
        if (damage is not ("identity" or "head"))
        {
            Assert.Equal(before.WorkspaceId, after.WorkspaceId);
            Assert.Equal(before.Head, after.Head);
            Assert.NotEqual(before.PublicStateSha256, after.PublicStateSha256);
        }
        else
        {
            Assert.Equal(before.PublicStateSha256, after.PublicStateSha256);
            if (damage == "identity")
            {
                Assert.NotEqual(before.WorkspaceId, after.WorkspaceId);
                Assert.Equal(before.Head, after.Head);
            }
            else
            {
                Assert.Equal(before.WorkspaceId, after.WorkspaceId);
                Assert.NotEqual(before.Head, after.Head);
            }
        }
        Assert.Throws<InvalidOperationException>(() => before.RequirePreserved(after, damage));
    }

    [Fact]
    [Trait("Category", "Filesystem")]
    public void Current_photo_loss_is_detected_and_missing_required_retained_bytes_cannot_produce_a_preservation_receipt()
    {
        Seed();
        Publish();
        var before = Capture("before");
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), app.Status);
            Assert.True(app.EditPhoto("avery@example.com", null), app.Status);
            Assert.Null(app.GetPhoto("avery@example.com"));
            Assert.NotEqual(before.PublicStateSha256, WorkspacePublicState.Hash(app));
        }
        Assert.Throws<InvalidOperationException>(() => Capture("missing-current-photo"));
        Publish();
        Assert.False(ReplaceFromSnapshot(data => data["photos"] = new JsonArray()));
        Assert.Throws<InvalidOperationException>(() => Capture("missing-retained-photo"));
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("change")]
    [InlineData("add-sqlite-lookalike")]
    [Trait("Category", "Filesystem")]
    public void Publication_allowance_does_not_ignore_any_other_private_file(string damage)
    {
        Seed();
        Publish();
        var before = Capture("before");
        var path = Path.Combine(Workspace, "owner-private.txt");
        if (damage == "remove") File.Delete(path);
        else File.WriteAllText(damage == "change" ? path : Path.Combine(Workspace, "workspace.sqlite.extra"), "Unexpected private-file mutation");
        var after = Capture("after");
        Assert.Equal(before.PublicStateSha256, after.PublicStateSha256);
        Assert.Equal(before.Head, after.Head);
        var error = Assert.Throws<InvalidOperationException>(() => before.RequirePreserved(after, damage));
        Assert.Contains(damage == "add-sqlite-lookalike" ? "workspace.sqlite.extra" : "owner-private.txt", error.Message);
    }

    private bool ReplaceFromSnapshot(Action<JsonObject> change)
    {
        var head = CurrentHead();
        var original = Directory.GetFiles(Published, "*.oesnapshot")
            .Single(path => path.EndsWith(head + ".oesnapshot", StringComparison.Ordinal));
        foreach (var path in Directory.GetFiles(Published, "*.oesnapshot").Where(path => path != original)) File.Delete(path);
        WorkspaceFixtures.RewriteSnapshot(original, change);
        var changed = JsonNode.Parse(File.ReadAllBytes(original))!;
        var content = JsonNode.Parse(Convert.FromBase64String(changed["content"]!.GetValue<string>()))!;
        var name = $"{content["workspaceId"]!.GetValue<string>()}-{content["revision"]!.GetValue<string>()}.oesnapshot";
        if (Path.GetFileName(original) != name) File.Move(original, Path.Combine(Published, name));
        Directory.Move(Workspace, Path.Combine(root, "original"));
        using var receiver = ApplicationWorkflow.CreateFileBacked();
        Assert.True(receiver.OpenWorkspace(Workspace), receiver.Status);
        Assert.True(receiver.ConfigureSynchronizationFolder(Published, joinExistingWorkspace: true), receiver.Status);
        var joined = receiver.SyncNow();
        // A valid candidate can commit before immutable republishing is refused.
        // The caller must independently reopen and prove the exact intended mutation.
        if (!joined) output.WriteLine("Replacement snapshot was refused: " + receiver.Status);
        File.Copy(Path.Combine(root, "original", "owner-private.txt"), Path.Combine(Workspace, "owner-private.txt"));
        return joined;

        string CurrentHead()
        {
            using var app = ApplicationWorkflow.CreateFileBacked();
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), app.Status);
            return app.Synchronization.LocalRevision!.Value.ToString("D");
        }
    }

    private static void RemoveRows(JsonArray rows, Func<JsonNode, bool> predicate)
    {
        foreach (var row in rows.Where(row => predicate(row!)).ToArray()) rows.Remove(row);
    }

    private void Publish()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), app.Status);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.True(app.VerifyExitPublication(), app.Status);
    }

    private void Seed()
    {
        var producer = Path.Combine(root, "producer");
        Directory.CreateDirectory(producer);
        File.WriteAllBytes(Path.Combine(producer, "avery.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC"));
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        Apply(InstallerWorkspacePreservation.InitialImport);
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        Assert.NotNull(app.CreatePersonNote("avery@example.com", "Installer lifecycle note", "# Private fixture\nRetain this Person writing."));
        Assert.NotNull(app.CreateTeamNote("morgan@example.com", "Installer Team context", "# Frozen fixture\nRetain former Manager writing."));
        Apply(InstallerWorkspacePreservation.TransferImport);
        Assert.True(app.ConfigureSynchronizationFolder(Published) && app.SyncNow(), app.Status);
        Assert.NotNull(app.CreatePersonNote("avery@example.com", "Automatic update safe local writing",
            "Final installed-app writing before automatic restart."));
        Assert.True(app.CanSafelyClose && app.Synchronization.HasUnpublishedChanges, app.Status);
        Assert.True(app.People.Single(person => person.Email == "morgan@example.com").Archived);
        Assert.NotEmpty(app.GetHistoricalTeamContexts("avery@example.com"));
        File.WriteAllText(Path.Combine(Workspace, "owner-private.txt"), "Private non-SQLite bytes must not change.");
        Directory.Delete(producer, true);

        void Apply(string source)
        {
            var path = Path.Combine(producer, "import.json");
            File.WriteAllText(path, source);
            var preview = app.PreviewImportFile(path);
            Assert.True(preview.CanApply, app.Status);
            Assert.True(app.ApplyImport(preview.Id), app.Status);
        }
    }

    private InstallerPreservationEvidence Capture(string name) =>
        InstallerWorkspacePreservation.Capture(Workspace, Path.Combine(root, name));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
