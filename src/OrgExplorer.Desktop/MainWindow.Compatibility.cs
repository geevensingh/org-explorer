using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private void ToggleSettings(object sender, RoutedEventArgs e)
    {
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
                if (SettingsPanel.Visibility != Visibility.Visible || !WorkspacePath.IsEnabled) return;
                FocusManager.SetFocusedElement(this, WorkspacePath);
                if (IsVisible) WorkspacePath.Focus();
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
        await RunWorkflowAsync(workflow.RefreshWorkspace, "Validating Workspace / conversion…");
        RefreshExternalWorkspace();
    }
}
