using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyCurrent1eeeNoteContracts(Type windowType, string root)
    {
        var failures = new List<string>();
        using (var fixture = new OrdinaryFixtureWindow(windowType, Path.Combine(root, "exact-owner-return")))
        {
            const NoteScope scope = NoteScope.Team;
            fixture.Select("morgan@example.com");
            fixture.NoteButton(scope, "NewButton");
            var ongoing = fixture.Model.TeamNotesView.SelectedNote!.Id;
            fixture.Note<TextBox>(scope, "NoteTitle").Text = "Original ongoing";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, ongoing) is null && fixture.Model.CanSafelyClose);
            fixture.NoteButton(scope, "DesignateButton");
            PumpUntil(() => fixture.Model.TeamNotesView.OngoingNoteId == ongoing);
            fixture.NoteButton(scope, "NewButton");
            var secondary = fixture.Model.TeamNotesView.SelectedNote!.Id;
            fixture.Note<TextBox>(scope, "NoteTitle").Text = "Exact secondary";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, secondary) is null && fixture.Model.CanSafelyClose);
            fixture.NoteButton(scope, "OngoingButton");
            fixture.Note<Expander>(scope, "OtherNotes").IsExpanded = true;
            fixture.Note<ListBox>(scope, "NotesList").SelectedItem =
                fixture.Note<ListBox>(scope, "NotesList").Items.Cast<OwnedNote>().Single(note => note.Id == secondary);
            fixture.Note<TabItem>(scope, "ReadTab").IsSelected = true;
            if (fixture.Model.TeamNotesView.SelectedNote?.Id != secondary)
                throw new InvalidOperationException("The exact secondary must be explicitly selected before testing owner return.");
            var revision = fixture.Model.Synchronization.LocalRevision;
            var saved = fixture.ReadSavedGraph();
            fixture.Select("avery@example.com");
            fixture.Select("morgan@example.com");
            fixture.Capture("1eee-clean-exact-owner-return", scope);
            if (fixture.Model.TeamNotesView.SelectedNote?.Id != secondary || fixture.Model.TeamNotesView.IsEditing ||
                fixture.Model.Synchronization.LocalRevision != revision || fixture.ReadSavedGraph() != saved)
                throw new InvalidOperationException("Clean exact owner return changed saved data, identity or Read mode.");
            fixture.NoteButton(scope, "OngoingButton");
            if (fixture.Model.TeamNotesView.SelectedNote?.Id != ongoing || fixture.Model.TeamNotesView.OtherNotesOpen ||
                fixture.Model.TeamNotesView.IsEditing || fixture.Model.Synchronization.LocalRevision != revision ||
                fixture.ReadSavedGraph() != saved)
                failures.Add("Explicit public ongoing browsing failed to preserve exact saved data and Read mode.");
        }
        Verify1eeeHeldExportEditor(windowType, root, failures);
        VerifyCopyNoteFailurePreparation(windowType, root, failures);
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS: current exact-owner selection and committed-return Read/explicit Edit preserve full saved state, immutable base and the same first actual held publisher; no native geometry acceptance.");
    }

    private static void VerifyCopyNoteFailurePreparation(Type windowType, string root, List<string> failures)
    {
        var path = Path.Combine(root, "copy-note-failure-preparation");
        Guid id;
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport(RecoveryPeople).Id))
                throw new InvalidOperationException(seed.Status);
            id = seed.CreatePersonNote("avery@example.com", "Saved copy context", "# Exact saved copy context")!.Id;
        }
        using var fixture = new OrdinaryFixtureWindow(windowType, path);
        fixture.Select("avery@example.com");
        fixture.Select("morgan@example.com");
        fixture.Select("avery@example.com");
        if (fixture.Model.PersonNotesView.IsEditing ||
            fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text.Length != 0)
            throw new InvalidOperationException("The copy failure replay must begin with clean Read and an unrealized empty hidden title.");
        var saved = fixture.ReadSavedGraph();
        fixture.Note<TabItem>(NoteScope.Person, "EditTab").IsSelected = true;
        fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text = "";
        var flush = fixture.Model.FlushNoteDraftsAsync();
        PumpUntil(() => flush.IsCompleted);
        if (flush.GetAwaiter().GetResult() ||
            fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id) is not { IsSaving: false, Error: not null } retained ||
            retained.Basis.OwnerEmail != "avery@example.com" || retained.Basis.Markdown != "# Exact saved copy context" ||
            retained.Title != "" || retained.Markdown != "# Exact saved copy context" || fixture.ReadSavedGraph() != saved)
            failures.Add("Copy failure preparation did not admit its intended exact invalid-title Edit; writing the empty hidden Read editor was a no-op.");
    }

    private static void Verify1eeeHeldExportEditor(Type windowType, string root, List<string> failures)
    {
        var path = Path.Combine(root, "held-owner-return");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var ended = new ManualResetEventSlim();
        var calls = 0;
        var publisher = new AutomaticSnapshotPublicationGate(() => true,
            () => { Interlocked.Increment(ref calls); entered.Set(); },
            () => release.Wait(TimeSpan.FromSeconds(30)), () => ended.Set());
        using var store = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher);
        if (!store.OpenWorkspace(path) || !store.ApplyImport(store.PreviewImport(RecoveryPeople).Id))
            throw new InvalidOperationException(store.Status);
        var note = store.CreateTeamNote("morgan@example.com", "Automatic publication owner", "Before automatic export")!;
        var transport = Path.Combine(root, "held-transport");
        if (!store.ConfigureSynchronizationFolder(transport))
            throw new InvalidOperationException(store.Status);
        var window = CreateAutomaticSnapshotWindow(windowType, store, path, new SnapshotClock());
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var surface = Control<UserControl>("TeamNotes");
        T Team<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        void Arrange()
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1100, 900));
            content.Arrange(new Rect(0, 0, 1100, 900));
            content.UpdateLayout();
        }
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        void Select(string email) => Control<TreeView>("PeopleList").RaiseEvent(
            new RoutedPropertyChangedEventArgs<object>(null!,
                model.ReportingRoots.SelectMany(node => new[] { node }.Concat(node.Reports)).Single(node => node.Person.Email == email),
                TreeView.SelectedItemChangedEvent));
        Select("morgan@example.com");
        Team<TabItem>("EditTab").IsSelected = true;
        Arrange();
        var oldEditor = Team<TextBox>("MarkdownEditor");
        var first = model.SynchronizeAsync();
        Exception? primaryFailure = null;
        try
        {
            PumpUntil(() => entered.IsSet);
            oldEditor.Text = "Newer writing during automatic export";
            PumpUntil(() => model.GetRetainedNoteDraft(NoteScope.Team, note.Id) is null &&
                model.LocalSave.OutstandingRequests == 0 && model.LocalSaveError is null);
            using (var reading = OpenSmokeWorkspace(path))
                if (reading.BeginNoteDraft(NoteScope.Team, note.OwnerEmail, note.Id)?.Markdown != oldEditor.Text)
                    throw new InvalidOperationException("Held-export setup did not actually commit exact newer owner writing.");
            var revision = model.Synchronization.LocalRevision;
            var basis = model.BeginNoteDraft(NoteScope.Team, note.OwnerEmail, note.Id)!;
            Select("avery@example.com");
            Select("morgan@example.com");
            Arrange();
            var peer = UIElementAutomationPeer.CreatePeerForElement(surface)!;
            var peers = OrdinaryPeerDescendants(peer).ToArray();
            Console.WriteLine("ACTUAL1EEE-HELD-RETURN " + JsonSerializer.Serialize(new
            {
                calls, entered = entered.IsSet, ended = ended.IsSet, released = release.IsSet,
                model.TeamNotesView, model.LocalSave, model.LocalSaveError, revision,
                editorAttached = oldEditor.IsDescendantOf(surface),
                editorPeerPresent = peers.Any(item => item.GetAutomationId() == "TeamNoteMarkdown")
            }));
            if (oldEditor.IsDescendantOf(surface))
                throw new InvalidOperationException("The committed-return reproduction unexpectedly retained an Edit provider instead of clean Read.");
            if (model.TeamNotesView.IsEditing || model.TeamNotesView.SelectedNote?.Id != note.Id ||
                model.TeamNotesView.SelectedNote.Markdown != basis.Markdown || calls != 1 || ended.IsSet || release.IsSet)
                throw new InvalidOperationException("Clean returned owner/note/base or same first held publisher was not preserved.");
            Team<TabItem>("EditTab").IsSelected = true;
            Arrange();
            var freshEditor = Team<TextBox>("MarkdownEditor");
            var freshPeer = UIElementAutomationPeer.CreatePeerForElement(freshEditor)!;
            var afterEdit = model.BeginNoteDraft(NoteScope.Team, note.OwnerEmail, note.Id);
            if (!freshEditor.IsDescendantOf(surface) || !freshEditor.IsEnabled ||
                freshPeer.GetPattern(PatternInterface.Value) is not IValueProvider { IsReadOnly: false } value ||
                value.Value != basis.Markdown || Team<TextBox>("NoteTitle").Text != basis.Title ||
                model.TeamNotesView.SelectedNote?.Id != note.Id || !model.TeamNotesView.IsEditing ||
                model.GetRetainedNoteDraft(NoteScope.Team, note.Id) is not null ||
                afterEdit is null || afterEdit.Scope != basis.Scope || afterEdit.Id != basis.Id ||
                afterEdit.OwnerEmail != basis.OwnerEmail || afterEdit.Title != basis.Title || afterEdit.Markdown != basis.Markdown ||
                model.Synchronization.LocalRevision != revision || calls != 1 || ended.IsSet || release.IsSet)
                failures.Add("Actual public Edit did not rebind complete writable current owner/id/body/base without mutation under the same first publisher hold.");
            release.Set();
            PumpUntil(() => first.IsCompleted);
            if (!first.GetAwaiter().GetResult()) throw new InvalidOperationException(model.Synchronization.Summary);
            void Sync()
            {
                PumpUntil(() => Control<Button>("SyncNowButton").IsEnabled);
                Invoke("SyncNowButton");
                PumpUntil(() => !model.IsSynchronizing && Control<Button>("SyncNowButton").IsEnabled &&
                    model.Synchronization.Error is null && !model.Synchronization.HasUnpublishedChanges);
            }
            Sync();
            using var incoming = ApplicationWorkflow.CreateFileBacked();
            if (!incoming.OpenWorkspace(Path.Combine(root, "incoming-owner-return")) ||
                !incoming.ConfigureSynchronizationFolder(transport, joinExistingWorkspace: true) || !incoming.SyncNow() ||
                !incoming.EditPreferredName(note.OwnerEmail, "Automatically refreshed Manager") || !incoming.SyncNow())
                throw new InvalidOperationException(incoming.Status);
            Invoke("OpenSettingsButton");
            Sync();
            Invoke("OpenSettingsButton");
            Arrange();
            PumpUntil(() =>
            {
                Arrange();
                return Control<TextBlock>("ProfileHeading").Text == "Automatically refreshed Manager" &&
                    model.TeamNotesView.IsEditing && model.TeamNotesView.SelectedNote?.Id == note.Id &&
                    Team<TextBox>("MarkdownEditor") is { IsEnabled: true } editor && editor.IsDescendantOf(surface) &&
                    editor.Text == basis.Markdown && Team<TextBox>("NoteTitle").Text == basis.Title;
            });
            var unavailable = Path.Combine(root, "held-transport-unavailable");
            Directory.Move(transport, unavailable);
            try
            {
                Invoke("SyncNowButton");
                PumpUntil(() => model.Synchronization.Error is not null && Control<Button>("SyncNowButton").IsEnabled);
                Team<TextBox>("MarkdownEditor").Text = "Locally durable while automatic synchronization is unavailable";
                PumpUntil(() => model.GetRetainedNoteDraft(NoteScope.Team, note.Id) is null && model.CanSafelyClose);
                using var independent = OpenSmokeWorkspace(path);
                if (independent.BeginNoteDraft(NoteScope.Team, note.OwnerEmail, note.Id)?.Markdown !=
                    "Locally durable while automatic synchronization is unavailable")
                    throw new InvalidOperationException("Provider failure prevented continued exact local note writing.");
            }
            finally { Directory.Move(unavailable, transport); }
            Sync();
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try
            {
                release.Set();
                PumpUntil(() => first.IsCompleted);
                if (!first.GetAwaiter().GetResult()) throw new InvalidOperationException(model.Synchronization.Summary);
                if (!model.SyncNow()) throw new InvalidOperationException(model.Synchronization.Summary);
                if (window.IsVisible || new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero)
                    throw new InvalidOperationException("The committed-return contract created a native or visible window.");
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                PumpUntil(() => closed);
            }
            catch (Exception cleanup) when (primaryFailure is not null)
            {
                Console.Error.WriteLine("CURRENT-NOTE-CONTRACT-CLEANUP-FAULT " + cleanup);
            }
        }
    }
}
