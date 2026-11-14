using System.IO;
using System.Windows;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private static string SettingsDirectory => Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgExplorer");
    private readonly WorkspaceSettings workspaceSettings = new(Path.Combine(SettingsDirectory, "workspace-settings.json"));
    private string? startupRestorePath;
    private string? startupRestoreError;
    private (string Path, WorkspaceOpenMode Mode)? failedWorkspaceOpen;

    private async Task RestoreStartupWorkspaceAsync()
    {
        var updateWorkspace = updateJournal?.PendingWorkspace;
        if (updateJournal is not null && installedVersion is not null)
        {
            var opened = false;
            string? failure = null;
            try
            {
                await RunWorkflowAsync(() =>
                {
                    updateJournal.Recover(installedVersion, path =>
                    {
                        WorkspaceLocation.RequireIndependent(path);
                        return opened = workspaceSettings.RestoreWorkspace(workflow, path);
                    }, () => workflow.Status);
                    return true;
                }, "Reopening Workspace after update...");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failure = error.Message;
                updateRecoveryError = "Update restart/Workspace recovery failed: " + error.Message +
                    " No successful recovery is claimed. Settings remain available.";
            }
            if (updateWorkspace is not null)
            {
                ShowWorkspaceOpenOutcome(updateWorkspace, WorkspaceOpenMode.ExistingOnly, opened, failure);
                PresentWorkspaceOpen();
                return;
            }
        }
        if (workspaceSettings.LastWorkspacePath is { } path)
            await OpenWorkspaceAtPathAsync(path, WorkspaceOpenMode.ExistingOnly);
        else
        {
            ShowWorkspaceStartup();
            ShowStatus();
        }
    }

    private async Task OpenWorkspaceAtPathAsync(string path, WorkspaceOpenMode mode = WorkspaceOpenMode.OpenOrCreate)
    {
        if (updateHandoffPending)
        {
            ShowNavigationBlocked("Workspace opening waits while the safe update handoff is in progress.");
            return;
        }
        try { WorkspaceLocation.RequireIndependent(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = error.Message;
            ShowWorkspaceOpenOutcome(path, mode, false, error.Message);
            return;
        }
        if (!TryLeaveLocalEdits()) return;
        var opened = await RunWorkflowAsync(() => mode == WorkspaceOpenMode.ExistingOnly
            ? workspaceSettings.RestoreWorkspace(workflow, path)
            : workspaceSettings.OpenWorkspace(workflow, path), "Opening Workspace...");
        if (!opened && (workflow.LastWorkspaceOpenWasRejected || workflow.ProfileEdit is not null))
        {
            ShowStatus();
            return;
        }
        ShowWorkspaceOpenOutcome(path, mode, opened);
        PresentWorkspaceOpen();
    }

    private void ShowWorkspaceOpenOutcome(string path, WorkspaceOpenMode mode, bool opened, string? failure = null)
    {
        failedWorkspaceOpen = opened ? null : (path, mode);
        if (opened)
        {
            startupRestorePath = null;
            startupRestoreError = null;
        }
        else if (mode == WorkspaceOpenMode.ExistingOnly)
        {
            startupRestorePath = path;
            startupRestoreError = $"The previous Workspace could not be reopened: {path}\n" +
                (failure ?? workflow.Status) + "\nRetry when it is available, or choose another folder. No replacement Workspace was created.";
        }
        ShowWorkspaceStartup();
        ShowOperationalNotice();
    }

    private void ShowWorkspaceStartup()
    {
        WorkspaceStartupStatus.Text = string.Join("\n", new[] { startupRestoreError,
                workspaceSettings.PreferenceError == workflow.LayoutPreferenceFeedback ||
                workspaceSettings.PreferenceError == workflow.ViewPreferenceFeedback ? null : workspaceSettings.PreferenceError }
            .Where(text => !string.IsNullOrEmpty(text)));
        WorkspaceStartupPanel.Visibility = WorkspaceStartupStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RetryStartupWorkspaceButton.Visibility = startupRestorePath is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void RetryStartupWorkspace(object sender, RoutedEventArgs e)
    {
        if (startupRestorePath is { } path) await OpenWorkspaceAtPathAsync(path, WorkspaceOpenMode.ExistingOnly);
    }

    private async void ChooseAnotherWorkspace(object sender, RoutedEventArgs e)
    {
        if (chooseWorkspaceFolder(this) is not { } path) return;
        SettingsPanel.Visibility = Visibility.Collapsed;
        UpdateMainViews();
        await OpenWorkspaceAtPathAsync(path);
    }
}
