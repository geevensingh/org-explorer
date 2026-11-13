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
        bool keepPreferredNameDraft = false, System.Windows.Controls.UserControl? noteDraft = null)
    {
        workPending = true;
        var completion = workCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowActivity.Text = activity;
        OpenWorkspaceButton.IsEnabled = PreviewButton.IsEnabled = ImportPath.IsEnabled = false;
        AttributeEditors.IsEnabled = ManagerEditor.IsEnabled = PersonNotes.IsEnabled = TeamNotes.IsEnabled = false;
        UpdateProfileMode();
        PreferredNameContext.IsEnabled = PreferredName.IsEnabled = keepPreferredNameDraft;
        PersonNotes.IsEnabled = ReferenceEquals(noteDraft, PersonNotes);
        TeamNotes.IsEnabled = ReferenceEquals(noteDraft, TeamNotes);
        FavoriteButton.IsEnabled = PersonMoreMenu.IsEnabled = false;
        Photo.CanEdit = false;
        ApplyButton.IsEnabled = false;
        DiscardImportButton.IsEnabled = false;
        try { return await Task.Run(operation); }
        finally
        {
            workPending = false;
            completion.TrySetResult();
            WorkflowActivity.Text = "Ready";
            OpenWorkspaceButton.IsEnabled = PreviewButton.IsEnabled = ImportPath.IsEnabled = true;
            PersonNotes.IsEnabled = TeamNotes.IsEnabled = true;
            UpdateProfileMode();
            var person = selectedEmail is null ? null : workflow.GetRelationships(selectedEmail)?.Person;
            FavoriteButton.IsEnabled = workflow.IsWorkspaceCurrent && person is not null;
            PersonMoreMenu.IsEnabled = workflow.IsWorkspaceCurrent && person is not null;
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
        // Settling the dispatcher may admit another queued save.
        do
        {
            while (workPending && workCompleted is { } completion) await completion.Task;
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        } while (workPending);
    }
}
