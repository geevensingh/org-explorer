using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyPreferredNameRetention(Type windowType, string settings,
        Func<Window, string?> chooseFolder)
    {
        VerifyProfileRetryOwner(windowType, settings);
        VerifyPreferredNameCancellations(windowType, settings, restoreFirstOwner: false);
        VerifyPreferredNameCancellations(windowType, settings, restoreFirstOwner: true);
        VerifyPreferredNameCleanReturn(windowType, settings);
        VerifyPreferredNameReturn(windowType, settings, chooseFolder, cancel: true, newerWhilePending: true, discardNewer: true);
        VerifyPreferredNameReturn(windowType, settings, chooseFolder, cancel: false, newerWhilePending: false);
        VerifyPreferredNameReturn(windowType, settings, chooseFolder, cancel: true, newerWhilePending: true);
        VerifyPreferredNameReturn(windowType, settings, chooseFolder, cancel: false, newerWhilePending: true);
        VerifyPreferredNameOriginalDuringReturn(windowType, settings);
        VerifyPreferredNameFailedDefaultReturn(windowType, settings, populatedBase: false);
        VerifyPreferredNameFailedDefaultReturn(windowType, settings, populatedBase: true);
    }

    private static void VerifyPreferredNameFailedDefaultReturn(Type windowType, string settings, bool populatedBase)
    {
        using var fixture = new PreferredRetentionWindow(windowType, Path.Combine(settings, "preferred-failure-" + populatedBase));
        if (populatedBase && !fixture.Model.EditPreferredName("avery@example.com", "Avery"))
            throw new InvalidOperationException(fixture.Model.Status);
        fixture.Select("avery@example.com");
        var basis = fixture.Model.GetRelationships("avery@example.com")!.Person;
        fixture.Click("EditPreferredNameButton");
        if (fixture.Control<TextBox>("PreferredName").Text != "Avery" ||
            fixture.Model.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName) is not null)
            throw new InvalidOperationException("Opening the Directory-name default must not retain writing.");
        using (var external = OpenSmokeWorkspace(fixture.Path))
            if (!external.EditPreferredName(basis.Email, "Delivered Preferred name"))
                throw new InvalidOperationException(external.Status);
        fixture.Observer.Hold("local-sqlite-read-snapshot");
        fixture.Click("SavePreferredNameButton");
        PumpUntil(() => fixture.Observer.Entered.IsSet);
        try
        {
            var pending = fixture.Model.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName);
            if (pending is null || pending.Basis != basis || pending.Value != "Avery" ||
                fixture.Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Visible ||
                fixture.Control<TextBox>("PreferredName").Text != "Avery")
                throw new InvalidOperationException("The untouched default was not retained before its admitted Save completed.");
        }
        finally { fixture.Observer.Release.Set(); }
        fixture.AwaitIdle();
        var failure = fixture.Model.LocalSaveError;
        if (failure is null || !failure.Contains("changed externally", StringComparison.Ordinal) ||
            fixture.Control<TextBlock>("PreferredNameError").Visibility != Visibility.Visible ||
            !fixture.Control<TextBlock>("PreferredNameErrorDetails").Text.Contains(failure, StringComparison.Ordinal))
            throw new InvalidOperationException("Saving the untouched default did not reject the changed Preferred-name base.");
        fixture.Select("casey@example.com");
        fixture.Select(basis.Email);
        var retained = fixture.Model.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName);
        if (fixture.Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Visible ||
            fixture.Control<TextBox>("PreferredName").Text != "Avery" ||
            retained is null || retained.Basis != basis || retained.Value != "Avery" ||
            retained.Error != failure ||
            fixture.Control<TextBlock>("PreferredNameError").Visibility != Visibility.Visible ||
            fixture.Control<TextBlock>("PreferredNameErrorDetails").Text != failure || fixture.Model.CanSafelyClose)
            throw new InvalidOperationException("A failed untouched-default Save lost its submitted text, original base or inline error after browsing away and back.");
        fixture.Click("SavePreferredNameButton");
        fixture.AwaitIdle();
        if (fixture.Model.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName)?.Basis != basis ||
            fixture.Control<TextBlock>("PreferredNameError").Visibility != Visibility.Visible)
            throw new InvalidOperationException("Retry replaced the failed default's original Preferred-name base.");
        using (var reading = OpenSmokeWorkspace(fixture.Path))
            if (reading.GetRelationships(basis.Email)!.Person.PreferredName != "Delivered Preferred name")
                throw new InvalidOperationException("A failed default submission or retry overwrote the external Preferred name.");
        fixture.Click("CancelPreferredNameButton");
        if (fixture.Model.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName) is not null ||
            fixture.Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Cancel did not release only the failed default entry.");
        Console.WriteLine($"PASS: failed untouched Directory-name default Save retains exact text, immutable base and inline error across browsing and stale retry; populatedBase={populatedBase}.");
    }

    private static void VerifyProfileRetryOwner(Type windowType, string settings)
    {
        foreach (var retry in new[] { "RetryProfileButton", "NoticeRetryButton" })
            VerifyProfileRetryOwner(windowType, settings, retry);
    }

    private static void VerifyProfileRetryOwner(Type windowType, string settings, string retry)
    {
        using var fixture = new PreferredRetentionWindow(windowType, Path.Combine(settings, "profile-retry-owner-" + retry));
        var secondary = new Dictionary<NoteScope, Guid>();
        foreach (var scope in Enum.GetValues<NoteScope>())
        {
            for (var index = 0; index < 2; index++)
            {
                var id = scope == NoteScope.Person
                    ? fixture.Model.CreatePersonNote("casey@example.com", $"Recovery Person {index}", $"Recovery Person body {index}")?.Id
                    : fixture.Model.CreateTeamNote("casey@example.com", $"Recovery Team {index}", $"Recovery Team body {index}")?.Id;
                secondary[scope] = id ?? throw new InvalidOperationException(fixture.Model.Status);
            }
        }
        fixture.Select("casey@example.com");
        fixture.Click("EditProfileButton");
        fixture.Control<TextBox>("OfficeNumber").Text = "B never submitted";
        var unsubmitted = fixture.Model.ProfileEdit!;
        fixture.Select("avery@example.com");
        fixture.Click("EditProfileButton");
        fixture.Control<TextBox>("OfficeNumber").Text = "A explicitly submitted";
        var submitted = fixture.Model.ProfileEdit!;
        using (WorkspaceFixtures.LockLocalStore(fixture.Path))
        {
            fixture.Click("SaveProfileButton");
            PumpUntil(() => fixture.Model.LocalSave.Phase == LocalSavePhase.Failed &&
                fixture.Control<Button>("RetryProfileButton").IsEnabled);
        }
        fixture.Observer.Hold("local-sqlite-read-snapshot");
        fixture.Click(retry);
        PumpUntil(() => fixture.Observer.Entered.IsSet);
        try
        {
            var content = (FrameworkElement)fixture.Window.Content;
            content.Measure(new Size(1100, 900));
            content.Arrange(new Rect(0, 0, 1100, 900));
            content.UpdateLayout();
            var inheritedSurface = fixture.Control<UserControl>("TeamNotes");
            var inherited = (Expander)inheritedSurface.FindName("CurrentTeamContext");
            if (!inheritedSurface.IsEnabled || !inherited.IsEnabled)
                throw new InvalidOperationException("Recovery disabled inherited Team-note browsing.");
            inherited.IsExpanded = true;
            var inheritedDocument = ((FlowDocumentScrollViewer)inheritedSurface.FindName("InheritedReading")).Document;
            if (fixture.Model.GetInheritedTeamNotes("avery@example.com").Count != 2 || inheritedDocument is null ||
                !new TextRange(inheritedDocument.ContentStart, inheritedDocument.ContentEnd).Text.Contains("Recovery Team body 1", StringComparison.Ordinal))
                throw new InvalidOperationException("Recovery lost exact inherited Team-note reading.");
            var query = fixture.Control<TextBox>("SearchQuery");
            query.Text = "casey@example.com";
            query.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                new UnshownInputSource { RootVisual = content }, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            var waiting = Stopwatch.StartNew();
            PumpUntil(() =>
            {
                if (waiting.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException("Person search stayed blocked during the same held reconciliation.");
                return fixture.Control<TextBlock>("Email").Text == "casey@example.com" && query.Text == "";
            });
            foreach (var scope in Enum.GetValues<NoteScope>())
            {
                var surface = fixture.Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes");
                T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
                if (!surface.IsEnabled || !Note<Expander>("OtherNotes").IsEnabled ||
                    !Note<ListBox>("NotesList").IsEnabled || !Note<TabItem>("ReadTab").IsEnabled)
                    throw new InvalidOperationException("Recovery disabled owned " + scope + "-note browsing.");
                Note<Expander>("OtherNotes").IsExpanded = true;
                Note<ListBox>("NotesList").SelectedItem = Note<ListBox>("NotesList").Items.Cast<OwnedNote>()
                    .Single(note => note.Id == secondary[scope]);
                Note<TabControl>("Views").SelectedItem = Note<TabItem>("ReadTab");
                var document = Note<FlowDocumentScrollViewer>("Reading").Document;
                if (fixture.Model.GetOwnedNotesView(scope).SelectedNote?.Id != secondary[scope] ||
                    document is null || !new TextRange(document.ContentStart, document.ContentEnd).Text
                        .Contains($"Recovery {scope} body 1", StringComparison.Ordinal))
                    throw new InvalidOperationException("Recovery did not present the exact selected " + scope + " note.");
                Note<TabControl>("Views").SelectedItem = Note<TabItem>("EditTab");
                if (Note<Button>("NewButton").IsEnabled || Note<Button>("ArchiveButton").IsEnabled ||
                    Note<TextBox>("NoteTitle").IsEnabled || Note<TextBox>("MarkdownEditor").IsEnabled)
                    throw new InvalidOperationException("Recovery enabled note mutation while its actual reconciliation remained held.");
                Note<TabControl>("Views").SelectedItem = Note<TabItem>("ReadTab");
            }
            if (!fixture.Observer.Active || fixture.Observer.Release.IsSet ||
                fixture.Control<Button>("OpenWorkspaceButton").IsEnabled ||
                fixture.Control<Button>("SaveProfileButton").IsEnabled)
                throw new InvalidOperationException("Recovery search must finish while reconciliation stays held and edit/replacement guards remain active.");
        }
        finally { fixture.Observer.Release.Set(); }
        fixture.AwaitIdle();
        using (var reading = OpenSmokeWorkspace(fixture.Path))
            if (fixture.Model.ProfileEdit != unsubmitted ||
                reading.GetRelationships("casey@example.com")!.Person != unsubmitted.Basis ||
                reading.GetRelationships("avery@example.com")!.Person != submitted.Basis)
                throw new InvalidOperationException("Profile Retry submitted another owner's retained draft after reconciliation.");
        fixture.Select("avery@example.com");
        if (fixture.Model.ProfileEdit != submitted)
            throw new InvalidOperationException("Navigation during Retry lost the explicitly submitted owner's retained session.");
        fixture.Click("RetryProfileButton");
        fixture.AwaitIdle();
        using (var reading = OpenSmokeWorkspace(fixture.Path))
            if (reading.GetRelationships("avery@example.com")!.Person != submitted.Draft ||
                reading.GetRelationships("casey@example.com")!.Person != unsubmitted.Basis)
                throw new InvalidOperationException("The same owner's explicit Retry did not save only its captured complete profile.");
        Console.WriteLine("PASS: actual compiled " + retry +
            " permits search during held reconciliation, cannot submit B, and retains A until its own explicit Retry.");
    }

    private static void VerifyPreferredNameCancellations(Type windowType, string settings, bool restoreFirstOwner)
    {
        using var fixture = new PreferredRetentionWindow(windowType,
            Path.Combine(settings, "preferred-cancel-owners-" + restoreFirstOwner));
        foreach (var owner in new[] { "avery@example.com", "casey@example.com" })
        {
            fixture.Select(owner);
            fixture.Click("EditPreferredNameButton");
            fixture.Control<TextBox>("PreferredName").Text = "Unsubmitted " + owner;
        }
        fixture.Observer.Hold("local-sqlite-commit");
        fixture.Click("FavoriteButton");
        PumpUntil(() => fixture.Observer.Entered.IsSet);
        fixture.Select("avery@example.com");
        fixture.Click("CancelPreferredNameButton");
        fixture.Select("casey@example.com");
        fixture.Click("CancelPreferredNameButton");
        if (restoreFirstOwner)
            fixture.Select("avery@example.com");
        fixture.Observer.Release.Set();
        fixture.AwaitIdle();
        if (fixture.Model.GetPersonTextDraft("casey@example.com", PersonTextField.PreferredName) is not null ||
            (restoreFirstOwner
                ? fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName)?.Value != "Unsubmitted avery@example.com"
                : fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not null) ||
            fixture.Model.CanSafelyClose == restoreFirstOwner)
            throw new InvalidOperationException("Deferred Preferred-name cancellation lost an owner or cleared its newer generation.");
        if (restoreFirstOwner)
        {
            var editor = fixture.Control<TextBox>("PreferredName");
            if (!editor.IsEnabled) throw new InvalidOperationException("The restored owner's input did not become writable.");
            editor.Text = "Newer A generation";
            if (fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName)?.Value != editor.Text)
                throw new InvalidOperationException("The restored owner's newer generation was not retained.");
        }
        using (var reading = OpenSmokeWorkspace(fixture.Path))
            if (reading.People.Any(person => person.PreferredName is not null) ||
                !reading.GetRelationships("casey@example.com")!.Person.IsFavorite)
                throw new InvalidOperationException("Preferred-name Cancel submitted writing or revoked the unrelated admitted Favorite.");
        Console.WriteLine($"PASS: actual compiled deferred Cancels retain every owner and protect restored newer writing; restoredA={restoreFirstOwner}.");
    }

    private static void VerifyPreferredNameCleanReturn(Type windowType, string settings)
    {
        using var fixture = new PreferredRetentionWindow(windowType, Path.Combine(settings, "preferred-clean-return"));
        fixture.Select("avery@example.com");
        fixture.Click("EditPreferredNameButton");
        if (fixture.Control<TextBox>("PreferredName").Text != "Avery" ||
            !fixture.Control<Button>("SavePreferredNameButton").IsEnabled ||
            !fixture.Control<Button>("CancelPreferredNameButton").IsEnabled || !fixture.Model.CanSafelyClose)
            throw new InvalidOperationException("A fresh Preferred-name entry must show the Directory name without retaining or submitting it.");
        fixture.Control<TextBox>("PreferredName").Text = "Changed but not submitted";
        fixture.Control<TextBox>("PreferredName").Text = "";
        fixture.Select("casey@example.com");
        fixture.Select("avery@example.com");
        if (!fixture.Model.CanSafelyClose || !fixture.Model.CanEnterImport ||
            fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not null ||
            fixture.Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Changing then restoring the original Preferred name retained false unsaved writing after browsing.");
        var editor = fixture.Control<TextBox>("PreferredName");
        var panel = fixture.Control<FrameworkElement>("PreferredNameEditorPanel");
        (string? Directory, string? Preferred, string Displayed, string? Saved)[] defaults =
        [
            ("Avery", null, "Avery", null),
            ("Avery", "  My AVERY  ", "  My AVERY  ", "  My AVERY  "),
            (" Avery ", "aVeRy", "aVeRy", null),
            (null, null, "", null),
            (null, "My Avery", "My Avery", "My Avery")
        ];
        foreach (var (directory, preferred, displayed, saved) in defaults)
        {
            if (!fixture.Model.EditPersonText("avery@example.com", PersonTextField.DirectoryName, directory) ||
                !fixture.Model.EditPreferredName("avery@example.com", preferred))
                throw new InvalidOperationException(fixture.Model.Status);
            fixture.Select("casey@example.com");
            fixture.Select("avery@example.com");
            var basis = fixture.Model.GetRelationships("avery@example.com")!.Person;
            fixture.Click("EditPreferredNameButton");
            if (editor.Text != displayed || !editor.IsEnabled ||
                !fixture.Control<Button>("SavePreferredNameButton").IsEnabled ||
                !fixture.Control<Button>("CancelPreferredNameButton").IsEnabled ||
                fixture.Model.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName) is not null ||
                !fixture.Model.CanSafelyClose)
                throw new InvalidOperationException(
                    $"Fresh entry did not preserve Preferred/Directory/empty precedence without retaining writing. " +
                    $"Directory='{directory}', Preferred='{preferred}', expected='{displayed}', actual='{editor.Text}', " +
                    $"input={editor.IsEnabled}, Save={fixture.Control<Button>("SavePreferredNameButton").IsEnabled}, " +
                    $"Cancel={fixture.Control<Button>("CancelPreferredNameButton").IsEnabled}, safeClose={fixture.Model.CanSafelyClose}, " +
                    $"draft={fixture.Model.GetPersonTextDraft(basis.Email, PersonTextField.PreferredName)}, status={fixture.Model.Status}");
            using (var reading = OpenSmokeWorkspace(fixture.Path))
                if (reading.GetRelationships(basis.Email)!.Person != basis)
                    throw new InvalidOperationException("Opening the default submitted or cleared a saved name.");
            fixture.Click("CancelPreferredNameButton");
            using (var reading = OpenSmokeWorkspace(fixture.Path))
                if (reading.GetRelationships(basis.Email)!.Person != basis || panel.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Cancelling the untouched default changed saved information.");
            fixture.Click("EditPreferredNameButton");
            fixture.Click("SavePreferredNameButton");
            fixture.AwaitIdle();
            using (var reading = OpenSmokeWorkspace(fixture.Path))
                if (reading.GetRelationships(basis.Email)!.Person != basis with { PreferredName = saved } ||
                    panel.Visibility != Visibility.Collapsed || !fixture.Model.CanSafelyClose)
                    throw new InvalidOperationException("Explicit default Save did not durably preserve or clear the expected name.");
        }
        fixture.Click("EditPreferredNameButton");
        editor.Text = "  Exact retained writing  ";
        var retained = fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName)!;
        fixture.Select("casey@example.com");
        if (!fixture.Model.EditPersonText("avery@example.com", PersonTextField.DirectoryName, "Changed Directory name"))
            throw new InvalidOperationException(fixture.Model.Status);
        fixture.Select("avery@example.com");
        if (editor.Text != "  Exact retained writing  " ||
            fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) != retained ||
            !fixture.Control<Button>("SavePreferredNameButton").IsEnabled ||
            !fixture.Control<Button>("CancelPreferredNameButton").IsEnabled)
            throw new InvalidOperationException("A newer Directory name replaced retained text, owner or its original immutable base.");
        fixture.Click("CancelPreferredNameButton");
        Console.WriteLine("PASS: actual compiled clean Preferred-name return retires unsaved retention without Save or Cancel.");
        Console.WriteLine("PASS: compiled Preferred/Directory/empty defaults, untouched Cancel and explicit Save reopen durably; Directory changes never reseed retained writing.");
    }

    private static void VerifyPreferredNameOriginalDuringReturn(Type windowType, string settings)
    {
        using var fixture = new PreferredRetentionWindow(windowType, Path.Combine(settings, "preferred-original-return"));
        fixture.Select("avery@example.com");
        fixture.Click("EditPreferredNameButton");
        var editor = fixture.Control<TextBox>("PreferredName");
        editor.Text = "First admitted name";
        var queued = 0;
        var typed = false;
        PropertyChangedEventHandler retypeOriginal = (_, e) =>
        {
            if (e.PropertyName != nameof(WorkspaceViewModel.People) ||
                fixture.Model.GetRelationships("avery@example.com")?.Person.PreferredName != "First admitted name" ||
                fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not null ||
                Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            fixture.Window.Dispatcher.BeginInvoke(() =>
            {
                if (!editor.IsEnabled) throw new InvalidOperationException("The admitted Preferred-name entry is not writable.");
                editor.Text = "";
                fixture.Select("casey@example.com");
                fixture.Select("avery@example.com");
                typed = true;
            }, System.Windows.Threading.DispatcherPriority.Send);
        };
        fixture.Model.PropertyChanged += retypeOriginal;
        try
        {
            fixture.Observer.Hold("local-sqlite-commit");
            fixture.Click("SavePreferredNameButton");
            PumpUntil(() => fixture.Observer.Entered.IsSet);
            fixture.Observer.Release.Set();
            fixture.AwaitIdle();
            if (!typed || editor.Text != "" ||
                fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not
                    { Value: null, Basis.PreferredName: "First admitted name", Error: null } ||
                fixture.Model.CanSafelyClose)
                throw new InvalidOperationException("Retyping the original value before the compiled Save acknowledgment lost newer writing or its accepted base.");
            using (var reading = OpenSmokeWorkspace(fixture.Path))
                if (reading.GetRelationships("avery@example.com")!.Person.PreferredName != "First admitted name")
                    throw new InvalidOperationException("An earlier Save implicitly submitted the retyped original value.");
            fixture.Click("SavePreferredNameButton");
            fixture.AwaitIdle();
            using var reopened = OpenSmokeWorkspace(fixture.Path);
            if (reopened.GetRelationships("avery@example.com")!.Person.PreferredName is not null ||
                fixture.Model.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not null ||
                !fixture.Model.CanSafelyClose)
                throw new InvalidOperationException("The retyped original value did not save explicitly against its accepted base.");
        }
        finally { fixture.Model.PropertyChanged -= retypeOriginal; }
        Console.WriteLine("PASS: actual compiled original-value typing and owner browsing before Save acknowledgment remain retained until their own explicit Save.");
    }

    private sealed class PreferredRetentionWindow : IDisposable
    {
        internal string Path { get; }
        internal Window Window { get; }
        internal WorkspaceViewModel Model { get; }
        internal PreferredRetentionObserver Observer { get; } = new();

        internal PreferredRetentionWindow(Type windowType, string path)
        {
            Path = path;
            using (var seed = ApplicationWorkflow.CreateFileBacked())
                if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport("""
                    {"schemaVersion":1,"batchId":"95000000-0000-4000-8000-000000000093",
                     "observedAt":"2026-11-01T17:00:00Z","people":[
                     {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com"},
                     {"email":"casey@example.com","displayName":"Casey","managerEmail":null}]}
                    """).Id))
                    throw new InvalidOperationException(seed.Status);
            Window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [ApplicationWorkflow.CreateFileBacked(fileObserver: Observer), (Func<Window, string?>)(_ => path)], null)!;
            Model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            Click("OpenSettingsButton");
            Click("OpenWorkspaceButton");
            PumpUntil(() => Model.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
        }

        internal T Control<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);
        internal void Click(string name)
        {
            var button = Control<Button>(name);
            if (!button.IsEnabled) throw new InvalidOperationException(name + " is not enabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        internal void AwaitIdle() => PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
        internal void Select(string owner)
        {
            var tree = Control<TreeView>("PeopleList");
            if (!tree.IsEnabled || !Model.CanNavigatePeople)
                throw new InvalidOperationException("Public Person browsing is disabled.");
            var node = Model.ReportingRoots.SelectMany(root => new[] { root }.Concat(root.Reports))
                .Single(item => item.Person.Email == owner);
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
            if (Control<TextBlock>("Email").Text != owner)
                throw new InvalidOperationException("Public Person selection did not reach " + owner);
        }
        public void Dispose()
        {
            Observer.Release.Set();
            AwaitIdle();
            if (Model.LocalSave.EditsPaused) Model.ReconcileLocalSaves();
            foreach (var owner in Model.People.Select(person => person.Email))
            {
                if (Model.GetPersonTextDraft(owner, PersonTextField.PreferredName) is { } draft)
                    Model.DiscardPersonTextDraft(draft.Basis, PersonTextField.PreferredName);
                Model.SelectProfilePerson(owner);
                Model.CancelProfileEdit();
            }
            if (Window.IsVisible || new WindowInteropHelper(Window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Preferred retention contracts created a native or visible window.");
            ClosePublishedCompiledWindow(Window);
            Observer.Dispose();
        }
    }

    private sealed class PreferredRetentionObserver : IWorkspaceFileObserver, IDisposable
    {
        private string? heldPhase;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal volatile bool Active;
        internal void Hold(string phase) => heldPhase = phase;
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Kind != WorkspaceStoragePhaseKind.Begin || phase.Name != Volatile.Read(ref heldPhase)) return;
            Interlocked.Exchange(ref heldPhase, null);
            Active = true;
            Entered.Set();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The fixture did not release its held real storage phase.");
            }
            finally { Active = false; }
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private static void VerifyPreferredNameReturn(Type windowType, string settings,
        Func<Window, string?> chooseFolder, bool cancel, bool newerWhilePending, bool discardNewer = false)
    {
        var path = Path.Combine(settings, $"preferred-name-return-{cancel}-{newerWhilePending}-{discardNewer}");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"95000000-0000-4000-8000-000000000092",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com"},
                 {"email":"casey@example.com","displayName":"Casey","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Preferred-name fixture failed: " + seed.Status);
        using var publisher = new HeldNotePublisher();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(fileObserver: publisher), chooseFolder], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var tree = Control<TreeView>("PeopleList");
        var workflow = (WorkspaceViewModel)tree.DataContext;
        var editor = Control<TextBox>("PreferredName");
        var panel = Control<FrameworkElement>("PreferredNameEditorPanel");
        void Invoke(string name)
        {
            var button = Control<Button>(name);
            if (!button.IsEnabled) throw new InvalidOperationException(name + " is not enabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void Select(string email)
        {
            if (!tree.IsEnabled || !workflow.CanNavigatePeople)
                throw new InvalidOperationException("Ordinary Person browsing is disabled during the admitted save.");
            var node = workflow.ReportingRoots.SelectMany(root => new[] { root }.Concat(root.Reports))
                .Single(item => item.Person.Email == email);
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
            if (Control<TextBlock>("Email").Text != email)
                throw new InvalidOperationException("The compiled selection did not display " + email);
        }
        try
        {
            Invoke("OpenSettingsButton");
            ChooseCompiledWorkspace(window, path);
            PumpUntil(() => workflow.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
            Select("avery@example.com");
            Invoke("EditPreferredNameButton");
            editor.Text = "First admitted name";
            publisher.HoldNextProfile = true;
            Invoke("SavePreferredNameButton");
            PumpUntil(() => publisher.Entered.IsSet);
            if (cancel)
            {
                Invoke("CancelPreferredNameButton");
                if (panel.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Cancel did not immediately close the admitted entry.");
            }
            Select("casey@example.com");
            Select("avery@example.com");
            if (panel.Visibility != Visibility.Visible || !editor.IsEnabled || editor.Text != "First admitted name")
                throw new InvalidOperationException("Returning before completion did not restore an enabled retained entry.");
            if (newerWhilePending)
            {
                editor.Text = "Newer restored writing";
                if (workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName)?.Value != "Newer restored writing")
                    throw new InvalidOperationException("Typing did not retain the restored owner's exact newer writing.");
            }
            publisher.Release.Set();
            PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
            using (var reading = OpenSmokeWorkspace(path))
                if (reading.GetRelationships("avery@example.com")!.Person.PreferredName != "First admitted name" ||
                    reading.GetRelationships("casey@example.com")!.Person.PreferredName is not null)
                    throw new InvalidOperationException("The admitted save did not durably affect only its actual owner.");
            if (newerWhilePending)
            {
                if (panel.Visibility != Visibility.Visible || editor.Text != "Newer restored writing" ||
                    workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not
                        { Value: "Newer restored writing", Basis.PreferredName: "First admitted name", Error: null } ||
                    workflow.CanSafelyClose)
                    throw new InvalidOperationException("An older save's deferred Cancel discarded the restored owner's newer writing.");
            }
            else if (panel.Visibility != Visibility.Visible || editor.Text != "First admitted name" || !editor.IsEnabled)
                throw new InvalidOperationException("Save completion replaced or disabled the restored owner entry.");
            if (discardNewer)
            {
                Invoke("CancelPreferredNameButton");
            }
            else
            {
                if (!newerWhilePending) editor.Text = "Newer explicit name";
                Invoke("SavePreferredNameButton");
                PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
            }
            var expected = discardNewer ? "First admitted name" :
                newerWhilePending ? "Newer restored writing" : "Newer explicit name";
            using (var reading = OpenSmokeWorkspace(path))
                if (reading.GetRelationships("avery@example.com")!.Person.PreferredName != expected ||
                    reading.GetRelationships("casey@example.com")!.Person.PreferredName is not null ||
                    panel.Visibility != Visibility.Collapsed || !workflow.CanSafelyClose)
                    throw new InvalidOperationException(discardNewer
                        ? "The restored entry's own Cancel did not discard only its newer writing: " + workflow.Status
                        : "The restored owner's next Save falsely rejected its own accepted base: " + workflow.LocalSaveError);
            if (!cancel && !newerWhilePending)
            {
                Invoke("EditPreferredNameButton");
                editor.Text = "  aVeRy  ";
                using (var external = OpenSmokeWorkspace(path))
                    if (!external.EditPreferredName("avery@example.com", "Genuinely external name"))
                        throw new InvalidOperationException(external.Status);
                Invoke("SavePreferredNameButton");
                PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
                if (panel.Visibility != Visibility.Visible || editor.Text != "  aVeRy  " ||
                    workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not
                        { Value: "  aVeRy  ", Basis.PreferredName: "Newer explicit name", Error: { } error } ||
                    !error.Contains("changed externally", StringComparison.Ordinal) ||
                    Control<TextBlock>("PreferredNameError").Visibility != Visibility.Visible || workflow.CanSafelyClose)
                    throw new InvalidOperationException("A genuinely changed external base was accepted or its rejected writing/error was lost.");
                using (var reading = OpenSmokeWorkspace(path))
                    if (reading.GetRelationships("avery@example.com")!.Person.PreferredName != "Genuinely external name")
                        throw new InvalidOperationException("A rejected stale entry overwrote the genuine external name.");
                Invoke("CancelPreferredNameButton");
                if (panel.Visibility != Visibility.Collapsed || !workflow.CanSafelyClose)
                    throw new InvalidOperationException("Explicit Cancel did not release the rejected stale entry.");
                Console.WriteLine("PASS: compiled Save still rejects a genuinely changed external Preferred-name base, retains exact writing/error and preserves independently reopened external data.");
            }
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Preferred-name retention created a native window.");
            Console.WriteLine($"PASS: compiled pending Preferred-name A-B-A return; earlierCancel={cancel}; " +
                $"newerWritingWhilePending={newerWhilePending}; ownAction={(discardNewer ? "Cancel" : "Save")}; " +
                "exact accepted base, retained text and independently reopened owner values.");
        }
        finally
        {
            publisher.Release.Set();
            PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
            if (panel.Visibility == Visibility.Visible) Invoke("CancelPreferredNameButton");
            if (workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is { } retained)
                workflow.DiscardPersonTextDraft(retained.Basis, PersonTextField.PreferredName);
            ClosePublishedCompiledWindow(window);
        }
    }
}
