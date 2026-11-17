using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyCompiledAutomaticInputScroll(Type windowType, string settings, Func<Window, string?> chooseFolder)
    {
        var path = Path.Combine(settings, "automatic-input-scroll");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path, WorkspaceOpenMode.OpenOrCreate) ||
                !seed.ApplyImport(seed.PreviewImport(RecoveryPeople).Id))
                throw new InvalidOperationException(seed.Status);
        var clock = new SnapshotClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var ended = new ManualResetEventSlim();
        var hold = false;
        var publisher = new AutomaticSnapshotPublicationGate(() => hold, entered.Set,
            () => release.Wait(TimeSpan.FromSeconds(15)), ended.Set);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(filePublisher: publisher), chooseFolder, (Action<string>)(_ => { }), clock], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WorkspaceViewModel? workflow = null;
        try
        {
            Invoke("OpenSettingsButton");
            ChooseCompiledWorkspace(window, path);
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready");
            workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            var morgan = workflow.ReportingRoots.Single(root => root.Person.Email == "morgan@example.com");
            Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, morgan,
                TreeView.SelectedItemChangedEvent));
            var surface = Control<UserControl>("TeamNotes");
            T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
            Note<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Note<TextBox>("NoteTitle").Text = "Automatic publication owner";
            Note<TextBox>("MarkdownEditor").Text = "Before automatic export";
            var note = workflow.TeamNotesView.SelectedNote!.Id;
            PumpUntil(() => workflow.GetRetainedNoteDraft(NoteScope.Team, note) is null);
            if (!workflow.ConfigureSynchronizationFolder(Path.Combine(settings, "automatic-input-out")))
                throw new InvalidOperationException(workflow.Synchronization.Summary);
            hold = true;
            clock.Advance(TimeSpan.FromSeconds(60));
            PumpUntil(() => entered.IsSet);
            var size = new Size(1024, 670);
            var content = (FrameworkElement)window.Content;
            void Layout()
            {
                content.InvalidateMeasure();
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();
            }
            Layout();
            var scroll = Control<ScrollViewer>("PersonScroll");
            var editor = Note<TextBox>("MarkdownEditor");
            var heading = Note<TextBlock>("NoteHeading");
            var signals = new List<object>();
            var armed = false;
            window.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((_, args) =>
                {
                    if (armed && signals.Count < 24)
                        signals.Add(new { signal = "bring-into-view", target = args.TargetObject.GetType().Name,
                            name = (args.TargetObject as FrameworkElement)?.Name, rect = args.TargetRect.ToString(),
                            scroll.VerticalOffset, scroll.ExtentHeight });
                }), true);
            scroll.ScrollChanged += (_, args) =>
            {
                if (armed && ReferenceEquals(args.OriginalSource, scroll) && signals.Count < 24)
                    signals.Add(new { signal = "scroll-changed", args.VerticalChange, args.ExtentHeightChange,
                        args.VerticalOffset, args.ExtentHeight });
            };
            double Y(FrameworkElement target) => target.TransformToAncestor(scroll).Transform(new Point()).Y;
            object Frame(string stage) => new
            {
                stage, scroll.VerticalOffset, scroll.ExtentHeight, scroll.ViewportHeight,
                titleY = Y(heading), titleHeight = heading.ActualHeight,
                editorY = Y(editor), editorHeight = editor.ActualHeight,
                editor.IsKeyboardFocused, editor.IsKeyboardFocusWithin, editor.CaretIndex,
                editor.Text, retained = workflow.GetRetainedNoteDraft(NoteScope.Team, note) is not null
            };
            var top = Y(heading) + scroll.VerticalOffset;
            var bottom = Y(editor) + editor.ActualHeight + scroll.VerticalOffset;
            scroll.ScrollToVerticalOffset((top + bottom - scroll.ViewportHeight) / 2);
            Layout();
            armed = true;
            if (Y(heading) < 0 || Y(editor) + editor.ActualHeight > scroll.ViewportHeight)
                throw new InvalidOperationException("The compiled input replay must begin with the complete original note reading context.");
            var before = Frame("before-dispatcher-drain");
            PumpUntil(() => { Layout(); return true; });
            var settled = Frame("before-provider-input");
            var provider = (IValueProvider)UIElementAutomationPeer.CreatePeerForElement(editor).GetPattern(PatternInterface.Value);
            provider.SetValue("Newer writing during automatic export");
            Layout();
            var immediate = Frame("after-provider-input");
            editor.BringIntoView();
            Layout();
            var requestedVisibility = Frame("after-public-editor-bring-into-view");
            PumpUntil(() => workflow.GetRetainedNoteDraft(NoteScope.Team, note) is null);
            Layout();
            var saved = Frame("after-local-save");
            if (Y(editor) < 0 || Y(editor) + editor.ActualHeight > scroll.ViewportHeight ||
                !editor.IsEnabled || provider.IsReadOnly || provider.Value != "Newer writing during automatic export" ||
                heading.Text != "Automatic publication owner" || workflow.TeamNotesView.SelectedNote?.Id != note ||
                AutomationProperties.GetHelpText(heading) != note.ToString("D") ||
                Control<TextBlock>("Email").Text != "morgan@example.com")
                throw new InvalidOperationException("Ordinary compiled editor visibility settlement lost usable exact owner writing.");
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            if (!reopened.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                reopened.BeginNoteDraft(NoteScope.Team, "morgan@example.com", note)?.Markdown != "Newer writing during automatic export" ||
                !workflow.IsSynchronizing || release.IsSet || ended.IsSet ||
                window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The compiled provider replay lost exact durable owner writing, the held export or invisible-only isolation.");
            Console.WriteLine("DIAGNOSTIC: compiled-automatic-input-viewport " + SnapshotNativeUsability.SerializeDiagnostic(new
            {
                before, settled, immediate, requestedVisibility, saved, signals,
                interpretation = "Actual compiled controls, public Value provider and deliberately requested BringIntoView during pending layout. No native focus/caret event, IsOffscreen or screen visibility is manufactured or inferred."
            }));
            Console.WriteLine("PASS: invisible compiled Team-note provider input and ordinary visibility request preserve exact owner, identity, durable writing and held export across pending-layout settlement; not native keyboard-focus or pixel evidence.");
        }
        finally
        {
            hold = false;
            release.Set();
            if (workflow is not null) PumpUntil(() => !workflow.IsSynchronizing);
            workflow?.Dispose();
            window.Close();
        }
    }
}
