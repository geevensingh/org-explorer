using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunWorkspaceCompatibilityFlow(string executable, string root)
    {
        var workspace = Path.Combine(root, "current");
        WorkspaceFixtures.CopyCurrent(workspace);
        using var process = Launch(executable);
        try
        {
            var window = Window(process);
            Await(() => Status(window) == "Choose a Workspace.");
            AssertEverydayChrome(window, "No Workspace open");
            ShowTechnicalSettings(window);
            if (Find(window, "WorkspacePath").Current.Name != "No Workspace open")
                throw new InvalidOperationException("First-run Settings invented a current Workspace path.");
            if (!Find(window, "OpenWorkspace").Current.IsEnabled)
                throw new InvalidOperationException("First launch did not leave usable Workspace-opening controls.");
            Await(() => Find(window, "SettingsGuidance").Current.Name.Contains("Settings"));
            AssertNoFakeUpdater(window);
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            OpenPerson(window, "casey@example.com", "alex@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            Await(() => Find(window, "FavoriteCount").Current.Name == "Favorites: 3");
            if (Find(window, "ToggleFavorite").Current.Name != "Unmark favorite")
                throw new InvalidOperationException("Current Workspace lost favorite state.");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            AssertPhotoVisible(window);
            if (!Find(window, "DirectoryName").Current.Name.Contains("Avery Example"))
                throw new InvalidOperationException("Current profile lost its Directory name.");
            Click(window, "EditPreferredName");
            var entry = PreferredNameEntry(window);
            Set(entry, "PreferredName", "Indexed UI edit");
            Click(entry, "SavePreferredName");
            Await(() => Status(window) == "Saved locally.");
            AssertPreferredNameReading(window, "Indexed UI edit", "Edit");
            var pointer = Path.Combine(workspace, "workspace.json");
            var compatible = File.ReadAllBytes(pointer);
            var data = JsonNode.Parse(compatible)!;
            data["schemaVersion"] = 99;
            data["futureRequiredField"] = "Must not discard";
            File.WriteAllText(pointer, data.ToJsonString());
            var before = WorkspaceFixtures.Bytes(workspace);
            Await(() => Status(window).Contains("Upgrade"));
            var mutationState = "";
            try
            {
                Await(() =>
                {
                    var edit = window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "EditPreferredName"));
                    var favorite = window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "ToggleFavorite"));
                    mutationState = $"Preferred name: present={edit is not null}, enabled={edit?.Current.IsEnabled}; " +
                        $"Favorite: present={favorite is not null}, enabled={favorite?.Current.IsEnabled}.";
                    return edit is not null && favorite is not null &&
                        !edit.Current.IsEnabled && !favorite.Current.IsEnabled;
                });
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("Incompatible external Workspace did not present disabled mutation controls. " +
                    mutationState, error);
            }
            if (CurrentPersonEmail(window) != "avery@example.com" ||
                Find(window, "ProfileHeading").Current.Name != "Indexed UI edit")
                throw new InvalidOperationException("Incompatible external delivery discarded the last validated Person.");
            OpenImportView(window);
            if (Find(window, "ApplyImport").Current.IsEnabled)
                throw new InvalidOperationException("Incompatible external Workspace left Import application enabled.");
            ReturnToPeople(window);
            ShowTechnicalSettings(window);
            Await(() => Find(window, "SettingsGuidance").Current.Name.Contains("Upgrade"));
            AssertNoFakeUpdater(window);
            Click(window, "RetryWorkspace");
            Await(() => Status(window).Contains("Upgrade"));
            AssertWorkspaceBytes(workspace, before);
            File.WriteAllBytes(pointer, compatible);
            SetSettingsVisible(window, false);
            Await(() => Find(window, "EditPreferredName").Current.IsEnabled);
            Click(window, "EditPreferredName");
            entry = PreferredNameEntry(window);
            if (!Find(entry, "PreferredName").Current.IsEnabled || !Find(entry, "SavePreferredName").Current.IsEnabled)
                throw new InvalidOperationException("Validated Workspace did not restore explicit Preferred-name editing.");
            Click(entry, "CancelPreferredName");
            Close(process);
        }
        finally
        {
            StopApplication(process);
        }

        var legacyWorkspace = Path.Combine(root, "older-rejected");
        WorkspaceFixtures.CopyVersionOne(legacyWorkspace);
        var originals = WorkspaceFixtures.Bytes(legacyWorkspace);
        var directories = Directory.GetDirectories(legacyWorkspace, "*", SearchOption.AllDirectories).Order().ToArray();
        var recoveryWorkspace = Path.Combine(root, "supported-alternative");
        WorkspaceFixtures.CopyCurrent(recoveryWorkspace);
        using var recovery = Launch(executable);
        try
        {
            var window = Window(recovery);
            AwaitRestoredWorkspace(window, workspace, "avery@example.com");
            SetSettingsVisible(window, true);
            using (File.Open(Path.Combine(legacyWorkspace, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PickWorkspaceFolder(window, legacyWorkspace);
                Await(() => Status(window).Contains("does not migrate"));
                ShowTechnicalSettings(window);
                Await(() => Find(window, "SettingsGuidance").Current.Name.Contains("does not migrate"));
                SetSettingsVisible(window, false);
                var edit = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "EditPreferredName"));
                if (edit?.Current.IsEnabled == true || CurrentPersonEmail(window).Length != 0)
                    throw new InvalidOperationException("Older Workspace falsely loaded a writable profile.");
                OpenImportView(window);
                if (Find(window, "ApplyImport").Current.IsEnabled)
                    throw new InvalidOperationException("Older Workspace enabled Import application.");
                ReturnToPeople(window);
                AssertWorkspaceBytes(legacyWorkspace, originals);
            }
            SetSettingsVisible(window, true);
            Click(window, "RetryWorkspace");
            Await(() => Status(window).Contains("does not migrate"));
            AssertWorkspaceBytes(legacyWorkspace, originals);
            if (File.Exists(Path.Combine(legacyWorkspace, ".workspace.lock")) ||
                !directories.SequenceEqual(Directory.GetDirectories(legacyWorkspace, "*", SearchOption.AllDirectories).Order()))
                throw new InvalidOperationException("Older Workspace rejection mutated its directories or lock.");
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, recoveryWorkspace);
            AwaitRestoredWorkspace(window, recoveryWorkspace);
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
                AwaitRestoredWorkspace(window, workspace, "avery@example.com");
                OpenPerson(window, "casey@example.com", "alex@example.com");
                Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
                AssertProfileValue(window, "OfficeNumber", "0009C");
                AssertPreferredNameReading(window, "Ave", "Edit");
                Close(retry);
            }
            finally { StopApplication(retry); }
        }

        Directory.Move(workspace, moved);
        var invalid = Path.Combine(root, "invalid-workspace");
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "workspace.json"), "{invalid");
        var invalidBytes = WorkspaceFixtures.Bytes(invalid);
        using (var failedManual = Launch(executable))
        {
            try
            {
                var window = Window(failedManual);
                AwaitStartupRecovery(window, workspace);
                SetSettingsVisible(window, true);
                PickWorkspaceFolder(window, invalid);
                Await(() => Status(window).Contains("could not be opened"));
                AssertWorkspaceBytes(invalid, invalidBytes);
                Close(failedManual);
            }
            finally { StopApplication(failedManual); }
        }

        var alternate = Path.Combine(root, "alternate-workspace");
        WorkspaceFixtures.CopyCurrent(alternate);
        using (var choose = Launch(executable))
        {
            try
            {
                var window = Window(choose);
                AwaitStartupRecovery(window, workspace);
                if (Directory.Exists(workspace))
                    throw new InvalidOperationException("A failed manual attempt replaced the remembered Workspace.");
                SetSettingsVisible(window, true);
                PickWorkspaceFolder(window, alternate, "ChooseAnotherWorkspace");
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

    private static void AwaitRestoredWorkspace(AutomationElement window, string workspace, string expectedEmail = "",
        Action<Func<bool>>? awaitState = null)
    {
        WorkspaceRestoration.AssertRestored(() =>
        {
            var recovery = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceStartupStatus"));
            var viewRecovery = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ViewPreferenceStatus"));
            return new(Find(window, "WorkspaceName").Current.HelpText, Activity(window),
                Freshness(window), CurrentPersonEmail(window),
                viewRecovery?.Current.IsOffscreen == false ? false : recovery?.Current.IsOffscreen);
        }, Path.GetFullPath(workspace), awaitState ?? Await, expectedEmail);
    }

    private static void AwaitUnavailableView(AutomationElement window, string workspace)
    {
        Await(() => Find(window, "WorkspaceName").Current.HelpText == Path.GetFullPath(workspace) &&
            Activity(window) == "Ready" &&
            Freshness(window) == "Current validated local Workspace." &&
            CurrentPersonEmail(window).Length == 0 &&
            !Find(window, "WorkspaceStart").Current.IsOffscreen &&
            !Find(window, "ViewPreferenceStatus").Current.IsOffscreen &&
            Find(window, "ViewPreferenceStatus").Current.Name.Contains("unavailable or archived") &&
            Find(window, "WorkspaceStartGuidance").Current.Name.Contains("Favorites"));
        var startup = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceStartupStatus"));
        if (startup?.Current.IsOffscreen == false)
            throw new InvalidOperationException("A valid Workspace with unavailable selection falsely reported Workspace reopen failure.");
    }

    private static void AssertRestoredNote(AutomationElement window, string scope, string id, string title)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("The selected note did not expose its stable GUID.");
        Await(() => Find(window, scope + "NoteHeading").Current.HelpText == id &&
            Find(window, scope + "NoteHeading").Current.Name == title &&
            Find(window, scope + "NoteKind").Current.Name == "Other " + scope + " note");
        AssertNoteView(window, scope, editing: false);
    }

    private static string CurrentPersonEmail(AutomationElement window)
    {
        var email = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "Email"));
        if (email is not null) return email.Current.Name;
        if (Find(window, "WorkspaceStart").Current.IsOffscreen)
            throw new InvalidOperationException("Startup has neither a Person identity nor a purposeful starting view.");
        return "";
    }

    private static void AwaitStartupRecovery(AutomationElement window, string workspace)
    {
        Await(() => !Find(window, "WorkspaceStartupStatus").Current.IsOffscreen &&
            Find(window, "WorkspaceStartupStatus").Current.Name.Contains(workspace) &&
            Find(window, "WorkspaceStartupStatus").Current.Name.Contains("could not be reopened") &&
            Activity(window) == "Ready");
        if (!Find(window, "RetryStartupWorkspace").Current.IsEnabled ||
            !Find(window, "ChooseAnotherWorkspace").Current.IsEnabled ||
            CurrentPersonEmail(window).Length != 0 ||
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
