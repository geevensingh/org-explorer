using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static int VerifyCompiledXaml(string executable, bool verifyProfileRefresh = false)
    {
        if (verifyProfileRefresh) Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
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
            if (verifyProfileRefresh)
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var type = assembly.GetType("OrgExplorer.Desktop.MainWindow", throwOnError: true)!;
            var window = (Window)Activator.CreateInstance(type)!;
            var failures = new List<string>();
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
            if (window.FindName("WorkspaceName") is not TextBlock workspaceName || workspaceName.Focusable)
                failures.Add("Everyday chrome needs a quiet, non-interactive Workspace name.");
            var search = (FrameworkElement)window.FindName("SearchQuery");
            if (Contains((FrameworkElement)window.FindName("ExplorerPanel"), search))
                failures.Add("Search must remain directly accessible outside the People view.");
            if (window.FindName("DiscardImportButton") is not Button)
                failures.Add("Failed Import has no independent discard control.");
            var personNotes = (FrameworkElement)window.FindName("PersonNotes");
            var noteList = (ListBox)personNotes.FindName("NotesList");
            var surfacePeer = UIElementAutomationPeer.CreatePeerForElement(personNotes);
            var listPeer = UIElementAutomationPeer.CreatePeerForElement(noteList);
            if (surfacePeer.GetAutomationId() == listPeer.GetAutomationId())
                failures.Add("The Person-note surface and its secondary title list share an automation identity; empty/collapsed-list discovery targets the visible surface.");
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
                VerifyProfileSaveRefresh(window, settings);
            }
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
            Directory.Delete(settings, true);
        }
    }

    private static void VerifySettingsFocus(Window window)
    {
        var settings = (Button)window.FindName("OpenSettingsButton");
        var folder = (TextBox)window.FindName("WorkspacePath");
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
            throw new InvalidOperationException("Opening Settings did not establish the Workspace folder as its logical focus destination.");
        Toggle();
        Toggle();
        Toggle();
        Settle();
        if (!ReferenceEquals(FocusManager.GetFocusedElement(window), settings))
            throw new InvalidOperationException("A queued Settings focus request stole logical focus after Settings closed.");
        Console.WriteLine("PASS: Settings logical focus settles on its folder and stale requests cannot focus closed Settings; physical keyboard focus still requires hosted WPF.");
    }

    private static void VerifyProfileSaveRefresh(Window window, string settings)
    {
        var folder = Path.Combine(settings, "workspace");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                    {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000001",
                     "observedAt":"2026-11-01T17:00:00Z","people":[
                     {"email":"avery@example.com","displayName":"Avery","managerEmail":null,
                      "jobTitle":"Researcher","department":"Research","officeCountry":"Canada",
                      "alias":"ave","officeNumber":"007B"}]}
                    """).Id) || seed.CreatePersonNote("avery@example.com", "Saved title", "# Saved note") is null)
                throw new InvalidOperationException("Profile refresh fixture failed: " + seed.Status);
        }
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Control<TextBox>("WorkspacePath").Text = folder;
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
        Invoke("OpenSettingsButton");
        if (Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Collapsed ||
            Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Collapsed ||
            Control<TextBlock>("WorkspaceName").Text != "workspace" ||
            System.Windows.Automation.AutomationProperties.GetHelpText(Control<TextBlock>("WorkspaceName")) != folder)
            throw new InvalidOperationException("Settings must use the bounded main work area without navigating or opening Import.");
        Invoke("OpenSettingsButton");
        var tree = Control<TreeView>("PeopleList");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items[0], TreeView.SelectedItemChangedEvent));
        if (window.FindName("ProfileDetails") is not Expander details || details.IsExpanded)
            throw new InvalidOperationException("Person reading must start with secondary profile details collapsed.");
        if (Control<TextBlock>("JobTitleValue").Text != "Researcher" ||
            Control<TextBlock>("DepartmentValue").Text != "Research" ||
            Control<TextBlock>("OfficeCountryValue").Text != "Canada")
            throw new InvalidOperationException("The essential profile summary lost role, department or location.");
        details.IsExpanded = true;
        if (Control<TextBlock>("OfficeNumberValue").Text != "007B" ||
            Control<FrameworkElement>("LinkedInProfileUrlValueRow").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("More details must expose populated attributes without empty rows.");
        Invoke("EditProfileButton");
        if (Control<FrameworkElement>("AttributeEditors").Visibility != Visibility.Visible ||
            Control<TextBox>("LinkedInProfileUrl").Text != "" ||
            Control<FrameworkElement>("ProfileValues").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Full Edit must expose empty fields without duplicate reading values.");
        Invoke("CancelProfileButton");
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
            }
            finally { property.RemoveValueChanged(status, saved); }
            if (dirtyNote)
            {
                Invoke("OpenSettingsButton");
                if (NoteControl<TextBox>("MarkdownEditor").Text != "# Retained unsaved note")
                    throw new InvalidOperationException("Opening Settings changed independent writing.");
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
        VerifyPersonNoteUpdateFlush(window, folder);
        Console.WriteLine("PASS: compact profile reading/editing, safe Settings Import rejection and profile-save note refresh without showing a window.");
    }

    private static void VerifyPersonNoteUpdateFlush(Window window, string folder)
    {
        var notes = (FrameworkElement)window.FindName("PersonNotes");
        T NoteControl<T>(string name) where T : FrameworkElement => (T)notes.FindName(name);
        var file = Directory.GetFiles(Path.Combine(folder, "notes"), "*.md").Single();
        Task<bool> Flush() => (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
        void WaitForSave() => PumpUntil(() => NoteControl<TextBlock>("NoteStatus").Text == "Person note saved locally.");
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
        PumpUntil(() => noteStatus.Text == "Person note saved locally.");
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
