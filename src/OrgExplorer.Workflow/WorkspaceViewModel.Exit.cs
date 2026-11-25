namespace OrgExplorer.Workflow;

public sealed record WorkspaceExitStatus(bool HasUnsavedChanges, bool HasUnpublishedChanges,
    bool IsSynchronizing, bool HasConflict, string? Error)
{
    public bool CanClose => !HasUnsavedChanges && !HasUnpublishedChanges && !IsSynchronizing && !HasConflict && Error is null;
    public string Message => string.Join("\n", new[]
    {
        HasUnsavedChanges ? "Local edits are NOT confirmed saved and may be lost if you exit. Explicit profile and Preferred-name drafts require their own Save or Cancel." : null,
        HasUnpublishedChanges ? "Locally committed work remains on this computer, but the latest version is not published and may be missing elsewhere." : null,
        HasConflict ? "Divergent Workspace versions are retained. The conflict is unresolved; exiting does not choose or delete a version." : null,
        IsSynchronizing ? "Publication / incoming checks are in progress. Browsing remains available." : null,
        Error,
        "Normal exit waits for current local commits and the latest publication, not provider delivery elsewhere. Exit anyway stops now; Cancel keeps the app and retained drafts open."
    }.Where(text => !string.IsNullOrEmpty(text)));
}

public sealed partial class WorkspaceViewModel
{
    public WorkspaceExitStatus ExitStatus
    {
        get
        {
            var state = Synchronization;
            var unavailable = state.Supported && !IsWorkspaceCurrent;
            var unsaved = !CanSafelyClose || unavailable;
            var error = string.Join("\n", new[] { LocalSaveError, state.Error, unavailable ? Compatibility.Guidance : null }
                .Where(text => !string.IsNullOrEmpty(text)).Distinct());
            return new(unsaved, state.HasUnpublishedChanges, IsSynchronizing, state.Diverged,
                error.Length == 0 ? null : error);
        }
    }

    // Inner waits can finish before the exit continuation runs; cancellation must cover that gap.
    public Task PrepareForExitAsync(CancellationToken cancellationToken) =>
        PrepareForExitCoreAsync(cancellationToken).WaitAsync(cancellationToken);

    private async Task PrepareForExitCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Synchronization;
        (Guid? WorkspaceId, string? Folder, Guid? LocalRevision)? attempted = null;
        Task<bool>? publishing = null;
        if (state.Supported && (state.HasUnpublishedChanges || state.Error is not null))
        {
            attempted = (state.WorkspaceId, state.Folder, state.LocalRevision);
            publishing = SynchronizeAsync();
        }
        await FlushNoteDraftsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (publishing is not null)
            {
                await publishing.WaitAsync(cancellationToken).ConfigureAwait(false);
                publishing = null;
            }
            var before = (Compatibility.WorkspacePath, Synchronization.WorkspaceId,
                Synchronization.Folder, Synchronization.LocalRevision, Synchronization.PublishedRevision);
            long epoch, order;
            lock (noteDraftGate) { epoch = noteAutosaveEpoch; order = noteDraftOrder; }
            var verified = ExitStatus.CanClose &&
                await Task.Run(workflow.VerifyExitPublication).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            bool sameDrafts;
            lock (noteDraftGate)
                sameDrafts = epoch == noteAutosaveEpoch && order == noteDraftOrder && localNoteDrafts.Count == 0;
            var after = (Compatibility.WorkspacePath, Synchronization.WorkspaceId,
                Synchronization.Folder, Synchronization.LocalRevision, Synchronization.PublishedRevision);
            if (verified && before == after && sameDrafts && Volatile.Read(ref explicitLocalSaves) == 0 &&
                localSaveRequests.Status.OutstandingRequests == 0 && !LocalSave.HasUnresolvedOutcome && ExitStatus.CanClose) return;
            state = Synchronization;
            var current = (state.WorkspaceId, state.Folder, state.LocalRevision);
            // A successful shared task may have exported an older revision.
            if (!verified && !IsSynchronizing && state.Supported && !state.Diverged && IsWorkspaceCurrent &&
                workflow.CanSafelyClose && !LocalSave.EditsPaused && current != attempted)
            {
                attempted = current;
                publishing = SynchronizeAsync();
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
}
