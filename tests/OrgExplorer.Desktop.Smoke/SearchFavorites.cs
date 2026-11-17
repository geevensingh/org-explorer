using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSearchFavoriteFlow(string executable, string root, bool responsive = false)
    {
        Process? process = null;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var remote = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/never-load";
        var workspace = Path.Combine(root, "search-workspace");
        Directory.CreateDirectory(workspace);
        var import = Path.Combine(root, "search-import.json");
        if (responsive) CreatePhotoFixture(Path.Combine(root, "avery.png"));
        File.WriteAllText(import, SearchImport(SearchBrowsingPeople(responsive, photo: responsive), 1));
        try
        {
            process = Launch(executable);
            var window = Window(process);
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            OpenImportView(window);
            Set(window, "ImportPath", import);
            ApplyReviewedHistoryImport(window);
            QueryAndSelect(window, "007b", "avery@example.com", "Current", "Profile:");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave Preferred");
            AssertGroupedSearchResults(window);
            Await(() => Find(window, "OfficeNumberValue").Current.Name == "007B");
            if (window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchContext")) is not null)
                throw new InvalidOperationException("Person search retained a duplicate generic inspector.");
            CaptureVisualEvidence(window, "search-grouped-person-profile");
            AssertMouseSearchActivation(window);
            if (responsive)
            {
                AssertLongAndMissingFacts(window, workspace);
                AssertResponsiveOwnedTeamName(window, import);
            }
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Individual context");
            Set(window, "PersonNoteMarkdown", $"# Orchid\nPrivate Person writing\n![remote]({remote}/image.png)\n<script>fetch('{remote}')</script>" +
                (responsive ? "\n\n" + SearchBrowsingParagraphs() : ""));
            QueryAndSelect(window, "orchid", "avery@example.com", "Current", "Person note");
            AssertSearchReading(window, "Private Person writing");
            AssertSearchReading(window, "Image not loaded");
            AssertNoteView(window, "Person", editing: false);
            CaptureVisualEvidence(window, "search-ordinary-person-note-read");
            double? peopleBalance = null;
            if (responsive)
            {
                peopleBalance = AssertAdaptiveWorkspace(window);
                AssertSearchBrowsingRestoration(window);
            }
            if (Find(window, "PersonNoteHeading").Current.Name != "Individual context")
                throw new InvalidOperationException("Search did not open the saved Person note for reading.");
            Click(window, "ArchivePersonNote");
            QueryAndSelect(window, "Individual context", "avery@example.com", "Archived note — not current writing", "Person note");
            ExpandOtherPersonNotes(window);
            var archivedNotes = (TogglePattern)Find(window, "ShowArchivedPersonNotes").GetCurrentPattern(TogglePattern.Pattern);
            if (archivedNotes.Current.ToggleState != ToggleState.On) archivedNotes.Toggle();
            SelectNote(window, "Individual context");
            AssertSearchReading(window, "Private Person writing");
            if (((TogglePattern)Find(window, "ShowArchivedPersonNotes").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != ToggleState.On)
                throw new InvalidOperationException("Ordinary Other notes access did not open the retained note.");
            Click(window, "ArchivePersonNote");
            NavigateToManager(window, "morgan@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Former team");
            Set(window, "TeamNoteMarkdown", $"# Cedar\nFormer Team writing\n![remote]({remote}/team.png)");
            QueryAndSelect(window, "cedar", "avery@example.com", "Current", "Inherited Team note", "morgan@example.com");
            ExpandCurrentTeamContext(window);
            AssertSearchReading(window, "Former Team writing", readingId: "InheritedTeamReading");
            CaptureVisualEvidence(window, "search-current-inherited-note");
            SelectTreePerson(window, "alex@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Current team");
            Set(window, "TeamNoteMarkdown", "# Pine\nNew current Team writing");

            QueryAndSelect(window, "007b", "avery@example.com", "Current", "Profile:");
            using (new FileStream(Path.Combine(workspace, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Click(window, "ToggleFavorite");
                Await(() => Status(window).Contains("NOT saved"));
                if (Find(window, "FavoriteStatus").Current.Name.StartsWith("Marked"))
                    throw new InvalidOperationException("Failed favorite save falsely showed a saved star.");
                QueryAndSelect(window, "alex@example", "alex@example.com", "Current", "Profile:", expectNavigation: false);
                if (Find(window, "Email").Current.Name != "avery@example.com")
                    throw new InvalidOperationException("Search discarded an unsaved favorite action.");
                CaptureVisualEvidence(window, "search-rejected-favorite-writing-retained");
                process.CloseMainWindow();
                Await(() => Status(window).Contains("Unsaved local changes prevent safe closing"));
            }
            Click(window, "RetryProfileSave");
            Await(() => Find(window, "ToggleFavorite").Current.Name == "Unmark favorite");
            AssertPreferredNameReading(window, "Ave Preferred", "Edit");
            SelectFavorite(window, "alex@example.com");
            SelectFavorite(window, "avery@example.com");
            AssertPreferredNameReading(window, "Ave Preferred", "Edit");
            Await(() => Find(window, "Email").Current.Name == "avery@example.com");
            File.WriteAllText(import, SearchImport("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 3));
            ApplyReviewedHistoryImport(window);
            QueryAndSelect(window, "cedar", "avery@example.com", "Historical", "Frozen Team note");
            AssertOrdinarySearchProfile(window, "avery@example.com");
            ExpandHistory(window);
            AssertSearchReading(window, "Former Team writing", readingId: "HistoricalTeamReading");
            AssertSearchReading(window, "Image not loaded", readingId: "HistoricalTeamReading");
            if (!HistoricalReadingText(window).Contains("Exact effective date unknown") ||
                !HistoricalEvidenceText(window).Contains("effective date unknown") ||
                !HistoricalReadingText(window).Contains("morgan@example.com"))
                throw new InvalidOperationException("Historical search omitted frozen ownership/provenance.");
            CaptureVisualEvidence(window, "search-person-ordinary-frozen-history-access");
            QueryAndSelect(window, "pine", "avery@example.com", "Current", "Inherited Team note");
            ExpandCurrentTeamContext(window);
            AssertSearchReading(window, "New current Team writing", readingId: "InheritedTeamReading");
            RequestPersonArchival(window, archived: true);
            QueryAndSelect(window, "007b", "avery@example.com", "Archived person", "Profile:");
            if (!Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived"))
                throw new InvalidOperationException("Archived match opened a current profile.");
            QueryAndSelect(window, "cedar", "avery@example.com", "Historical / Archived person", "Frozen Team note");
            ExpandHistory(window);
            AssertSearchReading(window, "Former Team writing", readingId: "HistoricalTeamReading");
            CaptureVisualEvidence(window, "search-archived-person-frozen-note");
            if (listener.Pending()) throw new InvalidOperationException("Search or selected current/frozen Markdown fetched remote content.");
            var beforeReopen = OrgExplorer.TestFixtures.WorkspaceFixtures.Bytes(workspace);
            Close(process);

            process = Launch(executable);
            window = Window(process);
            AwaitUnavailableView(window, workspace);
            AssertWorkspaceBytes(workspace, beforeReopen);
            SelectFavorite(window, "avery@example.com");
            if (responsive)
                Await(() => Math.Abs(RenderedPeoplePercent(window) - peopleBalance!.Value) < .5);
            if (!Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived"))
                throw new InvalidOperationException("Persisted favorite did not reopen the Archived profile.");
            QueryAndSelect(window, "cedar", "avery@example.com", "Historical", "Frozen Team note");
            ExpandHistory(window);
            AssertSearchReading(window, "Former Team writing", readingId: "HistoricalTeamReading");
            Click(window, "ToggleFavorite");
            Await(() => Find(window, "ToggleFavorite").Current.Name == "Mark favorite");
            Close(process);
            Console.WriteLine("PASS: actual WPF grouped Person search, ordinary profile/note/history access, retained writing, archived access and reopen proportions.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static string SearchBrowsingPeople(bool responsive, bool photo = false) => $$"""
            {"email":"avery@example.com","displayName":"Avery Directory","preferredName":"Ave Preferred","alias":"avhandle",
             "jobTitle":"Scientist","department":"Research","officeBuilding":"Juniper","officeNumber":"007B",
             "officeState":"Ontario","officeCountry":"Canada","linkedInProfileUrl":"https://www.linkedin.com/in/fictitious-avery",
             "managerEmail":"morgan@example.com"{{(photo ? ",\"photoPath\":\"avery.png\"" : "")}}},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null,"isFavorite":true}
            """ + (responsive ? """
            ,{"email":"longread@example.com","displayName":"Alexandra Reference with a deliberately long Directory name for accessible Person reading",
              "alias":"longreading","officeBuilding":"South campus research and collaboration building with a deliberately long descriptive location",
              "officeState":"A long state or province description that must wrap without shrinking the ordinary reading type",
              "managerEmail":null}
            """ : "") + "," + string.Join(",", Enumerable.Range(1, 40).Select(i => $$"""
                {"email":"browse{{i:00}}@example.com","displayName":"Z Browse {{i:00}}","managerEmail":null}
                """));

    private static string SearchBrowsingParagraphs() => string.Join("\n\n", Enumerable.Range(1, 32).Select(i =>
        $"Private Person writing paragraph {i}. Keep decisions and their context readable while the same Person identity remains anchored."));

    private static string SearchImport(string records, int id) => $$"""
        {"schemaVersion":1,"batchId":"13000000-0000-4000-8000-{{id:000000000000}}",
         "observedAt":"2026-11-06T17:00:00Z","people":[{{records}}]}
        """;

    private static void QueryAndSelect(AutomationElement window, string query, string email, string context,
        string source, string? owner = null, bool expectNavigation = true)
    {
        if (expectNavigation) AwaitLocalWorkIdle(window);
        var beforeEmail = CurrentPersonEmail(window);
        string? PersonNoteIdentity() => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PersonNoteHeading"))?.Current.HelpText;
        var beforeNote = PersonNoteIdentity();
        Set(window, "SearchQuery", query);
        AutomationElement? result = null;
        Await(() =>
        {
            result = Find(window, "SearchResults").FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>()
                .SingleOrDefault(r => r.Current.AutomationId == email && r.Current.Name.Contains($"({email})") &&
                    r.Current.HelpText.Contains(context) && r.Current.HelpText.Contains(source) &&
                    (owner is null || r.Current.HelpText.Contains($"({owner})")));
            return result is not null;
        });
        ((SelectionItemPattern)result!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (CurrentPersonEmail(window) != beforeEmail || PersonNoteIdentity() != beforeNote)
            throw new InvalidOperationException("Typing or highlighting opened a Person or replaced exact note reading before activation.");
        ActivateSearchRow(result);
        if (expectNavigation)
            Await(() => OrdinarySearchProfileReady(window, email));
        else
            Await(() => Status(window).Contains("Navigation blocked", StringComparison.OrdinalIgnoreCase) ||
                Status(window).Contains("before leaving", StringComparison.OrdinalIgnoreCase));
        if (!expectNavigation && (CurrentPersonEmail(window) != beforeEmail || PersonNoteIdentity() != beforeNote ||
            Value(window, "SearchQuery") != query ||
            !((SelectionItemPattern)result.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected))
            throw new InvalidOperationException("Rejected search activation changed the owner, exact note, query or highlighted Person.");
    }

    private static void ActivateSearchRow(AutomationElement result, Action<Func<bool>>? awaitReady = null)
    {
        awaitReady ??= Await;
        ((SelectionItemPattern)result.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        result.SetFocus();
        awaitReady(() => result.Current.HasKeyboardFocus);
        PressCopyKey(0x0D);
    }

    private static bool OrdinarySearchProfileReady(AutomationElement window, string email)
    {
        return Find(window, "Email").Current.Name == email && IsVisible(window, "ProfileHeading") &&
            !IsVisible(window, "SaveProfile") &&
            (!IsVisible(window, "PersonNoteEditTab") ||
                ((SelectionItemPattern)Find(window, "PersonNoteReadTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected) &&
            (!IsVisible(window, "TeamNoteEditTab") ||
                ((SelectionItemPattern)Find(window, "TeamNoteReadTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
    }

    private static void AssertOrdinarySearchProfile(AutomationElement window, string email)
    {
        Await(() => OrdinarySearchProfileReady(window, email));
        if (((ExpandCollapsePattern)Find(window, "HistoryAccess").GetCurrentPattern(ExpandCollapsePattern.Pattern))
            .Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
            throw new InvalidOperationException("A historical-source Person search automatically opened History.");
    }

    private static void AssertGroupedSearchResults(AutomationElement window)
    {
        Set(window, "SearchQuery", "avery");
        Await(() => Find(window, "SearchCount").Current.Name == "People found: 1 — local profiles and notes");
        var rows = Find(window, "SearchResults").FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().ToArray();
        if (rows.Length != 1 || rows[0].Current.AutomationId != "avery@example.com" ||
            !rows[0].Current.Name.Contains("Ave Preferred") ||
            !rows[0].Current.HelpText.Contains("Profile: email") || !rows[0].Current.HelpText.Contains("Directory name") ||
            rows[0].FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.NameProperty, "avery@example.com"))) is not { Current.IsOffscreen: false })
            throw new InvalidOperationException("Multiple matching fields must render one named Person with visible email and retained field attribution.");
        CaptureVisualEvidence(window, "search-one-person-multiple-fields");
    }

    private static void AssertMouseSearchActivation(AutomationElement window)
    {
        EnsurePeopleBrowsing(window);
        SelectTreePerson(window, "morgan@example.com");
        Await(() => Find(window, "Email").Current.Name == "morgan@example.com");
        Set(window, "SearchQuery", "avhandle");
        AutomationElement? result = null;
        Await(() => (result = Find(window, "SearchResults").FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))) is not null);
        var point = result!.GetClickablePoint();
        if (!SetCursorPos((int)point.X, (int)point.Y))
            throw new InvalidOperationException("The hosted desktop could not position the search-result pointer.");
        mouse_event(2, 0, 0, 0, 0);
        mouse_event(4, 0, 0, 0, 0);
        Await(() => OrdinarySearchProfileReady(window, "avery@example.com") && Find(window, "AliasValue").Current.Name == "avhandle");
        CaptureVisualEvidence(window, "search-mouse-ordinary-profile");
    }

    private static void SelectFavorite(AutomationElement window, string email)
    {
        AwaitLocalWorkIdle(window);
        EnsurePeopleBrowsing(window);
        AutomationElement? result = null;
        Await(() => (result = Find(window, "Favorites").FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, email))) is not null);
        ((SelectionItemPattern)result!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Find(window, "Email").Current.Name == email);
    }

    private static void AssertSearchReading(AutomationElement window, string expected, string? absent = null,
        string readingId = "PersonNoteReading")
    {
        Await(() => ((TextPattern)Find(window, readingId).GetCurrentPattern(TextPattern.Pattern))
            .DocumentRange.GetText(-1).Contains(expected));
        var text = ((TextPattern)Find(window, readingId).GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
        if (absent is not null && text.Contains(absent)) throw new InvalidOperationException("Search opened later writing in frozen context.");
    }
}
