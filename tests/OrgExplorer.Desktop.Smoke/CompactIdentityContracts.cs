using System.IO;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyCompactIdentityReplay(Type windowType, string root)
    {
        VerifyCompactSearchActivation(windowType, root);
        using var publisher = new HeldNotePublisher();
        using var store = ApplicationWorkflow.CreateFileBacked(fileObserver: publisher, filePublisher: publisher);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => CompiledWorkspaceChoices.Dequeue())], null)!;
        try { VerifyProfileSaveRefresh(window, root, publisher, compactIdentityOnly: true); }
        finally
        {
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The compact identity replay cannot create a native window.");
            ClosePublishedCompiledWindow(window);
        }
        Console.WriteLine("PASS: actual compact identity replay retains the same caller/save/search and complete identity/editor matrix without a native window.");
    }

    private static void VerifyCompactSearchActivation(Type windowType, string root)
    {
        VerifyCompactSaveQueryOverlap(windowType, root);
        foreach (var scenario in Enum.GetValues<CompactActivationCase>())
            VerifyCompactActivationCase(windowType, root, scenario);
    }

    private static void VerifyCompactSaveQueryOverlap(Type windowType, string root)
    {
        var path = Path.Combine(root, "overlap");
        var target = CompactIdentityStateCases[0].Email;
        var batch = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, batchId = Guid.NewGuid(), observedAt = "2026-11-01T17:00:00Z",
            people = CompactIdentityFixturePeople()
        });
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport(RecoveryPeople).Id) ||
                !seed.ApplyImport(seed.PreviewImport(batch).Id))
                throw new InvalidOperationException(seed.Status);
        }
        var proxy = DispatchProxy.Create<IApplicationWorkflow, CompactWorkflowBoundary>();
        var boundary = (CompactWorkflowBoundary)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var store = proxy;
        var window = CreateHeldSnapshotWindow(windowType, store, path);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var results = Control<ListBox>("SearchResults");
        var tree = Control<TreeView>("PeopleList");
        var model = (WorkspaceViewModel)tree.DataContext;
        var open = Control<Button>("OpenWorkspaceButton");
        var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(Button))!;
        var armEnter = false;
        var enterRequests = 0;
        CompactGestureCompletion? gesture = null;
        EventHandler ready = (_, _) =>
        {
            if (!armEnter || !open.IsEnabled) return;
            armEnter = false;
            window.Dispatcher.BeginInvoke(() =>
            {
                results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result => result.Person.Email == target);
                enterRequests++;
                gesture = RaiseCompactSearchEnter(window, results);
            }, DispatcherPriority.Send);
        };
        descriptor.AddValueChanged(open, ready);
        try
        {
            Invoke("OpenSettingsButton");
            Invoke("OpenWorkspaceButton");
            PumpUntil(() => model.IsWorkspaceCurrent && open.IsEnabled);
            var avery = model.ReportingRoots.SelectMany(node => new[] { node }.Concat(node.Reports))
                .Single(node => node.Person.Email == "avery@example.com");
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, avery, TreeView.SelectedItemChangedEvent));
            Invoke("EditProfileButton");
            Control<TextBox>("OfficeNumber").Text = "Committed before compact navigation";
            Control<TextBox>("SearchQuery").Text = target;
            PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == target));
            boundary.HoldProfile = true;
            Invoke("SaveProfileButton");
            PumpUntil(() => boundary.ProfileReturned.IsSet);
            var notificationsSettled = false;
            window.Dispatcher.BeginInvoke(() => notificationsSettled = true, DispatcherPriority.ContextIdle);
            PumpUntil(() => notificationsSettled);
            boundary.Query = target;
            boundary.HoldSearch = true;
            armEnter = true;
            boundary.ReleaseProfile.Set();
            PumpUntil(() => enterRequests == 1 && Volatile.Read(ref boundary.SearchEntries) >= 2);
            boundary.ReleaseSearch.Set();
            PumpUntil(() => gesture?.Completion.IsCompleted == true);
            if (Control<TextBlock>("Email").Text != target)
            {
                Console.WriteLine("COMPACT-OVERLAP-RED " + JsonSerializer.Serialize(new
                {
                    target, actual = Control<TextBlock>("Email").Text,
                    enterRequests, boundary.SearchEntries,
                    query = Control<TextBox>("SearchQuery").Text,
                    selected = (results.SelectedItem as PersonSearchResult)?.Person.Email,
                    profile = model.ProfileEdit?.Basis.Email, activity = Control<TextBlock>("WorkflowActivity").Text,
                    open.IsEnabled, model.LocalSave
                }));
                throw new InvalidOperationException("The completed single Enter dropped the exact selected Person during same-query refresh supersession.");
            }
            using var independent = OpenSmokeWorkspace(path);
            if (enterRequests != 1 || independent.GetRelationships("avery@example.com")!.Person.OfficeNumber !=
                "Committed before compact navigation")
                throw new InvalidOperationException("The same single Enter and independently committed caller profile must remain exact.");
            Console.WriteLine("PASS: one actual ListBox Enter activates the exact current Person across real successful profile-save/same-query refresh overlap; the caller profile independently reopened exactly.");
        }
        finally
        {
            boundary.ReleaseProfile.Set();
            boundary.ReleaseSearch.Set();
            descriptor.RemoveValueChanged(open, ready);
            PumpUntil(() => open.IsEnabled);
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The compact overlap replay cannot create a native window.");
            ClosePublishedCompiledWindow(window);
            boundary.DisposeGates();
        }
    }

    private enum CompactActivationCase { PendingSelection, QueryAwayAndBack, WorkspaceReplacement, BrowsingAwayAndBack }

    private static void VerifyCompactActivationCase(Type windowType, string root, CompactActivationCase scenario)
    {
        var path = Path.Combine(root, scenario.ToString());
        var replacement = Path.Combine(path, "replacement");
        var target = CompactIdentityStateCases[0].Email;
        var batch = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, batchId = Guid.NewGuid(), observedAt = "2026-11-01T17:00:00Z",
            people = CompactIdentityFixturePeople()
        });
        foreach (var workspace in scenario == CompactActivationCase.WorkspaceReplacement ? new[] { path, replacement } : [path])
        {
            using var seed = ApplicationWorkflow.CreateFileBacked();
            if (!seed.OpenWorkspace(workspace) || !seed.ApplyImport(seed.PreviewImport(RecoveryPeople).Id) ||
                !seed.ApplyImport(seed.PreviewImport(batch).Id))
                throw new InvalidOperationException(seed.Status);
        }
        var proxy = DispatchProxy.Create<IApplicationWorkflow, CompactWorkflowBoundary>();
        var boundary = (CompactWorkflowBoundary)proxy;
        boundary.Inner = ApplicationWorkflow.CreateFileBacked();
        using var store = proxy;
        var choices = new Queue<string>([path, replacement]);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => choices.Dequeue())], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var tree = Control<TreeView>("PeopleList");
        var model = (WorkspaceViewModel)tree.DataContext;
        var open = Control<Button>("OpenWorkspaceButton");
        var results = Control<ListBox>("SearchResults");
        var query = Control<TextBox>("SearchQuery");
        CompactGestureCompletion? gesture = null;
        try
        {
            Invoke("OpenSettingsButton");
            Invoke("OpenWorkspaceButton");
            PumpUntil(() => model.IsWorkspaceCurrent && open.IsEnabled);
            var avery = model.ReportingRoots.SelectMany(node => new[] { node }.Concat(node.Reports))
                .Single(node => node.Person.Email == "avery@example.com");
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, avery, TreeView.SelectedItemChangedEvent));
            if (scenario != CompactActivationCase.PendingSelection)
            {
                query.Text = target;
                PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == target));
                results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result => result.Person.Email == target);
            }
            var identity = model.Synchronization.WorkspaceId;
            boundary.Query = target;
            boundary.HoldSearch = true;
            if (scenario == CompactActivationCase.PendingSelection)
            {
                query.Text = target;
                PumpUntil(() => Volatile.Read(ref boundary.SearchEntries) == 1);
                if (results.SelectedItem is not null)
                    throw new InvalidOperationException("The pending ListBox Enter must start without a selected result.");
            }
            gesture = RaiseCompactSearchEnter(window, results);
            PumpUntil(() => Volatile.Read(ref boundary.SearchEntries) == 1);
            switch (scenario)
            {
                case CompactActivationCase.PendingSelection:
                    if (gesture.Completion.IsCompleted)
                        throw new InvalidOperationException("A no-selection Enter must await actual pending search publication.");
                    break;
                case CompactActivationCase.QueryAwayAndBack:
                    query.Text = "morgan@example.com";
                    query.Text = target;
                    break;
                case CompactActivationCase.WorkspaceReplacement:
                    Invoke("OpenSettingsButton");
                    Invoke("OpenWorkspaceButton");
                    PumpUntil(() => model.Compatibility.WorkspacePath == replacement && open.IsEnabled);
                    if (model.Synchronization.WorkspaceId == identity)
                        throw new InvalidOperationException("The delayed activation negative requires a genuinely different Workspace.");
                    break;
                case CompactActivationCase.BrowsingAwayAndBack:
                    if (!Control<Button>("Manager").IsEnabled)
                        throw new InvalidOperationException("The real Manager browsing action must remain usable during search revalidation.");
                    Invoke("Manager");
                    if (Control<TextBlock>("Email").Text != "morgan@example.com")
                        throw new InvalidOperationException("The actual Manager action did not establish intervening browsing.");
                    var reports = Control<ListBox>("ReportsList");
                    reports.SelectedItem = reports.Items.Cast<PersonProfile>().Single(person => person.Email == "avery@example.com");
                    if (Control<TextBlock>("Email").Text != "avery@example.com" || query.Text != target)
                        throw new InvalidOperationException("The actual report return must retain the identical search query before releasing the old activation.");
                    break;
            }
            var expected = scenario == CompactActivationCase.PendingSelection ? target : Control<TextBlock>("Email").Text;
            boundary.ReleaseSearch.Set();
            PumpUntil(() => gesture.Completion.IsCompleted);
            if (Control<TextBlock>("Email").Text != expected ||
                model.Compatibility.WorkspacePath != (scenario == CompactActivationCase.WorkspaceReplacement ? replacement : path))
                throw new InvalidOperationException($"A completed delayed Enter violated {scenario}.");
            Console.WriteLine(scenario == CompactActivationCase.PendingSelection
                ? "PASS: one actual no-selection ListBox Enter waits for pending publication, then freshly activates the exact Person."
                : $"PASS: actual single-Enter revalidation completes without overriding {scenario}; no replay or timing-based negative.");
        }
        finally
        {
            boundary.ReleaseProfile.Set();
            boundary.ReleaseSearch.Set();
            if (gesture is not null) PumpUntil(() => gesture.Completion.IsCompleted);
            PumpUntil(() => open.IsEnabled);
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Compact activation fences cannot create a native window.");
            ClosePublishedCompiledWindow(window);
            boundary.DisposeGates();
        }
    }

    private static CompactGestureCompletion RaiseCompactSearchEnter(Window window, ListBox results)
    {
        var previous = SynchronizationContext.Current ??
            throw new InvalidOperationException("The actual compiled Enter requires its dispatcher synchronization context.");
        var gesture = new CompactGestureCompletion(previous);
        SynchronizationContext.SetSynchronizationContext(gesture);
        try
        {
            results.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                new UnshownInputSource { RootVisual = (System.Windows.Media.Visual)window.Content }, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        if (gesture.Starts != 1)
            throw new InvalidOperationException("Exactly one actual asynchronous Enter handler must own the gesture completion.");
        return gesture;
    }

    private sealed class CompactGestureCompletion(SynchronizationContext dispatcher) : SynchronizationContext
    {
        // Observe the actual async-void routed handler ending, not a timed absence of navigation.
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pending;
        internal int Starts { get; private set; }
        internal Task Completion => completion.Task;
        public override void Post(SendOrPostCallback callback, object? state) => dispatcher.Post(callback, state);
        public override void Send(SendOrPostCallback callback, object? state) => dispatcher.Send(callback, state);
        public override void OperationStarted()
        {
            Starts++;
            Interlocked.Increment(ref pending);
            dispatcher.OperationStarted();
        }
        public override void OperationCompleted()
        {
            dispatcher.OperationCompleted();
            if (Interlocked.Decrement(ref pending) == 0) completion.TrySetResult();
        }
    }

    public class CompactWorkflowBoundary : DispatchProxy
    {
        private readonly object gateLock = new();
        private bool gatesClosed;
        private int activeGates;
        public IApplicationWorkflow Inner { get; set; } = null!;
        internal volatile bool HoldProfile, HoldSearch;
        internal string? Query;
        internal int SearchEntries;
        internal ManualResetEventSlim ProfileReturned { get; } = new();
        internal ManualResetEventSlim ReleaseProfile { get; } = new();
        internal ManualResetEventSlim ReleaseSearch { get; } = new();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result;
            try { result = targetMethod!.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            ManualResetEventSlim? release = null;
            lock (gateLock)
            {
                if (!gatesClosed && HoldProfile && targetMethod!.Name == nameof(IApplicationWorkflow.SaveProfileEdit) && result is true)
                {
                    HoldProfile = false;
                    ProfileReturned.Set();
                    release = ReleaseProfile;
                }
                else if (!gatesClosed && HoldSearch && targetMethod!.Name == nameof(IApplicationWorkflow.SearchPeople) && args?[0] as string == Query)
                {
                    Interlocked.Increment(ref SearchEntries);
                    release = ReleaseSearch;
                }
                if (release is not null) activeGates++;
            }
            if (release is not null)
            {
                try
                {
                    if (!release.Wait(TimeSpan.FromSeconds(15)))
                        throw new TimeoutException("The owning compact replay did not release its actual public workflow return.");
                }
                finally { lock (gateLock) activeGates--; }
            }
            return result;
        }
        internal void DisposeGates()
        {
            lock (gateLock) gatesClosed = true;
            ReleaseProfile.Set();
            ReleaseSearch.Set();
            PumpUntil(() => { lock (gateLock) return activeGates == 0; });
            ProfileReturned.Dispose();
            ReleaseProfile.Dispose();
            ReleaseSearch.Dispose();
        }
    }
}
