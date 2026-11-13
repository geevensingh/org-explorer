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
        var import = Path.Combine(root, "search-import.json");
        File.WriteAllText(import, SearchImport("""
            {"email":"avery@example.com","displayName":"Avery Directory","preferredName":"Ave Preferred","alias":"avhandle",
             "jobTitle":"Scientist","department":"Research","officeBuilding":"Juniper","officeNumber":"007B",
             "officeState":"Ontario","officeCountry":"Canada","linkedInProfileUrl":"https://www.linkedin.com/in/fictitious-avery",
             "managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null,"isFavorite":true}
            """ + (responsive ? """
            ,{"email":"longread@example.com","displayName":"Alexandra Reference with a deliberately long Directory name for accessible Person reading",
              "alias":"longreading","officeBuilding":"South campus research and collaboration building with a deliberately long descriptive location",
              "officeState":"A long state or province description that must wrap without shrinking the ordinary reading type",
              "managerEmail":null}
            """ : "") + "," + string.Join(",", Enumerable.Range(1, 40).Select(i => $$"""
                {"email":"browse{{i:00}}@example.com","displayName":"Z Browse {{i:00}}","managerEmail":null}
                """)), 1));
        try
        {
            process = Launch(executable);
            var window = Window(process);
            SetSettingsVisible(window, true);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenImportView(window);
            Set(window, "ImportPath", import);
            ApplyReviewedHistoryImport(window);
            QueryAndSelect(window, "007b", "avery@example.com", "Current", "Profile:");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave Preferred");
            Await(() => Find(window, "OfficeNumberValue").Current.HasKeyboardFocus &&
                Find(window, "OfficeNumberValue").Current.Name == "007B" && !Find(window, "OfficeNumberValue").Current.IsOffscreen);
            if (window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchContext")) is not null)
                throw new InvalidOperationException("Profile search retained a duplicate generic inspector instead of revealing the actual field.");
            CaptureVisualEvidence(window, "search-matched-office-number");
            AssertMouseSearchActivation(window);
            if (responsive) AssertLongAndMissingFacts(window);
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Individual context");
            Set(window, "PersonNoteMarkdown", $"# Orchid\nPrivate Person writing\n![remote]({remote}/image.png)\n<script>fetch('{remote}')</script>" +
                (responsive ? "\n\n" + string.Join("\n\n", Enumerable.Range(1, 32).Select(i =>
                    $"Private Person writing paragraph {i}. Keep decisions and their context readable while the same Person identity remains anchored.")) : ""));
            QueryAndSelect(window, "orchid", "avery@example.com", "Current", "Person note");
            AssertSearchReading(window, "Private Person writing");
            AssertSearchReading(window, "Image not loaded");
            AssertNoteView(window, "Person", editing: false);
            CaptureVisualEvidence(window, "search-exact-person-note-read");
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
            AssertSearchReading(window, "Private Person writing");
            if (((TogglePattern)Find(window, "ShowArchivedPersonNotes").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != ToggleState.On)
                throw new InvalidOperationException("Archived-note search did not open the retained note.");
            Click(window, "ArchivePersonNote");
            NavigateToManager(window, "morgan@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Former team");
            Set(window, "TeamNoteMarkdown", $"# Cedar\nFormer Team writing\n![remote]({remote}/team.png)");
            QueryAndSelect(window, "cedar", "avery@example.com", "Current", "Inherited Team note", "morgan@example.com");
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
                Await(() => Find(window, "Status").Current.Name.Contains("NOT saved"));
                if (Find(window, "FavoriteStatus").Current.Name.StartsWith("Marked"))
                    throw new InvalidOperationException("Failed favorite save falsely showed a saved star.");
                QueryAndSelect(window, "alex@example", "alex@example.com", "Current", "Profile:", expectNavigation: false);
                if (Find(window, "Email").Current.Name != "avery@example.com")
                    throw new InvalidOperationException("Search discarded an unsaved favorite action.");
                CaptureVisualEvidence(window, "search-rejected-favorite-writing-retained");
                process.CloseMainWindow();
                Await(() => Find(window, "Status").Current.Name.Contains("Unsaved local changes prevent safe closing"));
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
            AssertSearchReading(window, "Former Team writing", readingId: "HistoricalTeamReading");
            AssertSearchReading(window, "Image not loaded", readingId: "HistoricalTeamReading");
            if (!Find(window, "HistoricalTeamSummary").Current.Name.Contains("effective date unknown") ||
                !Find(window, "HistoricalTeamReading").Current.HelpText.Contains("morgan@example.com"))
                throw new InvalidOperationException("Historical search omitted frozen ownership/provenance.");
            CaptureVisualEvidence(window, "search-exact-frozen-note-owner-capture");
            QueryAndSelect(window, "pine", "avery@example.com", "Current", "Inherited Team note");
            AssertSearchReading(window, "New current Team writing", readingId: "InheritedTeamReading");
            RequestPersonArchival(window, archived: true);
            QueryAndSelect(window, "007b", "avery@example.com", "Archived person", "Profile:");
            if (!Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived"))
                throw new InvalidOperationException("Archived match opened a current profile.");
            QueryAndSelect(window, "cedar", "avery@example.com", "Historical / Archived person", "Frozen Team note");
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
            AssertSearchReading(window, "Former Team writing", readingId: "HistoricalTeamReading");
            Click(window, "ToggleFavorite");
            Await(() => Find(window, "ToggleFavorite").Current.Name == "Mark favorite");
            Close(process);
            Console.WriteLine("PASS: actual WPF exact search, retained writing, invalid-target startup recovery, deliberate archived access and reopen proportions.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static string SearchImport(string records, int id) => $$"""
        {"schemaVersion":1,"batchId":"13000000-0000-4000-8000-{{id:000000000000}}",
         "observedAt":"2026-11-06T17:00:00Z","people":[{{records}}]}
        """;

    private static void QueryAndSelect(AutomationElement window, string query, string email, string context,
        string source, string? owner = null, bool expectNavigation = true)
    {
        if (expectNavigation) AwaitLocalWorkIdle(window);
        Set(window, "SearchQuery", query);
        AutomationElement? result = null;
        Await(() =>
        {
            result = Find(window, "SearchResults").FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>()
                .FirstOrDefault(r => r.Current.Name.Contains($"({email}) — {context}") && r.Current.Name.Contains(source) &&
                    (owner is null || r.Current.Name.Contains($"({owner})")));
            return result is not null;
        });
        var beforeEmail = CurrentPersonEmail(window);
        string? PersonNoteIdentity() => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PersonNoteHeading"))?.Current.HelpText;
        var beforeNote = PersonNoteIdentity();
        (string Name, string Identity)[] SelectedRows() =>
            ((SelectionPattern)Find(window, "SearchResults").GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection()
                .Select(row => (row.Current.Name, row.Current.HelpText)).ToArray();
        var beforeSelection = SelectedRows();
        Find(window, "SearchQuery").SetFocus();
        Await(() => Find(window, "SearchQuery").Current.HasKeyboardFocus);
        PressCopyKey(0x0D);
        Await(() => Find(window, "SearchResults").Current.HasKeyboardFocus ||
            Find(window, "SearchResults").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true)) is not null);
        if (CurrentPersonEmail(window) != beforeEmail || PersonNoteIdentity() != beforeNote ||
            !beforeSelection.SequenceEqual(SelectedRows()))
            throw new InvalidOperationException("Query Enter opened a Person or replaced exact note reading before result activation.");
        var label = result!.Current.Name;
        var identity = result.Current.HelpText;
        ActivateSearchRow(result);
        if (expectNavigation)
            Await(() => Find(window, "Email").Current.Name == email && SearchDestinationReady(window, source, label, identity));
    }

    private static void ActivateSearchRow(AutomationElement result, Action<Func<bool>>? awaitReady = null)
    {
        awaitReady ??= Await;
        ((SelectionItemPattern)result.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        result.SetFocus();
        awaitReady(() => result.Current.HasKeyboardFocus);
        PressCopyKey(0x0D);
    }

    private static bool SearchDestinationReady(AutomationElement window, string source, string label, string identity)
    {
        if (source.StartsWith("Profile:", StringComparison.Ordinal))
            return AutomationElement.FocusedElement?.Current.HelpText == label;
        var reading = source switch
        {
            "Person note" => "PersonNoteReading",
            "Owned Team note" => "TeamNoteReading",
            "Inherited Team note" => "InheritedTeamReading",
            _ => "HistoricalTeamReading"
        };
        var target = Find(window, reading);
        if (target.Current.IsOffscreen) return false;
        var ids = System.Text.RegularExpressions.Regex.Matches(identity, @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
        if (ids.Count == 0) throw new InvalidOperationException("The selected note result omitted its exact document identity.");
        var observed = source switch
        {
            "Person note" => Find(window, "PersonNoteHeading").Current.HelpText,
            "Owned Team note" => Find(window, "TeamNoteHeading").Current.HelpText,
            _ => target.Current.HelpText
        };
        if (!ids.Cast<System.Text.RegularExpressions.Match>().All(id => observed.Contains(id.Value, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (source is "Person note" or "Owned Team note")
            return ((SelectionItemPattern)Find(window, source == "Person note" ? "PersonNoteReadTab" : "TeamNoteReadTab")
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;
        return true;
    }

    private static void AssertMouseSearchActivation(AutomationElement window)
    {
        Set(window, "SearchQuery", "avhandle");
        AutomationElement? result = null;
        Await(() => (result = Find(window, "SearchResults").FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))) is not null);
        var point = result!.GetClickablePoint();
        if (!SetCursorPos((int)point.X, (int)point.Y))
            throw new InvalidOperationException("The hosted desktop could not position the search-result pointer.");
        mouse_event(2, 0, 0, 0, 0);
        mouse_event(4, 0, 0, 0, 0);
        Await(() => Find(window, "AliasValue").Current.HasKeyboardFocus && Find(window, "AliasValue").Current.Name == "avhandle");
        CaptureVisualEvidence(window, "search-mouse-matched-alias");
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
