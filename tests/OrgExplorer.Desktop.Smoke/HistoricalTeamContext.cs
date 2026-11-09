using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunHistoricalTeamContextFlow(string executable, string root)
    {
        Process? process = null;
        var workspace = Path.Combine(root, "historical-workspace");
        var import = Path.Combine(root, "historical-import.json");
        try
        {
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"11000000-0000-4000-8000-000000000001",
                "observedAt":"2026-11-01T17:00:00Z","producer":"Fictitious desktop directory","people":[
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
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            if (!Find(window, "HistoricalTeamSummary").Current.Name.Contains("baseline"))
                throw new InvalidOperationException("Initial creation fabricated a historical transfer.");
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Individual");
            Set(window, "PersonNoteMarkdown", "Direct writing survives");
            NavigateToManager(window, "morgan@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Morgan context");
            Set(window, "TeamNoteMarkdown", "# Frozen direct\nOriginal direct");
            NavigateToManager(window, "casey@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Casey context");
            Set(window, "TeamNoteMarkdown", "# Frozen ancestor\nOriginal ancestor");
            SelectTreePerson(window, "alex@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Alex context");
            Set(window, "TeamNoteMarkdown", "# Current branch\nNew branch writing");
            OpenPerson(window, "casey@example.com", "morgan@example.com");

            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"11000000-0000-4000-8000-000000000002",
                "observedAt":"2026-11-06T17:00:00Z","producer":"Fictitious desktop directory","people":[
                {"email":"morgan@example.com","managerEmail":"alex@example.com"}]}
                """);
            ApplyReviewedHistoryImport(window);
            Await(() => Find(window, "HistoricalTeamSummary").Current.Name.Contains("Historical captures: 1"));
            var summary = Find(window, "HistoricalTeamSummary").Current.Name;
            if (!summary.Contains("Morgan (morgan@example.com) → Casey (casey@example.com)") ||
                !summary.Contains("Reporting transition for morgan@example.com") ||
                !summary.Contains("after 2026-11-01T17:00:00Z, by 2026-11-06T17:00:00Z") ||
                !summary.Contains("locally known at processing time") ||
                !summary.Contains("unknown actual transfer instant"))
                throw new InvalidOperationException("Ancestor history lost its old chain or honest provenance.");
            var frozen = HistoricalReadingText(window);
            var current = CurrentReadingText(window);
            if (!frozen.Contains("Original ancestor") || !frozen.Contains("Original direct") ||
                frozen.Contains("New branch writing") || !current.Contains("New branch writing") ||
                current.Contains("Original ancestor"))
                throw new InvalidOperationException($"Actual WPF mixed current and historical Team writing.\nFrozen:\n{frozen}\nCurrent:\n{current}");

            SelectTreePerson(window, "casey@example.com");
            SelectTeamNote(window, "Casey context");
            Set(window, "TeamNoteMarkdown", "# Changed former root\nLater root writing");
            Set(window, "PreferredName", "Casey renamed later");
            OpenPerson(window, "alex@example.com", "morgan@example.com");
            NavigateToManager(window, "morgan@example.com");
            SelectTeamNote(window, "Morgan context");
            Set(window, "TeamNoteMarkdown", "# Changed direct\nLater direct writing");
            OpenPerson(window, "alex@example.com", "morgan@example.com");
            if (HistoricalReadingText(window) != frozen ||
                !CurrentReadingText(window).Contains("Later direct writing"))
                throw new InvalidOperationException("Later note edits rewrote a frozen capture.");
            SelectNote(window, "Individual");
            AssertValue(window, "PersonNoteMarkdown", "Direct writing survives");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"11000000-0000-4000-8000-000000000003",
                "observedAt":"2026-11-07T17:00:00Z","people":[
                {"email":"avery@example.com","managerEmail":"alex@example.com"}]}
                """);
            ApplyReviewedHistoryImport(window);
            Await(() => Find(window, "HistoricalTeamSummary").Current.Name.Contains("Historical captures: 2"));
            if (Find(window, "InheritedTeamSummary").Current.Name.Contains("morgan@example.com") ||
                !CurrentReadingText(window).Contains("New branch writing"))
                throw new InvalidOperationException("Direct transfer did not replace current inheritance.");
            AssertValue(window, "PersonNoteMarkdown", "Direct writing survives");
            var both = HistoricalReadingText(window);
            if (!both.Contains("Original ancestor") || !both.Contains("Later direct writing") ||
                both.Contains("Later root writing"))
                throw new InvalidOperationException("Successive captures did not retain independent note versions.");
            ApplyReviewedHistoryImport(window);
            if (!Find(window, "HistoricalTeamSummary").Current.Name.Contains("Historical captures: 2") ||
                HistoricalReadingText(window) != both)
                throw new InvalidOperationException("Desktop replay duplicated or changed frozen context.");
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window, "alex@example.com");
            if (HistoricalReadingText(window) != both ||
                !Find(window, "HistoricalTeamSummary").Current.Name.Contains("Historical captures: 2"))
                throw new InvalidOperationException("Actual desktop reopening lost frozen historical context.");
            SelectNote(window, "Individual");
            AssertValue(window, "PersonNoteMarkdown", "Direct writing survives");
            if (CurrentReadingText(window).Contains("Original ancestor") ||
                CurrentReadingText(window).Contains("Later direct writing"))
                throw new InvalidOperationException("Reopening mixed old notes into current inheritance.");
            Close(process);
            Console.WriteLine("PASS: actual WPF baseline, ancestor/direct transfers, immutable history, provenance, later Team edits, replay and reopen.");
        }
        finally
        {
            if (process is not null && !process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
        }
    }

    private static void ApplyReviewedHistoryImport(AutomationElement window)
    {
        Click(window, "PreviewImport");
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        Click(window, "ApplyImport");
        Await(() => Find(window, "Status").Current.Name.Contains("applied") ||
            Find(window, "Status").Current.Name.Contains("no changes"));
    }

    private static string HistoricalReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "HistoricalTeamReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
    private static string CurrentReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "InheritedTeamReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
}
