using System.Windows.Threading;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private bool workPending;
    private bool ordinaryLocalWorkPending;
    private bool localImportPending;
    private bool recoveryWorkPending;
    private bool externalRefreshPending;
    private TaskCompletionSource? workCompleted;

    internal void MarkDraftPending()
    {
        if (!workPending) WorkflowActivity.Text = "Pending local edits — not yet saved";
    }
    internal async Task<T> RunWorkflowAsync<T>(Func<T> operation, string activity,
        bool isImport = false, bool ordinarySave = false, bool browseSafeRecovery = false)
    {
        ordinaryLocalWorkPending = ordinarySave && workflow.Compatibility.SupportsNonblockingNoteAutosave;
        workPending = true;
        localImportPending = isImport && RetainsPersonDrafts;
        recoveryWorkPending = browseSafeRecovery;
        var completion = workCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowActivity.Text = activity;
        OpenWorkspaceButton.IsEnabled = PreviewButton.IsEnabled = ImportPath.IsEnabled = false;
        CreateLocalSqliteWorkspaceButton.IsEnabled = false;
        AttributeEditors.IsEnabled = ManagerEditor.IsEnabled = PersonNotes.IsEnabled = TeamNotes.IsEnabled = false;
        UpdateProfileMode();
        var localSave = ordinarySave && workflow.Compatibility.SupportsNonblockingNoteAutosave;
        PersonNotes.IsEnabled = TeamNotes.IsEnabled = localImportPending || localSave || recoveryWorkPending;
        FavoriteButton.IsEnabled = PersonMoreMenu.IsEnabled = false;
        Photo.CanEdit = false;
        ApplyButton.IsEnabled = false;
        DiscardImportButton.IsEnabled = false;
        try { return localSave ? await workflow.RunLocalSaveAsync(operation) : await Task.Run(operation); }
        finally
        {
            if (pendingPreferredNameCancellations.Count != 0)
            {
                var cancellations = pendingPreferredNameCancellations.Values.ToArray();
                pendingPreferredNameCancellations.Clear();
                foreach (var cancelledName in cancellations)
                    workflow.DiscardPersonTextDraft(cancelledName, OrgExplorer.Workflow.PersonTextField.PreferredName);
                externalRefreshPending = true;
            }
            if (browseSafeRecovery)
            {
                // Ready controls can admit the next Import before the original caller resumes.
                externalRefreshPending = false;
                RefreshExternalWorkspace();
            }
            workPending = false;
            ordinaryLocalWorkPending = false;
            localImportPending = false;
            recoveryWorkPending = false;
            completion.TrySetResult();
            WorkflowActivity.Text = "Ready";
            OpenWorkspaceButton.IsEnabled = PreviewButton.IsEnabled = ImportPath.IsEnabled = true;
            CreateLocalSqliteWorkspaceButton.IsEnabled = true;
            PersonNotes.IsEnabled = TeamNotes.IsEnabled = true;
            UpdateProfileMode();
            var person = selectedEmail is null ? null : workflow.GetRelationships(selectedEmail)?.Person;
            FavoriteButton.IsEnabled = CanEditCurrentWorkspace && person is not null;
            PersonMoreMenu.IsEnabled = CanEditCurrentWorkspace && person is not null;
            queryDirty = true;
            if (!browseSafeRecovery && externalRefreshPending)
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
