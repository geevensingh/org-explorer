using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
            AssertQueryKeyboardNavigation(window, responsive);
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
            if (responsive)
            {
                ResizeWindow(window, 700, 900);
                QueryAndSelect(window, "007b", "avery@example.com", "Archived person", "Profile:");
                TypeSearchQuery(window, "007b");
                AssertQueryHighlight(window, "avery@example.com");
                ClickSearchPerson(window, "avery@example.com");
                AssertAcceptedSearchBrowsing(window, "avery@example.com", archived: true);
                CaptureVisualEvidence(window, "search-mouse-archived-compact");
                ResizeWindow(window, 1100, 900);
            }
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
            ,{"email":"v-longread@example.com","displayName":"Alexandra Reference with a deliberately long Directory name for accessible Person reading",
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
        string source, string? owner = null, bool expectNavigation = true, string? diagnosticsContext = null)
    {
        if (expectNavigation) AwaitLocalWorkIdle(window);
        var beforeEmail = CurrentPersonEmail(window);
        string? PersonNoteIdentity() => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PersonNoteHeading"))?.Current.HelpText;
        var beforeNote = PersonNoteIdentity();
        TypeSearchQuery(window, query);
        AutomationElement? result = null;
        string[] rowEmails = [];
        var archived = false;
        Await(() =>
        {
            var rows = SearchRows(window);
            result = rows.SingleOrDefault(r => r.Current.AutomationId == email && r.Current.Name.Contains($"({email})") &&
                r.Current.HelpText.Contains(context) && r.Current.HelpText.Contains(source) &&
                (owner is null || r.Current.HelpText.Contains($"({owner})")));
            if (result is null) return false;
            archived = result.Current.HelpText.Contains("Archived person", StringComparison.Ordinal);
            rowEmails = rows.Select(row => row.Current.AutomationId).ToArray();
            return true;
        });
        var index = Array.IndexOf(rowEmails, email);
        if (index < 0) throw new InvalidOperationException("The queried Person disappeared before keyboard navigation.");
        AssertQueryHighlight(window, rowEmails[0]);
        for (var i = 0; i < index; i++)
        {
            PressCopyKey(0x28);
            AssertQueryHighlight(window, rowEmails[i + 1]);
        }
        if (CurrentPersonEmail(window) != beforeEmail || PersonNoteIdentity() != beforeNote)
            throw new InvalidOperationException("Typing or highlighting opened a Person or replaced exact note reading before activation.");
        PressCopyKey(0x0D);
        if (expectNavigation)
        {
            Await(() => OrdinarySearchProfileReady(window, email));
            AssertAcceptedSearchBrowsing(window, email, archived, diagnosticsContext is null ? null :
                $"caller='{diagnosticsContext}'; requestedQuery='{query}'; requestedContext='{context}'; " +
                $"source='{source}'; requestedOwner='{owner ?? "none"}'");
        }
        else
            Await(() => Status(window).Contains("Navigation blocked", StringComparison.OrdinalIgnoreCase) ||
                Status(window).Contains("before leaving", StringComparison.OrdinalIgnoreCase));
        if (!expectNavigation && (CurrentPersonEmail(window) != beforeEmail || PersonNoteIdentity() != beforeNote ||
            Value(window, "SearchQuery") != query ||
            !((SelectionItemPattern)result!.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected))
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
        ClickSearchPerson(window, "avery@example.com");
        Await(() => OrdinarySearchProfileReady(window, "avery@example.com") && Find(window, "AliasValue").Current.Name == "avhandle");
        AssertAcceptedSearchBrowsing(window, "avery@example.com");
        CaptureVisualEvidence(window, "search-mouse-ordinary-profile");
    }

    private static void ClickSearchPerson(AutomationElement window, string email)
    {
        var result = SearchRows(window).Single(row => row.Current.AutomationId == email);
        var point = result.GetClickablePoint();
        if (!SetCursorPos((int)point.X, (int)point.Y))
            throw new InvalidOperationException("The hosted desktop could not position the search-result pointer.");
        mouse_event(2, 0, 0, 0, 0);
        mouse_event(4, 0, 0, 0, 0);
    }

    [DllImport("user32.dll", EntryPoint = "VkKeyScanW", CharSet = CharSet.Unicode)]
    private static extern short SearchCharacterKey(char character);

    private static void TypeSearchText(string text)
    {
        foreach (var character in text)
        {
            var key = SearchCharacterKey(character);
            if (key == -1) throw new InvalidOperationException($"The hosted keyboard cannot type search character {character}.");
            var modifiers = new[] { (Mask: 1, Key: (byte)0x10), (Mask: 2, Key: (byte)0x11), (Mask: 4, Key: (byte)0x12) };
            foreach (var modifier in modifiers)
                if (((key >> 8) & modifier.Mask) != 0) keybd_event(modifier.Key, 0, 0, 0);
            try { PressCopyKey((byte)(key & 0xFF)); }
            finally
            {
                foreach (var modifier in modifiers.Reverse())
                    if (((key >> 8) & modifier.Mask) != 0) keybd_event(modifier.Key, 0, 2, 0);
            }
        }
    }

    private static void TypeSearchQuery(AutomationElement window, string query)
    {
        var field = Find(window, "SearchQuery");
        field.SetFocus();
        Await(() => field.Current.HasKeyboardFocus);
        keybd_event(0x11, 0, 0, 0);
        try { PressCopyKey(0x41); }
        finally { keybd_event(0x11, 0, 2, 0); }
        PressCopyKey(0x08);
        TypeSearchText(query);
        Await(() => Value(window, "SearchQuery") == query);
    }

    private static AutomationElement[] SearchRows(AutomationElement window) =>
        Find(window, "SearchResults").FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().ToArray();

    private static void AssertQueryHighlight(AutomationElement window, string email)
    {
        Await(() =>
        {
            var row = SearchRows(window).SingleOrDefault(item =>
                ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            return row is not null && row.Current.AutomationId == email && !row.Current.IsOffscreen &&
                Find(window, "SearchResults").Current.BoundingRectangle.Contains(row.Current.BoundingRectangle) &&
                Find(window, "SearchQuery").Current.HasKeyboardFocus;
        });
    }

    private static void AssertAcceptedSearchBrowsing(AutomationElement window, string email, bool archived = false,
        string? diagnosticsContext = null)
    {
        var compact = false;
        void Report(string phase, string observation) =>
            Console.WriteLine($"[DEBUG-category-row] {diagnosticsContext}; expectedEmail='{email}'; " +
                $"archived={archived}; compact={compact}; phase='{phase}'; {observation}");
        string ObserveVisibility(bool accepted) => accepted ? "predicateAccepted=True" :
            ObserveCategoryRow(window, null, null, email, archived, compact, false, false, null, null);
        void ObserveWait(string phase, Func<bool> predicate, Func<bool, string> observe)
        {
            if (diagnosticsContext is null) Await(predicate);
            else ControlReadiness.AwaitObservedReadiness(predicate, observe, Await, observation => Report(phase, observation));
        }
        if (diagnosticsContext is not null) Report("enter", "caller context");
        try
        {
            ControlReadiness.AwaitSearchBrowsing(() => compact = IsVisible(window, "OpenPeople"),
                isCompact => Value(window, "SearchQuery") == "" && OrdinarySearchProfileReady(window, email) &&
                    (isCompact ? !IsVisible(window, "PeopleNavigation") && Find(window, "OpenPeople").Current.HasKeyboardFocus
                        : IsVisible(window, "People") && IsVisible(window, "Favorites")),
                predicate => ObserveWait("restore-browsing", predicate, ObserveVisibility));
        }
        catch (TimeoutException error)
        {
            var focused = AutomationElement.FocusedElement?.Current;
            var entry = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "OpenPeople"))?.Current;
            CaptureFailureEvidence(window, "search-accepted-browsing-failure");
            throw new TimeoutException("Accepted search did not clear the query and restore browsing/focus within 20 seconds. " +
                $"compact={compact}; archived={archived}; expectedEmail='{email}'; owner='{Find(window, "Email").Current.Name}'; " +
                $"query='{Value(window, "SearchQuery")}'; ordinaryProfileReady={OrdinarySearchProfileReady(window, email)}; " +
                $"peopleNavigationVisible={IsVisible(window, "PeopleNavigation")}; peopleVisible={IsVisible(window, "People")}; " +
                $"favoritesVisible={IsVisible(window, "Favorites")}; entryVisible={IsVisible(window, "OpenPeople")}; " +
                $"entryEnabled={entry?.IsEnabled}; entryFocused={entry?.HasKeyboardFocus}; " +
                $"focusedId='{focused?.AutomationId}'; focusedName='{focused?.Name}'; focusedProcess={focused?.ProcessId}; " +
                $"applicationProcess={window.Current.ProcessId}. No corrective actions were attempted.", error);
        }
        if (compact)
        {
            Click(window, "OpenPeople");
            bool? predicateNavigationVisible = null;
            bool? predicateListVisible = null;
            bool OverlayReady()
            {
                predicateNavigationVisible = predicateListVisible = null;
                return (predicateNavigationVisible = IsVisible(window, "PeopleNavigation")).Value &&
                    (predicateListVisible = IsVisible(window, archived ? "ArchivedPeople" : "People")).Value;
            }
            ObserveWait("overlay-visible", OverlayReady, accepted =>
                $"predicatePeopleNavigationVisible={predicateNavigationVisible?.ToString() ?? "not evaluated"}; " +
                $"predicateTargetListVisible={predicateListVisible?.ToString() ?? "not evaluated"}; " +
                ObserveVisibility(accepted));
        }
        var list = Find(window, archived ? "ArchivedPeople" : "People");
        AutomationElement? observedRow = null;
        System.Windows.Rect? predicateViewport = null;
        System.Windows.Rect? predicateBounds = null;
        var predicateObserved = false;
        var rowLookupCompleted = false;
        bool RowReady()
        {
            predicateObserved = true;
            rowLookupCompleted = false;
            observedRow = null;
            predicateViewport = predicateBounds = null;
            var row = list.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, email));
            rowLookupCompleted = true;
            observedRow = row;
            if (row is null || row.Current.IsOffscreen) return false;
            var viewport = list.Current.BoundingRectangle;
            var bounds = row.Current.BoundingRectangle;
            predicateViewport = viewport;
            predicateBounds = bounds;
            return bounds.Left >= viewport.Left && bounds.Left < viewport.Right &&
                bounds.Top >= viewport.Top && bounds.Bottom <= viewport.Bottom && viewport.Contains(row.GetClickablePoint()) &&
                ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected &&
                (compact || row.Current.HasKeyboardFocus);
        }
        ObserveWait("selected-row", RowReady,
            accepted => accepted ? $"predicateAccepted=True; predicateBounds='{predicateBounds}'; predicateViewport='{predicateViewport}'" :
                ObserveCategoryRow(window, list, observedRow, email, archived, compact,
                    predicateObserved, rowLookupCompleted, predicateViewport, predicateBounds));
        if (archived)
        {
            if (((TogglePattern)Find(window, "ShowArchivedPeople").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != ToggleState.On ||
                Find(window, "People").FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, email)) is not null ||
                !Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived", StringComparison.Ordinal))
                throw new InvalidOperationException("Accepted archived search must reveal the retained list without reactivating or inserting a tree row.");
        }
        else
        {
            var row = list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, email));
            for (var parent = TreeWalker.ControlViewWalker.GetParent(row);
                parent is not null && parent.Current.AutomationId != "People"; parent = TreeWalker.ControlViewWalker.GetParent(parent))
                if (parent.Current.ControlType == ControlType.TreeItem &&
                    ((ExpandCollapsePattern)parent.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                    throw new InvalidOperationException("Accepted search did not expand the selected Person's reporting path.");
        }
        if (compact)
        {
            Click(window, "ClosePeople");
            ObserveWait("return-focus", () => Find(window, "OpenPeople").Current.HasKeyboardFocus, ObserveVisibility);
        }
    }

    private static string ObserveCategoryRow(AutomationElement window, AutomationElement? list, AutomationElement? row,
        string email, bool archived, bool compact, bool predicateObserved, bool rowLookupCompleted,
        System.Windows.Rect? predicateViewport, System.Windows.Rect? predicateBounds)
    {
        static string Read(Func<object?> read)
        {
            try { return read()?.ToString() ?? "unavailable"; }
            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                return $"unavailable ({error.GetType().Name}: {error.Message})";
            }
        }
        var listExists = list is not null ? "True" : Read(() =>
        {
            list = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, archived ? "ArchivedPeople" : "People"));
            return list is not null;
        });
        var exists = predicateObserved
            ? rowLookupCompleted ? (row is not null).ToString() : "row lookup did not complete"
            : list is null ? "list missing or unavailable" : Read(() =>
            {
                row = list.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, email));
                return row is not null;
            });
        System.Windows.Point? point = null;
        var clickable = row is null ? "row missing" : Read(() =>
        {
            var available = row.TryGetClickablePoint(out var observedPoint);
            if (available) point = observedPoint;
            return available;
        });
        return $"capturedAt={DateTimeOffset.UtcNow:O}; units=native-pixels; expectedEmail='{email}'; archived={archived}; compact={compact}; " +
            $"predicateObserved={predicateObserved}; predicateRowLookupCompleted={rowLookupCompleted}; listExists={listExists}; rowExists={exists}; " +
            $"rowEmail='{(row is null ? "row missing or unavailable" : Read(() => row.Current.AutomationId))}'; " +
            $"rowBounds='{(row is null ? "row missing" : Read(() => row.Current.BoundingRectangle))}'; " +
            $"viewport='{(list is null ? "list missing or unavailable" : Read(() => list.Current.BoundingRectangle))}'; " +
            $"predicateBounds='{predicateBounds?.ToString() ?? "not evaluated"}'; " +
            $"predicateViewport='{predicateViewport?.ToString() ?? "not evaluated"}'; " +
            $"isOffscreen={(row is null ? "row missing" : Read(() => row.Current.IsOffscreen))}; " +
            $"selected={(row is null ? "row missing" : Read(() => row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                ? ((SelectionItemPattern)pattern).Current.IsSelected : "selection pattern unavailable"))}; " +
            $"clickablePointAvailable={clickable}; clickablePoint='{point?.ToString() ?? "unavailable"}'; " +
            $"clickablePointContained={(list is not null && point is { } location ? Read(() => list.Current.BoundingRectangle.Contains(location)) : "unavailable")}; " +
            $"rowKeyboardFocus={(row is null ? "row missing" : Read(() => row.Current.HasKeyboardFocus))}; " +
            $"peopleNavigationVisible={Read(() => IsVisible(window, "PeopleNavigation"))}; " +
            $"peopleVisible={Read(() => IsVisible(window, "People"))}; " +
            $"archivedPeopleVisible={Read(() => IsVisible(window, "ArchivedPeople"))}; " +
            $"peopleEntryKeyboardFocus={Read(() => Find(window, "OpenPeople").Current.HasKeyboardFocus)}; " +
            $"nativeTrace='{Read(() => Find(window, "OpenPeople").Current.HelpText)}'; " +
            $"overlayVisible={Read(() =>
            {
                var dismiss = window.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Close People overlay")));
                return dismiss is not null && !dismiss.Current.IsOffscreen;
            })}; " +
            $"owner='{Read(() => Find(window, "Email").Current.Name)}'; query='{Read(() => Value(window, "SearchQuery"))}'.";
    }

    private static void AssertQueryKeyboardNavigation(AutomationElement window, bool responsive)
    {
        foreach (var compact in responsive ? new[] { false, true } : new[] { false })
        {
            if (responsive) ResizeWindow(window, compact ? 700 : 1100, 900);
            var owner = Find(window, "Email").Current.Name;
            var note = Find(window, "PersonNoteHeading").Current.HelpText;
            TypeSearchQuery(window, "browse");
            Await(() => Find(window, "SearchCount").Current.Name == "People found: 40 — local profiles and notes");
            AssertQueryHighlight(window, "browse01@example.com");
            PressCopyKey(0x26);
            AssertQueryHighlight(window, "browse01@example.com");
            PressCopyKey(0x28);
            AssertQueryHighlight(window, "browse02@example.com");
            TypeSearchText("0");
            Await(() => Value(window, "SearchQuery") == "browse0" &&
                Find(window, "SearchCount").Current.Name == "People found: 9 — local profiles and notes");
            AssertQueryHighlight(window, "browse01@example.com");
            TypeSearchQuery(window, "browse");
            Await(() => Find(window, "SearchCount").Current.Name == "People found: 40 — local profiles and notes");
            for (var i = 2; i <= 40; i++)
            {
                PressCopyKey(0x28);
                AssertQueryHighlight(window, $"browse{i:00}@example.com");
            }
            PressCopyKey(0x28);
            AssertQueryHighlight(window, "browse40@example.com");
            PressCopyKey(0x26);
            AssertQueryHighlight(window, "browse39@example.com");
            if (Find(window, "Email").Current.Name != owner || Find(window, "PersonNoteHeading").Current.HelpText != note)
                throw new InvalidOperationException("Typing, waiting and navigating highlights changed ordinary profile/note reading.");
            CaptureVisualEvidence(window, compact ? "search-keyboard-last-compact" : "search-keyboard-last-wide");
            PressCopyKey(0x0D);
            AssertAcceptedSearchBrowsing(window, "browse39@example.com");
            TypeSearchQuery(window, "no-person-matches-this-query");
            Await(() => Find(window, "SearchCount").Current.Name == "People found: 0 — local profiles and notes" &&
                SearchRows(window).Length == 0);
            PressCopyKey(0x0D);
            PressCopyKey(0x28);
            if (Value(window, "SearchQuery") != "no-person-matches-this-query" ||
                Find(window, "Email").Current.Name != "browse39@example.com" || !IsVisible(window, "SearchResults") ||
                !Find(window, "SearchQuery").Current.HasKeyboardFocus)
                throw new InvalidOperationException("Empty-result Enter must retain search, focus and the existing Person.");
            TypeSearchQuery(window, "007b");
            AssertQueryHighlight(window, "avery@example.com");
            PressCopyKey(0x0D);
            AssertAcceptedSearchBrowsing(window, "avery@example.com");
            TypeSearchQuery(window, "avhandle");
            AssertQueryHighlight(window, "avery@example.com");
            ActivateSearchRow(SearchRows(window).Single());
            AssertAcceptedSearchBrowsing(window, "avery@example.com");
        }
        if (responsive) ResizeWindow(window, 1100, 900);
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
