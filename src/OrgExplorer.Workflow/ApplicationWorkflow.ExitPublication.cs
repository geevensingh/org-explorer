namespace OrgExplorer.Workflow;

public partial interface IApplicationWorkflow
{
    bool VerifyExitPublication();
}

public sealed partial class ApplicationWorkflow
{
    public bool VerifyExitPublication()
    {
        bool ready;
        WorkspaceSynchronization previous;
        WorkspaceCompatibility previousCompatibility;
        lock (synchronizationGate)
        lock (workspaceGate)
        {
            previous = Synchronization;
            previousCompatibility = Compatibility;
            if (!localSqlite) return CanSafelyClose;
            if (disposed || !workspaceCurrent || localSaveFault is not null || !CanSafelyClose ||
                Synchronization.Diverged) return false;
            try { ready = VerifyCurrentExitPublication(reloadChangedHead: true); }
            catch (Exception error) when (StorageError(error))
            {
                LocalStorageFailed(error);
                MarkCompatibilityFailure(error);
                ready = Fail("Normal exit cannot verify committed local data. Last valid information is STALE: " + error.Message);
            }
        }
        if (previous != Synchronization || previousCompatibility != Compatibility)
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return ready;
    }

    private bool VerifyCurrentExitPublication(bool reloadChangedHead)
    {
        RequireLocalSqliteLocation(workspace!);
        if (File.Exists(Path.Combine(workspace!, "workspace.json")) ||
            File.ReadAllText(Path.Combine(workspace!, SqliteDiscriminator)) != SqliteFormat)
            throw new UnsupportedWorkspaceFormat("The authoritative local SQLite Workspace discriminator changed.");
        using var connection = OpenLocalConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA application_id;";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0x4f455850)
            throw new FormatException("Incompatible local SQLite database.");
        command.CommandText = "SELECT format_version,identity,revision FROM workspace WHERE singleton=1;";
        Guid identity, head;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new FormatException("No committed local SQLite Workspace exists.");
            if (reader.GetInt32(0) != 3) throw new UnsupportedWorkspaceVersion("SQLite Workspace", reader.GetInt32(0));
            identity = ParseSnapshotId(reader.GetString(1));
            head = ParseSnapshotId(reader.GetString(2));
        }
        if (Synchronization.WorkspaceId != identity || revision != head.ToString("D"))
        {
            transaction.Dispose();
            connection.Dispose();
            if (!reloadChangedHead) return false;
            ObserveStoragePhase("exit.local-reload", () => OpenLocalSqlite(false));
            return VerifyCurrentExitPublication(reloadChangedHead: false);
        }
        LoadLocalSynchronization(connection, identity, head);
        if (!ReconcileLocalWriteEvidence() || !CanSafelyClose) return false;
        var state = Synchronization;
        if (state.Diverged || state.Error is not null || joiningWorkspace ||
            state.LocalRevision != state.PublishedRevision || state.Folder is not { } folder) return false;
        var name = $"{identity:D}-{head:D}.oesnapshot";
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='snapshot_inventory';";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0) return false;
        command.CommandText = "SELECT revision,hash FROM snapshot_inventory WHERE folder=$folder AND name=$name;";
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$name", name);
        string hash;
        using (var receipt = command.ExecuteReader())
        {
            if (!receipt.Read() || receipt.GetString(0) != head.ToString("D")) return false;
            hash = receipt.GetString(1);
        }
        try
        {
            // Hold the local read transaction and the shared folder lease through exact receipt verification.
            // No incoming adoption, candidate database, export, reclamation or version authorization is needed.
            using var lease = AcquireSnapshotFolder(folder);
            var delivered = ObserveStoragePhase("exit.publication-read", () => ReadDeliveredSnapshot(Path.Combine(folder, name)));
            if (delivered.Data.WorkspaceId != identity || delivered.Data.Revision != head || delivered.Hash != hash)
                throw new IOException("The latest complete publication no longer matches its known local receipt. Evidence is retained.");
            return CanSafelyClose && !Synchronization.Diverged && localSaveFault is null;
        }
        catch (Exception error) when (StorageError(error)) { return SynchronizationFailed(error.Message); }
    }
}

public sealed partial class WorkspaceViewModel
{
    public bool VerifyExitPublication() => Observe(workflow.VerifyExitPublication);
}
