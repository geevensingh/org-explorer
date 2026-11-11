using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using OrgExplorer.Workflow.Updates;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private UpdateSettings? updates;
    private readonly HttpClient updateHttp = new();
    private readonly CancellationTokenSource updateLifetime = new();
    private string? updateConfigurationError;
    private bool updateCheckPending;
    private bool manualUpdateCheck;
    private bool showInstallProgress;
    private bool updateHandoffPending;
    private UpdateInstaller? updateInstaller;
    private UpdateRestartJournal? updateJournal;
    private Version? installedVersion;
    private string? updateRecoveryError;

    private void InitializeUpdates()
    {
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("OrgExplorer.UpdateTrust.json")
                ?? throw new IOException("Embedded update trust is missing.");
            using var reader = new StreamReader(resource);
            var trust = UpdateTrust.Parse(reader.ReadToEnd());
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+')[0];
            var directory = SettingsDirectory;
            installedVersion = Version.Parse(version);
            updateJournal = new(Path.Combine(directory, "update-attempt.json"));
            updateInstaller = new(UpdateEffects.Transport(updateHttp), UpdateEffects.Restart(updateJournal),
                Path.Combine(directory, "staged-updates"), installedVersion);
            updateInstaller.Changed += (_, _) =>
            {
                if (!updateLifetime.IsCancellationRequested) _ = Dispatcher.BeginInvoke(ShowUpdates);
            };
            updates = new(new UpdateChecker(new HttpReleaseFeed(updateHttp, trust.ManifestUrl), trust, installedVersion),
                Path.Combine(directory, "update-settings.json"), async (release, token) =>
                {
                    if (!manualUpdateCheck && !updateJournal.AllowAutomatic(release)) return;
                    showInstallProgress = true;
                    await updateInstaller.InstallAsync(release, AtSafeUpdatePoint, token);
                });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            UpdateVerificationException or CryptographicException or ArgumentException or FormatException or InvalidOperationException)
        {
            updateConfigurationError = "Update verification configuration is invalid; availability is unknown. Obtain a trusted app rebootstrap. Local use remains available. " + error.Message;
        }
        ShowUpdates();
        Loaded += async (_, _) =>
        {
            await RestoreStartupWorkspaceAsync();
            ShowUpdates();
            if (updates is null || !updates.CheckOnStartup) return;
            await RunUpdateCheck(manual: false);
        };
        Closed += (_, _) => { updateLifetime.Cancel(); updateHttp.Dispose(); updateLifetime.Dispose(); };
    }

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await RunUpdateCheck(manual: true);

    private async Task RunUpdateCheck(bool manual)
    {
        if (updates is null || updateCheckPending) return;
        updateCheckPending = true;
        manualUpdateCheck = manual;
        showInstallProgress = false;
        ShowUpdates();
        // HTTP/cryptographic work never owns the Workspace work gate or dispatcher.
        var lifetime = updateLifetime.Token;
        try { await Task.Run(() => updates.CheckAsync(lifetime)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            updateCheckPending = false;
            if (!lifetime.IsCancellationRequested) ShowUpdates();
        }
        if (updateInstaller?.Status == UpdateInstallStatus.Restarting && !lifetime.IsCancellationRequested) Close();
    }

    private async Task<bool> AtSafeUpdatePoint(Action<string?> restart)
    {
        return await Dispatcher.InvokeAsync(async () =>
        {
            updateHandoffPending = true;
            SetUpdateWorkEnabled(false);
            var accepted = false;
            try
            {
                await WaitForLocalWork();
                if (workflow.LocalSaveError is not null || HasUnsavedRelationship) return false;
                await FlushProfileDraft();
                if (unsavedAttributes.Count != 0 ||
                    !await PersonNotes.FlushPendingDraftAsync() || !await TeamNotes.FlushPendingDraftAsync()) return false;
                if (!TryLeaveLocalEdits()) return false;
                accepted = await Task.Run(() => workflow.RestartWhenSafe(restart));
                return accepted;
            }
            finally
            {
                updateHandoffPending = false;
                if (!accepted) SetUpdateWorkEnabled(true);
                ShowStatus();
            }
        }).Task.Unwrap();
    }

    private void SetUpdateWorkEnabled(bool enabled)
    {
        ExplorerPanel.IsEnabled = WorkspaceControls.IsEnabled = WorkspaceStartupPanel.IsEnabled =
            ImportControls.IsEnabled = ImportPreviewPanel.IsEnabled = enabled;
    }

    private void ChangeStartupUpdateChecks(object sender, RoutedEventArgs e)
    {
        // State events also fire when restoring the accepted setting, including after a failed save.
        if (updates is null || StartupUpdateChecks.IsChecked == updates.CheckOnStartup) return;
        updates.SetCheckOnStartup(StartupUpdateChecks.IsChecked == true);
        ShowUpdates();
    }

    private void ShowUpdates()
    {
        CheckForUpdatesButton.IsEnabled = updates is not null && !updateCheckPending;
        StartupUpdateChecks.IsEnabled = updates is not null;
        StartupUpdateChecks.IsChecked = updates?.CheckOnStartup ?? true;
        StartupUpdateStatus.Text = updates?.StartupPreferenceGuidance ?? "Startup update preference is unavailable.";
        var message = showInstallProgress ? updateInstaller?.Message : updateCheckPending
            ? "Checking project-authorized updates… Local browsing and editing remain available."
            : updates?.Result.Message;
        UpdateSummary.Text = UpdateGuidance.Text = updateConfigurationError ??
            string.Join("\n", new[] { message, updates?.PreferenceError, updateRecoveryError, updateJournal?.Message }.Where(text => !string.IsNullOrEmpty(text)));
    }
}
