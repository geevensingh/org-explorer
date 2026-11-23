using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private string? retainedWritingNavigationFeedback;

    private void InitializeOperationalNotices()
    {
        var text = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler changed = (_, _) => ShowOperationalNotice();
        foreach (var control in new[] { StatusText, WorkflowActivity, WorkspaceFreshness })
            text.AddValueChanged(control, changed);
        Closed += (_, _) =>
        {
            foreach (var control in new[] { StatusText, WorkflowActivity, WorkspaceFreshness })
                text.RemoveValueChanged(control, changed);
        };
        ShowOperationalNotice();
    }

    private void ShowOperationalNotice()
    {
        var messages = new List<string>();
        if (workflow.LocalSave.Phase == Workflow.LocalSavePhase.Recovered && !workflow.CanSafelyClose)
            messages.Add(workflow.LocalSave.Message);
        var error = workflow.LocalSaveError;
        var retainedErrors = error?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var feedback = StatusText.Text != workflow.Status ? StatusText.Text : workflow.OperationError;
        if (workPending || error is null && string.IsNullOrEmpty(feedback) &&
            (HasPreferredNameDraft || HasUnsavedRelationship || !workflow.CanSafelyClose))
            messages.Add(WorkflowActivity.Text);
        if (!workflow.IsWorkspaceCurrent && workflow.Compatibility.State != Workflow.WorkspaceCompatibilityState.NotOpened)
            messages.Add("Workspace unavailable. Showing last validated information, if any.");
        if (error is not null)
        {
            foreach (var line in retainedErrors)
            {
                var separator = line.IndexOf(": ", StringComparison.Ordinal);
                messages.Add(separator < 0 ? line : line[..separator] + ".");
            }
        }
        var feedbackLine = feedback?.Split('\n')[0];
        if (!string.IsNullOrEmpty(feedbackLine) && !retainedErrors.Contains(feedbackLine, StringComparer.Ordinal) &&
            !(error is not null && feedback == retainedWritingNavigationFeedback))
            messages.Add(feedbackLine);
        if (profileCopyError is not null) messages.Add(profileCopyError);
        if (workflow.Synchronization.Error is { } synchronizationError) messages.Add(synchronizationError);
        NoticeSummary.Text = string.Join("\n", messages.Distinct());
        LocalWritingDetails.Text = workflow.LocalSaveError ?? "";
        OperationalNotice.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoticeRetryButton.Visibility =
            (failedWorkspaceOpen is not null || !workflow.IsWorkspaceCurrent &&
                workflow.Compatibility.State != Workflow.WorkspaceCompatibilityState.NotOpened ||
                workflow.LocalSave.EditsPaused)
            ? Visibility.Visible : Visibility.Collapsed;
        NoticeRetryButton.IsEnabled = RetryWorkspaceButton.IsEnabled = !workPending && !updateHandoffPending &&
            workflow.LocalSave.OutstandingRequests == 0;
    }

    private void OpenRecoverySettings(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.Visibility != Visibility.Visible) ToggleSettings(sender, e);
        TechnicalDetails.IsExpanded = true;
        TechnicalDetails.BringIntoView();
    }

    private void ToggleSettings(object sender, RoutedEventArgs e)
    {
        CancelPreferredNameEntry();
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsPanel.Visibility == Visibility.Visible && workflow.PeopleNavigation.OverlayOpen)
        {
            if (workflow.PeopleNavigation.IsBrowsing) RememberPeopleBrowsing();
            workflow.ShowPeopleOverlay(false);
        }
        UpdateMainViews();
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            SettingsScroll.ScrollToTop();
            // Collapsed content must finish layout and the current key event before accepting focus.
            Dispatcher.BeginInvoke(() =>
            {
                if (SettingsPanel.Visibility != Visibility.Visible || !OpenWorkspaceButton.IsEnabled) return;
                FocusManager.SetFocusedElement(this, OpenWorkspaceButton);
                if (IsVisible) OpenWorkspaceButton.Focus();
            }, DispatcherPriority.Input);
        }
        else
        {
            FocusManager.SetFocusedElement(this, OpenSettingsButton);
            if (IsVisible) OpenSettingsButton.Focus();
        }
    }

    private async void RetryWorkspace(object sender, RoutedEventArgs e)
    {
        if (updateHandoffPending)
        {
            StatusText.Text = "Workspace validation waits while the safe update handoff is in progress. Settings remain available.";
            return;
        }
        if (workPending) return;
        if (workflow.Compatibility.SupportsNonblockingNoteAutosave && workflow.LocalSave.EditsPaused)
        {
            await RunWorkflowAsync(workflow.ReconcileLocalSaves, "Reconciling the original local save outcome...",
                browseSafeRecovery: true);
            RefreshExternalWorkspace();
            return;
        }
        if (failedWorkspaceOpen is { } failed)
        {
            await OpenWorkspaceAtPathAsync(failed.Path, failed.Mode);
            return;
        }
        await RunWorkflowAsync(workflow.RefreshWorkspace, "Validating Workspace…");
        RefreshExternalWorkspace();
    }
}
