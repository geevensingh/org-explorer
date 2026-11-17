using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyHostedOrdinaryFixtureStateContracts(Type windowType, string root)
    {
        using var fixture = new OrdinaryFixtureWindow(windowType, Path.Combine(root, "ordinary-fixture"));
        foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
        {
            fixture.Select("morgan@example.com");
            fixture.NoteButton(scope, "NewButton");
            var id = fixture.Model.GetOwnedNotesView(scope).SelectedNote!.Id;
            fixture.Note<TextBox>(scope, "NoteTitle").Text = "";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, id) is { IsSaving: false, Error: not null });
            fixture.Capture("invalid-new-before-discard", scope);
            fixture.NoteButton(scope, "DiscardButton");
            fixture.Capture("invalid-new-after-discard", scope);
            using (var reading = OpenSmokeWorkspace(fixture.Path))
                if (!fixture.Model.GetOwnedNotesView(scope).IsEmpty ||
                    fixture.Model.GetOwnedNotesView(scope).SelectedNote is not null ||
                    fixture.Note<TextBlock>(scope, "EmptyNotes").Visibility != Visibility.Visible ||
                    reading.GetPersonNotes("morgan@example.com", true).Any(note => note.Id == id) ||
                    reading.GetTeamNotes("morgan@example.com", true).Any(note => note.Id == id))
                    throw new InvalidOperationException("Discard manufactured a saved record for an uncommitted creation.");

            fixture.NoteButton(scope, "NewButton");
            id = fixture.Model.GetOwnedNotesView(scope).SelectedNote!.Id;
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, id) is null && fixture.Model.CanSafelyClose);
            var saved = fixture.ReadNote(scope, id);
            var initial = scope == NoteScope.Person
                ? (object)ReadSmokePersonNote(fixture.Path, id) : ReadSmokeTeamNote(fixture.Path, id);
            if (saved.Title != $"New {scope} note" || saved.Markdown != "" ||
                saved.OwnerEmail != "morgan@example.com" || saved.Id != id)
                throw new InvalidOperationException("The initial note was not independently reopened before invalid editing.");
            fixture.Note<TextBox>(scope, "NoteTitle").Text = "";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, id) is { IsSaving: false, Error: not null });
            fixture.Select("avery@example.com");
            fixture.Select("morgan@example.com");
            if (fixture.Model.GetOwnedNotesView(scope).SelectedNote?.Id != id ||
                fixture.Note<TextBox>(scope, "NoteTitle").Text != "")
                throw new InvalidOperationException("Browsing changed the exact invalid existing note.");
            fixture.NoteButton(scope, "DiscardButton");
            fixture.Capture("existing-initial-note-after-discard", scope);
            if (fixture.Model.GetOwnedNotesView(scope).SelectedNote?.Id != id ||
                fixture.Note<TextBlock>(scope, "NoteHeading").Text != $"New {scope} note" ||
                fixture.Model.GetRetainedNoteDraft(scope, id) is not null ||
                !Equals(initial, scope == NoteScope.Person
                    ? (object)ReadSmokePersonNote(fixture.Path, id) : ReadSmokeTeamNote(fixture.Path, id)))
                throw new InvalidOperationException("Discard changed the independently saved exact note or retained dirty writing.");
            fixture.Note<TabItem>(scope, "ReadTab").IsSelected = true;
            if (fixture.Note<TabControl>(scope, "Views").SelectedItem != fixture.Note<TabItem>(scope, "ReadTab"))
                throw new InvalidOperationException("The actual Read action did not reach restored saved reading.");
        }
        Console.WriteLine("PASS: actual BAML uncommitted Person/Team creation discard never manufactures saved records; independently reopened initial notes restore exact saved owner/id/title/body and Read after invalid editing/browsing.");
        VerifyOrdinaryFailedNoteState(fixture);
        using var recovery = new OrdinaryFixtureWindow(windowType, Path.Combine(root, "ordinary-profile-failure"));
        VerifyOrdinaryCleanNoteAfterProfileFailure(recovery);
        using var navigation = new OrdinaryFixtureWindow(windowType, Path.Combine(root, "ordinary-search-selection"));
        VerifyOrdinarySearchSelection(navigation);
    }

    private static void VerifyOrdinarySearchSelection(OrdinaryFixtureWindow fixture)
    {
        fixture.Select("morgan@example.com");
        fixture.NoteButton(NoteScope.Person, "NewButton");
        var id = fixture.Model.PersonNotesView.SelectedNote!.Id;
        fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text = "";
        PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id) is { IsSaving: false, Error: not null });
        var retained = fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id);
        fixture.Arrange(700, 900);
        fixture.Control<TextBox>("SearchQuery").Text = "example.com";
        var results = fixture.Control<ListBox>("SearchResults");
        PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == "avery@example.com"));
        results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result =>
            result.Person.Email == "avery@example.com");
        results.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            new UnshownInputSource { RootVisual = (System.Windows.Media.Visual)fixture.Window.Content }, 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => fixture.Control<TextBlock>("Email").Text == "avery@example.com" &&
            fixture.Control<TextBox>("SearchQuery").Text == "");
        fixture.Arrange(700, 900);
        if (fixture.Control<GroupBox>("PeoplePanel").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("The actual compact search activation did not dismiss People.");
        Rect Viewport()
        {
            var scroll = fixture.Control<ScrollViewer>("PersonScroll");
            return scroll.TransformToAncestor((System.Windows.Media.Visual)fixture.Window.Content)
                .TransformBounds(new Rect(scroll.RenderSize));
        }
        var viewport = Viewport();
        fixture.Click("OpenPeopleButton");
        fixture.Arrange(700, 900);
        fixture.Control<TextBox>("SearchQuery").Text = "example.com";
        PumpUntil(() => results.Items.Cast<PersonSearchResult>().Any(result => result.Person.Email == "avery@example.com"));
        results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result => result.Person.Email == "avery@example.com");
        fixture.Arrange(700, 900);
        var peers = OrdinaryPeerDescendants(UIElementAutomationPeer.CreatePeerForElement(
            fixture.Control<GroupBox>("PeoplePanel"))!).ToArray();
        Console.WriteLine("ORDINARY-FIXTURE-SEARCH " + JsonSerializer.Serialize(new
        {
            query = fixture.Control<TextBox>("SearchQuery").Text,
            owner = fixture.Model.PeopleNavigation.SelectedEmail,
            overlay = fixture.Model.PeopleNavigation.OverlayOpen,
            searchResultsPresent = peers.Any(peer => peer.GetAutomationId() == "SearchResults"),
            browsingVisibility = fixture.Control<FrameworkElement>("BrowsingPanel").Visibility.ToString(),
            searchVisibility = fixture.Control<FrameworkElement>("SearchPanel").Visibility.ToString()
        }));
        void AssertSelection() => AssertOrdinaryNavigationSelection("avery@example.com",
            searching: !fixture.Model.PeopleNavigation.IsBrowsing,
            () =>
            {
                if (fixture.Control<FrameworkElement>("BrowsingPanel").Visibility != Visibility.Visible)
                    throw new InvalidOperationException("The selected search route has no visible ShowArchivedPeople; actual BAML BrowsingPanel is collapsed.");
                return fixture.Control<CheckBox>("ShowArchivedPeople").IsChecked == true;
            },
            list => OrdinaryPeerDescendants(peers.Single(peer => peer.GetAutomationId() == list))
                .Where(peer => peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider { IsSelected: true })
                .Select(peer => list == "SearchResults"
                    ? OrdinarySearchSelectionIdentity(peer.GetName(), fixture.Control<TextBlock>("ProfileHeading").Text,
                        "avery@example.com") : peer.GetAutomationId()));
        AssertSelection();
        results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result =>
            result.Person.Email == "morgan@example.com");
        if (fixture.Control<TextBlock>("Email").Text != "avery@example.com" ||
            fixture.Model.PeopleNavigation.HighlightedResult?.Person.Email != "morgan@example.com" ||
            fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id) != retained)
            throw new InvalidOperationException("Highlighting another grouped Person activated it or changed retained owner writing.");
        results.SelectedItem = results.Items.Cast<PersonSearchResult>().Single(result =>
            result.Person.Email == "avery@example.com");
        AssertSelection();
        if (fixture.Control<TextBox>("SearchQuery").Text != "example.com" || Viewport() != viewport ||
            fixture.Model.PeopleNavigation.SelectedEmail != "avery@example.com" ||
            fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id) != retained)
            throw new InvalidOperationException("Search selection observation changed query, displayed owner or retained writing.");
        fixture.Click("ClosePeopleButton");
        fixture.Arrange(700, 900);
        if (fixture.Model.PeopleNavigation.OverlayOpen || Viewport() != viewport ||
            fixture.Control<TextBox>("SearchQuery").Text != "example.com")
            throw new InvalidOperationException("Actual People dismissal changed the query or Person viewport.");
        fixture.Control<TextBox>("SearchQuery").Clear();
        fixture.Select("morgan@example.com");
        fixture.NoteButton(NoteScope.Person, "DiscardButton");
        Console.WriteLine("PASS: actual compact search selection observes its own result route without requiring a hidden archived toggle or altering query/owner/retained writing.");
    }

    private static void AssertOrdinaryNavigationSelection(string email, bool searching,
        Func<bool> readArchived, Func<string, IEnumerable<string>> readSelectedEmails)
    {
        var showArchivedPeople = !searching && readArchived();
        PersonSelection.AssertMatchesProfile(email, showArchivedPeople,
            list => readSelectedEmails(searching ? "SearchResults" : list));
    }

    private static string OrdinarySearchSelectionIdentity(string label, string heading, string email)
    {
        if (label.StartsWith("STALE retained — ", StringComparison.Ordinal)) label = label["STALE retained — ".Length..];
        return label.StartsWith($"{heading} ({email}) — ", StringComparison.Ordinal) ? email : label;
    }

    private static void VerifyOrdinaryCleanNoteAfterProfileFailure(OrdinaryFixtureWindow fixture)
    {
        const NoteScope scope = NoteScope.Person;
        fixture.Select("morgan@example.com");
        fixture.NoteButton(scope, "NewButton");
        var id = fixture.Model.PersonNotesView.SelectedNote!.Id;
        fixture.Note<TextBox>(scope, "NoteTitle").Text = "Independent recovery note";
        fixture.Note<TextBox>(scope, "MarkdownEditor").Text = "Independent exact recovery writing";
        PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, id) is null && fixture.Model.CanSafelyClose);
        var saved = fixture.ReadSavedGraph();
        fixture.Click("EditProfileButton");
        fixture.Control<TextBox>("TeamNameEditor").Text = "Retained recovery label";
        fixture.Control<TextBox>("Department").Text = "Retained recovery department";
        fixture.Note<TabItem>(scope, "EditTab").IsSelected = true;
        using (File.Open(System.IO.Path.Combine(fixture.Path, "workspace.sqlite"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            fixture.Click("SaveProfileButton");
            PumpUntil(() => fixture.Model.LocalSave.Phase == LocalSavePhase.Failed &&
                !fixture.Control<TextBox>("Department").IsEnabled);
            var failure = fixture.Model.LocalSaveError;
            fixture.Select("avery@example.com");
            fixture.Select("morgan@example.com");
            fixture.Arrange();
            fixture.Capture("failed-profile-clean-note-owner-return", scope);
            if (fixture.Model.LocalSave.Phase != LocalSavePhase.Failed ||
                fixture.Model.LocalSave.Outcome != LocalSaveOutcome.NotCommitted ||
                fixture.Model.LocalSaveError != failure || fixture.Model.CanEditLocalWorkspace ||
                fixture.Model.PersonNotesView.SelectedNote?.Id != id || fixture.Model.PersonNotesView.IsEditing ||
                fixture.Model.GetRetainedNoteDraft(scope, id) is not null ||
                fixture.Note<TabControl>(scope, "Views").SelectedItem != fixture.Note<TabItem>(scope, "ReadTab") ||
                fixture.Control<TextBox>("TeamNameEditor").Text != "Retained recovery label" ||
                fixture.Control<TextBox>("Department").Text != "Retained recovery department" ||
                fixture.NotePeers(scope).Any(peer => peer.GetAutomationId() == "PersonNoteMarkdown"))
                throw new InvalidOperationException("Failed profile browsing changed the clean Read note, exact retained profile or fault.");
            fixture.Note<TabItem>(scope, "EditTab").IsSelected = true;
            fixture.Arrange();
            var editor = fixture.Note<TextBox>(scope, "MarkdownEditor");
            var provider = UIElementAutomationPeer.CreatePeerForElement(editor)!;
            fixture.Capture("failed-profile-explicit-note-edit", scope);
            if (!editor.IsDescendantOf(fixture.Control<UserControl>("PersonNotes")) ||
                fixture.Note<TabControl>(scope, "Views").SelectedItem != fixture.Note<TabItem>(scope, "EditTab") ||
                provider.GetAutomationId() != "PersonNoteMarkdown" ||
                provider.GetPattern(PatternInterface.Value) is not IValueProvider value ||
                value.Value != "Independent exact recovery writing" || provider.IsEnabled() ||
                fixture.Model.PersonNotesView.SelectedNote?.Id != id ||
                fixture.Model.GetRetainedNoteDraft(scope, id) is not null || fixture.Model.LocalSaveError != failure)
                throw new InvalidOperationException("The actual Edit action did not realize exact read-only retained note values under the unchanged fault: " +
                    $"provider={provider?.GetAutomationId() ?? "missing"}; value={(provider?.GetPattern(PatternInterface.Value) as IValueProvider)?.Value}; " +
                    $"attached={fixture.Note<TextBox>(scope, "MarkdownEditor").IsDescendantOf(fixture.Control<UserControl>("PersonNotes"))}.");
        }
        fixture.Click("NoticeRetryButton");
        PumpUntil(() => fixture.Model.CanEditLocalWorkspace && fixture.Control<TextBox>("Department").IsEnabled);
        fixture.Click("CancelProfileButton");
        if (fixture.ReadSavedGraph() != saved)
            throw new InvalidOperationException("The failed-profile Edit observation changed independently saved profile/note data.");
        Console.WriteLine("PASS: failed explicit profile Save preserves complete owner drafts across A-B-A, leaves clean note in Read without a Markdown provider, and actual Edit realizes its exact disabled Value provider without note mutation.");
    }

    private static void VerifyOrdinaryFailedNoteState(OrdinaryFixtureWindow fixture)
    {
        foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
        {
            fixture.Select("morgan@example.com");
            var ongoingId = fixture.Model.GetOwnedNotesView(scope).SelectedNote!.Id;
            fixture.NoteButton(scope, "DesignateButton");
            PumpUntil(() => fixture.Model.GetOwnedNotesView(scope).OngoingNoteId == ongoingId &&
                fixture.Model.CanSafelyClose);
            fixture.NoteButton(scope, "NewButton");
            var id = fixture.Model.GetOwnedNotesView(scope).SelectedNote!.Id;
            fixture.Note<TextBox>(scope, "NoteTitle").Text = $"Secondary {scope} writing";
            fixture.Note<TextBox>(scope, "MarkdownEditor").Text = $"Saved exact {scope} body\nSecond line";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, id) is null && fixture.Model.CanSafelyClose);
            var saved = fixture.ReadSavedGraph();
            if (!fixture.Note<TextBox>(scope, "NoteTitle").IsEnabled ||
                !fixture.Note<TextBox>(scope, "MarkdownEditor").IsEnabled)
                throw new InvalidOperationException("Healthy Edit was not writable before the real SQLite failure.");
            using (WorkspaceFixtures.LockLocalStore(fixture.Path))
            {
                fixture.Note<TextBox>(scope, "MarkdownEditor").Text = $"Retained failed {scope} body\nSecond line";
                PumpUntil(() => fixture.Model.LocalSave.Phase == LocalSavePhase.Failed &&
                    fixture.Model.GetRetainedNoteDraft(scope, id) is { IsSaving: false, Error: not null });
                fixture.Capture("sqlite-failed-edit", scope);
                var retained = fixture.Model.GetRetainedNoteDraft(scope, id)!;
                var error = fixture.Model.LocalSaveError;
                void RequireRetainedPausedEdit()
                {
                    if (fixture.Model.LocalSave.Phase != LocalSavePhase.Failed ||
                        fixture.Model.LocalSave.Outcome != LocalSaveOutcome.NotCommitted ||
                        fixture.Model.LocalSaveError != error || fixture.Model.CanSafelyClose ||
                        fixture.Model.GetOwnedNotesView(scope).SelectedNote?.Id != id ||
                        fixture.Model.GetOwnedNotesView(scope).OwnerEmail != "morgan@example.com" ||
                        fixture.Model.GetOwnedNotesView(scope).OngoingNoteId != ongoingId ||
                        fixture.Model.GetRetainedNoteDraft(scope, id) != retained ||
                        fixture.Note<TabControl>(scope, "Views").SelectedItem != fixture.Note<TabItem>(scope, "EditTab") ||
                        fixture.Note<TextBox>(scope, "NoteTitle").Text != $"Secondary {scope} writing" ||
                        fixture.Note<TextBox>(scope, "MarkdownEditor").Text != $"Retained failed {scope} body\nSecond line" ||
                        fixture.Note<Button>(scope, "DesignateButton").IsEnabled ||
                        fixture.Note<TextBox>(scope, "NoteTitle").IsEnabled ||
                        fixture.Note<TextBox>(scope, "MarkdownEditor").IsEnabled ||
                        !fixture.Note<Button>(scope, "DiscardButton").IsEnabled ||
                        fixture.ReadSavedGraph() != saved)
                        throw new InvalidOperationException("Failed Edit lost exact retained writing, original error, paused mutation or whole saved data.");
                }
                RequireRetainedPausedEdit();
                fixture.NoteButton(scope, "OngoingButton");
                if (fixture.Model.GetOwnedNotesView(scope).SelectedNote?.Id != ongoingId)
                    throw new InvalidOperationException("Paused edits incorrectly blocked exact ongoing-note browsing.");
                fixture.Note<ListBox>(scope, "NotesList").SelectedItem =
                    fixture.Note<ListBox>(scope, "NotesList").Items.Cast<OwnedNote>().Single(note => note.Id == id);
                fixture.Note<TabItem>(scope, "EditTab").IsSelected = true;
                RequireRetainedPausedEdit();
                fixture.Select("avery@example.com");
                fixture.Select("morgan@example.com");
                RequireRetainedPausedEdit();
                fixture.Capture("sqlite-failed-retained-owner-return", scope);
            }
            fixture.Click("NoticeRetryButton");
            PumpUntil(() => fixture.Model.CanEditLocalWorkspace && fixture.Note<Button>(scope, "DiscardButton").IsEnabled);
            fixture.NoteButton(scope, "DiscardButton");
            fixture.Note<TabItem>(scope, "ReadTab").IsSelected = true;
            if (fixture.ReadSavedGraph() != saved)
                throw new InvalidOperationException("Reconciliation/discard changed saved profile or note data.");
            fixture.Note<TabItem>(scope, "EditTab").IsSelected = true;
            if (!fixture.Note<TextBox>(scope, "NoteTitle").IsEnabled ||
                !fixture.Note<TextBox>(scope, "MarkdownEditor").IsEnabled ||
                fixture.Note<TextBox>(scope, "MarkdownEditor").Text != $"Saved exact {scope} body\nSecond line")
                throw new InvalidOperationException("Recovered healthy Edit did not restore writable exact saved content.");
        }
        Console.WriteLine("PASS: real SQLite Person/Team noncommit leaves exact retained Edit readable but disabled, preserves ongoing and owner-return browsing/error/whole saved graph, and recovers healthy writable Edit without replay.");
    }

    private sealed class OrdinaryFixtureWindow : IDisposable
    {
        internal string Path { get; }
        internal Window Window { get; }
        internal WorkspaceViewModel Model { get; }

        internal OrdinaryFixtureWindow(Type windowType, string path)
        {
            Path = path;
            using (var seed = ApplicationWorkflow.CreateFileBacked())
                if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport(RecoveryPeople).Id))
                    throw new InvalidOperationException("Ordinary fixture creation failed: " + seed.Status);
            Window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [ApplicationWorkflow.CreateFileBacked(), (Func<Window, string?>)(_ => path)], null)!;
            Model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            Click("OpenSettingsButton");
            Click("OpenWorkspaceButton");
            PumpUntil(() => Model.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
        }

        internal T Control<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);
        internal T Note<T>(NoteScope scope, string name) where T : FrameworkElement =>
            (T)Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes").FindName(name);
        internal void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        internal void NoteButton(NoteScope scope, string name) =>
            Note<Button>(scope, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        internal NoteDraft ReadNote(NoteScope scope, Guid id)
        {
            using var reading = OpenSmokeWorkspace(Path);
            return reading.BeginNoteDraft(scope, "morgan@example.com", id)
                ?? throw new InvalidOperationException("The exact persisted ordinary fixture note could not be reopened.");
        }
        internal string ReadSavedGraph()
        {
            using var reading = OpenSmokeWorkspace(Path);
            return JsonSerializer.Serialize(reading.People.OrderBy(person => person.Email).Select(person => new
            {
                relationships = reading.GetRelationships(person.Email),
                personNotes = reading.GetPersonNotes(person.Email, true),
                teamNotes = reading.GetTeamNotes(person.Email, true)
            }).ToArray());
        }
        internal void Select(string email)
        {
            var node = Model.ReportingRoots.SelectMany(item => new[] { item }.Concat(item.Reports))
                .Single(item => item.Person.Email == email);
            Control<TreeView>("PeopleList").RaiseEvent(
                new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
            if (Control<TextBlock>("Email").Text != email || Model.PeopleNavigation.SelectedEmail != email)
                throw new InvalidOperationException("Ordinary fixture navigation did not reach its exact owner.");
        }
        internal void Arrange(double width = 1100, double height = 900)
        {
            var content = (FrameworkElement)Window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            Window.UpdateLayout();
        }
        internal IEnumerable<AutomationPeer> NotePeers(NoteScope scope) =>
            OrdinaryPeerDescendants(UIElementAutomationPeer.CreatePeerForElement(
                Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes"))!);

        internal void Capture(string stage, NoteScope scope) =>
            Console.WriteLine("ORDINARY-FIXTURE-STATE " + JsonSerializer.Serialize(new
            {
                stage, scope = scope.ToString(), person = Control<TextBlock>("Email").Text,
                view = Model.GetOwnedNotesView(scope),
                tab = (Note<TabControl>(scope, "Views").SelectedItem as TabItem)?.Name,
                title = Note<TextBox>(scope, "NoteTitle").Text,
                body = Note<TextBox>(scope, "MarkdownEditor").Text,
                titleEnabled = Note<TextBox>(scope, "NoteTitle").IsEnabled,
                bodyEnabled = Note<TextBox>(scope, "MarkdownEditor").IsEnabled,
                mutationEnabled = Note<Button>(scope, "DesignateButton").IsEnabled,
                phase = Model.LocalSave.Phase.ToString(), outcome = Model.LocalSave.Outcome.ToString()
            }));

        public void Dispose()
        {
            if (Window.IsVisible || new WindowInteropHelper(Window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Ordinary fixture contracts created a native or visible window.");
            foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
                if (Model.GetOwnedNotesView(scope).SelectedNote is { } note)
                    Model.DiscardUnsavedChanges(note.Id);
            if (Model.LocalSave.EditsPaused) Model.ReconcileLocalSaves();
            if (Model.ProfileEdit is not null) Click("CancelProfileButton");
            ClosePublishedCompiledWindow(Window);
        }

    }

    private static IEnumerable<AutomationPeer> OrdinaryPeerDescendants(AutomationPeer root)
    {
        foreach (var child in root.GetChildren() ?? [])
        {
            yield return child;
            foreach (var descendant in OrdinaryPeerDescendants(child)) yield return descendant;
        }
    }
}
