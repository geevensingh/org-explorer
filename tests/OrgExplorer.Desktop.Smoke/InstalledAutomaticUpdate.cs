using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunInstalledAutomaticUpdate(string[] args, string root)
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE")
            ?? throw new InvalidOperationException("The installed update requires an ephemeral trust/transport fixture.");
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "trust.json")));
        var endpoint = new Uri(configuration.RootElement.GetProperty("manifestUrl").GetString()!);
        using var fixture = new SignedReleaseFixture { Package = File.ReadAllBytes(Path.Combine(fixtureDirectory, "package.nupkg")) };
        fixture.Key.ImportFromPem(File.ReadAllText(Path.Combine(fixtureDirectory, "ephemeral-private.pem")));
        using var server = new LocalReleaseServer(() => fixture.Sign(fixture.Manifest(args[4])), endpoint.Port);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldResponse = held.Task;
        using var original = Launch(args[0]);
        try
        {
            var window = OpenInstalledWorkspace(original, args[0], args[1], args[3]);
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertInstallerWorkspace(window);
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Automatic update safe local writing");
            Set(window, "PersonNoteMarkdown", "Final installed-app writing before automatic restart.");
            held.TrySetResult();
            server.HoldResponse = null;
            Await(() => original.HasExited);
            AutomationElement? restartedWindow = null;
            Await(() =>
            {
                restartedWindow = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.NameProperty, $"Org explorer {args[4]} (Windows x64 / win)"));
                return restartedWindow is not null;
            });
            using var restarted = Process.GetProcessById(restartedWindow!.Current.ProcessId);
            Applications.Add(restarted);
            try
            {
                window = Window(restarted);
                ShowTechnicalSettings(window);
                Await(() => Find(window, "UpdateGuidance").Current.Name.Contains($"installation confirmed by running version {args[4]}"));
                if (Find(window, "WorkspacePath").Current.Name != Path.GetFullPath(args[1]))
                    throw new InvalidOperationException("The update restart did not display the original Workspace path.");
                SetSettingsVisible(window, false);
                OpenPerson(window, "casey@example.com", "alex@example.com");
                AssertInstallerWorkspace(window);
                SelectNote(window, "Automatic update safe local writing");
                SelectTab(window, "PersonNoteEditTab");
                AssertValue(window, "PersonNoteMarkdown", "Final installed-app writing before automatic restart.");
                SetSettingsVisible(window, true);
                Await(() => Find(window, "UpdateGuidance").Current.Name.Contains("up to date"));
                if (!Find(window, "CheckForUpdates").Current.IsEnabled)
                    throw new InvalidOperationException("Same installed version re-entered automatic download/restart.");
                SetSettingsVisible(window, false);
                Close(restarted);
            }
            finally { StopApplication(restarted); }
            Console.WriteLine("PASS: genuine installed full package authenticated, automatically applied, restarted at the same Workspace, and retained final WPF writing with no same-version loop.");
        }
        finally
        {
            held.TrySetResult();
            StopApplication(original);
        }
    }
}
