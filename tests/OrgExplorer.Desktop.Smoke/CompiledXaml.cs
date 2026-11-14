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
            }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            if (verifyProfileRefresh)
            {
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
                !seed.EditPersonManager("avery@example.com", "alex@example.com") ||
                !seed.EditPersonText("alex@example.com", PersonTextField.JobTitle, "Director"))
                throw new InvalidOperationException("Context reading fixture failed: " + seed.Status);
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
            PumpUntil(() => results.Items.Cast<SearchResult>().Any(result => result.Person.Email == "casey@example.com"));
            Layout();
            Measure("after-query-before-activate");
            void Enter(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                new UnshownInputSource { RootVisual = root }, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            FocusManager.SetFocusedElement(window, Control<TextBox>("SearchQuery"));
            Enter(Control<TextBox>("SearchQuery"));
            Layout();
            Measure("query-enter-focus");
            results.SelectedItem = results.Items.Cast<SearchResult>().Single(result => result.Person.Email == "casey@example.com");
            Enter(results);
            PumpUntil(() => results.SelectedItem is null);
            Layout();
            Measure("rejected-search");
            if (before != Bounds(Control<ScrollViewer>("PersonScroll")) ||
                Control<TextBlock>("Email").Text != "avery@example.com" || Note<TextBox>("NoteTitle").Text != "" ||
                Note<TextBox>("MarkdownEditor").Text != draft ||
                System.Windows.Automation.AutomationProperties.GetHelpText(Note<TextBlock>("NoteHeading")) != note ||
                Control<TextBox>("SearchQuery").Text != "casey@example" ||
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
        Control<TextBox>("SearchQuery").Text = "orchid";
        PumpUntil(() => Control<ListBox>("SearchResults").Items.Cast<SearchResult>().Any(r => r.NoteId == noteId));
        void Enter(UIElement target)
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new UnshownInputSource { RootVisual = root }, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        Enter(Control<TextBox>("SearchQuery"));
        if (Control<TextBlock>("Email").Text != "" ||
            !ReferenceEquals(FocusManager.GetFocusedElement(window), Control<ListBox>("SearchResults")))
            throw new InvalidOperationException("Query Enter must focus results without opening the first Person.");
        Control<ListBox>("SearchResults").SelectedItem = Control<ListBox>("SearchResults").Items.Cast<SearchResult>()
            .Single(r => r.NoteId == noteId);
        if (Control<TextBlock>("Email").Text != "")
            throw new InvalidOperationException("Selecting a result must not navigate before explicit activation.");
        Enter(Control<ListBox>("SearchResults"));
        PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com");
        void Activate(string query, Func<SearchResult, bool> predicate)
        {
            Control<TextBox>("SearchQuery").Text = query;
            var results = Control<ListBox>("SearchResults");
            PumpUntil(() => results.Items.Cast<SearchResult>().Any(predicate));
            results.SelectedItem = results.Items.Cast<SearchResult>().Single(predicate);
            Enter(results);
        }
        Activate("007b", r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.Profile);
        PumpUntil(() => ReferenceEquals(FocusManager.GetFocusedElement(window), Control<TextBlock>("OfficeNumberValue")));
        if (Control<TextBlock>("OfficeNumberValue").Text != "007B" ||
            window.FindName("SearchContextPanel") is not null)
            throw new InvalidOperationException("Profile search must reveal its actual matching field, not a generic inspector.");
        Activate("cedar", r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.HistoricalTeamNote);
        PumpUntil(() => Control<Expander>("HistoryAccess").IsExpanded &&
            Control<FlowDocumentScrollViewer>("HistoricalTeamReading").GetValue(
                System.Windows.Automation.AutomationProperties.HelpTextProperty) is string text && text.Contains(teamId.ToString(), StringComparison.Ordinal));
        var historical = Control<FlowDocumentScrollViewer>("HistoricalTeamReading");
        var frozen = historical.Document.Blocks.OfType<Section>().SelectMany(section => section.Blocks.OfType<Section>())
            .Single(section => Equals(section.Tag, teamId));
        var frozenText = new TextRange(frozen.ContentStart, frozen.ContentEnd).Text;
        if (!frozenText.Contains("Cedar frozen writing", StringComparison.Ordinal) ||
            frozenText.Contains("Pine current writing", StringComparison.Ordinal) ||
            !frozenText.Contains("morgan@example.com", StringComparison.Ordinal))
            throw new InvalidOperationException("Historical search did not reveal the exact frozen owner's document.");
        Activate("pine", r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.InheritedTeamNote);
        var teams = Control<FrameworkElement>("TeamNotes");
        PumpUntil(() => ((Expander)teams.FindName("CurrentTeamContext")).IsExpanded);
        var inherited = (FlowDocumentScrollViewer)teams.FindName("InheritedReading");
        if (!System.Windows.Automation.AutomationProperties.GetHelpText(inherited).Contains(teamId.ToString(), StringComparison.Ordinal) ||
            !new TextRange(inherited.Document.ContentStart, inherited.Document.ContentEnd).Text.Contains("Pine current writing", StringComparison.Ordinal))
            throw new InvalidOperationException("Current inherited search did not reveal the owner's exact current document.");
        Control<TextBox>("SearchQuery").Clear();
        Settle(false);
        foreach (var compact in new[] { false, true })
        {
            VirtualizingPanel.SetCacheLength(tree, new VirtualizationCacheLength(compact ? 1 : 0));
            Layout(compact);
            if (compact) Invoke("OpenPeopleButton");
            Settle(compact);
            var manager = tree.Items.Cast<ReportingNode>().Single(n => n.Person.Email == "morgan@example.com");
            var branch = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manager);
            if (!UIElementAutomationPeer.CreatePeerForElement(branch).GetName().Contains("morgan@example.com", StringComparison.Ordinal))
                throw new InvalidOperationException("Compact reporting rows must retain exact accessible Person identity.");
            if (!compact)
            {
                branch.IsSelected = true;
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
            var originalPosition = ReadPosition();
            Control<TextBox>("SearchQuery").Text = "orchid";
            PumpUntil(() => Control<ListBox>("SearchResults").Items.Cast<SearchResult>().Any(r => r.NoteId == noteId));
            Settle(compact);
            if (compact)
            {
                // Replay lost virtualized width across hiding without changing the Person population.
                VirtualizingPanel.SetCacheLength(tree, new VirtualizationCacheLength(0));
                tree.InvalidateMeasure();
                Settle(compact);
            }
            Control<ListBox>("SearchResults").SelectedItem = Control<ListBox>("SearchResults").Items.Cast<SearchResult>()
                .Single(r => r.NoteId == noteId);
            Enter(Control<ListBox>("SearchResults"));
            PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com" &&
                ((TextBlock)Control<FrameworkElement>("PersonNotes").FindName("NoteHeading")).Text == "Individual context");
            Settle(compact);
            Control<TextBox>("SearchQuery").Clear();
            if (compact) Invoke("OpenPeopleButton");
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
        Console.WriteLine("PASS: compiled wide/compact exact-note search restores the real tree scroll offset and expansion without a window/native handle.");
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
        var photo = Control<FrameworkElement>("Photo");
        var placeholder = (TextBlock)photo.FindName("Placeholder");
        if (placeholder.Text != "A" || photo.ActualWidth > 64 || photo.ActualHeight > 64 ||
            !System.Windows.Automation.AutomationProperties.GetName(placeholder).Contains("No local photo", StringComparison.Ordinal))
            throw new InvalidOperationException("Missing photos need compact, accessible initials rather than oversized missing-photo furniture.");
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
                throw new InvalidOperationException("The shared scroll moved identity or failed to move facts with notes.");
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
        var facts = Control<UniformGrid>("FactsGrid");
        var slots = facts.Children.Cast<FrameworkElement>().ToArray();
        var columns = facts.Columns;
        var lefts = slots.Select(slot => slot.TransformToAncestor(facts).Transform(new Point(0, 0)).X).ToArray();
        var tree = Control<TreeView>("PeopleList");
        var originalPerson = tree.Items.Cast<ReportingNode>().Single(n => n.Person.Email == "avery@example.com");
        var longPerson = tree.Items.Cast<ReportingNode>().Single(n => n.Person.Email == "zara@example.com");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(originalPerson, longPerson, TreeView.SelectedItemChangedEvent));
        Layout();
        if (facts.Children.Count != 8 || facts.Columns != columns ||
            slots.Any(slot => slot.Visibility != Visibility.Visible) ||
            slots.Where((slot, index) => Math.Abs(slot.TransformToAncestor(facts).Transform(new Point(0, 0)).X - lefts[index]) > 1).Any() ||
            Control<TextBlock>("DirectoryName").Text != "Zara with a deliberately long Directory name for accessible identity" ||
            Control<TextBlock>("OfficeNumberValue").Text != "—" ||
            Control<TextBlock>("LinkedInProfileUrlValue").Text != "—")
            throw new InvalidOperationException("Long or missing values repacked facts, lost full text, or hid labelled slots. " +
                $"Columns: {columns} -> {facts.Columns}; X: {string.Join(",", lefts)} -> " +
                $"{string.Join(",", slots.Select(slot => slot.TransformToAncestor(facts).Transform(new Point(0, 0)).X))}; " +
                $"Directory name={Control<TextBlock>("DirectoryName").Text}; office={Control<TextBlock>("OfficeNumberValue").Text}; " +
                $"URL={Control<TextBlock>("LinkedInProfileUrlValue").Text}; viewport={viewport.ViewportWidth}x{viewport.ViewportHeight}.");
        var wideColumns = facts.Columns;
        Layout(700, 500);
        if (facts.Columns >= wideColumns || Control<Button>("OpenPeopleButton").Visibility != Visibility.Visible ||
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
