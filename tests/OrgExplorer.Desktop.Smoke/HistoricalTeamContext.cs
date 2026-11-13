using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunTeamContextFlow(string executable, string root, bool responsive = false)
    {
        Process? process = null;
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var import = Path.Combine(root, "import.json");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var image = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/never-load.png";
        try
        {
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"11000000-0000-4000-8000-000000000001",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
                 {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"casey@example.com"},
                 {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
                 {"email":"alex@example.com","displayName":"Alex","managerEmail":null}]}
                """);
            process = Launch(executable);
            var window = Window(process);
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            OpenImportView(window);
            Set(window, "ImportPath", import);
            ApplyReviewedHistoryImport(window);
            SelectTreePerson(window, "casey@example.com");
            AssertEmptyNotes(window, "Team");
            CaptureVisualEvidence(window, "team-note-true-empty-invitation");
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "About Casey");
            Set(window, "PersonNoteMarkdown", "Never inherited");
            AwaitLocalWorkIdle(window);
            Click(window, "NewTeamNote");
            AssertNoteView(window, "Team", editing: true);
            if (Find(window, "TeamNoteTitle").Current.Name != "Team note title" ||
                Find(window, "TeamNoteMarkdown").Current.Name != "Team note Markdown")
                throw new InvalidOperationException("Team note title and writing lack distinct accessible names.");
            Set(window, "TeamNoteTitle", "Casey context");
            AwaitLocalWorkIdle(window);
            SelectTab(window, "TeamNoteReadTab");
            AssertNoteView(window, "Team", editing: false);
            AssertEmptyDocument(window, "Team");
            CaptureVisualEvidence(window, "team-note-existing-empty-read");
            SelectTab(window, "TeamNoteEditTab");
            AssertValue(window, "TeamNoteMarkdown", "");
            Set(window, "TeamNoteMarkdown", $"# Frozen ancestor\nOriginal **ancestor** R&amp;D\n![Remote image]({image})" +
                (responsive ? "\n\n" + string.Join("\n\n", Enumerable.Range(1, 32).Select(n =>
                    $"Former Team paragraph {n}. Preserve the former owner's writing and captured context while reading.")) : ""));
            SelectTab(window, "TeamNoteReadTab");
            AssertNoteView(window, "Team", editing: false);
            Await(() => TeamReadingText(window).Contains("Original ancestor"));
            if (!TeamReadingText(window).Contains("R&D"))
                throw new InvalidOperationException("Owned Team reading lost decoded Markdown entities.");
            if (!TeamReadingText(window).Contains("Image not loaded"))
                throw new InvalidOperationException("Owned Team Markdown did not show remote-image suppression.");
            ReadAndCaptureNote(window, "TeamNoteReading", "Original ancestor", "team-note-populated-owned-reading");
            AwaitLocalWorkIdle(window);
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Secondary empty Team note");
            AwaitLocalWorkIdle(window);
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            NavigateToManager(window, "morgan@example.com");
            NavigateToManager(window, "casey@example.com");
            Await(() => Find(window, "TeamOngoingNoteStatus").Current.Name.Contains("Choose an ongoing note"));
            AssertOtherTeamNotesCollapsed(window);
            AssertNoEmptyInvitation(window, "Team");
            CaptureVisualEvidence(window, "team-note-ambiguous-not-empty");
            SelectTeamNote(window, "Casey context");
            Click(window, "UseAsOngoingTeamNote");
            AssertOtherTeamNotesCollapsed(window);
            AssertNoteView(window, "Team", editing: false);
            QueryAndSelect(window, "Secondary empty Team note", "casey@example.com", "Current", "Owned Team note");
            Await(() => Find(window, "TeamNoteHeading").Current.Name == "Secondary empty Team note");
            AssertNoteView(window, "Team", editing: false);
            if (!Find(window, "TeamNoteKind").Current.Name.Contains("Other Team note"))
                throw new InvalidOperationException("Team search redirected a secondary note to the ongoing note.");
            AssertEmptyDocument(window, "Team");
            CaptureVisualEvidence(window, "search-exact-empty-other-team-note");
            var secondaryFile = Directory.GetFiles(Path.Combine(workspace, "team-notes"), "*.md")
                .Single(path => File.ReadAllText(path).Contains("Secondary empty Team note", StringComparison.Ordinal));
            var savedSecondary = File.ReadAllBytes(secondaryFile);
            SelectTab(window, "TeamNoteEditTab");
            using (File.Open(secondaryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Set(window, "TeamNoteMarkdown", "Retained secondary Team writing");
                Await(() => Find(window, "TeamNoteStatus").Current.Name.Contains("NOT saved"));
                Click(window, "OpenOngoingTeamNote");
                Await(() => Find(window, "TeamNoteStatus").Current.Name.Contains("before leaving or closing"));
                AssertValue(window, "TeamNoteMarkdown", "Retained secondary Team writing");
                AssertNoteView(window, "Team", editing: true);
                if (Find(window, "TeamNoteHeading").Current.Name != "Secondary empty Team note" ||
                    !savedSecondary.SequenceEqual(File.ReadAllBytes(secondaryFile)))
                    throw new InvalidOperationException("Ongoing Team navigation changed a failed secondary draft or saved writing.");
                AssertNoEmptyInvitation(window, "Team");
                CaptureVisualEvidence(window, "team-note-retained-secondary-save-failure");
            }
            Click(window, "DiscardTeamNoteDraft");
            AwaitLocalWorkIdle(window);
            AssertEmptyDocument(window, "Team");
            if (!savedSecondary.SequenceEqual(File.ReadAllBytes(secondaryFile)))
                throw new InvalidOperationException("Discard changed the saved secondary Team note.");
            AssertNoteView(window, "Team", editing: false);
            var preferences = Path.Combine(root, "app-settings", "workspace-settings.json");
            var savedPreferences = File.ReadAllBytes(preferences);
            using (File.Open(preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(window, "UseAsOngoingTeamNote");
                Await(() => Find(window, "TeamOngoingNoteStatus").Current.Name.Contains("NOT saved"));
                if (!Find(window, "TeamNoteKind").Current.Name.Contains("Other Team note") ||
                    !savedPreferences.SequenceEqual(File.ReadAllBytes(preferences)))
                    throw new InvalidOperationException("Failed Team designation changed the durable ongoing choice.");
                CaptureVisualEvidence(window, "team-note-designation-failure");
            }
            Click(window, "OpenOngoingTeamNote");
            Await(() => Find(window, "TeamNoteHeading").Current.Name == "Casey context");
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            NavigateToManager(window, "morgan@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Morgan context");
            Set(window, "TeamNoteMarkdown", "# Frozen direct\nOriginal direct");
            AwaitLocalWorkIdle(window);
            if (!Find(window, "TeamNoteOwnership").Current.Name.Contains("morgan@example.com"))
                throw new InvalidOperationException("Owned Team writing lost its explicit Manager attribution.");
            BeginProfileEditing(window);
            Set(window, "JobTitle", "Director");
            SaveProfileEditing(window);
            AssertNoteView(window, "Team", editing: true);
            Await(() => Find(window, "TeamNoteReview").Current.Name.Contains("Needs review"));
            AssertOtherTeamNotesCollapsed(window);
            var ownedWarning = Find(window, "OwnedTeamReviewWarnings").Current.Name;
            if (!ownedWarning.Contains("Morgan context") || !ownedWarning.Contains("morgan@example.com"))
                throw new InvalidOperationException("Collapsed Other Team notes concealed the reviewed note's exact ownership.");
            SelectTab(window, "TeamNoteReadTab");
            AssertNoteView(window, "Team", editing: false);
            Await(() => IsVisible(window, "TeamNoteReading"));
            ReadAndCaptureNote(window, "TeamNoteReading", "Original direct", "team-owned-review-warning-outside-other-notes");
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Primary Person context");
            Set(window, "PersonNoteMarkdown", "Everyday individual writing");
            AwaitLocalWorkIdle(window);
            SelectTab(window, "PersonNoteReadTab");
            if (responsive)
            {
                foreach (var size in new[] { (Width: 700d, Height: 500d), (Width: 1100d, Height: 900d) })
                {
                    ResizeWindow(window, size.Width, size.Height);
                    AssertCollapsedTeamWarnings(window, "morgan@example.com", "Morgan context");
                    CaptureVisualEvidence(window, $"team-inherited-warning-collapsed-{size.Width:0}");
                }
            }
            else
            {
                AssertCollapsedTeamWarnings(window, "morgan@example.com", "Morgan context");
                CaptureVisualEvidence(window, "team-inherited-warning-collapsed");
            }
            ExpandCurrentTeamContext(window);
            var inherited = Find(window, "InheritedTeamSummary").Current.Name;
            if (!inherited.Contains("morgan@example.com") || !inherited.Contains("casey@example.com") ||
                !inherited.Contains("Secondary empty Team note") ||
                !inherited.Contains("Needs review") || CurrentReadingText(window).Contains("Never inherited"))
                throw new InvalidOperationException("Inherited context lost ancestor attribution, review feedback or private scope.");
            ReadAndCaptureNote(window, "InheritedTeamReading", "Original direct", "team-current-all-ancestor-attribution");
            SelectTab(window, "PersonNoteEditTab");
            Set(window, "PersonNoteTitle", " ");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
            ReviewInheritedTeamNote(window, "morgan@example.com", "Morgan context");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("before leaving or closing"));
            AssertValue(window, "PersonNoteTitle", " ");
            AssertValue(window, "PersonNoteMarkdown", "Everyday individual writing");
            if (Find(window, "Email").Current.Name != "avery@example.com")
                throw new InvalidOperationException("Inherited review bypassed the selected Person's retained writing.");
            CaptureVisualEvidence(window, "team-owner-review-rejected-independent-person-draft");
            Click(window, "DiscardPersonNoteDraft");
            AwaitLocalWorkIdle(window);
            if (Find(window, "PersonNoteHeading").Current.Name != "Primary Person context" ||
                !ReadingText(window).Contains("Everyday individual writing"))
                throw new InvalidOperationException("Discard did not restore the exact saved Person note for reading.");
            ReviewInheritedTeamNote(window, "morgan@example.com", "Morgan context");
            Await(() => Find(window, "Email").Current.Name == "morgan@example.com");
            AssertNoteView(window, "Team", editing: false);
            // Archival feedback requires a recorded choice, not implicit sole-note reading.
            Click(window, "UseAsOngoingTeamNote");
            Await(() => Find(window, "TeamNoteKind").Current.Name == "Ongoing Team note");
            Click(window, "ConfirmTeamNote");
            Await(() => Find(window, "TeamNoteReview").Current.Name.Contains("Confirmed"));
            Click(window, "ArchiveTeamNote");
            Await(() => NoteFeedback(window, "Team").Contains("archived locally"));
            Await(() => Find(window, "TeamOngoingNoteStatus").Current.Name.Contains("unavailable or archived"));
            AssertNoEmptyInvitation(window, "Team");
            CaptureVisualEvidence(window, "team-note-unavailable-archived-choice");
            ExpandSection(window, "OtherTeamNotes");
            ((TogglePattern)Find(window, "ShowArchivedTeamNotes").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            SelectTeamNote(window, "Morgan context");
            SelectTab(window, "TeamNoteEditTab");
            AssertValue(window, "TeamNoteMarkdown", "# Frozen direct\nOriginal direct");
            Click(window, "ArchiveTeamNote");
            AwaitLocalWorkIdle(window);
            SelectTeamNote(window, "Morgan context");
            SelectTreePerson(window, "alex@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Alex context");
            Set(window, "TeamNoteMarkdown", "# Current branch\nNew branch writing" +
                (responsive ? "\n\n" + string.Join("\n\n", Enumerable.Range(1, 32).Select(n =>
                    $"Current Team paragraph {n}. Keep current ancestor context separate from frozen former writing.")) : ""));
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            ExpandHistory(window);
            if (!Find(window, "HistoricalTeamSummary").Current.Name.Contains("baseline"))
                throw new InvalidOperationException("Initial creation fabricated a historical transfer.");
            if (!Find(window, "FieldConfirmations").Current.Name.Contains("morgan@example.com") ||
                !Find(window, "FieldConfirmations").Current.Name.Contains("Imported") ||
                !Find(window, "ProfileObservations").Current.Name.Contains("Observation 2026-11-01T17:00:00Z"))
                throw new InvalidOperationException("Secondary History lost field confirmations or source observations.");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"11000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-06T17:00:00Z","people":[
                 {"email":"morgan@example.com","managerEmail":"alex@example.com"}]}
                """);
            ApplyReviewedHistoryImport(window);
            ExpandHistory(window);
            Await(() => Find(window, "HistoricalTeamSummary").Current.Name.Contains("Historical captures: 1"));
            var summary = Find(window, "HistoricalTeamSummary").Current.Name;
            if (!summary.Contains("Morgan (morgan@example.com) → Casey (casey@example.com)") ||
                !summary.Contains("after 2026-11-01T17:00:00Z, by 2026-11-06T17:00:00Z") ||
                !summary.Contains("locally known at processing time") || !summary.Contains("unknown actual transfer instant"))
                throw new InvalidOperationException("Historical presentation lost former attribution or honest dates.");
            var frozen = HistoricalReadingText(window);
            if (!frozen.Contains("Original ancestor R&D") || !frozen.Contains("Original direct") ||
                frozen.Contains("New branch writing") || !CurrentReadingText(window).Contains("New branch writing") ||
                CurrentReadingText(window).Contains("Original ancestor"))
                throw new InvalidOperationException($"Current and Historical writing were mixed.\nFrozen:\n{frozen}\nCurrent:\n{CurrentReadingText(window)}");
            ReadAndCaptureNote(window, "HistoricalTeamReading", "Original direct", "team-frozen-former-owner-attribution");
            NavigateToManager(window, "morgan@example.com");
            SelectTeamNote(window, "Morgan context");
            AssertNoteView(window, "Team", editing: false);
            SelectTab(window, "TeamNoteEditTab");
            Set(window, "TeamNoteMarkdown", "# Later direct\nLater writing");
            OpenPerson(window, "alex@example.com", "morgan@example.com");
            if (HistoricalReadingText(window) != frozen || !CurrentReadingText(window).Contains("Later writing"))
                throw new InvalidOperationException("Later Team editing changed frozen writing or failed to update current reading.");
            if (responsive)
                foreach (var size in new[] { (Width: 700d, Height: 500d), (Width: 1100d, Height: 900d) })
                {
                    ResizeWindow(window, size.Width, size.Height);
                    if (HistoricalReadingText(window) != frozen || !CurrentReadingText(window).Contains("Later writing") ||
                        ((ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern)).Current.HorizontallyScrollable)
                        throw new InvalidOperationException("Adaptive History access lost frozen/current context or requires horizontal reading.");
                    if (!frozen.Contains("Former Team paragraph 32") || !CurrentReadingText(window).Contains("Current Team paragraph 32"))
                        throw new InvalidOperationException("Responsive context lost the complete long current/frozen writing.");
                    ReadAndCaptureNote(window, "InheritedTeamReading", "Current Team paragraph 32", $"team-current-long-reading-{size.Width:0}");
                    ReadAndCaptureNote(window, "HistoricalTeamReading", "Former Team paragraph 32", $"team-frozen-long-reading-{size.Width:0}");
                }
            QueryAndSelect(window, "Original direct", "avery@example.com", "Historical", "Frozen Team note");
            AssertSearchReading(window, "Original direct", "Later writing", "HistoricalTeamReading");
            if (!Find(window, "HistoricalTeamReading").Current.HelpText.Contains("morgan@example.com"))
                throw new InvalidOperationException("Historical search lost its frozen former-owner attribution.");
            ReadAndCaptureNote(window, "HistoricalTeamReading", "Original direct", "team-exact-frozen-search");
            SelectTreePerson(window, "casey@example.com");
            SelectTeamNote(window, "Secondary empty Team note");
            var secondaryId = Find(window, "TeamNoteHeading").Current.HelpText;
            Close(process);
            process = Launch(executable);
            window = Window(process);
            AwaitRestoredWorkspace(window, workspace, "casey@example.com");
            AssertRestoredNote(window, "Team", secondaryId, "Secondary empty Team note");
            Await(() => !Find(window, "TeamNoteHeading").Current.IsOffscreen);
            AssertEmptyDocument(window, "Team");
            CaptureVisualEvidence(window, "team-note-exact-empty-reopened");
            if (responsive)
                foreach (var size in new[] { (Width: 700d, Height: 500d), (Width: 1100d, Height: 900d) })
                {
                    ResizeWindow(window, size.Width, size.Height);
                    AssertRestoredNote(window, "Team", secondaryId, "Secondary empty Team note");
                }
            OpenPerson(window, "alex@example.com", "morgan@example.com");
            if (HistoricalReadingText(window) != frozen || !CurrentReadingText(window).Contains("Later writing"))
                throw new InvalidOperationException("Reopening lost separate current and Historical writing.");
            SelectTreePerson(window, "casey@example.com");
            Await(() => Find(window, "TeamNoteHeading").Current.Name == "Casey context");
            AssertOtherTeamNotesCollapsed(window);
            AssertNoteView(window, "Team", editing: false);
            SelectTeamNote(window, "Secondary empty Team note");
            Click(window, "UseAsOngoingTeamNote");
            AssertOtherTeamNotesCollapsed(window);
            if (Find(window, "TeamNoteHeading").Current.Name != "Secondary empty Team note" ||
                Find(window, "TeamNoteKind").Current.Name != "Ongoing Team note")
                throw new InvalidOperationException("The reopened owned Team designation cannot be changed explicitly.");
            if (listener.Pending()) throw new InvalidOperationException("Team Markdown fetched remote content.");
            Close(process);
            Console.WriteLine("PASS: exact secondary owned-Team GUID restored before deliberate navigation, with frozen/current context retained.");
        }
        finally { if (process is not null) StopApplication(process); }
    }

    private static void ApplyReviewedHistoryImport(AutomationElement window)
    {
        OpenImportView(window);
        Click(window, "PreviewImport");
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        Click(window, "ApplyImport");
        Await(() => Status(window).Contains("applied") ||
            Status(window).Contains("no changes"));
        ReturnToPeople(window);
    }
    private static void OpenImportView(AutomationElement window)
    {
        var path = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ImportPath"));
        if (path is not null && !path.Current.IsOffscreen) return;
        AwaitLocalWorkIdle(window);
        SetSettingsVisible(window, true);
        Click(window, "OpenImport");
        Await(() => !Find(window, "ImportPath").Current.IsOffscreen);
        var people = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "People"));
        if (people is not null && !people.Current.IsOffscreen)
            throw new InvalidOperationException("Import did not replace the people view.");
    }
    private static void ReturnToPeople(AutomationElement window)
    {
        Click(window, "BackToPeople");
        Await(() =>
        {
            var path = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ImportPath"));
            return path is null || path.Current.IsOffscreen;
        });
        AssertImportHidden(window);
    }
    private static void AssertImportHidden(AutomationElement window)
    {
        foreach (var id in new[] { "ImportPath", "ChooseImportFile", "PreviewImport", "Preview", "ApplyImport", "BackToPeople" })
        {
            var control = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (control is not null && !control.Current.IsOffscreen)
                throw new InvalidOperationException($"People view still displays Import control {id}.");
        }
    }
    private static void SelectTeamNote(AutomationElement window, string title)
    {
        AwaitLocalWorkIdle(window);
        ExpandSection(window, "OtherTeamNotes");
        AutomationElement? item = null;
        Await(() => (item = Find(window, "TeamNotes").FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty, title)))) is not null);
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Find(window, "TeamNoteHeading").Current.Name == title);
    }
    private static string TeamReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "TeamNoteReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
    private static string HistoricalReadingText(AutomationElement window) =>
        SecondaryReadingText(window, "HistoryAccess", "HistoricalTeamReading");
    private static string CurrentReadingText(AutomationElement window) =>
        SecondaryReadingText(window, "CurrentTeamContext", "InheritedTeamReading");

    private static void ExpandSection(AutomationElement window, string id)
    {
        var expansion = (ExpandCollapsePattern)Find(window, id).GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (expansion.Current.ExpandCollapseState != ExpandCollapseState.Expanded) expansion.Expand();
        Await(() => expansion.Current.ExpandCollapseState == ExpandCollapseState.Expanded);
    }

    private static void ExpandHistory(AutomationElement window) => ExpandSection(window, "HistoryAccess");
    private static void ExpandCurrentTeamContext(AutomationElement window) => ExpandSection(window, "CurrentTeamContext");
    private static string SecondaryReadingText(AutomationElement window, string section, string reading)
    {
        ExpandSection(window, section);
        return ((TextPattern)Find(window, reading).GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
    }

    private static void AssertOtherTeamNotesCollapsed(AutomationElement window)
    {
        Await(() => ((ExpandCollapsePattern)Find(window, "OtherTeamNotes").GetCurrentPattern(ExpandCollapsePattern.Pattern))
            .Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
        var list = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TeamNotes"));
        if (list is not null && !list.Current.IsOffscreen)
            throw new InvalidOperationException("Ordinary Team reading still displays a permanent title picker.");
    }

    private static void AssertCollapsedTeamWarnings(AutomationElement window, string email, string title)
    {
        ControlReadiness.AssertCollapsed(["CurrentTeamContext", "HistoryAccess"],
            section => ((ExpandCollapsePattern)Find(window, section).GetCurrentPattern(ExpandCollapsePattern.Pattern))
                .Current.ExpandCollapseState == ExpandCollapseState.Collapsed, Await);
        var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
        if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
        Await(() => !Find(window, "InheritedTeamReviewWarnings").Current.IsOffscreen);
        var warning = Find(window, "InheritedTeamReviewWarnings").Current.Name;
        if (!warning.Contains(email) || !warning.Contains(title) || !warning.Contains("Needs review"))
            throw new InvalidOperationException("Collapsed inherited context concealed attributed review needs.");
        if (!Find(window, "CurrentTeamAttribution").Current.Name.Contains(email))
            throw new InvalidOperationException("Collapsed current context lost the ancestor's readable attribution.");
        var reading = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "InheritedTeamReading"));
        if (reading is not null && !reading.Current.IsOffscreen)
            throw new InvalidOperationException("Collapsed inherited details still occupy the primary reading path.");
    }

    private static void ReviewInheritedTeamNote(AutomationElement window, string owner, string title)
    {
        ControlReadiness.AwaitPattern(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, $"Read / review '{title}' - owner {owner}")))
                ?? throw new InvalidOperationException("Missing actual UI control: attributed inherited Team-note review action."),
            control => control.Current.IsEnabled,
            control => (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern), Await).Invoke();
    }
}
