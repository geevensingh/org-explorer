using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private const string SqliteDatabase = "workspace.sqlite";
    private const string SqliteDiscriminator = "local-workspace.format";
    private const string SqliteFormat = "OrgExplorer local SQLite Workspace";
    private bool localSqlite;
    private static string LocalSqliteDescription(int version) =>
        $"Local SQLite Workspace (format {version}). Person profiles, photos, Team names, notes, history and reviewed Imports are available. " +
        "Explicit complete-snapshot synchronization is available in Settings. Provider delivery remains independent.";

    private SqliteConnection OpenLocalConnection(bool create = false, bool validateOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(workspace!, SqliteDatabase),
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA synchronous;";
            if (Convert.ToInt32(command.ExecuteScalar()) != 2)
                throw new IOException("Local SQLite FULL durability is unavailable. No write was started.");
            if (!validateOnly)
            {
                command.CommandText = create ? "PRAGMA journal_mode=WAL;" : "PRAGMA journal_mode;";
                if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Local SQLite WAL mode is unavailable. Reopen to reconcile before writing; no rollback-journal fallback is used.");
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EstablishLocalWal()
    {
        using var connection = OpenLocalConnection(validateOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        if (string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase)) return;
        command.CommandText = "PRAGMA journal_mode=WAL;";
        if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Local SQLite WAL mode could not be established. The validated data is unchanged; close other local clients and reopen. No rollback-journal fallback is used.");
    }

    private bool OpenLocalSqlite(bool create)
    {
        RequireLocalSqliteLocation(workspace!);
        if (File.Exists(Path.Combine(workspace!, "workspace.json")))
            throw new UnsupportedWorkspaceFormat(
                "Readable-file Workspaces are no longer supported. This app does not migrate them. " +
                "Preserve this folder and create a new local Workspace in a new empty folder.");
        if (create)
        {
            if (Directory.EnumerateFileSystemEntries(workspace!).Any())
                throw new IOException("Create a local SQLite Workspace in a new empty folder. Existing Workspaces are never converted or overwritten.");
            // The immutable discriminator prevents an interrupted creation or missing database
            // from being interpreted as an empty legacy Workspace.
            WriteDurablyBytes(Path.Combine(workspace!, SqliteDiscriminator),
                System.Text.Encoding.UTF8.GetBytes(SqliteFormat));
            using var connection = OpenLocalConnection(create: true);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                PRAGMA application_id=0x4f455850;
                CREATE TABLE workspace (
                    singleton INTEGER PRIMARY KEY CHECK (singleton=1),
                    format_version INTEGER NOT NULL,
                    identity TEXT NOT NULL,
                    revision TEXT NOT NULL
                );
                INSERT INTO workspace VALUES (1, 3, $identity, $revision);
                CREATE TABLE people (email TEXT PRIMARY KEY COLLATE NOCASE, profile TEXT NOT NULL);
                CREATE TABLE profile_history (
                    email TEXT PRIMARY KEY COLLATE NOCASE REFERENCES people(email), history TEXT NOT NULL
                );
                CREATE TABLE accepted_batches (id TEXT PRIMARY KEY, batch TEXT NOT NULL);
                """;
            command.Parameters.AddWithValue("$identity", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("D"));
            command.ExecuteNonQuery();
            CreateLocalNoteSchema(command);
            CreateLocalProfileSchema(command);
            transaction.Commit();
        }
        if (!File.Exists(Path.Combine(workspace!, SqliteDiscriminator)) ||
            File.ReadAllText(Path.Combine(workspace!, SqliteDiscriminator)) != SqliteFormat)
            throw new FormatException("Incompatible local SQLite Workspace discriminator. No migration or fallback is performed.");
        var loaded = new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, PersonHistory>(StringComparer.OrdinalIgnoreCase);
        var accepted = new Dictionary<string, AcceptedBatch>();
        var loadedPersonNotes = new Dictionary<Guid, PersonNote>();
        var loadedTeamNotes = new Dictionary<Guid, TeamNote>();
        var confirmations = new Dictionary<Guid, Guid>();
        var contexts = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var historical = new Dictionary<string, List<HistoricalTeamContext>>(StringComparer.OrdinalIgnoreCase);
        var loadedPhotos = new Dictionary<string, byte[]>();
        string committedRevision;
        int version;
        using var readObservation = fileObserver is null ? null : new LocalReadSnapshotObservation(fileObserver);
        using (var connection = OpenLocalConnection(validateOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA application_id;";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0x4f455850)
                throw new FormatException("Incompatible SQLite database. This is not an Org explorer local Workspace.");
            command.CommandText = "PRAGMA quick_check;";
            if (command.ExecuteScalar() as string != "ok")
                throw new FormatException("Local SQLite integrity validation failed. No partial Workspace was opened.");
            command.CommandText = "SELECT format_version, identity, revision FROM workspace WHERE singleton=1;";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new FormatException("No committed local SQLite Workspace exists. Creation did not complete.");
            version = reader.GetInt32(0);
            if (version != 3) throw new UnsupportedWorkspaceVersion("SQLite Workspace", version);
            if (!Guid.TryParseExact(reader.GetString(1), "D", out var identity) || identity == Guid.Empty ||
                !Guid.TryParseExact(reader.GetString(2), "D", out var commit) || commit == Guid.Empty)
                throw new FormatException("Invalid local SQLite Workspace identity or revision.");
            committedRevision = commit.ToString("D");
            reader.Close();
            readObservation?.KnownHeld();
            command.CommandText = "SELECT email, profile FROM people;";
            using (var profiles = command.ExecuteReader())
                while (profiles.Read())
                {
                    if (JsonNode.Parse(profiles.GetString(1)) is not JsonObject json)
                        throw new FormatException("Invalid local Person record.");
                    json.Add("schemaVersion", 2);
                    var person = ReadPersonRecord(JsonSerializer.SerializeToElement(json));
                    if (!profiles.GetString(0).Equals(person.Email, StringComparison.OrdinalIgnoreCase) ||
                        !loaded.TryAdd(person.Email, person))
                        throw new FormatException("Unsupported or inconsistent local Person data. Nothing is partially opened.");
                }
            ValidateGraph(loaded);
            command.CommandText = "SELECT email, history FROM profile_history;";
            using (var histories = command.ExecuteReader())
                while (histories.Read())
                {
                    var item = JsonSerializer.Deserialize<PersonHistory>(histories.GetString(1), JsonOptions)
                        ?? throw new FormatException("Invalid local Profile history.");
                    if (!histories.GetString(0).Equals(item.Email, StringComparison.OrdinalIgnoreCase) ||
                        !loaded.TryGetValue(item.Email, out var person) || !history.TryAdd(item.Email, item))
                        throw new FormatException("Unknown or duplicate local Profile history owner.");
                    ValidateProfileHistory(item, person);
                }
            if (history.Count != loaded.Count)
                throw new FormatException("Every local Person requires complete Profile history.");
            command.CommandText = "SELECT id, batch FROM accepted_batches;";
            using (var imports = command.ExecuteReader())
                while (imports.Read())
                {
                    var item = JsonSerializer.Deserialize<AcceptedBatch>(imports.GetString(1), JsonOptions);
                    ValidateAcceptedBatch(imports.GetString(0), item);
                    accepted.Add(imports.GetString(0), item!);
                }
            foreach (var item in history.Values)
            {
                foreach (var evidence in item.Observations)
                    RequireAcceptedEvidence(evidence);
                foreach (var change in item.Changes)
                {
                    RequireAcceptedEvidence(change.Evidence);
                    if (change.PreviousEvidence is { } prior) RequireAcceptedEvidence(prior);
                }
            }
            {
                command.CommandText = "SELECT scope, id, owner_email, metadata, markdown FROM notes;";
                using (var notes = command.ExecuteReader())
                    while (notes.Read())
                    {
                        var scope = notes.GetString(0) switch
                        {
                            "person" => NoteScope.Person,
                            "team" => NoteScope.Team,
                            _ => throw new FormatException("Unknown local note scope.")
                        };
                        NoteMetadata metadata = scope == NoteScope.Team
                            ? JsonSerializer.Deserialize<TeamNoteMetadata>(notes.GetString(3), JsonOptions)!
                            : JsonSerializer.Deserialize<NoteMetadata>(notes.GetString(3), JsonOptions)!;
                        if (metadata is null) throw new FormatException("Invalid local note metadata.");
                        ValidateNoteMetadata(metadata, scope, loaded, "Local note " + notes.GetString(1));
                        if (notes.GetString(1) != metadata.Id.ToString("D") ||
                            !notes.GetString(2).Equals(metadata.OwnerEmail, StringComparison.OrdinalIgnoreCase))
                            throw new FormatException("Inconsistent local note identity or owner.");
                        if (scope == NoteScope.Person)
                            loadedPersonNotes.Add(metadata.Id, new(metadata.Id, metadata.OwnerEmail, metadata.Title,
                                notes.GetString(4), metadata.Archived, metadata.CreatedAt, metadata.UpdatedAt));
                        else
                        {
                            loadedTeamNotes.Add(metadata.Id, new(metadata.Id, metadata.OwnerEmail, metadata.Title,
                                notes.GetString(4), metadata.Archived, metadata.CreatedAt, metadata.UpdatedAt));
                            confirmations.Add(metadata.Id, ((TeamNoteMetadata)metadata).ConfirmedContextRevision);
                        }
                    }
                command.CommandText = "SELECT email, revision FROM team_context;";
                using var context = command.ExecuteReader();
                while (context.Read())
                {
                    if (!loaded.ContainsKey(context.GetString(0)) ||
                        !Guid.TryParseExact(context.GetString(1), "D", out var contextRevision))
                        throw new FormatException("Invalid local Team context revision.");
                    contexts.Add(context.GetString(0), contextRevision);
                }
            }
            {
                command.CommandText = "SELECT email, contexts FROM historical_team_context;";
                using (var captures = command.ExecuteReader())
                {
                    var ids = new HashSet<Guid>();
                    while (captures.Read())
                    {
                        var email = captures.GetString(0);
                        var items = JsonSerializer.Deserialize<List<HistoricalTeamContext>>(captures.GetString(1), JsonOptions)
                            ?? throw new FormatException("Invalid local Historical team context.");
                        if (!loaded.ContainsKey(email) || !historical.TryAdd(email, items))
                            throw new FormatException("Unknown or duplicate Historical team context owner.");
                        foreach (var capture in items)
                        {
                            ValidateHistoricalCapture(capture, email, ids);
                            foreach (var transition in capture.Transitions)
                            {
                                RequireAcceptedEvidence(transition.Change.Evidence);
                                if (transition.Change.PreviousEvidence is { } prior) RequireAcceptedEvidence(prior);
                            }
                        }
                    }
                }
                command.CommandText = "SELECT reference, content FROM photos;";
                using var resources = command.ExecuteReader();
                while (resources.Read())
                {
                    var reference = resources.GetString(0);
                    var bytes = (byte[])resources.GetValue(1);
                    ValidateLocalPhoto(reference, bytes);
                    loadedPhotos.Add(reference, bytes);
                }
            }
            foreach (var reference in RequiredPhotos(loaded, history, historical))
            {
                ValidateHistoryValue("photoPath", JsonSerializer.SerializeToElement(reference));
                if (!loadedPhotos.ContainsKey(reference))
                    throw new FormatException("Required current or retained Profile photo is unavailable.");
            }
            LoadLocalSynchronization(connection, identity, commit);

            void RequireAcceptedEvidence(FieldEvidence evidence)
            {
                if (evidence.Source == ProfileChangeSource.Import &&
                    (evidence.BatchId is not { } batchId || !accepted.ContainsKey(batchId.ToString("D"))))
                    throw new FormatException("Local Profile evidence requires its accepted Import.");
            }
        }
        readObservation?.Released();
        var walReady = false;
        try { EstablishLocalWal(); walReady = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            localWalFault ??= new(LocalSavePhase.Failed, LocalSaveOutcome.NotCommitted,
                CommittedRevision: committedRevision, Error:
                $"Validated local Workspace is read-only: WAL mode is unavailable ({error.GetType().Name}). " +
                "No ordinary write was attempted. Browsing remains available. Explicitly reconcile local storage before editing. " +
                error.Message);
        }
        people = loaded;
        batches = accepted;
        profileHistory = history;
        revision = committedRevision;
        historicalTeamContexts = historical;
        personNotes = loadedPersonNotes;
        teamNotes = loadedTeamNotes;
        teamNoteConfirmations = confirmations;
        teamContextRevisions = contexts;
        photos = loadedPhotos;
        InvalidateProjections();
        workspaceCurrent = true;
        compatibility = new(WorkspaceCompatibilityState.Compatible,
            localWalFault?.Message ?? LocalSqliteDescription(version),
            workspace, version)
        {
            SupportsOrdinaryEdits = walReady && localWalFault is null && !synchronization.Diverged,
            SupportsNoteEdits = walReady && localWalFault is null && !synchronization.Diverged,
            SupportsNonblockingNoteAutosave = true, SupportedWorkspaceVersion = 3
        };
        ReconcileLocalWriteEvidence();
        return walReady;
    }

    private string LocalSqliteRevision()
    {
        using var connection = OpenLocalConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT format_version, revision FROM workspace WHERE singleton=1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new FormatException("No committed local SQLite Workspace revision exists.");
        if (reader.GetInt32(0) != 3) throw new UnsupportedWorkspaceVersion("SQLite Workspace", reader.GetInt32(0));
        return reader.GetString(1);
    }

    private static void CreateLocalNoteSchema(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS notes (
                scope TEXT NOT NULL CHECK (scope IN ('person','team')),
                id TEXT NOT NULL,
                owner_email TEXT NOT NULL COLLATE NOCASE REFERENCES people(email),
                metadata TEXT NOT NULL,
                markdown TEXT NOT NULL,
                PRIMARY KEY (scope, id)
            );
            CREATE TABLE IF NOT EXISTS team_context (
                email TEXT PRIMARY KEY COLLATE NOCASE REFERENCES people(email), revision TEXT NOT NULL
            );
            """;
        command.Parameters.Clear();
        command.ExecuteNonQuery();
    }

    private void SaveLocalSqliteNote(NoteScope scope, NoteMetadata metadata, string markdown)
    {
        if (Synchronization.Diverged) throw new IOException("Edits are paused for preserved divergent Workspace versions.");
        try { SaveLocalSqliteNoteCore(scope, metadata, markdown); }
        catch (Exception error) when (StorageError(error))
        {
            LocalStorageFailed(error);
            throw;
        }
    }

    private void SaveLocalSqliteNoteCore(NoteScope scope, NoteMetadata metadata, string markdown)
    {
        if (localSaveFault is not null)
            throw new IOException("Reconcile the original local transaction before retrying.");
        EnsureWorkspaceWritable();
        using var connection = OpenLocalConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT format_version, revision FROM workspace WHERE singleton=1;";
        int version;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new FormatException("No committed local Workspace exists.");
            version = reader.GetInt32(0);
            if (version != 3) throw new UnsupportedWorkspaceVersion("SQLite Workspace", version);
            if (reader.GetString(1) != revision)
                throw new IOException("Local Workspace changed. Reopen to reconcile the note before retrying.");
        }
        ValidateNoteDraft(scope, metadata.Id);
        ValidateNoteMetadata(metadata, scope, people, "Local note");
        using var evidence = BeginLocalWrite();
        command.CommandText = """
            INSERT INTO notes VALUES ($scope, $id, $owner, $metadata, $markdown)
            ON CONFLICT(scope, id) DO UPDATE SET metadata=excluded.metadata, markdown=excluded.markdown;
            """;
        command.Parameters.AddWithValue("$scope", metadata.Kind);
        command.Parameters.AddWithValue("$id", metadata.Id.ToString("D"));
        command.Parameters.AddWithValue("$owner", metadata.OwnerEmail);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, metadata.GetType(), JsonOptions));
        command.Parameters.AddWithValue("$markdown", markdown);
        command.ExecuteNonQuery();
        var next = AdvanceLocalRevision(command, 3, evidence.Intent.NextRevision);
        CommitLocalWrite(transaction, evidence);
        AcceptLocalRevision(next);
    }

    private bool SaveLocalSqlite(Dictionary<string, PersonProfile> proposed,
        Dictionary<string, AcceptedBatch> accepted, Dictionary<string, byte[]> proposedPhotos,
        Dictionary<string, PersonHistory> history, Dictionary<string, Guid> nextContexts,
        Dictionary<string, List<HistoricalTeamContext>> nextHistorical, string operation,
        IReadOnlyList<PhotoResource>? resources)
    {
        if (Synchronization.Diverged) return Fail("Edits are paused for preserved divergent Workspace versions.");
        try
        {
            if (localSaveFault is not null)
                return Fail("Reconcile the original local transaction before retrying. No replacement write was started.");
            using var connection = OpenLocalConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT format_version, revision FROM workspace WHERE singleton=1;";
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new FormatException("No committed local Workspace exists.");
                if (reader.GetInt32(0) != 3)
                    throw new UnsupportedWorkspaceVersion("SQLite Workspace", reader.GetInt32(0));
                if (reader.GetString(1) != revision)
                    throw new IOException("Local Workspace changed. Reopen to reconcile before retrying.");
            }
            ValidateGraph(proposed);
            foreach (var person in proposed.Values) ValidateProfileHistory(history[person.Email], person);
            foreach (var reference in RequiredPhotos(proposed, history, nextHistorical))
                if (!proposedPhotos.ContainsKey(reference))
                    throw new IOException("Required current or retained Profile photo is unavailable. Nothing saved.");
            using var evidence = BeginLocalWrite();
            CreateLocalNoteSchema(command);
            CreateLocalProfileSchema(command);
            foreach (var person in proposed.Values)
            {
                if (people.GetValueOrDefault(person.Email) == person &&
                    ReferenceEquals(profileHistory.GetValueOrDefault(person.Email), history[person.Email])) continue;
                command.CommandText = """
                    INSERT INTO people VALUES ($key, $value)
                    ON CONFLICT(email) DO UPDATE SET profile=excluded.profile;
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", person.Email);
                command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(person, JsonOptions));
                command.ExecuteNonQuery();
                command.CommandText = """
                    INSERT INTO profile_history VALUES ($key, $value)
                    ON CONFLICT(email) DO UPDATE SET history=excluded.history;
                    """;
                command.Parameters["$value"].Value = JsonSerializer.Serialize(history[person.Email], JsonOptions);
                command.ExecuteNonQuery();
            }
            foreach (var (id, batch) in accepted)
            {
                if (batches.ContainsKey(id)) continue;
                command.CommandText = "INSERT INTO accepted_batches VALUES ($key, $value);";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", id);
                command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(batch, JsonOptions));
                command.ExecuteNonQuery();
            }
            foreach (var (email, context) in nextContexts)
            {
                if (teamContextRevisions.GetValueOrDefault(email) == context) continue;
                command.CommandText = """
                    INSERT INTO team_context VALUES ($key, $value)
                    ON CONFLICT(email) DO UPDATE SET revision=excluded.revision;
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", email);
                command.Parameters.AddWithValue("$value", context.ToString("D"));
                command.ExecuteNonQuery();
            }
            foreach (var (email, captures) in nextHistorical)
            {
                if (ReferenceEquals(historicalTeamContexts.GetValueOrDefault(email), captures)) continue;
                command.CommandText = """
                    INSERT INTO historical_team_context VALUES ($key, $value)
                    ON CONFLICT(email) DO UPDATE SET contexts=excluded.contexts;
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", email);
                command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(captures, JsonOptions));
                command.ExecuteNonQuery();
            }
            foreach (var (reference, bytes) in proposedPhotos)
            {
                if (photos.ContainsKey(reference)) continue;
                ValidateLocalPhoto(reference, bytes);
                command.CommandText = "INSERT INTO photos VALUES ($key, $value);";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", reference);
                command.Parameters.AddWithValue("$value", bytes);
                command.ExecuteNonQuery();
            }
            if (resources is not null && !PhotoResourcesUnchanged(resources))
                throw new IOException("Required photo changed while saving. Nothing saved; retain the draft and choose the intended image again.");
            var next = AdvanceLocalRevision(command, 3, evidence.Intent.NextRevision);
            CommitLocalWrite(transaction, evidence);
            AcceptLocalRevision(next);
            teamContextRevisions = nextContexts;
            historicalTeamContexts = nextHistorical;
            failedSaves.Remove(operation);
            return true;
        }

        catch (Exception error) when (StorageError(error))
        {
            LocalStorageFailed(error);
            if (operation == "import") workspaceCurrent = false;
            return SaveFailed(operation, "Change NOT confirmed saved locally. Draft retained; reopen to reconcile before retrying. " + error.Message);
        }
    }

    private static void CreateLocalProfileSchema(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS historical_team_context (
                email TEXT PRIMARY KEY COLLATE NOCASE REFERENCES people(email), contexts TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS photos (reference TEXT PRIMARY KEY, content BLOB NOT NULL);
            """;
        command.Parameters.Clear();
        command.ExecuteNonQuery();
    }

    private static void ValidateLocalPhoto(string reference, byte[] bytes)
    {
        ValidateHistoryValue("photoPath", JsonSerializer.SerializeToElement(reference));
        var photo = DecodePhoto(bytes, "Local Workspace photo");
        if (reference != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() +
            (photo.ContentType == "image/png" ? ".png" : ".jpg"))
            throw new FormatException("Local photo content does not match its immutable resource reference.");
    }

    private static void RequireLocalSqliteLocation(string workspacePath)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        foreach (var variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } synchronized)
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(synchronized));
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Local SQLite Workspaces must be outside the synchronized folder.");
            }
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Local SQLite Workspaces require an ordinary local folder, not a folder link.");
    }
}
