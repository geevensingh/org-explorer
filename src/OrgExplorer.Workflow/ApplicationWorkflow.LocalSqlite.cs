using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private const string SqliteDatabase = "workspace.sqlite";
    private const string SqliteDiscriminator = "local-workspace.format";
    private const string SqliteFormat = "OrgExplorer local SQLite Workspace";
    private const string SqliteEditingUnavailable =
        "Ordinary edits are not implemented for this staged SQLite Workspace. Initial People-only Import and browsing are available.";
    private bool localSqlite;

    private SqliteConnection OpenLocalConnection(bool create = false)
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
            command.CommandText = "PRAGMA synchronous=EXTRA; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void OpenLocalSqlite(bool create)
    {
        RequireLocalSqliteLocation(workspace!);
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
                INSERT INTO workspace VALUES (1, 1, $identity, $revision);
                CREATE TABLE people (email TEXT PRIMARY KEY COLLATE NOCASE, profile TEXT NOT NULL);
                CREATE TABLE profile_history (
                    email TEXT PRIMARY KEY COLLATE NOCASE REFERENCES people(email), history TEXT NOT NULL
                );
                CREATE TABLE accepted_batches (id TEXT PRIMARY KEY, batch TEXT NOT NULL);
                """;
            command.Parameters.AddWithValue("$identity", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("D"));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        if (!File.Exists(Path.Combine(workspace!, SqliteDiscriminator)) ||
            File.ReadAllText(Path.Combine(workspace!, SqliteDiscriminator)) != SqliteFormat)
            throw new FormatException("Incompatible local SQLite Workspace discriminator. No migration or fallback is performed.");
        if (File.Exists(Path.Combine(workspace!, "workspace.json")))
            throw new FormatException("A Workspace cannot contain both SQLite and legacy persistence authorities.");
        var loaded = new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, PersonHistory>(StringComparer.OrdinalIgnoreCase);
        var accepted = new Dictionary<string, AcceptedBatch>();
        string committedRevision;
        using (var connection = OpenLocalConnection())
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
            var version = reader.GetInt32(0);
            if (version != 1) throw new UnsupportedWorkspaceVersion("SQLite Workspace", version);
            if (!Guid.TryParseExact(reader.GetString(1), "D", out var identity) || identity == Guid.Empty ||
                !Guid.TryParseExact(reader.GetString(2), "D", out var commit) || commit == Guid.Empty)
                throw new FormatException("Invalid local SQLite Workspace identity or revision.");
            committedRevision = commit.ToString("D");
            reader.Close();
            command.CommandText = "SELECT email, profile FROM people;";
            using (var profiles = command.ExecuteReader())
                while (profiles.Read())
                {
                    if (JsonNode.Parse(profiles.GetString(1)) is not JsonObject json)
                        throw new FormatException("Invalid local Person record.");
                    json.Add("schemaVersion", 2);
                    var person = ReadPersonRecord(JsonSerializer.SerializeToElement(json), 2);
                    if (!profiles.GetString(0).Equals(person.Email, StringComparison.OrdinalIgnoreCase) ||
                        !loaded.TryAdd(person.Email, person) || person.PhotoPath is not null || person.TeamName is not null)
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
                foreach (var evidence in item.Observations)
                    if (evidence.BatchId is not { } batchId || !accepted.ContainsKey(batchId.ToString("D")))
                        throw new FormatException("Local Profile evidence requires its accepted Import.");
        }
        people = loaded;
        batches = accepted;
        profileHistory = history;
        revision = committedRevision;
        historicalTeamContexts = new(StringComparer.OrdinalIgnoreCase);
        personNotes = [];
        teamNotes = [];
        teamNoteConfirmations = [];
        teamContextRevisions = new(StringComparer.OrdinalIgnoreCase);
        photos = [];
        InvalidateProjections();
        workspaceCurrent = true;
        compatibility = new(WorkspaceCompatibilityState.Compatible,
            "Local SQLite Workspace (format 1). Initial People-only Import and browsing are available. " +
            "Other edits and synchronization are not implemented on this staged path. Settings remain available.",
            workspace, 1) { SupportsOrdinaryEdits = false, SupportedWorkspaceVersion = 1 };
    }

    private string LocalSqliteRevision()
    {
        using var connection = OpenLocalConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT format_version, revision FROM workspace WHERE singleton=1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new FormatException("No committed local SQLite Workspace revision exists.");
        if (reader.GetInt32(0) != 1) throw new UnsupportedWorkspaceVersion("SQLite Workspace", reader.GetInt32(0));
        return reader.GetString(1);
    }

    private bool SaveLocalSqliteImport(Dictionary<string, PersonProfile> proposed,
        Dictionary<string, AcceptedBatch> accepted, Dictionary<string, PersonHistory> history, string operation)
    {
        if (operation != "import") return Fail(SqliteEditingUnavailable);
        try
        {
            using var connection = OpenLocalConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT revision FROM workspace WHERE singleton=1;";
            if (command.ExecuteScalar() as string != revision)
                throw new IOException("Local Workspace changed. Reopen and review the Import again.");
            foreach (var person in proposed.Values)
            {
                command.CommandText = "INSERT INTO people VALUES ($key, $value);";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", person.Email);
                command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(person, JsonOptions));
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO profile_history VALUES ($key, $value);";
                command.Parameters["$value"].Value = JsonSerializer.Serialize(history[person.Email], JsonOptions);
                command.ExecuteNonQuery();
            }
            foreach (var (id, batch) in accepted)
            {
                command.CommandText = "INSERT INTO accepted_batches VALUES ($key, $value);";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", id);
                command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(batch, JsonOptions));
                command.ExecuteNonQuery();
            }
            var next = Guid.NewGuid().ToString("D");
            command.CommandText = "UPDATE workspace SET revision=$revision WHERE singleton=1;";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$revision", next);
            command.ExecuteNonQuery();
            transaction.Commit();
            revision = next;
            failedSaves.Remove(operation);
            return true;
        }
        catch (Exception error) when (StorageError(error))
        {
            workspaceCurrent = false;
            return SaveFailed(operation, "Initial Import NOT confirmed saved locally. Reopen to reconcile the transaction before reviewing again. " + error.Message);
        }
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
