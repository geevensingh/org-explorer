using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyPersonSearchIntegration(Type windowType, string root) =>
        VerifyPersonSearchIntegration(windowType, root, retainedOnly: false);

    private static void VerifyPersonSearchIntegration(Type windowType, string root, bool retainedOnly)
    {
        var index = 0;
        foreach (var verify in retainedOnly
            ? new Action<Window, string>[] { VerifyRetainedOverlayGeometry }
            : [VerifyPeopleSearchRestoration, VerifyRetainedOverlayGeometry])
        {
            var settings = Path.Combine(root, "s" + index++);
            Directory.CreateDirectory(settings);
            var previous = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
            var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [ApplicationWorkflow.CreateFileBacked(),
                    (Func<Window, string?>)(_ => CompiledWorkspaceChoices.Dequeue()),
                    (Action<string>)(_ => { }), new SnapshotClock()], null)!;
            var completed = false;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                verify(window, settings);
                if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                    throw new InvalidOperationException("People-search integration cannot create a native window.");
                completed = true;
            }
            finally
            {
                if (completed)
                {
                    ClosePublishedCompiledWindow(window);
                    PumpUntil(() => closed);
                }
                else
                {
                    window.Close();
                    ((Button)window.FindName("CancelExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext).Dispose();
                }
                Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previous);
            }
        }
        Console.WriteLine("PASS: existing actual compiled grouped/highlight/pending-Enter/current-row/manual-clear/history/retained-overlay checks run on real SQLite without native keyboard/focus acceptance.");
    }

    private static void VerifyCurrentSearchStorage(Type windowType, string root)
    {
        var path = Path.Combine(root, "cs");
        using var barrier = new NoteAcknowledgmentCommitBarrier();
        using var store = ApplicationWorkflow.CreateFileBacked(fileObserver: barrier);
        if (!store.OpenWorkspace(path) || !store.ApplyImport(store.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000111",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":"manager@example.com","officeNumber":"001A"},
             {"email":"manager@example.com","displayName":"Manager","managerEmail":null}]}
            """).Id)) throw new InvalidOperationException(store.Status);
        var notes = new Dictionary<NoteScope, (Guid Ongoing, Guid Secondary)>();
        foreach (var scope in Enum.GetValues<NoteScope>())
        {
             var ongoing = scope == NoteScope.Person
                 ? store.CreatePersonNote("owner@example.com", "Continuing", "Ordinary saved writing")!.Id
                 : store.CreateTeamNote("owner@example.com", "Continuing", "Ordinary saved writing")!.Id;
             var secondary = scope == NoteScope.Person
                 ? store.CreatePersonNote("owner@example.com", "Secondary", "Original secondary writing")!.Id
                 : store.CreateTeamNote("owner@example.com", "Secondary", "Original secondary writing")!.Id;
             notes[scope] = (ongoing, secondary);
        }
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => path), (Action<string>)(_ => { }), new SnapshotClock()], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1100, 900));
        content.Arrange(new Rect(0, 0, 1100, 900));
        content.UpdateLayout();
        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, model.ReportingRoots.Single().Reports.Single(), TreeView.SelectedItemChangedEvent));
        Invoke("EditProfileButton");
        var basis = model.ProfileEdit!.Basis;
        Control<TextBox>("OfficeNumber").Text = "Submitted owner writing";
        barrier.Armed = true;
        try
        {
            Invoke("SaveProfileButton");
            PumpUntil(() => barrier.Entered.IsSet);
            Control<TextBox>("OfficeNumber").Text = "Newer retained owner writing";
            void EnterPending(string query, string email)
            {
                Control<TextBox>("SearchQuery").Text = query;
                var key = new KeyEventArgs(Keyboard.PrimaryDevice,
                    new UnshownInputSource { RootVisual = content }, 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Control<TextBox>("SearchQuery").RaiseEvent(key);
                try
                {
                    PumpUntil(() => Control<TextBlock>("Email").Text == email &&
                        Control<TextBox>("SearchQuery").Text == "");
                }
                catch (TimeoutException error)
                {
                    throw new InvalidOperationException("Current single-Enter search did not activate during the same real held profile transaction: " +
                        $"handled={key.Handled}; owner={model.PeopleNavigation.SelectedEmail}; query={model.PeopleNavigation.Query}; " +
                        $"highlight={model.PeopleNavigation.HighlightedResult?.Person.Email}; local={model.LocalSave}; " +
                        $"stillHeld={barrier.Entered.IsSet && !barrier.Release.IsSet}.", error);
                }
                if (barrier.Release.IsSet || model.LocalSave.OutstandingRequests == 0)
                    throw new InvalidOperationException("Search only became usable after the held ordinary Save completed.");
            }
            EnterPending("manager@example.com", "manager@example.com");
            EnterPending("owner@example.com", "owner@example.com");
            if (!ReferenceEquals(model.ProfileEdit?.Basis, basis) ||
                Control<TextBox>("OfficeNumber").Text != "Newer retained owner writing")
                throw new InvalidOperationException("Current grouped search rebased or lost the exact in-flight owner's newer profile writing.");
            using var unchanged = OpenSmokeWorkspace(path);
            if (unchanged.GetRelationships("owner@example.com")?.Person.OfficeNumber != "001A")
                throw new InvalidOperationException("The search-held negative did not preserve original pre-Commit data.");
        }
        finally { barrier.Armed = false; barrier.Release.Set(); }
        PumpUntil(() => model.LocalSave.OutstandingRequests == 0 && Control<Button>("CancelProfileButton").IsEnabled);
        using (var independent = OpenSmokeWorkspace(path))
            if (independent.GetRelationships("owner@example.com")?.Person.OfficeNumber != "Submitted owner writing" ||
                Control<TextBox>("OfficeNumber").Text != "Newer retained owner writing" || model.CanSafelyClose)
                throw new InvalidOperationException("Current search confused submitted durable writing with the retained newer owner generation.");
        Invoke("CancelProfileButton");
        foreach (var scope in Enum.GetValues<NoteScope>())
        {
            var surface = Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes");
            T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
            if (!model.DesignateOngoingNote(scope, notes[scope].Ongoing))
                throw new InvalidOperationException(model.Status);
            Note<Expander>("OtherNotes").IsExpanded = true;
            Note<ListBox>("NotesList").SelectedItem = Note<ListBox>("NotesList").Items.Cast<OwnedNote>()
                .Single(item => item.Id == notes[scope].Secondary);
            Note<TabControl>("Views").SelectedItem = Note<TabItem>("EditTab");
            Note<TextBox>("NoteTitle").Text = "";
            Note<TextBox>("MarkdownEditor").Text = $"Exact retained {scope} writing";
            PumpUntil(() => model.GetRetainedNoteDraft(scope, notes[scope].Secondary)?.Error is not null);
            var retained = model.GetRetainedNoteDraft(scope, notes[scope].Secondary)!;
            foreach (var destination in new[] { "manager@example.com", "owner@example.com", "owner@example.com" })
            {
                Control<TextBox>("SearchQuery").Text = destination;
                Control<TextBox>("SearchQuery").RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                    new UnshownInputSource { RootVisual = content }, 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                PumpUntil(() => Control<TextBlock>("Email").Text == destination &&
                    Control<TextBox>("SearchQuery").Text == "");
            }
            if (model.GetOwnedNotesView(scope).SelectedNote?.Id != notes[scope].Secondary ||
                Note<TabControl>("Views").SelectedItem != Note<TabItem>("EditTab") ||
                Note<TextBox>("NoteTitle").Text != "" || Note<TextBox>("MarkdownEditor").Text != retained.Markdown ||
                !ReferenceEquals(model.GetRetainedNoteDraft(scope, notes[scope].Secondary)?.Basis, retained.Basis) ||
                model.GetRetainedNoteDraft(scope, notes[scope].Secondary)?.Error != retained.Error)
                throw new InvalidOperationException("Actual current/same-owner grouped activation replaced retained secondary note identity, view, writing, failure or immutable base.");
            using (var independent = OpenSmokeWorkspace(path))
                if (independent.BeginNoteDraft(scope, "owner@example.com", notes[scope].Secondary)?.Markdown != "Original secondary writing")
                    throw new InvalidOperationException("Search highlighting/activation silently committed retained note writing.");
            Note<Button>("DiscardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "cst")) || !model.SyncNow())
            throw new InvalidOperationException(model.Status);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Current search/storage verification cannot create a native window.");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
        Console.WriteLine("PASS: actual grouped/background single-Enter search browses during held SQLite Save and both-scope retained secondary failures, preserving exact identities/views/errors/bases, submitted versus newer writing and actual normal closure. No native keyboard/focus acceptance claimed.");
    }
}
