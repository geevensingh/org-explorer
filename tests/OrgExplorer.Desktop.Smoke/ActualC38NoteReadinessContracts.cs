using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyActualC38NoteReadinessContracts(Type windowType, string root)
    {
        var failures = new List<string>();
        VerifyC38ArchivedPersonReading(windowType, root, failures);
        VerifyC38RepeatedInheritedReview(windowType, root, failures);
        VerifyCompiledLocalSaveRecovery(windowType, Path.Combine(root, "late-recovery"), (window, model, id) =>
        {
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            T Team<T>(string name) where T : FrameworkElement => (T)Control<UserControl>("TeamNotes").FindName(name);
            using var independent = OpenSmokeWorkspace(model.Compatibility.WorkspacePath!);
            var saved = independent.BeginNoteDraft(NoteScope.Team, "morgan@example.com", id);
            var sameOwner = Control<TextBlock>("Email").Text == "morgan@example.com";
            var sameNote = Team<TextBlock>("NoteHeading").Text == "Compiled retained title" &&
                AutomationProperties.GetHelpText(Team<TextBlock>("NoteHeading")) == id.ToString();
            var editorEnabled = Team<TextBox>("MarkdownEditor").IsEnabled;
            var retainedDraft = model.GetRetainedNoteDraft(NoteScope.Team, id) is not null;
            var committedExact = saved is { OwnerEmail: "morgan@example.com", Title: "Compiled retained title",
                Markdown: "Compiled newer exact writing" } && saved.Id == id;
            var notice = Control<FrameworkElement>("OperationalNotice");
            Console.WriteLine("ACTUALC38-NOTE-READINESS " + JsonSerializer.Serialize(new
            {
                stage = "late-commit-quiet-recovery", sameOwner, sameNote, editorEnabled, retainedDraft, committedExact,
                model.LocalSave, model.LocalSaveError, model.CanSafelyClose, model.IsSynchronizing,
                noticeVisible = notice.Visibility == Visibility.Visible,
                noticeStatus = AutomationProperties.GetItemStatus(notice),
                saved, profile = independent.GetRelationships("morgan@example.com")!.Person,
                independentPersonNote = independent.GetPersonNotes("morgan@example.com").Single()
            }));
            if (!IsSmokeLateNoteCommitComplete(sameOwner, sameNote, editorEnabled, retainedDraft, committedExact,
                Team<TextBox>("NoteTitle").Text == "Compiled retained title" &&
                Team<TextBox>("MarkdownEditor").Text == "Compiled newer exact writing"))
                failures.Add("c38 late recovery: exact same-owner current editor and independently saved complete document did not establish completion.");
            PumpUntil(() => model.CanSafelyClose &&
                Control<FrameworkElement>("OperationalNotice").Visibility == Visibility.Collapsed);
            var feedback = CaptureCompiled5faRecoveryFeedback(window);
            var serialized = JsonSerializer.Serialize(feedback);
            var reopened = JsonSerializer.Deserialize<SmokeRecoveryFeedback>(serialized);
            if (reopened != feedback || feedback.NoticePresent || feedback.ItemStatus is not null ||
                feedback.Summary is not null || feedback.Activity != Control<TextBlock>("WorkflowActivity").Text ||
                feedback.Status != Control<TextBlock>("StatusText").Text)
                throw new InvalidOperationException("Quiet recovery evidence did not explicitly preserve absence and actual ordinary feedback.");
            Console.WriteLine("ACTUAL5FA-CONTINUING " + serialized);
        }, (stage, window, model) =>
        {
            var feedback = CaptureCompiled5faRecoveryFeedback(window);
            if (!feedback.NoticePresent || feedback.ItemStatus !=
                $"Local save: {model.LocalSave.Phase}; outcome: {model.LocalSave.Outcome}" ||
                string.IsNullOrWhiteSpace(feedback.Summary) ||
                stage == "failed" && !feedback.Summary.Contains("NOT saved", StringComparison.Ordinal) ||
                stage == "stalled" && !feedback.Summary.Contains("3 seconds", StringComparison.Ordinal))
                throw new InvalidOperationException("Genuine failed/stalled recovery evidence lost actual attention feedback.");
        });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS: actual archived Person/Team Read, repeated inherited review, restore/Edit/frozen-history continuation and quiet recovery evidence preserve exact owner/id/writing/review, independent errors/profile, immutable bases and mode-only nonmutation; no native windows.");
    }

    private static void VerifyC38ArchivedPersonReading(Type windowType, string root, List<string> failures)
    {
        using var fixture = new OrdinaryFixtureWindow(windowType, Path.Combine(root, "archived-person-read"));
        const NoteScope scope = NoteScope.Person;
        const string owner = "avery@example.com";
        const string title = "Private meeting";
        const string markdown = "# Private context\nMeeting with Avery.\n\nR&D\n\nRecovered draft.";
        fixture.Select(owner);
        fixture.NoteButton(scope, "NewButton");
        var id = fixture.Model.PersonNotesView.SelectedNote!.Id;
        fixture.Note<TextBox>(scope, "NoteTitle").Text = title;
        fixture.Note<TextBox>(scope, "MarkdownEditor").Text = markdown;
        PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, id) is null && fixture.Model.CanSafelyClose);
        fixture.NoteButton(scope, "DesignateButton");
        PumpUntil(() => fixture.Model.PersonNotesView.OngoingNoteId == id &&
            fixture.Model.LocalSave.OutstandingRequests == 0);
        fixture.NoteButton(scope, "NewButton");
        var secondary = fixture.Model.PersonNotesView.SelectedNote!.Id;
        fixture.Note<TextBox>(scope, "NoteTitle").Text = "Secondary empty note";
        PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, secondary) is null && fixture.Model.CanSafelyClose);
        fixture.NoteButton(scope, "OngoingButton");
        fixture.NoteButton(scope, "ArchiveButton");
        PumpUntil(() => fixture.Model.GetPersonNotes(owner, true).Single(note => note.Id == id).Archived);
        fixture.Note<CheckBox>(scope, "IncludeArchived").IsChecked = true;
        fixture.Note<ListBox>(scope, "NotesList").SelectedItem =
            fixture.Note<ListBox>(scope, "NotesList").Items.Cast<OwnedNote>().Single(note => note.Id == id);
        var saved = fixture.ReadSavedGraph();
        var revision = fixture.Model.Synchronization.LocalRevision;
        fixture.Note<TabItem>(scope, "ReadTab").IsSelected = true;
        fixture.Arrange();
        var editor = fixture.Note<TextBox>(scope, "MarkdownEditor");
        var reading = fixture.Note<FlowDocumentScrollViewer>(scope, "Reading");
        CaptureC38(fixture, "archived-person-selected", new
        {
            id, revision, owner, title, markdown,
            editorAttached = editor.IsDescendantOf(fixture.Control<UserControl>("PersonNotes")),
            readingAttached = reading.IsDescendantOf(fixture.Control<UserControl>("PersonNotes"))
        });
        var peer = UIElementAutomationPeer.CreatePeerForElement(reading)!;
        if (editor.IsDescendantOf(fixture.Control<UserControl>("PersonNotes")) ||
            !reading.IsDescendantOf(fixture.Control<UserControl>("PersonNotes")) ||
            peer.GetPattern(PatternInterface.Text) is not ITextProvider text ||
            !text.DocumentRange.GetText(-1).Contains("Private context", StringComparison.Ordinal))
            failures.Add("c38 archived Person note: actual Read did not expose its saved document without an Edit Markdown provider.");
        using var independent = OpenSmokeWorkspace(fixture.Path);
        var note = independent.GetPersonNotes(owner, true).Single(note => note.Id == id);
        if (!note.Archived || note.OwnerEmail != owner || note.Title != title || note.Markdown != markdown ||
            fixture.Model.PersonNotesView.SelectedNote?.Id != id || fixture.Model.PersonNotesView.IsEditing ||
            fixture.ReadSavedGraph() != saved || fixture.Model.Synchronization.LocalRevision != revision ||
            fixture.Model.GetRetainedNoteDraft(scope, id) is not null ||
            !new TextRange(reading.Document.ContentStart, reading.Document.ContentEnd).Text.Contains("Recovered draft.", StringComparison.Ordinal))
            throw new InvalidOperationException("Archived selection changed exact saved Person writing, identity, Read content, revision or retention.");
    }

    private static void VerifyC38RepeatedInheritedReview(Type windowType, string root, List<string> failures)
    {
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var settings = Path.Combine(root, "review-settings");
        Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
        try
        {
            using var fixture = new OrdinaryFixtureWindow(windowType, Path.Combine(root, "repeated-inherited-review"));
            const string owner = "avery@example.com";
            const string manager = "morgan@example.com";
            fixture.Select(manager);
            fixture.NoteButton(NoteScope.Team, "NewButton");
            var teamId = fixture.Model.TeamNotesView.SelectedNote!.Id;
            fixture.Note<TextBox>(NoteScope.Team, "NoteTitle").Text = "Morgan context";
            fixture.Note<TextBox>(NoteScope.Team, "MarkdownEditor").Text = "# Frozen direct\nOriginal direct";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Team, teamId) is null && fixture.Model.CanSafelyClose);
            fixture.NoteButton(NoteScope.Team, "DesignateButton");
            PumpUntil(() => fixture.Model.TeamNotesView.OngoingNoteId == teamId && fixture.Model.LocalSave.OutstandingRequests == 0);
            fixture.NoteButton(NoteScope.Team, "NewButton");
            var otherId = fixture.Model.TeamNotesView.SelectedNote!.Id;
            fixture.Note<TextBox>(NoteScope.Team, "NoteTitle").Text = "Secondary empty Team note";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Team, otherId) is null && fixture.Model.CanSafelyClose);
            var preferences = Path.Combine(settings, "workspace-settings.json");
            using (File.Open(preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fixture.NoteButton(NoteScope.Team, "DesignateButton");
                PumpUntil(() => fixture.Model.TeamNotesView.Feedback.Contains("NOT saved", StringComparison.Ordinal));
            }
            fixture.NoteButton(NoteScope.Team, "OngoingButton");
            fixture.Click("EditProfileButton");
            fixture.Control<TextBox>("JobTitle").Text = "Director";
            fixture.Click("SaveProfileButton");
            PumpUntil(() => fixture.Model.ProfileEdit is null && fixture.Control<Button>("EditProfileButton").IsEnabled);
            fixture.Select(owner);
            fixture.NoteButton(NoteScope.Person, "NewButton");
            var personId = fixture.Model.PersonNotesView.SelectedNote!.Id;
            fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text = "Primary Person context";
            fixture.Note<TextBox>(NoteScope.Person, "MarkdownEditor").Text = "Everyday individual writing";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Person, personId) is null && fixture.Model.CanSafelyClose);
            var graph = fixture.ReadSavedGraph();
            var history = JsonSerializer.Serialize(fixture.Model.GetHistoricalTeamContexts(owner));
            var preferenceError = fixture.Model.TeamNotesView.Feedback;
            fixture.Note<Expander>(NoteScope.Team, "CurrentTeamContext").IsExpanded = true;
            fixture.Note<TextBox>(NoteScope.Person, "NoteTitle").Text = " ";
            PumpUntil(() => fixture.Model.GetRetainedNoteDraft(NoteScope.Person, personId) is { IsSaving: false, Error: not null });
            var retained = fixture.Model.GetRetainedNoteDraft(NoteScope.Person, personId)!;
            Button Review() => fixture.Note<StackPanel>(NoteScope.Team, "ReviewOwners").Children.OfType<Button>()
                .Single(button => AutomationProperties.GetAutomationId(button) == "ReviewTeamNote-" + teamId);
            Review().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (fixture.Control<TextBlock>("Email").Text != manager || fixture.Model.TeamNotesView.SelectedNote?.Id != teamId)
                throw new InvalidOperationException("First actual inherited review did not select the exact Manager-owned note.");
            fixture.Select(owner);
            if (fixture.Model.GetRetainedNoteDraft(NoteScope.Person, personId) != retained)
                throw new InvalidOperationException("The first review changed the independent retained Person draft/base/error.");
            fixture.NoteButton(NoteScope.Person, "DiscardButton");
            fixture.Note<TabItem>(NoteScope.Person, "ReadTab").IsSelected = true;
            fixture.Arrange();
            var disclosure = fixture.Note<Expander>(NoteScope.Team, "CurrentTeamContext");
            disclosure.IsExpanded = true;
            fixture.Arrange();
            var action = Review();
            CaptureC38(fixture, "second-inherited-review", new
            {
                teamId, personId, disclosure.IsExpanded, action.IsEnabled,
                actionName = AutomationProperties.GetName(action), preferenceError,
                review = fixture.Model.GetTeamNotes(manager).Single(note => note.Id == teamId)
            });
            if (!disclosure.IsExpanded || !action.IsEnabled)
                failures.Add("c38 repeated inherited review: the native action waits inside a newly collapsed disclosure.");
            else
            {
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (fixture.Control<TextBlock>("Email").Text != manager ||
                    fixture.Model.TeamNotesView.SelectedNote?.Id != teamId ||
                    fixture.Model.TeamNotesView.IsEditing)
                    throw new InvalidOperationException("The second actual review did not reach exact Manager-owned Read.");
            }
            if (fixture.ReadSavedGraph() != graph ||
                JsonSerializer.Serialize(fixture.Model.GetHistoricalTeamContexts(owner)) != history ||
                fixture.Model.TeamNotesView.Feedback != preferenceError ||
                !fixture.Model.GetTeamNotes(manager).Single(note => note.Id == teamId).NeedsReview)
                throw new InvalidOperationException("Review/discard changed saved writing, history, review eligibility or independent preference failure.");
            Verify5faTeamArchiveContinuation(fixture, teamId, failures);
        }
        finally { Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings); }
    }

    private static void Verify5faTeamArchiveContinuation(OrdinaryFixtureWindow fixture, Guid id, List<string> failures)
    {
        const NoteScope scope = NoteScope.Team;
        const string owner = "morgan@example.com";
        const string report = "avery@example.com";
        const string title = "Morgan context";
        const string markdown = "# Frozen direct\nOriginal direct";
        void Act(string name)
        {
            if (!fixture.Note<Button>(scope, name).IsEnabled)
                throw new InvalidOperationException("The continuing Team action is ineligible: " + name);
            fixture.NoteButton(scope, name);
        }
        Act("DesignateButton");
        PumpUntil(() => fixture.Model.TeamNotesView.OngoingNoteId == id &&
            fixture.Model.LocalSave.OutstandingRequests == 0);
        Act("ConfirmButton");
        PumpUntil(() => !fixture.Model.GetTeamNotes(owner).Single(note => note.Id == id).NeedsReview &&
            fixture.Model.LocalSave.OutstandingRequests == 0);
        Act("ArchiveButton");
        PumpUntil(() => fixture.Model.GetTeamNotes(owner, true).Single(note => note.Id == id).Archived &&
            fixture.Model.TeamNotesView.SelectedNote is null);
        fixture.Note<CheckBox>(scope, "IncludeArchived").IsChecked = true;
        fixture.Note<ListBox>(scope, "NotesList").SelectedItem =
            fixture.Note<ListBox>(scope, "NotesList").Items.Cast<OwnedNote>().Single(note => note.Id == id);
        var savedGraph = fixture.ReadSavedGraph();
        var revision = fixture.Model.Synchronization.LocalRevision;
        var archived = fixture.Model.GetTeamNotes(owner, true).Single(note => note.Id == id);
        fixture.Note<TabItem>(scope, "ReadTab").IsSelected = true;
        fixture.Arrange();
        var editor = fixture.Note<TextBox>(scope, "MarkdownEditor");
        var reading = fixture.Note<FlowDocumentScrollViewer>(scope, "Reading");
        CaptureC38(fixture, "5fa-archived-Team-Read", new
        {
            archived, revision, tab = (fixture.Note<TabControl>(scope, "Views").SelectedItem as TabItem)?.Name,
            editorAttached = editor.IsDescendantOf(fixture.Control<UserControl>("TeamNotes")),
            readingAttached = reading.IsDescendantOf(fixture.Control<UserControl>("TeamNotes"))
        });
        var peer = UIElementAutomationPeer.CreatePeerForElement(reading)!;
        if (editor.IsDescendantOf(fixture.Control<UserControl>("TeamNotes")) ||
            !reading.IsDescendantOf(fixture.Control<UserControl>("TeamNotes")) ||
            peer.GetPattern(PatternInterface.Text) is not ITextProvider text ||
            !text.DocumentRange.GetText(-1).Contains("Original direct", StringComparison.Ordinal))
            failures.Add("5fa archived Team Read did not expose its saved document without an Edit Markdown provider.");
        using (var independent = OpenSmokeWorkspace(fixture.Path))
            if (independent.GetTeamNotes(owner, true).Single(note => note.Id == id) != archived ||
                archived.OwnerEmail != owner || archived.Title != title || archived.Markdown != markdown ||
                !archived.Archived || archived.NeedsReview || fixture.Model.TeamNotesView.SelectedNote?.Id != id ||
                fixture.Model.TeamNotesView.IsEditing || fixture.ReadSavedGraph() != savedGraph ||
                fixture.Model.Synchronization.LocalRevision != revision ||
                fixture.Model.GetRetainedNoteDraft(scope, id) is not null)
                throw new InvalidOperationException("Archived Team selection changed exact confirmed saved writing, identity, current Read or revision.");
        Act("ArchiveButton");
        PumpUntil(() => !fixture.Model.GetTeamNotes(owner, true).Single(note => note.Id == id).Archived &&
            fixture.Model.TeamNotesView.SelectedNote?.Id == id && fixture.Model.LocalSave.OutstandingRequests == 0);
        var restoredGraph = fixture.ReadSavedGraph();
        var restoredRevision = fixture.Model.Synchronization.LocalRevision;
        var restoredBasis = fixture.Model.BeginNoteDraft(scope, owner, id)!;
        fixture.Note<TabItem>(scope, "EditTab").IsSelected = true;
        fixture.Arrange();
        if (!editor.IsDescendantOf(fixture.Control<UserControl>("TeamNotes")) || !editor.IsEnabled ||
            editor.Text != markdown || fixture.Note<TextBox>(scope, "NoteTitle").Text != title ||
            restoredBasis.Id != id || restoredBasis.OwnerEmail != owner || restoredBasis.Markdown != markdown ||
            fixture.ReadSavedGraph() != restoredGraph || fixture.Model.Synchronization.LocalRevision != restoredRevision ||
            fixture.Model.GetRetainedNoteDraft(scope, id) is not null)
            throw new InvalidOperationException("Restored Team Edit did not realize exact current writable data/base without a mode-only write.");
        fixture.Note<TabItem>(scope, "ReadTab").IsSelected = true;
        fixture.Click("EditProfileButton");
        fixture.Control<TextBox>("JobTitle").Text = "Senior director";
        fixture.Click("SaveProfileButton");
        PumpUntil(() => fixture.Model.ProfileEdit is null && fixture.Control<Button>("EditProfileButton").IsEnabled);
        if (!fixture.Model.GetTeamNotes(owner).Single(note => note.Id == id).NeedsReview)
            throw new InvalidOperationException("The continuing profile Save lost restored Team review eligibility.");
        if (!fixture.Model.EditPersonManager(report, null))
            throw new InvalidOperationException("Could not close the continuing report's actual former Team context: " + fixture.Model.Status);
        var historical = JsonSerializer.Serialize(fixture.Model.GetHistoricalTeamContexts(report));
        if (!fixture.Model.GetHistoricalTeamContexts(report).SelectMany(context => context.TeamNotes)
            .Any(note => note.Id == id && note.OwnerEmail == owner && note.Markdown == markdown))
            throw new InvalidOperationException("The continuing history did not freeze the exact restored Team writing.");
        fixture.Note<TabItem>(scope, "EditTab").IsSelected = true;
        fixture.Note<TextBox>(scope, "MarkdownEditor").Text = "# Later direct\nLater writing";
        PumpUntil(() => fixture.Model.GetRetainedNoteDraft(scope, id) is null && fixture.Model.CanSafelyClose);
        using var latest = OpenSmokeWorkspace(fixture.Path);
        if (latest.GetTeamNotes(owner).Single(note => note.Id == id).Markdown != "# Later direct\nLater writing" ||
            JsonSerializer.Serialize(latest.GetHistoricalTeamContexts(report)) != historical)
            throw new InvalidOperationException("The continuing active Team edit changed frozen context or failed to save current writing.");
    }

    private static SmokeRecoveryFeedback CaptureCompiled5faRecoveryFeedback(Window window)
    {
        var notice = (FrameworkElement)window.FindName("OperationalNotice");
        return CaptureSmokeRecoveryFeedback(notice.Visibility == Visibility.Visible, () =>
        {
            if (notice.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Missing actual UI control: OperationalNotice");
            return AutomationProperties.GetItemStatus(notice);
        }, () => ((TextBlock)window.FindName("NoticeSummary")).Text,
            ((TextBlock)window.FindName("WorkflowActivity")).Text, ((TextBlock)window.FindName("StatusText")).Text);
    }

    private static void CaptureC38(OrdinaryFixtureWindow fixture, string stage, object context)
    {
        if (fixture.Window.IsVisible || new WindowInteropHelper(fixture.Window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The c38 functional contracts created a native or visible window.");
        Console.WriteLine("ACTUALC38-NOTE-READINESS " + JsonSerializer.Serialize(new
        {
            stage, context, owner = fixture.Control<TextBlock>("Email").Text,
            fixture.Model.PersonNotesView, fixture.Model.TeamNotesView, fixture.Model.LocalSave,
            fixture.Model.LocalSaveError, fixture.Model.CanSafelyClose,
            personTab = (fixture.Note<TabControl>(NoteScope.Person, "Views").SelectedItem as TabItem)?.Name
        }));
    }
}
