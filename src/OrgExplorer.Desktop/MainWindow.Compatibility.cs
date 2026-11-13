using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private Workflow.WorkspaceConflictReview? publicationConflictReview;

    private async void ReviewPublicationConflicts(object sender, RoutedEventArgs e)
    {
        if (workPending || updateHandoffPending) return;
        await RunWorkflowAsync(() =>
        {
            publicationConflictReview = workflow.ReviewWorkspaceConflicts();
            return publicationConflictReview is not null;
        }, "Reviewing current files and retained conflict evidence...");
        ShowStatus();
    }

    private async void ResolvePublicationConflicts(object sender, RoutedEventArgs e)
    {
        if (workPending || updateHandoffPending || publicationConflictReview is not { } review) return;
        if (MessageBox.Show(this,
            "Have you compared the retained local candidate and captured external evidence, and resolved the current files externally?\n" +
            "Acknowledgement does not restore or merge files. Unobserved external versions may be unavailable. " +
            "Only the reviewed conflict IDs and exact current revision can be acknowledged.",
            "Acknowledge external conflict resolution", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (await RunWorkflowAsync(() => workflow.ResolveWorkspaceConflicts(review), "Acknowledging the reviewed external resolution..."))
            publicationConflictReview = null;
        RefreshExternalWorkspace();
    }

    private void ShowPublicationConflicts()
    {
        var conflicts = workflow.GetWorkspaceConflicts();
        PublicationConflictDetails.Text = string.Join("\n", conflicts.Select(value =>
            $"{value.Id}: {value.Outcome}. Capture {(value.CaptureComplete ? "completed for observed bytes only" : "incomplete")}.\n" +
            $"Retained transaction/local candidate: {System.IO.Path.Combine(workflow.Compatibility.WorkspacePath!, "transactions", value.Id.ToString("N"))}\n" +
            string.Join("\n", value.LocalWritingPaths.Select(path => $"Retained local writing: {path}")) + "\n" +
            string.Join("\n", value.Evidence.Select(evidence => $"Captured {evidence.OriginalPath}: {evidence.Path}"))));
        ReviewPublicationConflictsButton.IsEnabled = !workPending && !updateHandoffPending && conflicts.Count != 0;
        ResolvePublicationConflictsButton.IsEnabled = !workPending && !updateHandoffPending &&
            conflicts.Count != 0 && publicationConflictReview is not null;
        ReviewPublicationConflictsButton.Visibility = ResolvePublicationConflictsButton.Visibility =
            conflicts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

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
            var publication = workflow.PublicationOutcome switch
            {
                Workflow.WorkspacePublicationOutcome.PublishedConflict =>
                    "Saved locally with a publication conflict. External resolution is required; closing is blocked.",
                Workflow.WorkspacePublicationOutcome.Indeterminate =>
                    "Publication outcome is unknown. Preserve the evidence and resolve externally; closing is blocked.",
                _ => null
            };
            if (publication is not null) messages.Add(publication);
            foreach (var line in retainedErrors)
            {
                var separator = line.IndexOf(": ", StringComparison.Ordinal);
                messages.Add(separator < 0 ? line : line[..separator] + ".");
            }
        }
        var feedbackLine = feedback?.Split('\n')[0];
        if (!string.IsNullOrEmpty(feedbackLine) && !retainedErrors.Contains(feedbackLine, StringComparer.Ordinal))
            messages.Add(feedbackLine);
        NoticeSummary.Text = string.Join("\n", messages.Distinct());
        LocalWritingDetails.Text = workflow.LocalSaveError ?? "";
        OperationalNotice.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoticeRetryButton.Visibility = workflow.GetWorkspaceConflicts().Count == 0 &&
            (failedWorkspaceOpen is not null || !workflow.IsWorkspaceCurrent &&
                workflow.Compatibility.State != Workflow.WorkspaceCompatibilityState.NotOpened ||
                workflow.HasPendingStorageMaintenance)
            ? Visibility.Visible : Visibility.Collapsed;
        NoticeRetryButton.IsEnabled = RetryWorkspaceButton.IsEnabled = !workPending && !updateHandoffPending;
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
        if (failedWorkspaceOpen is { } failed)
        {
            await OpenWorkspaceAtPathAsync(failed.Path, failed.Mode);
            return;
        }
        await RunWorkflowAsync(workflow.RefreshWorkspace, "Validating Workspace…");
        if (workflow.IsWorkspaceCurrent)
            await RunWorkflowAsync(workflow.RetryStorageMaintenance, "Completing storage maintenance…");
        RefreshExternalWorkspace();
    }
}
