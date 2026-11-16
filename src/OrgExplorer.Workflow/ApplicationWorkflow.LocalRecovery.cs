using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private sealed record LocalWriteIntent(int Format, Guid Id, string BaseRevision, string NextRevision);
    private sealed class LocalWriteEvidence(string path, LocalWriteIntent intent, FileStream lease) : IDisposable
    {
        public string Path { get; } = path;
        public LocalWriteIntent Intent { get; } = intent;
        public void Dispose() => lease.Dispose();
    }

    private LocalWriteEvidence BeginLocalWrite(string? nextRevision = null)
    {
        if (!ReconcileLocalWriteEvidence())
            throw new IOException("An original local transaction remains unresolved. No replacement write was started.");
        var next = nextRevision ?? Guid.NewGuid().ToString("D");
        if (next == revision) throw new FormatException("A local transaction requires a distinct next revision.");
        var intent = new LocalWriteIntent(1, Guid.NewGuid(), revision!, next);
        var path = Path.Combine(workspace!, $"local-write-{intent.Id:N}.json");
        var lease = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, JsonOptions);
            lease.Write(bytes);
            fileObserver?.Observe(new(WorkspaceFileEffectKind.Write, path, bytes.Length));
            lease.Flush(true);
            fileObserver?.Observe(new(WorkspaceFileEffectKind.Flush, path));
            return new(path, intent, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    private void CommitLocalWrite(SqliteTransaction transaction, LocalWriteEvidence evidence)
    {
        try { ObserveStoragePhase("local-sqlite-commit", transaction.Commit); }
        catch (Exception error) when (StorageError(error))
        {
            SetUnknownLocalOutcome(error);
            string actual;
            try
            {
                transaction.Dispose();
                actual = LocalSqliteRevision();
            }
            catch (Exception reconciliationError) when (StorageError(reconciliationError))
            {
                SetUnknownLocalOutcome(reconciliationError);
                throw;
            }
            if (actual != evidence.Intent.NextRevision)
            {
                if (actual == evidence.Intent.BaseRevision)
                {
                    localSaveFault = null;
                    LocalStorageFailed(error);
                }
                throw;
            }
        }
        revision = evidence.Intent.NextRevision;
        evidence.Dispose();
        try
        {
            DeleteWorkspaceFile(evidence.Path);
            localSaveFault = null;
        }
        catch (Exception error) when (StorageError(error))
        {
            localSaveFault = new(LocalSavePhase.Failed, LocalSaveOutcome.Committed,
                CommittedRevision: revision, Error:
                "The local revision committed, but transaction-evidence cleanup failed. Do not replay the edit. Reconcile local storage. " +
                error.Message);
        }
    }

    private void SetUnknownLocalOutcome(Exception error) =>
        localSaveFault = new(LocalSavePhase.Indeterminate, LocalSaveOutcome.Unknown,
            CommittedRevision: revision, Error:
                $"Local transaction outcome is unknown ({error.GetType().Name}). Evidence and drafts are retained. " +
                "Editing is paused; browsing remains available. Reconcile before retrying. " + error.Message);

    private bool ReconcileLocalWriteEvidence()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(workspace!, "local-write-*.json"))
            {
                RejectWorkspaceLink(path);
                using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (file.Length > 4096) throw new FormatException("Invalid local transaction evidence size.");
                    var intent = JsonSerializer.Deserialize<LocalWriteIntent>(file, JsonOptions)
                        ?? throw new FormatException("Missing local transaction evidence.");
                    if (intent.Format != 1 || intent.Id == Guid.Empty ||
                        Path.GetFileName(path) != $"local-write-{intent.Id:N}.json" ||
                        !Guid.TryParseExact(intent.BaseRevision, "D", out _) ||
                        !Guid.TryParseExact(intent.NextRevision, "D", out _) ||
                        intent.BaseRevision == intent.NextRevision)
                        throw new FormatException("Invalid local transaction evidence.");
                    // A matching validated revision establishes commit or rollback, not elapsed time.
                    if (revision != intent.BaseRevision && revision != intent.NextRevision)
                        throw new IOException("The validated local revision does not establish this transaction's outcome.");
                }
                DeleteWorkspaceFile(path);
                localSavingRecovered = true;
            }
            return true;
        }
        catch (Exception error) when (StorageError(error))
        {
            SetUnknownLocalOutcome(error);
            return false;
        }
    }
}
