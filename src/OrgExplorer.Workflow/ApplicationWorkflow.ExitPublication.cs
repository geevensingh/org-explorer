namespace OrgExplorer.Workflow;

public partial interface IApplicationWorkflow
{
    bool VerifyExitPublication();
}

public sealed partial class ApplicationWorkflow
{
    private sealed record ExitPublicationReceipt(string Workspace, Guid Identity, Guid Head,
        string Folder, string Name, string Hash);

    public bool VerifyExitPublication()
    {
        ExitPublicationReceipt? expected;
        WorkspaceSynchronization previous;
        WorkspaceCompatibility previousCompatibility;
        lock (synchronizationGate)
        lock (workspaceGate)
        {
            previous = Synchronization;
            previousCompatibility = Compatibility;
            if (!localSqlite) return CanSafelyClose;
            expected = CaptureExitPublicationReceipt();
        }
        var ready = false;
        if (expected is not null)
        {
            string? publicationError = null;
            try
            {
                using var lease = AcquireSnapshotFolder(expected.Folder);
                var delivered = ObserveStoragePhase("exit.publication-read",
                    () => ReadDeliveredSnapshot(Path.Combine(expected.Folder, expected.Name)));
                if (delivered.Data.WorkspaceId != expected.Identity || delivered.Data.Revision != expected.Head ||
                    delivered.Hash != expected.Hash)
                    throw new IOException("The latest complete publication no longer matches its known local receipt. Evidence is retained.");
            }
            catch (Exception error) when (StorageError(error)) { publicationError = error.Message; }
            // Release the provider lease before reacquiring local locks, matching synchronization's lock order.
            lock (synchronizationGate)
            lock (workspaceGate)
            {
                if (CaptureExitPublicationReceipt() == expected)
                    ready = publicationError is null
                        ? CanSafelyClose && !Synchronization.Diverged && localSaveFault is null
                        : SynchronizationFailed(publicationError);
            }
        }
        if (previous != Synchronization || previousCompatibility != Compatibility)
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return ready;
    }

    private ExitPublicationReceipt? CaptureExitPublicationReceipt()
    {
        if (disposed || !workspaceCurrent || localSaveFault is not null || !CanSafelyClose ||
            Synchronization.Diverged) return null;
        try { return CaptureCurrentExitPublicationReceipt(reloadChangedHead: true); }
        catch (Exception error) when (StorageError(error))
        {
            LocalStorageFailed(error);
            MarkCompatibilityFailure(error);
            Fail("Normal exit cannot verify committed local data. Last valid information is STALE: " + error.Message);
            return null;
        }
    }

    private ExitPublicationReceipt? CaptureCurrentExitPublicationReceipt(bool reloadChangedHead)
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
            if (!reloadChangedHead) return null;
            ObserveStoragePhase("exit.local-reload", () => OpenLocalSqlite(false));
            return CaptureCurrentExitPublicationReceipt(reloadChangedHead: false);
        }
        LoadLocalSynchronization(connection, identity, head);
        if (!ReconcileLocalWriteEvidence() || !CanSafelyClose) return null;
        var state = Synchronization;
        if (state.Diverged || state.Error is not null || joiningWorkspace ||
            state.LocalRevision != state.PublishedRevision || state.Folder is not { } folder) return null;
        var name = $"{identity:D}-{head:D}.oesnapshot";
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='snapshot_inventory';";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0) return null;
        command.CommandText = "SELECT revision,hash FROM snapshot_inventory WHERE folder=$folder AND name=$name;";
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$name", name);
        string hash;
        using (var receipt = command.ExecuteReader())
        {
            if (!receipt.Read() || receipt.GetString(0) != head.ToString("D")) return null;
            hash = receipt.GetString(1);
        }
        return new(workspace!, identity, head, folder, name, hash);
    }
}

public sealed partial class WorkspaceViewModel
{
    public bool VerifyExitPublication() => Observe(workflow.VerifyExitPublication);
}
