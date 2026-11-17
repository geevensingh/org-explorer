using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
    private static void VerifyActual39FunctionalContracts(Type windowType, string root)
    {
        var failures = new List<string>();
        VerifyActual39ExplicitProfileSave(windowType, root, failures);
        foreach (var archived in new[] { false, true })
            VerifyActual39DiscardReading(windowType, root, archived, failures);
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS: actual39 current/Archived Person-note discard retains exact saved owner/id/writing, " +
            "independent preference/review/history context and actual Read providers without native windows.");
    }

    private static void VerifyActual39ExplicitProfileSave(Type windowType, string root, List<string> failures)
    {
        using var fixture = new OrdinaryFixtureWindow(windowType, Path.Combine(root, "profile-completion"));
        const string owner = "avery@example.com";
        const string manager = "morgan@example.com";
        fixture.Select(manager);
        fixture.Click("EditProfileButton");
        fixture.Control<TextBox>("TeamNameEditor").Text = "Platform";
        fixture.Click("SaveProfileButton");
        PumpUntil(() => fixture.Model.ProfileEdit is null && fixture.Control<Button>("EditProfileButton").IsEnabled);
        fixture.Click("EditProfileButton");
        fixture.Control<TextBox>("TeamNameEditor").Text = "Renamed platform";
        var failedSession = fixture.Model.ProfileEdit!;
        using (WorkspaceFixtures.LockLocalStore(fixture.Path))
        {
            fixture.Click("SaveProfileButton");
            PumpUntil(() => fixture.Model.LocalSave.Phase == LocalSavePhase.Failed &&
                fixture.Control<Button>("NoticeRetryButton").IsEnabled);
            Actual39Capture(fixture, "profile-rejected", failedSession);
            using var unchanged = OpenSmokeWorkspace(fixture.Path);
            if (fixture.Model.ProfileEdit != failedSession ||
                fixture.Model.LocalSave.Outcome != LocalSaveOutcome.NotCommitted ||
                unchanged.GetRelationships(manager)!.Person != failedSession.Basis)
                throw new InvalidOperationException("The real rejected profile Save changed its exact immutable base/draft or saved owner.");
        }
        fixture.Click("NoticeRetryButton");
        PumpUntil(() => fixture.Model.CanEditLocalWorkspace && fixture.Control<Button>("SaveProfileButton").IsEnabled);
        if (fixture.Model.ProfileEdit != failedSession)
            throw new InvalidOperationException("Recovery discarded or replayed the retained complete profile.");
        fixture.Click("SaveProfileButton");
        PumpUntil(() => fixture.Model.ProfileEdit is null && fixture.Control<Button>("EditProfileButton").IsEnabled);
        fixture.Select(owner);
        var photo = Path.Combine(root, "actual39-photo.png");
        CreatePhotoFixture(photo);
        if (!fixture.Model.EditPhoto(owner, photo))
            throw new InvalidOperationException("Profile resource fixture failed: " + fixture.Model.Status);
        fixture.Select(manager);
        fixture.Select(owner);
        fixture.Click("EditPreferredNameButton");
        fixture.Control<TextBox>("PreferredName").Text = " ";
        fixture.Click("SavePreferredNameButton");
        PumpUntil(() => fixture.Model.GetPersonTextDraft(owner, PersonTextField.PreferredName)?.Error is not null &&
            fixture.Control<Button>("SavePreferredNameButton").IsEnabled);
        var rejected = fixture.Model.GetPersonTextDraft(owner, PersonTextField.PreferredName)!;
        Actual39Capture(fixture, "preferred-rejected", rejected);
        fixture.Control<TextBox>("PreferredName").Text = "Ave";
        // Native Enter submits this same explicit Save boundary; an invisible window has no keyboard focus.
        fixture.Click("SavePreferredNameButton");
        PumpUntil(() => fixture.Model.GetPersonTextDraft(owner, PersonTextField.PreferredName) is null &&
            fixture.Control<FrameworkElement>("PreferredNameEditorPanel").Visibility == Visibility.Collapsed);
        foreach (var dismissal in new[] { "cancel", "escape" })
        {
            var graph = fixture.ReadSavedGraph();
            fixture.Click("EditPreferredNameButton");
            fixture.Control<TextBox>("PreferredName").Text = "Must not save " + dismissal;
            if (dismissal == "cancel") fixture.Click("CancelPreferredNameButton");
            else
                fixture.Control<TextBox>("PreferredName").RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                    new UnshownInputSource { RootVisual = (System.Windows.Media.Visual)fixture.Window.Content }, 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Actual39Capture(fixture, "preferred-dismissed-" + dismissal, new
            {
                rejected.Basis, dismissal,
                panel = fixture.Control<FrameworkElement>("PreferredNameEditorPanel").Visibility.ToString(),
                retained = fixture.Model.GetPersonTextDraft(owner, PersonTextField.PreferredName)
            });
            if (fixture.Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Collapsed ||
                fixture.Model.GetPersonTextDraft(owner, PersonTextField.PreferredName) is not null ||
                fixture.ReadSavedGraph() != graph)
                throw new InvalidOperationException("Actual Preferred-name dismissal retained or committed the wrong owner's intent: " + dismissal);
        }
        using var before = OpenSmokeWorkspace(fixture.Path);
        var original = before.GetRelationships(owner)!.Person;
        var managerBefore = before.GetRelationships(manager)!.Person;
        var timeline = before.GetProfileTimeline(owner)!;
        var history = JsonSerializer.Serialize(before.GetHistoricalTeamContexts(owner));
        var resource = before.GetPhoto(owner)!;
        var resourceBytes = resource.Bytes.ToArray();
        fixture.Click("EditProfileButton");
        const string link = "https://127.0.0.1:49139/profile";
        fixture.Control<TextBox>("LinkedInProfileUrl").Text = link;
        var submitted = fixture.Model.ProfileEdit!;
        var revision = fixture.Model.LocalSave.CommittedRevision;
        if (submitted.Basis != original || submitted.Draft != original with { LinkedInProfileUrl = link })
            throw new InvalidOperationException("The actual explicit profile editor did not hold the expected complete submitted draft.");
        fixture.Click("SaveProfileButton");
        PumpUntil(() => fixture.Model.ProfileEdit is null && fixture.Control<Button>("EditProfileButton").IsEnabled);
        Actual39Capture(fixture, "explicit-link-save-complete", new
        {
            submitted, revision, preferred = fixture.Model.GetPersonTextDraft(owner, PersonTextField.PreferredName),
            form = fixture.Control<FrameworkElement>("ProfileForm").Visibility.ToString(),
            linkEnabled = fixture.Control<Button>("OpenLinkedInButton").IsEnabled
        });
        using var reopened = OpenSmokeWorkspace(fixture.Path);
        var expected = original with { LinkedInProfileUrl = link };
        if (fixture.Model.LocalSave.EditsPaused || fixture.Model.LocalSave.OutstandingRequests != 0 ||
            fixture.Model.LocalSave.CommittedRevision == revision ||
            fixture.Control<TextBlock>("Email").Text != owner ||
            fixture.Control<FrameworkElement>("ProfileForm").Visibility != Visibility.Collapsed ||
            fixture.Model.GetRelationships(owner)!.Person != expected ||
            reopened.GetRelationships(owner)!.Person != expected ||
            reopened.GetRelationships(manager)!.Person != managerBefore ||
            JsonSerializer.Serialize(reopened.GetHistoricalTeamContexts(owner)) != history ||
            reopened.GetProfileTimeline(owner)!.Changes.Count != timeline.Changes.Count + 1 ||
            !reopened.GetPhoto(owner)!.Bytes.Span.SequenceEqual(resourceBytes) ||
            !fixture.Control<Button>("OpenLinkedInButton").IsEnabled ||
            fixture.Model.GetPersonTextDraft(owner, PersonTextField.PreferredName) is not null)
            throw new InvalidOperationException("Explicit compiled Save did not independently commit the exact owner/profile/resource/history.");
        Actual39Capture(fixture, "explicit-link-save-independent", new
        {
            submitted,
            savedOwner = reopened.GetRelationships(owner)!.Person,
            savedManager = reopened.GetRelationships(manager)!.Person,
            savedTimeline = reopened.GetProfileTimeline(owner),
            savedHistory = reopened.GetHistoricalTeamContexts(owner),
            photo = new { resource.ContentType, resource.Width, resource.Height, resource.Bytes.Length },
            sameResourceBytes = reopened.GetPhoto(owner)!.Bytes.Span.SequenceEqual(resourceBytes)
        });
        fixture.NoteButton(NoteScope.Person, "NewButton");
        var noteId = fixture.Model.PersonNotesView.SelectedNote!.Id;
        fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text = "Independent retained Person writing";
        fixture.Note<TextBox>(NoteScope.Person, "MarkdownEditor").Text = "Exact saved independent body";
        PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Person, noteId) is null && fixture.Model.CanSafelyClose);
        var savedNote = fixture.Model.GetPersonNotes(owner, true).Single(note => note.Id == noteId);
        fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text = " ";
        PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Person, noteId) is { IsSaving: false, Error: not null });
        var retained = fixture.Model.GetRetainedNoteDraft(NoteScope.Person, noteId)!;
        var independentError = fixture.Model.LocalSaveError;
        fixture.Click("EditProfileButton");
        fixture.Control<TextBox>("Department").Text = "Research";
        var independentSubmission = fixture.Model.ProfileEdit!;
        var expectedWithIndependentWriting = expected with { Department = "Research" };
        if (independentSubmission.Basis != expected || independentSubmission.Draft != expectedWithIndependentWriting)
            throw new InvalidOperationException("The second explicit Save did not capture its exact complete draft beside independent writing.");
        if (!fixture.Control<Button>("SaveProfileButton").IsEnabled)
            throw new InvalidOperationException("Independent invalid writing disabled the actual explicit profile Save.");
        fixture.Click("SaveProfileButton");
        PumpUntil(() => fixture.Model.ProfileEdit is null && fixture.Control<Button>("EditProfileButton").IsEnabled);
        using var independent = OpenSmokeWorkspace(fixture.Path);
        if (fixture.Model.GetRetainedNoteDraft(NoteScope.Person, noteId) != retained ||
            fixture.Model.LocalSaveError != independentError || fixture.Model.CanSafelyClose ||
            independent.GetRelationships(owner)!.Person != expectedWithIndependentWriting ||
            independent.GetRelationships(manager)!.Person != managerBefore ||
            independent.GetPersonNotes(owner, true).Single(note => note.Id == noteId) != savedNote ||
            JsonSerializer.Serialize(independent.GetHistoricalTeamContexts(owner)) != history ||
            !independent.GetPhoto(owner)!.Bytes.Span.SequenceEqual(resourceBytes))
            throw new InvalidOperationException("Explicit profile Save changed independent retained/saved writing, errors, whole profile or resources.");
        var editing = fixture.Control<FrameworkElement>("ProfileForm").Visibility == Visibility.Visible;
        var canEdit = fixture.Control<Button>("EditProfileButton").IsEnabled;
        Actual39Capture(fixture, "explicit-save-independent-writing", new
        {
            independentSubmission, editing, canEdit, retained, independentError,
            savedOwner = independent.GetRelationships(owner)!.Person, savedNote
        });
        if (!IsSmokeExplicitProfileSaveComplete(owner, fixture.Control<TextBlock>("Email").Text,
            editing, canEdit))
            failures.Add("actual39 explicit profile Save: exact independently committed whole profile was vetoed by independent retained-note feedback.");
        if (fixture.Window.IsVisible || new WindowInteropHelper(fixture.Window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Actual39 profile contracts created a native or visible window.");
    }

    private static void VerifyActual39DiscardReading(Type windowType, string root, bool archived, List<string> failures)
    {
        var settings = Path.Combine(root, archived ? "archived-settings" : "history-settings");
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
        try
        {
            using var fixture = new OrdinaryFixtureWindow(windowType,
                Path.Combine(root, archived ? "archived-discard" : "history-discard"));
            const string owner = "avery@example.com";
            const string manager = "morgan@example.com";
            var title = archived ? "Retained individual" : "Primary Person context";
            var body = archived ? "# Retained individual writing" : "Everyday individual writing";
            Guid CreateNote(NoteScope scope, string noteTitle, string markdown)
            {
                fixture.NoteButton(scope, "NewButton");
                var id = fixture.Model.GetOwnedNotesView(scope).SelectedNote!.Id;
                fixture.Note<TextBox>(scope, "NoteTitle").Text = noteTitle;
                fixture.Note<TextBox>(scope, "MarkdownEditor").Text = markdown;
                PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, id) is null && fixture.Model.CanSafelyClose);
                return id;
            }
            void SelectOwner()
            {
                if (!archived) fixture.Select(owner);
                else
                {
                    fixture.Control<CheckBox>("ShowArchivedPeople").IsChecked = true;
                    fixture.Control<ListBox>("ArchivedPeopleList").SelectedItem =
                        fixture.Control<ListBox>("ArchivedPeopleList").Items.Cast<PersonProfile>()
                            .Single(person => person.Email == owner);
                    if (fixture.Control<TextBlock>("Email").Text != owner ||
                        fixture.Model.PeopleNavigation.SelectedEmail != owner)
                        throw new InvalidOperationException("The actual archived list did not select the exact retained owner.");
                }
            }
            fixture.Select(manager);
            var teamId = CreateNote(NoteScope.Team, "Morgan context", "# Frozen direct\nOriginal direct");
            fixture.Click("EditProfileButton");
            fixture.Control<TextBox>("JobTitle").Text = "Director";
            fixture.Click("SaveProfileButton");
            PumpUntil(() => fixture.Model.ProfileEdit is null && fixture.Control<Button>("EditProfileButton").IsEnabled);
            if (fixture.Model.GetTeamNotes(manager).Single(note => note.Id == teamId).NeedsReview != true)
                throw new InvalidOperationException("The fixture lost actual Manager-change Team review context.");
            string? preferenceError = null;
            if (!archived)
            {
                fixture.NoteButton(NoteScope.Team, "DesignateButton");
                PumpUntil(() => fixture.Model.TeamNotesView.OngoingNoteId == teamId &&
                    fixture.Model.LocalSave.OutstandingRequests == 0);
                var otherId = CreateNote(NoteScope.Team, "Secondary empty Team note", "");
                var secondary = JsonSerializer.Serialize(fixture.ReadNote(NoteScope.Team, otherId));
                using (WorkspaceFixtures.LockLocalStore(fixture.Path))
                {
                    fixture.Note<TextBox>(NoteScope.Team, "MarkdownEditor").Text = "Retained secondary Team writing";
                    PumpUntil(() => fixture.Model.LocalSave.Phase == LocalSavePhase.Failed &&
                        fixture.Model.GetRetainedNoteDraft(NoteScope.Team, otherId) is { IsSaving: false, Error: not null });
                    var failed = fixture.Model.GetRetainedNoteDraft(NoteScope.Team, otherId)!;
                    fixture.NoteButton(NoteScope.Team, "OngoingButton");
                    fixture.Note<ListBox>(NoteScope.Team, "NotesList").SelectedItem =
                        fixture.Note<ListBox>(NoteScope.Team, "NotesList").Items.Cast<OwnedNote>()
                            .Single(note => note.Id == otherId);
                    fixture.Note<TabItem>(NoteScope.Team, "EditTab").IsSelected = true;
                    if (fixture.Model.GetRetainedNoteDraft(NoteScope.Team, otherId) != failed ||
                        fixture.Note<TextBox>(NoteScope.Team, "MarkdownEditor").Text != "Retained secondary Team writing" ||
                        JsonSerializer.Serialize(fixture.ReadNote(NoteScope.Team, otherId)) != secondary)
                        throw new InvalidOperationException("Ongoing-note browsing changed the real failed secondary Team draft or saved writing.");
                }
                fixture.Click("NoticeRetryButton");
                PumpUntil(() => fixture.Model.CanEditLocalWorkspace &&
                    fixture.Note<Button>(NoteScope.Team, "DiscardButton").IsEnabled);
                fixture.NoteButton(NoteScope.Team, "DiscardButton");
                fixture.Note<TabItem>(NoteScope.Team, "ReadTab").IsSelected = true;
                if (JsonSerializer.Serialize(fixture.ReadNote(NoteScope.Team, otherId)) != secondary)
                    throw new InvalidOperationException("Recovery/discard changed the independent secondary Team note.");
                var preferences = Path.Combine(settings, "workspace-settings.json");
                var savedPreferences = File.ReadAllBytes(preferences);
                using (File.Open(preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fixture.NoteButton(NoteScope.Team, "DesignateButton");
                    PumpUntil(() => fixture.Model.TeamNotesView.Feedback.Contains("NOT saved", StringComparison.Ordinal));
                    preferenceError = fixture.Model.TeamNotesView.Feedback;
                    if (fixture.Model.TeamNotesView.OngoingNoteId != teamId ||
                        fixture.Model.TeamNotesView.SelectedNote?.Id != otherId ||
                        !savedPreferences.SequenceEqual(File.ReadAllBytes(preferences)))
                        throw new InvalidOperationException("Failed designation replaced the independent saved ongoing choice.");
                }
                fixture.NoteButton(NoteScope.Team, "OngoingButton");
                if (fixture.Model.TeamNotesView.SelectedNote?.Id != teamId)
                    throw new InvalidOperationException("The failed preference prevented exact ongoing-note browsing.");
            }
            fixture.Select(owner);
            var id = CreateNote(NoteScope.Person, title, body);
            var ownedTeamId = CreateNote(NoteScope.Team, "Retained owned Team note", "Owned Team writing");
            if (archived)
            {
                if (!fixture.Model.EditPersonArchived(owner, true))
                    throw new InvalidOperationException("Archived discard fixture failed: " + fixture.Model.Status);
                fixture.Select(manager);
                SelectOwner();
                fixture.Note<TabItem>(NoteScope.Team, "ReadTab").IsSelected = true;
            }
            var graph = fixture.ReadSavedGraph();
            var history = JsonSerializer.Serialize(fixture.Model.GetHistoricalTeamContexts(owner));
            using var saved = OpenSmokeWorkspace(fixture.Path);
            var document = JsonSerializer.Serialize(saved.BeginNoteDraft(NoteScope.Person, owner, id)!);
            var savedPerson = saved.GetRelationships(owner)!.Person;
            var savedTeam = saved.GetTeamNotes(owner, true).Single(note => note.Id == ownedTeamId);
            fixture.Note<TabItem>(NoteScope.Person, "EditTab").IsSelected = true;
            fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text = archived ? "" : " ";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id) is
                { IsSaving: false, Error: not null });
            var retained = fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id)!;
            if (archived) fixture.Select(manager);
            else
            {
                var review = fixture.Note<StackPanel>(NoteScope.Team, "ReviewOwners").Children.OfType<Button>()
                    .Single(button => AutomationProperties.GetAutomationId(button) == "ReviewTeamNote-" + teamId);
                review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (fixture.Control<TextBlock>("Email").Text != manager ||
                    fixture.Model.TeamNotesView.SelectedNote?.Id != teamId)
                    throw new InvalidOperationException("Actual inherited review did not browse to the exact Manager-owned note.");
            }
            SelectOwner();
            if (fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id) != retained ||
                fixture.Model.PersonNotesView.SelectedNote?.Id != id ||
                !fixture.Model.PersonNotesView.IsEditing ||
                fixture.Note<TextBox>(NoteScope.Person, "MarkdownEditor").Text != body)
                throw new InvalidOperationException("Review/Archived browsing changed the exact invalid owner draft.");
            var error = fixture.Model.LocalSaveError;
            Actual39Capture(fixture, "before-discard", new { archived, id, retained, error, preferenceError });
            fixture.NoteButton(NoteScope.Person, "DiscardButton");
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Person, id) is null);
            fixture.Arrange();
            Actual39Capture(fixture, "after-discard-before-read", new
            {
                archived, id, error, preferenceError, savedPerson, savedTeam, savedGraph = graph, savedHistory = history
            });
            if (fixture.Model.PersonNotesView.OwnerEmail != owner ||
                fixture.Model.PersonNotesView.SelectedNote is not { } selected || selected.Id != id ||
                selected.Title != title || selected.Markdown != body ||
                fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text != title ||
                fixture.Note<TextBox>(NoteScope.Person, "MarkdownEditor").Text != body ||
                fixture.ReadSavedGraph() != graph ||
                JsonSerializer.Serialize(fixture.Model.GetHistoricalTeamContexts(owner)) != history ||
                fixture.Model.GetRelationships(owner)!.Person != savedPerson ||
                fixture.Model.GetTeamNotes(owner, true).Single(note => note.Id == ownedTeamId) != savedTeam ||
                fixture.Model.GetTeamNotes(manager).Single(note => note.Id == teamId).NeedsReview != true ||
                !archived && fixture.Model.TeamNotesView.Feedback != preferenceError)
                throw new InvalidOperationException("Discard changed exact saved writing, archival/history/review or independent preference failure.");
            fixture.Note<TabItem>(NoteScope.Person, "ReadTab").IsSelected = true;
            fixture.Arrange();
            Actual39Capture(fixture, "after-actual-read", new { archived, id, preferenceError });
            var reading = fixture.Note<FlowDocumentScrollViewer>(NoteScope.Person, "Reading");
            var peer = UIElementAutomationPeer.CreatePeerForElement(reading)!;
            if (fixture.Model.PersonNotesView.IsEditing ||
                fixture.Note<TabControl>(NoteScope.Person, "Views").SelectedItem !=
                    fixture.Note<TabItem>(NoteScope.Person, "ReadTab") ||
                !reading.IsDescendantOf(fixture.Control<UserControl>("PersonNotes")) ||
                peer.GetAutomationId() != "PersonNoteReading" ||
                peer.GetPattern(PatternInterface.Text) is not ITextProvider text ||
                !text.DocumentRange.GetText(-1).Contains(body.TrimStart('#', ' '), StringComparison.Ordinal))
                failures.Add($"actual39 {(archived ? "Archived" : "history")} discard: restored saved note has no Read Text provider.");
            using var reopened = OpenSmokeWorkspace(fixture.Path);
            if (JsonSerializer.Serialize(reopened.BeginNoteDraft(NoteScope.Person, owner, id)) != document ||
                fixture.ReadSavedGraph() != graph ||
                JsonSerializer.Serialize(reopened.GetHistoricalTeamContexts(owner)) != history ||
                !archived && fixture.Model.TeamNotesView.Feedback != preferenceError)
                throw new InvalidOperationException("Read changed the independently reopened saved graph/history or independent preference failure.");
            if (fixture.Window.IsVisible || new WindowInteropHelper(fixture.Window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Actual39 functional contracts created a native or visible window.");
        }
        finally { Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings); }
    }

    private static void Actual39Capture(OrdinaryFixtureWindow fixture, string stage, object context) =>
        Console.WriteLine("ACTUAL39-FUNCTIONAL " + JsonSerializer.Serialize(new
        {
            stage, context, owner = fixture.Control<TextBlock>("Email").Text,
            fixture.Model.ProfileEdit, fixture.Model.PersonNotesView, fixture.Model.TeamNotesView,
            fixture.Model.LocalSave, fixture.Model.LocalSaveError, fixture.Model.Status, fixture.Model.CanSafelyClose,
            revision = fixture.Model.Synchronization.LocalRevision,
            noteTab = (fixture.Note<TabControl>(NoteScope.Person, "Views").SelectedItem as TabItem)?.Name,
            noteTitle = fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text,
            noteMarkdown = fixture.Note<TextBox>(NoteScope.Person, "MarkdownEditor").Text,
            readingAttached = fixture.Note<FlowDocumentScrollViewer>(NoteScope.Person, "Reading")
                .IsDescendantOf(fixture.Control<UserControl>("PersonNotes")),
            editorAttached = fixture.Note<TextBox>(NoteScope.Person, "MarkdownEditor")
                .IsDescendantOf(fixture.Control<UserControl>("PersonNotes")),
            personPeers = fixture.NotePeers(NoteScope.Person).Select(peer => peer.GetAutomationId()).ToArray()
        }));
}
