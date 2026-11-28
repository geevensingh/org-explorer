using System.Security.Cryptography;
using System.Text.Json;
using System.IO.MemoryMappedFiles;
using Microsoft.Data.Sqlite;
using OrgExplorer.Desktop.Smoke;
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
    public async Task Commit_control_keeps_the_last_complete_record_while_the_next_transport_slot_is_uncommitted()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The WPF control transport uses Windows named mappings.");
        using var control = new SnapshotAdoptionCommitControl();
        var id = Guid.NewGuid();
        control.Arm();
        var publishing = Task.Run(() =>
        {
            control.ObservePhase(new(id, "snapshot.adopt-commit", WorkspaceStoragePhaseKind.Begin, 0));
            control.ObservePhase(new(id, "snapshot.adopt-commit", WorkspaceStoragePhaseKind.End, 0));
        });
        try
        {
            Assert.True(SpinWait.SpinUntil(() => control.Read()?.Held == true, TimeSpan.FromSeconds(5)));
            using var mapping = MemoryMappedFile.OpenExisting(control.ArmName + "-state");
            using var view = mapping.CreateViewAccessor();
            var committed = view.ReadInt32(0);
            var inactiveSlot = 8 + ((committed / 2 + 1) % 2) * 64;
            view.Write(inactiveSlot, int.MinValue);
            var stillCommitted = Assert.IsType<SnapshotAdoptionCommitState>(control.Read());
            Assert.Equal(id, stillCommitted.OperationId);
            Assert.Equal(Environment.ProcessId, stillCommitted.ProcessId);
            Assert.True(stillCommitted.Held);
        }
        finally { control.Release(); await publishing.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(id, control.Read()!.OperationId);
        Assert.Equal(WorkspaceStoragePhaseKind.End, control.Read()!.Kind);
    }

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Cold_validated_DELETE_workspace_remains_browsable_and_paused_until_explicit_WAL_recovery(
        bool independentRecoveryFault, bool legacySnapshotPublication)
    {
        var local = Path.Combine(root, "cold");
        Guid noteId;
        Guid? identity, revision;
        string saved, history, teamNotes, timeline;
        var photo = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(seed.OpenWorkspace(local), seed.Status);
            Assert.True(seed.ApplyImport(seed.PreviewImport(Initial).Id), seed.Status);
            noteId = seed.CreatePersonNote("report@example.com", "Exact cold note", "# Saved cold writing")!.Id;
            Assert.NotNull(seed.CreateTeamNote("manager@example.com", "Cold owned context", "# Saved Team writing"));
            Assert.True(seed.BeginProfileEdit("manager@example.com"), seed.Status);
            Assert.True(seed.StageProfileTeamName("Private cold Team"), seed.Status);
            Assert.True(seed.SaveProfileEdit(), seed.Status);
            var file = Path.Combine(root, "cold-photo.png");
            File.WriteAllBytes(file, photo);
            Assert.True(seed.EditPhoto("report@example.com", file), seed.Status);
            Assert.True(seed.EditPersonManager("report@example.com", null), seed.Status);
            identity = seed.Synchronization.WorkspaceId;
            revision = seed.Synchronization.LocalRevision;
            saved = JsonSerializer.Serialize(seed.People);
            history = JsonSerializer.Serialize(seed.GetHistoricalTeamContexts("report@example.com"));
            teamNotes = JsonSerializer.Serialize(seed.GetTeamNotes("manager@example.com", true));
            timeline = JsonSerializer.Serialize(seed.GetProfileTimeline("report@example.com"));
        }
        using var cold = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        var receipt = Path.Combine(local, $"local-write-{Guid.NewGuid():N}.json");
        if (independentRecoveryFault) File.WriteAllText(receipt, "Interrupted independent transaction evidence");
        using (var external = new SqliteConnection($"Data Source={Path.Combine(local, "workspace.sqlite")};Pooling=False"))
        {
            external.Open();
            using var command = external.CreateCommand();
            if (legacySnapshotPublication)
            {
                command.CommandText = "UPDATE synchronization SET snapshot_format=1;";
                command.ExecuteNonQuery();
            }
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            Assert.Equal("delete", command.ExecuteScalar());
            using var reader = external.BeginTransaction(deferred: true);
            command.Transaction = reader;
            command.CommandText = "PRAGMA schema_version;";
            command.ExecuteScalar();
            Assert.True(cold.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly), cold.Status);
            Assert.Equal(saved, JsonSerializer.Serialize(cold.People));
            Assert.Equal(identity, cold.Synchronization.WorkspaceId);
            Assert.Equal(revision, cold.Synchronization.LocalRevision);
            Assert.Equal("# Saved cold writing", cold.BeginNoteDraft(NoteScope.Person, "report@example.com", noteId)!.Markdown);
            Assert.Equal(teamNotes, JsonSerializer.Serialize(cold.GetTeamNotes("manager@example.com", true)));
            Assert.Equal(history, JsonSerializer.Serialize(cold.GetHistoricalTeamContexts("report@example.com")));
            Assert.Equal(timeline, JsonSerializer.Serialize(cold.GetProfileTimeline("report@example.com")));
            Assert.Equal(photo, cold.GetPhoto("report@example.com")!.Bytes.ToArray());
            Assert.True(cold.IsWorkspaceCurrent);
            Assert.True(cold.CanNavigatePeople);
            Assert.False(cold.CanEditLocalWorkspace);
            Assert.False(cold.Compatibility.SupportsOrdinaryEdits);
            Assert.False(cold.Compatibility.SupportsNoteEdits);
            Assert.Contains("WAL", cold.Compatibility.Guidance);
            Assert.Contains("read-only", cold.Compatibility.Guidance);
            if (independentRecoveryFault) Assert.True(cold.LocalSave.HasUnresolvedOutcome);
            else Assert.Contains("WAL", cold.LocalSave.Error);
            Assert.False(cold.ReconcileLocalSaves());
            Assert.Equal(saved, JsonSerializer.Serialize(cold.People));
            Assert.Equal(history, JsonSerializer.Serialize(cold.GetHistoricalTeamContexts("report@example.com")));
            Assert.Equal(photo, cold.GetPhoto("report@example.com")!.Bytes.ToArray());
            reader.Rollback();
        }
        var originalError = cold.LocalSave.Error;
        Assert.True(cold.RefreshWorkspace(), cold.Status);
        Assert.Equal(originalError, cold.LocalSave.Error);
        Assert.False(cold.EditFavorite("report@example.com", true));
        Assert.False(cold.CanEditLocalWorkspace);
        Assert.False(cold.CanSafelyClose);
        Assert.Equal(revision, cold.Synchronization.LocalRevision);
        Assert.Equal(saved, JsonSerializer.Serialize(cold.People));
        Assert.True(cold.NavigatePerson("report@example.com"), cold.Status);
        if (independentRecoveryFault)
        {
            Assert.False(cold.ReconcileLocalSaves());
            Assert.True(cold.LocalSave.HasUnresolvedOutcome);
            Assert.Equal(originalError, cold.LocalSave.Error);
            Assert.Equal("Interrupted independent transaction evidence", File.ReadAllText(receipt));
            File.Delete(receipt);
        }
        Assert.True(cold.ReconcileLocalSaves(), cold.Status);
        Assert.True(cold.CanEditLocalWorkspace);
        Assert.True(cold.Compatibility.SupportsOrdinaryEdits);
        Assert.True(cold.Compatibility.SupportsNoteEdits);
        Assert.Equal("# Saved cold writing", cold.BeginNoteDraft(NoteScope.Person, "report@example.com", noteId)!.Markdown);
        Assert.True(cold.EditFavorite("report@example.com", true), cold.Status);
        using var independent = ApplicationWorkflow.CreateFileBacked();
        Assert.True(independent.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly), independent.Status);
        Assert.True(independent.GetRelationships("report@example.com")!.Person.IsFavorite);
        Assert.Equal("# Saved cold writing", independent.BeginNoteDraft(NoteScope.Person, "report@example.com", noteId)!.Markdown);
        output.WriteLine("COLD-WAL-READONLY: complete validated graph/identity/revision/note browsable through genuine mode-change refusal; no direct-write auto-recovery; only explicit reconciliation enables writing.");
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
