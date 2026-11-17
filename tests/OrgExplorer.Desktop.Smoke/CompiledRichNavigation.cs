using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private sealed class RichUiStorageObservation : IWorkspaceFileObserver
    {
        private readonly object gate = new();
        private readonly List<object> phases = [];
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (!phase.Name.StartsWith("snapshot.", StringComparison.Ordinal)) return;
            lock (gate)
                if (phases.Count < 256)
                    phases.Add(new { phase.Name, phase.Kind, timestamp = Stopwatch.GetTimestamp(),
                        thread = Environment.CurrentManagedThreadId });
        }
        internal object[] Snapshot() { lock (gate) return phases.ToArray(); }
    }

    private sealed class OwnedRealSynchronizationClock : TimeProvider, IDisposable
    {
        private readonly object gate = new();
        private readonly List<ITimer> timers = [];
        private bool stopped;
        private int callbacks;
        internal bool CallbacksComplete { get { lock (gate) return callbacks == 0; } }
        public override long TimestampFrequency => System.TimestampFrequency;
        public override long GetTimestamp() => System.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = System.CreateTimer(value =>
            {
                lock (gate)
                {
                    if (stopped) return;
                    callbacks++;
                }
                try { callback(value); }
                finally { lock (gate) callbacks--; }
            }, state, dueTime, period);
            timers.Add(timer);
            return timer;
        }
        internal void StopFutureScheduling()
        {
            lock (gate) stopped = true;
            foreach (var timer in timers) timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        public void Dispose() { foreach (var timer in timers) timer.Dispose(); }
    }

    private static void DiagnoseCompiledRichNavigation(Type windowType, string root)
    {
        var execution = Stopwatch.StartNew();
        using var fixture = new ScaleFixture(Path.Combine(root, "rn"));
        fixture.Build();
        var storage = new RichUiStorageObservation();
        using var clock = new OwnedRealSynchronizationClock();
        using var store = ApplicationWorkflow.CreateFileBacked(fileObserver: storage);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => fixture.Workspace), (Action<string>)(_ => { }), clock], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        void JoinOwnedPublication(Task<bool> task)
        {
            var remaining = TimeSpan.FromSeconds(660) - execution.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The whole diagnostic's existing 660-second budget expired.");
            using var timeout = new CancellationTokenSource(remaining);
            var bounded = task.WaitAsync(timeout.Token);
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = bounded.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            if (!bounded.GetAwaiter().GetResult())
                throw new InvalidOperationException("The owning rich diagnostic could not join shared publication: " + model.Synchronization.Summary);
        }
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var workload = ScaleFixture.RequireRichPopulation(model);
        var tree = Control<TreeView>("PeopleList");
        var pending = new Stack<ReportingNode>(model.ReportingRoots);
        var nodes = new Dictionary<string, ReportingNode>();
        while (pending.TryPop(out var node))
        {
            nodes.Add(node.Person.Email, node);
            foreach (var report in node.Reports) pending.Push(report);
        }
        var content = (FrameworkElement)window.Content;
        void Layout()
        {
            content.Measure(new Size(1100, 900));
            content.Arrange(new Rect(0, 0, 1100, 900));
            content.UpdateLayout();
        }
        void Select(string email) => tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, nodes[email], TreeView.SelectedItemChangedEvent));
        var owner = ScaleFixture.Email(11);
        Select(owner);
        Layout();
        var samples = new List<object>();
        var editors = new List<object>();
        var verification = new List<object>();
        var actionPhases = new List<object>();
        Stopwatch? actionClock = null;
        var observations = new List<(DependencyObject Element, DependencyPropertyDescriptor Descriptor, EventHandler Handler)>();
        void ObserveUi(DependencyObject element, DependencyProperty property, string name)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
            EventHandler handler = (_, _) =>
            {
                if (actionClock is not null && actionPhases.Count < 64)
                    actionPhases.Add(new { name, ms = actionClock.Elapsed.TotalMilliseconds });
            };
            descriptor.AddValueChanged(element, handler);
            observations.Add((element, descriptor, handler));
        }
        ObserveUi(Control<TextBlock>("Email"), TextBlock.TextProperty, "email");
        ObserveUi(Control<TextBlock>("ProfileHeading"), TextBlock.TextProperty, "heading");
        ObserveUi(Control<Button>("Manager"), ContentControl.ContentProperty, "manager");
        ObserveUi(Control<ListBox>("ReportsList"), ItemsControl.ItemsSourceProperty, "reports");
        foreach (var family in new[] { "PersonNotes", "TeamNotes" })
            ObserveUi((TextBlock)Control<UserControl>(family).FindName("NoteHeading"), TextBlock.TextProperty, family);
        var choiceAssignments = 0;
        var choices = Control<ComboBox>("ManagerChoices");
        var choicesDescriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox));
        EventHandler changedChoices = (_, _) => choiceAssignments++;
        choicesDescriptor.AddValueChanged(choices, changedChoices);
        var writing = new Dictionary<(NoteScope Scope, Guid Id), string>();
        foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
            for (var ordinal = 0; ordinal < (scope == NoteScope.Person ? 1 : 20); ordinal++)
            {
                var surface = Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes");
                T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
                var previous = model.GetOwnedNotesView(scope).SelectedNote?.Id;
                actionPhases.Clear();
                var allocatedEditor = GC.GetAllocatedBytesForCurrentThread();
                var editorStarted = Stopwatch.GetTimestamp();
                var exportingBeforeEditor = model.IsSynchronizing;
                actionClock = Stopwatch.StartNew();
                Note<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var editorDispatchMs = actionClock.Elapsed.TotalMilliseconds;
                var id = model.GetOwnedNotesView(scope).SelectedNote!.Id;
                Layout();
                if (id == previous || !Note<TextBox>("NoteTitle").IsEnabled || !Note<TextBox>("MarkdownEditor").IsEnabled ||
                    Note<TabControl>("Views").SelectedItem != Note<TabItem>("EditTab"))
                    throw new InvalidOperationException("The actual rich Add action did not expose a new exact usable logical editor.");
                editors.Add(new { scope, ordinal, editorStarted, dispatchMs = editorDispatchMs,
                    throughLayoutMs = actionClock.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedEditor,
                    exportingBefore = exportingBeforeEditor, exportingAfter = model.IsSynchronizing,
                    phases = actionPhases.ToArray() });
                actionClock = null;
                Note<TextBox>("NoteTitle").Text = $"Compiled rich {scope} {ordinal:D2}";
                var prefix = $"Exact compiled rich {scope} {ordinal:D2}";
                Note<TextBox>("MarkdownEditor").Text = prefix;
                foreach (var destination in new[] { ScaleFixture.Target, owner })
                {
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var assignments = choiceAssignments;
                    actionPhases.Clear();
                    var startedAt = Stopwatch.GetTimestamp();
                    var exportingBefore = model.IsSynchronizing;
                    actionClock = Stopwatch.StartNew();
                    if (destination == owner) Invoke("Manager");
                    else Select(destination);
                    var dispatchMs = actionClock.Elapsed.TotalMilliseconds;
                    Layout();
                    var throughLayoutMs = actionClock.Elapsed.TotalMilliseconds;
                    if (Control<TextBlock>("Email").Text != destination ||
                        model.PeopleNavigation.SelectedEmail != destination ||
                        Control<TextBlock>("ProfileHeading").Text != nodes[destination].Person.Heading ||
                        Control<Button>("EditProfileButton").IsEnabled == false)
                        throw new InvalidOperationException("The actual rich compiled navigation did not expose its exact usable owner/view.");
                    samples.Add(new { scope, ordinal, destination, dispatchMs, throughLayoutMs,
                        totalMs = actionClock.Elapsed.TotalMilliseconds, startedAt,
                        action = destination == owner ? "actual Manager button" : "actual reporting selection event",
                        managerChoiceAssignments = choiceAssignments - assignments,
                        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                        exportingBefore, exportingAfter = model.IsSynchronizing, phases = actionPhases.ToArray() });
                    actionClock = null;
                }
                Note<TabControl>("Views").SelectedItem = Note<TabItem>("EditTab");
                for (var edit = 0; edit < 6; edit++)
                {
                    var spacing = Stopwatch.StartNew();
                    PumpUntil(() => spacing.ElapsedMilliseconds >= 250);
                    Note<TextBox>("MarkdownEditor").Text = prefix + $" continued input {edit}";
                }
                writing[(scope, id)] = prefix + " continued input 5";
                PumpUntil(() => model.GetRetainedNoteDraft(scope, id) is null);
                var verifying = Stopwatch.StartNew();
                var reopened = Task.Run(() =>
                {
                    using var independent = OpenSmokeWorkspace(fixture.Workspace);
                    if (independent.BeginNoteDraft(scope, owner, id)?.Markdown != prefix + " continued input 5" ||
                        independent.BeginNoteDraft(scope, ScaleFixture.Target, id) is not null)
                        throw new InvalidOperationException("Per-sample full-rich reopen did not preserve the exact owner/writing.");
                });
                PumpUntil(() => reopened.IsCompleted);
                reopened.GetAwaiter().GetResult();
                verification.Add(new { scope, ordinal, fullReopenMs = verifying.Elapsed.TotalMilliseconds,
                    exportingAfter = model.IsSynchronizing });
            }
        PumpUntil(() => model.CanSafelyClose);
        clock.StopFutureScheduling();
        PumpUntil(() => clock.CallbacksComplete);
        var admittedPublication = model.SynchronizeAsync();
        JoinOwnedPublication(admittedPublication);
        PumpUntil(() => !model.IsSynchronizing);
        choicesDescriptor.RemoveValueChanged(choices, changedChoices);
        foreach (var observation in observations)
            observation.Descriptor.RemoveValueChanged(observation.Element, observation.Handler);
        Invoke("EditProfileButton");
        if (choices.Items.Count != model.People.Count || model.ProfileEdit?.Basis.Email != owner)
            throw new InvalidOperationException("Actual full-rich profile Edit omitted Manager candidates or changed its owner/base.");
        Invoke("CancelProfileButton");
        using (var independent = OpenSmokeWorkspace(fixture.Workspace))
        {
            ScaleFixture.RequireRichPopulation(independent, additionalPersonNotes: 1, additionalTeamNotes: 20);
            foreach (var ((scope, id), body) in writing)
                if (independent.BeginNoteDraft(scope, owner, id)?.Markdown != body ||
                    independent.BeginNoteDraft(scope, ScaleFixture.Target, id) is not null)
                    throw new InvalidOperationException("Rich compiled diagnosis lost exact independent owner/writing.");
        }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Compiled rich diagnosis cannot create a native window.");
        Console.WriteLine("COMPILED-RICH-NAVIGATION " + JsonSerializer.Serialize(new
        {
            workload, sampleCount = samples.Count, samples, editorCount = editors.Count, editors, verification,
            timestampFrequency = Stopwatch.Frequency, realPeriodicExportPhases = storage.Snapshot(),
            boundary = "Before actual compiled Add/report selection/Manager-button event through exact logical owner/header/editor and complete1100x900 layout. Actual six250ms continued inputs per note; each full-rich independent reopen runs on observer work while WPF pumps normally. Real System-backed60s synchronization runs during all samples; only fixture-owned future scheduling stops for joined cleanup.",
            limit = "Exploratory actual BAML/public-real-store feedback only; not native UIA/provider/pixels, healthy WPF acceptance or a replacement for any42 native samples/100ms target."
        }));
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
        Console.WriteLine("RICH-FIXTURE-CLOSED " + JsonSerializer.Serialize(new
        {
            closed, model.IsSynchronizing, clock.CallbacksComplete,
            logicalWindows = Application.Current.Windows.Count
        }));
        using var exclusive = File.Open(Path.Combine(fixture.SynchronizationFolder, ".orgexplorer-snapshot-lease"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
