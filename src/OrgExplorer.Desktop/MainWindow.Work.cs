using System.Windows.Threading;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private bool workPending;
    private bool externalRefreshPending;
    private TaskCompletionSource? workCompleted;

    internal bool HasPendingWork => workPending;
    internal void MarkDraftPending()
    {
        if (!workPending) WorkflowActivity.Text = "Pending local edits — not yet saved";
    }
    internal async Task<T> RunWorkflowAsync<T>(Func<T> operation, string activity,
        bool keepProfileDraft = false, System.Windows.Controls.UserControl? noteDraft = null)
    {
        workPending = true;
        var completion = workCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowActivity.Text = activity;
        OpenWorkspaceButton.IsEnabled = PreviewButton.IsEnabled = WorkspacePath.IsEnabled = ImportPath.IsEnabled = false;
        AttributeEditors.IsEnabled = ManagerEditor.IsEnabled = PersonNotes.IsEnabled = TeamNotes.IsEnabled = false;
        UpdateProfileMode();
        PreferredName.IsEnabled = keepProfileDraft;
        PersonNotes.IsEnabled = ReferenceEquals(noteDraft, PersonNotes);
        TeamNotes.IsEnabled = ReferenceEquals(noteDraft, TeamNotes);
        FavoriteButton.IsEnabled = ArchivePersonButton.IsEnabled = ReactivatePersonButton.IsEnabled = false;
        Photo.CanEdit = false;
        ApplyButton.IsEnabled = false;
        try { return await Task.Run(operation); }
        finally
        {
            workPending = false;
            completion.TrySetResult();
            WorkflowActivity.Text = "Ready";
            OpenWorkspaceButton.IsEnabled = PreviewButton.IsEnabled = WorkspacePath.IsEnabled = ImportPath.IsEnabled = true;
            PersonNotes.IsEnabled = TeamNotes.IsEnabled = true;
            UpdateProfileMode();
            var person = selectedEmail is null ? null : workflow.GetRelationships(selectedEmail)?.Person;
            FavoriteButton.IsEnabled = workflow.IsWorkspaceCurrent && person is not null;
            ArchivePersonButton.IsEnabled = workflow.IsWorkspaceCurrent && person is { Archived: false };
            ReactivatePersonButton.IsEnabled = workflow.IsWorkspaceCurrent && person is { Archived: true };
            queryDirty = true;
            if (externalRefreshPending)
            {
                externalRefreshPending = false;
                _ = Dispatcher.BeginInvoke(RefreshExternalWorkspace, DispatcherPriority.ContextIdle);
            }
        }
    }

    private async Task WaitForLocalWork()
    {
        while (workPending && workCompleted is { } completion) await completion.Task;
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
    }
}
