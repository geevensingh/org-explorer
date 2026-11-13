using System.IO;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

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
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "package.nupkg"), fixture.Package);
        using var server = new LocalReleaseServer(() => fixture.Sign(fixture.Manifest("9999.0.0")), endpoint.Port);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldResponse = held.Task;
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyCurrent(workspace);
        using (var process = Launch(executable))
        {
            try
            {
                var window = Window(process);
                ShowTechnicalSettings(window);
                Await(() => server.RequestArrived.Task.IsCompleted && Find(window, "UpdateGuidance").Current.Name.Contains("Checking"));
                var startup = (TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern);
                if (startup.Current.ToggleState != ToggleState.On)
                    throw new InvalidOperationException("Startup update checks were not enabled by default.");
                if (Find(window, "CheckForUpdates").Current.IsEnabled)
                    throw new InvalidOperationException("A duplicate manual check was enabled during the in-flight startup request.");
                PickWorkspaceFolder(window, workspace);
                AwaitRestoredWorkspace(window, workspace);
                OpenPerson(window, "casey@example.com", "alex@example.com");
                Click(window, "EditPreferredName");
                Set(window, "PreferredName", "Editable during update check");
                Await(() => Status(window) == "Saved locally.");
                SetSettingsVisible(window, true);
                if (!Find(window, "UpdateGuidance").Current.Name.Contains("Checking"))
                    throw new InvalidOperationException("The controlled pending feed did not remain pending during the local save.");
                held.SetResult();
                AwaitPackageVerificationFailure(window);
                startup.Toggle();
                AwaitStartupPreference(window, ToggleState.Off);
                SetSettingsVisible(window, false);
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
            ShowTechnicalSettings(window);
            AwaitStartupPreference(window, ToggleState.Off);
            Await(() => Find(window, "UpdateGuidance").Current.Name.Contains("No update availability has been checked"));
            if (((TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != ToggleState.Off ||
                server.Requests != beforeReopen)
                throw new InvalidOperationException("The disabled startup preference did not survive the actual WPF reopen.");
            Click(window, "CheckForUpdates");
            AwaitPackageVerificationFailure(window);
            AssertSafeUpdateDrafts(window, workspace, fixtureDirectory, fixture, server);

            using var unknown = new SignedReleaseFixture();
            server.Response = () => unknown.Sign(unknown.Manifest("9999.0.0"), "unknown-downloaded-key");
            SetSettingsVisible(window, true);
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
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertPreferredNameReading(window, "Editable during update check", "Edit");
            Click(window, "EditPreferredName");
            Set(window, "PreferredName", "Editable after update failure");
            Await(() => Activity(window) == "Ready" &&
                Status(window) ==
                    "Saved locally.\nStorage reclamation deferred for preserved unretired transactions.");
            using (var saved = ApplicationWorkflow.CreateFileBacked())
            {
                if (!saved.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly) ||
                    saved.GetRelationships("avery@example.com")!.Person.PreferredName != "Editable after update failure" ||
                    !saved.CanSafelyClose)
                    throw new InvalidOperationException("The post-update-failure edit was not durably saved: " + saved.Status);
            }
            SetSettingsVisible(window, true);
            var startup = (TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern);
            startup.Toggle();
            AwaitStartupPreference(window, ToggleState.On);
            SetSettingsVisible(window, false);
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
            ShowTechnicalSettings(window);
            AwaitUpdateFailure(window, "project signature is invalid");
            if (((TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != ToggleState.On)
                throw new InvalidOperationException("Re-enabled startup checks did not survive reopen.");
            SetSettingsVisible(window, false);
            Close(rejectedStartup);
        }
        finally { StopApplication(rejectedStartup); }
    }

    private static void AwaitStartupPreference(AutomationElement window, ToggleState expected)
    {
        var guidance = expected == ToggleState.On ? "Startup update checks are enabled." : "Startup update checks are disabled.";
        Await(() => ((TogglePattern)Find(window, "StartupUpdateChecks").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == expected &&
            Find(window, "StartupUpdateStatus").Current.Name == guidance);
    }

    private static void AwaitPackageVerificationFailure(AutomationElement window)
    {
        Await(() => Find(window, "CheckForUpdates").Current.IsEnabled &&
            Find(window, "UpdateGuidance").Current.Name.Contains("VerificationFailed"));
        var message = Find(window, "UpdateGuidance").Current.Name;
        if (!message.Contains("No update is confirmed installed"))
            throw new InvalidOperationException("Automatic package consumption claimed an installation after invalid Velopack bytes.");
    }

    private static void AssertSafeUpdateDrafts(AutomationElement window, string workspace, string fixtureDirectory,
        SignedReleaseFixture fixture, LocalReleaseServer server)
    {
        SetSettingsVisible(window, true);
        PickWorkspaceFolder(window, workspace);
        AwaitRestoredWorkspace(window, workspace);
        OpenPerson(window, "casey@example.com", "alex@example.com");
        Click(window, "NewPersonNote");
        Set(window, "PersonNoteTitle", "Automatic update writing");
        Set(window, "PersonNoteMarkdown", "Durable draft before update");
        AwaitLocalWorkIdle(window);
        var noteFile = Directory.GetFiles(Path.Combine(workspace, "notes"), "*.md")
            .Single(path => File.ReadAllText(path).Contains("Durable draft before update"));
        var handoff = Path.Combine(fixtureDirectory, "handoff.json");
        File.Delete(handoff);
        fixture.Package = ControlledFullPackage("9999.0.0");
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "package.nupkg"), fixture.Package);
        using (File.Open(noteFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Set(window, "PersonNoteMarkdown", "Retained failing update draft");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
            SetSettingsVisible(window, true);
            Click(window, "CheckForUpdates");
            Await(() => Find(window, "UpdateGuidance").Current.Name.Contains("Automatic update paused"));
            SetSettingsVisible(window, false);
            AssertValue(window, "PersonNoteMarkdown", "Retained failing update draft");
            if (File.Exists(handoff))
                throw new InvalidOperationException("An unsaved real WPF draft reached the installer boundary.");
        }
        Click(window, "RetryPersonNoteSave");
        AwaitLocalWorkIdle(window);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldResponse = held.Task;
        SetSettingsVisible(window, true);
        Click(window, "CheckForUpdates");
        Await(() => Find(window, "UpdateGuidance").Current.Name.Contains("Checking"));
        SetSettingsVisible(window, false);
        Set(window, "PersonNoteMarkdown", "Final private writing flushed before update");
        SetSettingsVisible(window, true);
        held.TrySetResult();
        server.HoldResponse = null;
        try { Await(() => Find(window, "UpdateGuidance").Current.Name.Contains("Controlled installer process boundary")); }
        catch (TimeoutException error)
        {
            string State(string id) => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id))?.Current.Name ?? "<not exposed>";
            string durable;
            try
            {
                durable = File.ReadAllText(noteFile).Contains("Final private writing flushed before update", StringComparison.Ordinal)
                    ? "final writing saved" : "final writing absent";
            }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException)
            {
                durable = "note could not be read: " + readError.Message;
            }
            throw new TimeoutException("The update did not reach its controlled installer boundary. " +
                $"Update: {State("UpdateGuidance")}; activity: {State("WorkflowActivity")}; status: {State("Status")}; " +
                $"Person note: {State("PersonNoteStatus")}; selected note: {State("PersonNoteKind")}; " +
                $"durable note: {durable}; handoff present: {File.Exists(handoff)}.", error);
        }
        SetSettingsVisible(window, false);
        AssertValue(window, "PersonNoteMarkdown", "Final private writing flushed before update");
        using var request = JsonDocument.Parse(File.ReadAllText(handoff));
        if (request.RootElement.GetProperty("workspace").GetString() != workspace ||
            request.RootElement.GetProperty("version").GetString() != "9999.0.0")
            throw new InvalidOperationException("The update handoff lost the active Workspace or exact authorized version.");
        var expectedHash = request.RootElement.GetProperty("workspaceFiles")
            .GetProperty(Path.GetRelativePath(workspace, noteFile)).GetString();
        if (!File.ReadAllText(noteFile).Contains("Final private writing flushed before update") ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(noteFile))) != expectedHash)
            throw new InvalidOperationException("The actual installer handoff preceded durable WPF draft publication.");
    }

    private static byte[] ControlledFullPackage(string version)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("geevensingh.OrgExplorer.nuspec").Open(), Encoding.UTF8))
                writer.Write($"""
                    <?xml version="1.0"?>
                    <package><metadata><id>geevensingh.OrgExplorer</id><version>{version}</version>
                    <authors>Fixture</authors><description>Never installed in desktop smoke</description></metadata></package>
                    """);
            using var executable = zip.CreateEntry("lib/net45/OrgExplorer.Desktop.exe").Open();
            executable.Write("Never executed."u8);
        }
        return output.ToArray();
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
