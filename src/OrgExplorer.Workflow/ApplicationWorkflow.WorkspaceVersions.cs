using System.Security.Cryptography;
using System.Text;

namespace OrgExplorer.Workflow;

public sealed record WorkspaceVersion(Guid Revision, string ContentHash, bool IsLocal, bool Retained,
    int People, int PersonNotes, int TeamNotes)
{
    public string Description => $"{Revision:D} - {(IsLocal ? "current local; " : "")}" +
        $"{(Retained ? "retained recovery; " : "")}{People} People, {PersonNotes} Person notes, {TeamNotes} Team notes";
}

public sealed record WorkspaceVersionReview(string Id, Guid WorkspaceId, Guid LocalRevision,
    IReadOnlyList<WorkspaceVersion> Versions);

public partial interface IApplicationWorkflow
{
    WorkspaceVersionReview? GetWorkspaceVersionReview();
    bool ChooseWorkspaceVersion(string reviewId, Guid revision);
    bool DeleteRetainedWorkspaceVersion(string reviewId, Guid revision);
}

public sealed partial class ApplicationWorkflow
{
    private sealed record VersionReview(WorkspaceVersionReview Public, DeliveredSnapshot[] Versions, bool HasDivergence);

    public WorkspaceVersionReview? GetWorkspaceVersionReview()
    {
        lock (synchronizationGate)
        {
            try
            {
                using var folderLease = AcquireSnapshotFolder(Synchronization.Folder ??
                    throw new IOException("Configure a synchronization folder before reviewing versions."));
                return ReadVersionReview().Public;
            }
            catch (Exception error) when (StorageError(error))
            {
                SynchronizationFailed(error.Message);
                return null;
            }
        }
    }

    private VersionReview ReadVersionReview()
    {
        string path, folder;
        DeliveredSnapshot local;
        lock (workspaceGate)
        {
            if (!localSqlite || !workspaceCurrent || Synchronization.Folder is not { } configured)
                throw new IOException("Open a local Workspace with a synchronization folder before reviewing versions.");
            path = workspace!;
            folder = configured;
            OpenLocalSqlite(false);
            local = EncodeSnapshot(CaptureLocalSnapshot());
        }
        RejectSnapshotLinks(folder);
        if (!Directory.Exists(folder)) throw new IOException("The synchronization folder is unavailable. Retained versions are unchanged.");
        var delivered = Directory.GetFiles(folder, "*.oesnapshot").Order(StringComparer.Ordinal)
            .Select(ReadDeliveredSnapshot).ToArray();
        foreach (var item in delivered)
        {
            using var validated = ValidateSnapshot(path, item.Data);
            if (item.Data.WorkspaceId != local.Data.WorkspaceId)
                throw new FormatException("A different Workspace identity arrived. No selection is authorized.");
        }
        var byRevision = new Dictionary<Guid, DeliveredSnapshot>();
        foreach (var item in delivered.Append(local))
        {
            if (byRevision.TryGetValue(item.Data.Revision, out var existing) && existing.Hash != item.Hash)
                throw new FormatException("One immutable Workspace revision has conflicting content. No selection is authorized.");
            byRevision[item.Data.Revision] = item;
        }
        var divergent = delivered.Where(item =>
                     !Continues(local.Data.Ancestry, item.Data.Ancestry, item.Data.ResolvedRevisions) &&
                     !Continues(item.Data.Ancestry, local.Data.Ancestry, local.Data.ResolvedRevisions)).ToArray();
        foreach (var item in divergent)
            PreserveDivergence(path, item);

        var retained = new HashSet<Guid>();
        var directory = Path.Combine(path, "snapshot-conflicts");
        RejectSnapshotLinks(directory);
        if (Directory.Exists(directory))
            foreach (var file in Directory.GetFiles(directory, "*.oesnapshot").Order(StringComparer.Ordinal))
            {
                var item = ReadDeliveredSnapshot(file);
                if (Path.GetFileName(file) != $"{item.Data.Revision:D}.oesnapshot")
                    continue;
                using var validated = ValidateSnapshot(path, item.Data);
                if (item.Data.WorkspaceId != local.Data.WorkspaceId)
                    throw new FormatException("Retained recovery belongs to a different Workspace. Nothing was replaced.");
                if (byRevision.TryGetValue(item.Data.Revision, out var existing) && existing.Hash != item.Hash)
                    throw new FormatException("Retained immutable Workspace evidence has changed. Nothing was replaced.");
                byRevision[item.Data.Revision] = item;
                retained.Add(item.Data.Revision);
            }
        // The authorization includes even stale deliveries, not just the selectable heads.
        var inventory = string.Join("\n", byRevision.Values.OrderBy(item => item.Data.Revision)
            .Select(item => $"{item.Data.Revision:D}:{item.Hash}:{retained.Contains(item.Data.Revision)}"));
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{path}\n{folder}\n{local.Data.Revision:D}\n{inventory}")));
        var choices = byRevision.Values.Where(item => retained.Contains(item.Data.Revision) ||
            item.Data.Revision == local.Data.Revision || !byRevision.Values.Any(other =>
                item.Data.Revision != other.Data.Revision &&
                Continues(item.Data.Ancestry, other.Data.Ancestry, other.Data.ResolvedRevisions)))
            .OrderBy(item => item.Data.Revision).ToArray();
        return new(new(token, local.Data.WorkspaceId, local.Data.Revision,
            Array.AsReadOnly(choices.Select(item => new WorkspaceVersion(item.Data.Revision, item.Hash,
                item.Data.Revision == local.Data.Revision, retained.Contains(item.Data.Revision),
                item.Data.People.Length, item.Data.Notes.Count(note => note.Scope == "person"),
                item.Data.Notes.Count(note => note.Scope == "team"))).ToArray())), byRevision.Values.ToArray(),
            divergent.Length != 0);
    }

    public bool ChooseWorkspaceVersion(string reviewId, Guid revision)
    {
        bool result;
        lock (synchronizationGate)
        {
            try
            {
                using var folderLease = AcquireSnapshotFolder(Synchronization.Folder ??
                    throw new IOException("Configure a synchronization folder before choosing a version."));
                var review = ReadVersionReview();
                if (review.Public.Id != reviewId || !review.Public.Versions.Any(item => item.Revision == revision))
                    throw new IOException("Workspace versions changed after the decision was shown. Review the current complete versions before choosing.");
                lock (workspaceGate)
                {
                    if (Synchronization.LocalRevision != review.Public.LocalRevision)
                        throw new IOException("A new local commit arrived. Review the current versions before choosing.");
                    if (LocalSave.EditsPaused || !CanSafelyClose || pending is not null ||
                        Volatile.Read(ref snapshotAdoptionDeferrals) != 0)
                        throw new IOException("Retained writing or an unresolved local save must be reconciled before choosing a whole Workspace.");
                    var selected = review.Versions.Single(item => item.Data.Revision == revision);
                    // This is a new transaction selecting one complete state, never a field merge.
                    // Exact acknowledged branch revisions stop known rejected exports recurring as conflicts.
                    var next = Guid.NewGuid();
                    var resolved = review.Versions.SelectMany(item =>
                        item.Data.Ancestry.Concat(item.Data.ResolvedRevisions ?? [])).Distinct().Order().ToArray();
                    var data = selected.Data with
                    {
                        Revision = next, Ancestry = [.. selected.Data.Ancestry, next], ResolvedRevisions = resolved
                    };
                    using var validated = ValidateSnapshot(workspace!, data);
                    PreserveDivergence(workspace!, selected);
                    AdoptSnapshot(validated, EncodeSnapshot(data), explicitChoice: true);
                    if (ReadVersionReview().HasDivergence)
                        result = SynchronizationFailed("The selected complete state committed, but new divergent versions arrived during the decision. " +
                            "All versions are retained and editing remains paused. Review again before choosing.");
                    else
                    {
                        using var connection = OpenLocalConnection();
                        using var transaction = connection.BeginTransaction();
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = """
                            UPDATE synchronization SET divergence=NULL WHERE singleton=1
                                AND EXISTS(SELECT 1 FROM workspace WHERE revision=$revision);
                            """;
                        command.Parameters.AddWithValue("$revision", next.ToString("D"));
                        if (command.ExecuteNonQuery() != 1)
                            throw new IOException("The local revision changed before selection could be finalized. Editing remains paused.");
                        transaction.Commit();
                        synchronization = Synchronization with { Diverged = false, Error = null };
                        compatibility = Compatibility with { SupportsOrdinaryEdits = true, SupportsNoteEdits = true };
                        Status = "Complete Workspace version selected. Other versions remain retained for recovery; publish the new continuation with Sync now.";
                        result = true;
                    }
                }
            }
            catch (Exception error) when (StorageError(error)) { result = SynchronizationFailed(error.Message); }
        }
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public bool DeleteRetainedWorkspaceVersion(string reviewId, Guid revision)
    {
        lock (synchronizationGate)
        {
            try
            {
                using var folderLease = AcquireSnapshotFolder(Synchronization.Folder ??
                    throw new IOException("Configure a synchronization folder before deleting retained recovery."));
                var review = ReadVersionReview();
                if (review.Public.Id != reviewId)
                    throw new IOException("Workspace versions changed. Review again before explicitly deleting recovery.");
                var selected = review.Public.Versions.SingleOrDefault(item => item.Revision == revision);
                if (Synchronization.Diverged || selected is not { Retained: true, IsLocal: false })
                    throw new IOException("Resolve divergence first. Only an unneeded retained recovery copy, never the current local version, can be deleted.");
                lock (workspaceGate)
                {
                    if (LocalSqliteRevision() != review.Public.LocalRevision.ToString("D") ||
                        LocalSave.EditsPaused || !CanSafelyClose || pending is not null ||
                        Volatile.Read(ref snapshotAdoptionDeferrals) != 0)
                        throw new IOException("Current local writing or recovery still requires protection. Nothing was deleted.");
                    RetireInventoriedSnapshot(Path.Combine(workspace!, "snapshot-conflicts", $"{revision:D}.oesnapshot"),
                        selected.ContentHash);
                }
                synchronization = Synchronization with { Error = null };
                Status = "The selected retained recovery copy was explicitly deleted. Routine current/previous snapshots and provider copies are unchanged.";
                return true;
            }
            catch (Exception error) when (StorageError(error)) { return SynchronizationFailed(error.Message); }
        }
    }
}

public sealed partial class WorkspaceViewModel
{
    public WorkspaceVersionReview? GetWorkspaceVersionReview() => Observe(workflow.GetWorkspaceVersionReview);
    public bool ChooseWorkspaceVersion(string reviewId, Guid revision)
    {
        using var unresolvedAdoption = !CanSafelyClose ? workflow.DeferSnapshotAdoption() : null;
        return Observe(() => workflow.ChooseWorkspaceVersion(reviewId, revision));
    }
    public bool DeleteRetainedWorkspaceVersion(string reviewId, Guid revision)
    {
        using var unresolvedAdoption = !CanSafelyClose ? workflow.DeferSnapshotAdoption() : null;
        return Observe(() => workflow.DeleteRetainedWorkspaceVersion(reviewId, revision));
    }
}
