using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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
            if (window.FindName("DiscardImportButton") is not Button)
                failures.Add("Failed Import has no independent discard control.");
            foreach (var name in new[] { "DirectoryName", "Email", "AliasValue", "JobTitleValue", "DepartmentValue",
                "OfficeBuildingValue", "OfficeNumberValue", "OfficeStateValue", "OfficeCountryValue", "LinkedInProfileUrlValue" })
            {
                if (window.FindName(name) is not TextBlock value || !value.Focusable ||
                    !KeyboardNavigation.GetIsTabStop(value) || value.ContextMenu is null)
                    failures.Add($"{name}: View-mode Copy must be reachable by keyboard.");
            }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            if (verifyProfileRefresh) VerifyProfileSaveRefresh(window, settings);
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

    private static void VerifyProfileSaveRefresh(Window window, string settings)
    {
        var folder = Path.Combine(settings, "workspace");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                    {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000001",
                     "observedAt":"2026-11-01T17:00:00Z","people":[
                     {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                    """).Id) || seed.CreatePersonNote("avery@example.com", "Saved title", "# Saved note") is null)
                throw new InvalidOperationException("Profile refresh fixture failed: " + seed.Status);
        }
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Control<TextBox>("WorkspacePath").Text = folder;
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
        var tree = Control<TreeView>("PeopleList");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items[0], TreeView.SelectedItemChangedEvent));
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
                NoteControl<Button>("DiscardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready");
            }
        }
        Console.WriteLine("PASS: profile Save refreshes accepted note delivery and preserves unsaved note writing without showing a window.");
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
