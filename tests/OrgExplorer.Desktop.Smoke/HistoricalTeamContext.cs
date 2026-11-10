using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunTeamContextFlow(string executable, string root)
    {
        Process? process = null;
        var workspace = Path.Combine(root, "workspace");
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
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Set(window, "ImportPath", import);
            ApplyReviewedHistoryImport(window);
            SelectTreePerson(window, "casey@example.com");
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "About Casey");
            Set(window, "PersonNoteMarkdown", "Never inherited");
            AwaitLocalWorkIdle(window);
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Casey context");
            Set(window, "TeamNoteMarkdown", $"# Frozen ancestor\nOriginal **ancestor**\n![Remote image]({image})");
            SelectTab(window, "TeamNoteReadTab");
            Await(() => TeamReadingText(window).Contains("Original ancestor"));
            if (!TeamReadingText(window).Contains("Image not loaded"))
                throw new InvalidOperationException("Owned Team Markdown did not show remote-image suppression.");
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            NavigateToManager(window, "morgan@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Morgan context");
            Set(window, "TeamNoteMarkdown", "# Frozen direct\nOriginal direct");
            AwaitLocalWorkIdle(window);
            if (!Find(window, "TeamNoteOwnership").Current.Name.Contains("morgan@example.com"))
                throw new InvalidOperationException("Owned Team writing lost its explicit Manager attribution.");
            Set(window, "JobTitle", "Director");
            Await(() => Find(window, "TeamNoteReview").Current.Name.Contains("Needs review"));
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            var inherited = Find(window, "InheritedTeamSummary").Current.Name;
            if (!inherited.Contains("morgan@example.com") || !inherited.Contains("casey@example.com") ||
                !inherited.Contains("Needs review") || CurrentReadingText(window).Contains("Never inherited"))
                throw new InvalidOperationException("Inherited context lost ancestor attribution, review feedback or private scope.");
            Click(window, "ReviewTeamNote-morgan@example.com");
            Await(() => Find(window, "Email").Current.Name == "morgan@example.com");
            Click(window, "ConfirmTeamNote");
            Await(() => Find(window, "TeamNoteReview").Current.Name.Contains("Confirmed"));
            Click(window, "ArchiveTeamNote");
            Await(() => Find(window, "TeamNoteStatus").Current.Name.Contains("archived locally"));
            ((TogglePattern)Find(window, "ShowArchivedTeamNotes").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            SelectTeamNote(window, "Morgan context");
            AssertValue(window, "TeamNoteMarkdown", "# Frozen direct\nOriginal direct");
            Click(window, "ArchiveTeamNote");
            AwaitLocalWorkIdle(window);
            SelectTeamNote(window, "Morgan context");
            SelectTreePerson(window, "alex@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Alex context");
            Set(window, "TeamNoteMarkdown", "# Current branch\nNew branch writing");
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            if (!Find(window, "HistoricalTeamSummary").Current.Name.Contains("baseline"))
                throw new InvalidOperationException("Initial creation fabricated a historical transfer.");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"11000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-06T17:00:00Z","people":[
                 {"email":"morgan@example.com","managerEmail":"alex@example.com"}]}
                """);
            ApplyReviewedHistoryImport(window);
            Await(() => Find(window, "HistoricalTeamSummary").Current.Name.Contains("Historical captures: 1"));
            var summary = Find(window, "HistoricalTeamSummary").Current.Name;
            if (!summary.Contains("Morgan (morgan@example.com) → Casey (casey@example.com)") ||
                !summary.Contains("after 2026-11-01T17:00:00Z, by 2026-11-06T17:00:00Z") ||
                !summary.Contains("locally known at processing time") || !summary.Contains("unknown actual transfer instant"))
                throw new InvalidOperationException("Historical presentation lost former attribution or honest dates.");
            var frozen = HistoricalReadingText(window);
            if (!frozen.Contains("Original ancestor") || !frozen.Contains("Original direct") ||
                frozen.Contains("New branch writing") || !CurrentReadingText(window).Contains("New branch writing") ||
                CurrentReadingText(window).Contains("Original ancestor"))
                throw new InvalidOperationException($"Current and Historical writing were mixed.\nFrozen:\n{frozen}\nCurrent:\n{CurrentReadingText(window)}");
            NavigateToManager(window, "morgan@example.com");
            SelectTeamNote(window, "Morgan context");
            Set(window, "TeamNoteMarkdown", "# Later direct\nLater writing");
            OpenPerson(window, "alex@example.com", "morgan@example.com");
            if (HistoricalReadingText(window) != frozen || !CurrentReadingText(window).Contains("Later writing"))
                throw new InvalidOperationException("Later Team editing changed frozen writing or failed to update current reading.");
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window, "alex@example.com", "morgan@example.com");
            if (HistoricalReadingText(window) != frozen || !CurrentReadingText(window).Contains("Later writing"))
                throw new InvalidOperationException("Reopening lost separate current and Historical writing.");
            if (listener.Pending()) throw new InvalidOperationException("Team Markdown fetched remote content.");
            Close(process);
        }
        finally { if (process is not null) StopApplication(process); }
    }

    private static void ApplyReviewedHistoryImport(AutomationElement window)
    {
        Click(window, "PreviewImport");
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        Click(window, "ApplyImport");
        Await(() => Find(window, "Status").Current.Name.Contains("applied") ||
            Find(window, "Status").Current.Name.Contains("no changes"));
    }
    private static void SelectTeamNote(AutomationElement window, string title)
    {
        AwaitLocalWorkIdle(window);
        AutomationElement? item = null;
        Await(() => (item = Find(window, "TeamNotes").FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty, title)))) is not null);
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Value(window, "TeamNoteTitle") == title);
    }
    private static string TeamReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "TeamNoteReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
    private static string HistoricalReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "HistoricalTeamReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
    private static string CurrentReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "InheritedTeamReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
}
