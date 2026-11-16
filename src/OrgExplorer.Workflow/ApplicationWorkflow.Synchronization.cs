using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OrgExplorer.Workflow;

public enum WorkspaceSynchronizationPhase { Idle, Checking, Adopting, Publishing }

public sealed record WorkspaceSynchronization(bool Supported, string? Folder = null,
    Guid? WorkspaceId = null, Guid? LocalRevision = null, Guid? PublishedRevision = null,
    string? Error = null, bool Diverged = false, WorkspaceSynchronizationPhase Phase = WorkspaceSynchronizationPhase.Idle)
{
    public bool HasUnpublishedChanges => Supported && LocalRevision != PublishedRevision;
    public string Summary => !Supported ? "Synchronization requires a local SQLite Workspace." : (Phase switch
        {
            WorkspaceSynchronizationPhase.Checking => "Checking complete incoming versions.\n",
            WorkspaceSynchronizationPhase.Adopting => "Installing a validated incoming version.\n",
            WorkspaceSynchronizationPhase.Publishing => "Publishing a complete local version.\n",
            _ => ""
        }) +
        (HasUnpublishedChanges ? "Locally saved; latest version is not confirmed published." : "Locally saved and published.") +
        " Provider delivery elsewhere is unknown." + (Error is null ? "" : "\n" + Error);
}

public partial interface IApplicationWorkflow
{
    WorkspaceSynchronization Synchronization { get; }
    bool ConfigureSynchronizationFolder(string folder, bool joinExistingWorkspace = false);
    bool SyncNow();
    IDisposable DeferSnapshotAdoption();
}

public sealed partial class ApplicationWorkflow
{
    private readonly object synchronizationGate = new();
    private WorkspaceSynchronization synchronization = new(false);
    private Guid[] localAncestry = [];
    private bool joiningWorkspace;
    private int snapshotAdoptionDeferrals;
    private const string SnapshotFormat = "OrgExplorer complete logical Workspace";
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonOptions)
    { RespectRequiredConstructorParameters = true };
    private sealed record SnapshotRow(string Key, string Value);
    private sealed record SnapshotNote(string Scope, string Id, string Owner, string Metadata, string Markdown);
    private sealed record SnapshotPhoto(string Reference, byte[] Content);
    private sealed record LogicalSnapshot(int WorkspaceFormat, Guid WorkspaceId, Guid Revision, Guid[] Ancestry,
        SnapshotRow[] People, SnapshotRow[] History, SnapshotRow[] AcceptedImports, SnapshotNote[] Notes,
        SnapshotRow[] TeamContexts, SnapshotRow[] HistoricalContexts, SnapshotPhoto[] Photos);
    private sealed record SnapshotEnvelope(string Format, int Version, string Sha256, byte[] Content);
    private sealed record DeliveredSnapshot(LogicalSnapshot Data, string Hash, byte[] Bytes);
    private sealed class SnapshotAdoptionDeferred(string message) : IOException(message);

    public WorkspaceSynchronization Synchronization => Volatile.Read(ref synchronization);

    public IDisposable DeferSnapshotAdoption()
    {
        lock (profileDraftGate)
        {
            snapshotAdoptionDeferrals++;
            return new SnapshotAdoptionDeferral(this);
        }
    }

    private sealed class SnapshotAdoptionDeferral(ApplicationWorkflow workflow) : IDisposable
    {
        private ApplicationWorkflow? owner = workflow;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref owner, null) is not { } current) return;
            lock (current.profileDraftGate) current.snapshotAdoptionDeferrals--;
        }
    }

    private static void EnsureLocalSynchronization(SqliteCommand command)
    {
        command.Parameters.Clear();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS synchronization (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), folder TEXT,
                published_revision TEXT, joining INTEGER NOT NULL DEFAULT 0,
                divergence TEXT
            );
            INSERT OR IGNORE INTO synchronization(singleton) VALUES(1);
            CREATE TABLE IF NOT EXISTS revision_ancestry (
                sequence INTEGER PRIMARY KEY, revision TEXT NOT NULL UNIQUE
            );
            INSERT INTO revision_ancestry(revision)
                SELECT revision FROM workspace WHERE NOT EXISTS(SELECT 1 FROM revision_ancestry);
            """;
        command.ExecuteNonQuery();
    }

    private string AdvanceLocalRevision(SqliteCommand command, int version, string? nextRevision = null)
    {
        EnsureLocalSynchronization(command);
        var next = nextRevision ?? Guid.NewGuid().ToString("D");
        command.CommandText = """
            UPDATE workspace SET format_version=$version, revision=$revision WHERE singleton=1;
            INSERT INTO revision_ancestry(revision) VALUES($revision);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$revision", next);
        command.ExecuteNonQuery();
        return next;
    }

    private void AcceptLocalRevision(string next)
    {
        revision = next;
        localAncestry = [.. localAncestry, Guid.Parse(next)];
        synchronization = synchronization with { LocalRevision = Guid.Parse(next) };
    }

    private void LoadLocalSynchronization(SqliteConnection connection, Guid identity, Guid committedRevision)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='synchronization';";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
        {
            localAncestry = [committedRevision];
            joiningWorkspace = false;
            synchronization = new(true, WorkspaceId: identity, LocalRevision: committedRevision);
            return;
        }
        command.CommandText = "SELECT revision FROM revision_ancestry ORDER BY sequence;";
        var ancestry = new List<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ancestry.Add(ParseSnapshotId(reader.GetString(0)));
        ValidateAncestry(ancestry.ToArray(), committedRevision);
        command.CommandText = "SELECT folder,published_revision,joining,divergence FROM synchronization WHERE singleton=1;";
        using var state = command.ExecuteReader();
        if (!state.Read()) throw new FormatException("Local synchronization metadata is incomplete.");
        var published = state.IsDBNull(1) ? (Guid?)null : ParseSnapshotId(state.GetString(1));
        if (published is { } known && !ancestry.Contains(known))
            throw new FormatException("Published revision is not in the local Workspace ancestry.");
        localAncestry = ancestry.ToArray();
        joiningWorkspace = state.GetInt32(2) != 0;
        var divergence = state.IsDBNull(3) ? null : state.GetString(3);
        var folder = state.IsDBNull(0) ? null : state.GetString(0);
        var error = divergence ?? (Synchronization.WorkspaceId == identity && Synchronization.Folder == folder
            ? Synchronization.Error : null);
        var phase = Synchronization.WorkspaceId == identity && Synchronization.Folder == folder
            ? Synchronization.Phase : WorkspaceSynchronizationPhase.Idle;
        synchronization = new(true, folder, identity, committedRevision, published, error, divergence is not null, phase);
    }

    public bool ConfigureSynchronizationFolder(string folder, bool joinExistingWorkspace = false)
    {
        lock (synchronizationGate)
        lock (workspaceGate)
        {
            try
            {
                if (!localSqlite || !workspaceCurrent)
                    throw new IOException("Open a current local SQLite Workspace before configuring synchronization.");
                OpenLocalSqlite(false);
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                var local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace!));
                if (ContainsPath(full, local) || ContainsPath(local, full))
                    throw new IOException("The synchronization folder and authoritative local Workspace must be separate, non-nested folders.");
                RejectSnapshotLinks(full);
                if (joinExistingWorkspace && (people.Count != 0 || batches.Count != 0 ||
                    localAncestry.Length != 1 || Synchronization.PublishedRevision is not null || !CanSafelyClose))
                    throw new IOException("Join requires a new empty local Workspace without writing or publication.");
                if (Synchronization.Diverged)
                    throw new IOException("Preserved divergent versions require explicit resolution before changing synchronization folders.");
                Directory.CreateDirectory(full);
                if (Directory.Exists(Path.Combine(full, "people")) || File.Exists(Path.Combine(full, "workspace.json")) ||
                    File.Exists(Path.Combine(full, SqliteDatabase)) || File.Exists(Path.Combine(full, SqliteDiscriminator)))
                    throw new IOException("Choose a snapshot interchange folder, not an existing legacy or local Workspace.");
                using var connection = OpenLocalConnection();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                if (LocalSqliteRevision() != revision)
                    throw new IOException("A new local commit arrived before folder configuration. Refresh before retrying.");
                EnsureLocalSynchronization(command);
                CreateLocalNoteSchema(command);
                CreateLocalProfileSchema(command);
                command.CommandText = """
                    UPDATE workspace SET format_version=3 WHERE singleton=1;
                    UPDATE synchronization SET folder=$folder, joining=$joining,
                        published_revision=CASE WHEN folder=$folder THEN published_revision ELSE NULL END WHERE singleton=1;
                    """;
                command.Parameters.AddWithValue("$folder", full);
                command.Parameters.AddWithValue("$joining", joinExistingWorkspace ? 1 : 0);
                command.ExecuteNonQuery();
                transaction.Commit();
                OpenLocalSqlite(false);
                Status = joinExistingWorkspace ? "Synchronization folder configured. Sync now will validate the existing Workspace before joining." :
                    "Synchronization folder configured. Local commits remain independent; use Sync now to publish.";
                return true;
            }
            catch (Exception error) when (StorageError(error)) { return SynchronizationFailed(error.Message); }
        }
    }

    public bool SyncNow()
    {
        bool result;
        lock (synchronizationGate)
        {
            try
            {
                string path, folder;
                lock (workspaceGate)
                {
                    if (!localSqlite || !workspaceCurrent || Synchronization.Folder is not { } configured)
                        throw new IOException("Configure a synchronization folder for a current local SQLite Workspace first.");
                    path = workspace!;
                    folder = configured;
                    if (Synchronization.Diverged) throw new IOException("Divergent versions are preserved. Explicit resolution is required.");
                }
                RejectSnapshotLinks(folder);
                if (!Directory.Exists(folder)) throw new IOException("The synchronization folder is unavailable. Local writing is retained.");
                SetSynchronizationPhase(WorkspaceSynchronizationPhase.Checking);
                var delivered = Directory.GetFiles(folder, "*.oesnapshot").Order(StringComparer.Ordinal)
                    .Select(ReadDeliveredSnapshot).ToArray();
                DeliveredSnapshot? newest = null;
                foreach (var item in delivered)
                {
                    using var validated = ValidateSnapshot(path, item.Data);
                    if (!joiningWorkspace && item.Data.WorkspaceId != Synchronization.WorkspaceId ||
                        newest is not null && item.Data.WorkspaceId != newest.Data.WorkspaceId)
                        throw new FormatException("Synchronization folder contains a different Workspace identity. Nothing adopted or overwritten.");
                    if (newest is null) newest = item;
                    else if (newest.Data.Revision == item.Data.Revision && newest.Hash != item.Hash)
                        throw new FormatException("One immutable snapshot revision has conflicting content. Nothing adopted.");
                    else if (IsPrefix(newest.Data.Ancestry, item.Data.Ancestry)) newest = item;
                    else if (!IsPrefix(item.Data.Ancestry, newest.Data.Ancestry))
                    {
                        PreserveDivergence(path, newest);
                        PreserveDivergence(path, item);
                        throw new IOException("Divergent complete Workspace versions were preserved; no automatic choice was made.");
                    }
                }
                if (newest is not null)
                {
                    using var candidate = ValidateSnapshot(path, newest.Data);
                    lock (workspaceGate)
                    {
                        if (workspace != path) throw new IOException("Workspace changed while synchronization was running. Retry for the current Workspace.");
                        // A second local process may have committed while folder validation ran.
                        ObserveStoragePhase("snapshot.local-read", () => OpenLocalSqlite(false));
                        if (!joiningWorkspace && !IsPrefix(localAncestry, newest.Data.Ancestry) &&
                            !IsPrefix(newest.Data.Ancestry, localAncestry))
                        {
                            PreserveDivergence(path, newest);
                            throw new IOException("Local and incoming Workspace versions diverged. Both are preserved; no automatic choice was made.");
                        }
                        if (joiningWorkspace || newest.Data.Revision != Synchronization.LocalRevision &&
                            IsPrefix(localAncestry, newest.Data.Ancestry))
                            AdoptSnapshot(candidate, newest);
                    }
                }
                else if (joiningWorkspace)
                    throw new IOException("No complete Workspace snapshot is available to join. Incomplete delivery is not an empty Workspace.");
                SetSynchronizationPhase(WorkspaceSynchronizationPhase.Publishing);
                PublishLocalSnapshot(path, folder);
                synchronization = Synchronization with { Error = null, Phase = WorkspaceSynchronizationPhase.Idle };
                Status = Synchronization.Summary;
                result = true;
            }
            catch (Exception error) when (StorageError(error)) { result = SynchronizationFailed(error.Message); }
        }
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private bool SynchronizationFailed(string message)
    {
        synchronization = Synchronization with { Error = "Synchronization failed: " + message, Phase = WorkspaceSynchronizationPhase.Idle };
        return Fail(Synchronization.Summary);
    }

    private void SetSynchronizationPhase(WorkspaceSynchronizationPhase phase)
    {
        synchronization = Synchronization with { Phase = phase };
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Guid ParseSnapshotId(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id :
            throw new FormatException("Invalid Workspace identity or revision.");

    private static void ValidateAncestry(Guid[] ancestry, Guid revision)
    {
        if (ancestry.Length == 0 || ancestry[^1] != revision || ancestry.Any(id => id == Guid.Empty) ||
            ancestry.Distinct().Count() != ancestry.Length)
            throw new FormatException("Invalid or incomplete Workspace revision ancestry.");
    }

    private static bool IsPrefix(Guid[] earlier, Guid[] later) =>
        earlier.Length <= later.Length && earlier.SequenceEqual(later.Take(earlier.Length));

    private static bool ContainsPath(string parent, string child) => parent.Equals(child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectSnapshotLinks(string path)
    {
        for (var item = path; item is not null; item = Path.GetDirectoryName(item))
            RejectWorkspaceLink(item, allowMissing: true);
    }

    private static DeliveredSnapshot ReadDeliveredSnapshot(string path)
    {
        RejectSnapshotLinks(path);
        var bytes = File.ReadAllBytes(path);
        var envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(bytes, SnapshotJson)
            ?? throw new FormatException("Snapshot envelope is empty.");
        if (envelope.Format != SnapshotFormat || envelope.Version != 1)
            throw new FormatException("Incompatible Workspace snapshot format. No partial adoption or migration is performed.");
        if (envelope.Content is null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Content)))
            throw new FormatException("Snapshot completeness/integrity validation failed.");
        var data = JsonSerializer.Deserialize<LogicalSnapshot>(envelope.Content, SnapshotJson)
            ?? throw new FormatException("Logical snapshot is empty.");
        if (data.WorkspaceFormat != 3 || data.WorkspaceId == Guid.Empty || data.Revision == Guid.Empty ||
            data.Ancestry is null || data.People is null || data.History is null || data.AcceptedImports is null ||
            data.Notes is null || data.TeamContexts is null || data.HistoricalContexts is null || data.Photos is null)
            throw new FormatException("Incompatible or incomplete logical Workspace snapshot.");
        ValidateAncestry(data.Ancestry, data.Revision);
        foreach (var rows in new[] { data.People, data.History, data.AcceptedImports, data.TeamContexts, data.HistoricalContexts })
            if (rows.Any(row => row is null || row.Key is null || row.Value is null))
                throw new FormatException("Incomplete logical snapshot record.");
        if (data.Notes.Any(note => note is null || note.Scope is null || note.Id is null || note.Owner is null ||
                note.Metadata is null || note.Markdown is null) ||
            data.Photos.Any(photo => photo is null || photo.Reference is null || photo.Content is null))
            throw new FormatException("Incomplete snapshot note or resource.");
        foreach (var row in data.History) JsonSerializer.Deserialize<PersonHistory>(row.Value, SnapshotJson);
        foreach (var row in data.AcceptedImports) JsonSerializer.Deserialize<AcceptedBatch>(row.Value, SnapshotJson);
        foreach (var row in data.HistoricalContexts)
            JsonSerializer.Deserialize<List<HistoricalTeamContext>>(row.Value, SnapshotJson);
        return new(data, envelope.Sha256, bytes);
    }

    private LogicalSnapshot CaptureLocalSnapshot()
    {
        using var connection = OpenLocalConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT identity, revision, format_version FROM workspace WHERE singleton=1;";
        Guid identity, commit;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new FormatException("No local committed Workspace is available.");
            if (reader.GetInt32(2) != 3) throw new FormatException("Incompatible local format for complete snapshot publication.");
            identity = ParseSnapshotId(reader.GetString(0));
            commit = ParseSnapshotId(reader.GetString(1));
        }
        command.CommandText = "SELECT revision FROM revision_ancestry ORDER BY sequence;";
        var ancestry = new List<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ancestry.Add(ParseSnapshotId(reader.GetString(0)));
        var peopleRows = Rows("people", "email", "profile");
        var historyRows = Rows("profile_history", "email", "history");
        var accepted = Rows("accepted_batches", "id", "batch");
        var contexts = Rows("team_context", "email", "revision");
        var historical = Rows("historical_team_context", "email", "contexts");
        command.CommandText = "SELECT scope,id,owner_email,metadata,markdown FROM notes ORDER BY scope,id;";
        var notes = new List<SnapshotNote>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) notes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4)));
        command.CommandText = "SELECT reference,content FROM photos ORDER BY reference;";
        var resources = new List<SnapshotPhoto>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) resources.Add(new(reader.GetString(0), (byte[])reader.GetValue(1)));
        ValidateAncestry(ancestry.ToArray(), commit);
        return new(3, identity, commit, ancestry.ToArray(), peopleRows, historyRows, accepted,
            notes.ToArray(), contexts, historical, resources.ToArray());

        SnapshotRow[] Rows(string table, string key, string value)
        {
            command.CommandText = $"SELECT {key},{value} FROM {table} ORDER BY {key};";
            var rows = new List<SnapshotRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1)));
            return rows.ToArray();
        }
    }

    private void PublishLocalSnapshot(string path, string folder)
    {
        LogicalSnapshot snapshot;
        lock (workspaceGate)
        {
            if (workspace != path || joiningWorkspace)
                throw new IOException("Workspace changed or has not yet joined. No empty or unrelated version was published.");
            snapshot = CaptureLocalSnapshot();
        }
        using var validated = ValidateSnapshot(path, snapshot);
        var encoded = EncodeSnapshot(snapshot);
        var destination = Path.Combine(folder, $"{snapshot.WorkspaceId:D}-{snapshot.Revision:D}.oesnapshot");
        if (File.Exists(destination))
        {
            if (ReadDeliveredSnapshot(destination).Hash != encoded.Hash)
                throw new FormatException("An immutable snapshot revision has different content. Existing evidence was not replaced.");
        }
        else
        {
            var staged = Path.Combine(folder, $".{snapshot.Revision:D}-{Guid.NewGuid():N}.pending");
            WriteDurablyBytes(staged, encoded.Bytes);
            filePublisher.Publish(staged, destination, null, WorkspaceFilePublicationMode.Create);
            if (ReadDeliveredSnapshot(destination).Hash != encoded.Hash)
                throw new IOException("Publication could not be verified. Local data remains saved; provider delivery is unknown.");
        }
        lock (workspaceGate)
        {
            if (workspace != path) throw new IOException("Workspace changed before publication could be recorded.");
            using var connection = OpenLocalConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE synchronization SET published_revision=$revision WHERE singleton=1 AND folder=$folder
                    AND EXISTS(SELECT 1 FROM workspace WHERE identity=$identity AND format_version=3)
                    AND EXISTS(SELECT 1 FROM revision_ancestry WHERE revision=$revision);
                """;
            command.Parameters.AddWithValue("$revision", snapshot.Revision.ToString("D"));
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue("$identity", snapshot.WorkspaceId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw new IOException("Synchronization configuration changed during publication.");
            transaction.Commit();
            OpenLocalSqlite(false);
        }
    }

    private static DeliveredSnapshot EncodeSnapshot(LogicalSnapshot snapshot)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJson);
        var hash = Convert.ToHexString(SHA256.HashData(content));
        return new(snapshot, hash, JsonSerializer.SerializeToUtf8Bytes(new SnapshotEnvelope(SnapshotFormat, 1, hash, content), SnapshotJson));
    }

    private sealed class ValidatedSnapshot(ApplicationWorkflow workflow, string directory) : IDisposable
    {
        public ApplicationWorkflow Workflow { get; } = workflow;
        public void Dispose()
        {
            Workflow.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private ValidatedSnapshot ValidateSnapshot(string path, LogicalSnapshot data)
    {
        var directory = Path.Combine(path, "snapshot-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        var candidate = new ApplicationWorkflow(false, null, timeProvider) { workspace = directory, localSqlite = true };
        var validated = new ValidatedSnapshot(candidate, directory);
        try
        {
            candidate.OpenLocalSqlite(true);
            using (var connection = candidate.OpenLocalConnection())
            using (var transaction = connection.BeginTransaction())
            {
                WriteLogicalSnapshot(connection, transaction, data);
                transaction.Commit();
            }
            candidate.OpenLocalSqlite(false);
            return validated;
        }
        catch
        {
            validated.Dispose();
            throw;
        }
    }

    private static void WriteLogicalSnapshot(SqliteConnection connection, SqliteTransaction transaction, LogicalSnapshot data)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM notes; DELETE FROM team_context; DELETE FROM historical_team_context;
            DELETE FROM profile_history; DELETE FROM people; DELETE FROM accepted_batches; DELETE FROM photos;
            """;
        command.ExecuteNonQuery();
        Put("people", data.People);
        Put("profile_history", data.History);
        Put("accepted_batches", data.AcceptedImports);
        foreach (var note in data.Notes)
            Insert("INSERT INTO notes VALUES($a,$b,$c,$d,$e);", note.Scope, note.Id, note.Owner, note.Metadata, note.Markdown);
        Put("team_context", data.TeamContexts);
        Put("historical_team_context", data.HistoricalContexts);
        foreach (var photo in data.Photos) Insert("INSERT INTO photos VALUES($a,$b);", photo.Reference, photo.Content);
        Insert("UPDATE workspace SET format_version=3, identity=$a, revision=$b WHERE singleton=1;",
            data.WorkspaceId.ToString("D"), data.Revision.ToString("D"));
        EnsureLocalSynchronization(command);
        command.CommandText = "DELETE FROM revision_ancestry;";
        command.ExecuteNonQuery();
        foreach (var ancestor in data.Ancestry) Insert("INSERT INTO revision_ancestry(revision) VALUES($a);", ancestor.ToString("D"));

        void Put(string table, SnapshotRow[] rows)
        {
            foreach (var row in rows) Insert($"INSERT INTO {table} VALUES($a,$b);", row.Key, row.Value);
        }
        void Insert(string sql, params object[] values)
        {
            command.Parameters.Clear();
            command.CommandText = sql;
            for (var i = 0; i < values.Length; i++)
                command.Parameters.AddWithValue("$" + (char)('a' + i), values[i] ??
                    throw new FormatException("Incomplete snapshot record."));
            command.ExecuteNonQuery();
        }
    }

    private void AdoptSnapshot(ValidatedSnapshot candidate, DeliveredSnapshot incoming)
    {
        if (LocalSave.HasUnresolvedOutcome || !CanSafelyClose || pending is not null ||
            Volatile.Read(ref snapshotAdoptionDeferrals) != 0 ||
            (joiningWorkspace
                ? people.Count != 0 || batches.Count != 0 || localAncestry.Length != 1 || Synchronization.PublishedRevision is not null
                : Synchronization.HasUnpublishedChanges))
            throw new IOException("Incoming version is waiting: unpublished local changes or unsaved drafts must be preserved.");
        try
        {
            var expected = revision;
            using var connection = OpenLocalConnection();
            using var transaction = connection.BeginTransaction();
            if (LocalSqliteRevision() != expected)
                throw new SnapshotAdoptionDeferred("A new local commit arrived during adoption. Retry without overwriting it.");
            using var evidence = BeginLocalWrite(incoming.Data.Revision.ToString("D"));
            WriteLogicalSnapshot(connection, transaction, incoming.Data);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE synchronization SET published_revision=$revision, joining=0 WHERE singleton=1;";
            command.Parameters.AddWithValue("$revision", incoming.Data.Revision.ToString("D"));
            command.ExecuteNonQuery();
            // Seal clean adoption before commit, without holding the draft/navigation gate
            // over storage I/O. Drafts admitted after this point retain their immutable bases.
            lock (profileDraftGate)
            {
                if (LocalSave.HasUnresolvedOutcome || !CanSafelyClose || pending is not null || snapshotAdoptionDeferrals != 0)
                    throw new SnapshotAdoptionDeferred("A new draft arrived during adoption. Incoming data was not installed.");
            }
            SetSynchronizationPhase(WorkspaceSynchronizationPhase.Adopting);
            ObserveStoragePhase("snapshot.adopt-commit", () => CommitLocalWrite(transaction, evidence));
            lock (profileDraftGate)
            {
                var accepted = candidate.Workflow;
                people = accepted.people;
                batches = accepted.batches;
                profileHistory = accepted.profileHistory;
                historicalTeamContexts = accepted.historicalTeamContexts;
                personNotes = accepted.personNotes;
                teamNotes = accepted.teamNotes;
                teamNoteConfirmations = accepted.teamNoteConfirmations;
                teamContextRevisions = accepted.teamContextRevisions;
                photos = accepted.photos;
                revision = incoming.Data.Revision.ToString("D");
                localAncestry = incoming.Data.Ancestry;
                joiningWorkspace = false;
                synchronization = Synchronization with
                {
                    WorkspaceId = incoming.Data.WorkspaceId, LocalRevision = incoming.Data.Revision,
                    PublishedRevision = incoming.Data.Revision, Error = null
                };
                compatibility = accepted.compatibility with { WorkspacePath = workspace };
                foreach (var owner in localProfileEdits.Where(item => !item.Value.IsDirty).Select(item => item.Key).ToArray())
                    localProfileEdits.Remove(owner);
                if (profileEdit is not { IsDirty: true }) profileEdit = null;
                queryView = accepted.queryView;
            }
        }
        catch (Exception error) when (StorageError(error) && error is not SnapshotAdoptionDeferred)
        {
            LocalStorageFailed(error);
            throw;
        }
    }

    private void PreserveDivergence(string path, DeliveredSnapshot incoming)
    {
        lock (workspaceGate)
        {
            if (workspace != path) throw new IOException("Workspace changed during divergence detection. Retry synchronization.");
            using (var connection = OpenLocalConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE synchronization SET divergence=$error WHERE singleton=1;";
                const string error = "Divergent complete Workspace versions are preserved. Explicit resolution is required.";
                command.Parameters.AddWithValue("$error", error);
                command.ExecuteNonQuery();
                synchronization = Synchronization with { Diverged = true, Error = error };
                compatibility = compatibility with { SupportsOrdinaryEdits = false, SupportsNoteEdits = false };
            }
            var directory = Path.Combine(path, "snapshot-conflicts");
            Directory.CreateDirectory(directory);
            Preserve(incoming);
            Preserve(EncodeSnapshot(CaptureLocalSnapshot()));
            void Preserve(DeliveredSnapshot item)
            {
                var evidence = Path.Combine(directory, $"{item.Data.Revision:D}.oesnapshot");
                if (!File.Exists(evidence)) WriteDurablyBytes(evidence, item.Bytes);
                else if (ReadDeliveredSnapshot(evidence).Hash != item.Hash)
                    throw new FormatException("Preserved immutable conflict evidence has changed. Nothing was replaced.");
            }
        }
    }
}

public sealed partial class WorkspaceViewModel
{
    public WorkspaceSynchronization Synchronization => workflow.Synchronization;
    public bool ConfigureSynchronizationFolder(string folder, bool joinExistingWorkspace = false) =>
        Observe(() => workflow.ConfigureSynchronizationFolder(folder, joinExistingWorkspace));
    public bool SyncNow()
    {
        using var unresolvedAdoption = LocalSave.HasUnresolvedOutcome ? workflow.DeferSnapshotAdoption() : null;
        return Observe(workflow.SyncNow);
    }
    public IDisposable DeferSnapshotAdoption() => workflow.DeferSnapshotAdoption();
}
