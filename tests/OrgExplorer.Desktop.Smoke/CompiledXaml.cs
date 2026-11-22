using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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

    private static int VerifyCompiledXaml(string executable, bool verifyProfileRefresh = false, bool verifyUpdateYield = false)
    {
        AssertDisplayInterop();
        if (verifyProfileRefresh || verifyUpdateYield) Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var heldPublisher = verifyProfileRefresh || verifyUpdateYield ? new HeldNotePublisher() : null;
        var assemblyPath = Path.ChangeExtension(executable, ".dll");
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
            resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var settings = Path.Combine(Path.GetTempPath(), "OrgExplorer-Xaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settings);
        Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
        AssemblyLoadContext.Default.Resolving += Resolve;
        var previousContext = SynchronizationContext.Current;
        Application? application = null;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (verifyProfileRefresh || verifyUpdateYield)
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var type = assembly.GetType("OrgExplorer.Desktop.MainWindow", throwOnError: true)!;
            Func<Window, string?> chooseFolder = _ => CompiledWorkspaceChoices.Dequeue();
            var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [ApplicationWorkflow.CreateFileBacked(filePublisher: heldPublisher), chooseFolder], null)!;
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
            if (verifyProfileRefresh)
            {
                VerifyProfileCopyControls(type, settings);
                var treeWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyTreePresentation(treeWindow, settings); }
                finally { treeWindow.Close(); }
                var unavailableNameWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyUnavailablePreferredNameEntry(unavailableNameWindow, settings); }
                finally { unavailableNameWindow.Close(); }
                var noteFailureWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyRetainedNoteCreationFailure(noteFailureWindow, settings); }
                finally { noteFailureWindow.Close(); }
                VerifySettingsFocus(window);
                // The empty 44-Person probe must not warm the separate cold browsing window.
                var emptyReadingWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyEmptyReadingScroll(emptyReadingWindow, settings); }
                finally { emptyReadingWindow.Close(); }
                var retainedWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyRejectedOverlayGeometry(retainedWindow, settings); }
                finally { retainedWindow.Close(); }
                var reportsWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyReportsVisibility(reportsWindow, settings); }
                finally { reportsWindow.Close(); }
                var teamNameWindow = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
                try { VerifyOwnedTeamNameProfile(teamNameWindow, Path.Combine(settings, "team-name-workspace")); }
                finally { teamNameWindow.Close(); }
                VerifyProfileSaveRefresh(window, settings, heldPublisher!);
                VerifyPeopleSearchRestoration(window, settings);
            }
            if (verifyUpdateYield) VerifyUpdateYield(window, settings, heldPublisher!);
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The compiled-XAML check must never show the window or create its native handle.");
            window.Close();
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
            PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
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
            var lifetime = System.Diagnostics.Stopwatch.StartNew();
            PumpUntil(() => feedback.Visibility == Visibility.Collapsed);
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
                        PumpUntil(() => feedback.Visibility == Visibility.Collapsed);
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
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(700, 500));
            root.Arrange(new Rect(0, 0, 700, 500));
            root.UpdateLayout();
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
                    PumpUntil(() => feedback.Visibility == Visibility.Collapsed);
                    copied = "Copy has not been invoked.";
                    ActivateKey(segment, key);
                    if (copied != "Platform" || feedback.Visibility != Visibility.Visible || feedback.Text != "Team name copied.")
                        throw new InvalidOperationException("Team-name activation must copy only that segment with the shared feedback.");
                }
            }
            PumpUntil(() => feedback.Visibility == Visibility.Collapsed);
            AssertWorkspaceBytes(folder, beforeCopy);
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
            PumpUntil(() => feedback.Visibility == Visibility.Collapsed);
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, avery, TreeView.SelectedItemChangedEvent));
            var notes = Control<FrameworkElement>("PersonNotes");
            var title = (TextBox)notes.FindName("NoteTitle");
            var noteStatus = (TextBlock)notes.FindName("NoteStatus");
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
            PumpUntil(() => feedback.Visibility == Visibility.Collapsed);
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
            window.Close();
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
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
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

    private static void VerifyProfileSaveRefresh(Window window, string settings, HeldNotePublisher publisher)
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
        var preferences = Path.Combine(settings, "workspace-settings.json");
        if (!new WorkspaceSettings(preferences).SetPaneProportions(folder, new(25, 92)))
            throw new InvalidOperationException("Could not seed the existing version-1 inner-ratio preference.");
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
        if (Control<TextBlock>("WorkspacePath").Text != folder)
            throw new InvalidOperationException("Settings did not display the opened Workspace's actual full path.");
        if (window.FindName("OperationalNotice") is not FrameworkElement { Visibility: Visibility.Collapsed })
            throw new InvalidOperationException("Healthy reading must not retain permanent Ready, validated, saved or inactive recovery furniture.");
        if (System.Windows.Automation.AutomationProperties.GetItemStatus(window) != "Ready" ||
            System.Windows.Automation.AutomationProperties.GetItemStatus(Control<TextBlock>("WorkspaceName")) !=
                "Current validated local Workspace.")
            throw new InvalidOperationException("Quiet reading lost its actual accessible activity or Workspace validation state.");
        VerifyRejectedPickerChoice(window, folder, preferences);
        VerifyImportPreviewKey(window, folder);
        Invoke("OpenSettingsButton");
        if (Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Collapsed ||
            Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Collapsed ||
            Control<TextBlock>("WorkspaceName").Text != "workspace" ||
            System.Windows.Automation.AutomationProperties.GetHelpText(Control<TextBlock>("WorkspaceName")) != folder)
            throw new InvalidOperationException("Settings must use the bounded main work area without navigating or opening Import.");
        Invoke("OpenSettingsButton");
        var tree = Control<TreeView>("PeopleList");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items[0], TreeView.SelectedItemChangedEvent));
        VerifyPreferredNameEntry(window, folder, publisher);
        VerifyPhotoViewportLayout(window);
        VerifyHeaderTextAndReadingOrder(window);
        VerifyCompactIdentityStates(window, folder);
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
        var notes = Control<FrameworkElement>("PersonNotes");
        T NoteControl<T>(string name) where T : FrameworkElement => (T)notes.FindName(name);
        NoteControl<ListBox>("NotesList").SelectedIndex = 0;
        var noteFile = Directory.GetFiles(Path.Combine(folder, "notes"), "*.md").Single();
        foreach (var dirtyNote in new[] { false, true })
        {
            Invoke("EditProfileButton");
            Control<TextBox>("DirectoryNameEditor").Text = dirtyNote ? "Second local profile" : "First local profile";
            if (dirtyNote)
            {
                NoteControl<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
                NoteControl<TextBox>("NoteTitle").Text = "";
                NoteControl<TextBox>("MarkdownEditor").Text = "# Retained unsaved note";
                PumpUntil(() => NoteControl<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal));
            }

            var status = Control<TextBlock>("StatusText");
            var property = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            var completed = false;
            string? observed = null;
            EventHandler saved = (_, _) =>
            {
                if (!status.Text.StartsWith("Profile saved locally.", StringComparison.Ordinal)) return;
                observed = NoteControl<TextBox>("MarkdownEditor").Text;
                completed = true;
            };
            property.AddValueChanged(status, saved);
            var delivered = File.ReadAllText(noteFile) + "\n# Delivered during profile Save";
            try
            {
                File.WriteAllText(noteFile, delivered);
                Invoke("SaveProfileButton");
                PumpUntil(() => completed);
                var expected = dirtyNote ? "# Retained unsaved note" : "# Saved note\n# Delivered during profile Save";
                if (observed != expected)
                    throw new InvalidOperationException($"Profile Save displayed stale or discarded Person-note writing: '{observed}'.");
                if (File.ReadAllText(noteFile) != delivered)
                    throw new InvalidOperationException("Profile Save rewrote delivered Person-note bytes.");
                if (!dirtyNote && new[] { notes, Control<FrameworkElement>("TeamNotes") }.Any(control =>
                        ((TextBlock)control.FindName("NoteStatus")).Visibility != Visibility.Collapsed))
                    throw new InvalidOperationException("Successful profile save/refresh left routine status in healthy Person/Team reading.");
                if (dirtyNote && (window.FindName("OperationalNotice") is not FrameworkElement { Visibility: Visibility.Visible } ||
                    !Control<TextBlock>("NoticeSummary").Text.Contains("NOT saved", StringComparison.Ordinal) ||
                    NoteControl<TextBlock>("NoteStatus").Visibility != Visibility.Visible))
                    throw new InvalidOperationException("Unrelated profile success concealed the independent failed Person-note writing.");
            }
            finally { property.RemoveValueChanged(status, saved); }
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
                    !File.ReadAllBytes(preferences).SequenceEqual(previousPreference) || File.ReadAllText(noteFile) != delivered)
                    throw new InvalidOperationException("Cancelling the picker changed retained writing, status, selection or the startup preference.");
                var blockedFolder = Path.Combine(settings, "blocked-by-note");
                ChooseCompiledWorkspace(window, blockedFolder);
                if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Collapsed ||
                    Control<TextBlock>("Email").Text != "avery@example.com" ||
                    NoteControl<TextBox>("MarkdownEditor").Text != "# Retained unsaved note" ||
                    !status.Text.Contains("Person note", StringComparison.Ordinal) || Directory.Exists(blockedFolder) ||
                    !File.ReadAllBytes(preferences).SequenceEqual(previousPreference) || File.ReadAllText(noteFile) != delivered)
                    throw new InvalidOperationException("Picker acceptance bypassed the failed note or stranded its exact writing behind Settings.");
                Invoke("OpenSettingsButton");
                Invoke("OpenImportButton");
                if (Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Collapsed ||
                    Control<TextBlock>("Email").Text != "avery@example.com" ||
                    NoteControl<TextBox>("MarkdownEditor").Text != "# Retained unsaved note" ||
                    !Control<TextBlock>("StatusText").Text.Contains("Person note", StringComparison.Ordinal) ||
                    File.ReadAllText(noteFile) != delivered)
                    throw new InvalidOperationException("Settings Import bypassed the failed independent Person-note draft.");
                NoteControl<Button>("DiscardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready");
            }
        }
        VerifyOwnedTeamPresentation(window, folder);
        VerifyMaintenanceRetryAfterBlockedOpen(window, folder);
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
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
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
            ((TextBlock)window.FindName("StatusText")).Text == "Workspace opened.");
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
            if (workflow.GetRelationships("avery@example.com")!.Person.PreferredName is not null ||
                File.ReadAllText(WorkspaceFixtures.IndexedDocuments(folder)["people:avery@example.com"])
                    .Contains("Unconfirmed", StringComparison.Ordinal))
                throw new InvalidOperationException("Typing or generic draining published an unconfirmed Preferred name.");
        }
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
            editor.Text = cancel ? "Admitted before Cancel" : "First admitted value";
            try
            {
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!publisher.Entered.Wait(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Preferred-name Save did not reach the held real-file publication.");
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
                }
                else editor.Text = "Newer retained value";
            }
            finally { publisher.Release.Set(); }
            Settled();
            var elapsedAfterSave = System.Diagnostics.Stopwatch.StartNew();
            PumpUntil(() => elapsedAfterSave.ElapsedMilliseconds >= 650);
            if (workflow.GetRelationships("avery@example.com")!.Person.PreferredName !=
                (cancel ? "Admitted before Cancel" : "First admitted value"))
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
        Invoke("EditPreferredNameButton");
        using (var provider = ApplicationWorkflow.CreateFileBacked())
        {
            if (!provider.OpenWorkspace(folder) || !provider.EditPreferredName("avery@example.com", "Delivered during entry"))
                throw new InvalidOperationException(provider.Status);
        }
        if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
        PumpUntil(() => Control<TextBlock>("ProfileHeading").Text == "Delivered during entry");
        editor.Text = "Retained stale entry";
        foreach (var attempt in new[] { 1, 2 })
        {
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settled();
            if (panel.Visibility != Visibility.Visible || editor.Text != "Retained stale entry" ||
                !details.Text.Contains("changed externally", StringComparison.Ordinal) ||
                workflow.GetRelationships("avery@example.com")!.Person.PreferredName != "Delivered during entry")
                throw new InvalidOperationException($"Explicit stale Preferred-name attempt {attempt} lost its original base/input or overwrote delivery.");
        }
        Invoke("CancelPreferredNameButton");
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
        Console.WriteLine("PASS: admitted Preferred-name I/O survives Cancel without blocking dismissal; newer input waits for another explicit Save; close remains blocked while publication is held.");
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
            PumpUntil(() => Control<TextBlock>("Email").Text == state.Email);
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
                using (var provider = ApplicationWorkflow.CreateFileBacked())
                    if (!provider.OpenWorkspace(folder) ||
                        !provider.EditPreferredName(state.Email, "Delivered Vendor") ||
                        !provider.EditPersonArchived(state.Email, true))
                        throw new InvalidOperationException("Vendor identity delivery failed: " + provider.Status);
                PumpUntil(() => heading.Text == "Delivered Vendor" && marker.Visibility == Visibility.Visible);
                if (Control<TextBlock>("PersonCategoryLabel").Text != "Vendor")
                    failures.Add("Accepted external name/archival delivery left a stale selected category.");
                using (var provider = ApplicationWorkflow.CreateFileBacked())
                    if (!provider.OpenWorkspace(folder) || !provider.EditPersonArchived(state.Email, false) ||
                        !provider.EditPreferredName(state.Email, null))
                        throw new InvalidOperationException("Vendor reactivation delivery failed: " + provider.Status);
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

    private static void VerifyRejectedOverlayGeometry(Window window, string settings)
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
        var savedFile = Directory.GetFiles(Path.Combine(folder, "notes"), "*.md").Single();
        var savedBytes = File.ReadAllBytes(savedFile);
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
            Measure("rejected");
            if (before != Bounds(Control<ScrollViewer>("PersonScroll")) ||
                Control<TextBlock>("Email").Text != "avery@example.com" || Note<TextBox>("NoteTitle").Text != "" ||
                Control<GroupBox>("PeoplePanel").Visibility != Visibility.Visible ||
                Note<TabControl>("Views").SelectedItem != notes.FindName("EditTab") || workflow.CanSafelyClose ||
                !Control<TextBlock>("StatusText").Text.Contains("Retry or discard", StringComparison.Ordinal) ||
                !Note<TextBlock>("NoteStatus").Text.Contains("Retry or discard", StringComparison.Ordinal) ||
                !File.ReadAllBytes(savedFile).SequenceEqual(savedBytes))
                throw new InvalidOperationException("Measured rejected overlay navigation changed geometry, owner, retained editor or panel.");
            var note = System.Windows.Automation.AutomationProperties.GetHelpText(Note<TextBlock>("NoteHeading"));
            var draft = Note<TextBox>("MarkdownEditor").Text;
            Measure("before-query");
            Control<TextBox>("SearchQuery").Text = "casey@example";
            var results = Control<ListBox>("SearchResults");
            PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == "casey@example.com"));
            Layout();
            Measure("after-query-before-activate");
            void Enter(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                new UnshownInputSource { RootVisual = root }, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            FocusManager.SetFocusedElement(window, Control<TextBox>("SearchQuery"));
            Enter(Control<TextBox>("SearchQuery"));
            Layout();
            Measure("query-enter-rejected");
            results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result => result.Person.Email == "casey@example.com");
            var highlighted = results.SelectedItem;
            PumpUntil(() => Control<TextBlock>("StatusText").Text.Contains("Retry or discard", StringComparison.Ordinal));
            Layout();
            Measure("rejected-search");
            if (before != Bounds(Control<ScrollViewer>("PersonScroll")) ||
                Control<TextBlock>("Email").Text != "avery@example.com" || Note<TextBox>("NoteTitle").Text != "" ||
                Note<TextBox>("MarkdownEditor").Text != draft ||
                System.Windows.Automation.AutomationProperties.GetHelpText(Note<TextBlock>("NoteHeading")) != note ||
                Control<TextBox>("SearchQuery").Text != "casey@example" || results.SelectedItem != highlighted ||
                Control<GroupBox>("PeoplePanel").Visibility != Visibility.Visible ||
                Note<TabControl>("Views").SelectedItem != notes.FindName("EditTab") || workflow.CanSafelyClose ||
                !File.ReadAllBytes(savedFile).SequenceEqual(savedBytes))
                throw new InvalidOperationException("Measured rejected search changed absolute viewport or retained query, owner, note, writing or panel.");
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
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened." &&
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
        if (workflow.People.Count != 44 || workflow.GetPersonNotes("avery@example.com").Count != 0 ||
            workflow.GetTeamNotes("avery@example.com").Count != 0 || scroll.ViewportHeight <= 0 ||
            provider.VerticallyScrollable || scroll.VerticalOffset != 0)
            throw new InvalidOperationException("The full wide empty-reading fixture did not establish the non-scrollable precondition.");
        ControlReadiness.ScrollToTopIfScrollable(() => provider.VerticallyScrollable,
            () => provider.SetScrollPercent(System.Windows.Automation.ScrollPattern.NoScroll, 0));
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Empty-reading scroll proof must remain invisible without a native main-window handle.");
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
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened." &&
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
        if (queryResults.SelectedItem is not null || queryField.Text != "unmatched-query" || Control<TextBlock>("Email").Text != "")
            throw new InvalidOperationException("No-result query Enter must not activate a stale Person or dismiss search.");
        queryField.Text = "orchid";
        PumpUntil(() => queryResults.Items.Cast<PersonSearchResult>().Any(r => r.Matches.Any(m => m.NoteId == noteId)));
        if (Control<TextBlock>("Email").Text != "" ||
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
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
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
            using (File.Open(Path.Combine(folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ((Button)notes.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => workflow.LocalSaveError is not null && status.Text.Contains("NOT saved", StringComparison.Ordinal));
            }
            var retained = workflow.LocalSaveError!;
            if (Count() != originalCount || workflow.CanSafelyClose)
                throw new InvalidOperationException("Failed creation must retain its error without creating replacement writing.");
            using (var provider = ApplicationWorkflow.CreateFileBacked())
                if (!provider.OpenWorkspace(folder) ||
                    !provider.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "Delivered " + scope))
                    throw new InvalidOperationException("Unrelated provider delivery failed: " + provider.Status);
            if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
            if (workflow.LocalSaveError != retained || workflow.CanSafelyClose ||
                status.Visibility != Visibility.Visible || status.Text != retained ||
                System.Windows.Automation.AutomationProperties.GetItemStatus(notes) != retained)
                throw new InvalidOperationException($"Unrelated successful refresh must not replace the unresolved {scope} creation error with success feedback: {status.Text}");
            var selector = Path.Combine(folder, "workspace.json");
            var selectorBytes = File.ReadAllBytes(selector);
            try
            {
                File.WriteAllText(selector, "{}");
                if (workflow.RefreshWorkspace())
                    throw new InvalidOperationException("The invalid delivery fixture was unexpectedly accepted.");
                settled = false;
                window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
                PumpUntil(() => settled);
                if (!status.Text.Contains("STALE", StringComparison.Ordinal) || !status.Text.Contains(retained, StringComparison.Ordinal))
                    throw new InvalidOperationException("A distinct current failure must remain visible alongside the retained creation error.");
            }
            finally { File.WriteAllBytes(selector, selectorBytes); }
            if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
            workflow.DiscardUnsavedChanges();
            ((Button)notes.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Count() == originalCount + 1 && status.Visibility == Visibility.Collapsed &&
                workflow.LocalSaveError is null);
        }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Note failure checks must not show a window or create its native handle.");
        Console.WriteLine("PASS: Person/Team creation failures remain visible and accessible through successful external refresh, and healthy feedback becomes quiet after deliberate resolution.");
    }

    private static void VerifyUnavailablePreferredNameEntry(Window window, string settings)
    {
        var folder = Path.Combine(settings, "unavailable-preferred-name");
        void Seed(string path, string email, string name)
        {
            using var source = ApplicationWorkflow.CreateFileBacked();
            if (!source.OpenWorkspace(path) || !source.ApplyImport(source.PreviewImport($$"""
                {"schemaVersion":1,"batchId":"88000000-0000-4000-8000-000000000001",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"{{email}}","displayName":"Directory name","preferredName":"{{name}}","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Unavailable-name fixture failed: " + source.Status);
        }
        Seed(folder, "avery@example.com", "Saved name");
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
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
        void Deliver(string email, string name)
        {
            var delivery = Path.Combine(settings, "preferred-delivery-" + Guid.NewGuid().ToString("N"));
            Seed(delivery, email, name);
            foreach (var file in Directory.GetFiles(delivery, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != ".workspace.lock")
                .OrderBy(path => Path.GetFileName(path) == "workspace.json"))
            {
                var target = Path.Combine(folder, Path.GetRelativePath(delivery, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
            var settled = false;
            window.Dispatcher.BeginInvoke(() => settled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => settled);
        }
        Dictionary<string, byte[]> Bytes() => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != ".workspace.lock").ToDictionary(path => path, File.ReadAllBytes);
        void Unchanged(Dictionary<string, byte[]> before)
        {
            var after = Bytes();
            if (after.Count != before.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var bytes) ||
                !pair.Value.SequenceEqual(bytes)))
                throw new InvalidOperationException("Preferred-name retention/cancellation rewrote delivered Workspace bytes.");
        }
        SelectOwner();
        Invoke("EditPreferredNameButton");
        Control<TextBox>("PreferredName").Text = "Retained local entry";
        Deliver("casey@example.com", "Casey");
        var delivered = Bytes();
        if (Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Visible ||
            Control<TextBox>("PreferredName").Text != "Retained local entry" ||
            Control<TextBlock>("Email").Text != "avery@example.com")
            throw new InvalidOperationException("Valid owner removal must retain the unconfirmed Preferred-name entry and its owner.");
        if (Control<Button>("SavePreferredNameButton").IsEnabled || !Control<Button>("CancelPreferredNameButton").IsEnabled ||
            !Control<TextBlock>("PreferredNameError").Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            !System.Windows.Automation.AutomationProperties.GetItemStatus(window).Contains("Pending", StringComparison.Ordinal))
            throw new InvalidOperationException("Unavailable-owner feedback must retain unsafe state, disable Save and leave Cancel available.");
        Invoke("CancelPreferredNameButton");
        if (Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Collapsed ||
            Control<TextBlock>("Email").Text != "")
            throw new InvalidOperationException("Deliberate cancellation must release the unavailable owner and close only the entry.");
        Unchanged(delivered);
        Deliver("avery@example.com", "Saved name");
        SelectOwner();
        Invoke("EditPreferredNameButton");
        Control<TextBox>("PreferredName").Text = "Still based on saved name";
        Deliver("casey@example.com", "Casey");
        Deliver("avery@example.com", "Changed externally");
        delivered = Bytes();
        Invoke("SavePreferredNameButton");
        PumpUntil(() => Control<TextBlock>("PreferredNameError").Visibility == Visibility.Visible);
        if (Control<TextBox>("PreferredName").Text != "Still based on saved name" ||
            !System.Windows.Automation.AutomationProperties.GetHelpText(Control<TextBlock>("PreferredNameError"))
                .Contains("changed externally", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Owner disappearance/return must not replace the entry's original save base.");
        Unchanged(delivered);
        Invoke("CancelPreferredNameButton");
        Unchanged(delivered);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Unavailable-name checks must not show a window or create its native handle.");
        Console.WriteLine("PASS: valid owner removal retains Preferred-name input/owner/unsafe state, deliberate Cancel is nonmutating and a returning owner cannot reset the save base.");
    }

    private static void VerifyTreePresentation(Window window, string settings)
    {
        var folder = Path.Combine(settings, "tree-presentation");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"87000000-0000-4000-8000-000000000001",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
                 {"email":"alex@example.com","displayName":"Alex","managerEmail":"avery@example.com"},
                 {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
                 {"email":"jordan@example.com","displayName":"Jordan","managerEmail":"casey@example.com"}]}
                """).Id))
                throw new InvalidOperationException("Tree presentation fixture failed: " + seed.Status);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var root = (FrameworkElement)window.Content;
        void Layout()
        {
            root.Measure(new Size(1100, 900));
            root.Arrange(new Rect(0, 0, 1100, 900));
            root.UpdateLayout();
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Tree measurements must not show a window or create its native handle.");
        }
        Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
        if (Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible)
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        var tree = Control<TreeView>("PeopleList");
        var manager = tree.Items.Cast<ReportingNode>().Single(node => node.Person.Email == "morgan@example.com");
        var container = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manager);
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
        if (Control<TextBlock>("Email").Text != "avery@example.com" || siblingContainer.IsExpanded || !child.IsSelected)
            throw new InvalidOperationException("Rejected navigation must retain the owner and must not expand the rejected Person.");
        ((Button)notes.FindName("DiscardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((WorkspaceViewModel)tree.DataContext).CanSafelyClose);
        Console.WriteLine("PASS: real tree selection expands only immediate reports, retains manual collapse/other branches, reveals accepted search destinations and leaves rejected branches unchanged.");
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
        void Layout(double width)
        {
            root.Measure(new Size(width, 900));
            root.Arrange(new Rect(0, 0, width, 900));
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
        Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
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
        Expect(false, "empty wide browsing");
        Layout(700);
        Expect(false, "empty compact browsing");
        Select(manager);
        Layout(700);
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
        Console.WriteLine("PASS: accepted-search tree echoes preserve the compact overlay; explicit same-Person activation and different-Person navigation still dismiss it; report visibility and profile geometry remain intact.");
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
            if (slots.Length != 7 || rows[0].Count() != columns ||
                Control<TextBlock>("TeamNameEmpty").Visibility != Visibility.Visible ||
                !Control<TextBlock>("TeamNameEmpty").IsDescendantOf(facts) ||
                url.Text != longUrl || url.ActualHeight <= url.FontSize * 2 ||
                slots.Any(slot => Bounds(slot).Left < 0 || Bounds(slot).Right > facts.ActualWidth + 1))
                throw new InvalidOperationException($"Facts lost their responsive columns, full wrapping or containment at {width} DIP.");
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
        var notes = (FrameworkElement)window.FindName("TeamNotes");
        T Control<T>(string name) where T : FrameworkElement => (T)notes.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Saved()
        {
            try
            {
                PumpUntil(() => Control<TextBlock>("NoteStatus").Text.StartsWith(
                    "Team note saved locally and confirmed for current context.", StringComparison.Ordinal) &&
                    ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready");
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"Team save did not settle: note={Control<TextBlock>("NoteStatus").Text}; " +
                    $"activity={((TextBlock)window.FindName("WorkflowActivity")).Text}; " +
                    $"status={((TextBlock)window.FindName("StatusText")).Text}; title={Control<TextBox>("NoteTitle").Text}.", error);
            }
        }
        void Select(string title)
        {
            Control<Expander>("OtherNotes").IsExpanded = true;
            var list = Control<ListBox>("NotesList");
            list.SelectedItem = list.Items.Cast<OwnedNote>().Single(n => n.Title == title);
        }
        Invoke("NewButton");
        PumpUntil(() => Control<TextBox>("NoteTitle").Text == "New Team note");
        if (Control<TabControl>("Views").SelectedItem != notes.FindName("EditTab"))
            throw new InvalidOperationException("New Team writing must start directly in Edit.");
        Control<TextBox>("NoteTitle").Text = "Continuing Team";
        Control<TextBox>("MarkdownEditor").Text = "Saved Team writing";
        Saved();
        Invoke("NewButton");
        PumpUntil(() => Control<TextBox>("NoteTitle").Text == "New Team note");
        Control<TextBox>("NoteTitle").Text = "Secondary Team";
        Saved();
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => ((TextBlock)window.FindName("StatusText")).Text == "Workspace opened.");
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
        if (Control<TextBox>("MarkdownEditor").Text != "" || Control<TabControl>("Views").SelectedItem != notes.FindName("ReadTab"))
            throw new InvalidOperationException("An empty secondary Team note must remain an exact, Read-first destination.");
        if (notes.FindName("EmptyBody") is not TextBlock { Visibility: Visibility.Visible } ||
            Control<TextBlock>("EmptyNotes").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("An exact empty Team document needs an Edit invitation, not an absent-collection state or blank viewer.");
        var settings = Path.Combine(Path.GetDirectoryName(folder)!, "workspace-settings.json");
        var preference = File.ReadAllBytes(settings);
        using (File.Open(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Invoke("DesignateButton");
            PumpUntil(() => Control<TextBlock>("OngoingStatus").Text.Contains("NOT saved", StringComparison.Ordinal));
            if (Control<TextBlock>("NoteKind").Text != "Other Team note" ||
                !File.ReadAllBytes(settings).SequenceEqual(preference))
                throw new InvalidOperationException("Failed Team designation must retain both the exact secondary note and durable choice.");
        }
        Control<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
        var file = Directory.GetFiles(Path.Combine(folder, "team-notes"), "*.md")
            .Single(path => File.ReadAllText(path).Contains("Secondary Team", StringComparison.Ordinal));
        var saved = File.ReadAllBytes(file);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Control<TextBox>("MarkdownEditor").Text = "Retained secondary Team draft";
            PumpUntil(() => Control<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal));
            Invoke("OngoingButton");
            if (Control<TextBox>("MarkdownEditor").Text != "Retained secondary Team draft" ||
                Control<TextBox>("NoteTitle").Text != "Secondary Team" ||
                Control<TabControl>("Views").SelectedItem != notes.FindName("EditTab") ||
                !Control<TextBlock>("NoteStatus").Text.Contains("before leaving or closing", StringComparison.Ordinal) ||
                !File.ReadAllBytes(file).SequenceEqual(saved))
                throw new InvalidOperationException("Ongoing navigation must retain the exact failed Team editor and saved bytes.");
        }
        Invoke("RetryButton");
        Saved();
        Invoke("OngoingButton");
        if (Control<TextBox>("MarkdownEditor").Text != "Saved Team writing" ||
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
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", id.ToString("D") + ".md");
        var text = File.ReadAllText(file);
        var headerStart = text.IndexOf('\n') + 1;
        var headerEnd = text.IndexOf("\n---", headerStart, StringComparison.Ordinal);
        var header = JsonNode.Parse(text[headerStart..headerEnd])!;
        header["updatedAt"] = DateTimeOffset.Parse(header["updatedAt"]!.GetValue<string>()).AddMinutes(1).ToString("O");
        File.WriteAllText(file, text[..headerStart] + header.ToJsonString() + text[headerEnd..]);
        if (!workflow.RefreshWorkspace()) throw new InvalidOperationException(workflow.Status);
        notes.GetType().GetMethod("RefreshExternalWorkspace")!.Invoke(notes, null);
        if (Control<TextBox>("MarkdownEditor").Text != "")
            throw new InvalidOperationException("Timestamp-only delivery changed the displayed note writing.");
        Control<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
        Control<TextBox>("MarkdownEditor").Text = "Writing after timestamp-only delivery";
        var flush = (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
        PumpUntil(() => flush.IsCompleted);
        if (!flush.GetAwaiter().GetResult() ||
            !File.ReadAllText(file).Contains("Writing after timestamp-only delivery", StringComparison.Ordinal))
            throw new InvalidOperationException($"Clean {scope} editor retained a stale timestamp base: " +
                Control<TextBlock>("NoteStatus").Text);
        Ready();
        if (Control<TextBlock>("NoteStatus").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"Successful {scope} autosave left routine status in the note section.");
        PumpUntil(() => System.Windows.Automation.AutomationProperties.GetItemStatus(notes) == Control<TextBlock>("NoteStatus").Text);
        Console.WriteLine($"PASS: actual compiled clean {scope} editor accepts timestamp-only delivery before its next save.");

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

    private static void VerifyMaintenanceRetryAfterBlockedOpen(Window window, string folder)
    {
        var workflow = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        void Settled() => PumpUntil(() => ((Button)window.FindName("OpenWorkspaceButton")).IsEnabled);
        var selected = workflow.PeopleNavigation.SelectedEmail!;
        var destination = workflow.PeopleNavigation.LastValidView;
        var preferredName = workflow.GetRelationships(selected)!.Person.PreferredName;
        using (File.Open(Path.Combine(folder, "recovery", "current.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            if (!workflow.EditPreferredName(selected, "Maintenance retry fixture"))
                throw new InvalidOperationException(workflow.Status);
        if (workflow.CanSafelyClose || !workflow.LocalSaveError!.Contains("recovery maintenance incomplete", StringComparison.Ordinal))
            throw new InvalidOperationException("The compiled retry fixture did not retain a committed maintenance failure.");
        var alternate = Path.Combine(Path.GetDirectoryName(folder)!, "blocked-workspace");
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ChooseCompiledWorkspace(window, alternate);
        Settled();
        if (workflow.Compatibility.WorkspacePath != folder ||
            workflow.PeopleNavigation.SelectedEmail != selected ||
            workflow.PeopleNavigation.LastValidView != destination || Directory.Exists(alternate) ||
            !((TextBlock)window.FindName("NoticeSummary")).Text.Contains("Saved locally; recovery maintenance incomplete", StringComparison.Ordinal) ||
            workflow.CanSafelyClose)
            throw new InvalidOperationException("Blocked Workspace navigation changed the current presentation or created the destination.");
        var retry = LogicalDescendants((DependencyObject)window.FindName("SettingsPanel")).OfType<Button>()
            .Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "RetryWorkspace");
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settled();
        if (!workflow.CanSafelyClose || workflow.LocalSaveError is not null ||
            workflow.Compatibility.WorkspacePath != folder || Directory.Exists(alternate))
            throw new InvalidOperationException("Retry after blocked Workspace navigation did not complete current storage maintenance: " + workflow.Status);
        if (!workflow.EditPreferredName(selected, preferredName))
            throw new InvalidOperationException(workflow.Status);
        Console.WriteLine("PASS: actual compiled blocked Workspace navigation retains presentation and Retry completes current maintenance without retrying the unopened destination.");
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
        var file = Path.Combine(folder, "notes", selected.Id.ToString("D") + ".md");
        Task<bool> Flush() => (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
        void WaitForSave() => PumpUntil(() => NoteControl<TextBlock>("NoteStatus").Text.StartsWith(
            "Person note saved locally.", StringComparison.Ordinal) &&
            ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready");
        NoteControl<TabControl>("Views").SelectedItem = notes.FindName("EditTab");
        NoteControl<TextBox>("MarkdownEditor").Text = "Durable draft before update";
        WaitForSave();
        var saved = File.ReadAllBytes(file);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            NoteControl<TextBox>("MarkdownEditor").Text = "Retained failing update draft";
            PumpUntil(() => NoteControl<TextBlock>("NoteStatus").Text.Contains("NOT saved", StringComparison.Ordinal));
            var rejected = Flush();
            PumpUntil(() => rejected.IsCompleted);
            if (rejected.GetAwaiter().GetResult() || !File.ReadAllBytes(file).SequenceEqual(saved))
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
                !NoteControl<TextBlock>("NoteStatus").Text.Contains("before leaving or closing", StringComparison.Ordinal) ||
                !File.ReadAllBytes(file).SequenceEqual(saved))
                throw new InvalidOperationException("Close must be rejected with its exact retained Person-note draft, failure text and unchanged saved writing.");
            if (((FrameworkElement)window.FindName("SettingsPanel")).Visibility != Visibility.Collapsed ||
                ((FrameworkElement)window.FindName("ExplorerPanel")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Close was correctly rejected and its draft/error retained, but Settings still hides the Person-note editor.");
        }
        NoteControl<Button>("RetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitForSave();
        NoteControl<TextBox>("MarkdownEditor").Text = "Final private writing flushed before update";
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var flushed = Flush();
        PumpUntil(() => flushed.IsCompleted);
        var accepted = flushed.GetAwaiter().GetResult();
        if (!accepted ||
            !File.ReadAllText(file).Contains("Final private writing flushed before update", StringComparison.Ordinal))
            throw new InvalidOperationException("An update flush must save newer Person-note writing after a successful retry, even while Settings hides the editor. " +
                $"Accepted: {accepted}; note status: {NoteControl<TextBlock>("NoteStatus").Text}");
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE") is { } fixtureDirectory)
            VerifyPersonNoteUpdateHandoff(window, file, fixtureDirectory);
    }

    private static void VerifyPersonNoteUpdateHandoff(Window window, string file, string fixtureDirectory)
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
        var folder = Path.GetDirectoryName(Path.GetDirectoryName(file))!;
        Invoke((Button)notes.FindName("NewButton"));
        PumpUntil(() => ((TextBox)notes.FindName("NoteTitle")).Text == "New Person note");
        ((TextBox)notes.FindName("NoteTitle")).Text = "Automatic update writing";
        editor.Text = "Durable draft before update handoff";
        PumpUntil(() => noteStatus.Text.StartsWith("Person note saved locally.", StringComparison.Ordinal) &&
            ((TextBlock)window.FindName("WorkflowActivity")).Text == "Ready");
        file = Directory.GetFiles(Path.Combine(folder, "notes"), "*.md")
            .Single(path => File.ReadAllText(path).Contains("Durable draft before update handoff", StringComparison.Ordinal));
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            editor.Text = "Retained failing update handoff draft";
            PumpUntil(() => noteStatus.Text.Contains("NOT saved", StringComparison.Ordinal));
            SetSettings(true);
            Invoke(check);
            PumpUntil(() => check.IsEnabled && updateStatus.Text.Contains("Automatic update paused", StringComparison.Ordinal));
            SetSettings(false);
        }
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
        var workspace = Path.GetDirectoryName(Path.GetDirectoryName(file))!;
        var expectedHash = handoff.RootElement.GetProperty("workspaceFiles").GetProperty(Path.GetRelativePath(workspace, file)).GetString();
        if (!File.ReadAllText(file).Contains("Final private writing flushed before installer handoff", StringComparison.Ordinal) ||
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) != expectedHash)
            throw new InvalidOperationException("The actual update command handed off before the final Person-note bytes were durable.");
        SetSettings(false);
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) => { if (complete() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!complete()) throw new TimeoutException("The no-window profile refresh operation did not complete.");
    }
}
