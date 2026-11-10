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
            var directory = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgExplorer");
            updates = new(new UpdateChecker(new HttpReleaseFeed(updateHttp, trust.ManifestUrl), trust, Version.Parse(version)),
                Path.Combine(directory, "update-settings.json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            UpdateVerificationException or CryptographicException or ArgumentException or FormatException or InvalidOperationException)
        {
            updateConfigurationError = "Update verification configuration is invalid; availability is unknown. Obtain a trusted app rebootstrap. Local use remains available. " + error.Message;
        }
        ShowUpdates();
        Loaded += async (_, _) =>
        {
            if (updates is null || !updates.CheckOnStartup) return;
            await RunUpdateCheck();
        };
        Closed += (_, _) => { updateLifetime.Cancel(); updateHttp.Dispose(); updateLifetime.Dispose(); };
    }

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await RunUpdateCheck();

    private async Task RunUpdateCheck()
    {
        if (updates is null || updateCheckPending) return;
        updateCheckPending = true;
        ShowUpdates();
        // HTTP/cryptographic work never owns the Workspace work gate or dispatcher.
        var lifetime = updateLifetime.Token;
        try { await Task.Run(() => updates.CheckAsync(lifetime)); }
        finally
        {
            updateCheckPending = false;
            if (!lifetime.IsCancellationRequested) ShowUpdates();
        }
    }

    private void ChangeStartupUpdateChecks(object sender, RoutedEventArgs e)
    {
        updates?.SetCheckOnStartup(StartupUpdateChecks.IsChecked == true);
        ShowUpdates();
    }

    private void ShowUpdates()
    {
        CheckForUpdatesButton.IsEnabled = updates is not null && !updateCheckPending;
        StartupUpdateChecks.IsEnabled = updates is not null;
        StartupUpdateChecks.IsChecked = updates?.CheckOnStartup ?? true;
        var message = updateCheckPending
            ? "Checking project-authorized updates… Local browsing and editing remain available."
            : updates?.Result.Message;
        UpdateSummary.Text = UpdateGuidance.Text = updateConfigurationError ??
            string.Join("\n", new[] { message, updates?.PreferenceError }.Where(text => !string.IsNullOrEmpty(text)));
    }
}
