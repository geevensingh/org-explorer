using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunWorkspaceCompatibilityFlow(string executable, string root)
    {
        var workspace = Path.Combine(root, "legacy");
        WorkspaceFixtures.CopyVersionOne(workspace);
        using var process = Launch(executable);
        try
        {
            var window = Window(process);
            Await(() => Find(window, "Status").Current.Name == "Choose a Workspace.");
            AssertEverydayChrome(window, "No Workspace open");
            SetSettingsVisible(window, true);
            AssertValue(window, "WorkspacePath", "");
            if (!Find(window, "OpenWorkspace").Current.IsEnabled)
                throw new InvalidOperationException("First launch did not leave usable Workspace-opening controls.");
            Await(() => Find(window, "SettingsGuidance").Current.Name.Contains("Settings"));
            AssertNoFakeUpdater(window);
            SetSettingsVisible(window, false);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Await(() => Find(window, "Status").Current.Name.Contains("automatically converted"));
            OpenPerson(window, "casey@example.com", "alex@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            Await(() => Find(window, "FavoriteCount").Current.Name == "Favorites: 3");
            if (!Find(window, "FavoriteStatus").Current.Name.Contains("Marked favorite"))
                throw new InvalidOperationException("Supported conversion lost favorite state.");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            AssertPhotoVisible(window);
            if (!Find(window, "DirectoryName").Current.Name.Contains("Avery Example"))
                throw new InvalidOperationException("Converted profile lost its Directory name.");
            Click(window, "EditPreferredName");
            Set(window, "PreferredName", "Migration UI edit");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            var pointer = Path.Combine(workspace, "workspace.json");
            var compatible = File.ReadAllBytes(pointer);
            var data = JsonNode.Parse(compatible)!;
            data["schemaVersion"] = 99;
            data["futureRequiredField"] = "Must not discard";
            File.WriteAllText(pointer, data.ToJsonString());
            var before = WorkspaceFixtures.Bytes(workspace);
            Await(() => Find(window, "Status").Current.Name.Contains("Upgrade"));
            if (Find(window, "PreferredName").Current.IsEnabled ||
                Find(window, "ToggleFavorite").Current.IsEnabled)
                throw new InvalidOperationException("Incompatible external Workspace left mutation controls enabled.");
            OpenImportView(window);
            if (Find(window, "ApplyImport").Current.IsEnabled)
                throw new InvalidOperationException("Incompatible external Workspace left Import application enabled.");
            ReturnToPeople(window);
            SetSettingsVisible(window, true);
            Await(() => Find(window, "SettingsGuidance").Current.Name.Contains("Upgrade"));
            AssertNoFakeUpdater(window);
            Click(window, "RetryWorkspace");
            Await(() => Find(window, "Status").Current.Name.Contains("Upgrade"));
            AssertWorkspaceBytes(workspace, before);
            File.WriteAllBytes(pointer, compatible);
            SetSettingsVisible(window, false);
            Await(() => Find(window, "PreferredName").Current.IsEnabled);
            Close(process);
        }
        finally
        {
            StopApplication(process);
        }

        var recoveryWorkspace = Path.Combine(root, "migration-recovery");
        WorkspaceFixtures.CopyVersionOne(recoveryWorkspace);
        var originals = WorkspaceFixtures.Bytes(recoveryWorkspace);
        using var recovery = Launch(executable);
        try
        {
            var window = Window(recovery);
            AwaitRestoredWorkspace(window, workspace);
            Set(window, "WorkspacePath", recoveryWorkspace);
            using (File.Open(Path.Combine(recoveryWorkspace, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(window, "OpenWorkspace");
                Await(() => Find(window, "Status").Current.Name.Contains("conversion failed"));
                SetSettingsVisible(window, true);
                Await(() => Find(window, "SettingsGuidance").Current.Name.Contains("Recoverable originals"));
                SetSettingsVisible(window, false);
                if (Find(window, "EditPreferredName").Current.IsEnabled)
                    throw new InvalidOperationException("Failed migration falsely loaded a writable profile.");
                OpenImportView(window);
                if (Find(window, "ApplyImport").Current.IsEnabled)
                    throw new InvalidOperationException("Failed migration enabled Import application.");
                ReturnToPeople(window);
                foreach (var (path, bytes) in originals)
                    if (!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(recoveryWorkspace, path))))
                        throw new InvalidOperationException("Failed migration replaced original " + path);
            }
            SetSettingsVisible(window, true);
            Click(window, "RetryWorkspace");
            Await(() => Find(window, "Status").Current.Name.Contains("automatically converted"));
            SetSettingsVisible(window, false);
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            Close(recovery);
        }
        finally
        {
            StopApplication(recovery);
        }
        RunStartupRecoveryFlow(executable, root, recoveryWorkspace);
    }

    private static void RunStartupRecoveryFlow(string executable, string root, string workspace)
    {
        var moved = Path.Combine(root, "temporarily-moved");
        Directory.Move(workspace, moved);
        using (var retry = Launch(executable))
        {
            try
            {
                var window = Window(retry);
                AwaitStartupRecovery(window, workspace);
                if (Directory.Exists(workspace))
                    throw new InvalidOperationException("Startup recreated a missing Workspace.");
                Directory.Move(moved, workspace);
                Click(window, "RetryStartupWorkspace");
                AwaitRestoredWorkspace(window, workspace);
                OpenPerson(window, "casey@example.com", "alex@example.com");
                Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
                AssertProfileValue(window, "OfficeNumber", "0009C");
                AssertPreferredNameReading(window, "Ave", "Edit");
                Close(retry);
            }
            finally { StopApplication(retry); }
        }

        Directory.Move(workspace, moved);
        var invalid = Path.Combine(root, "not-a-folder");
        File.WriteAllText(invalid, "Not a Workspace directory.");
        using (var failedManual = Launch(executable))
        {
            try
            {
                var window = Window(failedManual);
                AwaitStartupRecovery(window, workspace);
                Set(window, "WorkspacePath", invalid);
                Click(window, "OpenWorkspace");
                Await(() => Find(window, "Status").Current.Name.Contains("could not be opened"));
                Close(failedManual);
            }
            finally { StopApplication(failedManual); }
        }

        var alternate = Path.Combine(root, "alternate-workspace");
        WorkspaceFixtures.CopyVersionOne(alternate);
        using (var choose = Launch(executable))
        {
            try
            {
                var window = Window(choose);
                AwaitStartupRecovery(window, workspace);
                if (Directory.Exists(workspace))
                    throw new InvalidOperationException("A failed manual attempt replaced the remembered Workspace.");
                Set(window, "WorkspacePath", alternate);
                Click(window, "OpenWorkspace");
                AwaitRestoredWorkspace(window, alternate);
                Close(choose);
            }
            finally { StopApplication(choose); }
        }
        using var reopened = Launch(executable);
        try
        {
            var window = Window(reopened);
            AwaitRestoredWorkspace(window, alternate);
            OpenPerson(window, "casey@example.com", "alex@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            AssertPreferredNameReading(window, "Ave", "Edit");
            Close(reopened);
        }
        finally { StopApplication(reopened); }
    }

    private static void AwaitRestoredWorkspace(AutomationElement window, string workspace)
    {
        SetSettingsVisible(window, false);
        WorkspaceRestoration.AssertRestored(() =>
        {
            var recovery = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceStartupStatus"));
            return new(Find(window, "WorkspaceName").Current.HelpText, Find(window, "WorkflowActivity").Current.Name,
                Find(window, "WorkspaceFreshness").Current.Name, Find(window, "Email").Current.Name,
                recovery?.Current.IsOffscreen);
        }, Path.GetFullPath(workspace), Await);
    }

    private static void AwaitStartupRecovery(AutomationElement window, string workspace)
    {
        Await(() => !Find(window, "WorkspaceStartupStatus").Current.IsOffscreen &&
            Find(window, "WorkspaceStartupStatus").Current.Name.Contains(workspace) &&
            Find(window, "WorkspaceStartupStatus").Current.Name.Contains("could not be reopened") &&
            Find(window, "WorkflowActivity").Current.Name == "Ready");
        if (!Find(window, "RetryStartupWorkspace").Current.IsEnabled ||
            !Find(window, "ChooseAnotherWorkspace").Current.IsEnabled ||
            Find(window, "Email").Current.Name.Length != 0 ||
            Find(window, "People").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)) is not null)
            throw new InvalidOperationException("Failed startup did not remain empty with actionable recovery controls.");
    }

    private static void AssertNoFakeUpdater(AutomationElement window)
    {
        _ = Find(window, "CheckForUpdates");
        if (string.IsNullOrWhiteSpace(Find(window, "UpdateGuidance").Current.Name) ||
            Find(window, "UpdateGuidance").Current.Name.Contains("up to date", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Settings fabricated an up-to-date result without an available verified feed.");
    }

    private static void AssertWorkspaceBytes(string root, Dictionary<string, byte[]> expected)
    {
        var actual = WorkspaceFixtures.Bytes(root);
        if (!expected.Keys.Order().SequenceEqual(actual.Keys.Order()))
            throw new InvalidOperationException("Incompatible Workspace created or removed files.");
        foreach (var (path, bytes) in expected)
            if (!bytes.AsSpan().SequenceEqual(actual[path]))
                throw new InvalidOperationException("Incompatible Workspace wrote " + path);
    }
}
