using System.Windows;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private void ToggleSettings(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SettingsGuidance.Text = workflow.Compatibility.Guidance;
    }

    private void RetryWorkspace(object sender, RoutedEventArgs e)
    {
        workflow.RefreshWorkspace();
        RefreshExternalWorkspace();
    }
}
