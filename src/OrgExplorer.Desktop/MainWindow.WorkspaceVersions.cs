using System.Windows;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private WorkspaceVersionReview? workspaceVersionReview;

    private void ShowWorkspaceVersionStatus()
    {
        var state = workflow.Synchronization;
        var ready = !synchronizationPending && !workflow.IsSynchronizing && !updateHandoffPending && state.Folder is not null;
        ReviewWorkspaceVersionsButton.IsEnabled = ready;
        WorkspaceVersionsPanel.Visibility = state.Diverged || workspaceVersionReview is not null
            ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceVersionGuidance.Text = state.Diverged
            ? "Divergent complete Workspace versions are retained. New edits are paused; Person browsing remains available. Review, then deliberately choose a complete version."
            : "Retained recovery is separate from rotating current/previous snapshots. It is not automatically deleted after selection.";
        var selected = WorkspaceVersionChoices.SelectedItem as WorkspaceVersion;
        var currentReview = workspaceVersionReview is { } review &&
            review.WorkspaceId == state.WorkspaceId && review.LocalRevision == state.LocalRevision;
        ChooseWorkspaceVersionButton.IsEnabled = ready && currentReview && selected is not null;
        DeleteRetainedWorkspaceVersionButton.IsEnabled = ready && currentReview && !state.Diverged &&
            selected is { Retained: true, IsLocal: false };
    }

    private void WorkspaceVersionSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowWorkspaceVersionStatus();

    private async void ReviewWorkspaceVersions(object sender, RoutedEventArgs e)
    {
        if (synchronizationPending || workflow.IsSynchronizing || updateHandoffPending) return;
        synchronizationPending = true;
        ShowSynchronizationStatus();
        try
        {
            workspaceVersionReview = await Task.Run(workflow.GetWorkspaceVersionReview);
            WorkspaceVersionChoices.ItemsSource = workspaceVersionReview?.Versions;
            WorkspaceVersionChoices.SelectedIndex = -1;
        }
        finally
        {
            synchronizationPending = false;
            RefreshExternalWorkspace();
        }
    }

    private async void ChooseWorkspaceVersion(object sender, RoutedEventArgs e)
    {
        if (synchronizationPending || workflow.IsSynchronizing || updateHandoffPending || workspaceVersionReview is not { } review ||
            WorkspaceVersionChoices.SelectedItem is not WorkspaceVersion selected) return;
        synchronizationPending = true;
        ShowSynchronizationStatus();
        try { await Task.Run(() => workflow.ChooseWorkspaceVersion(review.Id, selected.Revision)); }
        finally
        {
            workspaceVersionReview = null;
            WorkspaceVersionChoices.ItemsSource = null;
            synchronizationPending = false;
            RefreshExternalWorkspace();
        }
    }

    private async void DeleteRetainedWorkspaceVersion(object sender, RoutedEventArgs e)
    {
        if (synchronizationPending || workflow.IsSynchronizing || updateHandoffPending || workspaceVersionReview is not { } review ||
            WorkspaceVersionChoices.SelectedItem is not WorkspaceVersion selected ||
            MessageBox.Show(this, $"Permanently delete retained recovery version {selected.Revision:D}?\n" +
                "This removes its retained recovery copy, not the current local Workspace or unknown provider copies.",
                "Delete retained recovery", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        synchronizationPending = true;
        ShowSynchronizationStatus();
        try { await Task.Run(() => workflow.DeleteRetainedWorkspaceVersion(review.Id, selected.Revision)); }
        finally
        {
            workspaceVersionReview = null;
            WorkspaceVersionChoices.ItemsSource = null;
            synchronizationPending = false;
            RefreshExternalWorkspace();
        }
    }
}
