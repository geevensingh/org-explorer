using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OrgExplorer.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Persistence.Tests;

public sealed class WalWorkspaceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Initial = """
        {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000093",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"manager@example.com","displayName":"Manager","managerEmail":null},
         {"email":"report@example.com","displayName":"Report","managerEmail":"manager@example.com"}]}
        """;

    [Fact]
    public async Task Uncheckpointed_committed_WAL_state_publishes_complete_notes_profiles_photos_and_history()
    {
        var local = Path.Combine(root, "local");
        var exchange = Path.Combine(root, "exchange");
        using var writer = ApplicationWorkflow.CreateFileBacked();
        Assert.True(writer.OpenWorkspace(local), writer.Status);
        Assert.True(writer.ApplyImport(writer.PreviewImport(Initial).Id), writer.Status);
        var person = writer.CreatePersonNote("report@example.com", "Person note", "Old Person body")!;
        var team = writer.CreateTeamNote("manager@example.com", "Team note", "Old Team body")!;
        Assert.True(writer.ConfigureSynchronizationFolder(exchange), writer.Status);
        Assert.True(writer.SyncNow(), writer.Status);
        using var gate = new HeldRead();
        using var oldReader = ApplicationWorkflow.CreateFileBacked(fileObserver: gate);
        var opening = Task.Run(() => oldReader.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(writer.EditPersonNote(person.Id, person.Title, "Latest Person body"), writer.Status);
            Assert.True(writer.EditTeamNote(team.Id, team.Title, "Latest Team body"), writer.Status);
            var photo = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
            var photoPath = Path.Combine(root, "photo.png");
            File.WriteAllBytes(photoPath, photo);
            Assert.True(writer.EditPhoto("report@example.com", photoPath), writer.Status);
            Assert.True(writer.EditPersonText("report@example.com", PersonTextField.Department, "Latest department"), writer.Status);
            Assert.True(writer.EditPersonManager("report@example.com", null), writer.Status);
            Assert.NotEmpty(writer.GetHistoricalTeamContexts("report@example.com"));
            using (var physical = new SqliteConnection($"Data Source={Path.Combine(local, "workspace.sqlite")};Pooling=False"))
            {
                physical.Open();
                using var mode = physical.CreateCommand();
                mode.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", mode.ExecuteScalar());
                mode.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                using var checkpoint = mode.ExecuteReader();
                Assert.True(checkpoint.Read());
                var frames = checkpoint.GetInt32(1);
                var copied = checkpoint.GetInt32(2);
                Assert.True(frames > copied, "The old actual public reader must retain committed, uncheckpointed WAL frames.");
                output.WriteLine($"WAL-PHYSICAL sqlite={physical.ServerVersion}; frames={frames}; checkpointed={copied}");
            }
            Assert.True(writer.SyncNow(), writer.Status);
            using var peer = ApplicationWorkflow.CreateFileBacked();
            Assert.True(peer.OpenWorkspace(Path.Combine(root, "peer")), peer.Status);
            Assert.True(peer.ConfigureSynchronizationFolder(exchange, joinExistingWorkspace: true), peer.Status);
            Assert.True(peer.SyncNow(), peer.Status);
            Assert.Equal(writer.Synchronization.LocalRevision, peer.Synchronization.LocalRevision);
            Assert.Equal("Latest Person body", peer.BeginNoteDraft(NoteScope.Person, "report@example.com", person.Id)!.Markdown);
            Assert.Equal("Latest Team body", peer.BeginNoteDraft(NoteScope.Team, "manager@example.com", team.Id)!.Markdown);
            Assert.Equal("Latest department", peer.GetRelationships("report@example.com")!.Person.Department);
            Assert.Null(peer.GetRelationships("report@example.com")!.Person.ManagerEmail);
            Assert.Equal(photo, peer.GetPhoto("report@example.com")!.Bytes.ToArray());
            Assert.Equal(JsonSerializer.Serialize(writer.GetProfileTimeline("report@example.com")),
                JsonSerializer.Serialize(peer.GetProfileTimeline("report@example.com")));
            Assert.Equal(JsonSerializer.Serialize(writer.GetHistoricalTeamContexts("report@example.com")),
                JsonSerializer.Serialize(peer.GetHistoricalTeamContexts("report@example.com")));
            Assert.DoesNotContain(Directory.GetFiles(exchange, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path).StartsWith("workspace.sqlite", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase));
            Assert.False(gate.Release.IsSet);
            output.WriteLine("WAL-SNAPSHOT " + JsonSerializer.Serialize(new
            {
                latestCommittedBeforeCheckpointAndReaderRelease = true,
                completeLogicalPeerMatches = true,
                photoSha256 = Convert.ToHexString(SHA256.HashData(photo)),
                writer.Synchronization.WorkspaceId, writer.Synchronization.LocalRevision
            }));
        }
        finally { gate.Release.Set(); Assert.True(await opening.WaitAsync(TimeSpan.FromSeconds(10)), oldReader.Status); }
        Assert.Equal("Old Person body", oldReader.BeginNoteDraft(NoteScope.Person, "report@example.com", person.Id)!.Markdown);
        Assert.Null(oldReader.GetPhoto("report@example.com"));
    }

    [Fact]
    public void Failed_WAL_negotiation_preserves_last_valid_graph_and_explicit_profile_draft_without_fallback()
    {
        using var model = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        var local = Path.Combine(root, "local");
        Assert.True(model.OpenWorkspace(local, WorkspaceOpenMode.OpenOrCreate), model.Status);
        Assert.True(model.ApplyImport(model.PreviewImport(Initial).Id), model.Status);
        Assert.True(model.BeginProfileEdit("report@example.com"), model.Status);
        Assert.True(model.StageProfileText(PersonTextField.Department, "Retained negotiation draft"), model.Status);
        var saved = JsonSerializer.Serialize(model.People);
        using (var external = new SqliteConnection($"Data Source={Path.Combine(local, "workspace.sqlite")};Pooling=False"))
        {
            external.Open();
            using var command = external.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            Assert.Equal("delete", command.ExecuteScalar());
            using var reader = external.BeginTransaction(deferred: true);
            command.Transaction = reader;
            command.CommandText = "PRAGMA schema_version;";
            command.ExecuteScalar();
            Assert.False(model.RefreshWorkspace());
            Assert.Equal(saved, JsonSerializer.Serialize(model.People));
            Assert.Equal("Retained negotiation draft", model.ProfileEdit!.Draft.Department);
            Assert.False(model.CanEditLocalWorkspace);
            Assert.True(model.CanNavigatePeople);
            reader.Rollback();
        }
        Assert.True(model.ReconcileLocalSaves(), model.Status);
        Assert.Equal("Retained negotiation draft", model.ProfileEdit!.Draft.Department);
        Assert.True(model.SaveProfileEdit(), model.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Retained negotiation draft", reopened.GetRelationships("report@example.com")!.Person.Department);
    }

    [Fact]
    public void Actual_writer_refuses_an_externally_changed_non_WAL_mode_until_explicit_recovery()
    {
        using var model = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        var local = Path.Combine(root, "local");
        Assert.True(model.OpenWorkspace(local, WorkspaceOpenMode.OpenOrCreate), model.Status);
        Assert.True(model.ApplyImport(model.PreviewImport(Initial).Id), model.Status);
        Assert.True(model.BeginProfileEdit("report@example.com"), model.Status);
        Assert.True(model.StageProfileText(PersonTextField.Department, "Exact refused writer draft"), model.Status);
        using (var external = new SqliteConnection($"Data Source={Path.Combine(local, "workspace.sqlite")};Pooling=False"))
        {
            external.Open();
            using var command = external.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            Assert.Equal("delete", command.ExecuteScalar());
        }
        Assert.False(model.SaveProfileEdit());
        Assert.Contains("WAL mode", model.Status);
        Assert.Equal("Exact refused writer draft", model.ProfileEdit!.Draft.Department);
        Assert.Null(model.GetRelationships("report@example.com")!.Person.Department);
        Assert.False(model.CanEditLocalWorkspace);
        Assert.True(model.ReconcileLocalSaves(), model.Status);
        Assert.True(model.SaveProfileEdit(), model.Status);
    }

    private sealed class HeldRead : IWorkspaceFileObserver, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name != "local-sqlite-read-snapshot" || phase.Kind != WorkspaceStoragePhaseKind.Begin) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("The owning WAL snapshot test did not release its reader.");
        }
        public void Dispose() => Release.Dispose();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
