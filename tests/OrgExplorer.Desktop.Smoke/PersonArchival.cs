using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunPersonArchivalFlow(string executable, string root)
    {
        Process? process = null;
        var workspace = Path.Combine(root, "archival-workspace");
        var producer = Path.Combine(root, "archival-producer");
        Directory.CreateDirectory(producer);
        CreatePhotoFixture(Path.Combine(producer, "avery.png"));
        var import = Path.Combine(producer, "archival.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"12000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery","preferredName":"Ave","alias":"avery",
              "officeNumber":"007B","managerEmail":"morgan@example.com","photoPath":"avery.png"},
             {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"casey@example.com"},
             {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
             {"email":"alex@example.com","displayName":"Alex","managerEmail":null}]}
            """);
        try
        {
            process = Launch(executable);
            var window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Set(window, "ImportPath", import);
            ApplyReviewedHistoryImport(window);
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Retained individual");
            Set(window, "PersonNoteMarkdown", "# Retained individual writing");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Retained owned Team note");
            Set(window, "TeamNoteMarkdown", "Owned Team writing");
            Click(window, "NavigateManager");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Manager context");
            Set(window, "TeamNoteMarkdown", "# Frozen Manager\nOriginal Manager writing");
            Click(window, "NavigateManager");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Ancestor context");
            Set(window, "TeamNoteMarkdown", "# Frozen ancestor\nOriginal ancestor writing");
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            Click(window, "NavigateManager");
            Click(window, "ArchivePerson");
            Await(() => Find(window, "Status").Current.Name.Contains("avery@example.com.managerEmail"));
            if (!Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Active") ||
                !Find(window, "Status").Current.Name.Contains("No cascade"))
                throw new InvalidOperationException("In-app Manager archival did not visibly reject inconsistent active reports.");

            OpenPerson(window, "casey@example.com", "morgan@example.com");
            SelectNote(window, "Retained individual");
            using (new FileStream(Path.Combine(workspace, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Click(window, "ArchivePerson");
                Await(() => Find(window, "Status").Current.Name.Contains("NOT saved"));
                if (!Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Active"))
                    throw new InvalidOperationException("Failed archival falsely published an Archived profile.");
                AssertPhotoVisible(window);
                Click(window, "NavigateManager");
                if (Find(window, "Email").Current.Name != "avery@example.com")
                    throw new InvalidOperationException("Failed archival did not retain the pending profile.");
                process.CloseMainWindow();
                Await(() => Find(window, "Status").Current.Name.Contains("Unsaved local changes prevent safe closing"));
                if (process.HasExited) throw new InvalidOperationException("Failed archival allowed unsafe closing.");
            }
            Click(window, "RetryProfileSave");
            Await(() => Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived"));
            AssertPhotoVisible(window);
            AssertValue(window, "PreferredName", "Ave");
            AssertValue(window, "OfficeNumber", "007B");
            AssertValue(window, "PersonNoteMarkdown", "# Retained individual writing");
            SelectTeamNote(window, "Retained owned Team note");
            AssertValue(window, "TeamNoteMarkdown", "Owned Team writing");
            if (!Find(window, "TeamNoteReview").Current.Name.Contains("Needs review") ||
                !Find(window, "HistoricalTeamSummary").Current.Name.Contains("Archival closure") ||
                CurrentReadingText(window).Contains("Original Manager writing"))
                throw new InvalidOperationException("Archival did not close current context and retain owned/frozen writing.");
            AssertAbsentFromCurrentTree(window, "avery@example.com");
            var frozen = HistoricalReadingText(window);
            if (!frozen.Contains("Original Manager writing") || !frozen.Contains("Original ancestor writing"))
                throw new InvalidOperationException("Archival did not freeze every ancestor's known writing.");
            Close(process);
            Directory.Delete(producer, true);

            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            ((TogglePattern)Find(window, "ShowArchivedPeople").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            SelectArchivedPerson(window, "avery@example.com");
            AssertPhotoVisible(window);
            AssertValue(window, "Alias", "avery");
            AssertValue(window, "OfficeNumber", "007B");
            SelectNote(window, "Retained individual");
            AssertValue(window, "PersonNoteMarkdown", "# Retained individual writing");
            SelectTeamNote(window, "Retained owned Team note");
            AssertValue(window, "TeamNoteMarkdown", "Owned Team writing");
            if (HistoricalReadingText(window) != frozen)
                throw new InvalidOperationException("Archived-profile reopening lost frozen context.");
            Set(window, "PersonNoteTitle", "");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
            SelectTreePerson(window, "casey@example.com");
            if (Find(window, "Email").Current.Name != "avery@example.com")
                throw new InvalidOperationException("Archived-profile navigation discarded an unsaved retained-note draft.");
            AssertSelectionMatchesProfile(window);
            Click(window, "DiscardPersonNoteDraft");
            AssertValue(window, "PersonNoteTitle", "Retained individual");
            Click(window, "NavigateManager");
            SelectTeamNote(window, "Manager context");
            Set(window, "TeamNoteMarkdown", "# Later Manager\nLater Manager writing");
            SelectArchivedPerson(window, "avery@example.com");
            AssertSelectionMatchesProfile(window);
            if (HistoricalReadingText(window) != frozen)
                throw new InvalidOperationException("Later edits rewrote archived history.");
            Click(window, "ReactivatePerson");
            Await(() => Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Active"));
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            AssertPhotoVisible(window);
            if (!CurrentReadingText(window).Contains("Later Manager writing") ||
                HistoricalReadingText(window) != frozen)
                throw new InvalidOperationException("Reactivation mixed frozen writing into current context.");

            import = Path.Combine(root, "archival-import.json");
            Set(window, "ImportPath", import);
            File.WriteAllText(import, ArchivalImport("""
                {"email":"morgan@example.com","archived":true},
                {"email":"avery@example.com","archived":true}
                """, 2));
            ApplyReviewedHistoryImport(window);
            Await(() => Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived"));
            Click(window, "ReactivatePerson");
            Await(() => Find(window, "Status").Current.Name.Contains("active reports require an active Manager"));
            if (!Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived"))
                throw new InvalidOperationException("Reactivation accepted an Archived Manager.");
            File.WriteAllText(import, ArchivalImport("""{"email":"avery@example.com","officeCountry":"Canada"}""", 3));
            ApplyReviewedHistoryImport(window);
            if (!Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived"))
                throw new InvalidOperationException("Ordinary update implicitly reactivated a Person.");
            File.WriteAllText(import, ArchivalImport("""
                {"email":"avery@example.com","archived":false},
                {"email":"morgan@example.com","archived":false}
                """, 4));
            ApplyReviewedHistoryImport(window);
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            AssertValue(window, "OfficeCountry", "Canada");
            var allFrozen = HistoricalReadingText(window);
            ApplyReviewedHistoryImport(window);
            if (HistoricalReadingText(window) != allFrozen)
                throw new InvalidOperationException("Archival/reactivation replay changed frozen captures.");
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            AssertPhotoVisible(window);
            SelectNote(window, "Retained individual");
            AssertValue(window, "PersonNoteMarkdown", "# Retained individual writing");
            AssertValue(window, "OfficeCountry", "Canada");
            if (HistoricalReadingText(window) != allFrozen ||
                !Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Active"))
                throw new InvalidOperationException("Reopening lost durable reactivation/history.");
            SelectManagerChoice(window, "(explicit no-Manager — clear relationship)");
            Click(window, "ApplyManagerChange");
            Await(() => Find(window, "Status").Current.Name.Contains("Manager change saved locally"));
            if (!Find(window, "NavigateManager").Current.Name.Contains("explicit no-Manager"))
                throw new InvalidOperationException("Explicit in-app Manager clear was not applied.");
            SelectTreePerson(window, "morgan@example.com");
            Click(window, "ArchivePerson");
            Await(() => Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Archived"));
            if (!Find(window, "HistoricalTeamSummary").Current.Name.Contains("Archival closure"))
                throw new InvalidOperationException("Explicit report changes did not allow consistent Manager archival.");
            SelectTeamNote(window, "Manager context");
            AssertValue(window, "TeamNoteMarkdown", "# Later Manager\nLater Manager writing");
            Click(window, "ReactivatePerson");
            Await(() => Find(window, "PersonArchivalStatus").Current.Name.StartsWith("Active"));
            SelectTreePerson(window, "avery@example.com");
            if (CurrentReadingText(window).Contains("Later Manager writing"))
                throw new InvalidOperationException("Cleared Manager relationship retained current inheritance.");
            AssertPhotoVisible(window);
            Close(process);
            Console.WriteLine("PASS: actual WPF archive/filter/reopen/reactivate, retained photo/notes/history, explicit Manager changes, failed-save retry/safe-close, whole-batch graph validation and replay.");
        }
        finally
        {
            if (process is not null && !process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
        }
    }

    private static string ArchivalImport(string people, int id) => $$"""
        {"schemaVersion":1,"batchId":"12000000-0000-4000-8000-{{id:000000000000}}",
         "observedAt":"2026-11-06T17:00:00Z","people":[{{people}}]}
        """;

    private static void SelectArchivedPerson(AutomationElement window, string email)
    {
        AutomationElement? person = null;
        Await(() => (person = Find(window, "ArchivedPeople").FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, email))) is not null);
        ((SelectionItemPattern)person!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Find(window, "Email").Current.Name == email);
    }

    private static void AssertAbsentFromCurrentTree(AutomationElement window, string email)
    {
        if (Find(window, "People").FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, email)) is not null)
            throw new InvalidOperationException("Archived Person remained in the ordinary current tree.");
    }

    private static void SelectManagerChoice(AutomationElement window, string label)
    {
        var combo = Find(window, "ManagerChoice");
        if (Find(window, "ProfileScroll").TryGetCurrentPattern(ScrollPattern.Pattern, out var scrolling))
            ((ScrollPattern)scrolling).SetScrollPercent(ScrollPattern.NoScroll, 0);
        combo.SetFocus();
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        AutomationElement? choice = null;
        var condition = new AndCondition(new PropertyCondition(AutomationElement.NameProperty, label),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        Await(() => (choice = combo.FindFirst(TreeScope.Descendants, condition) ??
            AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(condition,
                new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId)))) is not null);
        ((SelectionItemPattern)choice!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        expand.Collapse();
    }

    private static void AssertSelectionMatchesProfile(AutomationElement window)
    {
        var email = Find(window, "Email").Current.Name;
        foreach (var list in new[] { "People", "ArchivedPeople" })
            foreach (AutomationElement element in Find(window, list).FindAll(TreeScope.Descendants, Condition.TrueCondition))
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selected) &&
                    ((SelectionItemPattern)selected).Current.IsSelected && element.Current.AutomationId != email)
                    throw new InvalidOperationException("Navigation left a different Person selected while showing the retained profile.");
    }
}
