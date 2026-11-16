using System.Windows;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private bool synchronizationPending;

    private void ShowSynchronizationStatus()
    {
        var state = workflow.Synchronization;
        var pending = synchronizationPending || workflow.IsSynchronizing;
        SynchronizationPanel.Visibility = state.Supported ? Visibility.Visible : Visibility.Collapsed;
        SynchronizationFolder.Text = state.Folder ?? "No synchronization folder configured";
        SynchronizationStatus.Text = (pending ? "Synchronization in progress. Browsing and local saves remain independent.\n" : "") +
            state.Summary;
        ConfigureSynchronizationFolderButton.IsEnabled = !pending && !updateHandoffPending;
        JoinSynchronizationFolderButton.IsEnabled = !pending && !updateHandoffPending &&
            workflow.People.Count == 0 && workflow.CanSafelyClose;
        SyncNowButton.IsEnabled = !pending && !updateHandoffPending && state.Folder is not null;
        ShowWorkspaceVersionStatus();
    }

    private void ConfigureSynchronizationFolder(object sender, RoutedEventArgs e) => ConfigureSynchronization(join: false);
    private void JoinSynchronizationFolder(object sender, RoutedEventArgs e) => ConfigureSynchronization(join: true);

    private async void ConfigureSynchronization(bool join)
    {
        if (synchronizationPending || workflow.IsSynchronizing || updateHandoffPending || !workflow.Synchronization.Supported ||
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
        try { await workflow.SynchronizeAsync(); }
        finally
        {
            synchronizationPending = false;
            RefreshExternalWorkspace();
        }
    }
}
