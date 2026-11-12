using System.Windows;

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
        ReviewPublicationConflictsButton.IsEnabled = !workPending && conflicts.Count != 0;
        ResolvePublicationConflictsButton.IsEnabled = !workPending && conflicts.Count != 0 && publicationConflictReview is not null;
    }

    private void ToggleSettings(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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
