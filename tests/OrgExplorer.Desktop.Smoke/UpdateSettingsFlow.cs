using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunUpdateSettingsFlow(string executable, string root)
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE")
            ?? throw new InvalidOperationException("Prepare an ephemeral embedded trust build using eng\\Run-CiLane.ps1 desktop. This scenario never bypasses production signature verification.");
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "trust.json")));
        var endpoint = new Uri(configuration.RootElement.GetProperty("manifestUrl").GetString()!);
        using var fixture = new SignedReleaseFixture();
        fixture.Key.ImportFromPem(File.ReadAllText(Path.Combine(fixtureDirectory, "ephemeral-private.pem")));
        using var server = new LocalReleaseServer(() => fixture.Sign(fixture.Manifest("9999.0.0")), endpoint.Port);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldResponse = held.Task;
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        using (var process = Launch(executable))
        {
            try
            {
                var window = Window(process);
                Click(window, "OpenSettings");
                Await(() => server.RequestArrived.Task.IsCompleted && Find(window, "UpdateGuidance").Current.Name.Contains("Checking"));
                var startup = (TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern);
                if (startup.Current.ToggleState != ToggleState.On)
                    throw new InvalidOperationException("Startup update checks were not enabled by default.");
                if (Find(window, "CheckForUpdates").Current.IsEnabled)
                    throw new InvalidOperationException("A duplicate manual check was enabled during the in-flight startup request.");
                Set(window, "WorkspacePath", workspace);
                Click(window, "OpenWorkspace");
                Await(() => Find(window, "Status").Current.Name.Contains("automatically converted"));
                OpenPerson(window, "casey@example.com", "alex@example.com");
                Set(window, "PreferredName", "Editable during update check");
                Await(() => Find(window, "Status").Current.Name == "Saved locally.");
                if (!Find(window, "UpdateGuidance").Current.Name.Contains("Checking"))
                    throw new InvalidOperationException("The controlled pending feed did not remain pending during the local save.");
                held.SetResult();
                Await(() => Find(window, "UpdateGuidance").Current.Name.Contains("Project-authorized update 9999.0.0 is available"));
                startup.Toggle();
                Await(() => startup.Current.ToggleState == ToggleState.Off);
                Close(process);
            }
            finally { held.TrySetResult(); StopApplication(process); }
        }

        server.HoldResponse = null;
        var beforeReopen = server.Requests;
        using var reopened = Launch(executable);
        try
        {
            var window = Window(reopened);
            Click(window, "OpenSettings");
            Await(() => Find(window, "UpdateGuidance").Current.Name.Contains("No update availability has been checked"));
            if (((TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != ToggleState.Off ||
                server.Requests != beforeReopen)
                throw new InvalidOperationException("The disabled startup preference did not survive the actual WPF reopen.");
            Click(window, "CheckForUpdates");
            Await(() => Find(window, "UpdateGuidance").Current.Name.Contains("Project-authorized update 9999.0.0 is available"));

            using var unknown = new SignedReleaseFixture();
            server.Response = () => unknown.Sign(unknown.Manifest("9999.0.0"), "unknown-downloaded-key");
            Click(window, "CheckForUpdates");
            AwaitUpdateFailure(window, "not an embedded trusted project key");
            server.Response = () => unknown.Sign(unknown.Manifest("9999.0.0"));
            Click(window, "CheckForUpdates");
            AwaitUpdateFailure(window, "project signature is invalid");
            server.Response = () =>
            {
                var manifest = fixture.Manifest("9999.0.0");
                manifest["channel"] = "beta";
                return fixture.Sign(manifest);
            };
            Click(window, "CheckForUpdates");
            AwaitUpdateFailure(window, "Unsupported update protocol");
            server.StatusCode = 429;
            Click(window, "CheckForUpdates");
            AwaitUpdateFailure(window, "rate limited");
            server.Dispose();
            Click(window, "CheckForUpdates");
            AwaitUpdateFailure(window, "offline");
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Await(() => Find(window, "WorkflowActivity").Current.Name == "Ready");
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertValue(window, "PreferredName", "Editable during update check");
            Set(window, "PreferredName", "Editable after update failure");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            var startup = (TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern);
            startup.Toggle();
            Await(() => startup.Current.ToggleState == ToggleState.On);
            Close(reopened);
        }
        finally { StopApplication(reopened); }

        using var attacker = new SignedReleaseFixture();
        using var startupFeed = new LocalReleaseServer(() => attacker.Sign(attacker.Manifest("9999.0.0")), endpoint.Port);
        using var rejectedStartup = Launch(executable);
        try
        {
            var window = Window(rejectedStartup);
            Await(() => !Find(window, "UpdateSummary").Current.IsOffscreen &&
                Find(window, "UpdateSummary").Current.Name.Contains("project signature is invalid"));
            Click(window, "OpenSettings");
            AwaitUpdateFailure(window, "project signature is invalid");
            if (((TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != ToggleState.On)
                throw new InvalidOperationException("Re-enabled startup checks did not survive reopen.");
            Close(rejectedStartup);
        }
        finally { StopApplication(rejectedStartup); }
    }

    private static void AwaitUpdateFailure(AutomationElement window, string expected)
    {
        Await(() => Find(window, "UpdateGuidance").Current.Name.Contains(expected, StringComparison.OrdinalIgnoreCase));
        var status = Find(window, "UpdateGuidance").Current.Name;
        if (!status.Contains("unknown", StringComparison.OrdinalIgnoreCase) || status.Contains("up to date", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("is available", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A failed update check fabricated availability: " + status);
        if (Find(window, "UpdateSummary").Current.Name != status)
            throw new InvalidOperationException("An update failure was hidden when Settings is closed.");
    }
}
