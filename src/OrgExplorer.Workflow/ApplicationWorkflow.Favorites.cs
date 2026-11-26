using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace OrgExplorer.Workflow;

public partial interface IApplicationWorkflow
{
    IReadOnlyList<PersonProfile> FavoritePeople { get; }
    bool EditFavorite(string email, bool isFavorite);
    bool ReorderFavorite(string email, string? beforeEmail);
}

public sealed partial class ApplicationWorkflow
{
    private IReadOnlyList<string> favoriteOrder = [];

    public IReadOnlyList<PersonProfile> FavoritePeople
    { get { if (queryView is { } view) return view.FavoritePeople;
        lock (workspaceGate) return favoriteOrder.Select(email => people[email]).ToArray(); } }

    private static IReadOnlyList<string> InitialFavoriteOrder(IEnumerable<PersonProfile> population) =>
        Array.AsReadOnly(population.Where(person => person.IsFavorite).OrderBy(person => person.Heading)
            .Select(person => person.Email).ToArray());

    private IReadOnlyList<string> NextFavoriteOrder(Dictionary<string, PersonProfile> proposed,
        IEnumerable<PersonProfile>? additions = null)
    {
        var retained = favoriteOrder.Where(email => proposed.TryGetValue(email, out var person) && person.IsFavorite).ToArray();
        var members = retained.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly(retained.Concat((additions ?? proposed.Values).Where(person =>
            person.IsFavorite && !members.Contains(person.Email)).OrderBy(person => person.Heading)
            .Select(person => person.Email)).ToArray());
    }

    private static void ValidateFavoriteOrder(IReadOnlyList<string> order, Dictionary<string, PersonProfile> population)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in order)
            if (string.IsNullOrWhiteSpace(email) || !identities.Add(email) ||
                !population.TryGetValue(email, out var person) || !person.IsFavorite)
                throw new FormatException("Invalid Favorites order: duplicate, unknown or non-Favorite identity.");
        if (identities.Count != population.Values.Count(person => person.IsFavorite))
            throw new FormatException("Invalid Favorites order: every Favorite must appear exactly once.");
    }

    private static IReadOnlyList<string>? ReadLocalFavoriteOrder(SqliteCommand command,
        Dictionary<string, PersonProfile> population)
    {
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='favorite_order';";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0) return null;
        command.CommandText = "SELECT singleton, format_version, sequence FROM favorite_order;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 1)
            throw new FormatException("Initialized Favorites order is missing.");
        if (reader.GetInt32(1) != 1)
            throw new UnsupportedWorkspaceVersion("Favorites order", reader.GetInt32(1));
        var order = JsonSerializer.Deserialize<string[]>(reader.GetString(2), JsonOptions)
            ?? throw new FormatException("Initialized Favorites order is missing.");
        if (reader.Read()) throw new FormatException("Duplicate Favorites ordering state.");
        ValidateFavoriteOrder(order, population);
        return Array.AsReadOnly(order);
    }

    private static void WriteLocalFavoriteOrder(SqliteCommand command, IReadOnlyList<string> order)
    {
        command.Parameters.Clear();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS favorite_order (
                singleton INTEGER PRIMARY KEY CHECK (singleton=1),
                format_version INTEGER NOT NULL,
                sequence TEXT NOT NULL
            );
            INSERT INTO favorite_order VALUES(1, 1, $sequence)
            ON CONFLICT(singleton) DO UPDATE SET sequence=excluded.sequence;
            """;
        command.Parameters.AddWithValue("$sequence", JsonSerializer.Serialize(order, JsonOptions));
        command.ExecuteNonQuery();
    }

    private void ValidateStoredFavoriteOrder(SqliteCommand command)
    {
        try
        {
            ReadLocalSnapshotFormat(command);
            var stored = ReadLocalFavoriteOrder(command, people)
                ?? throw new FormatException("Saved Favorites order is missing. Reopen to reconcile; nothing saved.");
            if (!stored.SequenceEqual(favoriteOrder, StringComparer.OrdinalIgnoreCase))
                throw new IOException("Local Favorites order changed. Reopen to reconcile before retrying.");
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            MarkCompatibilityFailure(error);
            throw;
        }
    }

    private string InitializeLocalFavoriteOrder(IReadOnlyList<string> order, string expectedRevision,
        Dictionary<string, PersonProfile>? initializedPopulation = null)
    {
        try
        {
            using var connection = OpenLocalConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT format_version, revision FROM workspace WHERE singleton=1;";
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetInt32(0) != 3 || reader.GetString(1) != expectedRevision)
                    throw new IOException("Workspace changed before Favorites initialization. Reopen to reconcile; nothing saved.");
            }
            if (initializedPopulation is not null)
            {
                var stored = ReadLocalFavoriteOrder(command, initializedPopulation);
                if (stored is null || !stored.SequenceEqual(order, StringComparer.OrdinalIgnoreCase))
                    throw new IOException("Saved Favorites changed before snapshot initialization. Reopen to reconcile; nothing saved.");
            }
            revision = expectedRevision;
            using var evidence = BeginLocalWrite();
            WriteLocalFavoriteOrder(command, order);
            var next = AdvanceLocalRevision(command, 3, evidence.Intent.NextRevision);
            CommitLocalWrite(transaction, evidence);
            AcceptLocalRevision(next);
            return next;
        }
        catch (Exception error) when (StorageError(error))
        {
            LocalStorageFailed(error);
            throw;
        }
    }

    public bool EditFavorite(string email, bool isFavorite)
        => CanBeginLocalSave() && TrackLocalSave(() => EditFavoriteCore(email, isFavorite));

    // A null destination appends; otherwise insert before that identity after removing the mover.
    public bool ReorderFavorite(string email, string? beforeEmail)
        => CanBeginLocalSave() && TrackLocalSave(() =>
        {
            lock (workspaceGate)
            {
                if (workspace is null || !workspaceCurrent)
                    return Fail("Open a current Workspace before rearranging Favorites.");
                if (!people.TryGetValue(email, out var moving) || !moving.IsFavorite ||
                    beforeEmail is not null && (!people.TryGetValue(beforeEmail, out var destination) || !destination.IsFavorite))
                    return Fail("Favorite move rejected: both identities must be current Favorites. Nothing saved.");
                if (string.Equals(email, beforeEmail, StringComparison.OrdinalIgnoreCase))
                {
                    Status = "Favorite arrangement unchanged; nothing saved.";
                    return true;
                }
                var next = favoriteOrder.Where(identity => !identity.Equals(email, StringComparison.OrdinalIgnoreCase)).ToList();
                var index = beforeEmail is null ? next.Count :
                    next.FindIndex(identity => identity.Equals(beforeEmail, StringComparison.OrdinalIgnoreCase));
                next.Insert(index, moving.Email);
                if (favoriteOrder.SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
                {
                    Status = "Favorite arrangement unchanged; nothing saved.";
                    return true;
                }
                if (!Save(people, batches, operation: "favorites-order", orderedFavorites: next.AsReadOnly())) return false;
                InvalidateProjections();
                pending = null;
                Status = "Favorite arrangement saved locally.";
                return true;
            }
        });

    private bool EditFavoriteCore(string email, bool isFavorite)
    {
        lock (workspaceGate)
        {
            if (workspace is null || !people.TryGetValue(email, out var person)) return Fail("Open a Person first.");
            if (!workspaceCurrent) return Fail("Workspace unavailable; retained information is STALE. No changes saved.");
            if (person.IsFavorite == isFavorite)
            {
                Status = "Favorite membership unchanged; no new arrangement or Profile change saved.";
                return true;
            }
            return SavePersonRelationship(person, person with { IsFavorite = isFavorite }, "isFavorite",
                isFavorite ? "Favorite marked and saved locally." : "Favorite unmarked and saved locally.");
        }
    }
}
