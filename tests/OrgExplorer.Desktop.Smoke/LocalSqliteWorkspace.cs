using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifySqliteCreationNavigation(Type windowType, string settings,
        Func<Window, string?> chooseFolder)
    {
        var previousOptIn = Environment.GetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES");
        Window? window = null;
        try
        {
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", "1");
            window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var legacy = Path.Combine(settings, "sqlite-navigation-legacy");
            WorkspaceFixtures.CopyCurrent(legacy);
            Invoke("OpenSettingsButton");
            ChooseCompiledWorkspace(window, legacy);
            PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
            if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Ordinary Workspace opening must return to browsing.");

            Invoke("OpenSettingsButton");
            if (Control<Button>("CreateLocalSqliteWorkspaceButton").Visibility != Visibility.Visible ||
                !Control<Button>("CreateLocalSqliteWorkspaceButton").IsEnabled)
                throw new InvalidOperationException("The compiled SQLite opt-in did not expose its Create action.");
            Invoke("CreateLocalSqliteWorkspaceButton");
            PumpUntil(() => Control<TextBlock>("StatusText").Text == "SQLite Workspace created and saved locally." &&
                Control<TextBlock>("WorkflowActivity").Text == "Ready");
            if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Collapsed ||
                Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Visible ||
                Control<FrameworkElement>("WorkspaceStartPanel").Visibility != Visibility.Visible)
                throw new InvalidOperationException(
                    "SQLite creation left Settings covering the browsing/start view required before Import navigation.");

            var workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            if (!workflow.IsWorkspaceCurrent || workflow.People.Count != 0)
                throw new InvalidOperationException("SQLite creation did not expose its committed empty Workspace.");
            Invoke("OpenSettingsButton");
            if (!Control<Button>("OpenImportButton").IsEnabled)
                throw new InvalidOperationException("SQLite creation did not leave the normal Import action enabled.");
            Invoke("OpenImportButton");
            if (Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Visible ||
                Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Collapsed ||
                !Control<TextBox>("ImportPath").IsEnabled)
                throw new InvalidOperationException("The normal Settings Import action did not reach a usable Import view after SQLite creation.");
            var import = Path.Combine(settings, "sqlite-navigation-import.json");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"91000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
                """);
            Control<TextBox>("ImportPath").Text = import;
            Invoke("PreviewButton");
            PumpUntil(() => Control<Button>("ApplyButton").IsEnabled);
            if (workflow.People.Count != 0 ||
                !Control<ListBox>("PreviewText").Items.Cast<string>().Any(text => text.Contains("avery@example.com")))
                throw new InvalidOperationException("The compiled SQLite Import preview lost its reviewed Person or accepted data before Apply.");
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("SQLite navigation verification must not show a window or create its native handle.");
            Console.WriteLine("PASS: compiled SQLite Create returns to the ordinary start view and normal Settings Import reaches a real reviewed preview without a visible window/native handle.");
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", previousOptIn);
        }
    }

    private static void RunLocalSqliteWorkspaceFlow(string executable, string root)
    {
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var previousOptIn = Environment.GetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES");
        var settingsDirectory = Path.Combine(root, "sqlite-app-settings");
        var oldWorkspace = Path.Combine(root, "sqlite-preserved-old");
        WorkspaceFixtures.CopyCurrent(oldWorkspace);
        var originals = WorkspaceFixtures.Bytes(oldWorkspace);
        var settings = new WorkspaceSettings(Path.Combine(settingsDirectory, "workspace-settings.json"));
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!settings.OpenWorkspace(seed, oldWorkspace))
                throw new InvalidOperationException("Could not prepare the existing startup Workspace: " + seed.Status);
        var import = Path.Combine(root, "sqlite-initial.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"91000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","producer":"Hosted SQLite journey","people":[
             {"email":"avery@example.com","displayName":"Avery Example","alias":"avery","officeNumber":"007B",
              "department":"Research","jobTitle":"Research engineer","officeCountry":"Canada",
              "managerEmail":"morgan@example.com"},
             {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null}]}
            """);
        try
        {
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settingsDirectory);
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", "1");
            string local;
            using (var process = Launch(executable))
            {
                try
                {
                    var window = Window(process);
                    AwaitRestoredWorkspace(window, oldWorkspace);
                    SetSettingsVisible(window, true);
                    Click(window, "CreateLocalSqliteWorkspace");
                    Await(() => Status(window) == "SQLite Workspace created and saved locally.");
                    local = new WorkspaceSettings(Path.Combine(settingsDirectory, "workspace-settings.json")).LastWorkspacePath
                        ?? throw new InvalidOperationException("SQLite creation did not remember its local Workspace.");
                    if (!local.StartsWith(Path.Combine(settingsDirectory, "LocalWorkspaces") + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("SQLite creation did not use app-managed local storage.");
                    OpenImportView(window);
                    Set(window, "ImportPath", import);
                    Click(window, "PreviewImport");
                    Await(() => Find(window, "ApplyImport").Current.IsEnabled);
                    using (var empty = ApplicationWorkflow.CreateFileBacked())
                        if (!empty.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) || empty.People.Count != 0)
                            throw new InvalidOperationException("Preview accepted the SQLite population before Apply.");
                    Click(window, "ApplyImport");
                    Await(() => Status(window) == "Import applied and saved locally.");
                    ReturnToPeople(window);
                    EnsurePeopleBrowsing(window);
                    var manager = Find(window, "People").FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "morgan@example.com"))
                        ?? throw new InvalidOperationException("SQLite Import did not expose the Manager in People.");
                    ((ExpandCollapsePattern)manager.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                    SelectTreePerson(window, "avery@example.com");
                    Await(() => CurrentPersonEmail(window) == "avery@example.com");
                    AssertProfileValue(window, "OfficeNumber", "007B");
                    AssertProfileValue(window, "Department", "Research");
                    Await(() => Find(window, "ProfileHeading").Current.Name == "Avery Example");
                    if (Find(window, "EditProfile").Current.IsEnabled ||
                        Find(window, "ToggleFavorite").Current.IsEnabled)
                        throw new InvalidOperationException("The incomplete SQLite path enabled unsupported ordinary edits.");
                    CaptureVisualEvidence(window, "sqlite-initial-import-browsing");
                    AssertWorkspaceBytes(oldWorkspace, originals);
                    Close(process);
                }
                finally { StopApplication(process); }
            }
            using var reopened = Launch(executable);
            try
            {
                var window = Window(reopened);
                AwaitRestoredWorkspace(window, local, "avery@example.com");
                AssertProfileValue(window, "OfficeNumber", "007B");
                AssertProfileValue(window, "OfficeCountry", "Canada");
                Click(window, "NavigateManager");
                Await(() => CurrentPersonEmail(window) == "morgan@example.com");
                AssertWorkspaceBytes(oldWorkspace, originals);
                Close(reopened);
            }
            finally { StopApplication(reopened); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", previousOptIn);
        }
    }
}
