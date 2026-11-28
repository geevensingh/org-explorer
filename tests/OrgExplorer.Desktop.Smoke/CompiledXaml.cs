using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static readonly Queue<string?> CompiledWorkspaceChoices = new();
    private static readonly (string Email, string DirectoryName, string? PreferredName, bool Archived, bool ArchivedManager, string Category)[] CompactIdentityStateCases =
    [
        ("header-directory@example.com", "Avery\r\nExample", null, false, false, "Employee"),
        ("header-preferred@example.com", "Preferred Directory", "Preferred\r\nPerson", false, false, "Employee"),
        ("header-archived@example.com", "Archived Example", null, true, false, "Employee"),
        ("header-archived-name@example.com", "Archived Directory", "Archived\r\nPreferred", true, true, "Employee"),
        ("V-header@example.com", "Vendor\r\nExample", null, false, false, "Vendor"),
        ("v-header-archived@example.com", "Archived Vendor", null, true, true, "Vendor")
    ];

    private static JsonArray CompactIdentityFixturePeople()
    {
        var people = new JsonArray(new JsonObject
        {
            ["email"] = "header-manager@example.com", ["displayName"] = "ZZZ Header Manager", ["managerEmail"] = null
        });
        people.Add(new JsonObject
        {
            ["email"] = "header-archived-manager@example.com", ["displayName"] = "ZZZ Archived Header Manager",
            ["managerEmail"] = null, ["archived"] = true
        });
        foreach (var state in CompactIdentityStateCases)
            people.Add(new JsonObject
            {
                ["email"] = state.Email, ["displayName"] = state.DirectoryName, ["preferredName"] = state.PreferredName,
                ["archived"] = state.Archived,
                ["managerEmail"] = state.ArchivedManager ? "header-archived-manager@example.com" : "header-manager@example.com",
                ["jobTitle"] = "Engineer", ["department"] = "Research", ["officeBuilding"] = "Juniper",
                ["officeNumber"] = "009C", ["officeState"] = "Ontario", ["officeCountry"] = "Canada"
            });
        return people;
    }

    private static int VerifyCompiledXaml(string executable, bool verifyProfileRefresh = false, bool verifyUpdateYield = false,
        bool verifyLegacySaveFeedback = false, bool verifySnapshotAdoption = false, bool verifyExit = false,
        bool verifyExitStartup = false, bool verifyProfileNoteRefresh = false, bool verifyPreferredNameRetention = false,
        bool verifyPreferredSnapshotDeferral = false, bool verifyTreePresentation = false)
    {
        AssertDisplayInterop();
        VerifyProfileCaptureStateNames();
        if (verifyProfileRefresh || verifyProfileNoteRefresh || verifyUpdateYield || verifyLegacySaveFeedback ||
            verifySnapshotAdoption || verifyExit || verifyExitStartup || verifyPreferredNameRetention ||
            verifyPreferredSnapshotDeferral || verifyTreePresentation)
            Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var heldPublisher = verifyProfileRefresh || verifyProfileNoteRefresh || verifyUpdateYield ? new HeldNotePublisher() : null;
        var assemblyPath = Path.ChangeExtension(executable, ".dll");
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
            resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var settings = Path.GetFullPath(Path.Combine(".test-workspaces", "OrgExplorer-Xaml-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(settings);
        Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
        AssemblyLoadContext.Default.Resolving += Resolve;
        var previousContext = SynchronizationContext.Current;
        Application? application = null;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (verifyProfileRefresh || verifyProfileNoteRefresh || verifyUpdateYield || verifyLegacySaveFeedback ||
                verifySnapshotAdoption || verifyExit || verifyExitStartup || verifyPreferredNameRetention ||
                verifyPreferredSnapshotDeferral || verifyTreePresentation)
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var type = assembly.GetType("OrgExplorer.Desktop.MainWindow", throwOnError: true)!;
            Func<Window, string?> chooseFolder = _ => CompiledWorkspaceChoices.Dequeue();
            var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [ApplicationWorkflow.CreateFileBacked(filePublisher: heldPublisher, fileObserver: heldPublisher), chooseFolder], null)!;
            var failures = new List<string>();
            if (window.Icon is not BitmapFrame { Decoder: IconBitmapDecoder icon } ||
                !icon.Frames.Select(frame => frame.PixelWidth).Order().SequenceEqual(
                    new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 }))
                failures.Add("The compiled window must load the complete native C1 icon, not the generic WPF identity.");
            var settingsPanel = (FrameworkElement)window.FindName("SettingsPanel");
            static bool Contains(DependencyObject parent, DependencyObject? child)
            {
                for (; child is not null; child = LogicalTreeHelper.GetParent(child))
                    if (ReferenceEquals(parent, child)) return true;
                return false;
            }
            foreach (var name in new[] { "WorkspaceControls", "WorkspacePath", "OpenImportButton" })
            {
                var control = (FrameworkElement)window.FindName(name);
                if (!Contains(settingsPanel, control))
                    failures.Add($"{name}: infrequent management belongs in Settings, not everyday chrome.");
            }
            if (window.FindName("WorkspacePath") is not TextBlock { Focusable: true, ContextMenu: not null } currentPath ||
                !KeyboardNavigation.GetIsTabStop(currentPath) ||
                LogicalDescendants(settingsPanel).OfType<TextBox>().Any())
                failures.Add("Settings must display the actual copyable Workspace path, with no editable pending path.");
            var folderActions = LogicalDescendants(settingsPanel).OfType<Button>().Where(button =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(button) is "ChooseWorkspace" or "OpenWorkspace").ToArray();
            if (folderActions.Length != 1 ||
                System.Windows.Automation.AutomationProperties.GetAutomationId(folderActions[0]) != "OpenWorkspace")
                failures.Add("Settings must offer one picker-and-open action, not separate Choose and Open steps.");
            if (window.FindName("WorkspaceName") is not TextBlock workspaceName || workspaceName.Focusable)
                failures.Add("Everyday chrome needs a quiet, non-interactive Workspace name.");
            var search = (FrameworkElement)window.FindName("SearchQuery");
            if (Contains((FrameworkElement)window.FindName("ExplorerPanel"), search))
                failures.Add("Search must remain directly accessible outside the People view.");
            if (window.FindName("DiscardImportButton") is not Button)
                failures.Add("Failed Import has no independent discard control.");
            if (window.FindName("OpenPeopleButton") is not Button people ||
                System.Windows.Automation.AutomationProperties.GetName(people) != "People" || !people.Focusable)
                failures.Add("Compact People navigation needs a labelled keyboard-accessible entry.");
            foreach (var name in new[] { "PeopleDivider" })
                if (window.FindName(name) is not GridSplitter { Focusable: true } divider ||
                    string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(divider)))
                    failures.Add($"{name}: proportional dividers must expose a meaningful keyboard-accessible control.");
            var readingScroll = (ScrollViewer)window.FindName("PersonScroll");
            if (Contains(readingScroll, (DependencyObject)window.FindName("PersonIdentity")) ||
                !Contains(readingScroll, (DependencyObject)window.FindName("ProfileSummary")) ||
                !Contains(readingScroll, (DependencyObject)window.FindName("NotesPane")))
                failures.Add("Identity must stay outside the shared facts-and-notes reading scroll.");
            if (window.FindName("PersonCategoryLabel") is not TextBlock { Focusable: true } category ||
                !Contains((DependencyObject)window.FindName("PersonIdentity"), category) ||
                Contains(readingScroll, category) ||
                !System.Windows.Automation.AutomationProperties.GetHelpText(category).Contains("classification", StringComparison.Ordinal))
                failures.Add("A read-only, accessible Person-category badge belongs beside the anchored name.");
            if (window.FindName("ProfileDivider") is not null || window.FindName("ProfileDetails") is not null)
                failures.Add("Ordinary reading must not retain an inner divider or a More details gate.");
            if (window.FindName("ClearSearchButton") is not Button { Focusable: true } clearSearch ||
                string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(clearSearch)))
                failures.Add("The single search field needs a named keyboard-accessible clear action.");
            foreach (var name in new[] { "WorkspaceSurface", "WorkspaceStartPanel", "PeoplePanel", "ProfileSummary", "NotesPane", "OperationalNotice" })
                if (UIElementAutomationPeer.CreatePeerForElement((UIElement)window.FindName(name)) is null)
                    failures.Add($"{name}: actual adaptive geometry must be observable through UI Automation.");
            if (KeyboardNavigation.GetTabNavigation((DependencyObject)window.FindName("PeoplePanel")) == KeyboardNavigationMode.Cycle)
                failures.Add("People must not trap Tab navigation when no compact overlay is open.");
            var personNotes = (FrameworkElement)window.FindName("PersonNotes");
            var noteList = (ListBox)personNotes.FindName("NotesList");
            var surfacePeer = UIElementAutomationPeer.CreatePeerForElement(personNotes);
            var listPeer = UIElementAutomationPeer.CreatePeerForElement(noteList);
            if (surfacePeer.GetAutomationId() == listPeer.GetAutomationId())
                failures.Add("The Person-note surface and its secondary title list share an automation identity; empty/collapsed-list discovery targets the visible surface.");
            var teamNotes = (FrameworkElement)window.FindName("TeamNotes");
            if (teamNotes.FindName("OtherNotes") is not Expander { IsExpanded: false } otherTeamNotes ||
                !Contains(otherTeamNotes, (DependencyObject)teamNotes.FindName("NotesList")))
                failures.Add("Owned Team titles must be behind collapsed Other notes, not a permanent picker.");
            if (teamNotes.FindName("CurrentTeamContext") is not Expander { IsExpanded: false } teamContext ||
                teamNotes.FindName("InheritedReviewWarnings") is not TextBlock warning ||
                Contains(teamContext, warning))
                failures.Add("Inherited Team details must start collapsed without hiding attributed review warnings.");
            if (window.FindName("HistoryAccess") is not Expander { IsExpanded: false } history ||
                new[] { "ProfileTimelineText", "FieldConfirmationsText", "ProfileObservationsText",
                    "HistoricalTeamSummary", "HistoricalTeamReading" }.Any(name =>
                        !Contains(history, (DependencyObject)window.FindName(name))))
                failures.Add("Secondary History must retain profile changes, confirmations, observations and frozen Team evidence.");
            foreach (var name in new[] { "DirectoryName", "Email", "AliasValue", "JobTitleValue", "DepartmentValue",
                "OfficeBuildingValue", "OfficeNumberValue", "OfficeStateValue", "OfficeCountryValue", "LinkedInProfileUrlValue" })
            {
                if (window.FindName(name) is not TextBlock value || !value.Focusable ||
                    !KeyboardNavigation.GetIsTabStop(value) || value.ContextMenu is null)
                    failures.Add($"{name}: View-mode Copy must be reachable by keyboard.");
                else if (UIElementAutomationPeer.CreatePeerForElement(value).GetPattern(PatternInterface.Invoke) is not IInvokeProvider)
                    failures.Add($"{name}: the read-only value must expose its copy action to accessibility clients.");
                else
                {
                    value.ContextMenu.PlacementTarget = value;
                    var copy = value.ContextMenu.Items.OfType<MenuItem>().Single();
                    System.Windows.Data.BindingOperations.GetBindingExpression(copy, UIElement.IsEnabledProperty)?.UpdateTarget();
                    if (copy.IsEnabled || value.Cursor == Cursors.Hand ||
                        System.Windows.Automation.AutomationProperties.GetItemStatus(value).Contains("Click to copy", StringComparison.Ordinal))
                        failures.Add($"{name}: an absent value must not advertise or enable copying.");
                }
            }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            if (verifyExitStartup || verifyExit || verifyProfileRefresh)
                HarnessRuntime.Run("compiled-exit-host-startup", () => VerifyOwnedExitHostStartup(executable, settings));
            if (verifyExit || verifyProfileRefresh)
            {
                HarnessRuntime.Run("compiled-workspace-exit", () => VerifyCompiledWorkspaceExit(type, settings));
                HarnessRuntime.Run("owned-workspace-exit", () => VerifyOwnedExitProcesses(executable, settings));
            }
            if (verifySnapshotAdoption)
            {
                HarnessRuntime.Run("compiled-automatic-input-scroll", () => VerifyCompiledAutomaticInputScroll(type, settings, chooseFolder));
                HarnessRuntime.Run("compiled-snapshots", () => VerifySnapshotSynchronization(type, settings, chooseFolder));
            }
            if (verifyProfileRefresh || verifyLegacySaveFeedback)
                HarnessRuntime.Run("compiled-legacy-save-feedback", () => VerifyLegacySaveFeedback(type, settings, chooseFolder));
            if (verifyProfileRefresh || verifyPreferredNameRetention)
                HarnessRuntime.Run("compiled-preferred-name-retention", () => VerifyPreferredNameRetention(type, settings, chooseFolder));
            if (verifyProfileRefresh || verifyTreePresentation)
            {
                var treeWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyTreePresentation(treeWindow, settings); }
                finally { ClosePublishedCompiledWindow(treeWindow); }
            }
            if (verifyProfileRefresh)
            {
                HarnessRuntime.Run("compiled-cutover-publication", () =>
                    VerifyHostedPublicationContract(type, Path.Combine(settings, "cp")));
                HarnessRuntime.Run("compiled-cutover-note-modes", () =>
                    VerifyHostedNoteModeContracts(type, Path.Combine(settings, "cm")));
                HarnessRuntime.Run("compiled-cutover-acknowledgment", () =>
                    VerifyHostedAcknowledgmentContract(type, Path.Combine(settings, "ca")));
                HarnessRuntime.Run("compiled-cutover-failure-feedback", () =>
                    VerifyHostedMutationFeedbackContracts(type, Path.Combine(settings, "cf")));
                HarnessRuntime.Run("compiled-cutover-incoming", () =>
                    VerifyHostedIncomingContract(type, Path.Combine(settings, "ci")));
                HarnessRuntime.Run("compiled-cutover-close-publication", () =>
                    VerifyHostedClosePublicationContracts(type, Path.Combine(settings, "cc")));
                HarnessRuntime.Run("compiled-recovered-copy", () =>
                    VerifyRecoveredCopyContract(type, Path.Combine(settings, "cr")));
                HarnessRuntime.Run("compiled-independent-note-leave", () =>
                    VerifyIndependentNoteLeaveContract(type, Path.Combine(settings, "il")));
                HarnessRuntime.Run("compiled-import-recovery-admission", () =>
                    VerifyImportRecoveryAdmission(type, Path.Combine(settings, "ir")));
                HarnessRuntime.Run("compiled-manager-choice-demand", () =>
                    VerifyManagerChoiceDemand(type, Path.Combine(settings, "mc")));
                HarnessRuntime.Run("compiled-ordinary-fixture-state", () =>
                    VerifyHostedOrdinaryFixtureStateContracts(type, Path.Combine(settings, "os")));
                HarnessRuntime.Run("compiled-current-search-storage", () =>
                    VerifyCurrentSearchStorage(type, Path.Combine(settings, "cs")));
                HarnessRuntime.Run("compiled-automatic-people-reading", () =>
                    VerifyAutomaticPeopleReading(type, Path.Combine(settings, "pr")));
                HarnessRuntime.Run("compiled-note-read-demand", () =>
                    VerifyNoteReadDemand(type, Path.Combine(settings, "nd")));
                HarnessRuntime.Run("compiled-actual39-functional", () =>
                    VerifyActual39FunctionalContracts(type, Path.Combine(settings, "af")));
                HarnessRuntime.Run("compiled-normal-close-publication-preparation", () =>
                    VerifyNormalClosePublicationPreparation(type, Path.Combine(settings, "np")));
                HarnessRuntime.Run("compiled-rich-host-restoration", () =>
                    VerifyRichHostRestoration(type, Path.Combine(settings, "rh")));
                HarnessRuntime.Run("compiled-incoming-smoke-publication", () =>
                    VerifyIncomingSmokePublication(type, Path.Combine(settings, "ip")));
                HarnessRuntime.Run("compiled-actualc38-note-readiness", () =>
                    VerifyActualC38NoteReadinessContracts(type, Path.Combine(settings, "cn")));
                HarnessRuntime.Run("compiled-current1eee-note-contexts", () =>
                    VerifyCurrent1eeeNoteContracts(type, Path.Combine(settings, "nc")));
                HarnessRuntime.Run("compiled-copy", () => VerifyProfileCopyControls(type, settings));
                var unavailableNameWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyPreferredNameSnapshotDeferral(unavailableNameWindow, settings); }
                finally { ClosePublishedCompiledWindow(unavailableNameWindow); }
                var noteFailureWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyRetainedNoteCreationFailure(noteFailureWindow, settings); }
                finally { ClosePublishedCompiledWindow(noteFailureWindow); }
                VerifySettingsFocus(window);
                HarnessRuntime.Run("compiled-sqlite", () => VerifySqliteCreationNavigation(type, settings, chooseFolder));
                HarnessRuntime.Run("compiled-snapshots", () => VerifySnapshotSynchronization(type, settings, chooseFolder));
                HarnessRuntime.Run("compiled-local-recovery", () => VerifyLocalRecoveryCleanup(type, settings));
                // The empty reading probe must not warm the separate cold browsing window.
                var emptyReadingWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyEmptyReadingScroll(emptyReadingWindow, settings); }
                finally { ClosePublishedCompiledWindow(emptyReadingWindow); }
                var retainedWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyRetainedOverlayGeometry(retainedWindow, settings); }
                finally { ClosePublishedCompiledWindow(retainedWindow); }
                var reportsWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyReportsVisibility(reportsWindow, settings); }
                finally { ClosePublishedCompiledWindow(reportsWindow); }
                var teamNameWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyOwnedTeamNameProfile(teamNameWindow, Path.Combine(settings, "team-name-workspace")); }
                finally { ClosePublishedCompiledWindow(teamNameWindow); }
                VerifyProfileSaveRefresh(window, settings, heldPublisher!);
                VerifyPeopleSearchRestoration(window, settings);
                VerifyCompactSearchActivation(type, Path.Combine(settings, "compact-activation"));
            }
            if (verifyPreferredSnapshotDeferral)
                VerifyPreferredNameSnapshotDeferral(window, settings);
            if (verifyProfileNoteRefresh)
                VerifyProfileSaveRefresh(window, settings, heldPublisher!, noteRefreshOnly: true);
            if (verifyUpdateYield)
            {
                VerifyUpdateYield(window, settings, heldPublisher!);
                VerifyOwnedUpdateExit(executable, settings);
            }
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The compiled-XAML check must never show the window or create its native handle.");
            ClosePublishedCompiledWindow(window);
            Console.WriteLine("PASS: actual compiled MainWindow BAML and connectors load without showing a window.");
            return 0;
        }
        finally
        {
            application?.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            AssemblyLoadContext.Default.Resolving -= Resolve;
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
            CompiledWorkspaceChoices.Clear();
            Directory.Delete(settings, true);
        }
    }

    private static void VerifyProfileCopyControls(Type windowType, string settings)
    {
        var folder = Path.Combine(settings, "copy-workspace");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000104",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery Example","alias":"avery",
                  "jobTitle":"Research\r\nengineer","department":"Research","officeBuilding":"Juniper",
                  "officeNumber":"007B","officeState":"Ontario","officeCountry":"Canada",
                  "linkedInProfileUrl":"https://example.com/avery","managerEmail":"morgan@example.com"},
                 {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"rowan@example.com"},
                 {"email":"rowan@example.com","displayName":"Rowan","managerEmail":null},
                 {"email":"unset@example.com","displayName":"Unset","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Copy fixture failed: " + seed.Status);
            if (seed.CreatePersonNote("avery@example.com", "Saved", "# Saved") is null)
                throw new InvalidOperationException("Copy note fixture failed: " + seed.Status);
            foreach (var owner in new[] { "morgan@example.com", "rowan@example.com" })
                if (!seed.BeginProfileEdit(owner) || !seed.StageProfileTeamName("Platform") || !seed.SaveProfileEdit())
                    throw new InvalidOperationException("Copy Team-name fixture failed: " + seed.Status);
        }
        var copied = "Copy has not been invoked.";
        var failClipboard = false;
        Action<string> clipboard = text =>
        {
            if (failClipboard) throw new System.Runtime.InteropServices.ExternalException("Clipboard is held by another application.");
            copied = text;
        };
        Func<Window, string?> chooseFolder = _ => CompiledWorkspaceChoices.Dequeue();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(), chooseFolder, clipboard], null)!;
        try
        {
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ChooseCompiledWorkspace(window, folder);
            PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
            var tree = Control<TreeView>("PeopleList");
            var avery = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "rowan@example.com")
                .Reports.Single().Reports.Single();
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, avery, TreeView.SelectedItemChangedEvent));
            var beforeCopy = WorkspaceFixtures.Bytes(folder);
            var room = Control<TextBlock>("OfficeNumberValue");
            room.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            if (copied != "007B")
                throw new InvalidOperationException("Primary activation must copy the exact office number, including leading zeroes.");
            if (window.FindName("ProfileCopyFeedback") is not TextBlock { Visibility: Visibility.Visible } feedback ||
                feedback.Text != "Office number copied." ||
                UIElementAutomationPeer.CreatePeerForElement(feedback).GetName() != "Office number copied." ||
                feedback.IsHitTestVisible)
                throw new InvalidOperationException("Copy success needs a non-blocking, accessible confirmation that omits the copied value.");
            void WaitForFeedback()
            {
                using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.FeedbackExpiry);
                PumpUntil(() => feedback.Visibility == Visibility.Collapsed);
            }
            var lifetime = System.Diagnostics.Stopwatch.StartNew();
            WaitForFeedback();
            if (lifetime.Elapsed > TimeSpan.FromSeconds(3))
                throw new InvalidOperationException("Copy confirmation did not return to quiet reading within three seconds.");
            if (Control<FrameworkElement>("OperationalNotice").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A completed copy must return healthy reading to quiet.");
            var fields = new[]
            {
                ("Email", "avery@example.com", "Email"),
                ("JobTitleValue", "Research\r\nengineer", "Job title"),
                ("OfficeBuildingValue", "Juniper", "Office building"),
                ("OfficeNumberValue", "007B", "Office number"),
                ("DepartmentValue", "Research", "Department"),
                ("OfficeStateValue", "Ontario", "Office state / province"),
                ("OfficeCountryValue", "Canada", "Office country"),
                ("DirectoryName", "Avery Example", "Directory name"),
                ("AliasValue", "avery", "Alias"),
                ("LinkedInProfileUrlValue", "https://example.com/avery", "LinkedIn profile URL")
            };
            void ActivateKey(TextBlock value, Key key) => value.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                new UnshownInputSource { RootVisual = (Visual)window.Content }, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            HarnessRuntime.Checkpoint("compiled-copy-payload-matrix");
            foreach (var (name, expected, label) in fields)
            {
                var value = Control<TextBlock>(name);
                var menu = value.ContextMenu!;
                var copy = menu.Items.OfType<MenuItem>().Single();
                System.Windows.Data.BindingOperations.GetBindingExpression(copy, UIElement.IsEnabledProperty)!.UpdateTarget();
                if (!copy.IsEnabled || value.Cursor != Cursors.Hand ||
                    value.ToolTip as string != "Click to copy\n" + expected ||
                    UIElementAutomationPeer.CreatePeerForElement(value).GetName() != expected ||
                    System.Windows.Automation.AutomationProperties.GetItemStatus(value) != "Click to copy")
                    throw new InvalidOperationException(name + " lost its full value, copy hint or accessible action.");
                foreach (var gesture in new[] { "primary", "Enter", "Space", "menu", "accessible Invoke" })
                {
                    if (name is "Email" or "OfficeNumberValue")
                        WaitForFeedback();
                    copied = "Copy has not been invoked.";
                    if (gesture == "primary")
                        value.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    else if (gesture == "menu") copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    else if (gesture == "accessible Invoke")
                    {
                        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(value).GetPattern(PatternInterface.Invoke)).Invoke();
                        PumpUntil(() => copied == expected);
                    }
                    else ActivateKey(value, gesture == "Enter" ? Key.Enter : Key.Space);
                    if (copied != expected || feedback.Visibility != Visibility.Visible || feedback.Text != label + " copied.")
                        throw new InvalidOperationException($"{name} {gesture} must copy its exact payload with the same value-free confirmation.");
                }
            }
            HarnessRuntime.Checkpoint("compiled-copy-team-segments");
            var root = (FrameworkElement)window.Content;
            using (HarnessRuntime.Measure(HarnessRuntime.Work.CompiledLayout))
            {
                root.Measure(new Size(700, 500));
                root.Arrange(new Rect(0, 0, 700, 500));
                root.UpdateLayout();
            }
            var segments = VisualDescendants(Control<ItemsControl>("TeamNames")).OfType<TextBlock>()
                .Where(value => System.Windows.Automation.AutomationProperties.GetAutomationId(value) == "TeamNameSegment").ToArray();
            if (segments.Length != 2) throw new InvalidOperationException("Copy fixture lost duplicate attributed Team names.");
            foreach (var (segment, owner) in segments.Zip(new[] { "Morgan (morgan@example.com)", "Rowan (rowan@example.com)" }))
            {
                if (System.Windows.Automation.AutomationProperties.GetHelpText(segment) != "Owned by " + owner ||
                    segment.ToolTip is not ToolTip tooltip)
                    throw new InvalidOperationException("Copyable duplicate Team names must retain independent owners.");
                tooltip.PlacementTarget = segment;
                System.Windows.Data.BindingOperations.GetBindingExpression(tooltip, ContentControl.ContentProperty)!.UpdateTarget();
                if (tooltip.Content as string != "Click to copy\nPlatform\nOwned by " + owner || tooltip.IsOpen)
                    throw new InvalidOperationException("The compiled Team tooltip must retain the full label, copy hint and owner without opening.");
                foreach (var key in new[] { Key.Enter, Key.Space })
                {
                    WaitForFeedback();
                    copied = "Copy has not been invoked.";
                    ActivateKey(segment, key);
                    if (copied != "Platform" || feedback.Visibility != Visibility.Visible || feedback.Text != "Team name copied.")
                        throw new InvalidOperationException("Team-name activation must copy only that segment with the shared feedback.");
                }
            }
            WaitForFeedback();
            AssertWorkspaceBytes(folder, beforeCopy);
            HarnessRuntime.Checkpoint("compiled-copy-missing-and-failure");
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!,
                tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "unset@example.com"),
                TreeView.SelectedItemChangedEvent));
            Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<TextBox>("DirectoryNameEditor").Clear();
            Control<Button>("SaveProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<FrameworkElement>("ProfileForm").Visibility == Visibility.Collapsed);
            foreach (var (name, _, _) in fields.Where(field => field.Item1 != "Email"))
            {
                var value = Control<TextBlock>(name);
                var copy = value.ContextMenu!.Items.OfType<MenuItem>().Single();
                System.Windows.Data.BindingOperations.GetBindingExpression(copy, UIElement.IsEnabledProperty)!.UpdateTarget();
                if (copy.IsEnabled || value.Cursor == Cursors.Hand || value.ToolTip is not null ||
                    UIElementAutomationPeer.CreatePeerForElement(value).IsEnabled() ||
                    System.Windows.Automation.AutomationProperties.GetItemStatus(value) != "")
                    throw new InvalidOperationException("Absent " + name + " must expose no enabled copy action, cursor or hint.");
                copied = "Copy has not been invoked.";
                value.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                ActivateKey(value, Key.Enter);
                ActivateKey(value, Key.Space);
                copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (copied != "Copy has not been invoked." || feedback.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("An absent value replaced the clipboard sentinel or displayed success.");
            }
            Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<TextBox>("OfficeState").Text = "—";
            Control<Button>("SaveProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<FrameworkElement>("ProfileForm").Visibility == Visibility.Collapsed);
            copied = "Copy has not been invoked.";
            Control<TextBlock>("OfficeStateValue").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            if (copied != "—") throw new InvalidOperationException("A real dash value is not a missing-value placeholder.");
            WaitForFeedback();
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, avery, TreeView.SelectedItemChangedEvent));
            var notes = Control<FrameworkElement>("PersonNotes");
            var title = (TextBox)notes.FindName("NoteTitle");
            var noteStatus = (TextBlock)notes.FindName("NoteStatus");
            ((TabItem)notes.FindName("EditTab")).IsSelected = true;
            title.Text = "";
            PumpUntil(() => noteStatus.Text.Contains("NOT saved", StringComparison.Ordinal));
            Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<TextBox>("DirectoryNameEditor").Text = "Retained profile draft";
            var unrelatedStatus = Control<TextBlock>("StatusText").Text;
            var unrelatedNoteStatus = noteStatus.Text;
            var bytes = WorkspaceFixtures.Bytes(folder);
            room.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            copied = "Copy has not been invoked.";
            failClipboard = true;
            room.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            if (feedback.Visibility != Visibility.Collapsed || copied != "Copy has not been invoked." ||
                !Control<TextBlock>("NoticeSummary").Text.Contains("Copy failed", StringComparison.Ordinal) ||
                Control<TextBlock>("StatusText").Text != unrelatedStatus || noteStatus.Text != unrelatedNoteStatus ||
                Control<TextBox>("DirectoryNameEditor").Text != "Retained profile draft" ||
                Control<TextBlock>("Email").Text != "avery@example.com" || title.Text != "")
                throw new InvalidOperationException("Clipboard failure must report failure without success or displacing unrelated errors, the Person or staged writing.");
            AssertWorkspaceBytes(folder, bytes);
            failClipboard = false;
            Control<TextBlock>("Email").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            WaitForFeedback();
            if (!Control<TextBlock>("NoticeSummary").Text.Contains("Person note", StringComparison.Ordinal) ||
                noteStatus.Text != unrelatedNoteStatus)
                throw new InvalidOperationException("Successful copy or its expiry dismissed an independent writing failure.");
            AssertWorkspaceBytes(folder, bytes);
            Control<Button>("CancelProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)notes.FindName("DiscardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Copy checks must stay invisible without a native window or real clipboard writes.");
            Console.WriteLine("PASS: compiled primary/Enter/Space/menu copying retains exact ordinary and duplicate Team-name values, missing-value sentinels, actual-dash data, accessible hints and transient feedback; clipboard failure preserves independent errors/drafts/Workspace bytes. Only the clipboard boundary is substituted; no window/native handle or real clipboard writes.");
        }
        finally
        {
            ((Button)window.FindName("CancelProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var notes = (FrameworkElement)window.FindName("PersonNotes");
            ((Button)notes.FindName("DiscardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClosePublishedCompiledWindow(window);
        }
    }

    private static void VerifyOwnedTeamNameProfile(Window window, string folder)
    {
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000087",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"rowan@example.com","displayName":"Rowan","managerEmail":null},
                 {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"rowan@example.com"},
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
                 {"email":"unset@example.com","displayName":"Unset","managerEmail":null}]}
                """).Id) || !seed.BeginProfileEdit("morgan@example.com") ||
                !seed.StageProfileTeamName("Platform") || !seed.SaveProfileEdit() ||
                !seed.BeginProfileEdit("rowan@example.com") ||
                !seed.StageProfileTeamName("Platform") || !seed.SaveProfileEdit())
                throw new InvalidOperationException("Owned Team-name fixture failed: " + seed.Status);
        }
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
        var tree = Control<TreeView>("PeopleList");
        var ancestor = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "rowan@example.com");
        var owner = ancestor.Reports.Single();
        void Select(ReportingNode node) =>
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
        Select(owner);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 900));
        root.Arrange(new Rect(0, 0, 1100, 900));
        root.UpdateLayout();
        TextBlock[] Segments() => VisualDescendants(Control<ItemsControl>("TeamNames")).OfType<TextBlock>()
            .Where(text => System.Windows.Automation.AutomationProperties.GetAutomationId(text) == "TeamNameSegment").ToArray();
        void AssertPath(string ownedName)
        {
            var segments = Segments();
            if (!segments.Select(text => (text.Text, System.Windows.Automation.AutomationProperties.GetHelpText(text)))
                .SequenceEqual(new[] { (ownedName, "Owned by Morgan (morgan@example.com)"),
                    ("Platform", "Owned by Rowan (rowan@example.com)") }))
                throw new InvalidOperationException("The compiled path lost ordered duplicate labels or their distinct owners.");
            var separators = VisualDescendants(Control<ItemsControl>("TeamNames")).OfType<TextBlock>()
                .Where(text => System.Windows.Automation.AutomationProperties.GetAutomationId(text) == "TeamNameSeparator" &&
                    text.Visibility == Visibility.Visible).ToArray();
            if (separators.Length != 1 || separators[0].Text != " > " ||
                separators[0].Focusable || segments.Any(text =>
                    UIElementAutomationPeer.CreatePeerForElement(text).GetPattern(PatternInterface.Value) is not null ||
                    UIElementAutomationPeer.CreatePeerForElement(text).GetPattern(PatternInterface.Invoke) is not IInvokeProvider))
                throw new InvalidOperationException("The compiled path must separate read-only segments with exactly ' > '.");
            var path = string.Concat(VisualDescendants(Control<ItemsControl>("TeamNames")).OfType<TextBlock>()
                .Where(text => text.Visibility == Visibility.Visible &&
                    System.Windows.Automation.AutomationProperties.GetAutomationId(text) is "TeamNameSegment" or "TeamNameSeparator")
                .Select(text => text.Text));
            if (path != ownedName + " > Platform")
                throw new InvalidOperationException("The compiled separators do not form the exact ordered Team-name path.");
            foreach (var (value, expected) in segments.Zip(new[] { ownedName, "Platform" }))
            {
                if (!value.Focusable || !KeyboardNavigation.GetIsTabStop(value) ||
                    value.ContextMenu is not { } menu ||
                    menu.Items.OfType<MenuItem>().SingleOrDefault() is not { Header: "Copy value" } copy)
                    throw new InvalidOperationException("Each Team-name segment needs its own keyboard-accessible Copy value action.");
                menu.PlacementTarget = value;
                System.Windows.Data.BindingOperations.GetBindingExpression(copy, FrameworkElement.TagProperty)!.UpdateTarget();
                if (copy.Tag is not string copied || copied != expected || menu.IsOpen)
                    throw new InvalidOperationException("Team-name Copy must bind only the actual segment label, not its owner or the full path.");
            }
            var available = Control<ItemsControl>("TeamNames").ActualWidth;
            if (segments.Any(text => text.TranslatePoint(new Point(), Control<ItemsControl>("TeamNames")).X < -1 ||
                text.TranslatePoint(new Point(text.ActualWidth, 0), Control<ItemsControl>("TeamNames")).X > available + 1))
                throw new InvalidOperationException("A compiled Team-name segment overflows its supported fact width.");
        }
        AssertPath("Platform");
        IEnumerable<UIElementAutomationPeer> SegmentPeers(AutomationPeer candidate)
        {
            if (candidate is UIElementAutomationPeer element && candidate.GetAutomationId() == "TeamNameSegment")
                yield return element;
            foreach (var child in candidate.GetChildren() ?? [])
                foreach (var descendant in SegmentPeers(child)) yield return descendant;
        }
        var initialNames = Control<ItemsControl>("TeamNames");
        void AssertLivePeers(params (string Name, string Help)[] expected)
        {
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
            var peers = SegmentPeers(UIElementAutomationPeer.CreatePeerForElement(initialNames)).ToArray();
            if (!peers.Select(peer => (peer.GetName(), peer.GetHelpText())).SequenceEqual(expected) ||
                peers.Any(peer => !peer.Owner.IsDescendantOf(initialNames)))
                throw new InvalidOperationException("Team-name path updates lost distinct live attributed automation peers. " +
                    $"Expected={string.Join("; ", expected)}; " +
                    $"Peers={string.Join("; ", peers.Select(peer => $"{peer.GetName()} / {peer.GetHelpText()} / live={peer.Owner.IsDescendantOf(initialNames)}"))}; " +
                    $"Visual={string.Join("; ", Segments().Select(text => $"{text.Text} / {System.Windows.Automation.AutomationProperties.GetHelpText(text)}"))}; " +
                    $"Items={initialNames.Items.Count}; status={Control<TextBlock>("StatusText").Text}.");
        }
        AssertLivePeers(("Platform", "Owned by Morgan (morgan@example.com)"),
            ("Platform", "Owned by Rowan (rowan@example.com)"));
        var segment = Segments()[0];
        var tooltip = (ToolTip)segment.ToolTip;
        tooltip.PlacementTarget = segment;
        System.Windows.Data.BindingOperations.GetBindingExpression(tooltip, ContentControl.ContentProperty)!.UpdateTarget();
        var help = System.Windows.Automation.AutomationProperties.GetHelpText(segment);
        var tooltipText = tooltip.Content as string;
        var peer = UIElementAutomationPeer.CreatePeerForElement(segment);
        if (!segment.Focusable || !KeyboardNavigation.GetIsTabStop(segment) ||
            help != "Owned by Morgan (morgan@example.com)" || tooltipText != "Click to copy\nPlatform\nOwned by Morgan (morgan@example.com)" ||
            peer.GetName() != "Platform" || peer.GetHelpText() != help)
            throw new InvalidOperationException("The compiled owned segment lost focusable owner attribution or accessible text. " +
                $"Focusable={segment.Focusable}; tab={KeyboardNavigation.GetIsTabStop(segment)}; help='{help}'; tooltip='{tooltipText}'; name='{peer.GetName()}'.");
        var team = Control<FrameworkElement>("TeamNotes");
        T TeamControl<T>(string name) where T : FrameworkElement => (T)team.FindName(name);
        TeamControl<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => TeamControl<TextBox>("NoteTitle").Text == "New Team note" &&
            Control<TextBlock>("WorkflowActivity").Text == "Ready");
        TeamControl<TextBox>("NoteTitle").Text = "Manager context";
        TeamControl<TextBox>("MarkdownEditor").Text = "# Frozen Manager\nOriginal Manager writing";
        var disabledDuringProfileEntry = false;
        DependencyPropertyChangedEventHandler observeEntry = (_, e) =>
        {
            if (e.NewValue is false && Control<FrameworkElement>("ProfileForm").Visibility == Visibility.Visible)
                disabledDuringProfileEntry = true;
        };
        Control<TextBox>("DirectoryNameEditor").IsEnabledChanged += observeEntry;
        PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready" &&
            TeamControl<TextBlock>("NoteStatus").Text.StartsWith(
                "Team note saved locally and confirmed for current context.", StringComparison.Ordinal));
        Invoke("EditProfileButton");
        PumpUntil(() => TeamControl<TextBlock>("NoteStatus").Text.StartsWith(
            "Team note saved locally and confirmed for current context.", StringComparison.Ordinal) &&
            Control<TextBox>("DirectoryNameEditor").IsEnabled &&
            ReferenceEquals(FocusManager.GetFocusedElement(window), Control<TextBox>("DirectoryNameEditor")));
        Control<TextBox>("DirectoryNameEditor").IsEnabledChanged -= observeEntry;
        if (disabledDuringProfileEntry)
            throw new InvalidOperationException("Late Team-note fixture autosave disabled Directory name after profile entry, invalidating its focus target.");
        if (Control<StackPanel>("TeamNameEditorRow").Visibility != Visibility.Visible ||
            Control<TextBox>("TeamNameEditor").Text != "Platform" || !Control<TextBox>("TeamNameEditor").IsEnabled)
            throw new InvalidOperationException("Eligible profile editing did not load the owned Team name.");
        Control<TextBox>("TeamNameEditor").Text = "Renamed platform";
        Invoke("SaveProfileButton");
        PumpUntil(() => Control<TextBlock>("StatusText").Text.StartsWith("Profile saved locally.", StringComparison.Ordinal));
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
            if (!reopened.OpenWorkspace(folder) ||
                reopened.GetRelationships("morgan@example.com")!.Person.TeamName != "Renamed platform")
                throw new InvalidOperationException("The compiled owned-name editor did not publish its displayed draft.");
        var names = Control<ItemsControl>("TeamNames");
        bool ExposesSegment(AutomationPeer candidate) =>
            (candidate is UIElementAutomationPeer element && candidate.GetAutomationId() == "TeamNameSegment" &&
                candidate.GetName() == "Renamed platform" && element.Owner.IsDescendantOf(names)) ||
            candidate.GetChildren()?.Any(ExposesSegment) == true;
        void ReadingLayout(double width, double height)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }
        ReadingLayout(700, 500);
        AssertPath("Renamed platform");
        var namesPeer = UIElementAutomationPeer.CreatePeerForElement(names);
        if (!ExposesSegment(namesPeer))
            throw new InvalidOperationException("The initial compiled Team-name segment is absent from its actual automation peer tree.");
        Invoke("EditProfileButton");
        ReadingLayout(700, 500);
        var ownedEditor = Control<TextBox>("TeamNameEditor");
        FocusManager.SetFocusedElement(window, ownedEditor);
        ownedEditor.BringIntoView();
        ReadingLayout(700, 500);
        Invoke("CancelProfileButton");
        PumpUntil(() => ReferenceEquals(FocusManager.GetFocusedElement(window), Control<Button>("EditProfileButton")));
        ReadingLayout(1100, 900);
        AssertPath("Renamed platform");
        AssertLivePeers(("Renamed platform", "Owned by Morgan (morgan@example.com)"),
            ("Platform", "Owned by Rowan (rowan@example.com)"));
        if (!ExposesSegment(namesPeer))
            throw new InvalidOperationException("Cancel/resize lost the compiled Team-name segment from its actual automation peer tree. " +
                $"Items={names.Items.Count}; visual segments={Segments().Length}.");
        const string longName = "Platform engineering and developer experience across the current reporting subtree";
        Invoke("EditProfileButton");
        Control<TextBox>("TeamNameEditor").Text = longName;
        Invoke("SaveProfileButton");
        PumpUntil(() => Control<TextBlock>("StatusText").Text.StartsWith("Profile saved locally.", StringComparison.Ordinal));
        foreach (var size in new[] { new Size(700, 500), new Size(1100, 900) })
        {
            ReadingLayout(size.Width, size.Height);
            AssertPath(longName);
        }
        Invoke("EditProfileButton");
        Control<TextBox>("TeamNameEditor").Clear();
        Invoke("SaveProfileButton");
        PumpUntil(() => Control<TextBlock>("StatusText").Text.StartsWith("Profile saved locally.", StringComparison.Ordinal));
        ReadingLayout(700, 500);
        AssertLivePeers(("Platform", "Owned by Rowan (rowan@example.com)"));
        if (Segments().Single().Text != "Platform" || VisualDescendants(names).OfType<TextBlock>().Any(text =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(text) == "TeamNameSeparator" &&
            text.Visibility == Visibility.Visible))
            throw new InvalidOperationException("Clearing the owned label removed or misdelimited the inherited ancestor.");
        Invoke("EditProfileButton");
        Control<TextBox>("TeamNameEditor").Text = "Renamed platform";
        Invoke("SaveProfileButton");
        PumpUntil(() => Control<TextBlock>("StatusText").Text.StartsWith("Profile saved locally.", StringComparison.Ordinal));
        ReadingLayout(1100, 900);
        AssertPath("Renamed platform");
        AssertLivePeers(("Renamed platform", "Owned by Morgan (morgan@example.com)"),
            ("Platform", "Owned by Rowan (rowan@example.com)"));
        Select(owner.Reports.Single());
        Invoke("EditProfileButton");
        if (Control<StackPanel>("TeamNameEditorRow").Visibility != Visibility.Collapsed ||
            Control<ItemsControl>("TeamNames").Items.Count != 2)
            throw new InvalidOperationException("An ineligible profile exposed an owned-name editor or lost inherited context.");
        Invoke("CancelProfileButton");
        ReadingLayout(1100, 900);
        AssertPath("Renamed platform");
        AssertLivePeers(("Renamed platform", "Owned by Morgan (morgan@example.com)"),
            ("Platform", "Owned by Rowan (rowan@example.com)"));
        Select(tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "unset@example.com"));
        Invoke("EditProfileButton");
        if (Control<StackPanel>("TeamNameEditorRow").Visibility != Visibility.Collapsed ||
            Control<TextBlock>("TeamNameEmpty").Visibility != Visibility.Visible)
            throw new InvalidOperationException("An empty ineligible profile exposed an owned-name editor or hid Not set.");
        Invoke("CancelProfileButton");
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Owned-name wiring must remain invisible without a native handle.");
        Console.WriteLine("PASS: compiled ordered/duplicate Team-name segments, separators, segment-only Copy bindings, owner bindings, owner-only durable Save, inherited/empty reading and live Cancel/resize automation peers without a window/native handle; physical input remains hosted evidence.");
    }

    private static void VerifySettingsFocus(Window window)
    {
        var settings = (Button)window.FindName("OpenSettingsButton");
        var folder = (Button)window.FindName("OpenWorkspaceButton");
        void Toggle() => settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Settle()
        {
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
        }

        Toggle();
        Settle();
        if (!ReferenceEquals(FocusManager.GetFocusedElement(window), folder))
            throw new InvalidOperationException("Opening Settings did not establish Open Workspace as its logical focus destination.");
        Toggle();
        Toggle();
        Toggle();
        Settle();
        if (!ReferenceEquals(FocusManager.GetFocusedElement(window), settings))
            throw new InvalidOperationException("A queued Settings focus request stole logical focus after Settings closed.");
        Console.WriteLine("PASS: Settings logical focus settles on Open Workspace and stale requests cannot focus closed Settings; physical keyboard focus still requires hosted WPF.");
    }

    private static void ChooseCompiledWorkspace(Window window, string? folder)
    {
        if (((FrameworkElement)window.FindName("SettingsPanel")).Visibility != Visibility.Visible)
            throw new InvalidOperationException("The compiled picker action requires explicit Settings preparation.");
        CompiledWorkspaceChoices.Enqueue(folder);
        ((Button)window.FindName("OpenWorkspaceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (CompiledWorkspaceChoices.Count != 0)
            throw new InvalidOperationException("Open Workspace did not reach its external folder-picker boundary.");
    }

    private static void VerifyProfileCaptureStateNames()
    {
        var records = new List<string>();
        HarnessRuntime.Run("compiled-capture-state-names", () =>
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in CompactIdentityStateCases)
            {
                var stem = PersonCaptureState("profile-inline-minimum-700-500", state.Email);
                var names = new List<string> { stem + "-top", stem + "-manager" };
                if (state.Archived) names.Add(PersonCaptureState("profile-reading-narrow", state.Email));
                foreach (var name in names)
                {
                    ValidateVisualEvidenceState(name);
                    if (!emitted.Add(name))
                        throw new InvalidOperationException("Profile evidence states must remain unique: " + name);
                }
            }
            return true;
        }, write: records.Add);
        using var result = JsonDocument.Parse(records[^1]["DIAGNOSTIC: harness-runtime ".Length..]);
        var capture = result.RootElement.GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("name").GetString() == "Capture");
        if (capture.GetProperty("started").GetInt64() != 0 || capture.GetProperty("completed").GetInt64() != 0)
            throw new InvalidOperationException("Profile evidence name validation must not report screenshot capture work.");
        Console.WriteLine("PASS: actual profile-case capture names satisfy the strict path-safe contract and remain unique.");
    }

    private static void VerifyProfileSaveRefresh(Window window, string settings, HeldNotePublisher publisher,
        bool noteRefreshOnly = false, bool compactIdentityOnly = false)
    {
        var folder = Path.Combine(settings, "workspace");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                    {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000001",
                     "observedAt":"2026-11-01T17:00:00Z","people":[
                     {"email":"avery@example.com","displayName":"Avery","managerEmail":null,
                      "jobTitle":"Researcher","department":"Research","officeCountry":"Canada",
                      "alias":"ave","officeNumber":"007B"},
                     {"email":"zara@example.com","displayName":"Zara with a deliberately long Directory name for accessible identity",
                      "officeBuilding":"South campus research and collaboration building with a long descriptive location",
                      "managerEmail":null}]}
                    """).Id) || seed.CreatePersonNote("avery@example.com", "Saved title", "# Saved note") is null)
                throw new InvalidOperationException("Profile refresh fixture failed: " + seed.Status);
            var stateImport = seed.PreviewImport(new JsonObject
            {
                ["schemaVersion"] = 1, ["batchId"] = "39000000-0000-4000-8000-000000000009",
                ["observedAt"] = "2026-11-01T17:00:00Z", ["people"] = CompactIdentityFixturePeople()
            }.ToJsonString());
            if (!stateImport.CanApply || !seed.ApplyImport(stateImport.Id))
                throw new InvalidOperationException("Compact identity-state fixtures failed: " + seed.Status);
        }
        using var snapshotPeer = new SmokeSnapshotPeer(folder);
        var preferences = Path.Combine(settings, "workspace-settings.json");
        if (!new WorkspaceSettings(preferences).SetPaneProportions(folder, new(25, 92)))
            throw new InvalidOperationException("Could not seed the existing version-1 inner-ratio preference.");
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
        if (Control<TextBlock>("WorkspacePath").Text != folder)
            throw new InvalidOperationException("Settings did not display the opened Workspace's actual full path.");
        if (window.FindName("OperationalNotice") is not FrameworkElement { Visibility: Visibility.Collapsed })
            throw new InvalidOperationException("Healthy reading must not retain permanent Ready, validated, saved or inactive recovery furniture.");
        if (System.Windows.Automation.AutomationProperties.GetItemStatus(window) != "Ready" ||
            System.Windows.Automation.AutomationProperties.GetItemStatus(Control<TextBlock>("WorkspaceName")) !=
                "Current validated local Workspace.")
            throw new InvalidOperationException("Quiet reading lost its actual accessible activity or Workspace validation state.");
        if (!noteRefreshOnly)
        {
            VerifyRejectedPickerChoice(window, folder, preferences);
            VerifyImportPreviewKey(window, folder);
            Invoke("OpenSettingsButton");
            if (Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Collapsed ||
                Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Collapsed ||
                Control<TextBlock>("WorkspaceName").Text != "workspace" ||
                System.Windows.Automation.AutomationProperties.GetHelpText(Control<TextBlock>("WorkspaceName")) != folder)
                throw new InvalidOperationException("Settings must use the bounded main work area without navigating or opening Import.");
            Invoke("OpenSettingsButton");
        }
        else if (Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible)
            Invoke("OpenSettingsButton");
        var tree = Control<TreeView>("PeopleList");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items[0], TreeView.SelectedItemChangedEvent));
        if (!noteRefreshOnly)
        {
            VerifyPreferredNameEntry(window, folder, publisher);
            VerifyPhotoViewportLayout(window);
            VerifyHeaderTextAndReadingOrder(window);
            VerifyCompactIdentityStates(window, folder);
            if (compactIdentityOnly) return;
            VerifyIdentityContext(window);
            VerifyAdaptiveReadingLayout(window);
            if (new WorkspaceSettings(preferences).GetPaneProportions(folder)?.ProfilePercent != 92)
                throw new InvalidOperationException("People resizing or compact transitions erased the unused existing inner ratio.");
            if (Control<TextBlock>("JobTitleValue").Text != "Researcher" ||
                Control<TextBlock>("DepartmentValue").Text != "Research" ||
                Control<TextBlock>("OfficeCountryValue").Text != "Canada")
                throw new InvalidOperationException("The essential profile summary lost role, department or location.");
            if (Control<TextBlock>("OfficeNumberValue").Text != "007B" ||
                Control<TextBlock>("LinkedInProfileUrlValue").Text != "—" ||
                Control<FrameworkElement>("LinkedInProfileUrlValueRow").Visibility != Visibility.Visible ||
                Control<TextBlock>("OfficeBuildingValue").Text != "—" ||
                Control<TextBlock>("PersonArchivalStatus").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("All ordinary facts need stable populated/missing slots without an Active badge.");
            Invoke("EditProfileButton");
            if (Control<FrameworkElement>("AttributeEditors").Visibility != Visibility.Visible ||
                Control<TextBox>("LinkedInProfileUrl").Text != "" ||
                Control<FrameworkElement>("ProfileValues").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Full Edit must expose empty fields without duplicate reading values.");
            if (Control<Button>("Manager").IsDescendantOf(Control<Border>("ProfileValues")))
                throw new InvalidOperationException("Profile editing hid saved-Manager navigation, preventing the existing guarded leave flow.");
            VerifyInlineProfileLayout(window);
            var focusSettled = false;
            window.Dispatcher.BeginInvoke(() => focusSettled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => focusSettled);
            if (!ReferenceEquals(FocusManager.GetFocusedElement(window), Control<TextBox>("DirectoryNameEditor")))
                throw new InvalidOperationException("Entering the inline profile must focus its first field after layout, not leave focus on a hidden action.");
            Control<TextBox>("OfficeNumber").Text = "Cancelled by the owner";
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice,
                new UnshownInputSource { RootVisual = (Visual)window.Content }, 0, Key.Return)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            Control<TextBox>("OfficeNumber").RaiseEvent(enter);
            if (Control<FrameworkElement>("ProfileSessionActions").Visibility != Visibility.Visible ||
                Control<TextBox>("OfficeNumber").Text != "Cancelled by the owner")
                throw new InvalidOperationException("Field Enter must leave the complete inline draft open and unchanged.");
            Invoke("CancelProfileButton");
            focusSettled = false;
            window.Dispatcher.BeginInvoke(() => focusSettled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => focusSettled);
            if (!ReferenceEquals(FocusManager.GetFocusedElement(window), Control<Button>("EditProfileButton")))
                throw new InvalidOperationException("Profile Cancel must return logical focus to Edit profile.");
            if (Control<TextBlock>("OfficeNumberValue").Text != "007B")
                throw new InvalidOperationException("Profile Cancel changed the readable saved value.");
        }
        var notes = Control<FrameworkElement>("PersonNotes");
        T NoteControl<T>(string name) where T : FrameworkElement => (T)notes.FindName(name);
        NoteControl<ListBox>("NotesList").SelectedIndex = 0;
        var workflow = (WorkspaceViewModel)tree.DataContext;
        var noteId = workflow.GetPersonNotes("avery@example.com").Single().Id;
        foreach (var dirtyNote in new[] { false, true })
        {
            if (!workflow.SyncNow() || !snapshotPeer.Workflow.SyncNow())
                throw new InvalidOperationException(workflow.Status);
            var delivered = "# Saved note\n# Delivered before profile Save " + dirtyNote;
            snapshotPeer.Publish(source => source.EditPersonNote(noteId, "Saved title", delivered));
            if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Status);
            notes.GetType().GetMethod("RefreshExternalWorkspace")!.Invoke(notes, null);
            Invoke("EditProfileButton");
            var intendedName = dirtyNote ? "Second local profile" : "First local profile";
            Control<TextBox>("DirectoryNameEditor").Text = intendedName;
            if (dirtyNote)
            {
                NoteControl<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
                NoteControl<TextBox>("NoteTitle").Text = "";
                NoteControl<TextBox>("MarkdownEditor").Text = "# Retained unsaved note";
                PumpUntil(() => NoteControl<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal));
            }

            var status = Control<TextBlock>("StatusText");
            var submitted = workflow.ProfileEdit ??
                throw new InvalidOperationException("The profile-refresh fixture has no submitted profile.");
            if (submitted.Basis.Email != "avery@example.com" || submitted.Draft.Email != submitted.Basis.Email ||
                !submitted.IsDirty || submitted.Draft.DirectoryName != intendedName ||
                submitted.Basis.DirectoryName == intendedName || submitted.PhotoChanged ||
                submitted.Draft.PhotoPath != submitted.Basis.PhotoPath)
                throw new InvalidOperationException("Profile refresh must submit the intended changed whole profile without changing its owner or photo.");
            var priorPhoto = workflow.GetPhoto(submitted.Basis.Email);
            var priorPhotoBytes = priorPhoto?.Bytes.ToArray();
            var priorDraft = workflow.GetRetainedNoteDraft(NoteScope.Person, noteId);
            var priorNoteError = workflow.LocalSaveError;
            var priorTitle = workflow.PersonNotesView.SelectedNote?.Title;
            var priorMode = NoteControl<TabControl>("Views").SelectedItem;
            bool ProfileAccepted() => workflow.PeopleNavigation.SelectedEmail == submitted.Basis.Email &&
                Control<TextBlock>("Email").Text == submitted.Basis.Email &&
                workflow.ProfileEdit is null &&
                Control<FrameworkElement>("ProfileForm").Visibility == Visibility.Collapsed &&
                Control<FrameworkElement>("ProfileSessionActions").Visibility == Visibility.Collapsed &&
                Control<FrameworkElement>("ProfileValues").Visibility == Visibility.Visible &&
                Control<Button>("EditProfileButton").IsEnabled;
            string? Bounded(string? value) => value is { Length: > 512 } ? value[..512] : value;
            void ReportBoundary(string boundary)
            {
                var edit = workflow.ProfileEdit;
                var local = workflow.LocalSave;
                var retained = workflow.GetRetainedNoteDraft(NoteScope.Person, noteId);
                var selected = workflow.PersonNotesView.SelectedNote;
                Console.WriteLine("PROFILE-NOTE-REFRESH-BOUNDARY " + JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, boundary, dirtyNote, profileAccepted = ProfileAccepted(),
                    workflowStatus = Bounded(workflow.Status),
                    operationError = Bounded(workflow.OperationError),
                    localSaveError = Bounded(workflow.LocalSaveError),
                    localSave = new
                    {
                        local.Phase, local.Outcome, local.OutstandingRequests, local.CommittedRevision,
                        local.EditsPaused, local.HasUnresolvedOutcome, error = Bounded(local.Error)
                    },
                    profile = new { isNull = edit is null, owner = edit?.Basis.Email, dirty = edit?.IsDirty },
                    controls = new
                    {
                        status = Bounded(status.Text),
                        activity = Bounded(Control<TextBlock>("WorkflowActivity").Text),
                        ready = Control<TextBlock>("WorkflowActivity").Text == "Ready",
                        editEnabled = Control<Button>("EditProfileButton").IsEnabled,
                        saveEnabled = Control<Button>("SaveProfileButton").IsEnabled,
                        editorEnabled = Control<TextBox>("DirectoryNameEditor").IsEnabled,
                        form = Control<FrameworkElement>("ProfileForm").Visibility.ToString(),
                        actions = Control<FrameworkElement>("ProfileSessionActions").Visibility.ToString(),
                        settings = Control<FrameworkElement>("SettingsPanel").Visibility.ToString(),
                        import = Control<FrameworkElement>("ImportPanel").Visibility.ToString()
                    },
                    note = new
                    {
                        expectedId = noteId, selectedId = selected?.Id, owner = selected?.OwnerEmail,
                        retained = retained is not null, saving = retained?.IsSaving,
                        retainedOwner = retained?.Basis.OwnerEmail, retainedId = retained?.Basis.Id,
                        retainedTitleLength = retained?.Title.Length, error = Bounded(retained?.Error),
                        mode = NoteControl<TabControl>("Views").SelectedItem == notes.FindName("EditTab") ? "Edit" : "Read",
                        feedback = Bounded(NoteControl<TextBlock>("NoteStatus").Text)
                    },
                    workflow.CanSafelyClose, workflow.CanEditLocalWorkspace, workflow.CanNavigatePeople,
                    workflow.CanEnterImport, workflow.IsImportOpen,
                    windowVisible = window.IsVisible
                }));
            }
            Invoke("SaveProfileButton");
            try { PumpUntil(ProfileAccepted); }
            catch (TimeoutException)
            {
                ReportBoundary("wait-timeout");
                throw;
            }
            using (var reopened = OpenSmokeWorkspace(folder))
            {
                if (reopened.GetRelationships(submitted.Basis.Email)?.Person != submitted.Draft)
                    throw new InvalidOperationException("Accepted profile did not independently reopen as the exact submitted whole Person.");
                var reopenedPhoto = reopened.GetPhoto(submitted.Basis.Email);
                if ((priorPhoto is null) != (reopenedPhoto is null) ||
                    priorPhoto is not null && reopenedPhoto is not null &&
                    (reopenedPhoto.ContentType != priorPhoto.ContentType ||
                     reopenedPhoto.Width != priorPhoto.Width || reopenedPhoto.Height != priorPhoto.Height ||
                     !reopenedPhoto.Bytes.Span.SequenceEqual(priorPhotoBytes!)))
                    throw new InvalidOperationException("Profile Save changed the independently reopened photo.");
            }
            var expected = dirtyNote ? "# Retained unsaved note" : delivered;
            var reading = NoteControl<FlowDocumentScrollViewer>("Reading").Document;
            var readText = new TextRange(reading.ContentStart, reading.ContentEnd).Text;
            var exactContent = priorMode == NoteControl<TabItem>("EditTab")
                ? NoteControl<TextBox>("MarkdownEditor").Text == expected && NoteControl<TextBox>("NoteTitle").Text == priorTitle
                : NoteControl<TextBlock>("NoteHeading").Text == priorTitle &&
                    expected.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .All(line => readText.Contains(line.TrimStart('#', ' '), StringComparison.Ordinal));
            if (!exactContent || workflow.PersonNotesView.SelectedNote?.Markdown != expected ||
                NoteControl<TabControl>("Views").SelectedItem != priorMode ||
                workflow.PersonNotesView.SelectedNote?.Id != noteId ||
                workflow.PersonNotesView.SelectedNote?.OwnerEmail != submitted.Basis.Email ||
                System.Windows.Automation.AutomationProperties.GetHelpText(NoteControl<TextBlock>("NoteHeading")) != noteId.ToString("D"))
                throw new InvalidOperationException("Profile Save changed exact Person-note identity, owner, writing or editor mode.");
            var retainedDraft = workflow.GetRetainedNoteDraft(NoteScope.Person, noteId);
            if (dirtyNote && (priorDraft is null || retainedDraft is null ||
                    retainedDraft.Basis != priorDraft.Basis || retainedDraft.Title != priorDraft.Title ||
                    retainedDraft.Markdown != priorDraft.Markdown || retainedDraft.Error != priorDraft.Error ||
                    retainedDraft.IsSaving || priorNoteError is null || workflow.LocalSaveError != priorNoteError) ||
                !dirtyNote && retainedDraft is not null)
                throw new InvalidOperationException("Profile Save cleared or changed the independent retained Person-note draft/error.");
            if (ReadSmokePersonNote(folder, noteId).Markdown != delivered)
                throw new InvalidOperationException("Profile Save rewrote delivered Person-note writing.");
            if (!dirtyNote && new[] { notes, Control<FrameworkElement>("TeamNotes") }.Any(control =>
                    ((TextBlock)control.FindName("NoteStatus")).Visibility != Visibility.Collapsed))
                throw new InvalidOperationException("Successful profile save/refresh left routine status in healthy Person/Team reading.");
            if (dirtyNote && (window.FindName("OperationalNotice") is not FrameworkElement { Visibility: Visibility.Visible } ||
                !Control<TextBlock>("NoticeSummary").Text.Contains("NOT saved", StringComparison.Ordinal) ||
                NoteControl<TextBlock>("NoteStatus").Visibility != Visibility.Visible))
                throw new InvalidOperationException("Unrelated profile success concealed the independent failed Person-note writing.");
            if (dirtyNote)
            {
                Invoke("OpenSettingsButton");
                if (NoteControl<TextBox>("MarkdownEditor").Text != "# Retained unsaved note")
                    throw new InvalidOperationException("Opening Settings changed independent writing.");
                var previousPreference = File.ReadAllBytes(preferences);
                var previousStatus = status.Text;
                ChooseCompiledWorkspace(window, null);
                if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Visible ||
                    status.Text != previousStatus || Control<TextBlock>("Email").Text != "avery@example.com" ||
                    NoteControl<TextBox>("MarkdownEditor").Text != "# Retained unsaved note" ||
                    !File.ReadAllBytes(preferences).SequenceEqual(previousPreference) || ReadSmokePersonNote(folder, noteId).Markdown != delivered)
                    throw new InvalidOperationException("Cancelling the picker changed retained writing, status, selection or the startup preference.");
                var blockedFolder = Path.Combine(settings, "blocked-by-note");
                ChooseCompiledWorkspace(window, blockedFolder);
                try { PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled); }
                catch (TimeoutException)
                {
                    ReportBoundary("blocked-workspace-open-settlement-timeout");
                    throw;
                }
                if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Collapsed ||
                    Control<TextBlock>("Email").Text != "avery@example.com" ||
                    NoteControl<TextBox>("MarkdownEditor").Text != "# Retained unsaved note" ||
                    !status.Text.Contains("Person note", StringComparison.Ordinal) || Directory.Exists(blockedFolder) ||
                    !File.ReadAllBytes(preferences).SequenceEqual(previousPreference) || ReadSmokePersonNote(folder, noteId).Markdown != delivered)
                    throw new InvalidOperationException("Picker acceptance bypassed the failed note or stranded its exact writing behind Settings.");
                Invoke("OpenSettingsButton");
                Invoke("OpenImportButton");
                if (Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Collapsed ||
                    Control<TextBlock>("Email").Text != "avery@example.com" ||
                    NoteControl<TextBox>("MarkdownEditor").Text != "# Retained unsaved note" ||
                    !Control<TextBlock>("StatusText").Text.Contains("Person note", StringComparison.Ordinal) ||
                    ReadSmokePersonNote(folder, noteId).Markdown != delivered)
                {
                    ReportBoundary("settings-import-rejection-failed");
                    throw new InvalidOperationException("Settings Import bypassed the failed independent Person-note draft.");
                }
                NoteControl<Button>("DiscardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready");
            }
        }
        VerifyOwnedTeamPresentation(window, folder);
        if (noteRefreshOnly)
        {
            Console.WriteLine("PASS: targeted existing compiled profile-save/note-refresh path; full profile selection retains all companion coverage.");
            return;
        }
        VerifyLocalReconciliationAfterBlockedOpen(window, folder);
        VerifyOwnedNoteRevisions(window, folder, NoteScope.Person);
        VerifyOwnedNoteRevisions(window, folder, NoteScope.Team);
        VerifyPersonNoteUpdateFlush(window, folder);
        VerifyContextReadingLayout(window, Path.Combine(settings, "context-workspace"));
        Console.WriteLine("PASS: compact profile reading/editing, safe Settings Import rejection and profile-save note refresh without showing a window.");
    }

    private static void VerifyContextReadingLayout(Window window, string folder)
    {
        string Writing(string marker) => string.Join("\n\n",
            Enumerable.Range(1, 32).Select(n => $"{marker} paragraph {n}. Keep the decision and its reasoning together."));
        IReadOnlyList<HistoricalTeamContext> captures;
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000003",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
                 {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
                 {"email":"alex@example.com","displayName":"Alex","managerEmail":null}]}
                """).Id) ||
                seed.CreatePersonNote("avery@example.com", "Individual reading", Writing("Private")) is null ||
                seed.CreateTeamNote("avery@example.com", "Owned reading", Writing("Owned")) is null ||
                seed.CreateTeamNote("morgan@example.com", "Former reading", Writing("Frozen")) is null ||
                seed.CreateTeamNote("alex@example.com", "Ancestor reading", Writing("Current")) is null ||
                !seed.EditPersonText("morgan@example.com", PersonTextField.JobTitle, "Former Director") ||
                !seed.EditPersonManager("avery@example.com", null) ||
                !seed.EditPersonArchived("avery@example.com", true) ||
                !seed.EditPersonManager("avery@example.com", "alex@example.com") ||
                !seed.EditPersonArchived("avery@example.com", false) ||
                !seed.EditPersonText("alex@example.com", PersonTextField.JobTitle, "Director"))
                throw new InvalidOperationException("Context reading fixture failed: " + seed.Status);
            captures = seed.GetHistoricalTeamContexts("avery@example.com");
        }
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var person = Control<FrameworkElement>("PersonNotes");
        var team = Control<FrameworkElement>("TeamNotes");
        T Team<T>(string name) where T : FrameworkElement => (T)team.FindName(name);
        Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
        var tree = Control<TreeView>("PeopleList");
        var avery = tree.Items.Cast<ReportingNode>().SelectMany(n => n.Reports.Prepend(n))
            .Single(n => n.Person.Email == "avery@example.com");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, avery, TreeView.SelectedItemChangedEvent));
        if (Team<Expander>("CurrentTeamContext").IsExpanded || Team<Expander>("OtherNotes").IsExpanded ||
            Control<Expander>("HistoryAccess").IsExpanded ||
            !Team<TextBlock>("OwnedReviewWarnings").Text.Contains("Owned reading", StringComparison.Ordinal) ||
            !Team<TextBlock>("OwnedReviewWarnings").Text.Contains("avery@example.com", StringComparison.Ordinal) ||
            !Team<TextBlock>("InheritedReviewWarnings").Text.Contains("Ancestor reading", StringComparison.Ordinal) ||
            !Team<TextBlock>("InheritedReviewWarnings").Text.Contains("alex@example.com", StringComparison.Ordinal) ||
            Team<TextBlock>("InheritedReviewWarnings").Visibility != Visibility.Visible ||
            !Team<TextBlock>("ContextAttribution").Text.Contains("alex@example.com", StringComparison.Ordinal))
            throw new InvalidOperationException("Collapsed context must retain current owner attribution and both owned/inherited review warnings.");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 900));
        root.Arrange(new Rect(0, 0, 1100, 900));
        root.UpdateLayout();
        var context = Team<Expander>("CurrentTeamContext");
        var header = VisualDescendants(context).OfType<ToggleButton>().First();
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(header).GetPattern(PatternInterface.Toggle)).Toggle();
        if (!context.IsExpanded)
            throw new InvalidOperationException("The real disclosure header did not expand its bound current context.");
        Control<Expander>("HistoryAccess").IsExpanded = true;
        var inherited = Team<FlowDocumentScrollViewer>("InheritedReading");
        var frozen = Control<FlowDocumentScrollViewer>("HistoricalTeamReading");
        string Text(FlowDocumentScrollViewer reader) => new TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text;
        if (Control<TextBlock>("HistoricalTeamSummary").Text != "Historical captures: 2")
            throw new InvalidOperationException("The capture summary must count captures without duplicating technical evidence.");
        var disclosures = new List<Expander>();
        foreach (var capture in captures)
        {
            var section = frozen.Document.Blocks.OfType<Section>().Single(s => Equals(s.Tag, capture.Id));
            var disclosure = (Expander)section.Blocks.OfType<BlockUIContainer>().Single().Child;
            disclosures.Add(disclosure);
            var evidence = (TextBox)disclosure.Content;
            var reading = new TextRange(section.ContentStart, section.ContentEnd).Text;
            if (disclosure.IsExpanded || disclosure.Header as string != "Capture details" ||
                System.Windows.Automation.AutomationProperties.GetAutomationId(disclosure) != $"HistoricalCaptureDetails-{capture.Id}" ||
                string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(disclosure)) ||
                !evidence.IsReadOnly || !evidence.Focusable || !KeyboardNavigation.GetIsTabStop(evidence) ||
                !evidence.Text.Contains(capture.DisplayProvenance, StringComparison.Ordinal) ||
                reading.Contains("Prior evidence:", StringComparison.Ordinal) ||
                !reading.Contains("Captured locally", StringComparison.Ordinal) ||
                !reading.Contains("Exact effective date unknown", StringComparison.Ordinal))
                throw new InvalidOperationException("Capture evidence must be separately named, collapsed and copyable without replacing honest capture attribution.");
            foreach (var note in capture.TeamNotes)
            {
                var noteSection = section.Blocks.OfType<Section>().Single(s => Equals(s.Tag, note.Id));
                var noteText = new TextRange(noteSection.ContentStart, noteSection.ContentEnd).Text;
                if (!noteText.Contains($"Frozen Team note: {note.Title}", StringComparison.Ordinal) ||
                    !noteText.Contains("Owned by Morgan (morgan@example.com)", StringComparison.Ordinal) ||
                    !noteText.Contains("Needs review", StringComparison.Ordinal) ||
                    noteText.Contains(note.Version, StringComparison.Ordinal) || noteText.Contains(note.Id.ToString(), StringComparison.Ordinal) ||
                    !evidence.Text.Contains(note.Version, StringComparison.Ordinal) ||
                    !evidence.Text.Contains(note.Id.ToString(), StringComparison.Ordinal) ||
                    noteSection.BorderThickness.Left <= 0 ||
                    !noteText.Contains("Frozen paragraph 32", StringComparison.Ordinal))
                    throw new InvalidOperationException("Frozen writing must retain its boundary, exact owner and captured review warning without technical note labels.");
            }
            if (capture.TeamNotes.Count == 0 &&
                (!reading.Contains("(explicit no-Manager)", StringComparison.Ordinal) ||
                 !reading.Contains("Archival closure", StringComparison.Ordinal) ||
                 !reading.Contains("No active Team notes were locally known", StringComparison.Ordinal)))
                throw new InvalidOperationException("A capture with no known notes must retain its former chain and evidence.");
        }
        if (!Text(inherited).Contains("Current paragraph 32", StringComparison.Ordinal) ||
            Text(inherited).Contains("Frozen paragraph", StringComparison.Ordinal) ||
            !Text(frozen).Contains("Frozen paragraph 32", StringComparison.Ordinal) ||
            !Text(frozen).Contains("morgan@example.com", StringComparison.Ordinal) ||
            Text(frozen).Contains("Current paragraph", StringComparison.Ordinal))
            throw new InvalidOperationException("Long current and frozen reading lost exact writing or owner attribution.");
        var viewport = Control<ScrollViewer>("PersonScroll");
        foreach (var size in new[] { new Size(700, 500), new Size(1100, 900), new Size(1920, 1040) })
        {
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Context layout must remain invisible without a native main-window handle.");
            foreach (var reader in new[] { (FlowDocumentScrollViewer)person.FindName("Reading"),
                Team<FlowDocumentScrollViewer>("Reading"), inherited, frozen })
            {
                if (reader.ActualWidth <= 0 || reader.ActualWidth > 600 || reader.ActualHeight <= 600 ||
                    reader.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ||
                    reader.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled ||
                    reader.TransformToAncestor(viewport).Transform(new Point(reader.ActualWidth, 0)).X > viewport.ViewportWidth + 1)
                    throw new InvalidOperationException($"Long owned/current/frozen prose must size to content in the single bounded reading scroll at {size}: {reader.Name} {reader.RenderSize}.");
            }
            var title = Team<TextBlock>("ContextHeading").TransformToAncestor(team).Transform(new Point());
            var prose = inherited.TransformToAncestor(team).Transform(new Point());
            if (Math.Abs(title.X - prose.X) > 2 ||
                Math.Abs(person.ActualWidth - Control<GroupBox>("NotesPane").ActualWidth) > 1 ||
                Control<TextBlock>("HistoricalTeamSummary").ActualWidth > 600 ||
                Team<TextBlock>("InheritedSummary").ActualWidth > 600)
                throw new InvalidOperationException($"Full-width context sections must align their title and bounded prose at {size}: title={title.X}, prose={prose.X}.");
            foreach (var disclosure in disclosures)
            {
                var toggle = VisualDescendants(disclosure).OfType<ToggleButton>().Single();
                var provider = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle);
                var before = Text(frozen);
                provider.Toggle();
                root.UpdateLayout();
                var evidence = (TextBox)disclosure.Content;
                if (!disclosure.IsExpanded || evidence.ActualHeight <= 0 || disclosure.ActualWidth > 600 ||
                    evidence.TextWrapping != TextWrapping.Wrap || evidence.ExtentWidth > evidence.ViewportWidth + 1 ||
                    disclosure.TransformToAncestor(viewport).Transform(new Point(disclosure.ActualWidth, 0)).X > viewport.ViewportWidth + 1 ||
                    Text(frozen) != before)
                    throw new InvalidOperationException($"Expanding capture evidence must wrap within the shared reading scroll without changing frozen writing at {size}.");
                provider.Toggle();
                root.UpdateLayout();
            }
        }
        foreach (var notes in new[] { person, team })
        {
            var tab = (TabItem)notes.FindName("ReadTab");
            var reader = (FlowDocumentScrollViewer)notes.FindName("Reading");
            var body = reader.Document.Blocks.OfType<Paragraph>().First();
            if (!tab.IsSelected || !Equals(body.Foreground, window.Foreground) ||
                !Equals(reader.Document.Foreground, window.Foreground))
                throw new InvalidOperationException($"Selected note Read styling lost body ink: selected={tab.IsSelected}; " +
                    $"body={body.Foreground}; document={reader.Document.Foreground}; window={window.Foreground}.");
            if (!VisualDescendants(tab).OfType<TextBlock>().Any(label =>
                    label.Text == "Read" && Equals(label.Foreground, window.FindResource("AccentBrush"))))
                throw new InvalidOperationException("The selected note header lost its accent while restoring normal body ink.");
        }
        Console.WriteLine("PASS: actual compiled long Person/owned/current/frozen reading stays bounded in one scroll at 700/1100/maximized sizes, with collapsed attributed warnings.");
    }

    private static void VerifyImportPreviewKey(Window window, string folder)
    {
        var before = WorkspaceFixtures.Bytes(folder);
        var import = Path.Combine(Path.GetDirectoryName(folder)!, "enter-preview.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000073",
             "observedAt":"2026-11-02T17:00:00Z","people":[{"email":"avery@example.com","officeNumber":"123"}]}
            """);
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)window.FindName("OpenImportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var path = (TextBox)window.FindName("ImportPath");
        path.Text = import;
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice,
            new UnshownInputSource { RootVisual = (Visual)window.Content }, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
        path.RaiseEvent(enter);
        PumpUntil(() => ((Button)window.FindName("ApplyButton")).IsEnabled);
        if (!enter.Handled || !((ListBox)window.FindName("PreviewText")).Items.Cast<string>().Any(text =>
                text.Contains("123", StringComparison.Ordinal)))
            throw new InvalidOperationException("Import-path Enter did not produce the same explicit reviewed preview.");
        AssertWorkspaceBytes(folder, before);
        var back = LogicalDescendants((DependencyObject)window.FindName("ImportPanel")).OfType<Button>()
            .Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "BackToPeople");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (((Button)window.FindName("ApplyButton")).IsEnabled)
            throw new InvalidOperationException("Leaving the preview retained an applicable unreviewed Import.");
        Console.WriteLine("PASS: compiled Import-path Enter previews without applying or changing saved Workspace bytes.");
    }

    private static void VerifyRejectedPickerChoice(Window window, string folder, string preferences)
    {
        var savedPreference = File.ReadAllBytes(preferences);
        var savedWorkspace = WorkspaceFixtures.Bytes(folder);
        var invalid = Path.Combine(Path.GetDirectoryName(folder)!, "invalid-picker-choice");
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "workspace.json"), "{invalid");
        var invalidBytes = WorkspaceFixtures.Bytes(invalid);
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, invalid);
        PumpUntil(() => ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready" &&
            ((TextBlock)window.FindName("StatusText")).Text.Contains("could not be opened", StringComparison.Ordinal));
        if (((FrameworkElement)window.FindName("OperationalNotice")).Visibility != Visibility.Visible ||
            ((TextBlock)window.FindName("WorkspacePathLabel")).Text != "Workspace folder (unavailable)" ||
            !File.ReadAllBytes(preferences).SequenceEqual(savedPreference))
            throw new InvalidOperationException("A rejected picker choice claimed a successful current Workspace or replaced the successful startup preference.");
        AssertWorkspaceBytes(folder, savedWorkspace);
        AssertWorkspaceBytes(invalid, invalidBytes);
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready" &&
            ((TextBlock)window.FindName("StatusText")).Text == "Local SQLite Workspace opened.");
        Console.WriteLine("PASS: rejected picker acceptance preserves both Workspace byte sets and the last successful startup preference; native selection remains hosted-only evidence.");
    }

    private static void VerifyPreferredNameEntry(Window window, string folder, HeldNotePublisher publisher)
    {
        T Control<T>(string name) where T : FrameworkElement => (T?)window.FindName(name)
            ?? throw new InvalidOperationException("Missing explicit Preferred-name control: " + name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        var editor = Control<TextBox>("PreferredName");
        var save = Control<Button>("SavePreferredNameButton");
        var panel = Control<FrameworkElement>("PreferredNameEditorPanel");
        var error = Control<TextBlock>("PreferredNameError");
        var details = Control<TextBlock>("PreferredNameErrorDetails");
        void Settled() => PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
        void ObserveNoAutosave()
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            PumpUntil(() => elapsed.ElapsedMilliseconds >= 650);
            using var saved = OpenSmokeWorkspace(folder);
            if (workflow.GetRelationships("avery@example.com")!.Person.PreferredName is not null ||
                saved.GetRelationships("avery@example.com")!.Person.PreferredName is not null)
                throw new InvalidOperationException("Typing or generic draining published an unconfirmed Preferred name.");
        }
        Invoke("EditPreferredNameButton");
        if (editor.Text != "Avery" || !editor.IsEnabled || !save.IsEnabled ||
            !Control<Button>("CancelPreferredNameButton").IsEnabled || !workflow.CanSafelyClose)
            throw new InvalidOperationException("The compact entry did not present an enabled, unsubmitted Directory-name default.");
        ObserveNoAutosave();
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settled();
        if (panel.Visibility != Visibility.Collapsed || workflow.GetRelationships("avery@example.com")!.Person.PreferredName is not null)
            throw new InvalidOperationException("Saving the unchanged Directory-name default created an override.");
        Invoke("EditPreferredNameButton");
        editor.Text = "Unconfirmed entry";
        ObserveNoAutosave();
        Invoke("OpenSettingsButton");
        Invoke("OpenSettingsButton");
        if (panel.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Settings must cancel only the small entry.");
        ObserveNoAutosave();

        Invoke("EditPreferredNameButton");
        editor.Text = " ";
        var beforeFailure = WorkspaceFixtures.Bytes(folder);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settled();
        if (panel.Visibility != Visibility.Visible || editor.Text != " " ||
            error.Visibility != Visibility.Visible || !details.Text.Contains("preferredName", StringComparison.Ordinal))
            throw new InvalidOperationException("An explicit rejected save must retain input and its inline error.");
        if (Control<TextBlock>("NoticeSummary").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 1 ||
            error.Text.Contains("expected nonblank", StringComparison.Ordinal))
            throw new InvalidOperationException("One rejected Preferred name repeated global status or exposed technical schema prose as its local summary.");
        var disclosure = Control<Expander>("PreferredNameFailureDetails");
        disclosure.ApplyTemplate();
        var toggle = VisualDescendants(disclosure).OfType<ToggleButton>().First();
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
        if (!disclosure.IsExpanded || !ReferenceEquals(Control<Popup>("PreferredNamePopup").Child, panel) ||
            !LogicalDescendants(panel).Contains(disclosure) || !LogicalDescendants(disclosure).Contains(details) ||
            !details.Focusable || !KeyboardNavigation.GetIsTabStop(details) ||
            !System.Windows.Automation.AutomationProperties.GetHelpText(error).Contains("preferredName", StringComparison.Ordinal) ||
            editor.Text != " " || workflow.CanSafelyClose)
            throw new InvalidOperationException("Retained-name technical Details must be accessible inside the same entry without cancelling writing.");
        AssertWorkspaceBytes(folder, beforeFailure);
        if (Control<TextBlock>("LocalWritingDetails").Text != workflow.LocalSaveError)
            throw new InvalidOperationException("Concise notice presentation dropped retained technical save evidence.");
        Invoke("FavoriteButton");
        Settled();
        if (error.Visibility != Visibility.Visible || !details.Text.Contains("preferredName", StringComparison.Ordinal))
            throw new InvalidOperationException("An unrelated successful Favorite action hid the failed Preferred-name entry.");
        Invoke("CancelPreferredNameButton");
        if (panel.Visibility != Visibility.Collapsed || !workflow.CanSafelyClose ||
            workflow.GetRelationships("avery@example.com")!.Person.PreferredName is not null)
            throw new InvalidOperationException("Preferred-name Cancel must discard only that failed entry without saving.");
        if (Control<FrameworkElement>("OperationalNotice").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Preferred-name cancellation left a routine global notice in healthy reading.");

        Invoke("EditPreferredNameButton");
        editor.Text = "Explicit name";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settled();
        if (panel.Visibility != Visibility.Collapsed || workflow.GetRelationships("avery@example.com")!.Person.PreferredName != "Explicit name")
            throw new InvalidOperationException("Successful explicit Save must publish and close the entry.");
        Invoke("EditPreferredNameButton");
        if (editor.Text != "Explicit name") throw new InvalidOperationException("Existing Preferred name did not take precedence on entry.");
        editor.Clear();
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settled();
        if (panel.Visibility != Visibility.Collapsed || workflow.GetRelationships("avery@example.com")!.Person.PreferredName is not null)
            throw new InvalidOperationException("Explicit empty Save must clear Preferred name.");
        foreach (var cancel in new[] { false, true })
        {
            publisher.Entered.Reset();
            publisher.Release.Reset();
            publisher.HoldNextProfile = true;
            Invoke("EditPreferredNameButton");
            editor.Text = cancel ? "Admitted before Cancel" : "  aVeRy  ";
            try
            {
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!publisher.Entered.Wait(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Preferred-name Save did not reach the held local SQLite commit.");
                if (cancel)
                {
                    editor.Text = "Cancelled newer writing";
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    Invoke("CancelPreferredNameButton");
                    if (elapsed.ElapsedMilliseconds > 1000 || panel.Visibility != Visibility.Collapsed)
                        throw new InvalidOperationException("Cancel waited behind admitted I/O instead of closing only the entry.");
                    var rejected = false;
                    CancelEventHandler observe = (_, e) => rejected = e.Cancel;
                    window.Closing += observe;
                    try { window.Close(); }
                    finally { window.Closing -= observe; }
                    if (!rejected || Control<Button>("OpenWorkspaceButton").IsEnabled)
                        throw new InvalidOperationException("Cancel falsely enabled close before admitted Preferred-name I/O completed.");
                    Invoke("CancelExitButton");
                }
                else editor.Text = "Newer retained value";
            }
            finally { publisher.Release.Set(); }
            Settled();
            var elapsedAfterSave = System.Diagnostics.Stopwatch.StartNew();
            PumpUntil(() => elapsedAfterSave.ElapsedMilliseconds >= 650);
            if (workflow.GetRelationships("avery@example.com")!.Person.PreferredName !=
                (cancel ? "Admitted before Cancel" : null))
                throw new InvalidOperationException("Save completion dropped admitted writing or implicitly saved newer input.");
            if (cancel)
            {
                if (panel.Visibility != Visibility.Collapsed || !workflow.CanSafelyClose)
                    throw new InvalidOperationException("An admitted save reopened its cancelled entry or retained a false unsaved error.");
            }
            else
            {
                if (panel.Visibility != Visibility.Visible || editor.Text != "Newer retained value")
                    throw new InvalidOperationException("Save completion discarded newer Preferred-name input.");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settled();
                if (workflow.GetRelationships("avery@example.com")!.Person.PreferredName != "Newer retained value" ||
                    panel.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("The next explicit Save did not use the exact accepted base for newer typing.");
            }
        }
        Invoke("EditPreferredNameButton");
        editor.Clear();
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settled();
        using var entryPeer = new SmokeSnapshotPeer(folder);
        if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
        Invoke("EditPreferredNameButton");
        editor.Text = "Retained stale entry";
        entryPeer.Publish(source => source.EditPreferredName("avery@example.com", "Delivered during entry"));
        workflow.SyncNow();
        if (panel.Visibility != Visibility.Visible || editor.Text != "Retained stale entry" ||
            workflow.GetRelationships("avery@example.com")!.Person.PreferredName is not null)
            throw new InvalidOperationException("Incoming snapshot replaced an unconfirmed Preferred-name entry or its saved base.");
        Invoke("CancelPreferredNameButton");
        if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Status);
        PumpUntil(() => Control<TextBlock>("ProfileHeading").Text == "Delivered during entry");
        Invoke("EditPreferredNameButton");
        if (editor.Text != "Delivered during entry")
            throw new InvalidOperationException("Reopening after cancellation did not intentionally accept the delivered name.");
        editor.Clear();
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settled();
        var notes = Control<FrameworkElement>("PersonNotes");
        ((TabControl)notes.FindName("Views")).SelectedItem = notes.FindName("EditTab");
        var noteEditor = (TextBox)notes.FindName("MarkdownEditor");
        Invoke("EditPreferredNameButton");
        editor.Text = "Unconfirmed alongside note writing";
        publisher.Entered.Reset();
        publisher.Release.Reset();
        publisher.HoldNextNote = true;
        Task<bool>? noteSave = null;
        try
        {
            noteEditor.Text = "Independent admitted note writing";
            noteSave = (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
            if (!publisher.Entered.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Independent note save did not reach the held publisher.");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Invoke("CancelPreferredNameButton");
            if (elapsed.ElapsedMilliseconds > 1000 || panel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Preferred-name cancellation waited behind independent note I/O.");
        }
        finally { publisher.Release.Set(); }
        PumpUntil(() => noteSave!.IsCompleted);
        if (!noteSave!.GetAwaiter().GetResult() || workflow.GetRelationships("avery@example.com")!.Person.PreferredName is not null)
            throw new InvalidOperationException("Preferred-name cancellation affected independent note publication.");
        noteEditor.Text = "# Saved note";
        var restoreNote = (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
        PumpUntil(() => restoreNote.IsCompleted);
        if (!restoreNote.GetAwaiter().GetResult()) throw new InvalidOperationException(workflow.Status);
        ((TabControl)notes.FindName("Views")).SelectedItem = notes.FindName("ReadTab");
        Console.WriteLine("PASS: invisible compiled Preferred-name typing does not save; Settings/Cancel isolate dismissal, failure retains input, Save/clear publish explicitly.");
        Console.WriteLine("PASS: admitted Preferred-name SQLite commit survives Cancel without blocking dismissal; newer input waits for explicit Save; pending snapshot adoption waits for deliberate entry cancellation.");
    }

    private static void VerifyPhotoViewportLayout(Window window)
    {
        var photo = (FrameworkElement)window.FindName("Photo");
        var image = (Image)photo.FindName("PhotoImage");
        var show = photo.GetType().GetMethod("Show") ?? throw new MissingMethodException("Photo.Show");
        var name = ((TextBlock)window.FindName("ProfileHeading")).Text;
        var root = (FrameworkElement)window.Content;
        try
        {
            foreach (var (width, height) in new[] { (300, 300), (300, 150), (150, 300), (72, 72), (72, 36) })
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
                    PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4)));
                using var bytes = new MemoryStream();
                encoder.Save(bytes);
                show.Invoke(photo, [new PersonPhoto("image/png", width, height, bytes.ToArray()), name]);
                root.Measure(new Size(700, 900));
                root.Arrange(new Rect(0, 0, 700, 900));
                root.UpdateLayout();
                if (Math.Abs(image.ActualWidth - 144) > 1 || Math.Abs(image.ActualHeight - 144) > 1)
                    throw new InvalidOperationException($"Photo image geometry exceeds its accessible viewport for {width}x{height}: {image.RenderSize}.");
            }
        }
        finally { show.Invoke(photo, [null, name]); }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The photo-layout regression must not show a window or create a native handle.");
        Console.WriteLine("PASS: actual compiled square/non-square photo image geometry stays within its 144-DIP viewport; rendered crop/automation require hosted evidence.");
    }

    private static void VerifyHeaderTextAndReadingOrder(Window window)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var fields = new[] { (Editor: "JobTitle", Value: "JobTitleValue"),
            (Editor: "OfficeBuilding", Value: "OfficeBuildingValue"), (Editor: "OfficeNumber", Value: "OfficeNumberValue") };
        var multiline = "First header line\r\n" + string.Join("\r\n", Enumerable.Repeat("Additional header line", 20)) +
            "\r\nLast header line";
        var root = (FrameworkElement)window.Content;
        var manager = Control<Button>("Manager");
        var originalManager = manager.Content;
        var failures = new List<string>();
        Invoke("EditProfileButton");
        var original = fields.ToDictionary(field => field.Editor, field => Control<TextBox>(field.Editor).Text);
        try
        {
            foreach (var field in fields) Control<TextBox>(field.Editor).Text = multiline;
            Invoke("SaveProfileButton");
            PumpUntil(() => Control<FrameworkElement>("ProfileSessionActions").Visibility == Visibility.Collapsed &&
                fields.All(field => Control<TextBlock>(field.Value).Text == multiline));
            foreach (var width in new[] { 700d, 1100d, 1920d })
            {
                manager.Content = multiline;
                root.Measure(new Size(width, 500));
                root.Arrange(new Rect(0, 0, width, 500));
                root.UpdateLayout();
                var lineHeight = Control<TextBlock>("Email").ActualHeight;
                foreach (var field in fields)
                {
                    var value = Control<TextBlock>(field.Value);
                    if (value.ActualHeight > lineHeight + 2)
                        failures.Add($"Multiline {field.Value} grew beyond one header line at {width}: {value.ActualHeight}.");
                    if (value.Text != multiline || value.ToolTip as string != "Click to copy\n" + multiline ||
                        !new TextBlockAutomationPeer(value).GetName().Contains("Last header line", StringComparison.Ordinal))
                        failures.Add($"Bounded {field.Value} lost its full Copy/tooltip/accessible value.");
                }
                if (manager.ActualHeight > lineHeight + 4 ||
                    !new ButtonAutomationPeer(manager).GetName().Contains("Last header line", StringComparison.Ordinal))
                    failures.Add($"Multiline Manager must remain bounded with its full accessible name: {manager.ActualHeight}.");
                var office = (FrameworkElement)Control<TextBlock>("OfficeBuildingValue").Parent;
                var order = new FrameworkElementAutomationPeer(office).GetChildren()?
                    .Select(peer => peer.GetAutomationId())
                    .Where(id => id is "OfficeBuildingValue" or "OfficeNumberValue").ToArray() ?? [];
                if (!order.SequenceEqual(new[] { "OfficeBuildingValue", "OfficeNumberValue" }))
                    failures.Add("Office automation reading order must be building then room: " + string.Join(", ", order));
            }
        }
        finally
        {
            manager.Content = originalManager;
            Invoke("EditProfileButton");
            foreach (var field in fields) Control<TextBox>(field.Editor).Text = original[field.Editor];
            Invoke("SaveProfileButton");
            PumpUntil(() => Control<FrameworkElement>("ProfileSessionActions").Visibility == Visibility.Collapsed);
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS: multiline header values stay bounded without losing full text; Office automation order matches visual building/room order.");
    }

    private static void VerifyCompactIdentityStates(Window window, string folder)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var root = (FrameworkElement)window.Content;
        var identity = Control<Grid>("PersonIdentity");
        var heading = Control<TextBlock>("ProfileHeading");
        var marker = Control<TextBlock>("PersonArchivalStatus");
        var scroll = Control<ScrollViewer>("PersonScroll");
        var results = Control<ListBox>("SearchResults");
        var failures = new List<string>();
        void Layout(double width = 684, double height = 461)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }
        void DragPeople(double delta)
        {
            var divider = Control<GridSplitter>("PeopleDivider");
            divider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            divider.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Layout(1920, 1040);
            divider.RaiseEvent(new DragCompletedEventArgs(delta, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Layout(1920, 1040);
        }
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        foreach (var state in CompactIdentityStateCases)
        {
            Control<TextBox>("SearchQuery").Text = state.Email;
            PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == state.Email));
            results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result => result.Person.Email == state.Email);
            results.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new UnshownInputSource { RootVisual = root }, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            try { PumpUntil(() => Control<TextBlock>("Email").Text == state.Email); }
            catch (TimeoutException)
            {
                var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
                Console.WriteLine("COMPACT-IDENTITY-FAILURE " + JsonSerializer.Serialize(new
                {
                    expected = state.Email, actual = Control<TextBlock>("Email").Text,
                    query = Control<TextBox>("SearchQuery").Text,
                    selected = (results.SelectedItem as PersonSearchResult)?.Person.Email,
                    highlighted = model.PeopleNavigation.HighlightedResult?.Person.Email,
                    results = results.Items.Cast<PersonSearchResult>().Select(result => result.Person.Email).ToArray(),
                    activity = Control<TextBlock>("WorkflowActivity").Text,
                    openEnabled = Control<Button>("OpenWorkspaceButton").IsEnabled,
                    searchEnabled = Control<TextBox>("SearchQuery").IsEnabled, resultsEnabled = results.IsEnabled,
                    profileOwner = model.ProfileEdit?.Basis.Email,
                    profileActions = Control<FrameworkElement>("ProfileSessionActions").Visibility.ToString(),
                    profileEditEnabled = Control<Button>("EditProfileButton").IsEnabled,
                    model.LocalSave, navigation = model.PeopleNavigation.SelectedEmail,
                    status = Control<TextBlock>("StatusText").Text
                }));
                throw;
            }
            Layout();
            var fullName = state.PreferredName ?? state.DirectoryName;
            var manager = Control<Button>("Manager");
            var managerEmail = state.ArchivedManager ? "header-archived-manager@example.com" : "header-manager@example.com";
            if (manager.Content is not string relation ||
                state.Archived && !relation.StartsWith("Last stored: ", StringComparison.Ordinal) ||
                state.ArchivedManager && !relation.Contains("\u2014 Archived", StringComparison.Ordinal) ||
                manager.ToolTip as string != $"{relation} ({managerEmail})")
                failures.Add($"Manager tooltip lost full relationship qualifiers for {state.Email}.");
            if (heading.Text != fullName || heading.ToolTip as string != fullName ||
                !new TextBlockAutomationPeer(heading).GetName().Contains(fullName.Split("\r\n").Last(), StringComparison.Ordinal))
                failures.Add($"Compact identity lost the full saved name for {state.Email}.");
            if (heading.ActualHeight > heading.FontSize * heading.FontFamily.LineSpacing + 1)
                failures.Add($"Compact heading adds extra lines for {state.Email}: {heading.ActualHeight}.");
            foreach (var (width, height) in new[] { (684d, 461d), (700d, 500d), (1100d, 900d),
                (959d, 900d), (960d, 900d), (961d, 900d), (1920d, 1040d) })
            {
                Layout(width, height);
                var category = Control<TextBlock>("PersonCategoryLabel");
                var badge = Bounds(category);
                var name = Bounds(heading);
                var peer = new TextBlockAutomationPeer(category);
                if (category.Text != state.Category || peer.GetName() != $"Person category: {state.Category}" ||
                    !category.IsDescendantOf(identity) || badge.Width <= 0 || badge.Left < name.Right - 1 ||
                    badge.Top < name.Top - 1 || badge.Bottom > name.Bottom + 1 ||
                    badge.Right > Bounds(identity).Right + 1 ||
                    identity.ActualHeight > Control<FrameworkElement>("Photo").ActualHeight + 1 ||
                    scroll.ScrollableWidth > 0)
                    failures.Add($"Category badge must remain readable beside the full accessible name within the photo-height header at {width} for {state.Email}: badge={badge}, name={name}.");
            }
            Layout();
            if ((marker.Visibility == Visibility.Visible) != state.Archived ||
                state.Archived && (marker.Text != "Archived Person" || marker.ToolTip is not string tip ||
                    !tip.Contains("retained", StringComparison.Ordinal) || !marker.IsDescendantOf(identity)))
                failures.Add($"Archived identity lost its visible retained-data marker for {state.Email}.");
            if (state.Archived)
            {
                Layout(1920, 1040);
                var people = Control<GroupBox>("PeoplePanel");
                var originalPeople = people.ActualWidth;
                DragPeople(Control<Grid>("ExplorerPanel").ActualWidth - people.ActualWidth);
                if (Math.Abs(scroll.ActualWidth - 408) > 2 ||
                    identity.ActualHeight > Control<FrameworkElement>("Photo").ActualHeight + 1 ||
                    marker.ActualWidth <= 0)
                    failures.Add($"Narrow archived reading exceeds its photo-height header for {state.Email}: " +
                        $"viewport={scroll.ActualWidth}, identity={identity.ActualHeight}, marker={marker.RenderSize}.");
                DragPeople(originalPeople - people.ActualWidth);
                Layout();
            }
            Invoke("EditProfileButton");
            PumpUntil(() => Control<FrameworkElement>("ProfileSessionActions").Visibility == Visibility.Visible);
            try
            {
                Control<TextBox>("OfficeNumber").Text = "Unsaved compact-state office";
                Layout();
                if (identity.ActualHeight > Control<FrameworkElement>("Photo").ActualHeight + 1)
                    failures.Add($"Compact state grew beyond its photo-height header for {state.Email}: {identity.ActualHeight}.");
                foreach (var name in new[] { "DirectoryNameEditor", "Alias", "JobTitle", "Department", "OfficeBuilding",
                    "OfficeNumber", "OfficeState", "OfficeCountry", "LinkedInProfileUrl", "ManagerChoices" })
                {
                    var editor = Control<Control>(name);
                    editor.BringIntoView();
                    Layout();
                    var bounds = Bounds(editor);
                    var viewport = Bounds(scroll);
                    if (bounds.Top < viewport.Top - 1 || bounds.Bottom > viewport.Bottom + 1)
                        failures.Add($"Dirty compact state clips {name} for {state.Email}: editor={bounds}, viewport={viewport}.");
                }
            }
            finally { Invoke("CancelProfileButton"); }
            if (state.Email == "V-header@example.com")
            {
                using var provider = new SmokeSnapshotPeer(folder);
                var workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
                if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
                provider.Publish(source => source.EditPreferredName(state.Email, "Delivered Vendor") &&
                    source.EditPersonArchived(state.Email, true));
                if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Synchronization.Error);
                PumpUntil(() => heading.Text == "Delivered Vendor" && marker.Visibility == Visibility.Visible);
                if (Control<TextBlock>("PersonCategoryLabel").Text != "Vendor")
                    failures.Add("Accepted external name/archival delivery left a stale selected category.");
                provider.Publish(source => source.EditPersonArchived(state.Email, false) &&
                    source.EditPreferredName(state.Email, null));
                if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Synchronization.Error);
                PumpUntil(() => heading.Text == fullName && marker.Visibility == Visibility.Collapsed);
                if (new TextBlockAutomationPeer(Control<TextBlock>("PersonCategoryLabel")).GetName() != "Person category: Vendor")
                    failures.Add("Accepted external reactivation lost the accessible selected category.");
            }
        }
        Control<TextBox>("SearchQuery").Text = "";
        var tree = Control<TreeView>("PeopleList");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!,
            tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "avery@example.com"),
            TreeView.SelectedItemChangedEvent));
        PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Compact identity-state checks must remain invisible without a native handle.");
        Console.WriteLine("PASS: Employee/Vendor badges remain accessible beside full multiline names in the anchored photo footprint across the size matrix; archived identity and dirty minimum-client editors remain usable.");
    }

    private static void VerifyIdentityContext(Window window)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var root = (FrameworkElement)window.Content;
        var identity = Control<Grid>("PersonIdentity");
        var building = Control<TextBlock>("OfficeBuildingValue");
        var number = Control<TextBlock>("OfficeNumberValue");
        if (!Control<Button>("Manager").IsDescendantOf(identity) ||
            !building.IsDescendantOf(identity) || !number.IsDescendantOf(identity) ||
            !Control<TextBlock>("DepartmentValue").IsDescendantOf(Control<Panel>("FactsGrid")) ||
            !Control<Panel>("DepartmentValueRow").Children.OfType<TextBlock>().Any(label => label.Text == "Department"))
            throw new InvalidOperationException("Manager and Office belong in identity; Department needs a labelled facts slot.");
        foreach (var (officeBuilding, officeNumber) in new[]
        {
            ("Juniper", "009C"), ("Juniper", ""), ("", ""),
            ("South campus research and collaboration building with a long descriptive location", "009C"),
            ("", "007B")
        })
        {
            Invoke("EditProfileButton");
            if (!Control<Button>("Manager").IsDescendantOf(identity))
                throw new InvalidOperationException("Editing must retain anchored saved-Manager navigation.");
            Control<TextBox>("OfficeBuilding").Text = officeBuilding;
            Control<TextBox>("OfficeNumber").Text = officeNumber;
            Invoke("SaveProfileButton");
            PumpUntil(() => Control<FrameworkElement>("ProfileSessionActions").Visibility == Visibility.Collapsed &&
                building.Text == (officeBuilding.Length == 0 ? "—" : officeBuilding) &&
                number.Text == (officeNumber.Length == 0 ? "—" : officeNumber));
            if ((building.Visibility == Visibility.Visible) != (officeBuilding.Length > 0 || officeNumber.Length == 0) ||
                (number.Visibility == Visibility.Visible) != (officeNumber.Length > 0) ||
                (Control<TextBlock>("OfficeSeparator").Visibility == Visibility.Visible) !=
                    (officeBuilding.Length > 0 && officeNumber.Length > 0))
                throw new InvalidOperationException("Office must join available building/room values without stray separators or missing-part dashes.");
            foreach (var width in new[] { 700d, 1100d, 1920d })
            {
                root.Measure(new Size(width, 900));
                root.Arrange(new Rect(0, 0, width, 900));
                root.UpdateLayout();
                var manager = Control<Button>("Manager");
                if (manager.ActualWidth > manager.DesiredSize.Width + 1)
                    throw new InvalidOperationException($"Manager's hit target stretches beyond its name at {width}: actual={manager.ActualWidth}, desired={manager.DesiredSize.Width}.");
                if (identity.ActualHeight > Control<FrameworkElement>("Photo").ActualHeight + 1)
                    throw new InvalidOperationException($"Header context grew beyond the existing photo footprint at {width}: {identity.ActualHeight}; " +
                        string.Join("; ", new[] { "ProfileHeading", "JobTitleValue", "Email", "ManagerReadingSlot",
                            "OfficeReadingSlot", "IdentityActions", "PersonArchivalStatus" }
                            .Select(name => $"{name}={Control<FrameworkElement>(name).RenderSize}, desired={Control<FrameworkElement>(name).DesiredSize}")));
                foreach (var value in new FrameworkElement[] { Control<Button>("Manager"), building, number })
                {
                    if (value.Visibility != Visibility.Visible) continue;
                    var bounds = value.TransformToAncestor(identity).TransformBounds(new Rect(value.RenderSize));
                    if (bounds.Width <= 0 || bounds.Left < 0 || bounds.Right > identity.ActualWidth + 1 ||
                        bounds.Bottom > identity.ActualHeight + 1)
                        throw new InvalidOperationException($"Anchored context is clipped at {width}: {value.Name}={bounds}.");
                }
            }
        }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The identity-context regression must remain invisible without a native handle.");
        Console.WriteLine("PASS: labelled anchored Manager/Office, partial/missing office values and labelled Department facts fit the existing photo-height header.");
    }

    private static void VerifyInlineProfileLayout(Window window)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var root = (FrameworkElement)window.Content;
        var scroll = Control<ScrollViewer>("PersonScroll");
        var identity = Control<Grid>("PersonIdentity");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        void Layout(double width, double height)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }
        Layout(1920, 1040);
        var directory = Bounds(Control<TextBox>("DirectoryNameEditor"));
        var alias = Bounds(Control<TextBox>("Alias"));
        if (Math.Abs(directory.Top - alias.Top) > 1 || alias.Left < directory.Right)
            throw new InvalidOperationException("The wide inline profile must pair Directory name and Alias in a responsive labelled form.");
        var divider = Control<GridSplitter>("PeopleDivider");
        var people = Control<GroupBox>("PeoplePanel");
        var host = Control<Grid>("ExplorerPanel");
        var originalPeople = people.ActualWidth;
        void Drag(double delta)
        {
            divider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            divider.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Layout(1920, 1040);
            divider.RaiseEvent(new DragCompletedEventArgs(delta, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Layout(1920, 1040);
        }
        Drag(host.ActualWidth - people.ActualWidth);
        directory = Bounds(Control<TextBox>("DirectoryNameEditor"));
        alias = Bounds(Control<TextBox>("Alias"));
        if (alias.Top <= directory.Bottom || Math.Abs(alias.Left - directory.Left) > 1)
            throw new InvalidOperationException("A narrow Person pane must stack labelled profile fields rather than clipping paired columns.");
        var actions = (FrameworkElement)Control<Border>("ProfileSessionActions").Child;
        var actionOrder = new FrameworkElementAutomationPeer(actions).GetChildren()?
            .Select(peer => peer.GetAutomationId())
            .Where(id => id is "ChoosePhoto" or "ClearPhoto" or "CancelProfile" or "SaveProfile").ToArray() ?? [];
        var actionFailures = new List<string>();
        if (!actionOrder.SequenceEqual(new[] { "ChoosePhoto", "ClearPhoto", "CancelProfile", "SaveProfile" }))
            actionFailures.Add("Profile action automation order differs from visual order: " + string.Join(", ", actionOrder));
        foreach (var name in new[] { "CancelProfileButton", "SaveProfileButton" })
        {
            var button = Control<Button>(name);
            if (button.ActualHeight > button.DesiredSize.Height + 1)
                actionFailures.Add($"Wrapped photo controls stretched {name}: actual={button.ActualHeight}, desired={button.DesiredSize.Height}.");
        }
        if (actionFailures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, actionFailures));
        Drag(originalPeople - people.ActualWidth);
        foreach (var size in new[] { new Size(700, 500), new Size(1100, 900), new Size(1920, 1040) })
        {
            Layout(size.Width, size.Height);
            var viewport = Bounds(scroll);
            foreach (var name in new[] { "DirectoryNameEditor", "Alias", "JobTitle", "Department",
                "OfficeBuilding", "OfficeNumber", "OfficeState", "OfficeCountry", "LinkedInProfileUrl", "ManagerChoices" })
            {
                var editor = Control<Control>(name);
                var bounds = Bounds(editor);
                if (!editor.IsEnabled || editor.Visibility != Visibility.Visible || !editor.Focusable ||
                    !KeyboardNavigation.GetIsTabStop(editor) || editor.FontSize < 14 ||
                    bounds.Width < 180 || bounds.Left < viewport.Left - 1 || bounds.Right > viewport.Right + 1)
                    throw new InvalidOperationException($"Inline profile field {name} is inaccessible or overflows at {size}: {bounds}, viewport={viewport}.");
            }
            if (scroll.ScrollableWidth > 0 ||
                scroll.ViewportHeight < Control<TextBox>("DirectoryNameEditor").ActualHeight ||
                Bounds(Control<GroupBox>("ProfileSummary")).Bottom > Bounds(Control<GroupBox>("NotesPane")).Top + 1)
                throw new InvalidOperationException("Profile editing must precede independent notes without horizontal page scrolling.");
            var beforeIdentity = Bounds(identity);
            var save = Control<Button>("SaveProfileButton");
            var beforeSave = Bounds(save);
            scroll.ScrollToBottom();
            Layout(size.Width, size.Height);
            if (beforeIdentity != Bounds(identity) || beforeSave != Bounds(save) ||
                Bounds(save).Bottom > Bounds(scroll).Top + 1 || save.IsDefault ||
                Control<Button>("CancelProfileButton").IsCancel)
                throw new InvalidOperationException("Profile identity and explicit Save/Cancel must remain anchored without a window-wide Enter/Escape action.");
            scroll.ScrollToTop();
        }
        var savedOffice = Control<TextBox>("OfficeNumber").Text;
        try
        {
            Control<TextBox>("OfficeNumber").Text = "Unsaved minimum-window office";
            Layout(700, 500);
            var firstEditor = Control<TextBox>("DirectoryNameEditor");
            var firstLabel = ((Panel)firstEditor.Parent).Children.OfType<Label>().Single();
            if (scroll.ViewportHeight < firstLabel.ActualHeight + firstEditor.ActualHeight)
                throw new InvalidOperationException($"Dirty profile at 700x500 cannot expose a labelled editor: " +
                    $"viewport={scroll.ViewportHeight}, required={firstLabel.ActualHeight + firstEditor.ActualHeight}, " +
                    $"identity={identity.ActualHeight}, notice={Control<FrameworkElement>("OperationalNotice").ActualHeight}.");
            // Reproduce the client allocation observed inside the hosted 700x500 native window.
            Layout(684, 461);
            foreach (var name in new[] { "DirectoryNameEditor", "Alias", "JobTitle", "Department",
                "OfficeBuilding", "OfficeNumber", "OfficeState", "OfficeCountry", "LinkedInProfileUrl", "ManagerChoices" })
            {
                var editor = Control<Control>(name);
                editor.BringIntoView();
                Layout(684, 461);
                var bounds = Bounds(editor);
                var viewport = Bounds(scroll);
                if (bounds.Top < viewport.Top - 1 || bounds.Bottom > viewport.Bottom + 1)
                    throw new InvalidOperationException($"Dirty native-minimum client clips {name}: editor={bounds}, viewport={viewport}.");
            }
        }
        finally { Control<TextBox>("OfficeNumber").Text = savedOffice; }
        Layout(1920, 1040);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The inline profile contract must stay invisible without a native window handle.");
        Console.WriteLine("PASS: responsive inline profile fields and anchored explicit actions at minimum, ordinary and wide sizes without showing a window.");
    }

    private static void VerifyRetainedOverlayGeometry(Window window, string settings)
    {
        var folder = Path.Combine(settings, "retained-overlay");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000005",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com","officeNumber":"Discard me"}]}
                """).Id))
                throw new InvalidOperationException("Retained overlay fixture failed: " + seed.Status);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var notes = Control<FrameworkElement>("PersonNotes");
        T Note<T>(string name) where T : FrameworkElement => (T)notes.FindName(name);
        Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("WorkspaceName").Text == "retained-overlay" &&
            Control<TextBlock>("WorkflowActivity").Text == "Ready");
        var tree = Control<TreeView>("PeopleList");
        var workflow = (WorkspaceViewModel)tree.DataContext;
        var casey = workflow.ReportingRoots.Single();
        var avery = casey.Reports.Single();
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, avery, TreeView.SelectedItemChangedEvent));
        Note<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Note<TextBox>("NoteTitle").Text == "New Person note" &&
            Control<TextBlock>("WorkflowActivity").Text == "Ready");
        var savedId = workflow.GetPersonNotes("avery@example.com").Single().Id;
        var savedNote = ReadSmokePersonNote(folder, savedId);
        var root = (FrameworkElement)window.Content;
        var searchFrame = (FrameworkElement)LogicalTreeHelper.GetParent(Control<TextBox>("SearchQuery"));
        var toolbar = (FrameworkElement)LogicalTreeHelper.GetParent(searchFrame);
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        void Layout()
        {
            root.Measure(new Size(700, 900));
            root.Arrange(new Rect(0, 0, 700, 900));
            root.UpdateLayout();
        }
        void Measure(string phase) => Console.WriteLine($"OVERLAY-PROBE {phase}: viewport={Bounds(Control<ScrollViewer>("PersonScroll"))}; " +
            $"notice={Bounds(Control<GroupBox>("OperationalNotice"))}; owner={Control<TextBlock>("Email").Text}; " +
            $"toolbar={Bounds(toolbar)}; searchFrame={Bounds(searchFrame)}; search={Bounds(Control<TextBox>("SearchQuery"))}; " +
            $"clear={Bounds(Control<Button>("ClearSearchButton"))}/{Control<Button>("ClearSearchButton").Visibility}; " +
            $"desiredSearch={Control<TextBox>("SearchQuery").DesiredSize}; desiredClear={Control<Button>("ClearSearchButton").DesiredSize}; " +
            $"logicalFocus={(FocusManager.GetFocusedElement(window) as FrameworkElement)?.Name}; " +
            $"title='{Note<TextBox>("NoteTitle").Text}'; editing={Note<TabControl>("Views").SelectedItem == notes.FindName("EditTab")}; " +
            $"editor={Bounds(Note<TextBox>("MarkdownEditor"))}; " +
            $"panel={Control<GroupBox>("PeoplePanel").Visibility}; summary={Control<TextBlock>("NoticeSummary").Text.Replace('\n', '|')}.");
        try
        {
            Note<TextBox>("NoteTitle").Text = "";
            PumpUntil(() => Note<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal) &&
                Control<Button>("OpenWorkspaceButton").IsEnabled);
            Layout();
            Measure("after-resize");
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
            Layout();
            var before = Bounds(Control<ScrollViewer>("PersonScroll"));
            Measure("baseline");
            Control<Button>("OpenPeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            Measure("opened");
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(avery, casey, TreeView.SelectedItemChangedEvent));
            Layout();
            if (Control<TextBlock>("Email").Text != "casey@example.com")
                throw new InvalidOperationException("Retained failed writing blocked Person browsing.");
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(casey, avery, TreeView.SelectedItemChangedEvent));
            Layout();
            Measure("returned");
            if (before != Bounds(Control<ScrollViewer>("PersonScroll")) ||
                Control<TextBlock>("Email").Text != "avery@example.com" || Note<TextBox>("NoteTitle").Text != "" ||
                Note<TabControl>("Views").SelectedItem != notes.FindName("EditTab") || workflow.CanSafelyClose ||
                !Note<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal) ||
                ReadSmokePersonNote(folder, savedId) != savedNote)
                throw new InvalidOperationException("Measured overlay browsing changed geometry, owner or retained editor.");
            var note = System.Windows.Automation.AutomationProperties.GetHelpText(Note<TextBlock>("NoteHeading"));
            var draft = Note<TextBox>("MarkdownEditor").Text;
            Measure("before-query");
            Control<TextBox>("SearchQuery").Text = "casey@example";
            var results = Control<ListBox>("SearchResults");
            PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == "casey@example.com"));
            Layout();
            Measure("after-query-before-activate");
            results.SelectedItem = results.Items.Cast<PersonSearchResult>()
                .Single(result => result.Person.Email == "casey@example.com");
            void Enter(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                new UnshownInputSource { RootVisual = root }, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            FocusManager.SetFocusedElement(window, Control<TextBox>("SearchQuery"));
            Enter(Control<TextBox>("SearchQuery"));
            Layout();
            Measure("query-enter-requested");
            PumpUntil(() => Control<TextBlock>("Email").Text == "casey@example.com" &&
                Control<TextBox>("SearchQuery").Text == "");
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(casey, avery, TreeView.SelectedItemChangedEvent));
            Layout();
            Measure("returned-search");
            if (before != Bounds(Control<ScrollViewer>("PersonScroll")) ||
                Control<TextBlock>("Email").Text != "avery@example.com" || Note<TextBox>("NoteTitle").Text != "" ||
                Note<TextBox>("MarkdownEditor").Text != draft ||
                System.Windows.Automation.AutomationProperties.GetHelpText(Note<TextBlock>("NoteHeading")) != note ||
                Control<TextBox>("SearchQuery").Text != "" ||
                Note<TabControl>("Views").SelectedItem != notes.FindName("EditTab") || workflow.CanSafelyClose ||
                ReadSmokePersonNote(folder, savedId) != savedNote)
                throw new InvalidOperationException("Measured search browsing changed absolute viewport, owner, note or retained writing.");
            Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            Measure("query-cleared");
            if (before != Bounds(Control<ScrollViewer>("PersonScroll")) || Control<TextBox>("SearchQuery").Text != "")
                throw new InvalidOperationException("Clearing the query changed the original absolute Person viewport.");
        }
        finally
        {
            Note<Button>("DiscardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready" && workflow.CanSafelyClose);
        }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Overlay measurement must remain invisible without a native main-window handle.");
    }

    private static void VerifyEmptyReadingScroll(Window window, string settings)
    {
        var folder = Path.Combine(settings, "empty-reading-scroll");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(folder) ||
                !seed.ApplyImport(seed.PreviewImport(SearchImport(SearchBrowsingPeople(responsive: true), 902)).Id))
                throw new InvalidOperationException("Empty reading fixture failed: " + seed.Status);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened." &&
            Control<TextBlock>("WorkspaceName").Text == "empty-reading-scroll" &&
            Control<TextBlock>("WorkflowActivity").Text == "Ready");
        var tree = Control<TreeView>("PeopleList");
        var workflow = (WorkspaceViewModel)tree.DataContext;
        var avery = workflow.ReportingRoots.Single(node => node.Person.Email == "morgan@example.com").Reports
            .Single(node => node.Person.Email == "avery@example.com");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, avery, TreeView.SelectedItemChangedEvent));
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1920, 1012));
        root.Arrange(new Rect(0, 0, 1920, 1012));
        root.UpdateLayout();
        var scroll = Control<ScrollViewer>("PersonScroll");
        var provider = (IScrollProvider)UIElementAutomationPeer.CreatePeerForElement(scroll).GetPattern(PatternInterface.Scroll);
        Console.WriteLine($"EMPTY-READING: people={workflow.People.Count}; viewport={scroll.ViewportHeight}; " +
            $"extent={scroll.ExtentHeight}; verticallyScrollable={provider.VerticallyScrollable}; offset={scroll.VerticalOffset}.");
        if (workflow.People.Count != 46 || workflow.GetPersonNotes("avery@example.com").Count != 0 ||
            workflow.GetTeamNotes("avery@example.com").Count != 0 || scroll.ViewportHeight <= 0 ||
            provider.VerticallyScrollable || scroll.VerticalOffset != 0)
            throw new InvalidOperationException("The full wide empty-reading fixture did not establish the non-scrollable precondition.");
        ControlReadiness.ScrollToTopIfScrollable(() => provider.VerticallyScrollable,
            () => provider.SetScrollPercent(System.Windows.Automation.ScrollPattern.NoScroll, 0));
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Empty-reading scroll proof must remain invisible without a native main-window handle.");
    }

    private static void VerifyAutomaticPeopleReading(Type windowType, string root)
    {
        VerifyAutomaticRefreshSearch(windowType, root, compact: false);
        VerifyAutomaticRefreshSearch(windowType, root, compact: true);
        VerifyAutomaticRefreshSearch(windowType, root, compact: false, reviewedImport: true);
        foreach (var historical in new[] { false, true })
            foreach (var import in new[] { false, true })
                VerifyReturningRememberedContext(windowType, root, historical, import);
        VerifyAutomaticPeopleReading(windowType, root, importRetry: true);
        VerifyAutomaticPeopleReading(windowType, root, importRetry: true, invalidateReview: true);
        VerifyAutomaticPeopleReading(windowType, root, importRetry: false);
        VerifyAutomaticPeopleReading(windowType, root, importRetry: false, recoveryScope: NoteScope.Person);
        VerifyAutomaticPeopleReading(windowType, root, importRetry: false, recoveryScope: NoteScope.Team);
    }

    private static void VerifyReturningRememberedContext(Type windowType, string root, bool historical, bool import)
    {
        var path = Path.Combine(root, $"returning-context-{historical}-{import}");
        WorkspaceFixtures.CopyCurrent(path);
        var preferencesPath = Path.Combine(Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY")!,
            "workspace-settings.json");
        var settings = new WorkspaceSettings(preferencesPath);
        Guid noteId;
        Guid? captureId = null;
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(path)) throw new InvalidOperationException(seed.Status);
            noteId = seed.CreateTeamNote("casey@example.com", "Context", "Context")!.Id;
            seed.CreateTeamNote("avery@example.com", "Own reading", "Not the remembered context");
            if (historical)
            {
                if (!seed.EditPersonManager("avery@example.com", null)) throw new InvalidOperationException(seed.Status);
                captureId = seed.GetHistoricalTeamContexts("avery@example.com")
                    .Single(capture => capture.TeamNotes.Any(note => note.Id == noteId)).Id;
                if (!seed.EditTeamNote(noteId, "Later context", "Different current writing"))
                    throw new InvalidOperationException(seed.Status);
            }
            var archived = string.Join(",", seed.People.Select(person =>
                $$"""{"email":"{{person.Email}}","archived":true}"""));
            if (!seed.ApplyImport(seed.PreviewImport(SearchImport(archived, 911)).Id))
                throw new InvalidOperationException(seed.Status);
        }
        var remembered = new RememberedView("avery@example.com",
            historical ? SearchSource.HistoricalTeamNote : SearchSource.InheritedTeamNote,
            noteId, "casey@example.com", captureId);
        if (!settings.SetLastView(path, remembered)) throw new InvalidOperationException(settings.PreferenceError);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [ApplicationWorkflow.CreateFileBacked(), (Func<Window, string?>)(_ => path)], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        try
        {
            Invoke("OpenSettingsButton");
            Invoke("OpenWorkspaceButton");
            PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
            if (model.PeopleNavigation.SelectedEmail is not null || model.ViewRecoveryFeedback is null)
                throw new InvalidOperationException("Returning-context fixture did not start with an unavailable remembered Person.");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1100, 900));
            content.Arrange(new Rect(0, 0, 1100, 900));
            content.UpdateLayout();
            Control<TextBox>("SearchQuery").Text = "Context";
            Invoke("OpenSettingsButton");
            Invoke("OpenImportButton");
            var preferences = File.ReadAllBytes(preferencesPath);
            var records = $$"""
                {"email":"avery@example.com","archived":false,"managerEmail":{{(historical ? "null" : "\"alex@example.com\"")}}},
                {"email":"alex@example.com","archived":false},
                {"email":"casey@example.com","archived":false},
                {"email":"higher@example.com","displayName":"A higher","managerEmail":null},
                {"email":"one@example.com","displayName":"One","managerEmail":"higher@example.com"},
                {"email":"two@example.com","displayName":"Two","managerEmail":"higher@example.com"}
                """;
            var batch = SearchImport(records, 910);
            FocusManager.SetFocusedElement(window, Control<TextBox>("SearchQuery"));
            if (import)
            {
                var source = path + "-returning.json";
                File.WriteAllText(source, batch);
                Control<TextBox>("ImportPath").Text = source;
                Invoke("PreviewButton");
                PumpUntil(() => Control<Button>("ApplyButton").IsEnabled);
                Invoke("ApplyButton");
            }
            else
            {
                using var peer = OpenSmokeWorkspace(path);
                if (!peer.ApplyImport(peer.PreviewImport(batch).Id) || !model.RefreshWorkspace())
                    throw new InvalidOperationException(model.Status);
            }
            PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com" &&
                Control<TextBlock>("WorkflowActivity").Text == "Ready");
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
            var destination = model.PeopleNavigation.SelectedResult;
            if (destination?.NoteId != noteId || destination.HistoricalContextId != captureId ||
                destination.MatchText != "Context" ||
                model.ViewRecoveryFeedback is not null || !model.IsImportOpen ||
                Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Visible ||
                Control<TextBox>("SearchQuery").Text != "Context" ||
                FocusManager.GetFocusedElement(window) != Control<TextBox>("SearchQuery") ||
                !preferences.SequenceEqual(File.ReadAllBytes(preferencesPath)))
                throw new InvalidOperationException("Returning remembered context changed its exact destination, Import, search or preferences: " +
                    $"historical={historical}; import={import}; note={destination?.NoteId}; capture={destination?.HistoricalContextId}; " +
                    $"recovery={model.ViewRecoveryFeedback}; importOpen={model.IsImportOpen}; " +
                    $"panel={Control<FrameworkElement>("ImportPanel").Visibility}; query={Control<TextBox>("SearchQuery").Text}; " +
                    $"preferencesUnchanged={preferences.SequenceEqual(File.ReadAllBytes(preferencesPath))}.");
            var team = Control<UserControl>("TeamNotes");
            if (historical ? !Control<Expander>("HistoryAccess").IsExpanded :
                !((Expander)team.FindName("CurrentTeamContext")).IsExpanded)
                throw new InvalidOperationException("Returning remembered context was resolved but its actual reading section remained closed.");
            var reading = historical
                ? Control<FlowDocumentScrollViewer>("HistoricalTeamReading")
                : (FlowDocumentScrollViewer)team.FindName("InheritedReading");
            var section = historical
                ? reading.Document.Blocks.OfType<Section>().Single(block => Equals(block.Tag, captureId))
                    .Blocks.OfType<Section>().Single(block => Equals(block.Tag, noteId))
                : reading.Document.Blocks.OfType<Section>().Single(block => Equals(block.Tag, noteId));
            var text = new TextRange(section.ContentStart, section.ContentEnd).Text;
            if (!text.Contains("Context", StringComparison.Ordinal) ||
                text.Contains("Different current writing", StringComparison.Ordinal))
                throw new InvalidOperationException("Returning remembered context did not present its exact saved writing.");
            Control<TextBox>("SearchQuery").Text = "No such note";
            PumpUntil(() => !Control<ListBox>("SearchResults").HasItems &&
                Control<TextBlock>("SearchCount").Text.StartsWith("People found: 0", StringComparison.Ordinal));
            if (model.PeopleNavigation.SelectedResult != destination || model.PeopleNavigation.LastValidView != remembered)
                throw new InvalidOperationException("An unrelated search discarded the automatically restored exact note destination.");
            Control<TextBox>("SearchQuery").Clear();
            PumpUntil(() => Control<TextBlock>("SearchCount").Text.StartsWith("Enter text to search", StringComparison.Ordinal));
            if (model.PeopleNavigation.SelectedResult != destination)
                throw new InvalidOperationException("Clearing search discarded the automatically restored exact note destination.");
            LogicalDescendants(Control<FrameworkElement>("ImportPanel")).OfType<Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "BackToPeople")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (model.IsImportOpen || model.PeopleNavigation.SelectedResult != destination ||
                !preferences.SequenceEqual(File.ReadAllBytes(preferencesPath)))
                throw new InvalidOperationException("Returning to browsing lost the exact note destination or changed its saved preference.");
            var next = model.ReportingRoots.Single(node => node.Person.Email == "higher@example.com");
            Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
                null!, next, TreeView.SelectedItemChangedEvent));
            if (model.PeopleNavigation.SelectedEmail != "higher@example.com" ||
                model.PeopleNavigation.SelectedResult is not null ||
                new WorkspaceSettings(preferencesPath).GetLastView(path)?.PersonEmail != "higher@example.com")
                throw new InvalidOperationException("Deliberate navigation did not replace the restored exact note destination.");
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Returning-context verification cannot create a native window.");
            Console.WriteLine($"PASS: returning {(historical ? "frozen" : "inherited")} exact context is presented after " +
                $"{(import ? "accepted Import" : "refresh")} without leaving Import, clearing search or changing preferences.");
        }
        finally
        {
            window.Close();
            Invoke("CancelExitButton");
            model.Dispose();
        }
    }

    private static void VerifyAutomaticRefreshSearch(Type windowType, string root, bool compact, bool reviewedImport = false)
    {
        var path = Path.Combine(root, reviewedImport ? "refresh-search-reviewed" :
            compact ? "refresh-search-compact" : "refresh-search-wide");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport(SearchImport("""
                {"email":"former@example.com","displayName":"Needle","managerEmail":null,"archived":true}
                """, 907)).Id) || seed.CreatePersonNote("former@example.com", "Retained writing", "Exact private writing") is null)
                throw new InvalidOperationException(seed.Status);
        using var peer = new SmokeSnapshotPeer(path);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(), (Func<Window, string?>)(_ => path)], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        try
        {
            Invoke("OpenSettingsButton");
            Invoke("OpenWorkspaceButton");
            PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
            var content = (FrameworkElement)window.Content;
            var size = compact ? new Size(700, 500) : new Size(1100, 900);
            void Layout()
            {
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();
            }
            Layout();
            Control<TextBox>("SearchQuery").Text = "Needle";
            PumpUntil(() => model.PeopleNavigation.HighlightedResult?.Person.Email == "former@example.com");
            Invoke("OpenSettingsButton");
            Invoke("OpenImportButton");
            var import = path + "-import.json";
            File.WriteAllText(import, SearchImport("""{"email":"former@example.com","jobTitle":"Unaccepted review"}""", 908));
            Control<TextBox>("ImportPath").Text = import;
            if (reviewedImport)
            {
                Invoke("PreviewButton");
                PumpUntil(() => Control<Button>("ApplyButton").IsEnabled);
            }
            var preferences = Path.Combine(Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY")!,
                "workspace-settings.json");
            var saved = File.ReadAllBytes(preferences);
            var initialRevision = model.Synchronization.LocalRevision;
            peer.Publish(source => source.ApplyImport(source.PreviewImport(SearchImport(AutomaticRefreshSearchPeople(), 909)).Id));
            Invoke("OpenSettingsButton");
            var overlay = model.PeopleNavigation.OverlayOpen;
            Invoke("SyncNowButton");
            PumpUntil(() => Control<Button>("SyncNowButton").IsEnabled);
            Invoke("OpenSettingsButton");
            Layout();
            if (!model.IsImportOpen || Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Visible ||
                Control<TextBox>("ImportPath").Text != import || Control<TextBox>("SearchQuery").Text != "Needle" ||
                model.PeopleNavigation.HighlightedResult?.Person.Email != "former@example.com" ||
                model.PeopleNavigation.OverlayOpen != overlay || !saved.SequenceEqual(File.ReadAllBytes(preferences)))
                throw new InvalidOperationException("Snapshot checking changed Import, search/highlight/overlay or saved preferences: " +
                    $"compact={compact}; reviewed={reviewedImport}; import={model.IsImportOpen}; panel={Control<FrameworkElement>("ImportPanel").Visibility}; " +
                    $"path='{Control<TextBox>("ImportPath").Text}'; query='{Control<TextBox>("SearchQuery").Text}'; " +
                    $"highlight='{model.PeopleNavigation.HighlightedResult?.Person.Email}'; overlay={overlay}->{model.PeopleNavigation.OverlayOpen}; " +
                    $"preferencesUnchanged={saved.SequenceEqual(File.ReadAllBytes(preferences))}.");
            if (reviewedImport)
            {
                if (model.Synchronization.Error is not { } error || !error.Contains("Incoming version is waiting", StringComparison.Ordinal) ||
                    model.Synchronization.LocalRevision != initialRevision || model.PeopleNavigation.SelectedEmail is not null ||
                    Control<TextBlock>("Email").Text != "" || model.GetRelationships("shallow@example.com") is not null)
                    throw new InvalidOperationException("An unapplied Import preview did not retain the original graph and explicit deferred-adoption error.");
                if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                    throw new InvalidOperationException("Refresh-search verification cannot create a native window.");
                Console.WriteLine("PASS: actual compiled unapplied Import review defers one incoming snapshot request without selecting unaccepted data.");
                return;
            }
            if (model.Synchronization.Error is not null || Control<Button>("ApplyButton").IsEnabled)
                throw new InvalidOperationException("Valid refresh retained a failure or enabled an unreviewed Import.");
            LogicalDescendants(Control<FrameworkElement>("ImportPanel")).OfType<Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "BackToPeople")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            using var accepted = OpenSmokeWorkspace(path);
            if (accepted.Synchronization.LocalRevision != peer.Workflow.Synchronization.LocalRevision)
                throw new InvalidOperationException("The added refresh-search path did not accept its incoming snapshot: " +
                    $"compact={compact}; email='{Control<TextBlock>("Email").Text}'; query='{Control<TextBox>("SearchQuery").Text}'; " +
                    $"highlight='{model.PeopleNavigation.HighlightedResult?.Person.Email}'; selected='{model.PeopleNavigation.SelectedEmail}'; " +
                    $"synchronization='{model.Synchronization.Error}'.");
            PumpUntil(() => Control<TextBlock>("Email").Text == "shallow@example.com" &&
                Control<TextBox>("SearchQuery").Text == "Needle");
            if (compact && Control<FrameworkElement>("PeoplePanel").Visibility != Visibility.Visible)
                Invoke("OpenPeopleButton");
            if (model.PeopleNavigation.HighlightedResult?.Person.Email != "former@example.com" ||
                model.PersonNotesView.OwnerEmail != "shallow@example.com" || model.TeamNotesView.OwnerEmail != "shallow@example.com" ||
                model.ProfileEdit is not null || model.PersonNotesView.IsEditing || model.TeamNotesView.IsEditing)
                throw new InvalidOperationException("Automatic refresh activated a search match or failed to open ordinary Person reading.");
            Control<TextBox>("SearchQuery").Text = "";
            PumpUntil(() =>
            {
                Layout();
                var tree = Control<TreeView>("PeopleList");
                var row = VisualDescendants(tree).OfType<TreeViewItem>().SingleOrDefault(item =>
                    item.DataContext is ReportingNode node && node.Person.Email == "shallow@example.com");
                if (row is not { IsSelected: true }) return false;
                var header = VisualDescendants(row).OfType<TextBlock>().FirstOrDefault(text => text.Text == "Z shallow Manager");
                if (header is null) return false;
                var bounds = header.TransformToAncestor(tree).TransformBounds(new Rect(header.RenderSize));
                return !bounds.IsEmpty && bounds.Left >= 0 && bounds.Right <= tree.ActualWidth &&
                    bounds.Top >= 0 && bounds.Bottom <= tree.ActualHeight;
            });
            var ancestor = VisualDescendants(Control<TreeView>("PeopleList")).OfType<TreeViewItem>().Single(item =>
                item.DataContext is ReportingNode node && node.Person.Email == "z-root@example.com");
            if (!ancestor.IsExpanded || !saved.SequenceEqual(File.ReadAllBytes(preferences)) ||
                accepted.GetRelationships("former@example.com")!.Person.JobTitle is not null ||
                accepted.GetPersonNotes("former@example.com", true).Single().Markdown != "Exact private writing" ||
                accepted.GetPersonNotes("shallow@example.com", true).Count != 0 ||
                accepted.GetTeamNotes("shallow@example.com", true).Count != 0)
                throw new InvalidOperationException("Deferred reveal changed the unreviewed input, notes, preferences or expanded ancestor.");
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Refresh-search verification cannot create a native window.");
            Console.WriteLine($"PASS: actual compiled {(compact ? "compact" : "wide")} refresh accepts the exact peer revision, retains Import/search and reveals its selected header without replacing preferences or writing.");
        }
        finally
        {
            window.Close();
            Invoke("CancelExitButton");
            model.Dispose();
        }
    }

    private static void VerifyAutomaticPeopleReading(Type windowType, string root, bool importRetry,
        bool invalidateReview = false, NoteScope? recoveryScope = null)
    {
        var path = Path.Combine(root, recoveryScope is { } scope ? $"remembered-{scope}-recovery" :
            invalidateReview ? "automatic-import-invalidated" :
            importRetry ? "automatic-import-reading" : "automatic-reading");
        var email = recoveryScope is not null ? "avery@example.com" : importRetry ? "morgan@example.com" : "casey@example.com";
        var heading = recoveryScope is not null ? "Avery" : importRetry ? "Morgan Example" : "Casey";
        var ancestorEmail = importRetry ? "z-context@example.com" : "anchor@example.com";
        var records = (importRetry ? """
            {"email":"z-context@example.com","displayName":"Z single-report ancestor","managerEmail":null},
            {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":"z-context@example.com"},
            {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"morgan@example.com"},
            {"email":"V-reference@example.com","displayName":"Vendor reference","managerEmail":"morgan@example.com"},
            {"email":"v-child@example.com","displayName":"Vendor child","managerEmail":"V-reference@example.com"},
            """ : """
            {"email":"anchor@example.com","displayName":"Z ancestor","managerEmail":null},
            {"email":"casey@example.com","displayName":"Casey","managerEmail":"anchor@example.com"},
            {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com"},
            {"email":"second@example.com","displayName":"Second","managerEmail":"casey@example.com"},
            """) + string.Join(",", Enumerable.Range(0, 24).Select(i =>
                $$"""{"email":"context{{i:00}}@example.com","displayName":"A context {{i:00}}","managerEmail":null}"""));
        RememberedView? remembered = null;
        byte[]? preferences = null;
        var preferencesPath = Path.Combine(Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY")!,
            "workspace-settings.json");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(path) || !importRetry && !seed.ApplyImport(seed.PreviewImport(SearchImport(records, 902)).Id))
                throw new InvalidOperationException(seed.Status);
            if (recoveryScope is { } noteScope)
            {
                var settings = ReadSmokeWorkspacePreferences();
                if (!settings.OpenWorkspace(seed, path)) throw new InvalidOperationException(seed.Status);
                var person = seed.CreatePersonNote(email, "Person ongoing", "Never substitute this Person writing") ??
                    throw new InvalidOperationException(seed.Status);
                var team = seed.CreateTeamNote(email, "Team ongoing", "Never substitute this Team writing") ??
                    throw new InvalidOperationException(seed.Status);
                var archived = seed.CreatePersonNote(email, "Archived exact note", "Retained archived writing") ??
                    throw new InvalidOperationException(seed.Status);
                if (!seed.ArchivePersonNote(archived.Id)) throw new InvalidOperationException(seed.Status);
                remembered = new(email, noteScope == NoteScope.Person ? SearchSource.PersonNote : SearchSource.OwnedTeamNote,
                    noteScope == NoteScope.Person ? archived.Id : Guid.NewGuid(), email);
                if (!settings.SetOngoingNote(path, NoteScope.Person, email, person.Id) ||
                    !settings.SetOngoingNote(path, NoteScope.Team, email, team.Id) ||
                    !settings.SetLastView(path, remembered))
                    throw new InvalidOperationException(settings.PreferenceError);
                preferences = File.ReadAllBytes(preferencesPath);
            }
        }
        var writing = recoveryScope is not null ? WorkspaceFixtures.Bytes(path) : null;
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(), (Func<Window, string?>)(_ => path)], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            Invoke("OpenSettingsButton");
            Invoke("OpenWorkspaceButton");
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready" &&
                Control<TextBlock>("Email").Text == (importRetry ? "" : email));
            var content = (FrameworkElement)window.Content;
            var observedBringExtents = new List<double>();
            var observedTree = Control<TreeView>("PeopleList");
            VirtualizingPanel.SetCacheLength(observedTree, new VirtualizationCacheLength(0));
            observedTree.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, request) =>
            {
                if (request.TargetObject is TreeViewItem { DataContext: ReportingNode node } &&
                    node.Person.Email == email && !request.TargetRect.IsEmpty && request.TargetRect.Width == 1)
                    observedBringExtents.Add(VisualDescendants(observedTree).OfType<ScrollViewer>().First().ExtentHeight);
            }), handledEventsToo: true);
            void Settle()
            {
                var settled = false;
                window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
                PumpUntil(() => settled);
            }
            void Layout(Size? requestedSize = null)
            {
                var size = requestedSize ?? (importRetry ? new Size(1024, 697) : new Size(1100, 736));
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                Settle();
                content.UpdateLayout();
                Settle();
            }
            Layout();
            if (importRetry)
            {
                var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
                var import = Path.Combine(root, "automatic-import.json");
                File.WriteAllText(import, SearchImport(records, 903));
                void RequestPreview()
                {
                    Invoke("OpenSettingsButton");
                    Invoke("OpenImportButton");
                    Control<TextBox>("ImportPath").Text = import;
                    Invoke("PreviewButton");
                }
                void AwaitPreview(bool settleReview = false)
                {
                    var observedApplicable = false;
                    var reviewSettled = false;
                    PumpUntil(() =>
                    {
                        var applicable = Control<Button>("ApplyButton").IsEnabled;
                        if (settleReview && applicable && !observedApplicable)
                        {
                            observedApplicable = true;
                            window.Dispatcher.BeginInvoke(() => reviewSettled = true, DispatcherPriority.ContextIdle);
                        }
                        return applicable && (!settleReview || reviewSettled);
                    });
                }
                void BackToPeople() => LogicalDescendants(Control<FrameworkElement>("ImportPanel")).OfType<Button>()
                    .Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "BackToPeople")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequestPreview();
                AwaitPreview();
                using (WorkspaceFixtures.LockLocalStore(path))
                {
                    Invoke("ApplyButton");
                    PumpUntil(() => model.HasFailedImport && Control<Button>("DiscardImportButton").IsEnabled);
                    BackToPeople();
                    Invoke("DiscardImportButton");
                }
                DispatcherOperation? recoveredPreview = null;
                TimeoutException? admittedPreviewError = null;
                var open = Control<Button>("OpenWorkspaceButton");
                DependencyPropertyChangedEventHandler enterWhenReady = (_, _) =>
                {
                    if (!open.IsEnabled || model.LocalSave.EditsPaused || recoveredPreview is not null) return;
                    recoveredPreview = window.Dispatcher.InvokeAsync(RequestPreview, DispatcherPriority.Send);
                    try
                    {
                        PumpUntil(() => recoveredPreview.Task.IsCompleted &&
                            Control<Button>("ApplyButton").IsEnabled);
                    }
                    catch (TimeoutException error) { admittedPreviewError = error; }
                };
                open.IsEnabledChanged += enterWhenReady;
                try
                {
                    Invoke("NoticeRetryButton");
                    PumpUntil(() => recoveredPreview?.Task.IsCompleted == true);
                }
                finally { open.IsEnabledChanged -= enterWhenReady; }
                var requestedPreview = recoveredPreview ??
                    throw new InvalidOperationException("Recovered controls did not admit the single preview request.");
                requestedPreview.Task.GetAwaiter().GetResult();
                if (admittedPreviewError is not null)
                    throw new InvalidOperationException("Advertised recovery readiness did not admit an applicable Import.", admittedPreviewError);
                AwaitPreview(settleReview: true);
                if (invalidateReview)
                {
                    var reviewedRevision = model.Synchronization.LocalRevision;
                    using (var delivered = ApplicationWorkflow.CreateFileBacked())
                    {
                        if (!delivered.OpenWorkspace(path) || !delivered.ApplyImport(delivered.PreviewImport(SearchImport("""
                            {"email":"delivered@example.com","displayName":"Delivered after review","managerEmail":null}
                            """, 904)).Id))
                            throw new InvalidOperationException(delivered.Status);
                    }
                    if (!model.RefreshWorkspace()) throw new InvalidOperationException(model.Status);
                    Settle();
                    if (model.Synchronization.LocalRevision == reviewedRevision ||
                        model.GetRelationships("delivered@example.com")?.Person.Heading != "Delivered after review" ||
                        Control<Button>("ApplyButton").IsEnabled || !model.IsImportOpen ||
                        !Control<Button>("PreviewButton").IsEnabled)
                        throw new InvalidOperationException("A different accepted revision did not invalidate the recovered Import review.");
                    if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                        throw new InvalidOperationException("Import invalidation verification cannot create a native window.");
                    Console.WriteLine("PASS: a genuinely different accepted revision still invalidates the recovered applicable review without replaying or accepting it.");
                    return;
                }
                Invoke("ApplyButton");
                PumpUntil(() => model.PeopleNavigation.SelectedEmail == email &&
                    Control<TextBlock>("WorkflowActivity").Text == "Ready");
                if (!model.IsImportOpen) throw new InvalidOperationException("Automatic selection left Import.");
                BackToPeople();
                Layout();
            }
            var tree = Control<TreeView>("PeopleList");
            var row = VisualDescendants(tree).OfType<TreeViewItem>().Single(item =>
                item.DataContext is ReportingNode node && node.Person.Email == email);
            var browsing = (WorkspaceViewModel)tree.DataContext;
            var treeScroll = VisualDescendants(tree).OfType<ScrollViewer>().First();
            if (observedBringExtents.Count != 1 || observedBringExtents[0] < treeScroll.ExtentHeight - 1)
                throw new InvalidOperationException($"The single reveal used an unsettled scroll extent: " +
                    $"requests={observedBringExtents.Count}; requestedExtent={observedBringExtents.FirstOrDefault()}; " +
                    $"settledExtent={treeScroll.ExtentHeight}.");
            if (Math.Abs(browsing.PeopleNavigation.BrowseOffset - treeScroll.VerticalOffset) >= 1)
                throw new InvalidOperationException($"The accepted reveal remembered a stale browsing offset: " +
                    $"remembered={browsing.PeopleNavigation.BrowseOffset}; actual={treeScroll.VerticalOffset}.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(row);
            var headerPeer = (UIElementAutomationPeer)(peer.GetChildren() ?? []).Single(child =>
                child.GetAutomationControlType() == AutomationControlType.Text);
            var header = (FrameworkElement)headerPeer.Owner;
            var viewport = tree.TransformToAncestor(content).TransformBounds(new Rect(tree.RenderSize));
            var headerBounds = header.TransformToAncestor(content).TransformBounds(new Rect(header.RenderSize));
            var headerCenter = new Point(headerBounds.Left + headerBounds.Width / 2, headerBounds.Top + headerBounds.Height / 2);
            ControlReadiness.AwaitSelectedHeader(
                () => new ControlReadiness.SelectedRowHeader<UIElementAutomationPeer>(peer.IsOffscreen(),
                    ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).IsSelected, headerPeer),
                current => current.GetName() == heading && viewport.Contains(headerCenter) &&
                    headerBounds.Top >= viewport.Top && headerBounds.Bottom <= viewport.Bottom,
                condition =>
                {
                    if (!condition())
                        throw new InvalidOperationException("Logical row metadata rejected the actual selected viewport-visible header.");
                });
            if (peer.GetName() != $"{heading} ({email}); Directory name: {heading}" ||
                peer.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider { IsSelected: true } ||
                headerPeer.GetName() != heading || !viewport.Contains(headerCenter) ||
                headerBounds.Top < viewport.Top || headerBounds.Bottom > viewport.Bottom ||
                Control<Button>("EditProfileButton").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Automatic opening did not expose the exact selected Person's reading profile and viewport-visible header.");
            var ancestor = tree.Items.Cast<ReportingNode>().Single(item => item.Person.Email == ancestorEmail);
            if (tree.ItemContainerGenerator.ContainerFromItem(ancestor) is not TreeViewItem { IsExpanded: true })
                throw new InvalidOperationException("Automatic opening did not expand the selected Manager's ancestor.");
            if (!importRetry && recoveryScope is null)
            {
                DispatcherOperation? changedOwner = null;
                PeopleNavigationView? retainedBrowsing = null;
                var previousRequests = observedBringExtents.Count;
                RequestBringIntoViewEventHandler changeAfterScroll = (_, request) =>
                {
                    if (changedOwner is not null ||
                        request.TargetObject is not TreeViewItem { DataContext: ReportingNode node } ||
                        node.Person.Email != email || request.TargetRect.IsEmpty || request.TargetRect.Width != 1)
                        return;
                    changedOwner = window.Dispatcher.InvokeAsync(() =>
                    {
                        if (!browsing.NavigatePerson("context00@example.com"))
                            throw new InvalidOperationException(browsing.Status);
                        browsing.RememberPeopleBrowsing(["context00@example.com"], 0);
                        retainedBrowsing = browsing.PeopleNavigation;
                    }, DispatcherPriority.Send);
                };
                tree.AddHandler(FrameworkElement.RequestBringIntoViewEvent, changeAfterScroll, handledEventsToo: true);
                try
                {
                    Invoke("OpenSettingsButton");
                    Invoke("OpenWorkspaceButton");
                    PumpUntil(() => changedOwner?.Task.IsCompleted == true);
                    changedOwner!.Task.GetAwaiter().GetResult();
                    Settle();
                    if (retainedBrowsing is null || Control<TextBlock>("Email").Text != email ||
                        browsing.PeopleNavigation.SelectedEmail != "context00@example.com" ||
                        browsing.PeopleNavigation.BrowseOffset != retainedBrowsing.BrowseOffset ||
                        !browsing.PeopleNavigation.ExpandedPeople.SequenceEqual(retainedBrowsing.ExpandedPeople) ||
                        observedBringExtents.Count != previousRequests + 1)
                        throw new InvalidOperationException("A stale post-scroll snapshot overwrote the newly selected workflow owner's browsing.");
                    Console.WriteLine("PASS: one actual compiled reveal cannot publish a stale browsing snapshot after the workflow owner changes.");
                }
                finally { tree.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, changeAfterScroll); }
            }
            if (recoveryScope is not null)
                foreach (var size in new[] { new Size(700, 500), new Size(1100, 736) })
                {
                    Layout(size);
                    if (browsing.PeopleNavigation.SelectedEmail != email || browsing.PeopleNavigation.SelectedResult is not null ||
                        browsing.ProfileEdit is not null || browsing.ViewRecoveryFeedback is null ||
                        Control<TextBlock>("ViewPreferenceStatus").Visibility != Visibility.Visible ||
                        !Control<TextBlock>("ViewPreferenceStatus").Text.Contains("no replacement was opened") ||
                        browsing.PersonNotesView.SelectedNote is not null || browsing.TeamNotesView.SelectedNote is not null ||
                        browsing.PersonNotesView.IsEmpty || browsing.TeamNotesView.IsEmpty ||
                        ((TextBlock)Control<FrameworkElement>("PersonNotes").FindName("NoteHeading")).Visibility != Visibility.Collapsed ||
                        ((TextBlock)Control<FrameworkElement>("TeamNotes").FindName("NoteHeading")).Visibility != Visibility.Collapsed ||
                        ReadSmokeWorkspacePreferences().GetLastView(path) != remembered ||
                        !File.ReadAllBytes(preferencesPath).SequenceEqual(preferences!))
                        throw new InvalidOperationException("Compiled remembered-note recovery lost its Person, warning, unchanged preferences or unselected writing.");
                    AssertWorkspaceBytes(path, writing!);
                }
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Automatic-reading verification cannot create a native window.");
            Console.WriteLine("PASS: actual compiled automatic opening exposes the exact reading Person, selected named header and expanded ancestor within the no-window viewport.");
            if (recoveryScope is { } recoveredScope)
                Console.WriteLine($"PASS: unavailable exact {recoveredScope} note retains its remembered Person, visible recovery, unselected writing and unchanged preferences/Workspace bytes at 700/1100.");
        }
        finally { ClosePublishedCompiledWindow(window); }
    }

    private static void VerifyPeopleSearchRestoration(Window window, string settings)
    {
        var folder = Path.Combine(settings, "responsive-browsing");
        Guid noteId;
        Guid teamId;
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport(
                    SearchImport(SearchBrowsingPeople(responsive: true), 901)).Id))
                throw new InvalidOperationException("Responsive browsing fixture failed: " + seed.Status);
            noteId = seed.CreatePersonNote("avery@example.com", "Individual context", "# Orchid\nPrivate Person writing\n\n" +
                SearchBrowsingParagraphs())!.Id;
            teamId = seed.CreateTeamNote("morgan@example.com", "Former direction", "Cedar frozen writing")!.Id;
            if (!seed.EditPersonManager("avery@example.com", "alex@example.com") ||
                !seed.EditPersonManager("avery@example.com", "morgan@example.com") ||
                !seed.EditTeamNote(teamId, "Current direction", "Pine current writing"))
                throw new InvalidOperationException("Attributed search fixture failed: " + seed.Status);
        }
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var root = (FrameworkElement)window.Content;
        if (!new WorkspaceSettings(Path.Combine(settings, "workspace-settings.json")).SetPaneProportions(folder, new(27, 35)))
            throw new InvalidOperationException("Could not seed the hosted browsing pane balance.");
        void Layout(bool compact)
        {
            var size = compact ? new Size(700, 900) : new Size(1920, 1012);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
        }
        void Settle(bool compact)
        {
            Layout(compact);
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
            Layout(compact);
        }
        Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened." &&
            Control<TextBlock>("WorkspaceName").Text == "responsive-browsing");
        var tree = Control<TreeView>("PeopleList");
        // Force cold realization rather than letting an invisible host premeasure the entire small fixture.
        VirtualizingPanel.SetCacheLength(tree, new VirtualizationCacheLength(0));
        Layout(false);
        void Press(UIElement target, Key key)
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new UnshownInputSource { RootVisual = root }, 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        void Enter(UIElement target) => Press(target, Key.Enter);
        var queryField = Control<TextBox>("SearchQuery");
        var queryResults = Control<ListBox>("SearchResults");
        if (Control<TextBlock>("Email").Text != "alex@example.com")
            throw new InvalidOperationException("The populated search fixture did not open its automatic root Person.");
        queryField.Text = "browse";
        PumpUntil(() => queryResults.Items.Count == 40);
        FocusManager.SetFocusedElement(window, queryField);
        Press(queryField, Key.Up);
        if (queryResults.SelectedItem is not PersonSearchResult { Person.Email: "browse01@example.com" })
            throw new InvalidOperationException("Query Up must clamp at the automatically highlighted first Person.");
        Press(queryField, Key.Down);
        if (queryResults.SelectedItem is not PersonSearchResult { Person.Email: "browse02@example.com" } ||
            !ReferenceEquals(FocusManager.GetFocusedElement(window), queryField))
            throw new InvalidOperationException("Query Down must choose the next Person without transferring logical typing focus.");
        queryField.Text = "unmatched-query";
        PumpUntil(() => queryResults.Items.Count == 0);
        Enter(queryField);
        if (queryResults.SelectedItem is not null || queryField.Text != "unmatched-query" ||
            Control<TextBlock>("Email").Text != "alex@example.com")
            throw new InvalidOperationException("No-result query Enter must not activate a stale Person or dismiss search.");
        queryField.Text = "orchid";
        PumpUntil(() => queryResults.Items.Cast<PersonSearchResult>().Any(r => r.Matches.Any(m => m.NoteId == noteId)));
        if (Control<TextBlock>("Email").Text != "alex@example.com" ||
            Control<ListBox>("SearchResults").SelectedItem is not PersonSearchResult { Person.Email: "avery@example.com" })
            throw new InvalidOperationException("The first Person must be highlighted without opening a profile.");
        Enter(Control<TextBox>("SearchQuery"));
        PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com" &&
            Control<TextBox>("SearchQuery").Text == "");
        queryField.Text = "alex@example.com";
        Enter(queryField);
        PumpUntil(() => Control<TextBlock>("Email").Text == "alex@example.com" && queryField.Text == "");
        queryField.Text = "orchid";
        Enter(queryField);
        PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com" && queryField.Text == "");
        void Activate(string query, Func<PersonSearchResult, bool> predicate)
        {
            var results = Control<ListBox>("SearchResults");
            Control<TextBox>("SearchQuery").Text = "alex@example.com";
            PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(r => r.Person.Email == "alex@example.com"));
            results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(r => r.Person.Email == "alex@example.com");
            Enter(results);
            PumpUntil(() => Control<TextBlock>("Email").Text == "alex@example.com" && Control<TextBox>("SearchQuery").Text == "");
            Control<TextBox>("SearchQuery").Text = query;
            PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(predicate));
            results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(predicate);
            Enter(results);
            PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com" && Control<TextBox>("SearchQuery").Text == "");
        }
        Control<TextBox>("SearchQuery").Text = "avery";
        PumpUntil(() => Control<ListBox>("SearchResults").Items.Count == 1);
        Layout(false);
        var grouped = (PersonSearchResult)Control<ListBox>("SearchResults").Items[0];
        if (grouped.Matches.Count < 2 || !Control<TextBlock>("SearchCount").Text.StartsWith("People found: 1", StringComparison.Ordinal) ||
            !VisualDescendants(Control<ListBox>("SearchResults")).OfType<TextBlock>().Any(text => text.Text == "avery@example.com"))
            throw new InvalidOperationException("Grouped results must render one named Person, their email and a distinct-Person count.");
        Activate("007b", r => r.Person.Email == "avery@example.com");
        if (Control<TextBlock>("OfficeNumberValue").Text != "007B" ||
            System.Windows.Automation.AutomationProperties.GetHelpText(Control<TextBlock>("OfficeNumberValue")).Contains("Profile:", StringComparison.Ordinal))
            throw new InvalidOperationException("Profile search must open ordinary reading without selecting a matching field.");
        Control<Expander>("HistoryAccess").IsExpanded = false;
        Activate("cedar", r => r.Person.Email == "avery@example.com" && r.Matches.Any(m => m.Source == SearchSource.HistoricalTeamNote));
        if (Control<Expander>("HistoryAccess").IsExpanded)
            throw new InvalidOperationException("Historical search must not automatically navigate History.");
        Control<Expander>("HistoryAccess").IsExpanded = true;
        var historical = Control<FlowDocumentScrollViewer>("HistoricalTeamReading");
        var frozen = historical.Document.Blocks.OfType<Section>().SelectMany(section => section.Blocks.OfType<Section>())
            .Single(section => Equals(section.Tag, teamId));
        var frozenText = new TextRange(frozen.ContentStart, frozen.ContentEnd).Text;
        if (!frozenText.Contains("Cedar frozen writing", StringComparison.Ordinal) ||
            frozenText.Contains("Pine current writing", StringComparison.Ordinal) ||
            !frozenText.Contains("morgan@example.com", StringComparison.Ordinal))
            throw new InvalidOperationException("Historical search did not reveal the exact frozen owner's document.");
        Activate("morgan@example.com", r => r.Person.Email == "avery@example.com" &&
            r.Matches.Any(m => m.Source == SearchSource.FormerReportingChain && m.Field == "email"));
        if (new TextRange(historical.Document.ContentStart, historical.Document.ContentEnd).Text
                .Contains("Former Reporting chain: email", StringComparison.Ordinal))
            throw new InvalidOperationException("Grouped search must not insert a matched historical fact into ordinary History.");
        Activate("pine", r => r.Person.Email == "avery@example.com" && r.Matches.Any(m => m.Source == SearchSource.InheritedTeamNote));
        var teams = Control<FrameworkElement>("TeamNotes");
        ((Expander)teams.FindName("CurrentTeamContext")).IsExpanded = true;
        var inherited = (FlowDocumentScrollViewer)teams.FindName("InheritedReading");
        var currentText = new TextRange(inherited.Document.ContentStart, inherited.Document.ContentEnd).Text;
        if (!currentText.Contains("Pine current writing", StringComparison.Ordinal) ||
            !currentText.Contains("morgan@example.com", StringComparison.Ordinal))
            throw new InvalidOperationException("Ordinary inherited context lost the owner's current writing.");
        queryField.Text = "browse40";
        PumpUntil(() => queryResults.SelectedItem is PersonSearchResult { Person.Email: "browse40@example.com" });
        Enter(queryField);
        PumpUntil(() => queryField.Text == "" && Control<TextBlock>("Email").Text == "browse40@example.com");
        Settle(false);
        var acceptedRow = VisualDescendants(tree).OfType<TreeViewItem>().Single(item =>
            item.DataContext is ReportingNode { Person.Email: "browse40@example.com" });
        var treeViewport = tree.TransformToAncestor(root).TransformBounds(new Rect(tree.RenderSize));
        var acceptedBounds = acceptedRow.TransformToAncestor(root).TransformBounds(new Rect(acceptedRow.RenderSize));
        if (!acceptedRow.IsSelected || acceptedBounds.Left < treeViewport.Left || acceptedBounds.Left >= treeViewport.Right ||
            acceptedBounds.Top < treeViewport.Top || acceptedBounds.Bottom > treeViewport.Bottom ||
            !ReferenceEquals(FocusManager.GetFocusedElement(window), acceptedRow))
            throw new InvalidOperationException("Accepted search must realize/select/reveal its offscreen row, not restore the previous browsing offset. " +
                $"selected={acceptedRow.IsSelected}; viewport={treeViewport}; row={acceptedBounds}; " +
                $"focus={(FocusManager.GetFocusedElement(window) as FrameworkElement)?.Name}; focusedRow={ReferenceEquals(FocusManager.GetFocusedElement(window), acceptedRow)}; " +
                $"status={Control<TextBlock>("StatusText").Text}");
        Activate("007b", r => r.Person.Email == "avery@example.com");
        Control<TextBox>("SearchQuery").Clear();
        Settle(false);
        foreach (var compact in new[] { false, true })
        {
            VirtualizingPanel.SetCacheLength(tree, new VirtualizationCacheLength(compact ? 1 : 0));
            Settle(compact);
            if (compact) Invoke("OpenPeopleButton");
            Settle(compact);
            var manager = tree.Items.Cast<ReportingNode>().Single(n => n.Person.Email == "morgan@example.com");
            var branch = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manager);
            if (!UIElementAutomationPeer.CreatePeerForElement(branch).GetName().Contains("morgan@example.com", StringComparison.Ordinal))
                throw new InvalidOperationException("Compact reporting rows must retain exact accessible Person identity.");
            if (!compact)
            {
                branch.IsExpanded = true;
            }
            Settle(compact);
            foreach (var item in VisualDescendants(tree).OfType<TreeViewItem>().ToArray())
                item.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Settle(compact);
            if (!branch.IsExpanded)
                throw new InvalidOperationException("The compiled compact transition lost the expanded Manager branch.");
            var scrolling = VisualDescendants(tree).OfType<ScrollViewer>().First();
            var provider = (IScrollProvider)UIElementAutomationPeer.CreatePeerForElement(scrolling).GetPattern(PatternInterface.Scroll);
            if (!provider.VerticallyScrollable)
                throw new InvalidOperationException("The responsive replay must exercise a real reporting-tree scroll extent.");
            try
            {
                TreeItemRealization.PrepareMeasuredScrollPosition(70,
                    percent =>
                    {
                        Console.WriteLine($"BROWSE-PREP compact={compact}; request={percent}; actual={provider.VerticalScrollPercent}; " +
                            $"offset={scrolling.VerticalOffset}; extent={scrolling.ExtentHeight}; viewport={scrolling.ViewportHeight}");
                        provider.SetScrollPercent(-1, percent);
                    }, () => provider.VerticalScrollPercent,
                    condition => PumpUntil(() => { Layout(compact); return condition(); }));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"Compiled browsing preparation: compact={compact}; percent={provider.VerticalScrollPercent}; " +
                    $"offset={scrolling.VerticalOffset}; extent={scrolling.ExtentHeight}; viewport={scrolling.ViewportHeight}; " +
                    $"notice={Control<TextBlock>("NoticeSummary").Text}.", error);
            }
            if (compact)
            {
                scrolling.ScrollToHorizontalOffset(39);
                Settle(compact);
                if (Math.Abs(scrolling.HorizontalOffset - 39) > 0.01)
                    throw new InvalidOperationException($"The compact replay did not establish its observed 39-DIP horizontal offset: " +
                        $"offset={scrolling.HorizontalOffset}; extent={scrolling.ExtentWidth}; viewport={scrolling.ViewportWidth}.");
            }
            TreeItemRealization.BrowsingPosition ReadPosition()
            {
                var viewport = new Rect(tree.RenderSize);
                var rows = VisualDescendants(tree).OfType<TreeViewItem>()
                    .Where(item => item.DataContext is ReportingNode && item.ActualWidth > 0 && item.ActualHeight > 0)
                    .Select(item => (Node: (ReportingNode)item.DataContext,
                        Bounds: item.TransformToAncestor(tree).TransformBounds(new Rect(item.RenderSize))))
                    .Where(row => row.Bounds.IntersectsWith(viewport))
                    .Select(row => new TreeItemRealization.BrowsingRow(row.Node.Person.Email,
                        row.Bounds.Left, row.Bounds.Top)).ToArray();
                return new(provider.VerticalScrollPercent, rows);
            }
            if (Control<GroupBox>("PeoplePanel").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Browsing baseline requires the settled People panel to be open.");
            var originalPosition = ReadPosition();
            var originalEmail = Control<TextBlock>("Email").Text;
            Control<TextBox>("SearchQuery").Text = "orchid";
            PumpUntil(() => Control<ListBox>("SearchResults").Items.Cast<PersonSearchResult>().Any(r => r.Matches.Any(m => m.NoteId == noteId)));
            Settle(compact);
            if (compact)
            {
                // Replay lost virtualized width across hiding without changing the Person population.
                VirtualizingPanel.SetCacheLength(tree, new VirtualizationCacheLength(0));
                tree.InvalidateMeasure();
                Settle(compact);
            }
            Control<ListBox>("SearchResults").SelectedItem = Control<ListBox>("SearchResults").Items.Cast<PersonSearchResult>()
                .Single(r => r.Person.Email == "avery@example.com");
            if (Control<TextBlock>("Email").Text != originalEmail)
                throw new InvalidOperationException("Highlighting grouped results must not open another Person.");
            Settle(compact);
            Invoke("ClearSearchButton");
            Settle(compact);
            if (!TreeItemRealization.IsBrowsingPositionRestored(originalPosition, ReadPosition()) ||
                Control<FrameworkElement>("BrowsingPanel").Visibility != Visibility.Visible)
                throw new InvalidOperationException($"Compiled search/clear lost browsing restoration: compact={compact}; " +
                    $"originalRows={DescribeBrowsingRows(originalPosition)}; currentRows={DescribeBrowsingRows(ReadPosition())}; " +
                    $"originalPercent={originalPosition.Percent}%; actual={provider.VerticalScrollPercent}%; offset={scrolling.VerticalOffset}; " +
                    $"extent={scrolling.ExtentHeight}; viewport={scrolling.ViewportHeight}; " +
                    $"horizontalOffset={scrolling.HorizontalOffset}; extentWidth={scrolling.ExtentWidth}; " +
                    $"viewportWidth={scrolling.ViewportWidth}; scrollableWidth={scrolling.ScrollableWidth}.");
            provider.SetScrollPercent(-1, 0);
            Settle(compact);
            branch = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manager);
            if (!branch.IsExpanded)
                throw new InvalidOperationException("Search/clear lost the expanded Manager branch after returning to the top.");
        }
        Console.WriteLine("PASS: compiled grouped Person search, ordinary profile activation and manual-clear browsing restoration without a window/native handle.");
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static void VerifyAdaptiveReadingLayout(Window window)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var root = (FrameworkElement)window.Content;
        var viewport = Control<ScrollViewer>("PersonScroll");
        var body = Control<Grid>("PersonBody");
        var profile = Control<GroupBox>("ProfileSummary");
        var notes = Control<GroupBox>("NotesPane");
        var identity = Control<Grid>("PersonIdentity");
        var peopleDivider = Control<GridSplitter>("PeopleDivider");
        var people = Control<GroupBox>("PeoplePanel");
        var host = Control<Grid>("ExplorerPanel");
        void Layout(double width = 1920, double height = 1040)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The compiled divider regression must not show a window or create its native handle.");
        }
        void Contained(string stage)
        {
            var profileBounds = profile.TransformToAncestor(body).TransformBounds(new Rect(profile.RenderSize));
            var noteBounds = notes.TransformToAncestor(body).TransformBounds(new Rect(notes.RenderSize));
            var viewportBounds = viewport.TransformToAncestor(host).TransformBounds(new Rect(viewport.RenderSize));
            if (viewport.ViewportWidth <= 0 || people.ActualWidth < 239 || notes.ActualWidth < 359 ||
                viewportBounds.Right > host.ActualWidth + 1 ||
                body.ActualWidth > viewport.ViewportWidth + 1 ||
                noteBounds.Right > viewport.ViewportWidth + 1 ||
                Math.Abs(profileBounds.Left - noteBounds.Left) > 1 ||
                Math.Abs(profileBounds.Width - noteBounds.Width) > 1 || profileBounds.Bottom > noteBounds.Top + 1)
                throw new InvalidOperationException($"Compiled reading overflow/order {stage}: viewport={viewport.ViewportWidth}, " +
                    $"body={body.ActualWidth}, profile={profile.ActualWidth}, notes={notes.ActualWidth}, " +
                    $"profileBounds={profileBounds}, noteBounds={noteBounds}.");
        }
        void Drag(GridSplitter divider, double delta, string stage)
        {
            divider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            try
            {
                divider.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                Layout();
                Contained(stage + " held");
            }
            finally
            {
                divider.RaiseEvent(new DragCompletedEventArgs(delta, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Layout();
            }
            Contained(stage + " completed");
        }
        void ResizeWithKey(GridSplitter divider, FrameworkElement pane, Key key, double expectedDelta)
        {
            var before = pane.ActualWidth;
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, new UnshownInputSource { RootVisual = root }, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            divider.RaiseEvent(input);
            Layout();
            Contained($"{divider.Name} {key}");
            var actualDelta = pane.ActualWidth - before;
            if (!input.Handled || Math.Abs(actualDelta - expectedDelta) > 1)
                throw new InvalidOperationException($"Compiled {divider.Name} {key} did not resize by {expectedDelta} DIP: " +
                    $"before={before}, after={pane.ActualWidth}, actualDelta={actualDelta}, handled={input.Handled}.");
        }
        Layout();
        Contained("initial");
        VerifyFactRowSpacing(window);
        Layout();
        var photo = Control<FrameworkElement>("Photo");
        var placeholder = (TextBlock)photo.FindName("Placeholder");
        if (placeholder.Text != "A" || Math.Abs(photo.ActualWidth - 144) > 1 || Math.Abs(photo.ActualHeight - 144) > 1 ||
            !System.Windows.Automation.AutomationProperties.GetName(placeholder).Contains("No local photo", StringComparison.Ordinal))
            throw new InvalidOperationException("Missing photos need the same 144-DIP footprint and accessible initials as saved photos.");
        Drag(peopleDivider, 240 - people.ActualWidth, "People minimum");
        ResizeWithKey(peopleDivider, people, Key.Right, 10);
        Drag(peopleDivider, (host.ActualWidth - 8) * .27 - people.ActualWidth, "People 27%");
        Drag(peopleDivider, host.ActualWidth - people.ActualWidth, "People maximum");
        ResizeWithKey(peopleDivider, people, Key.Left, -10);
        Layout(1100, 900);
        Contained("narrower wide viewport");
        var personNotes = Control<FrameworkElement>("PersonNotes");
        var reading = (FlowDocumentScrollViewer)personNotes.FindName("Reading");
        var original = reading.Document;
        if (original.Blocks.OfType<Paragraph>().Any(paragraph => !Equals(paragraph.Foreground, window.Foreground)))
            throw new InvalidOperationException("Actual saved Markdown headings must retain normal body ink in the selected Read tab.");
        var longReading = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var paragraph in Enumerable.Range(1, 40))
            longReading.Blocks.Add(new Paragraph(new Run($"Paragraph {paragraph}: the same Person remains identifiable while reading.")));
        try
        {
            reading.Document = longReading;
            viewport.ScrollToTop();
            Layout();
            var identityBefore = identity.TransformToAncestor(host).TransformBounds(new Rect(identity.RenderSize));
            var factsBefore = profile.TransformToAncestor(host).TransformBounds(new Rect(profile.RenderSize));
            if (reading.ActualHeight <= 600 || reading.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ||
                reading.ActualWidth > 600 || viewport.ScrollableHeight <= 0)
                throw new InvalidOperationException($"Long note reading is still an independently bounded box: reading={reading.RenderSize}, outerExtent={viewport.ExtentHeight}.");
            viewport.ScrollToVerticalOffset(240);
            Layout();
            var identityAfter = identity.TransformToAncestor(host).TransformBounds(new Rect(identity.RenderSize));
            var factsAfter = profile.TransformToAncestor(host).TransformBounds(new Rect(profile.RenderSize));
            if (identityBefore != identityAfter || Math.Abs(factsBefore.Top - factsAfter.Top - 240) > 1)
                throw new InvalidOperationException($"The shared scroll moved identity or failed to move facts with notes: " +
                    $"identity={identityBefore}->{identityAfter}, facts={factsBefore}->{factsAfter}, " +
                    $"offset={viewport.VerticalOffset}, extent={viewport.ExtentHeight}, viewport={viewport.ViewportHeight}.");
            var provider = (IScrollProvider)UIElementAutomationPeer.CreatePeerForElement(viewport).GetPattern(PatternInterface.Scroll);
            if (!provider.VerticallyScrollable)
                throw new InvalidOperationException("The long reading fixture must still exercise a genuinely scrollable axis.");
            ControlReadiness.ScrollToTopIfScrollable(() => provider.VerticallyScrollable,
                () => provider.SetScrollPercent(System.Windows.Automation.ScrollPattern.NoScroll, 0));
            Layout();
            if (viewport.VerticalOffset != 0)
                throw new InvalidOperationException("A valid scroll-to-top request did not restore long reading to the top.");
        }
        finally
        {
            reading.Document = original;
            viewport.ScrollToTop();
            Layout();
        }
        Layout();
        Drag(peopleDivider, (host.ActualWidth - 8) * .27 - people.ActualWidth, "restored People 27%");
        var facts = Control<Panel>("FactsGrid");
        var slots = facts.Children.Cast<FrameworkElement>().ToArray();
        var lefts = slots.Select(slot => slot.TransformToAncestor(facts).Transform(new Point(0, 0)).X).ToArray();
        int Columns() => slots.Select(slot => Math.Round(slot.TransformToAncestor(facts).Transform(new Point()).X, 2)).Distinct().Count();
        var columns = Columns();
        var tree = Control<TreeView>("PeopleList");
        var originalPerson = tree.Items.Cast<ReportingNode>().Single(n => n.Person.Email == "avery@example.com");
        var longPerson = tree.Items.Cast<ReportingNode>().Single(n => n.Person.Email == "zara@example.com");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(originalPerson, longPerson, TreeView.SelectedItemChangedEvent));
        Layout();
        if (facts.Children.Count != 7 || Columns() != columns ||
            Control<TextBlock>("TeamNameEmpty").Visibility != Visibility.Visible ||
            slots.Any(slot => slot.Visibility != Visibility.Visible) ||
            slots.Where((slot, index) => Math.Abs(slot.TransformToAncestor(facts).Transform(new Point(0, 0)).X - lefts[index]) > 1).Any() ||
            Control<TextBlock>("DirectoryName").Text != "Zara with a deliberately long Directory name for accessible identity" ||
            Control<TextBlock>("OfficeNumberValue").Text != "—" ||
            Control<TextBlock>("LinkedInProfileUrlValue").Text != "—")
            throw new InvalidOperationException("Long or missing values repacked facts, lost full text, or hid labelled slots. " +
                $"Columns: {columns} -> {Columns()}; X: {string.Join(",", lefts)} -> " +
                $"{string.Join(",", slots.Select(slot => slot.TransformToAncestor(facts).Transform(new Point(0, 0)).X))}; " +
                $"Directory name={Control<TextBlock>("DirectoryName").Text}; office={Control<TextBlock>("OfficeNumberValue").Text}; " +
                $"URL={Control<TextBlock>("LinkedInProfileUrlValue").Text}; viewport={viewport.ViewportWidth}x{viewport.ViewportHeight}.");
        var wideColumns = Columns();
        Layout(700, 500);
        if (Columns() >= wideColumns || Control<Button>("OpenPeopleButton").Visibility != Visibility.Visible ||
            profile.ActualWidth != notes.ActualWidth || profile.ActualWidth > viewport.ViewportWidth)
            throw new InvalidOperationException("Compact facts must use fewer columns above full-width notes without shrinking type.");
        var beforeOverlay = viewport.TransformToAncestor(host).TransformBounds(new Rect(viewport.RenderSize));
        Control<Button>("OpenPeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(700, 500);
        if (people.Visibility != Visibility.Visible || beforeOverlay != viewport.TransformToAncestor(host).TransformBounds(new Rect(viewport.RenderSize)))
            throw new InvalidOperationException("The compiled compact overlay moved or squeezed the Person viewport.");
        Control<Button>("ClosePeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(longPerson, originalPerson, TreeView.SelectedItemChangedEvent));
        Layout();
        Contained("restored original Person");
        Control<TextBox>("SearchQuery").Text = "Researcher";
        Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Control<TextBox>("SearchQuery").Text != "" || Control<TextBlock>("Email").Text != "avery@example.com" ||
            Control<FrameworkElement>("BrowsingPanel").Visibility != Visibility.Visible)
            throw new InvalidOperationException("Clear search must restore browsing without replacing the selected Person.");
        Console.WriteLine("PASS: actual compiled facts precede full-width, content-sized reading under anchored identity; People drags/10-DIP keys stay within the viewport; no visible window/native handle.");
    }

    private static void VerifyRetainedNoteCreationFailure(Window window, string settings)
    {
        var folder = Path.Combine(settings, "retained-note-creation");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"89000000-0000-4000-8000-000000000001",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Note-creation failure fixture failed: " + seed.Status);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
        if (Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible)
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var tree = Control<TreeView>("PeopleList");
        var workflow = (WorkspaceViewModel)tree.DataContext;
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items.Cast<ReportingNode>().Single(),
            TreeView.SelectedItemChangedEvent));
        foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
        {
            var notes = Control<FrameworkElement>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes");
            var status = (TextBlock)notes.FindName("NoteStatus");
            int Count() => scope == NoteScope.Person ? workflow.GetPersonNotes("avery@example.com").Count :
                workflow.GetTeamNotes("avery@example.com").Count;
            var originalCount = Count();
            using (WorkspaceFixtures.LockLocalStore(folder))
            {
                ((Button)notes.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => workflow.LocalSaveError is not null && status.Text.Contains("NOT saved", StringComparison.Ordinal));
            }
            var retained = workflow.LocalSaveError!;
            if (Count() != originalCount || workflow.CanSafelyClose)
                throw new InvalidOperationException("Failed creation must retain its error without creating replacement writing.");
            using var provider = new SmokeSnapshotPeer(folder);
            provider.Publish(source => source.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "Delivered " + scope));
            if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
            workflow.SyncNow();
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
            if (workflow.LocalSaveError != retained || workflow.CanSafelyClose ||
                status.Visibility != Visibility.Visible || status.Text != retained ||
                System.Windows.Automation.AutomationProperties.GetItemStatus(notes) != retained)
                throw new InvalidOperationException($"Unrelated successful refresh must not replace the unresolved {scope} creation error with success feedback: {status.Text}");
            using (File.Open(Path.Combine(folder, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (workflow.RefreshWorkspace())
                    throw new InvalidOperationException("The unavailable local store was unexpectedly accepted.");
                settled = false;
                window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
                PumpUntil(() => settled);
                if (workflow.IsWorkspaceCurrent || !status.Text.Contains("NOT saved", StringComparison.Ordinal))
                    throw new InvalidOperationException("A distinct current failure must remain visible alongside the retained creation error.");
            }
            if (!workflow.ReconcileLocalSaves()) throw new InvalidOperationException(workflow.Status);
            if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
            workflow.DiscardUnsavedChanges();
            if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Status);
            ((Button)notes.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Count() == originalCount + 1 && status.Visibility == Visibility.Collapsed &&
                workflow.LocalSaveError is null);
        }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Note failure checks must not show a window or create its native handle.");
        Console.WriteLine("PASS: Person/Team creation failures remain visible through refresh and deferred snapshot delivery, with explicit local reconciliation before new writing.");
    }

    private static void VerifyPreferredNameSnapshotDeferral(Window window, string settings)
    {
        // Direct-file owner deletion is retired; complete incoming versions wait behind unconfirmed writing.
        var folder = Path.Combine(settings, "unavailable-preferred-name");
        void Seed(string path, string email, string name)
        {
            using var source = ApplicationWorkflow.CreateFileBacked();
            if (!source.OpenWorkspace(path) || !source.ApplyImport(source.PreviewImport($$"""
                {"schemaVersion":1,"batchId":"88000000-0000-4000-8000-000000000001",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"{{email}}","displayName":"Directory name","preferredName":"{{name}}","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Preferred-name snapshot fixture failed: " + source.Status);
        }
        Seed(folder, "avery@example.com", "Saved name");
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
        if (Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible) Invoke("OpenSettingsButton");
        var workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        void SelectOwner()
        {
            var tree = Control<TreeView>("PeopleList");
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items.Cast<ReportingNode>().Single(),
                TreeView.SelectedItemChangedEvent));
            if (Control<TextBlock>("Email").Text != "avery@example.com")
                throw new InvalidOperationException("Unavailable-name fixture did not select Avery.");
        }
        using var provider = new SmokeSnapshotPeer(folder);
        if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
        SelectOwner();
        Invoke("EditPreferredNameButton");
        Control<TextBox>("PreferredName").Text = "Retained local entry";
        provider.Publish(source => source.EditPreferredName("avery@example.com", "Changed externally") &&
            source.EditPersonArchived("avery@example.com", true));
        workflow.SyncNow();
        if (Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Visible ||
            Control<TextBox>("PreferredName").Text != "Retained local entry" ||
            Control<TextBlock>("Email").Text != "avery@example.com" ||
            workflow.GetRelationships("avery@example.com")!.Person.PreferredName != "Saved name" ||
            workflow.GetRelationships("avery@example.com")!.Person.Archived || workflow.CanSafelyClose)
            throw new InvalidOperationException("Incoming archival replaced the retained Preferred-name entry, exact owner or original save base.");
        Invoke("CancelPreferredNameButton");
        if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Status);
        using var durable = OpenSmokeWorkspace(folder);
        if (Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Collapsed ||
            durable.GetRelationships("avery@example.com")!.Person is not { Archived: true, PreferredName: "Changed externally" })
            throw new InvalidOperationException("Deliberate entry cancellation did not release validated snapshot archival without rewriting it.");
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Unavailable-name checks must not show a window or create its native handle.");
        Console.WriteLine("PASS: incoming snapshot archival retains Preferred-name input, exact owner and save base until deliberate Cancel releases adoption.");
    }

    private static void VerifyTreePresentation(Window window, string settings)
    {
        var folder = Path.Combine(settings, "tree-presentation");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"87000000-0000-4000-8000-000000000001",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null,"isFavorite":true},
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
                 {"email":"alex@example.com","displayName":"Alex","managerEmail":"avery@example.com"},
                 {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
                 {"email":"jordan@example.com","displayName":"Jordan","managerEmail":"casey@example.com"},
                 {"email":"jordan-leaf@example.com","displayName":"Jordan leaf","managerEmail":"jordan@example.com"},
                 {"email":"archived-a@example.com","displayName":"Archived A","managerEmail":null,"archived":true},
                 {"email":"archived-b@example.com","displayName":"Archived B","managerEmail":null,"archived":true},
                 {"email":"archived-c@example.com","displayName":"Archived C","managerEmail":null,"archived":true},
                 {"email":"archived-d@example.com","displayName":"Archived D","managerEmail":null,"archived":true}]}
                """).Id))
                throw new InvalidOperationException("Tree presentation fixture failed: " + seed.Status);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var root = (FrameworkElement)window.Content;
        void Layout(double width = 1100, double height = 900)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Tree measurements must not show a window or create its native handle.");
        }
        Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
        if (Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible)
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        var tree = Control<TreeView>("PeopleList");
        var manager = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "morgan@example.com");
        var container = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manager);
        var favorites = Control<ListBox>("FavoritesList");
        var favorite = (ListBoxItem)favorites.ItemContainerGenerator.ContainerFromItem(favorites.Items.Cast<object>().Single());
        var favoritePeer = UIElementAutomationPeer.CreatePeerForElement(favorite);
        var reportingPeer = UIElementAutomationPeer.CreatePeerForElement(container);
        var capturedTarget = TreeItemRealization.ReportingTarget<AutomationPeer>(
            [favoritePeer, reportingPeer], "morgan@example.com", peer => peer.GetAutomationId(),
            peer => peer.GetAutomationControlType() == AutomationControlType.TreeItem);
        Rect ObservedBounds(FrameworkElement element) =>
            element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        var capturedBounds = ObservedBounds((FrameworkElement)((UIElementAutomationPeer)capturedTarget).Owner);
        var treeBounds = ObservedBounds(tree);
        var reportingBounds = ObservedBounds(container);
        if (reportingBounds.Top < treeBounds.Top || reportingBounds.Top + 20 > treeBounds.Bottom)
            throw new InvalidOperationException("The real compiled reporting target is unavailable in the Favorite/People fixture.");
        if (!ReferenceEquals(capturedTarget, reportingPeer) ||
            favoritePeer.GetAutomationId() != reportingPeer.GetAutomationId() ||
            favoritePeer.GetAutomationControlType() != AutomationControlType.ListItem ||
            capturedBounds.Top < treeBounds.Top || capturedBounds.Top + 20 > treeBounds.Bottom)
            throw new InvalidOperationException($"Scale subtree expansion changed the open profile or lost its command target: " +
                $"captured={capturedTarget.GetAutomationControlType()}; target={capturedBounds}; " +
                $"reporting={reportingBounds}; viewport={treeBounds}.");
        var provider = (IExpandCollapseProvider)UIElementAutomationPeer.CreatePeerForElement(container).GetPattern(PatternInterface.ExpandCollapse);
        var expander = (ToggleButton)container.Template.FindName("Expander", container);
        var chevron = (System.Windows.Shapes.Path)expander.Template.FindName("Chevron", expander);
        Rect Ink() => chevron.TransformToAncestor(tree).TransformBounds(
            chevron.RenderedGeometry.GetRenderBounds(new Pen(chevron.Stroke, chevron.StrokeThickness)));
        Rect ToggleBounds() => expander.TransformToAncestor(tree).TransformBounds(new Rect(expander.RenderSize));
        var collapsedToggle = ToggleBounds();
        var collapsed = Ink();
        var header = (FrameworkElement)container.Template.FindName("PART_Header", container);
        var headerBefore = header.TransformToAncestor(tree).Transform(new Point());
        provider.Expand();
        Layout();
        var expanded = Ink();
        var headerAfter = header.TransformToAncestor(tree).Transform(new Point());
        var centerBefore = collapsed.Left + collapsed.Width / 2;
        var centerAfter = expanded.Left + expanded.Width / 2;
        Console.WriteLine($"TREE-CHEVRON collapsed={collapsed}; expanded={expanded}; centers={centerBefore}/{centerAfter}; " +
            $"toggle={collapsedToggle}/{ToggleBounds()}; header={headerBefore}/{headerAfter}.");
        if (collapsedToggle != ToggleBounds() || Math.Abs(centerBefore - centerAfter) > 1 || headerBefore.X != headerAfter.X)
            throw new InvalidOperationException("Expanded and collapsed chevrons must keep the same depth and horizontal center without moving the Person label.");
        provider.Collapse();
        Layout();
        var rowDiscoveries = 0;
        var liveRows = TreeItemRealization.ReportingRows(email =>
        {
            rowDiscoveries++;
            var item = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == email);
            return (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(item);
        }, (row, email) => row.DataContext is ReportingNode node && node.Person.Email == email);
        if (liveRows("morgan@example.com").IsExpanded)
            throw new InvalidOperationException("The reporting-row lookup did not observe the collapsed control.");
        provider.Expand();
        Layout();
        if (!liveRows("morgan@example.com").IsExpanded || rowDiscoveries != 1)
            throw new InvalidOperationException($"Live reporting-row observation repeated discovery or retained stale expansion: discoveries={rowDiscoveries}.");
        provider.Collapse();
        Layout();
        var sibling = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "casey@example.com");
        var siblingContainer = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(sibling);
        siblingContainer.IsExpanded = true;
        var selection = (ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(container).GetPattern(PatternInterface.SelectionItem);
        selection.Select();
        Layout();
        if (Control<TextBlock>("Email").Text != "morgan@example.com" || !container.IsExpanded)
            throw new InvalidOperationException("Selecting a Person must expand that Person's immediate reports.");
        var child = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single());
        if (child.IsExpanded || !siblingContainer.IsExpanded)
            throw new InvalidOperationException("Selecting a Person must not recursively expand reports or collapse another branch.");
        provider.Collapse();
        Control<TextBox>("SearchQuery").Text = "avery";
        Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        container = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manager);
        if (container.IsExpanded)
            throw new InvalidOperationException("A deliberate collapse of the selected Person must survive search/clear without a new Person selection.");
        Control<TextBox>("SearchQuery").Text = "avery";
        var results = Control<ListBox>("SearchResults");
        PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == "avery@example.com"));
        results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result => result.Person.Email == "avery@example.com");
        results.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new UnshownInputSource { RootVisual = root }, 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com");
        Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        container = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manager);
        child = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single());
        siblingContainer = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(sibling);
        if (!container.IsExpanded || child is null || !child.IsExpanded || !child.IsSelected ||
            child.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single().Reports.Single()) is not TreeViewItem ||
            !siblingContainer.IsExpanded)
            throw new InvalidOperationException("A Person selected through search must return to a visible, expanded branch without losing unrelated expansion.");
        siblingContainer.IsExpanded = false;
        var notes = Control<FrameworkElement>("PersonNotes");
        ((Button)notes.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBox)notes.FindName("NoteTitle")).Text == "New Person note");
        ((TextBox)notes.FindName("NoteTitle")).Text = "";
        PumpUntil(() => ((TextBlock)notes.FindName("NoteStatus")).Text.Contains("NOT saved", StringComparison.Ordinal));
        ((ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(siblingContainer)
            .GetPattern(PatternInterface.SelectionItem)).Select();
        Layout();
        if (Control<TextBlock>("Email").Text != "casey@example.com" || !siblingContainer.IsExpanded)
            throw new InvalidOperationException("Failed note writing must not block selecting and expanding another Person.");
        ((ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(child)
            .GetPattern(PatternInterface.SelectionItem)).Select();
        Layout();
        if (Control<TextBlock>("Email").Text != "avery@example.com" ||
            ((TextBox)notes.FindName("NoteTitle")).Text != "")
            throw new InvalidOperationException("Returning through the tree lost the original Person's exact failed draft.");
        Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Control<TextBox>("OfficeNumber").Text = "  Compiled independent profile writing\r\n";
        container.IsExpanded = true;
        Layout();
        child = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single());
        child.IsExpanded = true;
        siblingContainer.IsExpanded = true;
        Layout();
        var model = (WorkspaceViewModel)tree.DataContext;
        var beforeAction = model.PeopleNavigation.SelectedEmail;
        var profileDraft = model.ProfileEdit;
        var noteDraft = model.GetRetainedNoteDraft(NoteScope.Person, model.PersonNotesView.SelectedNote!.Id);
        var beforeError = model.OperationError;
        var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
        container.RaiseEvent(rightClick);
        if (!rightClick.Handled || model.PeopleNavigation.SelectedEmail != beforeAction)
            throw new InvalidOperationException("Reporting-row right-click must bypass ordinary focus/selection navigation.");
        var menu = container.ContextMenu;
        if (menu is null || menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, "Collapse subtree")) is not { } collapse ||
            collapse.Command is not RoutedCommand command ||
            menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, "Collapse other branches")) is not { } collapseOthers ||
            collapseOthers.Command is not RoutedCommand outsideCommand ||
            menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, "Expand subtree")) is not { } expand ||
            expand.Command is not RoutedCommand expandCommand)
            throw new InvalidOperationException("The compiled reporting-row menu must expose all three routed tree actions.");
        menu.PlacementTarget = container;
        System.Windows.Data.BindingOperations.GetBindingExpression(collapse, MenuItem.CommandParameterProperty)?.UpdateTarget();
        System.Windows.Data.BindingOperations.GetBindingExpression(collapse, MenuItem.CommandTargetProperty)?.UpdateTarget();
        if (!Equals(collapse.CommandParameter, "morgan@example.com") ||
            collapse.CommandTarget != container || !command.CanExecute(collapse.CommandParameter, container))
            throw new InvalidOperationException("The real compiled tree command must target its placement row, not the open profile.");
        command.Execute(collapse.CommandParameter, container);
        Layout();
        if (container.IsExpanded || child.IsExpanded || !siblingContainer.IsExpanded ||
            model.PeopleNavigation.SelectedEmail != beforeAction || Control<TextBlock>("Email").Text != beforeAction ||
            model.PeopleNavigation.ExpandedPeople.Contains("morgan@example.com") ||
            model.PeopleNavigation.ExpandedPeople.Contains("avery@example.com"))
            throw new InvalidOperationException("Recursive compiled collapse must reset logical/current rows while retaining the hidden profile and unrelated expansion.");
        if (model.ProfileEdit != profileDraft ||
            model.GetRetainedNoteDraft(NoteScope.Person, model.PersonNotesView.SelectedNote!.Id) != noteDraft ||
            model.OperationError != beforeError || Control<TextBox>("OfficeNumber").Text != "  Compiled independent profile writing\r\n" ||
            ((TextBox)notes.FindName("NoteTitle")).Text != "" || model.CanSafelyClose)
            throw new InvalidOperationException("The compiled tree command submitted or replaced independent profile/note writing or cleared its error.");
        Layout(700, 500);
        Control<Button>("OpenPeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(700, 500);
        if (!model.PeopleNavigation.OverlayOpen || container.IsExpanded || child.IsExpanded || !siblingContainer.IsExpanded)
            throw new InvalidOperationException("Compact People restoration resurrected recursively reset expansion.");
        Layout();
        if (container.IsExpanded || child.IsExpanded || !siblingContainer.IsExpanded || model.ProfileEdit != profileDraft)
            throw new InvalidOperationException("Returning to wide People resurrected expansion or replaced retained writing.");
        Control<TextBox>("SearchQuery").Text = "casey";
        Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        if (container.IsExpanded || !siblingContainer.IsExpanded)
            throw new InvalidOperationException("Search-clear resurrected recursively reset expansion.");
        container.IsExpanded = true;
        Layout();
        child = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single());
        if (child.IsExpanded)
            throw new InvalidOperationException("Later realization restored old nested expansion after recursive collapse.");
        child.DataContext = sibling;
        if (!child.IsExpanded)
            throw new InvalidOperationException("A recycled row did not adopt the next Person's remembered expansion.");
        child.DataContext = manager.Reports.Single();
        if (child.IsExpanded)
            throw new InvalidOperationException("A recycled row resurrected the recursively reset Person's expansion.");
        if (command.CanExecute("alex@example.com", child))
            throw new InvalidOperationException("A leaf must not expose a no-op Collapse subtree command.");
        Layout();
        siblingContainer.IsExpanded = true;
        Layout();
        var outsideNode = sibling.Reports.Single();
        var outsideRow = (TreeViewItem)siblingContainer.ItemContainerGenerator.ContainerFromItem(outsideNode);
        outsideRow.IsExpanded = true;
        siblingContainer.IsExpanded = false;
        Layout();
        menu.PlacementTarget = child;
        System.Windows.Data.BindingOperations.GetBindingExpression(collapseOthers, MenuItem.CommandParameterProperty)?.UpdateTarget();
        System.Windows.Data.BindingOperations.GetBindingExpression(collapseOthers, MenuItem.CommandTargetProperty)?.UpdateTarget();
        if (!Equals(collapseOthers.CommandParameter, "avery@example.com") || collapseOthers.CommandTarget != child ||
            !outsideCommand.CanExecute(collapseOthers.CommandParameter, child))
            throw new InvalidOperationException("Hidden outside expansion must offer the actual routed action on a collapsed target.");
        outsideCommand.Execute(collapseOthers.CommandParameter, child);
        Layout();
        if (!container.IsExpanded || child.IsExpanded || siblingContainer.IsExpanded || outsideRow.IsExpanded ||
            !model.PeopleNavigation.ExpandedPeople.SequenceEqual(["morgan@example.com"]) ||
            model.PeopleNavigation.SelectedEmail != beforeAction || model.ProfileEdit != profileDraft ||
            model.GetRetainedNoteDraft(NoteScope.Person, model.PersonNotesView.SelectedNote!.Id) != noteDraft ||
            model.OperationError != beforeError || Control<TextBox>("OfficeNumber").Text != "  Compiled independent profile writing\r\n")
            throw new InvalidOperationException("Outside collapse lost its ancestor path, expanded its target, navigated or changed retained writing.");
        Layout(700, 500);
        Layout();
        Control<TextBox>("SearchQuery").Text = "casey";
        Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        if (!container.IsExpanded || child.IsExpanded || siblingContainer.IsExpanded ||
            model.PeopleNavigation.ExpandedPeople.Contains("jordan@example.com"))
            throw new InvalidOperationException("Search or layout restoration resurrected outside expansion.");
        child.IsExpanded = true;
        siblingContainer.IsExpanded = true;
        Layout();
        outsideRow = (TreeViewItem)siblingContainer.ItemContainerGenerator.ContainerFromItem(outsideNode);
        if (outsideRow.IsExpanded)
            throw new InvalidOperationException("Reopening an outside root resurrected cleared hidden expansion.");
        outsideRow.IsExpanded = true;
        Layout();
        var leaf = (TreeViewItem)child.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single().Reports.Single());
        if (!outsideCommand.CanExecute("alex@example.com", leaf))
            throw new InvalidOperationException("A leaf must offer outside collapse when unrelated expansion exists.");
        Layout(700, 500);
        Control<Button>("OpenPeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(700, 500);
        if (!model.PeopleNavigation.OverlayOpen)
            throw new InvalidOperationException("The leaf outside-collapse fixture must begin with compact People open.");
        outsideCommand.Execute("alex@example.com", leaf);
        Layout(700, 500);
        if (!container.IsExpanded || !child.IsExpanded || siblingContainer.IsExpanded || outsideRow.IsExpanded ||
            model.PeopleNavigation.SelectedEmail != beforeAction || model.ProfileEdit != profileDraft ||
            model.GetRetainedNoteDraft(NoteScope.Person, model.PersonNotesView.SelectedNote!.Id) != noteDraft ||
            !model.PeopleNavigation.OverlayOpen)
            throw new InvalidOperationException("Leaf outside collapse changed its protected path/subtree, profile, writing or compact panel.");
        outsideRow.DataContext = manager.Reports.Single();
        if (!outsideRow.IsExpanded)
            throw new InvalidOperationException("The recycled outside row did not adopt protected target expansion.");
        outsideRow.DataContext = outsideNode;
        if (outsideRow.IsExpanded || outsideCommand.CanExecute("alex@example.com", leaf))
            throw new InvalidOperationException("Recycling resurrected cleared outside expansion or offered a no-op leaf action.");
        menu.PlacementTarget = leaf;
        foreach (var item in new[] { expand, collapse, collapseOthers })
        {
            System.Windows.Data.BindingOperations.GetBindingExpression(item, MenuItem.CommandParameterProperty)?.UpdateTarget();
            System.Windows.Data.BindingOperations.GetBindingExpression(item, MenuItem.CommandTargetProperty)?.UpdateTarget();
        }
        CommandManager.InvalidateRequerySuggested();
        PumpUntil(() => !expand.IsEnabled && !collapse.IsEnabled && !collapseOthers.IsEnabled);
        if (expand.Visibility != Visibility.Collapsed || collapse.Visibility != Visibility.Collapsed ||
            collapseOthers.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Actual no-op leaf menu actions must be omitted, not merely disabled.");
        var normalTreeHeight = tree.Height;
        tree.Height = 92;
        Layout(700, 500);
        IScrollItemProvider LeafScroll() => (IScrollItemProvider)UIElementAutomationPeer.CreatePeerForElement(tree).GetChildren()
            .Single(peer => peer.GetAutomationId() == "morgan@example.com").GetChildren()
            .Single(peer => peer.GetAutomationId() == "avery@example.com").GetChildren()
            .Single(peer => peer.GetAutomationId() == "alex@example.com").GetPattern(PatternInterface.ScrollItem);
        (double Left, double Top, double Width, double Height) LeafBounds()
        {
            var origin = leaf.TranslatePoint(new Point(), tree);
            return (origin.X, origin.Y, leaf.ActualWidth, leaf.ActualHeight);
        }
        bool ContextVisible()
        {
            var row = LeafBounds();
            var managerTop = container.TranslatePoint(new Point(), tree).Y;
            return row.Top >= 0 && row.Top + 20 <= tree.ActualHeight &&
                managerTop >= 0 && managerTop + 20 <= tree.ActualHeight;
        }
        void AwaitContext(Func<bool> condition) => PumpUntil(() => { Layout(700, 500); return condition(); });
        LeafScroll().ScrollIntoView();
        AwaitContext(ContextVisible);
        var fixtureInputs = new List<string>();
        TreeViewItem FixtureBranch(string email) => email == "casey@example.com" ? siblingContainer :
            (TreeViewItem)siblingContainer.ItemContainerGenerator.ContainerFromItem(outsideNode);
        TreeItemRealization.PrepareMenuBranches(["casey@example.com", "jordan@example.com"],
            email => FixtureBranch(email).IsExpanded, (email, input) =>
            {
                if (input != TreeItemRealization.MenuBranchInput.DisclosureToggle)
                    throw new InvalidOperationException("Compiled menu setup must use the identity-owned disclosure.");
                var row = FixtureBranch(email);
                var toggle = (ToggleButton)row.Template.FindName("Expander", row);
                ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle)
                    .GetPattern(PatternInterface.Toggle)).Toggle();
                fixtureInputs.Add(email);
            }, AwaitContext);
        var viewport = (0d, 0d, tree.ActualWidth, tree.ActualHeight);
        try
        {
            TreeItemRealization.ReportingHeaderPoint(LeafBounds(), viewport, 1);
            throw new InvalidOperationException("The compiled preparation replay did not reproduce its offscreen header.");
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("The reporting row header has no visible content pixel:", StringComparison.Ordinal))
        {
            Console.WriteLine("TREE-MENU-PREPARATION-COMPILED expected refusal: " + error.Message);
        }
        var menuRealizations = 0;
        TreeItemRealization.AwaitVisible(() => leaf, _ =>
        {
            if (ContextVisible()) return;
            menuRealizations++;
            LeafScroll().ScrollIntoView();
        }, _ => ContextVisible(), AwaitContext);
        TreeItemRealization.ReportingHeaderPoint(LeafBounds(), viewport, 1);
        if (!fixtureInputs.SequenceEqual(["casey@example.com", "jordan@example.com"]) || menuRealizations != 1 ||
            model.PeopleNavigation.SelectedEmail != beforeAction || model.ProfileEdit != profileDraft ||
            model.GetRetainedNoteDraft(NoteScope.Person, model.PersonNotesView.SelectedNote!.Id) != noteDraft ||
            model.OperationError != beforeError || !model.PeopleNavigation.OverlayOpen ||
            Control<TextBox>("OfficeNumber").Text != "  Compiled independent profile writing\r\n")
            throw new InvalidOperationException("Pre-input menu realization replayed setup or changed retained profile/writing/People.");
        tree.Height = normalTreeHeight;
        Layout();
        command.Execute("morgan@example.com", container);
        container.IsExpanded = true;
        siblingContainer.IsExpanded = true;
        Layout();
        menu.PlacementTarget = container;
        System.Windows.Data.BindingOperations.GetBindingExpression(expand, MenuItem.CommandParameterProperty)?.UpdateTarget();
        System.Windows.Data.BindingOperations.GetBindingExpression(expand, MenuItem.CommandTargetProperty)?.UpdateTarget();
        if (!Equals(expand.CommandParameter, "morgan@example.com") || expand.CommandTarget != container ||
            !expandCommand.CanExecute(expand.CommandParameter, container))
            throw new InvalidOperationException("The compiled Expand subtree binding must target its placement row, not the open profile.");
        expandCommand.Execute(expand.CommandParameter, container);
        if (model.PeopleTreeExpansion is not { IsRunning: true } ||
            Control<Button>("CancelTreeExpansionButton").Visibility != Visibility.Visible)
            throw new InvalidOperationException("The actual expansion command must expose an actionable cancellation control before unfinished work.");
        var currentDisclosure = (ToggleButton)siblingContainer.Template.FindName("Expander", siblingContainer);
        var currentDisclosurePeer = UIElementAutomationPeer.CreatePeerForElement(tree).GetChildren()
            .Single(peer => peer.GetAutomationId() == "casey@example.com").GetChildren()
            .Single(peer => peer.GetAutomationId() == "Expander" && peer.GetName() == "Expand or collapse reporting subtree");
        ((IToggleProvider)currentDisclosurePeer
            .GetPattern(PatternInterface.Toggle)).Toggle();
        if (siblingContainer.IsExpanded || model.PeopleTreeExpansion.Status != PeopleTreeExpansionStatus.Cancelled ||
            !container.IsExpanded || model.ProfileEdit != profileDraft)
            throw new InvalidOperationException($"Ordinary disclosure must still apply its own state and cancel unfinished recursive expansion without navigating: " +
                $"expanded={container.IsExpanded}; disclosure={currentDisclosure.IsChecked}; operation={model.PeopleTreeExpansion.Status}; " +
                $"outside={siblingContainer.IsExpanded}; profileRetained={model.ProfileEdit == profileDraft}.");
        ((IToggleProvider)currentDisclosurePeer.GetPattern(PatternInterface.Toggle)).Toggle();
        expandCommand.Execute("morgan@example.com", container);
        Control<Button>("CancelTreeExpansionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (model.PeopleTreeExpansion.Status != PeopleTreeExpansionStatus.Cancelled ||
            !expandCommand.CanExecute("morgan@example.com", container))
            throw new InvalidOperationException("Actual expansion cancellation must retain partial state and allow another expansion.");
        expandCommand.Execute("morgan@example.com", container);
        PumpUntil(() => model.PeopleTreeExpansion is { Status: PeopleTreeExpansionStatus.Completed });
        Layout();
        child = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single());
        if (!container.IsExpanded || !child.IsExpanded || !siblingContainer.IsExpanded ||
            child.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single().Reports.Single()) is not TreeViewItem ||
            expandCommand.CanExecute("morgan@example.com", container) ||
            expandCommand.CanExecute("alex@example.com", child) ||
            model.PeopleNavigation.SelectedEmail != beforeAction || Control<TextBlock>("Email").Text != beforeAction ||
            model.ProfileEdit != profileDraft ||
            model.GetRetainedNoteDraft(NoteScope.Person, model.PersonNotesView.SelectedNote!.Id) != noteDraft ||
            model.OperationError != beforeError || Control<TextBox>("OfficeNumber").Text != "  Compiled independent profile writing\r\n")
            throw new InvalidOperationException("Compiled full expansion lost nested/current rows, action availability or independent owner writing.");
        child.DataContext = sibling;
        child.DataContext = manager.Reports.Single();
        if (!child.IsExpanded)
            throw new InvalidOperationException("A recycled row did not retain completed subtree expansion.");
        Layout(700, 500);
        Control<Button>("OpenPeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(700, 500);
        Control<TextBox>("SearchQuery").Text = "casey";
        Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(700, 500);
        if (!model.PeopleNavigation.OverlayOpen || !container.IsExpanded || !child.IsExpanded ||
            model.ProfileEdit != profileDraft || Control<TextBlock>("Email").Text != beforeAction)
            throw new InvalidOperationException("Compact search-clear restoration lost completed subtree expansion or owner writing.");
        Layout();
        command.Execute("morgan@example.com", container);
        Layout();
        container.IsExpanded = true;
        Layout();
        ((Button)notes.FindName("DiscardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Control<Button>("CancelProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => model.CanSafelyClose);
        ((ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(siblingContainer)
            .GetPattern(PatternInterface.SelectionItem)).Select();
        container.IsExpanded = true;
        Layout();
        child = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single());
        child.IsExpanded = true;
        ((IExpandCollapseProvider)UIElementAutomationPeer.CreatePeerForElement(container)
            .GetPattern(PatternInterface.ExpandCollapse)).Collapse();
        Layout();
        if (!child.IsExpanded || !model.PeopleNavigation.ExpandedPeople.Contains("avery@example.com") ||
            model.PeopleNavigation.ExpandedPeople.Contains("morgan@example.com") ||
            !command.CanExecute("morgan@example.com", container))
            throw new InvalidOperationException("Ordinary disclosure collapse must retain actionable hidden nested expansion.");
        command.Execute("morgan@example.com", container);
        Layout();
        if (child.IsExpanded || model.PeopleNavigation.ExpandedPeople.Contains("avery@example.com") ||
            model.PeopleNavigation.SelectedEmail != "casey@example.com")
            throw new InvalidOperationException("Recursive collapse failed to clear hidden nested expansion without navigating the unrelated profile.");
        container.IsExpanded = true;
        Layout();
        child = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(manager.Reports.Single());
        ((ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(child)
            .GetPattern(PatternInterface.SelectionItem)).Select();
        Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var retainedProfile = model.ProfileEdit;
        Layout(700, 500);
        Control<Button>("OpenPeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(700, 500);
        var managerDataPeer = UIElementAutomationPeer.CreatePeerForElement(tree).GetChildren()
            .Single(peer => peer.GetAutomationId() == "morgan@example.com");
        var retainedProvider = (IExpandCollapseProvider)managerDataPeer.GetPattern(PatternInterface.ExpandCollapse);
        command.Execute("morgan@example.com", container);
        Layout(700, 500);
        if (container.IsExpanded || !model.PeopleNavigation.OverlayOpen)
            throw new InvalidOperationException("The compact compiled provider replay did not reach its collapsed/open-panel baseline.");
        retainedProvider.Expand();
        Layout(700, 500);
        if (!container.IsExpanded || model.PeopleNavigation.SelectedEmail != "avery@example.com" ||
            !model.PeopleNavigation.OverlayOpen || model.ProfileEdit != retainedProfile ||
            Control<TextBlock>("Email").Text != "avery@example.com")
            throw new InvalidOperationException("A retained real WPF data-item expansion provider navigated or dismissed compact People after recursive collapse.");
        command.Execute("morgan@example.com", container);
        Layout(700, 500);
        var disclosure = (ToggleButton)container.Template.FindName("Expander", container);
        var disclosureDataPeer = managerDataPeer.GetChildren().Single(peer =>
            peer.GetAutomationId() == "Expander" && peer.GetName() == "Expand or collapse reporting subtree");
        var disclosureProvider = (IToggleProvider)disclosureDataPeer.GetPattern(PatternInterface.Toggle);
        var ordinaryRequests = 0;
        var disclosureRequests = 0;
        TreeItemRealization.PrepareMenuBranch(() => container.IsExpanded, input =>
        {
            if (input == TreeItemRealization.MenuBranchInput.ExpandCollapseProvider)
            {
                ordinaryRequests++;
                retainedProvider.Expand();
            }
            else if (input == TreeItemRealization.MenuBranchInput.DisclosureToggle)
            {
                disclosureRequests++;
                disclosureProvider.Toggle();
            }
            else throw new InvalidOperationException("Menu preparation requested a coordinate-bound disclosure input.");
        }, condition => PumpUntil(condition));
        Layout(700, 500);
        if (disclosure.Focusable || ordinaryRequests != 0 || disclosureRequests != 1 || !container.IsExpanded ||
            model.PeopleNavigation.SelectedEmail != "avery@example.com" || !model.PeopleNavigation.OverlayOpen ||
            model.ProfileEdit != retainedProfile || Control<TextBlock>("Email").Text != "avery@example.com")
            throw new InvalidOperationException("Menu preparation must use the real non-focusable disclosure binding, not ordinary provider expansion/navigation.");
        Control<Button>("CancelProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => model.CanSafelyClose);
        Control<CheckBox>("ShowArchivedPeople").IsChecked = true;
        Layout(700, 500);
        var archivedList = Control<ListBox>("ArchivedPeopleList");
        var archivedPerson = archivedList.Items.Cast<PersonProfile>().Single(person => person.Email == "archived-a@example.com");
        archivedList.SelectedItem = archivedPerson;
        Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Control<TextBox>("OfficeNumber").Text = "  Exact archived-profile fixture writing\r\n";
        var archivedDraft = model.ProfileEdit;
        Layout(700, 500);
        Control<Button>("OpenPeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(700, 500);
        var archiveToggle = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(Control<CheckBox>("ShowArchivedPeople"))
            .GetPattern(PatternInterface.Toggle);
        var archiveInputs = 0;
        bool ArchiveShown() => Control<CheckBox>("ShowArchivedPeople").IsChecked == true;
        void ToggleArchive() { archiveInputs++; archiveToggle.Toggle(); }
        var archiveInitiallyShown = TreeItemRealization.PrepareArchivedListForMenu(ArchiveShown, ToggleArchive, () =>
        {
            Layout(700, 500);
            return tree.ActualHeight > 0;
        }, condition => PumpUntil(condition));
        Layout(700, 500);
        container = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manager);
        command.Execute("morgan@example.com", container);
        Layout(700, 500);
        if (!archiveInitiallyShown || archiveInputs != 1 || ArchiveShown() || container.IsExpanded ||
            model.PeopleNavigation.SelectedEmail != archivedPerson.Email || !model.PeopleNavigation.OverlayOpen ||
            model.ProfileEdit != archivedDraft || Control<TextBox>("OfficeNumber").Text != "  Exact archived-profile fixture writing\r\n")
            throw new InvalidOperationException("The real archived-list fixture hide/menu action lost its archived profile/draft or changed its input count.");
        TreeItemRealization.RestoreArchivedListAfterMenu(archiveInitiallyShown, ArchiveShown, ToggleArchive,
            condition => PumpUntil(condition));
        Layout(700, 500);
        if (!ArchiveShown() || archiveInputs != 2 || model.ProfileEdit != archivedDraft ||
            model.PeopleNavigation.SelectedEmail != archivedPerson.Email || !model.PeopleNavigation.OverlayOpen ||
            Control<TextBox>("OfficeNumber").Text != "  Exact archived-profile fixture writing\r\n")
            throw new InvalidOperationException("The real archived-list restoration changed its captured mode, profile/draft or open People panel.");
        Control<Button>("CancelProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => model.CanSafelyClose);
        VerifySearchDuringTreeExpansion(window, settings);
        Console.WriteLine("PASS: real tree selection/disclosure, all three non-navigating tree actions, pre-input menu realization, actual cancellation/disclosure interruption, protected path/leaf scope, hidden/recycled reset, search/layout restoration and exact independent profile/note writing.");
    }

    private static void VerifySearchDuringTreeExpansion(Window window, string settings)
    {
        var folder = Path.Combine(settings, "search-during-tree-expansion");
        var people = Enumerable.Range(0, 256).SelectMany(index => new[]
        {
            new { email = $"branch{index:000}@example.com", displayName = $"Search branch {index:000}", managerEmail = (string?)"root@example.com" },
            new { email = $"leaf{index:000}@example.com", displayName = $"Search leaf {index:000}", managerEmail = (string?)$"branch{index:000}@example.com" }
        }).Prepend(new { email = "root@example.com", displayName = "Root", managerEmail = (string?)null })
          .Append(new { email = "owner@example.com", displayName = "Unrelated owner", managerEmail = (string?)null })
          .Append(new { email = "destination@example.com", displayName = "Search destination", managerEmail = (string?)null })
          .Append(new { email = "unrelated@example.com", displayName = "Z separate reporting branch", managerEmail = (string?)null })
          .Concat(Enumerable.Range(0, 256).Select(index => new
          {
              email = $"unrelated{index:000}@example.com", displayName = $"Unrelated report {index:000}",
              managerEmail = (string?)"unrelated@example.com"
          }));
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, batchId = Guid.NewGuid(), observedAt = "2026-11-01T17:00:00Z", people
            })).Id))
                throw new InvalidOperationException("Search during expansion fixture failed: " + seed.Status);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var surface = (FrameworkElement)window.Content;
        void Layout(double width, double height)
        {
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Search during expansion must not show a window or create its native handle.");
        }
        bool Presented(FrameworkElement control)
        {
            for (DependencyObject? current = control; current is not null && !ReferenceEquals(current, surface);
                current = VisualTreeHelper.GetParent(current))
                if (current is FrameworkElement { Visibility: not Visibility.Visible }) return false;
            var bounds = control.TransformToAncestor(surface).TransformBounds(new Rect(control.RenderSize));
            return bounds.Width > 0 && bounds.Height > 0 && new Rect(surface.RenderSize).Contains(bounds);
        }
        Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
        if (Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible)
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(1100, 900);
        var tree = Control<TreeView>("PeopleList");
        var model = (WorkspaceViewModel)tree.DataContext;
        var owner = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "owner@example.com");
        ((ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(
            (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(owner)).GetPattern(PatternInterface.SelectionItem)).Select();
        var ownerRevealSettled = false;
        window.Dispatcher.BeginInvoke(() => ownerRevealSettled = true, DispatcherPriority.ContextIdle);
        PumpUntil(() => ownerRevealSettled);
        VisualDescendants(tree).OfType<ScrollViewer>().First().ScrollToTop();
        Layout(1100, 900);
        var fixtureScrollSettled = false;
        window.Dispatcher.BeginInvoke(() => fixtureScrollSettled = true, DispatcherPriority.ContextIdle);
        PumpUntil(() => fixtureScrollSettled);
        Layout(1100, 900);
        Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Control<TextBox>("OfficeNumber").Text = "  Search-time independent profile writing\r\n";
        var profile = model.ProfileEdit;
        foreach (var (width, height) in new[] { (1100d, 900d), (700d, 500d) })
        {
            Layout(width, height);
            if (width == 700)
                Control<Button>("OpenPeopleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(width, height);
            var node = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "root@example.com");
            PumpUntil(() =>
            {
                Layout(width, height);
                return tree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem;
            });
            var row = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(node);
            var collapse = (RoutedCommand)row.ContextMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "Collapse subtree")).Command;
            var expand = (RoutedCommand)row.ContextMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "Expand subtree")).Command;
            if (collapse.CanExecute("root@example.com", row)) collapse.Execute("root@example.com", row);
            expand.Execute("root@example.com", row);
            PumpUntil(() => model.PeopleTreeExpansion is { ExpandedBranches: > 0 });
            if (model.PeopleTreeExpansion is not { IsRunning: true, VisitedPeople: > 0 and < 513 })
                throw new InvalidOperationException("Query entry must start during genuinely unfinished compiled expansion with retained partial branches.");
            Control<TextBox>("SearchQuery").Text = "Search branch 255";
            Layout(width, height);
            var cancel = Control<Button>("CancelTreeExpansionButton");
            var status = Control<TextBlock>("TreeExpansionStatus");
            var results = Control<FrameworkElement>("SearchPanel");
            if (model.PeopleTreeExpansion is not { IsRunning: true } || model.PeopleNavigation.IsBrowsing ||
                !Presented(results) || !Presented(cancel) || !cancel.IsEnabled || !Presented(status) ||
                !status.Text.StartsWith("Expanding subtree:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Nonempty search must retain visible progress and actionable cancellation during unfinished expansion at {width}x{height}: " +
                    $"running={model.PeopleTreeExpansion?.IsRunning}; browsing={model.PeopleNavigation.IsBrowsing}; " +
                    $"results={Presented(results)}; cancel={Presented(cancel)}/{cancel.IsEnabled}; status={Presented(status)}; text={status.Text}.");
            var partial = model.PeopleNavigation.ExpandedPeople.ToArray();
            var progress = model.PeopleTreeExpansion;
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<ListBox>("SearchResults").Items.Cast<PersonSearchResult>()
                .Any(result => result.Person.Email == "branch255@example.com"));
            Layout(width, height);
            if (model.PeopleTreeExpansion != progress with { Status = PeopleTreeExpansionStatus.Cancelled } ||
                !partial.SequenceEqual(model.PeopleNavigation.ExpandedPeople) || !Presented(status) ||
                !status.Text.StartsWith("Expand subtree cancelled;", StringComparison.Ordinal) ||
                cancel.Visibility != Visibility.Collapsed || model.ProfileEdit != profile ||
                Control<TextBox>("OfficeNumber").Text != "  Search-time independent profile writing\r\n" ||
                Control<TextBlock>("Email").Text != "owner@example.com")
                throw new InvalidOperationException("Cancelling from search must stop expansion and retain its exact partial state and independent profile writing.");
            Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(width, height);
            row = tree.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem
                ?? throw new InvalidOperationException("Search-clear did not restore the current expansion-root row.");
            if (!partial.SequenceEqual(model.PeopleNavigation.ExpandedPeople) || !row.IsExpanded ||
                !expand.CanExecute("root@example.com", row) || width == 700 && !model.PeopleNavigation.OverlayOpen)
                throw new InvalidOperationException($"Search-clear must retain cancelled partial expansion, resumability and compact People: " +
                    $"width={width}; retained={partial.SequenceEqual(model.PeopleNavigation.ExpandedPeople)}; expanded={row.IsExpanded}; " +
                    $"row={(row.DataContext as ReportingNode)?.Person.Email ?? "detached"}; resume={expand.CanExecute("root@example.com", row)}; overlay={model.PeopleNavigation.OverlayOpen}.");
        }
        Control<Button>("CancelProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => model.CanSafelyClose);
        Layout(1100, 900);
        var expansionRoot = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "root@example.com");
        var expansionRow = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(expansionRoot);
        var collapseRoot = (RoutedCommand)expansionRow.ContextMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Collapse subtree")).Command;
        var expandRoot = (RoutedCommand)expansionRow.ContextMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Expand subtree")).Command;
        collapseRoot.Execute("root@example.com", expansionRow);
        VisualDescendants(tree).OfType<ScrollViewer>().First().ScrollToTop();
        Layout(1100, 900);
        var unrelated = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "unrelated@example.com");
        var unrelatedRow = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(unrelated);
        var unrelatedItems = new ObservedReportingItems(unrelated.Reports);
        unrelatedRow.ItemsSource = unrelatedItems;
        unrelatedRow.IsExpanded = true;
        Layout(1100, 900);
        unrelatedRow.IsExpanded = false;
        Layout(1100, 900);
        int? unrelatedReads = null;
        void ObserveBatch(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(WorkspaceViewModel.PeopleTreeExpansion) ||
                model.PeopleTreeExpansion is not { IsRunning: true, ExpandedBranches: > 0 }) return;
            window.Dispatcher.BeginInvoke(() =>
            {
                unrelatedReads = unrelatedItems.Reads;
                Control<Button>("CancelTreeExpansionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.Normal);
        }
        unrelatedItems.Reads = 0;
        model.PropertyChanged += ObserveBatch;
        try
        {
            expandRoot.Execute("root@example.com", expansionRow);
            PumpUntil(() => unrelatedReads.HasValue);
        }
        finally { model.PropertyChanged -= ObserveBatch; }
        if (unrelatedReads != 0 || !expansionRow.IsExpanded ||
            model.PeopleTreeExpansion is not { Status: PeopleTreeExpansionStatus.Cancelled })
            throw new InvalidOperationException($"One expansion batch enumerated {unrelatedReads} unrelated collapsed items instead of updating only its affected rows.");
        unrelatedRow.ItemsSource = unrelated.Reports;
        foreach (var cancelled in new[] { true, false })
        {
            Layout(1100, 900);
            var query = Control<TextBox>("SearchQuery");
            var results = Control<ListBox>("SearchResults");
            void Activate(string email)
            {
                PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == email));
                results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result => result.Person.Email == email);
                results.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                    new UnshownInputSource { RootVisual = surface }, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                PumpUntil(() => Control<TextBlock>("Email").Text == email && model.PeopleNavigation.IsBrowsing);
                Layout(1100, 900);
            }
            query.Text = "Unrelated owner";
            Activate("owner@example.com");
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
            var node = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "root@example.com");
            var row = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(node);
            var collapse = (RoutedCommand)row.ContextMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "Collapse subtree")).Command;
            var expand = (RoutedCommand)row.ContextMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "Expand subtree")).Command;
            collapse.Execute("root@example.com", row);
            expand.Execute("root@example.com", row);
            PumpUntil(() => model.PeopleTreeExpansion is { ExpandedBranches: > 0 });
            query.Text = "Search destination";
            // Advance the actual operation while search hides the tree, not its retained presentation snapshot.
            model.ContinuePeopleTreeExpansion();
            model.ContinuePeopleTreeExpansion();
            if (cancelled) Control<Button>("CancelTreeExpansionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var completed = model.PeopleNavigation.ExpandedPeople.ToArray();
            if (!completed.Contains("branch063@example.com") || model.PeopleTreeExpansion is not { VisitedPeople: < 513 })
                throw new InvalidOperationException($"Search navigation needs genuine partial expansion completed while the tree is hidden: cancelled={cancelled}; progress={model.PeopleTreeExpansion}; feedback={model.PeopleTreeActionFeedback}; count={completed.Length}.");
            Activate("destination@example.com");
            var lost = completed.Except(model.PeopleNavigation.ExpandedPeople, StringComparer.OrdinalIgnoreCase).ToArray();
            if (lost.Length != 0)
                throw new InvalidOperationException($"Accepted search navigation discarded completed {(cancelled ? "cancelled" : "running")} expansion: {string.Join(", ", lost)}.");
            if (model.PeopleTreeExpansion is not { Status: PeopleTreeExpansionStatus.Cancelled })
                throw new InvalidOperationException("Accepted search navigation left recursive expansion running behind the new destination.");
            var stopped = model.PeopleTreeExpansion;
            model.ContinuePeopleTreeExpansion();
            if (model.PeopleTreeExpansion != stopped)
                throw new InvalidOperationException("A later expansion batch continued after accepted search navigation.");
        }
        if (model.PeopleTreeExpansion is not { Status: PeopleTreeExpansionStatus.Cancelled } ||
            Control<FrameworkElement>("TreeExpansionPanel").Visibility != Visibility.Visible ||
            string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetItemStatus(tree)))
            throw new InvalidOperationException("Workspace replacement must begin with observable cancelled expansion from the old Workspace.");
        Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, Path.Combine(settings, "tree-replacement"));
        PumpUntil(() => model.Compatibility.WorkspacePath == Path.GetFullPath(Path.Combine(settings, "tree-replacement")) &&
            model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var replacementSettled = false;
        window.Dispatcher.BeginInvoke(() => replacementSettled = true, DispatcherPriority.ContextIdle);
        PumpUntil(() => replacementSettled);
        if (model.PeopleTreeExpansion is not null || model.PeopleTreeActionFeedback is not null ||
            Control<FrameworkElement>("TreeExpansionPanel").Visibility != Visibility.Collapsed ||
            Control<Button>("CancelTreeExpansionButton").Visibility != Visibility.Collapsed ||
            Control<TextBlock>("TreeExpansionStatus").Text != "" || System.Windows.Automation.AutomationProperties.GetItemStatus(tree) != "")
            throw new InvalidOperationException("A new Workspace retained the previous tree operation, feedback or accessible status.");
        Console.WriteLine("PASS: actual nonempty query during unfinished expansion, visible progress/cancel, exact partial-state retention and search-clear in wide/compact compiled People.");
    }

    private sealed class ObservedReportingItems(IReadOnlyList<ReportingNode> items) : System.Collections.IList
    {
        public int Reads { get; set; }
        public object? this[int index] { get { Reads++; return items[index]; } set => throw new NotSupportedException(); }
        public int Count => items.Count;
        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public System.Collections.IEnumerator GetEnumerator()
        {
            for (var index = 0; index < items.Count; index++) { Reads++; yield return items[index]; }
        }
        public bool Contains(object? value) => IndexOf(value) >= 0;
        public int IndexOf(object? value)
        {
            for (var index = 0; index < items.Count; index++)
                if (Equals(items[index], value)) return index;
            return -1;
        }
        public void CopyTo(Array array, int index) { foreach (var item in this) array.SetValue(item, index++); }
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }

    private static void VerifyReportsVisibility(Window window, string settings)
    {
        var folder = Path.Combine(settings, "reports-visibility");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"86000000-0000-4000-8000-000000000001",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                {"email":"v-morgan@example.com","displayName":"Morgan","managerEmail":null},
                {"email":"avery@example.com","displayName":"Avery","managerEmail":"v-morgan@example.com"},
                {"email":"archived@example.com","displayName":"Archived","managerEmail":null,"archived":true}]}
                """).Id))
                throw new InvalidOperationException("Reports visibility fixture failed: " + seed.Status);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var root = (FrameworkElement)window.Content;
        void Layout(double width, double height = 900)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Reports visibility checks must not show a window or create its native handle.");
        }
        bool Presented(FrameworkElement element)
        {
            for (DependencyObject? current = element; current is FrameworkElement parent && current != window;
                current = LogicalTreeHelper.GetParent(current))
                if (parent.Visibility != Visibility.Visible) return false;
            return true;
        }
        void Expect(bool visible, string stage)
        {
            if (Presented(Control<ListBox>("ReportsList")) != visible ||
                Presented(Control<TextBlock>("ReportCount")) != visible)
                throw new InvalidOperationException($"Reports list and count must be {(visible ? "shown" : "hidden")} during {stage}.");
        }
        void ExpectSummary(string? counts)
        {
            if (window.FindName("ReportingStructureSlot") is not FrameworkElement slot ||
                window.FindName("ReportingStructureValue") is not TextBox value ||
                window.FindName("ReportingStructureScope") is not TextBlock scope)
                throw new InvalidOperationException("The compiled profile is missing its ordinary Reporting structure fact.");
            if (Presented(slot) != (counts is not null) || value.Text != (counts ?? "") ||
                counts is not null && new TextBoxAutomationPeer(value).GetName() != $"Reporting structure: {counts}" ||
                scope.Text != "Known current reports, all levels")
                throw new InvalidOperationException("The compiled Reporting structure fact lost counts, accessible text, scope or eligibility.");
            if (counts is null) return;
            var facts = Control<Panel>("FactsGrid");
            if (facts.Children.IndexOf(slot) != 1 || !slot.IsDescendantOf(facts) ||
                value.TextWrapping != TextWrapping.Wrap || scope.TextWrapping != TextWrapping.Wrap ||
                value.ActualWidth <= 0 || value.ActualHeight <= 0 ||
                value.TranslatePoint(new Point(value.ActualWidth, 0), facts).X > facts.ActualWidth + 1)
                throw new InvalidOperationException("Reporting structure must wrap compactly immediately after the first Team-name fact.");
            if (UIElementAutomationPeer.CreatePeerForElement(value)?.GetPattern(PatternInterface.Text) is not ITextProvider text)
                throw new InvalidOperationException("The compiled reporting value must expose a real TextPattern provider for final-category readability.");
            var finalCategory = counts[(counts.LastIndexOf(", ", StringComparison.Ordinal) + 2)..];
            var finalRange = text.DocumentRange.FindText(finalCategory, false, false);
            if (text.DocumentRange.GetText(-1) != counts || finalRange is null || finalRange.GetText(-1) != finalCategory)
                throw new InvalidOperationException("The actual reporting TextPattern must retain the complete count and final category.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(value)!;
            if (peer.GetPattern(PatternInterface.Value) is not IValueProvider { IsReadOnly: true } readOnly ||
                !value.IsReadOnly || !value.Focusable || !KeyboardNavigation.GetIsTabStop(value) ||
                value.BorderThickness != new Thickness(0) || value.Padding != new Thickness(0) ||
                value.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled ||
                value.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                throw new InvalidOperationException("Reporting counts must remain borderless, selectable and read-only without inner scrolling.");
            var rejected = false;
            try { readOnly.SetValue("Attempted reporting edit"); }
            catch (InvalidOperationException) { rejected = true; }
            value.SelectAll();
            if (!rejected || value.Text != counts || value.SelectedText != counts)
                throw new InvalidOperationException("Selecting reporting counts must not permit editing their derived value.");
            value.Select(0, 0);
            var ordinary = new TextBlock { Text = counts, TextWrapping = TextWrapping.Wrap,
                FontFamily = value.FontFamily, FontSize = value.FontSize };
            ordinary.Measure(new Size(value.ActualWidth, double.PositiveInfinity));
            if (Math.Abs(value.ActualHeight - ordinary.DesiredSize.Height) > 1)
                throw new InvalidOperationException("Selectable reporting counts must retain the ordinary fact's content-sized line spacing.");
            var start = counts.Length - finalCategory.Length;
            var cursor = finalRange.Clone();
            cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, cursor, TextPatternRangeEndpoint.Start);
            for (var index = start; index < counts.Length; index++)
            {
                var character = cursor.Clone();
                if (character.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) != 1 ||
                    character.GetText(-1) != counts[index].ToString())
                    throw new InvalidOperationException($"The final reporting category character {index - start} is missing from the actual provider range.");
                var leading = value.GetRectFromCharacterIndex(index);
                var trailing = value.GetRectFromCharacterIndex(index, trailingEdge: true);
                if (leading.IsEmpty || trailing.IsEmpty ||
                    leading.Left < -1 || trailing.Right > value.ActualWidth + 1 ||
                    leading.Top < -1 || leading.Bottom > value.ActualHeight + 1 ||
                    trailing.Top < -1 || trailing.Bottom > value.ActualHeight + 1)
                    throw new InvalidOperationException($"The final reporting category character {index - start} has clipped actual text layout.");
                cursor.MoveEndpointByRange(TextPatternRangeEndpoint.Start, character, TextPatternRangeEndpoint.End);
            }
        }
        Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Local SQLite Workspace opened.");
        if (Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible)
            Invoke("OpenSettingsButton");
        if (Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Visible)
            throw new InvalidOperationException("Reports visibility must be checked in the real Person workspace, not behind Settings.");
        var tree = Control<TreeView>("PeopleList");
        var manager = tree.Items.Cast<ReportingNode>().Single();
        void Activate(string email)
        {
            var query = Control<TextBox>("SearchQuery");
            var results = Control<ListBox>("SearchResults");
            query.Text = email;
            PumpUntil(() => results.SelectedItem is PersonSearchResult result && result.Person.Email == email);
            query.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                new UnshownInputSource { RootVisual = root }, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpUntil(() => query.Text == "" && Control<TextBlock>("Email").Text == email);
            Layout(700);
        }
        Activate("archived@example.com");
        ExpectSummary(null);
        Activate("v-morgan@example.com");
        Invoke("OpenPeopleButton");
        Layout(700);
        if (Control<GroupBox>("PeoplePanel").Visibility != Visibility.Visible)
            throw new InvalidOperationException("Accepted current-after-archived search must allow opening People.");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, manager, TreeView.SelectedItemChangedEvent));
        Layout(700);
        if (Control<TextBlock>("Email").Text != "v-morgan@example.com" ||
            Control<GroupBox>("PeoplePanel").Visibility != Visibility.Visible ||
            !((WorkspaceViewModel)tree.DataContext).PeopleNavigation.OverlayOpen)
            throw new InvalidOperationException("A delayed tree selection echo for the already accepted Person must not close the compact People overlay.");
        Activate("v-morgan@example.com");
        if (Control<GroupBox>("PeoplePanel").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Explicit search activation of the same Person must still dismiss the compact overlay.");
        Invoke("OpenPeopleButton");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(manager, manager.Reports.Single(), TreeView.SelectedItemChangedEvent));
        if (Control<TextBlock>("Email").Text != "avery@example.com" ||
            Control<GroupBox>("PeoplePanel").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Intentional tree navigation to a different Person must still dismiss the overlay and open that Person.");
        void Select(ReportingNode person)
        {
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, person, TreeView.SelectedItemChangedEvent));
            if (Control<TextBlock>("Email").Text != person.Person.Email)
                throw new InvalidOperationException("The reports fixture did not navigate to its intended Person.");
            var expectedCategory = person.Person.Email == "v-morgan@example.com" ? "Vendor" : "Employee";
            if (Control<TextBlock>("PersonCategoryLabel").Text != expectedCategory ||
                new TextBlockAutomationPeer(Control<TextBlock>("PersonCategoryLabel")).GetName() != $"Person category: {expectedCategory}")
                throw new InvalidOperationException("Accepted report/manager navigation did not present that Person's category.");
        }
        Select(manager.Reports.Single());
        Layout(1100);
        ExpectSummary(null);
        Expect(false, "empty wide browsing");
        Layout(700);
        Expect(false, "empty compact browsing");
        Select(manager);
        Layout(700);
        ExpectSummary("1 total, 1 Employees, 0 Vendors");
        Expect(true, "populated compact browsing");
        var viewport = Control<ScrollViewer>("PersonScroll");
        var reports = Control<ListBox>("ReportsList");
        var before = reports.TransformToAncestor(root).TransformBounds(new Rect(reports.RenderSize));
        var extent = viewport.ExtentHeight;
        Invoke("OpenPeopleButton");
        Layout(700);
        Expect(true, "temporary compact People overlay");
        if (before != reports.TransformToAncestor(root).TransformBounds(new Rect(reports.RenderSize)) ||
            extent != viewport.ExtentHeight)
            throw new InvalidOperationException("Opening People must not reflow the underlying reports or profile content.");
        Invoke("ClosePeopleButton");
        Layout(1100);
        ExpectSummary("1 total, 1 Employees, 0 Vendors");
        Expect(false, "populated wide tree browsing");
        Control<TextBox>("SearchQuery").Text = "avery";
        Expect(true, "populated wide search");
        Control<Button>("ClearSearchButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Expect(false, "return to wide tree browsing");
        Control<TextBox>("SearchQuery").Text = "avery";
        reports.SelectedItem = reports.Items.Cast<PersonProfile>().Single();
        if (Control<TextBlock>("Email").Text != "avery@example.com" ||
            Control<TextBlock>("PersonCategoryLabel").Text != "Employee" ||
            new TextBlockAutomationPeer(Control<TextBlock>("PersonCategoryLabel")).GetName() != "Person category: Employee")
            throw new InvalidOperationException("Visible report selection must still navigate to its exact Person and category.");
        Expect(false, "empty search destination");
        ExpectSummary(null);
        Select(manager);
        using var provider = new SmokeSnapshotPeer(folder);
        var workflow = (WorkspaceViewModel)tree.DataContext;
        if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
        provider.Publish(source =>
        {
            var review = source.PreviewImport("""
                {"schemaVersion":1,"batchId":"86000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-02T17:00:00Z","people":[
                 {"email":"v-middle@example.com","displayName":"Vendor middle","managerEmail":"avery@example.com"},
                 {"email":"leaf@example.com","displayName":"Leaf","managerEmail":"v-middle@example.com"},
                 {"email":"v-solo@example.com","displayName":"Vendor root","managerEmail":null},
                 {"email":"v-only@example.com","displayName":"Vendor only","managerEmail":"v-solo@example.com"}]}
                """);
            return review.CanApply && source.ApplyImport(review.Id);
        });
        if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Synchronization.Error);
        PumpUntil(() => Control<TextBox>("ReportingStructureValue").Text == "3 total, 2 Employees, 1 Vendors");
        if (Control<ListBox>("ReportsList").Items.Count != 1 ||
            Control<TextBlock>("ReportCount").Text != "Imported reports: 1 (selected population, not a full-company count)")
            throw new InvalidOperationException("All-level reporting counts replaced the separate direct-only list or count.");
        foreach (var (width, height) in new[] { (700d, 500d), (1100d, 900d), (1920d, 900d) })
        {
            Layout(width, height);
            ExpectSummary("3 total, 2 Employees, 1 Vendors");
            if (width == 1100)
            {
                var value = Control<TextBox>("ReportingStructureValue");
                value.Text = "12000 total, 9000 Employees, 3000 Vendors";
                root.UpdateLayout();
                if (value.LineCount < 2)
                    throw new InvalidOperationException("The reporting text-layout fixture must exercise actual width-dependent wrapping.");
                ExpectSummary("12000 total, 9000 Employees, 3000 Vendors");
                value.Text = "3 total, 2 Employees, 1 Vendors";
                root.UpdateLayout();
                ExpectSummary("3 total, 2 Employees, 1 Vendors");
            }
            var team = Control<Panel>("TeamNameContext").Children.OfType<TextBlock>().Single(value =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(value) == "TeamNameLabel");
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            Control<ScrollViewer>("PersonScroll").ScrollToTop();
            root.UpdateLayout();
            if (!IsFirstScrollingTeamName(Bounds(team), Bounds(Control<Button>("Manager")),
                Bounds(Control<ScrollViewer>("PersonScroll")), 1))
                throw new InvalidOperationException("Reporting counts moved Team name out of the first scrolling position.");
        }
        Control<ListBox>("ReportsList").SelectedItem = Control<ListBox>("ReportsList").Items.Cast<PersonProfile>()
            .Single(person => person.Email == "avery@example.com");
        Layout(700, 500);
        ExpectSummary("2 total, 1 Employees, 1 Vendors");
        if (Control<TextBlock>("Email").Text != "avery@example.com" || Control<TextBlock>("PersonCategoryLabel").Text != "Employee")
            throw new InvalidOperationException("Direct-report navigation lost its exact destination category or reporting summary.");
        Activate("v-solo@example.com");
        ExpectSummary("1 total, 0 Employees, 1 Vendors");
        Activate("v-morgan@example.com");
        provider.Publish(source => source.EditPersonArchived("leaf@example.com", true) &&
            source.EditPersonArchived("v-middle@example.com", true) && source.EditPersonArchived("avery@example.com", true));
        if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Synchronization.Error);
        PumpUntil(() => Control<FrameworkElement>("ReportingStructureSlot").Visibility == Visibility.Collapsed);
        Layout(700);
        ExpectSummary(null);
        provider.Publish(source => source.EditPersonArchived("avery@example.com", false));
        if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Synchronization.Error);
        PumpUntil(() => Control<TextBox>("ReportingStructureValue").Text == "1 total, 1 Employees, 0 Vendors");
        Layout(1100);
        ExpectSummary("1 total, 1 Employees, 0 Vendors");
        Console.WriteLine("PASS: accepted-search tree echoes preserve the compact overlay; explicit same-Person activation and different-Person navigation still dismiss it; compact zero-inclusive reporting facts refresh/reappear across the size matrix, preserving Team-first geometry and direct-only report destinations.");
    }

    private static void VerifyFactRowSpacing(Window window)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var root = (FrameworkElement)window.Content;
        var facts = Control<Panel>("FactsGrid");
        var url = Control<TextBlock>("LinkedInProfileUrlValue");
        var longUrl = "https://www.linkedin.com/in/" + new string('a', 180);
        void SetUrl(string value)
        {
            Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<TextBox>("LinkedInProfileUrl").Text = value;
            Control<Button>("SaveProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<FrameworkElement>("AttributeEditors").Visibility == Visibility.Collapsed &&
                url.Text == (value.Length == 0 ? "—" : value));
        }
        if (url.Text != "—")
            throw new InvalidOperationException("The facts-spacing fixture must begin without a LinkedIn URL.");
        SetUrl(longUrl);
        foreach (var (width, columns) in new[] { (1920d, 4), (1100d, 3), (700d, 2) })
        {
            root.Measure(new Size(width, 900));
            root.Arrange(new Rect(0, 0, width, 900));
            root.UpdateLayout();
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Fact measurements must not show a window or create its native handle.");
            Control<ScrollViewer>("PersonScroll").ScrollToTop();
            root.UpdateLayout();
            var teamLabel = Control<Panel>("TeamNameContext").Children.OfType<TextBlock>()
                .Single(value => System.Windows.Automation.AutomationProperties.GetAutomationId(value) == "TeamNameLabel");
            Rect RootBounds(FrameworkElement element) =>
                element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            var labelBounds = RootBounds(teamLabel);
            var managerBounds = RootBounds(Control<Button>("Manager"));
            var viewportBounds = RootBounds(Control<ScrollViewer>("PersonScroll"));
            if (!IsFirstScrollingTeamName(labelBounds, managerBounds, viewportBounds, 1))
                throw new InvalidOperationException($"First Team-name fact rejects its real layout inset: " +
                    $"label={labelBounds}, Manager={managerBounds}, viewport={viewportBounds}.");
            var laterRow = labelBounds;
            laterRow.Offset(0, 54);
            var laterColumn = labelBounds;
            laterColumn.Offset(240, 0);
            if (IsFirstScrollingTeamName(laterRow, managerBounds, viewportBounds, 1) ||
                IsFirstScrollingTeamName(laterColumn, managerBounds, viewportBounds, 1))
                throw new InvalidOperationException("Team-name placement accepted a later row or column.");
            var slots = facts.Children.Cast<StackPanel>().ToArray();
            Rect Bounds(FrameworkElement element) =>
                element.TransformToAncestor(facts).TransformBounds(new Rect(element.RenderSize));
            var rows = slots.GroupBy(slot => Math.Round(Bounds(slot).Top, 2)).OrderBy(row => row.Key).ToArray();
            var expectedSlots = Control<FrameworkElement>("ReportingStructureSlot").Visibility == Visibility.Visible ? 8 : 7;
            if (slots.Length != expectedSlots || rows[0].Count() != columns ||
                Control<TextBlock>("TeamNameEmpty").Visibility != Visibility.Visible ||
                !Control<TextBlock>("TeamNameEmpty").IsDescendantOf(facts) ||
                url.Text != longUrl || url.ActualHeight <= url.FontSize * 2 ||
                slots.Any(slot => Bounds(slot).Left < 0 || Bounds(slot).Right > facts.ActualWidth + 1))
                throw new InvalidOperationException($"Facts lost their responsive columns, full wrapping or containment at {width} DIP: " +
                    $"slots={slots.Length}/{expectedSlots}, firstRow={rows[0].Count()}/{columns}, " +
                    $"summary={Control<FrameworkElement>("ReportingStructureSlot").Visibility}, urlHeight={url.ActualHeight}, " +
                    $"factsWidth={facts.ActualWidth}, rowSlots={string.Join("; ", slots.Select(slot => $"{slot.Name}={Bounds(slot)}"))}.");
            for (var index = 0; index < rows.Length - 1; index++)
            {
                var row = rows[index].ToArray();
                var advance = rows[index + 1].Key - rows[index].Key;
                var naturalHeight = row.Max(slot => slot.DesiredSize.Height);
                var contentBottom = row.Max(slot => slot.Children.Cast<FrameworkElement>()
                    .Where(child => child.Visibility == Visibility.Visible).Max(child => Bounds(child).Bottom));
                var gap = rows[index + 1].Key - contentBottom;
                Console.WriteLine($"FACT-ROWS width={width}; row={index}; advance={advance}; natural={naturalHeight}; gap={gap}; urlHeight={url.ActualHeight}.");
                if (Math.Abs(advance - naturalHeight) > 1 || gap < 0 || gap > 13)
                    throw new InvalidOperationException("Each facts row must fit its own content with at most a 12-DIP inter-row gap; a taller later row must not enlarge it.");
            }
        }
        SetUrl("");
        Console.WriteLine("PASS: actual compiled facts rows fit their own content with compact gaps and full long-URL wrapping at four/three/two columns.");
    }

    private sealed class UnshownInputSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() =>
            throw new InvalidOperationException("The non-UI routed-key fixture must not create a rendering target.");
    }

    private static void VerifyOwnedTeamPresentation(Window window, string folder)
    {
        var workspace = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        var owner = workspace.PeopleNavigation.SelectedEmail;
        if (owner != "avery@example.com")
            throw new InvalidOperationException("The owned-Team fixture must retain Avery as its exact owner.");
        var notes = (FrameworkElement)window.FindName("TeamNotes");
        T Control<T>(string name) where T : FrameworkElement => (T)notes.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Guid SelectedIdentity()
        {
            if (!Guid.TryParse(System.Windows.Automation.AutomationProperties.GetHelpText(
                    Control<TextBlock>("NoteHeading")), out var id) ||
                workspace.TeamNotesView.SelectedNote?.Id != id ||
                workspace.TeamNotesView.SelectedNote?.OwnerEmail != owner)
                throw new InvalidOperationException("Compiled Team creation did not expose its exact owner/note identity.");
            return id;
        }
        void Saved(Guid id, string title, string markdown)
        {
            try
            {
                PumpUntil(() =>
                {
                    var committed = workspace.GetTeamNotes(owner, includeArchived: true).SingleOrDefault(note => note.Id == id);
                    return committed is { Archived: false, NeedsReview: false } &&
                        committed.OwnerEmail == owner && committed.Title == title && committed.Markdown == markdown &&
                        workspace.GetRetainedNoteDraft(NoteScope.Team, id) is null &&
                        !workspace.HasUnsavedNoteDraft(NoteScope.Team, id) &&
                        !workspace.LocalSave.EditsPaused && !workspace.LocalSave.HasUnresolvedOutcome &&
                        workspace.LocalSave.OutstandingRequests == 0 &&
                        workspace.PeopleNavigation.SelectedEmail == owner &&
                        workspace.TeamNotesView.SelectedNote?.Id == id &&
                        workspace.TeamNotesView.SelectedNote?.OwnerEmail == owner &&
                        ((TextBlock)window.FindName("Email")).Text == owner &&
                        System.Windows.Automation.AutomationProperties.GetHelpText(Control<TextBlock>("NoteHeading")) == id.ToString("D") &&
                        Control<TextBox>("NoteTitle").Text == title &&
                        Control<TextBox>("MarkdownEditor").Text == markdown &&
                        Control<TextBox>("NoteTitle").IsEnabled && Control<TextBox>("MarkdownEditor").IsEnabled &&
                        Control<TabControl>("Views").Visibility == Visibility.Visible &&
                        Control<TabControl>("Views").SelectedItem == notes.FindName("EditTab");
                });
            }
            catch (TimeoutException error)
            {
                var committed = workspace.GetTeamNotes(owner, includeArchived: true).SingleOrDefault(note => note.Id == id);
                var retained = workspace.GetRetainedNoteDraft(NoteScope.Team, id);
                Console.WriteLine("OWNED-TEAM-SAVE-BOUNDARY " + JsonSerializer.Serialize(new
                {
                    owner, expectedId = id, committedId = committed?.Id, committedOwner = committed?.OwnerEmail,
                    titleMatches = committed?.Title == title, markdownMatches = committed?.Markdown == markdown,
                    archived = committed?.Archived, needsReview = committed?.NeedsReview,
                    retained = retained is not null, saving = retained?.IsSaving,
                    workspace.LocalSave.Phase, workspace.LocalSave.Outcome, workspace.LocalSave.OutstandingRequests,
                    selectedId = workspace.TeamNotesView.SelectedNote?.Id,
                    titleEnabled = Control<TextBox>("NoteTitle").IsEnabled,
                    editorEnabled = Control<TextBox>("MarkdownEditor").IsEnabled,
                    editing = Control<TabControl>("Views").SelectedItem == notes.FindName("EditTab"),
                    windowVisible = window.IsVisible
                }));
                throw new TimeoutException("Exact owned-Team commit, confirmation and compiled editor did not settle.", error);
            }
            var accepted = workspace.GetTeamNotes(owner, includeArchived: true).Single(note => note.Id == id);
            using var reopened = OpenSmokeWorkspace(folder);
            var durable = reopened.GetTeamNotes(owner, includeArchived: true).SingleOrDefault(note => note.Id == id);
            if (durable != accepted || durable is not { Archived: false, NeedsReview: false } ||
                durable.OwnerEmail != owner || durable.Title != title || durable.Markdown != markdown)
                throw new InvalidOperationException("Owned Team writing and its exact confirmed current-context note did not independently reopen.");
        }
        void Select(string title)
        {
            Control<Expander>("OtherNotes").IsExpanded = true;
            var list = Control<ListBox>("NotesList");
            list.SelectedItem = list.Items.Cast<OwnedNote>().Single(n => n.Title == title);
        }
        Invoke("NewButton");
        PumpUntil(() => Control<TextBox>("NoteTitle").Text == "New Team note");
        var continuingId = SelectedIdentity();
        if (Control<TabControl>("Views").SelectedItem != notes.FindName("EditTab"))
            throw new InvalidOperationException("New Team writing must start directly in Edit.");
        Control<TextBox>("NoteTitle").Text = "Continuing Team";
        Control<TextBox>("MarkdownEditor").Text = "Saved Team writing";
        Saved(continuingId, "Continuing Team", "Saved Team writing");
        Invoke("NewButton");
        PumpUntil(() => Control<TextBox>("NoteTitle").Text == "New Team note");
        var secondaryId = SelectedIdentity();
        Control<TextBox>("NoteTitle").Text = "Secondary Team";
        Saved(secondaryId, "Secondary Team", "");
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => ((TextBlock)window.FindName("StatusText")).Text == "Local SQLite Workspace opened.");
        var tree = (TreeView)window.FindName("PeopleList");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items[0], TreeView.SelectedItemChangedEvent));
        if (!Control<TextBlock>("OngoingStatus").Text.Contains("Choose", StringComparison.Ordinal) ||
            Control<Expander>("OtherNotes").IsExpanded || Control<TabControl>("Views").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("An ambiguous Team collection must not silently open replacement writing.");
        Select("Continuing Team");
        Invoke("DesignateButton");
        PumpUntil(() => Control<TextBlock>("NoteKind").Text == "Ongoing Team note");
        if (Control<Expander>("OtherNotes").IsExpanded || Control<TabControl>("Views").SelectedItem != notes.FindName("ReadTab"))
            throw new InvalidOperationException("Designated Team writing must open directly in Read without a permanent title list.");
        Select("Secondary Team");
        var emptyReading = Control<FlowDocumentScrollViewer>("Reading").Document;
        if (!string.IsNullOrWhiteSpace(new TextRange(emptyReading.ContentStart, emptyReading.ContentEnd).Text) ||
            Control<TabControl>("Views").SelectedItem != notes.FindName("ReadTab"))
            throw new InvalidOperationException("An empty secondary Team note must remain an exact, Read-first destination.");
        if (notes.FindName("EmptyBody") is not TextBlock { Visibility: Visibility.Visible } ||
            Control<TextBlock>("EmptyNotes").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("An exact empty Team document needs an Edit invitation, not an absent-collection state or blank viewer.");
        var settings = Path.Combine(Path.GetDirectoryName(folder)!, "workspace-settings.json");
        var preference = File.ReadAllBytes(settings);
        using (File.Open(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Invoke("DesignateButton");
            var kind = Control<TextBlock>("NoteKind");
            var kindId = System.Windows.Automation.AutomationProperties.GetAutomationId(kind);
            if (kindId != "TeamNoteKind")
                throw new InvalidOperationException("The compiled failed-designation replay lost its actual named note-kind control.");
            var kindReads = 0;
            string ReadKind()
            {
                // Replay unavailable discovery without changing the actual selected note or its presentation.
                if (++kindReads == 1)
                    System.Windows.Automation.AutomationProperties.SetAutomationId(kind, "UnavailableTeamNoteKind");
                try
                {
                    var observed = LogicalDescendants(notes).OfType<TextBlock>().SingleOrDefault(control =>
                        System.Windows.Automation.AutomationProperties.GetAutomationId(control) == "TeamNoteKind");
                    if (observed is null) throw new InvalidOperationException("Missing actual UI control: TeamNoteKind");
                    return UIElementAutomationPeer.CreatePeerForElement(observed).GetName();
                }
                finally { System.Windows.Automation.AutomationProperties.SetAutomationId(kind, kindId); }
            }
            var observedKind = ControlReadiness.AwaitFailedTeamDesignation(
                () => UIElementAutomationPeer.CreatePeerForElement(Control<TextBlock>("OngoingStatus")).GetName(),
                ReadKind, condition =>
                {
                    // Match native Await's missing-control policy and retain its one successful observation.
                    var ready = false;
                    PumpUntil(() =>
                    {
                        if (ready) return true;
                        try { return ready = condition(); }
                        catch (InvalidOperationException error) when (error.Message == "Missing actual UI control: TeamNoteKind")
                        {
                            return false;
                        }
                    });
                });
            if (observedKind != "Other Team note" || kindReads != 2 ||
                workspace.PeopleNavigation.SelectedEmail != owner || workspace.TeamNotesView.SelectedNote?.Id != secondaryId ||
                Control<TabControl>("Views").SelectedItem != notes.FindName("ReadTab") ||
                !File.ReadAllBytes(settings).SequenceEqual(preference))
                throw new InvalidOperationException("Failed Team designation must retain both the exact secondary note and durable choice.");
            Console.WriteLine("PASS: actual failed Team designation retains its exact Other-note reading after one unavailable named-control discovery, without replaying the action.");
        }
        Control<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
        if (SmokeNoteId(folder, owner, "Secondary Team", team: true) != secondaryId)
            throw new InvalidOperationException("The secondary Team note lost its exact created identity.");
        var saved = ReadSmokeTeamNote(folder, secondaryId);
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            Control<TextBox>("MarkdownEditor").Text = "Retained secondary Team draft";
            PumpUntil(() => Control<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal));
            Invoke("OngoingButton");
            PumpUntil(() => Control<TextBlock>("NoteHeading").Text == "Continuing Team");
            Select("Secondary Team");
            Control<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
            if (Control<TextBox>("MarkdownEditor").Text != "Retained secondary Team draft" ||
                Control<TextBox>("NoteTitle").Text != "Secondary Team" ||
                Control<TabControl>("Views").SelectedItem != notes.FindName("EditTab") ||
                ReadSmokeTeamNote(folder, secondaryId) != saved)
                throw new InvalidOperationException("Ongoing browsing must retain the exact failed Team editor and saved writing.");
        }
        if (!workspace.ReconcileLocalSaves()) throw new InvalidOperationException(workspace.Status);
        Invoke("RetryButton");
        Saved(secondaryId, "Secondary Team", "Retained secondary Team draft");
        Invoke("OngoingButton");
        var ongoingReading = Control<FlowDocumentScrollViewer>("Reading").Document;
        if (!new TextRange(ongoingReading.ContentStart, ongoingReading.ContentEnd).Text.Contains("Saved Team writing", StringComparison.Ordinal) ||
            Control<TabControl>("Views").SelectedItem != notes.FindName("ReadTab"))
            throw new InvalidOperationException("Explicit ongoing navigation did not return to saved Team reading after retry.");
        Invoke("DesignateButton");
        PumpUntil(() => Control<TextBlock>("OngoingStatus").Text.Length == 0 &&
            Control<TextBlock>("NoteKind").Text == "Ongoing Team note");
        Invoke("ArchiveButton");
        try { PumpUntil(() => Control<TextBlock>("OngoingStatus").Text.Contains("unavailable or archived", StringComparison.Ordinal)); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("Archived Team choice feedback did not settle: " +
                Control<TextBlock>("OngoingStatus").Text + "; " + Control<TextBlock>("NoteStatus").Text, error);
        }
        Console.WriteLine("PASS: actual compiled owned Team interactions retain ambiguous choice, exact empty notes, preference failures and protected drafts without showing a window.");
    }

    private static void VerifyOwnedNoteRevisions(Window window, string folder, NoteScope scope)
    {
        var workflow = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        var notes = (FrameworkElement)window.FindName(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes");
        T Control<T>(string name) where T : FrameworkElement => (T)notes.FindName(name);
        void Open(Guid id) => notes.GetType().GetMethod("OpenNote")!.Invoke(notes, [id]);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool Archived(Guid id) => workflow.GetOwnedNotesView(scope).Notes.Single(n => n.Id == id).Archived;
        void Ready() => PumpUntil(() => ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready");
        var owner = workflow.PeopleNavigation.SelectedEmail!;
        var prior = workflow.GetOwnedNotesView(scope).Notes.First(n => !n.Archived).Id;
        var id = scope == NoteScope.Person
            ? workflow.CreatePersonNote(owner, "Review secondary Person", "")!.Id
            : workflow.CreateTeamNote(owner, "Review secondary Team", "")!.Id;
        Open(id);
        if (Control<TextBlock>("EmptyBody").Visibility != Visibility.Visible ||
            Control<TextBlock>("EmptyNotes").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"Exact empty {scope} reading must invite Edit without substituting a collection-empty state.");
        using var provider = new SmokeSnapshotPeer(folder);
        if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
        // Replace externally edited note headers with a genuine next logical version at the public transport seam.
        var title = scope == NoteScope.Person ? "Review secondary Person" : "Review secondary Team";
        provider.Publish(source => scope == NoteScope.Person ? source.EditPersonNote(id, title, "Delivered secondary writing") :
            source.EditTeamNote(id, title, "Delivered secondary writing"));
        if (!workflow.SyncNow()) throw new InvalidOperationException(workflow.Status);
        notes.GetType().GetMethod("RefreshExternalWorkspace")!.Invoke(notes, null);
        var deliveredReading = Control<FlowDocumentScrollViewer>("Reading").Document;
        if (!new TextRange(deliveredReading.ContentStart, deliveredReading.ContentEnd).Text.Contains("Delivered secondary writing", StringComparison.Ordinal) ||
            workflow.GetOwnedNotesView(scope).SelectedNote?.Markdown != "Delivered secondary writing")
            throw new InvalidOperationException("Clean note reading did not accept the complete delivered snapshot.");
        Control<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
        if (Control<TextBox>("MarkdownEditor").Text != "Delivered secondary writing" ||
            Control<TextBox>("NoteTitle").Text != title || workflow.GetRetainedNoteDraft(scope, id) is not null)
            throw new InvalidOperationException("Actual Edit did not populate the same complete delivered title/body without retaining it.");
        Control<TextBox>("MarkdownEditor").Text = "Writing after snapshot delivery";
        if (workflow.GetRetainedNoteDraft(scope, id)?.Basis.Markdown != "Delivered secondary writing")
            throw new InvalidOperationException("Actual editing combined delivered presentation with a different immutable note base.");
        var flush = (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
        PumpUntil(() => flush.IsCompleted);
        if (!flush.GetAwaiter().GetResult() ||
            (scope == NoteScope.Person ? ReadSmokePersonNote(folder, id).Markdown : ReadSmokeTeamNote(folder, id).Markdown)
                != "Writing after snapshot delivery")
            throw new InvalidOperationException($"Clean {scope} editor retained a stale snapshot base: " +
                Control<TextBlock>("NoteStatus").Text);
        Ready();
        if (Control<TextBlock>("NoteStatus").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"Successful {scope} autosave left routine status in the note section.");
        PumpUntil(() => System.Windows.Automation.AutomationProperties.GetItemStatus(notes) == Control<TextBlock>("NoteStatus").Text);
        Console.WriteLine($"PASS: actual compiled clean {scope} editor accepts snapshot delivery before its next save.");

        Invoke("ArchiveButton");
        PumpUntil(() => Archived(id));
        Ready();
        Control<CheckBox>("IncludeArchived").IsChecked = true;
        Control<ListBox>("NotesList").SelectedItem = Control<ListBox>("NotesList").Items
            .Cast<OwnedNote>().Single(note => note.Id == prior);
        if (Control<CheckBox>("IncludeArchived").IsChecked != true ||
            !Control<ListBox>("NotesList").Items.Cast<OwnedNote>().Any(note => note.Id == id && note.Archived) ||
            workflow.GetOwnedNotesView(scope).SelectedNote?.Id != prior)
            throw new InvalidOperationException($"Selecting an active {scope} note reset the explicit archived-notes filter.");
        Console.WriteLine($"PASS: actual compiled {scope} active-note selection preserves the archived-notes filter and archived entries.");
        Open(id);
        var preferencePath = Path.Combine(Path.GetDirectoryName(folder)!, "workspace-settings.json");
        if (new WorkspaceSettings(preferencePath).GetLastView(folder)?.NoteId != prior)
            throw new InvalidOperationException("Opening archived writing unexpectedly replaced the prior valid view.");
        Invoke("ArchiveButton");
        PumpUntil(() => !Archived(id));
        Ready();
        if (new WorkspaceSettings(preferencePath).GetLastView(folder)?.NoteId != id)
            throw new InvalidOperationException($"Restored active {scope} note was not remembered as the exact destination.");
        var settings = new WorkspaceSettings(preferencePath);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        if (!settings.RestoreWorkspace(reopened) || reopened.GetOwnedNotesView(scope).SelectedNote?.Id != id ||
            reopened.GetOwnedNotesView(scope).IsEditing)
            throw new InvalidOperationException($"Restored {scope} writing did not reopen by exact identity in Read.");
        Console.WriteLine($"PASS: actual compiled restored {scope} note becomes the durable exact Read destination.");

        var navigation = workflow.PeopleNavigation;
        var offDispatcher = 0;
        PropertyChangedEventHandler observe = (_, change) =>
        {
            if (change.PropertyName != nameof(WorkspaceViewModel.PeopleNavigation)) return;
            var current = workflow.PeopleNavigation;
            if (!ReferenceEquals(Interlocked.Exchange(ref navigation, current), current) &&
                !window.Dispatcher.CheckAccess()) Interlocked.Increment(ref offDispatcher);
        };
        var query = workflow.PeopleNavigation.Query;
        var overlay = workflow.PeopleNavigation.OverlayOpen;
        workflow.PropertyChanged += observe;
        try
        {
            Invoke("DesignateButton");
            workflow.ChangePeopleQuery("Query during ongoing designation");
            workflow.ShowPeopleOverlay(true);
            PumpUntil(() => Control<TextBlock>("NoteKind").Text == $"Ongoing {scope} note");
            if (offDispatcher != 0 ||
                workflow.PeopleNavigation.Query != "Query during ongoing designation" ||
                !workflow.PeopleNavigation.OverlayOpen)
                throw new InvalidOperationException($"Ongoing {scope} designation mutated navigation off the dispatcher or lost concurrent query/overlay changes.");
        }
        finally
        {
            workflow.PropertyChanged -= observe;
            workflow.ChangePeopleQuery(query);
            workflow.ShowPeopleOverlay(overlay);
        }
        Console.WriteLine($"PASS: actual compiled {scope} designation keeps presentation mutations on the dispatcher and retains active query/overlay changes.");

        foreach (var failure in new[] { "locked", "corrupt" })
        {
            var before = File.ReadAllBytes(preferencePath);
            if (failure == "corrupt") File.WriteAllText(preferencePath, "{broken");
            try
            {
                using var held = failure == "locked"
                    ? File.Open(preferencePath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                Control<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
                var warning = (TextBlock)window.FindName("ViewPreferenceStatus");
                if (workflow.ViewPreferenceFeedback?.Contains("NOT remembered", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException($"The {failure} preference fixture did not reject the {scope} Read/Edit view write.");
                if (warning.Visibility != Visibility.Visible ||
                    !warning.Text.Contains("NOT remembered", StringComparison.Ordinal))
                    throw new InvalidOperationException($"The {scope} Read/Edit handler did not immediately present the {failure} view-preference failure.");
                if (failure == "locked" && !before.SequenceEqual(File.ReadAllBytes(preferencePath)) ||
                    failure == "corrupt" && File.ReadAllText(preferencePath) != "{broken")
                    throw new InvalidOperationException("The failed view preference write changed the previous settings bytes.");
            }
            finally
            {
                if (failure == "corrupt") File.WriteAllBytes(preferencePath, before);
            }
            Control<TabControl>("Views").SelectedItem = notes.FindName("ReadTab");
            if (workflow.ViewPreferenceFeedback is not null ||
                ((TextBlock)window.FindName("ViewPreferenceStatus")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException($"The {scope} Read/Edit retry did not immediately clear the recovered preference warning.");
        }
        Console.WriteLine($"PASS: actual compiled {scope} Read/Edit immediately presents locked/corrupt view-preference failures and clears successful retries.");
    }

    private static void VerifyLocalReconciliationAfterBlockedOpen(Window window, string folder)
    {
        var workflow = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        void Settled() => PumpUntil(() => ((Button)window.FindName("OpenWorkspaceButton")).IsEnabled);
        var selected = workflow.PeopleNavigation.SelectedEmail!;
        var destination = workflow.PeopleNavigation.LastValidView;
        var preferredName = workflow.GetRelationships(selected)!.Person.PreferredName;
        using (WorkspaceFixtures.LockLocalStore(folder))
            if (workflow.EditPreferredName(selected, "Local recovery retry fixture"))
                throw new InvalidOperationException("The locked SQLite write unexpectedly committed.");
        if (workflow.CanSafelyClose || !workflow.LocalSave.EditsPaused ||
            !workflow.LocalSaveError!.Contains("NOT saved", StringComparison.Ordinal))
            throw new InvalidOperationException("The compiled retry fixture did not retain a confirmed local noncommit.");
        var alternate = Path.Combine(Path.GetDirectoryName(folder)!, "blocked-workspace");
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, alternate);
        Settled();
        if (workflow.Compatibility.WorkspacePath != folder ||
            workflow.PeopleNavigation.SelectedEmail != selected ||
            workflow.PeopleNavigation.LastValidView != destination || Directory.Exists(alternate) ||
            !((TextBlock)window.FindName("NoticeSummary")).Text.Contains("NOT saved", StringComparison.Ordinal) ||
            workflow.CanSafelyClose)
            throw new InvalidOperationException("Blocked Workspace navigation changed the current presentation or created the destination.");
        var retry = LogicalDescendants((DependencyObject)window.FindName("SettingsPanel")).OfType<Button>()
            .Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "RetryWorkspace");
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settled();
        if (workflow.LocalSave.EditsPaused || workflow.Compatibility.WorkspacePath != folder || Directory.Exists(alternate))
            throw new InvalidOperationException("Retry after blocked Workspace opening did not reconcile current local storage: " + workflow.Status);
        workflow.DiscardPersonChanges(selected);
        if (!workflow.EditPreferredName(selected, preferredName))
            throw new InvalidOperationException(workflow.Status);
        Console.WriteLine("PASS: actual compiled blocked Workspace opening retains presentation and Retry reconciles current local saving without retrying the unopened destination.");
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
        }
    }

    private static void VerifyPersonNoteUpdateFlush(Window window, string folder)
    {
        var notes = (FrameworkElement)window.FindName("PersonNotes");
        T NoteControl<T>(string name) where T : FrameworkElement => (T)notes.FindName(name);
        var selected = NoteControl<ListBox>("NotesList").SelectedItem as OwnedNote ??
            throw new InvalidOperationException("The update-flush fixture has no exact selected Person note.");
        Task<bool> Flush() => (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
        void WaitForSave() => PumpUntil(() => NoteControl<TextBlock>("NoteStatus").Text.StartsWith(
            "Person note saved locally.", StringComparison.Ordinal) &&
            ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready");
        NoteControl<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
        NoteControl<TextBox>("MarkdownEditor").Text = "Durable draft before update";
        WaitForSave();
        var saved = ReadSmokePersonNote(folder, selected.Id);
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            NoteControl<TextBox>("MarkdownEditor").Text = "Retained failing update draft";
            PumpUntil(() => NoteControl<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal));
            var rejected = Flush();
            PumpUntil(() => rejected.IsCompleted);
            if (rejected.GetAwaiter().GetResult() || ReadSmokePersonNote(folder, selected.Id) != saved)
                throw new InvalidOperationException("Failed Person-note writing must block an update flush without changing durable bytes.");
            var retainedTitle = NoteControl<TextBox>("NoteTitle").Text;
            var retainedOwner = NoteControl<TextBlock>("Ownership").Text;
            ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (((FrameworkElement)window.FindName("SettingsPanel")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("The rejected-close regression must begin in Settings, with the note editor hidden.");
            var closingRejected = false;
            CancelEventHandler observeClose = (_, e) => closingRejected = e.Cancel;
            window.Closing += observeClose;
            try { window.Close(); }
            finally { window.Closing -= observeClose; }
            var closeSettled = false;
            window.Dispatcher.BeginInvoke(() => closeSettled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => closeSettled);
            if (!closingRejected ||
                NoteControl<TextBox>("MarkdownEditor").Text != "Retained failing update draft" ||
                NoteControl<TextBox>("NoteTitle").Text != retainedTitle ||
                NoteControl<TextBlock>("Ownership").Text != retainedOwner ||
                !NoteControl<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal) ||
                !((TextBlock)window.FindName("ExitRisk")).Text.Contains("may be lost", StringComparison.Ordinal) ||
                ReadSmokePersonNote(folder, selected.Id) != saved)
                throw new InvalidOperationException("Close must be rejected with its exact retained Person-note draft, failure text and unchanged saved writing.");
            if (((FrameworkElement)window.FindName("SettingsPanel")).Visibility != Visibility.Collapsed ||
                ((FrameworkElement)window.FindName("ExplorerPanel")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Close was correctly rejected and its draft/error retained, but Settings still hides the Person-note editor.");
            ((Button)window.FindName("CancelExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        var workflow = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        if (!workflow.ReconcileLocalSaves()) throw new InvalidOperationException(workflow.Status);
        NoteControl<Button>("RetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitForSave();
        NoteControl<TextBox>("MarkdownEditor").Text = "Final private writing flushed before update";
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var flushed = Flush();
        PumpUntil(() => flushed.IsCompleted);
        var accepted = flushed.GetAwaiter().GetResult();
        if (!accepted ||
            ReadSmokePersonNote(folder, selected.Id).Markdown != "Final private writing flushed before update")
            throw new InvalidOperationException("An update flush must save newer Person-note writing after a successful retry, even while Settings hides the editor. " +
                $"Accepted: {accepted}; note status: {NoteControl<TextBlock>("NoteStatus").Text}");
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE") is { } fixtureDirectory)
            VerifyPersonNoteUpdateHandoff(window, folder, fixtureDirectory);
    }

    private static void VerifyPersonNoteUpdateHandoff(Window window, string folder, string fixtureDirectory)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "trust.json")));
        var endpoint = new Uri(configuration.RootElement.GetProperty("manifestUrl").GetString()!);
        using var fixture = new SignedReleaseFixture();
        fixture.Key.ImportFromPem(File.ReadAllText(Path.Combine(fixtureDirectory, "ephemeral-private.pem")));
        fixture.Package = ControlledFullPackage("9999.0.0");
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "package.nupkg"), fixture.Package);
        // Server disposal must not wait for a continuation queued to this STA dispatcher.
        using var server = Task.Run(() => new LocalReleaseServer(
            () => fixture.Sign(fixture.Manifest("9999.0.0")), endpoint.Port)).GetAwaiter().GetResult();
        File.Delete(Path.Combine(fixtureDirectory, "handoff.json"));
        var notes = (FrameworkElement)window.FindName("PersonNotes");
        var editor = (TextBox)notes.FindName("MarkdownEditor");
        var noteStatus = (TextBlock)notes.FindName("NoteStatus");
        var updateStatus = (TextBlock)window.FindName("UpdateGuidance");
        void Invoke(Button button) =>
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
        var check = (Button)window.FindName("CheckForUpdatesButton");
        var settings = (Button)window.FindName("OpenSettingsButton");
        void SetSettings(bool visible)
        {
            var panel = (FrameworkElement)window.FindName("SettingsPanel");
            if ((panel.Visibility == Visibility.Visible) == visible) return;
            Invoke(settings);
            PumpUntil(() => (panel.Visibility == Visibility.Visible) == visible);
        }
        Invoke((Button)notes.FindName("NewButton"));
        PumpUntil(() => ((TextBox)notes.FindName("NoteTitle")).Text == "New Person note");
        ((TextBox)notes.FindName("NoteTitle")).Text = "Automatic update writing";
        editor.Text = "Durable draft before update handoff";
        PumpUntil(() => noteStatus.Text.StartsWith("Person note saved locally.", StringComparison.Ordinal) &&
            ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready");
        var noteId = SmokeNoteId(folder, "avery@example.com", "Automatic update writing");
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            editor.Text = "Retained failing update handoff draft";
            PumpUntil(() => noteStatus.Text.Contains("NOT saved", StringComparison.Ordinal));
            SetSettings(true);
            Invoke(check);
            PumpUntil(() => check.IsEnabled && updateStatus.Text.Contains("Automatic update paused", StringComparison.Ordinal));
            SetSettings(false);
        }
        var workflow = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        if (!workflow.ReconcileLocalSaves()) throw new InvalidOperationException(workflow.Status);
        Invoke((Button)notes.FindName("RetryButton"));
        PumpUntil(() => ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready" &&
            !noteStatus.Text.StartsWith("Pending", StringComparison.Ordinal));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldResponse = held.Task;
        try
        {
            SetSettings(true);
            Invoke(check);
            PumpUntil(() => updateStatus.Text.Contains("Checking", StringComparison.Ordinal));
            SetSettings(false);
            editor.Text = "Final private writing flushed before installer handoff";
            SetSettings(true);
        }
        finally { held.TrySetResult(); }
        PumpUntil(() => check.IsEnabled);
        if (!updateStatus.Text.Contains("Controlled installer process boundary", StringComparison.Ordinal))
            throw new InvalidOperationException("The update must reach its controlled installer boundary after a successful note retry and newer writing. " +
                $"Update: {updateStatus.Text}; note: {noteStatus.Text}");
        using var handoff = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "handoff.json")));
        var file = Path.Combine(folder, "workspace.sqlite");
        var expectedHash = handoff.RootElement.GetProperty("workspaceFiles").GetProperty("workspace.sqlite").GetString();
        if (ReadSmokePersonNote(folder, noteId).Markdown != "Final private writing flushed before installer handoff" ||
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) != expectedHash)
            throw new InvalidOperationException("The actual update command handed off before the final Person-note bytes were durable.");
        SetSettings(false);
    }

    private static void PumpUntil(Func<bool> complete)
    {
        using var waiting = HarnessRuntime.Measure(HarnessRuntime.Work.DispatcherWait);
        bool Observe()
        {
            using var observation = HarnessRuntime.Measure(HarnessRuntime.Work.Observation);
            return complete();
        }

        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) => { if (Observe() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!Observe()) throw new TimeoutException("The no-window profile refresh operation did not complete.");
    }

    private static void ClosePublishedCompiledWindow(Window window)
    {
        var workflow = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        if (workflow.IsWorkspaceCurrent && workflow.CanSafelyClose &&
            workflow.Compatibility.WorkspacePath is { } folder)
        {
            if (workflow.Synchronization.Folder is null &&
                !workflow.ConfigureSynchronizationFolder(SmokeSnapshotFolder(folder)))
                throw new InvalidOperationException(workflow.Status);
            var publishing = workflow.SynchronizeAsync();
            PumpUntil(() => publishing.IsCompleted);
            if (!publishing.GetAwaiter().GetResult() || !workflow.ExitStatus.CanClose)
                throw new InvalidOperationException("Ordinary compiled fixture could not publish before normal exit: " + workflow.ExitStatus.Message);
        }
        window.Close();
    }
}
