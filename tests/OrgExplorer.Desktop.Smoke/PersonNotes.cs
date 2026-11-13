using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunPersonNoteFlow(string executable, string root)
    {
        Process? process = null;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var markdown = "# Private context\nMeeting with **Avery**.\nR&amp;D &#169; &#x1F680; &lt;script&gt;\n\n"
            + $"![Remote portrait](http://127.0.0.1:{port}/portrait.png)\n\n"
            + $"<img src=\"http://127.0.0.1:{port}/html.png\">\n\n"
            + $"[External reference](http://127.0.0.1:{port}/reference)";
        try
        {
            var import = Path.Combine(root, "import.json");
            CreatePhotoFixture(Path.Combine(root, "avery.png"));
            const string initialImport = """
                {"schemaVersion":1,"batchId":"20000000-0000-4000-8000-000000000001",
                "observedAt":"2026-11-01T17:00:00Z","people":[
                {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null},
                {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"casey@example.com","photoPath":"avery.png"},
                {"email":"jordan@example.com","displayName":"Jordan Example","managerEmail":"avery@example.com"}]}
                """;
            File.WriteAllText(import, initialImport);
            var workspace = Path.Combine(root, "workspace");
            process = Launch(executable);
            var window = Window(process);
            SetSettingsVisible(window, true);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenImportView(window);
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            Await(() => Find(window, "Status").Current.Name.Contains("Import applied"));
            ReturnToPeople(window);
            OpenPerson(window, "casey@example.com");
            AssertPhotoVisible(window);
            AssertEmptyNotes(window, "Person");
            AssertEmptyNotes(window, "Team");
            CaptureVisualEvidence(window, "notes-true-empty-invitations");
            Click(window, "NewPersonNote");
            Await(() => Find(window, "PersonNoteTitle").Current.IsEnabled);
            AssertNoteView(window, "Person", editing: true);
            if (Find(window, "PersonNoteTitle").Current.Name != "Person note title" ||
                Find(window, "PersonNoteMarkdown").Current.Name != "Person note Markdown")
                throw new InvalidOperationException("Person note title and writing lack distinct accessible names.");
            Set(window, "PersonNoteTitle", "Private meeting");
            AwaitLocalWorkIdle(window);
            NavigateToManager(window, "casey@example.com");
            OpenPerson(window, "casey@example.com");
            Await(() => Find(window, "PersonNoteHeading").Current.Name == "Private meeting");
            AssertOtherPersonNotesCollapsed(window);
            AssertNoteView(window, "Person", editing: false);
            AssertEmptyDocument(window, "Person");
            CaptureVisualEvidence(window, "person-note-existing-empty-read");
            SelectTab(window, "PersonNoteEditTab");
            AssertNoteView(window, "Person", editing: true);
            AssertValue(window, "PersonNoteMarkdown", "");
            Set(window, "PersonNoteMarkdown", markdown);
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("saved locally"));
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Secondary empty note");
            AwaitLocalWorkIdle(window);
            NavigateToManager(window, "casey@example.com");
            OpenPerson(window, "casey@example.com");
            Await(() => Find(window, "PersonOngoingNoteStatus").Current.Name.Contains("Choose an ongoing note"));
            AssertOtherPersonNotesCollapsed(window);
            AssertNoEmptyInvitation(window, "Person");
            CaptureVisualEvidence(window, "person-note-ambiguous-not-empty");
            SelectNote(window, "Private meeting");
            Click(window, "UseAsOngoingPersonNote");
            AssertOtherPersonNotesCollapsed(window);
            SelectTab(window, "PersonNoteReadTab");
            AssertNoteView(window, "Person", editing: false);
            Await(() => ReadingText(window).Contains("Private context") && ReadingText(window).Contains("Meeting with Avery."));
            if (!ReadingText(window).Contains("R&D \u00a9 \U0001F680 <script>"))
                throw new InvalidOperationException("Markdown entities were lost or interpreted as HTML instead of readable text.");
            if (!Find(window, "PersonNoteOwnership").Current.Name.Contains("avery@example.com"))
                throw new InvalidOperationException("Person-note owner was not explicit.");
            if (!ReadingText(window).Contains("Image not loaded"))
                throw new InvalidOperationException("Image suppression was not visible in the rendered reading view.");
            ReadAndCaptureNote(window, "PersonNoteReading", "Meeting with Avery.", "person-note-populated-ongoing");
            SelectTab(window, "PersonNoteEditTab");
            AssertValue(window, "PersonNoteTitle", "Private meeting");
            AssertValue(window, "PersonNoteMarkdown", markdown);
            var noteFile = Directory.GetFiles(Path.Combine(workspace, "notes"), "*.md")
                .Single(file => File.ReadAllText(file).Contains("# Private context", StringComparison.Ordinal));
            using (File.Open(noteFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Set(window, "PersonNoteMarkdown", markdown + "\nRecovered draft.");
                Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
                Click(window, "UseAsOngoingPersonNote");
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                AssertNoteView(window, "Person", editing: true);
                Click(window, "NavigateManager");
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                SelectTreePerson(window, "casey@example.com", expectNavigation: false);
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                var otherWorkspace = Path.Combine(root, "other-workspace");
                SetSettingsVisible(window, true);
                Set(window, "WorkspacePath", otherWorkspace);
                Click(window, "OpenWorkspace");
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                if (Directory.Exists(otherWorkspace))
                    throw new InvalidOperationException("Unsaved Person note did not block Workspace navigation.");
                SetSettingsVisible(window, true);
                Set(window, "WorkspacePath", workspace);
                process.CloseMainWindow();
                Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("before leaving or closing"));
                Await(() => !IsVisible(window, "SettingsTitle"));
                AssertNoteView(window, "Person", editing: true);
                if (process.HasExited) throw new InvalidOperationException("App closed despite an unsaved Person note.");
                CaptureVisualEvidence(window, "person-note-retained-failure-after-rejected-close");
            }
            Click(window, "RetryPersonNoteSave");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("saved locally"));
            markdown += "\nRecovered draft.";
            SelectNote(window, "Secondary empty note");
            var preferences = Path.Combine(root, "app-settings", "workspace-settings.json");
            var previousPreference = File.ReadAllBytes(preferences);
            using (File.Open(preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(window, "UseAsOngoingPersonNote");
                Await(() => Find(window, "PersonOngoingNoteStatus").Current.Name.Contains("NOT saved"));
                if (!Find(window, "PersonNoteKind").Current.Name.Contains("Other Person note") ||
                    !File.ReadAllBytes(preferences).SequenceEqual(previousPreference))
                    throw new InvalidOperationException("Failed designation falsely changed the ongoing note.");
                CaptureVisualEvidence(window, "person-note-designation-failure");
            }
            SelectNote(window, "Private meeting");
            Click(window, "UseAsOngoingPersonNote");
            AssertOtherPersonNotesCollapsed(window);
            QueryAndSelect(window, "Secondary empty note", "avery@example.com", "Current", "Person note");
            Await(() => Find(window, "PersonNoteHeading").Current.Name == "Secondary empty note");
            AssertNoteView(window, "Person", editing: false);
            if (!Find(window, "PersonNoteKind").Current.Name.Contains("Other Person note"))
                throw new InvalidOperationException("Search silently redirected a secondary note to the ongoing note.");
            AssertEmptyDocument(window, "Person");
            CaptureVisualEvidence(window, "person-note-exact-other-empty-search");
            Click(window, "OpenOngoingPersonNote");
            Await(() => Find(window, "PersonNoteHeading").Current.Name == "Private meeting");
            NavigateToManager(window, "casey@example.com");
            if (!Find(window, "PersonNoteOwnership").Current.Name.Contains("casey@example.com"))
                throw new InvalidOperationException("Manager navigation inherited the report's private Person notes.");
            var ownerReport = Find(window, "ImportedReports").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                ?? throw new InvalidOperationException("Manager lost the note owner's imported report.");
            ((SelectionItemPattern)ownerReport.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Await(() => Find(window, "Email").Current.Name == "avery@example.com");
            Await(() => Find(window, "PersonNoteHeading").Current.Name == "Private meeting");
            AssertOtherPersonNotesCollapsed(window);
            AssertNoteView(window, "Person", editing: false);
            SelectTab(window, "PersonNoteEditTab");
            AssertValue(window, "PersonNoteMarkdown", markdown);
            SelectNote(window, "Secondary empty note");
            var secondaryId = Find(window, "PersonNoteHeading").Current.HelpText;
            Close(process);
            process = Launch(executable);
            window = Window(process);
            AwaitRestoredWorkspace(window, workspace, "avery@example.com");
            AssertRestoredNote(window, "Person", secondaryId, "Secondary empty note");
            Await(() => !Find(window, "PersonNoteHeading").Current.IsOffscreen);
            AssertEmptyDocument(window, "Person");
            CaptureVisualEvidence(window, "person-note-exact-empty-reopened");
            Click(window, "OpenOngoingPersonNote");
            Await(() => Find(window, "PersonNoteHeading").Current.Name == "Private meeting");
            AssertOtherPersonNotesCollapsed(window);
            AssertNoteView(window, "Person", editing: false);
            Await(() => ReadingText(window).Contains("Meeting with Avery."));
            Click(window, "ArchivePersonNote");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("archived locally"));
            Await(() => Find(window, "PersonOngoingNoteStatus").Current.Name.Contains("unavailable or archived"));
            AssertNoEmptyInvitation(window, "Person");
            CaptureVisualEvidence(window, "person-note-unavailable-ongoing-choice");
            ExpandOtherPersonNotes(window);
            ((TogglePattern)Find(window, "ShowArchivedPersonNotes").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            SelectNote(window, "Private meeting");
            SelectTab(window, "PersonNoteEditTab");
            if (Value(window, "PersonNoteMarkdown") != markdown)
                throw new InvalidOperationException("Archival discarded the writing.");
            SelectTab(window, "PersonNoteReadTab");
            Await(() => ReadingText(window).Contains("Private context"));
            if (listener.Pending()) throw new InvalidOperationException("Markdown rendering fetched remote content.");
            Close(process);
            Console.WriteLine("PASS: exact secondary Person-note GUID restored in Read mode before deliberate ongoing navigation.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static void SelectNote(AutomationElement window, string title)
    {
        AwaitLocalWorkIdle(window);
        ExpandOtherPersonNotes(window);
        AutomationElement? item = null;
        Await(() =>
        {
            item = Find(window, "PersonNotes").FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.NameProperty, title)));
            return item is not null;
        });
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Find(window, "PersonNoteHeading").Current.Name == title);
    }

    private static void ExpandOtherPersonNotes(AutomationElement window)
    {
        var expander = (ExpandCollapsePattern)Find(window, "OtherPersonNotes").GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (expander.Current.ExpandCollapseState != ExpandCollapseState.Expanded) expander.Expand();
    }

    private static void AssertOtherPersonNotesCollapsed(AutomationElement window)
    {
        Await(() => ((ExpandCollapsePattern)Find(window, "OtherPersonNotes").GetCurrentPattern(ExpandCollapsePattern.Pattern))
            .Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
        var list = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PersonNotes"));
        if (list is not null && !list.Current.IsOffscreen)
            throw new InvalidOperationException("Ordinary Person-note reading still requires a permanent title list.");
    }

    private static void SelectTreePerson(AutomationElement window, string email, bool expectNavigation = true)
    {
        if (expectNavigation) AwaitLocalWorkIdle(window);
        EnsurePeopleBrowsing(window);
        AutomationElement? item = null;
        Await(() => (item = Find(window, "People").FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, email))) is not null);
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (expectNavigation) Await(() => Find(window, "Email").Current.Name == email);
    }

    private static void AssertBlockedNoteNavigation(AutomationElement window, string draft)
    {
        Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("before leaving or closing"));
        Await(() => ((SelectionItemPattern)Find(window, "avery@example.com")
            .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
        if (Find(window, "Email").Current.Name != "avery@example.com" ||
            Value(window, "PersonNoteMarkdown") != draft)
            throw new InvalidOperationException("Navigation changed the owner or discarded an unsaved Person note.");
    }

    private static string Value(AutomationElement window, string id) =>
        ((ValuePattern)Find(window, id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    private static string ReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "PersonNoteReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
    private static void SelectTab(AutomationElement window, string id) =>
        ((SelectionItemPattern)Find(window, id).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    private static void AssertNoteView(AutomationElement window, string kind, bool editing)
    {
        Await(() =>
        {
            var tab = Find(window, $"{kind}Note{(editing ? "Edit" : "Read")}Tab");
            if (!((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected) return false;
            foreach (var suffix in new[] { "Title", "Markdown" })
            {
                var editor = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, $"{kind}Note{suffix}"));
                if (editing ? editor is null || !editor.Current.IsEnabled : editor is not null && !editor.Current.IsOffscreen)
                    return false;
            }
            return true;
        });
    }

    private static void AssertEmptyNotes(AutomationElement window, string kind)
    {
        if (!Find(window, $"{kind}NotesEmpty").Current.Name.StartsWith("No ", StringComparison.Ordinal) ||
            !Find(window, $"New{kind}Note").Current.IsEnabled)
            throw new InvalidOperationException($"Empty {kind} notes lost their message or New note action.");
        var list = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, $"{kind}Notes"));
        if (list is not null && !list.Current.IsOffscreen)
            throw new InvalidOperationException($"Empty {kind} notes retain a blank permanent picker.");
    }

    private static void AssertNoEmptyInvitation(AutomationElement window, string kind)
    {
        var empty = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, $"{kind}NotesEmpty"));
        if (empty is not null && !empty.Current.IsOffscreen)
            throw new InvalidOperationException($"Unavailable, ambiguous or retained {kind} writing was presented as an empty collection.");
    }

    private static void AssertEmptyDocument(AutomationElement window, string kind)
    {
        AssertNoEmptyInvitation(window, kind);
        if (!Find(window, $"{kind}NoteEmptyBody").Current.Name.Contains("Choose Edit", StringComparison.Ordinal))
            throw new InvalidOperationException($"The exact empty {kind} document lost its purposeful Edit invitation.");
    }

    private static void ReadAndCaptureNote(AutomationElement window, string readingId, string expected, string state)
    {
        var reading = Find(window, readingId);
        var range = ((TextPattern)reading.GetCurrentPattern(TextPattern.Pattern)).DocumentRange
            .FindText(expected, false, false) ?? throw new InvalidOperationException("Missing actual note prose: " + expected);
        range.ScrollIntoView(true);
        Await(() => !reading.Current.IsOffscreen && range.GetBoundingRectangles().Length > 0);
        if (reading.Current.BoundingRectangle.Width / DpiScale(window) > 601 ||
            reading.TryGetCurrentPattern(ScrollPattern.Pattern, out var inner) && ((ScrollPattern)inner).Current.VerticallyScrollable ||
            ((ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern)).Current.HorizontallyScrollable)
            throw new InvalidOperationException("Owned/current/frozen prose lost its bounded measure or reintroduced an independent reading scroll.");
        CaptureVisualEvidence(window, state);
    }
}
