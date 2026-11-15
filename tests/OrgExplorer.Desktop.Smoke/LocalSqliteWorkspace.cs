using System.IO;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
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
