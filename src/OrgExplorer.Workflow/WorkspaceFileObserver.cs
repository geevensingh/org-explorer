namespace OrgExplorer.Workflow;

public enum WorkspaceFileEffectKind { Write, Flush, Publish, Delete }

public sealed record WorkspaceFileEffect(WorkspaceFileEffectKind Kind, string Path, long Bytes = 0,
    string? Destination = null, string? Backup = null);

public enum WorkspaceStoragePhaseKind { Begin, End, Failed }
public sealed record WorkspaceStoragePhase(Guid Id, string Name, WorkspaceStoragePhaseKind Kind,
    double Milliseconds, double CpuMilliseconds = 0);

public sealed record WorkspaceSqliteSetupFailure(Guid OperationId, int ThreadId, long Timestamp, long TimestampFrequency,
    DateTimeOffset ObservedAt, int Code, int ExtendedCode, int HResult);
public sealed record WorkspaceSqliteNativeModule(string Name, string Path, string Sha256, string Architecture,
    string? Version = null, string? SourceId = null);
public sealed record WorkspaceSqliteNativeProbe(string Status, int? ReportedSystemError,
    WorkspaceSqliteNativeModule? Module, string? Reason);

/// <summary>
/// Observes completed filesystem effects and aggregate phases without replacing them. Calls can arrive concurrently.
/// Write bytes are payload bytes, including staging and repeated overwrites, not filesystem metadata.
/// Observers must not throw or modify the Workspace.
/// </summary>
public interface IWorkspaceFileObserver
{
    // Bounded object I/O may report concurrent effects.
    void Observe(WorkspaceFileEffect effect);
    void ObservePhase(WorkspaceStoragePhase phase) { }
    // The read callback is valid only synchronously on this callback's owning thread.
    // Default observers never request native probing.
    void ObserveSqliteSetupFailure(WorkspaceSqliteSetupFailure failure,
        Func<WorkspaceSqliteNativeProbe> readNativeSystemError) { }
}

public sealed partial class ApplicationWorkflow
{
    private void ObserveSqliteSetupFailure(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteException error)
    {
        var observer = fileObserver!;
        var failure = new WorkspaceSqliteSetupFailure(Guid.NewGuid(), Environment.CurrentManagedThreadId,
            System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency,
            DateTimeOffset.UtcNow, error.SqliteErrorCode, error.SqliteExtendedErrorCode, error.HResult);
        var active = true;
        WorkspaceSqliteNativeProbe? captured = null;
        WorkspaceSqliteNativeProbe ReadNative()
        {
            if (!Volatile.Read(ref active) || Environment.CurrentManagedThreadId != failure.ThreadId)
                return new("Unavailable", null, null, "The owning setup-failure callback has ended or changed thread.");
            if (failure.Code != 10 || failure.ExtendedCode != 1546)
                return new("NotApplicable", null, null, "No matching SQLITE_IOERR_TRUNCATE occurred.");
            return captured ??= SqliteSetupNativeProbe.Read(connection);
        }
        try { observer.ObserveSqliteSetupFailure(failure, ReadNative); }
        catch (Exception diagnosticError)
        {
            var detail = $"type={diagnosticError.GetType().Name}; hresult=0x{diagnosticError.HResult:X8}";
            error.Data["SQLiteSetupObserverFault"] = detail;
            try { System.Diagnostics.Trace.TraceError("SQLite setup observation failed: {0}. Original SQLite failure is unchanged.", detail); }
            catch (Exception traceError)
            {
                error.Data["SQLiteSetupTraceFault"] = $"type={traceError.GetType().Name}; hresult=0x{traceError.HResult:X8}";
            }
        }
        finally { Volatile.Write(ref active, false); }
    }

    private sealed class LocalReadSnapshotObservation(IWorkspaceFileObserver observer) : IDisposable
    {
        // Begin is a known-held snapshot point, not acquisition time. Only End confirms
        // successful transaction/connection disposal; Failed makes no release claim.
        private readonly Guid id = Guid.NewGuid();
        private System.Diagnostics.Stopwatch? timer;
        private bool terminal;

        public void KnownHeld()
        {
            timer = System.Diagnostics.Stopwatch.StartNew();
            Report(WorkspaceStoragePhaseKind.Begin);
        }

        public void Released()
        {
            terminal = true;
            Report(WorkspaceStoragePhaseKind.End);
        }

        public void Dispose()
        {
            if (timer is null || terminal) return;
            terminal = true;
            Report(WorkspaceStoragePhaseKind.Failed);
        }

        private void Report(WorkspaceStoragePhaseKind kind)
        {
            try { observer.ObservePhase(new(id, "local-sqlite-read-snapshot", kind, timer!.Elapsed.TotalMilliseconds)); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError(
                    "Workspace read observation failed: kind={0}; type={1}; hresult=0x{2:X8}. Storage outcome is unchanged.",
                    kind, error.GetType().Name, error.HResult);
            }
        }
    }

    private T ObserveStoragePhase<T>(string name, Func<T> action)
    {
        if (fileObserver is null) return action();
        var id = Guid.NewGuid();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        fileObserver.ObservePhase(new(id, name, WorkspaceStoragePhaseKind.Begin, 0));
        var outcome = WorkspaceStoragePhaseKind.Failed;
        try
        {
            var result = action();
            outcome = WorkspaceStoragePhaseKind.End;
            return result;
        }
        finally
        {
            fileObserver.ObservePhase(new(id, name, outcome, timer.Elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - cpu).TotalMilliseconds));
        }
    }

    private void ObserveStoragePhase(string name, Action action) =>
        ObserveStoragePhase(name, () => { action(); return true; });
}
