namespace OrgExplorer.Workflow;

public enum WorkspaceFileEffectKind { Write, Flush, Publish, Delete }

public sealed record WorkspaceFileEffect(WorkspaceFileEffectKind Kind, string Path, long Bytes = 0,
    string? Destination = null, string? Backup = null);

public enum WorkspaceStoragePhaseKind { Begin, End, Failed }
public sealed record WorkspaceStoragePhase(Guid Id, string Name, WorkspaceStoragePhaseKind Kind,
    double Milliseconds, double CpuMilliseconds = 0);

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
}

public sealed partial class ApplicationWorkflow
{
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
