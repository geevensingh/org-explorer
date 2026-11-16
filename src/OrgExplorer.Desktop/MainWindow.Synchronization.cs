using System.Windows;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private bool synchronizationPending;

    private void ShowSynchronizationStatus()
    {
        var state = workflow.Synchronization;
        SynchronizationPanel.Visibility = state.Supported ? Visibility.Visible : Visibility.Collapsed;
        SynchronizationFolder.Text = state.Folder ?? "No synchronization folder configured";
        SynchronizationStatus.Text = (synchronizationPending ? "Synchronization in progress. Browsing and local saves remain independent.\n" : "") +
            state.Summary;
        ConfigureSynchronizationFolderButton.IsEnabled = !synchronizationPending && !updateHandoffPending;
        JoinSynchronizationFolderButton.IsEnabled = !synchronizationPending && !updateHandoffPending &&
            workflow.People.Count == 0 && workflow.CanSafelyClose;
        SyncNowButton.IsEnabled = !synchronizationPending && !updateHandoffPending && state.Folder is not null;
        ShowWorkspaceVersionStatus();
    }

    private void ConfigureSynchronizationFolder(object sender, RoutedEventArgs e) => ConfigureSynchronization(join: false);
    private void JoinSynchronizationFolder(object sender, RoutedEventArgs e) => ConfigureSynchronization(join: true);

    private async void ConfigureSynchronization(bool join)
    {
        if (synchronizationPending || updateHandoffPending || !workflow.Synchronization.Supported ||
            chooseWorkspaceFolder(this) is not { } folder) return;
        synchronizationPending = true;
        ShowSynchronizationStatus();
        try { await Task.Run(() => workflow.ConfigureSynchronizationFolder(folder, join)); }
        finally
        {
            synchronizationPending = false;
            ShowStatus();
        }
    }

    private async void SynchronizeNow(object sender, RoutedEventArgs e)
    {
        if (synchronizationPending || updateHandoffPending) return;
        synchronizationPending = true;
        ShowSynchronizationStatus();
        try { await Task.Run(workflow.SyncNow); }
        finally
        {
            synchronizationPending = false;
            RefreshExternalWorkspace();
        }
    }
}
