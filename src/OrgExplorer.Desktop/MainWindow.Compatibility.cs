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
        if (workPending) return;
        await RunWorkflowAsync(workflow.RefreshWorkspace, "Validating Workspace / conversion…");
        RefreshExternalWorkspace();
    }
}
