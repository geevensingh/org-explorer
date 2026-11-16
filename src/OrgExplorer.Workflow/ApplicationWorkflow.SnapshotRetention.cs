namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private static FileStream AcquireSnapshotFolder(string folder)
    {
        RejectSnapshotLinks(folder);
        var path = Path.Combine(folder, ".orgexplorer-snapshot-lease");
        RejectSnapshotLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void ReclaimRoutineSnapshots(string path, string folder, LogicalSnapshot published)
    {
        var inventory = Directory.GetFiles(folder, "*.oesnapshot").Select(file => (File: file, Item: ReadDeliveredSnapshot(file))).ToArray();
        foreach (var (_, item) in inventory)
        {
            using var validated = ValidateSnapshot(path, item.Data);
            if (item.Data.WorkspaceId != published.WorkspaceId)
                throw new IOException("A different Workspace arrived during publication. Snapshot cleanup was refused.");
            if (!Continues(item.Data.Ancestry, published.Ancestry, published.ResolvedRevisions) &&
                !Continues(published.Ancestry, item.Data.Ancestry, item.Data.ResolvedRevisions))
            {
                PreserveDivergence(path, item);
                throw new IOException("A divergent version arrived during publication. Complete versions are retained; cleanup was refused.");
            }
        }
        var owned = inventory.Where(entry => Path.GetFileName(entry.File) ==
            $"{entry.Item.Data.WorkspaceId:D}-{entry.Item.Data.Revision:D}.oesnapshot").ToArray();
        var retained = new HashSet<Guid>();
        var recovery = Path.Combine(path, "snapshot-conflicts");
        RejectSnapshotLinks(recovery);
        if (Directory.Exists(recovery))
            foreach (var file in Directory.GetFiles(recovery, "*.oesnapshot"))
            {
                var item = ReadDeliveredSnapshot(file);
                using var validated = ValidateSnapshot(path, item.Data);
                retained.Add(item.Data.Revision);
            }
        var keep = owned.Where(entry => entry.Item.Data.Revision == published.Revision).Select(entry => entry.Item.Data.Revision).ToHashSet();
        var previous = owned.Where(entry => entry.Item.Data.Revision != published.Revision &&
                IsPrefix(entry.Item.Data.Ancestry, published.Ancestry))
            .OrderByDescending(entry => entry.Item.Data.Ancestry.Length).FirstOrDefault();
        if (previous.Item is not null) keep.Add(previous.Item.Data.Revision);

        lock (workspaceGate)
        {
            if (workspace != path) throw new IOException("Workspace changed before snapshot cleanup. Nothing was reclaimed.");
            using var connection = OpenLocalConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS snapshot_inventory (
                    folder TEXT NOT NULL, name TEXT NOT NULL, revision TEXT NOT NULL, hash TEXT NOT NULL,
                    PRIMARY KEY(folder,name)
                );
                """;
            command.ExecuteNonQuery();
            foreach (var (file, item) in owned)
            {
                command.Parameters.Clear();
                command.CommandText = "SELECT hash FROM snapshot_inventory WHERE folder=$folder AND name=$name;";
                command.Parameters.AddWithValue("$folder", folder);
                command.Parameters.AddWithValue("$name", Path.GetFileName(file));
                if (command.ExecuteScalar() is string known && known != item.Hash)
                    throw new IOException("Previously inventoried snapshot bytes changed. Unknown/provider bytes were not reclaimed.");
                command.CommandText = """
                    INSERT OR IGNORE INTO snapshot_inventory(folder,name,revision,hash) VALUES($folder,$name,$revision,$hash);
                    """;
                command.Parameters.AddWithValue("$revision", item.Data.Revision.ToString("D"));
                command.Parameters.AddWithValue("$hash", item.Hash);
                command.ExecuteNonQuery();
            }
            foreach (var (file, item) in owned)
            {
                if (keep.Contains(item.Data.Revision) || retained.Contains(item.Data.Revision) ||
                    !Continues(item.Data.Ancestry, published.Ancestry, published.ResolvedRevisions)) continue;
                RetireInventoriedSnapshot(file, item.Hash);
                command.Parameters.Clear();
                command.CommandText = "DELETE FROM snapshot_inventory WHERE folder=$folder AND name=$name;";
                command.Parameters.AddWithValue("$folder", folder);
                command.Parameters.AddWithValue("$name", Path.GetFileName(file));
                command.ExecuteNonQuery();
            }
        }
    }

    private void RetireInventoriedSnapshot(string file, string expectedHash)
    {
        RejectSnapshotLinks(file);
        if (ReadDeliveredSnapshot(file).Hash != expectedHash)
            throw new IOException("Snapshot bytes changed before reclamation. Nothing was deleted.");
        // Move only an inventoried exact artifact, then verify the bytes actually displaced.
        // A noncooperating provider's replacement is retained, never deleted as our old version.
        var retired = file + "." + Guid.NewGuid().ToString("N") + ".retiring";
        File.Move(file, retired);
        try
        {
            if (ReadDeliveredSnapshot(retired).Hash != expectedHash)
                throw new IOException("Provider bytes changed during reclamation. Displaced bytes are preserved.");
            DeleteWorkspaceFile(retired);
        }
        catch
        {
            if (File.Exists(retired) && !File.Exists(file)) File.Move(retired, file);
            throw;
        }
    }
}
