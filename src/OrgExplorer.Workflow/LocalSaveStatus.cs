namespace OrgExplorer.Workflow;

public enum LocalSavePhase { Ready, Pending, Stalled, Failed, Indeterminate, Recovered }
public enum LocalSaveOutcome { None, NotCommitted, Unknown, Committed, Rejected }

public sealed record LocalSaveStatus(LocalSavePhase Phase, LocalSaveOutcome Outcome,
    int OutstandingRequests = 0, string? CommittedRevision = null, string? Error = null)
{
    public bool EditsPaused => Phase is LocalSavePhase.Stalled or LocalSavePhase.Failed or LocalSavePhase.Indeterminate;
    public bool HasUnresolvedOutcome => Outcome == LocalSaveOutcome.Unknown;
    public string Message => Error ?? Phase switch
    {
        LocalSavePhase.Pending => "Saving locally - changes are not yet confirmed saved.",
        LocalSavePhase.Stalled => "Local save stalled after 3 seconds. Changes are NOT confirmed saved. Editing is paused; browsing remains available. Wait for the original outcome before recovery or retry.",
        LocalSavePhase.Recovered => "Local saving recovered. Confirmed committed revisions are accounted for; retained drafts remain unsaved until committed.",
        _ => ""
    };
}

internal sealed class LocalSaveRequests(TimeProvider clock, Action changed) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<long, long> requests = [];
    private long sequence;
    private bool stalled;
    private ITimer? timer;

    public LocalSaveStatus Status
    {
        get
        {
            lock (gate)
            {
                if (requests.Count == 0)
                    return new(stalled ? LocalSavePhase.Recovered : LocalSavePhase.Ready, LocalSaveOutcome.None);
                if (clock.GetElapsedTime(requests.Values.Min()) >= TimeSpan.FromSeconds(3))
                {
                    stalled = true;
                    return new(LocalSavePhase.Stalled, LocalSaveOutcome.Unknown, requests.Count);
                }
                return new(LocalSavePhase.Pending, LocalSaveOutcome.Unknown, requests.Count);
            }
        }
    }

    public long Begin()
    {
        lock (gate)
        {
            var id = ++sequence;
            if (requests.Count == 0) stalled = false;
            requests.Add(id, clock.GetTimestamp());
            ArmTimer();
            return id;
        }
    }

    public void Complete(long id)
    {
        lock (gate)
        {
            _ = Status;
            requests.Remove(id);
            ArmTimer();
        }
    }

    private void ArmTimer()
    {
        timer?.Dispose();
        timer = null;
        if (requests.Count == 0) return;
        var due = TimeSpan.FromSeconds(3) - clock.GetElapsedTime(requests.Values.Min());
        timer = clock.CreateTimer(_ => changed(), null, due > TimeSpan.Zero ? due : TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
    }

    public void Dispose() { lock (gate) { timer?.Dispose(); timer = null; } }
}

public partial interface IApplicationWorkflow
{
    LocalSaveStatus LocalSave { get; }
    bool CanEditLocalWorkspace { get; }
    bool ReconcileLocalSaves();
}

public sealed partial class ApplicationWorkflow
{
    private LocalSaveStatus? localSaveFault;
    // Mode readiness remains independent of transaction outcomes and cannot be cleared by an ordinary write retry.
    private LocalSaveStatus? localWalFault;
    private bool localSavingRecovered;
    private readonly LocalSaveRequests localRequests;
    public LocalSaveStatus LocalSave => Volatile.Read(ref localSaveFault) ?? Volatile.Read(ref localWalFault) ??
        (localRequests.Status.Phase != LocalSavePhase.Ready ? localRequests.Status with { CommittedRevision = revision } :
            new(localSavingRecovered ? LocalSavePhase.Recovered : LocalSavePhase.Ready,
                LocalSaveOutcome.None, CommittedRevision: revision));
    public bool CanEditLocalWorkspace => IsWorkspaceCurrent && !LocalSave.EditsPaused && !Synchronization.Diverged;
    public bool ReconcileLocalSaves()
    {
        if (!localSqlite) return RefreshWorkspace();
        if (localRequests.Status.OutstandingRequests != 0) return false;
        lock (workspaceGate)
        {
            if (!RefreshWorkspaceCore()) return false;
            if (!ReconcileLocalWriteEvidence()) return false;
            localSaveFault = null;
            localWalFault = null;
            compatibility = compatibility with
            {
                Guidance = LocalSqliteDescription(3),
                SupportsOrdinaryEdits = !Synchronization.Diverged,
                SupportsNoteEdits = !Synchronization.Diverged
            };
            localSavingRecovered = true;
            Status = "Local storage recovered. Retained drafts are not saved until explicitly retried.";
            return true;
        }
    }

    private void LocalStorageFailed(Exception error, LocalSaveOutcome outcome = LocalSaveOutcome.NotCommitted)
    {
        if (!localSqlite || error is not (IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException))
            return;
        if (localSaveFault?.Outcome == LocalSaveOutcome.Unknown) return;
        localSaveFault = new(outcome == LocalSaveOutcome.Unknown ? LocalSavePhase.Indeterminate : LocalSavePhase.Failed,
            outcome, CommittedRevision: revision, Error:
                $"Local save failed ({error.GetType().Name}). Changes are NOT saved locally (confirmed noncommit). Editing is paused; browsing remains available. Reconcile local storage before retry. " +
                error.Message);
    }

    private readonly AsyncLocal<int> localSaveDepth = new();
    private bool CanBeginLocalSave()
    {
        if (!localSqlite) return true;
        if (localWalFault is { } walFault) return Fail(walFault.Message);
        if (Synchronization.Diverged)
            return Fail("Edits are paused for preserved divergent Workspace versions.");
        if (localSaveFault?.Outcome == LocalSaveOutcome.Unknown ||
            localSaveDepth.Value == 0 && LocalSave.EditsPaused && LocalSave.HasUnresolvedOutcome)
            return Fail("Local save outcome remains unknown. No competing retry was started; browsing and retained drafts remain available.");
        if (localSaveDepth.Value == 0 && localSaveFault is not null) return ReconcileLocalSaves();
        return true;
    }

    private bool AllowLocalDraftEdit() => !localSqlite || !LocalSave.EditsPaused && !Synchronization.Diverged ||
        Fail("Editing paused. Retained drafts are unchanged. Resolve local saving or retained synchronization versions; browsing remains available.");

    private T TrackLocalSave<T>(Func<T> operation)
    {
        if (!localSqlite || localSaveDepth.Value != 0) return operation();
        var request = localRequests.Begin();
        localSaveDepth.Value++;
        try { return operation(); }
        finally { localSaveDepth.Value--; localRequests.Complete(request); }
    }
}

public sealed partial class WorkspaceViewModel
{
    private readonly LocalSaveRequests localSaveRequests;
    private LocalSaveStatus? viewSaveFault;
    private IDisposable? unknownSaveAdoptionDeferral;
    private volatile bool localSaveReconciling;
    public LocalSaveStatus LocalSave => localSaveReconciling
        ? new(LocalSavePhase.Indeterminate, LocalSaveOutcome.Unknown,
            Error: "Reconciling original local save outcomes. Drafts are retained; editing is paused and browsing remains available.")
        : Volatile.Read(ref viewSaveFault) ?? (workflow.LocalSave.EditsPaused ? workflow.LocalSave :
        localSaveRequests.Status.Phase != LocalSavePhase.Ready
            ? localSaveRequests.Status with { CommittedRevision = workflow.LocalSave.CommittedRevision } : workflow.LocalSave);
    public bool CanEditLocalWorkspace => IsWorkspaceCurrent && !LocalSave.EditsPaused && !Synchronization.Diverged;
    private bool AllowLocalDraftEdit()
    {
        if (!Compatibility.SupportsNonblockingNoteAutosave || !LocalSave.EditsPaused && !Synchronization.Diverged) return true;
        navigationError = "Editing paused. Retained drafts are unchanged; reconcile local saving. Browsing remains available.";
        Notify();
        return false;
    }
    public bool ReconcileLocalSaves()
    {
        bool recovering;
        long epoch;
        LocalSaveStatus? fault;
        lock (noteDraftGate)
        {
            if (localSaveReconciling) return false;
            if (localNoteDrafts.Values.Any(draft => draft.Saving) || Volatile.Read(ref explicitLocalSaves) != 0) return false;
            recovering = LocalSave.EditsPaused;
            localSaveReconciling = true;
            if (recovering)
            {
                noteAutosaveEpoch++;
                noteAutosaveTask = null;
            }
            epoch = noteAutosaveEpoch;
            fault = viewSaveFault;
        }
        using var adoptionDeferral = workflow.DeferSnapshotAdoption();
        try
        {
            return Observe(() =>
            {
                if (!workflow.ReconcileLocalSaves()) return false;
                lock (noteDraftGate)
                {
                    if (epoch != noteAutosaveEpoch || !ReferenceEquals(fault, viewSaveFault))
                    {
                        navigationError = "A newer local save outcome requires reconciliation. Editing remains paused; no adoption lease was released.";
                        return false;
                    }
                    if (!ReconcileUnknownNoteDrafts()) return false;
                    if (recovering)
                        foreach (var draft in localNoteDrafts.Values)
                        {
                            foreach (var request in draft.Requests) localSaveRequests.Complete(request.Request);
                            draft.Requests.Clear();
                        }
                    viewSaveFault = null;
                    unknownSaveAdoptionDeferral?.Dispose();
                    unknownSaveAdoptionDeferral = null;
                }
                return true;
            });
        }
        finally
        {
            localSaveReconciling = false;
            if (!recovering) lock (noteDraftGate) ScheduleNoteAutosave();
            NotifyLocalNoteDrafts();
        }
    }

    private void UnknownLocalSave(Exception error)
    {
        lock (noteDraftGate)
        {
            unknownSaveAdoptionDeferral ??= workflow.DeferSnapshotAdoption();
            viewSaveFault = new(LocalSavePhase.Indeterminate, LocalSaveOutcome.Unknown,
                Error: $"Local save acknowledgment is unknown ({error.GetType().Name}). Exact drafts are retained. " +
                    "Editing is paused; browsing remains available. Reconcile committed local state before retrying. " + error.Message);
        }
    }

    private int explicitLocalSaves;
    private readonly AsyncLocal<bool> admittedLocalSave = new();
    private bool CanSubmitLocalSave()
    {
        if (admittedLocalSave.Value || !LocalSave.EditsPaused || !LocalSave.HasUnresolvedOutcome) return true;
        navigationError = "The original local save outcome remains unknown. No competing retry was started; drafts and browsing are retained.";
        Notify();
        return false;
    }

    private T ObserveLocalSave<T>(Func<T> operation)
    {
        if (!Compatibility.SupportsNonblockingNoteAutosave) return Observe(operation);
        var request = Compatibility.SupportsNonblockingNoteAutosave ? localSaveRequests.Begin() : (long?)null;
        using var adoptionDeferral = request is not null ? workflow.DeferSnapshotAdoption() : null;
        Interlocked.Increment(ref explicitLocalSaves);
        try { return Observe(operation); }
        finally
        {
            Interlocked.Decrement(ref explicitLocalSaves);
            if (request is { } id) localSaveRequests.Complete(id);
            NotifyLocalNoteDrafts();
        }
    }

    public async Task<T> RunLocalSaveAsync<T>(Func<T> operation)
    {
        var admitted = CanSubmitLocalSave();
        var request = Compatibility.SupportsNonblockingNoteAutosave ? localSaveRequests.Begin() : (long?)null;
        using var adoptionDeferral = request is not null ? workflow.DeferSnapshotAdoption() : null;
        Interlocked.Increment(ref explicitLocalSaves);
        try
        {
            return await Task.Run(() =>
            {
                admittedLocalSave.Value = admitted;
                try { return operation(); }
                finally { admittedLocalSave.Value = false; }
            });
        }
        finally
        {
            Interlocked.Decrement(ref explicitLocalSaves);
            if (request is { } id) localSaveRequests.Complete(id);
            NotifyLocalNoteDrafts();
        }
    }
}
