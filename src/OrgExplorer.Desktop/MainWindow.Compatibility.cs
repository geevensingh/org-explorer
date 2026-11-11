using System.Windows;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
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
