using System.IO;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

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
            AwaitRestoredWorkspace(window, workspace, "casey@example.com");
            AssertSelectedPeopleRow(window, "casey@example.com");
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
            Set(entry, "PreferredName", "SQLite UI edit");
            Click(entry, "SavePreferredName");
            Await(() => Status(window) == "Saved locally.");
            AssertPreferredNameReading(window, "SQLite UI edit", "Edit");
            var unsupported = Path.Combine(root, "future-readable-format");
            WorkspaceFixtures.CopyVersion(99, unsupported);
            var before = WorkspaceFixtures.Bytes(unsupported);
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, unsupported);
            Await(() => Status(window).Contains("does not migrate"));
            SetSettingsVisible(window, false);
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
                    return edit?.Current.IsEnabled != true && favorite?.Current.IsEnabled != true;
                });
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("Unsupported readable-file Workspace did not present disabled mutation controls. " +
                    mutationState, error);
            }
            if (CurrentPersonEmail(window).Length != 0)
                throw new InvalidOperationException("Unsupported readable-file Workspace fabricated a Person.");
            OpenImportView(window);
            if (Find(window, "ApplyImport").Current.IsEnabled)
                throw new InvalidOperationException("Incompatible external Workspace left Import application enabled.");
            ReturnToPeople(window);
            ShowTechnicalSettings(window);
            Await(() => Find(window, "SettingsGuidance").Current.Name.Contains("does not migrate") &&
                Find(window, "SettingsGuidance").Current.Name.Contains("empty folder"));
            AssertNoFakeUpdater(window);
            Click(window, "RetryWorkspace");
            Await(() => Status(window).Contains("does not migrate"));
            AssertWorkspaceBytes(unsupported, before);
            PickWorkspaceFolder(window, workspace);
            SetSettingsVisible(window, false);
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertPreferredNameReading(window, "SQLite UI edit", "Edit");
            Click(window, "EditPreferredName");
            entry = PreferredNameEntry(window);
            if (!Find(entry, "PreferredName").Current.IsEnabled || !Find(entry, "SavePreferredName").Current.IsEnabled)
                throw new InvalidOperationException("Validated Workspace did not restore explicit Preferred-name editing.");
            Click(entry, "CancelPreferredName");
            ClosePublishedWorkspace(process, workspace);
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
            AwaitRestoredWorkspace(window, recoveryWorkspace, "casey@example.com");
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            ClosePublishedWorkspace(recovery, recoveryWorkspace);
        }
        finally
        {
            StopApplication(recovery);
        }
        RunStartupRecoveryFlow(executable, root, recoveryWorkspace);
        HarnessRuntime.Run("native-sqlite", () => RunLocalSqliteWorkspaceFlow(executable, root));
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
                ClosePublishedWorkspace(retry, workspace);
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
                AwaitRestoredWorkspace(window, alternate, "casey@example.com");
                ClosePublishedWorkspace(choose, alternate);
            }
            finally { StopApplication(choose); }
        }
        using var reopened = Launch(executable);
        try
        {
            var window = Window(reopened);
            AwaitRestoredWorkspace(window, alternate, "casey@example.com");
            OpenPerson(window, "casey@example.com", "alex@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            AssertPreferredNameReading(window, "Ave", "Edit");
            ClosePublishedWorkspace(reopened, alternate);
        }
        finally { StopApplication(reopened); }
    }

    private static void AwaitRestoredWorkspace(AutomationElement window, string workspace, string expectedEmail = "",
        Action<Func<bool>>? awaitState = null)
    {
        WorkspaceRestoration.AssertRestored(() => ObserveWorkspaceRestoration(window),
            Path.GetFullPath(workspace), awaitState ?? Await, expectedEmail);
    }

    private static WorkspaceRestoration.Observation ObserveWorkspaceRestoration(AutomationElement window)
    {
        var recovery = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceStartupStatus"));
        var viewRecovery = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ViewPreferenceStatus"));
        return new(Find(window, "WorkspaceName").Current.HelpText, Activity(window),
            Freshness(window), CurrentPersonEmail(window),
            viewRecovery?.Current.IsOffscreen == false ? false : recovery?.Current.IsOffscreen);
    }

    private static WorkspaceSettings ReadSmokeWorkspacePreferences()
    {
        var directory = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Remembered reopening requires the fixture's isolated app-settings directory.");
        return new(Path.Combine(directory, "workspace-settings.json"));
    }

    private static void AwaitRememberedWorkspace(AutomationElement window, string workspace,
        WorkspaceSettings preferences, Action<Func<bool>> awaitState)
    {
        WorkspaceRestoration.AssertRemembered(() => ObserveWorkspaceRestoration(window),
            Path.GetFullPath(workspace), preferences, awaitState);
    }

    private static void AwaitUnavailableView(AutomationElement window, string workspace, string expectedEmail)
    {
        Await(() => Find(window, "WorkspaceName").Current.HelpText == Path.GetFullPath(workspace) &&
            Activity(window) == "Ready" &&
            Freshness(window) == "Current validated local Workspace." &&
            CurrentPersonEmail(window) == expectedEmail &&
            !IsVisible(window, "WorkspaceStart") &&
            !Find(window, "ViewPreferenceStatus").Current.IsOffscreen &&
            Find(window, "ViewPreferenceStatus").Current.Name.Contains("unavailable or archived"));
        AssertSelectedPeopleRow(window, expectedEmail);
        var startup = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceStartupStatus"));
        if (startup?.Current.IsOffscreen == false)
            throw new InvalidOperationException("A valid Workspace with unavailable selection falsely reported Workspace reopen failure.");
    }

    private static void AssertAutomaticImportContext(AutomationElement window, string preferences)
    {
        Await(() => IsVisible(window, "ImportPath") && IsVisible(window, "BackToPeople") && !IsVisible(window, "People"));
        var settings = new WorkspaceSettings(preferences);
        if (settings.PreferenceError is { } error) throw new InvalidOperationException(error);
        if (settings.GetLastView(Find(window, "WorkspaceName").Current.HelpText) is not null)
            throw new InvalidOperationException("Automatic Import selection published a remembered destination.");
    }

    private sealed class NativeHeaderFailureObservation
    {
        public bool? TreeOffscreen { get; set; }
        public bool? RowFound { get; set; }
        public bool? RowOffscreen { get; set; }
        public bool? Selected { get; set; }
        public bool? HeaderFound { get; set; }
        public bool? HeaderOffscreen { get; set; }
        public bool? HeaderNameEmpty { get; set; }
        public string? Viewport { get; set; }
        public string? Header { get; set; }
        public bool? ClickPointProviderReturned { get; set; }
        public string? ClickPoint { get; set; }
        public string? ClickViewport { get; set; }
        public string? FailedCondition { get; set; }
    }

    private static void AssertSelectedPeopleRow(AutomationElement window, string email, params string[] ancestors)
    {
        Await(() => CurrentPersonEmail(window) == email && IsVisible(window, "Email") && IsVisible(window, "EditProfile"));
        var openedOverlay = IsVisible(window, "OpenPeople") && !IsVisible(window, "PeopleNavigation");
        if (openedOverlay) Click(window, "OpenPeople");
        AutomationElement? observedTree = null;
        var last = new NativeHeaderFailureObservation();
        try { ControlReadiness.AwaitSelectedHeader(() =>
        {
            last = new() { FailedCondition = "tree-discovery" };
            var tree = observedTree = Find(window, "People");
            last.FailedCondition = "row-discovery";
            var row = tree.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, email));
            last.RowFound = row is not null;
            last.FailedCondition = "tree-offscreen";
            var treeOffscreen = tree.Current.IsOffscreen;
            last.TreeOffscreen = treeOffscreen;
            if (treeOffscreen) return null;
            last.FailedCondition = "row-missing";
            if (row is null) return null;
            last.FailedCondition = "row-offscreen-read";
            var rowOffscreen = row.Current.IsOffscreen;
            last.RowOffscreen = rowOffscreen;
            last.FailedCondition = "row-not-selected";
            var selected = ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;
            last.Selected = selected;
            if (selected) last.FailedCondition = "header-discovery";
            var header = selected ? row.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)) : null;
            if (selected)
            {
                last.HeaderFound = header is not null;
                last.FailedCondition = header is null ? "header-missing" : "header-observation";
            }
            return new ControlReadiness.SelectedRowHeader<AutomationElement>(rowOffscreen, selected, header);
        }, text =>
        {
            last.FailedCondition = "header-offscreen";
            var headerOffscreen = text.Current.IsOffscreen;
            last.HeaderOffscreen = headerOffscreen;
            if (headerOffscreen) return false;
            last.FailedCondition = "header-name-empty";
            var nameEmpty = string.IsNullOrEmpty(text.Current.Name);
            last.HeaderNameEmpty = nameEmpty;
            if (nameEmpty) return false;
            var tree = observedTree!;
            last.FailedCondition = "viewport-read";
            var viewport = tree.Current.BoundingRectangle;
            last.Viewport = viewport.ToString();
            last.FailedCondition = "header-geometry-read";
            var header = text.Current.BoundingRectangle;
            last.Header = header.ToString();
            last.FailedCondition = "header-empty";
            if (header.IsEmpty) return false;
            last.FailedCondition = "header-left-before-viewport";
            if (!(header.Left >= viewport.Left)) return false;
            last.FailedCondition = "header-left-outside-viewport";
            if (!(header.Left < viewport.Right)) return false;
            last.FailedCondition = "header-top-before-viewport";
            if (!(header.Top >= viewport.Top)) return false;
            last.FailedCondition = "header-bottom-after-viewport";
            if (!(header.Bottom <= viewport.Bottom)) return false;
            last.FailedCondition = "click-point-provider-false";
            var clickable = text.TryGetClickablePoint(out var point);
            last.ClickPointProviderReturned = clickable;
            last.ClickPoint = point.ToString();
            if (!clickable) return false;
            last.FailedCondition = "click-viewport-read";
            var clickViewport = tree.Current.BoundingRectangle;
            last.ClickViewport = clickViewport.ToString();
            last.FailedCondition = "click-point-outside-viewport";
            if (!clickViewport.Contains(point)) return false;
            last.FailedCondition = null;
            return true;
        }, Await); }
        catch (TimeoutException)
        {
            HarnessRuntime.ReportDiagnostic("DEBUG-136-NATIVE-HEADER " + System.Text.Json.JsonSerializer.Serialize(last));
            throw;
        }
        foreach (var emailOfAncestor in ancestors)
        {
            var ancestor = Find(window, "People").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, emailOfAncestor));
            if (ancestor is null ||
                ((ExpandCollapsePattern)ancestor.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                throw new InvalidOperationException("Automatic selection did not expand its reporting ancestors.");
        }
        if (openedOverlay) Click(window, "ClosePeople");
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
