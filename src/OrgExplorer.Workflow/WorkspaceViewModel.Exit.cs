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
            var unsaved = !CanSafelyClose;
            var state = Synchronization;
            var error = string.Join("\n", new[] { LocalSaveError, state.Error }.Where(text => !string.IsNullOrEmpty(text)).Distinct());
            return new(unsaved, state.HasUnpublishedChanges, IsSynchronizing, state.Diverged,
                error.Length == 0 ? null : error);
        }
    }

    public async Task PrepareForExitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Synchronization;
        var attempted = (state.WorkspaceId, state.Folder, state.LocalRevision);
        Task<bool>? publishing = state.Supported ? SynchronizeAsync() : null;
        await FlushNoteDraftsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (publishing is not null)
            {
                await publishing.WaitAsync(cancellationToken).ConfigureAwait(false);
                publishing = null;
            }
            if (ExitStatus.CanClose) return;
            state = Synchronization;
            var current = (state.WorkspaceId, state.Folder, state.LocalRevision);
            // A successful shared task may have exported an older revision.
            if (!IsSynchronizing && state.Supported && state.HasUnpublishedChanges && current != attempted)
            {
                attempted = current;
                publishing = SynchronizeAsync();
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
}
