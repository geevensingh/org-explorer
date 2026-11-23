using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    bool ConfigureSynchronizationFolder(string folder, bool joinExistingWorkspace = false,
        string? expectedWorkspacePath = null, Guid? expectedWorkspaceId = null);
    bool SyncNow();
    IDisposable DeferSnapshotAdoption();
}

public sealed partial class ApplicationWorkflow
{
    private readonly object synchronizationGate = new();
    private WorkspaceSynchronization synchronization = new(false);
    private Guid[] localAncestry = [];
    private Guid[] localResolvedRevisions = [];
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
        SnapshotRow[] TeamContexts, SnapshotRow[] HistoricalContexts, SnapshotPhoto[] Photos,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid[]? ResolvedRevisions = null);
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
            CREATE TABLE IF NOT EXISTS resolved_revisions (
                revision TEXT PRIMARY KEY
            );
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
            localResolvedRevisions = [];
            joiningWorkspace = false;
            synchronization = new(true, WorkspaceId: identity, LocalRevision: committedRevision);
            return;
        }
        command.CommandText = "SELECT revision FROM revision_ancestry ORDER BY sequence;";
        var ancestry = new List<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ancestry.Add(ParseSnapshotId(reader.GetString(0)));
        ValidateAncestry(ancestry.ToArray(), committedRevision);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='resolved_revisions';";
        var resolved = new List<Guid>();
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            command.CommandText = "SELECT revision FROM resolved_revisions ORDER BY revision;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) resolved.Add(ParseSnapshotId(reader.GetString(0)));
        }
        command.CommandText = "SELECT folder,published_revision,joining,divergence FROM synchronization WHERE singleton=1;";
        using var state = command.ExecuteReader();
        if (!state.Read()) throw new FormatException("Local synchronization metadata is incomplete.");
        var published = state.IsDBNull(1) ? (Guid?)null : ParseSnapshotId(state.GetString(1));
        if (published is { } known && !ancestry.Contains(known))
            throw new FormatException("Published revision is not in the local Workspace ancestry.");
        localAncestry = ancestry.ToArray();
        localResolvedRevisions = resolved.ToArray();
        joiningWorkspace = state.GetInt32(2) != 0;
        var divergence = state.IsDBNull(3) ? null : state.GetString(3);
        var folder = state.IsDBNull(0) ? null : state.GetString(0);
        var error = divergence ?? (Synchronization.WorkspaceId == identity && Synchronization.Folder == folder
            ? Synchronization.Error : null);
        var phase = Synchronization.WorkspaceId == identity && Synchronization.Folder == folder
            ? Synchronization.Phase : WorkspaceSynchronizationPhase.Idle;
        synchronization = new(true, folder, identity, committedRevision, published, error, divergence is not null, phase);
    }

    public bool ConfigureSynchronizationFolder(string folder, bool joinExistingWorkspace = false,
        string? expectedWorkspacePath = null, Guid? expectedWorkspaceId = null)
    {
        lock (synchronizationGate)
        {
            try
            {
                string path, full, expectedRevision;
                Guid? identity;
                string? previousFolder;
                lock (workspaceGate)
                {
                    if (!ExpectedWorkspaceMatches())
                        return Fail("The Workspace changed before synchronization configuration. Nothing was configured for the current Workspace.");
                    if (!localSqlite || !workspaceCurrent)
                        throw new IOException("Open a current local SQLite Workspace before configuring synchronization.");
                    OpenLocalSqlite(false);
                    if (!ExpectedWorkspaceMatches())
                        return Fail("The Workspace changed before synchronization configuration. Nothing was configured for the current Workspace.");
                    full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                    path = workspace!;
                    var local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                    if (ContainsPath(full, local) || ContainsPath(local, full))
                        throw new IOException("The synchronization folder and authoritative local Workspace must be separate, non-nested folders.");
                    EnsureConfigurationAllowed();
                    identity = Synchronization.WorkspaceId;
                    expectedRevision = revision!;
                    previousFolder = Synchronization.Folder;
                }
                RejectSnapshotLinks(full);
                Directory.CreateDirectory(full);
                if (Directory.Exists(Path.Combine(full, "people")) || File.Exists(Path.Combine(full, "workspace.json")) ||
                    File.Exists(Path.Combine(full, SqliteDatabase)) || File.Exists(Path.Combine(full, SqliteDiscriminator)))
                    throw new IOException("Choose a snapshot interchange folder, not an existing legacy or local Workspace.");
                lock (workspaceGate)
                {
                    EnsureSynchronizationWorkspace(path, previousFolder, identity);
                    if (!ExpectedWorkspaceMatches() || revision != expectedRevision)
                        throw new IOException("The local Workspace changed before folder configuration. Refresh before retrying.");
                    EnsureConfigurationAllowed();
                    using var connection = OpenLocalConnection();
                    using var transaction = connection.BeginTransaction();
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    EnsureSynchronizationHead(command, identity, expectedRevision);
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
                    EnsureConfigurationAllowed();
                    transaction.Commit();
                    OpenLocalSqlite(false);
                    Status = joinExistingWorkspace ? "Synchronization folder configured. Sync now will validate the existing Workspace before joining." :
                        "Synchronization folder configured. Local commits remain independent; use Sync now to publish.";
                    return true;
                }
            }
            catch (Exception error) when (StorageError(error)) { return SynchronizationFailed(error.Message); }
        }

        bool ExpectedWorkspaceMatches() =>
            expectedWorkspacePath is null && expectedWorkspaceId is null ||
            string.Equals(expectedWorkspacePath, workspace, StringComparison.OrdinalIgnoreCase) &&
                expectedWorkspaceId == Synchronization.WorkspaceId;

        void EnsureConfigurationAllowed()
        {
            if (joinExistingWorkspace && (people.Count != 0 || batches.Count != 0 ||
                localAncestry.Length != 1 || Synchronization.PublishedRevision is not null || !CanSafelyClose))
                throw new IOException("Join requires a new empty local Workspace without writing or publication.");
            if (Synchronization.Diverged)
                throw new IOException("Preserved divergent versions require explicit resolution before changing synchronization folders.");
        }
    }

    private void EnsureSynchronizationWorkspace(string path, string? folder, Guid? identity)
    {
        if (!localSqlite || !workspaceCurrent || workspace != path ||
            Synchronization.WorkspaceId != identity || Synchronization.Folder != folder)
            throw new SnapshotAdoptionDeferred("Workspace changed while synchronization was running. Retry for the current Workspace.");
    }

    private static void EnsureSynchronizationHead(SqliteCommand command, Guid? identity, string expectedRevision)
    {
        command.CommandText = "SELECT identity, revision, format_version FROM workspace WHERE singleton=1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(2) != 3 ||
            ParseSnapshotId(reader.GetString(0)) != identity || reader.GetString(1) != expectedRevision)
            throw new SnapshotAdoptionDeferred("The local Workspace identity or commit changed before synchronization could commit. Refresh before retrying.");
    }

    public bool SyncNow()
    {
        bool result;
        lock (synchronizationGate)
        {
            try
            {
                string path, folder;
                Guid? identity;
                bool joining;
                lock (workspaceGate)
                {
                    if (!localSqlite || !workspaceCurrent || Synchronization.Folder is not { } configured)
                        throw new IOException("Configure a synchronization folder for a current local SQLite Workspace first.");
                    path = workspace!;
                    folder = configured;
                    identity = Synchronization.WorkspaceId;
                    joining = joiningWorkspace;
                    if (Synchronization.Diverged) throw new IOException("Divergent versions are preserved. Explicit resolution is required.");
                }
                RejectSnapshotLinks(folder);
                if (!Directory.Exists(folder)) throw new IOException("The synchronization folder is unavailable. Local writing is retained.");
                using var folderLease = AcquireSnapshotFolder(folder);
                SetSynchronizationPhase(WorkspaceSynchronizationPhase.Checking);
                var delivered = Directory.GetFiles(folder, "*.oesnapshot").Order(StringComparer.Ordinal)
                    .Select(ReadDeliveredSnapshot).ToArray();
                foreach (var item in delivered)
                {
                    using var validated = ValidateSnapshot(path, item.Data);
                    if (!joining && item.Data.WorkspaceId != identity ||
                        delivered.Any(other => item.Data.WorkspaceId != other.Data.WorkspaceId))
                        throw new FormatException("Synchronization folder contains a different Workspace identity. Nothing adopted or overwritten.");
                    if (delivered.Any(other => other.Data.Revision == item.Data.Revision && other.Hash != item.Hash))
                        throw new FormatException("One immutable snapshot revision has conflicting content. Nothing adopted.");
                }
                var heads = delivered.DistinctBy(item => item.Data.Revision).Where(item =>
                    !delivered.Any(other => item.Data.Revision != other.Data.Revision &&
                        Continues(item.Data.Ancestry, other.Data.Ancestry, other.Data.ResolvedRevisions))).ToArray();
                if (heads.Length > 1)
                {
                    foreach (var item in heads) PreserveDivergence(path, item);
                    throw new IOException("Divergent complete Workspace versions were preserved; no automatic choice was made.");
                }
                var newest = heads.SingleOrDefault();
                if (newest is not null)
                {
                    using var candidate = ValidateSnapshot(path, newest.Data);
                    LogicalSnapshot? previous = null;
                    string? expectedRevision = null;
                    lock (workspaceGate)
                    {
                        EnsureSynchronizationWorkspace(path, folder, identity);
                        // A second local process may have committed while folder validation ran.
                        ObserveStoragePhase("snapshot.local-read", () => OpenLocalSqlite(false));
                        EnsureSynchronizationWorkspace(path, folder, identity);
                        if (!joiningWorkspace && !Continues(localAncestry, newest.Data.Ancestry, newest.Data.ResolvedRevisions) &&
                            !Continues(newest.Data.Ancestry, localAncestry, localResolvedRevisions))
                        {
                            PreserveDivergence(path, newest);
                            throw new IOException("Local and incoming Workspace versions diverged. Both are preserved; no automatic choice was made.");
                        }
                        if (joiningWorkspace || newest.Data.Revision != Synchronization.LocalRevision &&
                            Continues(localAncestry, newest.Data.Ancestry, newest.Data.ResolvedRevisions))
                        {
                            EnsureSnapshotAdoptionAllowed();
                            expectedRevision = revision;
                            if (!joiningWorkspace) previous = CaptureLocalSnapshot();
                        }
                    }
                    if (expectedRevision is not null)
                    {
                        if (previous is not null)
                        {
                            using var validatedPrevious = ValidateSnapshot(path, previous);
                            EnsureSnapshotArtifact(folder, EncodeSnapshot(previous));
                        }
                        lock (workspaceGate)
                        {
                            EnsureSynchronizationWorkspace(path, folder, identity);
                            if (revision != expectedRevision || joiningWorkspace != joining)
                                throw new SnapshotAdoptionDeferred("A new local commit arrived during retention. Incoming data was not installed.");
                            AdoptSnapshot(candidate, newest);
                        }
                    }
                }
                else if (joining)
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

    private static bool Continues(Guid[] earlier, Guid[] later, Guid[]? resolved) =>
        IsPrefix(earlier, later) || resolved?.Contains(earlier[^1]) == true;

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
        if (data.ResolvedRevisions is { } resolved &&
            (resolved.Any(id => id == Guid.Empty || id == data.Revision) || resolved.Distinct().Count() != resolved.Length))
            throw new FormatException("Invalid explicit Workspace resolution ancestry.");
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
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='resolved_revisions';";
        Guid[]? resolved = null;
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            command.CommandText = "SELECT revision FROM resolved_revisions ORDER BY revision;";
            var items = new List<Guid>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) items.Add(ParseSnapshotId(reader.GetString(0)));
            if (items.Count != 0) resolved = items.ToArray();
        }
        return new(3, identity, commit, ancestry.ToArray(), peopleRows, historyRows, accepted,
            notes.ToArray(), contexts, historical, resources.ToArray(), resolved);

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
        EnsureSnapshotArtifact(folder, encoded);
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
        ReclaimRoutineSnapshots(path, folder, snapshot);
    }

    private void EnsureSnapshotArtifact(string folder, DeliveredSnapshot encoded)
    {
        var snapshot = encoded.Data;
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
            ObserveStoragePhase("snapshot.validate-write", () =>
            {
                using var connection = candidate.OpenLocalConnection();
                using var transaction = connection.BeginTransaction();
                WriteLogicalSnapshot(connection, transaction, data);
                ObserveStoragePhase("snapshot.validate-commit", transaction.Commit);
            });
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
        command.CommandText = "DELETE FROM resolved_revisions;";
        command.ExecuteNonQuery();
        foreach (var resolved in data.ResolvedRevisions ?? [])
            Insert("INSERT INTO resolved_revisions(revision) VALUES($a);", resolved.ToString("D"));

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

    private void EnsureSnapshotAdoptionAllowed(bool explicitChoice = false)
    {
        if (LocalSave.HasUnresolvedOutcome || !CanSafelyClose || pending is not null ||
            Volatile.Read(ref snapshotAdoptionDeferrals) != 0 ||
            (joiningWorkspace
                ? people.Count != 0 || batches.Count != 0 || localAncestry.Length != 1 || Synchronization.PublishedRevision is not null
                : !explicitChoice && Synchronization.HasUnpublishedChanges))
            throw new IOException("Incoming version is waiting: unpublished local changes or unsaved drafts must be preserved.");
    }

    private void AdoptSnapshot(ValidatedSnapshot candidate, DeliveredSnapshot incoming, bool explicitChoice = false)
    {
        EnsureSnapshotAdoptionAllowed(explicitChoice);
        try
        {
            var expected = revision;
            using var connection = OpenLocalConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            EnsureSynchronizationHead(command, Synchronization.WorkspaceId, expected!);
            using var evidence = BeginLocalWrite(incoming.Data.Revision.ToString("D"));
            WriteLogicalSnapshot(connection, transaction, incoming.Data);
            command.CommandText = """
                UPDATE synchronization SET published_revision=$revision, joining=0,
                    divergence=CASE WHEN $choice=1 THEN
                        'Complete version selected; arrival validation must finish before editing resumes.'
                        ELSE divergence END WHERE singleton=1;
                """;
            command.Parameters.AddWithValue("$revision", explicitChoice ? DBNull.Value : incoming.Data.Revision.ToString("D"));
            command.Parameters.AddWithValue("$choice", explicitChoice ? 1 : 0);
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
                localResolvedRevisions = incoming.Data.ResolvedRevisions ?? [];
                joiningWorkspace = false;
                synchronization = Synchronization with
                {
                    WorkspaceId = incoming.Data.WorkspaceId, LocalRevision = incoming.Data.Revision,
                    PublishedRevision = explicitChoice ? null : incoming.Data.Revision, Error = null
                };
                compatibility = accepted.compatibility with
                {
                    WorkspacePath = workspace,
                    SupportsOrdinaryEdits = !Synchronization.Diverged,
                    SupportsNoteEdits = !Synchronization.Diverged
                };
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
            RejectSnapshotLinks(directory);
            Directory.CreateDirectory(directory);
            Preserve(incoming);
            Preserve(EncodeSnapshot(CaptureLocalSnapshot()));
            void Preserve(DeliveredSnapshot item)
            {
                var evidence = Path.Combine(directory, $"{item.Data.Revision:D}.oesnapshot");
                RejectSnapshotLinks(evidence);
                if (!File.Exists(evidence)) WriteDurablyBytes(evidence, item.Bytes);
                else if (ReadDeliveredSnapshot(evidence).Hash != item.Hash)
                    throw new FormatException("Preserved immutable conflict evidence has changed. Nothing was replaced.");
            }
        }
    }
}
