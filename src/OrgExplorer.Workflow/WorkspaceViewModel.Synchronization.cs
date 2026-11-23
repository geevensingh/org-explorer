namespace OrgExplorer.Workflow;

public sealed partial class WorkspaceViewModel
{
    private readonly object synchronizationLifecycle = new();
    private readonly TimeProvider synchronizationClock;
    private ITimer? synchronizationTimer;
    private string? scheduledWorkspace, scheduledFolder;
    private Task<bool>? synchronizationTask;
    private bool synchronizationRequested, synchronizationStopped;
    private long synchronizationSchedule, synchronizationRun;
    private string? synchronizationWorkerError;

    public WorkspaceSynchronization Synchronization => Volatile.Read(ref synchronizationWorkerError) is { } error
        ? workflow.Synchronization with { Error = error } : workflow.Synchronization;
    public bool IsSynchronizing { get { lock (synchronizationLifecycle) return synchronizationTask is not null; } }
    public bool ConfigureSynchronizationFolder(string folder, bool joinExistingWorkspace = false,
        string? expectedWorkspacePath = null, Guid? expectedWorkspaceId = null) =>
        Observe(() => workflow.ConfigureSynchronizationFolder(folder, joinExistingWorkspace,
            expectedWorkspacePath, expectedWorkspaceId));
    public bool SyncNow() => SynchronizeAsync().GetAwaiter().GetResult();
    public IDisposable DeferSnapshotAdoption() => workflow.DeferSnapshotAdoption();

    public Task<bool> SynchronizeAsync()
    {
        lock (synchronizationLifecycle)
        {
            ObjectDisposedException.ThrowIf(synchronizationStopped, this);
            return RequestSynchronization();
        }
    }

    private Task<bool> RequestSynchronization()
    {
        if (synchronizationTask is not null)
        {
            synchronizationRequested = true;
            return synchronizationTask;
        }
        synchronizationRequested = false;
        var run = ++synchronizationRun;
        var task = synchronizationTask = Task.Run(RunSynchronization);
        _ = task.ContinueWith(failed =>
        {
            var error = failed.Exception!.GetBaseException();
            lock (synchronizationLifecycle)
            {
                if (run != synchronizationRun) return;
                synchronizationWorkerError = $"Synchronization worker failed ({error.GetType().Name}): {error.Message}";
            }
            NotifySynchronization();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return task;
    }

    private bool RunSynchronization()
    {
        var ended = false;
        try
        {
            while (true)
            {
                synchronizationWorkerError = null;
                NotifySynchronization();
                bool result;
                // VM requests/generations can outlive the core commit and its return.
                try
                {
                    using (LocalSave.HasUnresolvedOutcome ? workflow.DeferSnapshotAdoption() : null)
                        result = Observe(workflow.SyncNow);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    synchronizationWorkerError = $"Synchronization failed ({error.GetType().Name}): {error.Message}";
                    result = false;
                }
                lock (synchronizationLifecycle)
                {
                    if (synchronizationRequested && !synchronizationStopped)
                    {
                        synchronizationRequested = false;
                        continue;
                    }
                    synchronizationTask = null;
                    ended = true;
                    return result;
                }
            }
        }
        finally
        {
            if (!ended) lock (synchronizationLifecycle) synchronizationTask = null;
            NotifySynchronization();
        }
    }

    private void UpdateSynchronizationSchedule()
    {
        var state = Synchronization;
        var path = IsWorkspaceCurrent && state.Supported ? Compatibility.WorkspacePath : null;
        var folder = path is not null ? state.Folder : null;
        lock (synchronizationLifecycle)
        {
            if (synchronizationStopped || path == scheduledWorkspace && folder == scheduledFolder) return;
            synchronizationTimer?.Dispose();
            synchronizationTimer = null;
            scheduledWorkspace = path;
            scheduledFolder = folder;
            synchronizationWorkerError = null;
            var schedule = ++synchronizationSchedule;
            if (folder is null) return;
            synchronizationTimer = synchronizationClock.CreateTimer(state =>
            {
                lock (synchronizationLifecycle)
                    if (!synchronizationStopped && schedule == synchronizationSchedule) _ = RequestSynchronization();
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
    }

    private void NotifySynchronization()
    {
        if (noteAutosaveStopped) return;
        Notify();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task<bool>? StopSynchronization()
    {
        lock (synchronizationLifecycle)
        {
            synchronizationStopped = true;
            synchronizationRequested = false;
            synchronizationTimer?.Dispose();
            synchronizationTimer = null;
            return synchronizationTask;
        }
    }
}
