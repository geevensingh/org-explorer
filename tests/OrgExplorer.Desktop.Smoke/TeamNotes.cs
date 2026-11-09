using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunTeamNoteFlow(string executable)
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Process? process = null;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var markdown = $"# Organization context\nPrivate **Root** writing.\n![Image](http://127.0.0.1:{port}/image.png)";
        try
        {
            var import = Path.Combine(root, "import.json");
            var workspace = Path.Combine(root, "workspace");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000001",
                "observedAt":"2026-11-01T17:00:00Z","people":[
                {"email":"root@example.com","displayName":"Root Example","managerEmail":null},
                {"email":"manager@example.com","displayName":"Manager Example","managerEmail":"root@example.com"},
                {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"manager@example.com"},
                {"email":"replacement@example.com","displayName":"Replacement Example","managerEmail":null}]}
                """);
            process = Launch(executable);
            var window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            SelectTreePerson(window, "root@example.com");
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "About Root");
            Set(window, "PersonNoteMarkdown", "Never inherited");
            AwaitLocalWorkIdle(window);
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Organization");
            Set(window, "TeamNoteMarkdown", markdown);
            AwaitLocalWorkIdle(window);
            SelectTab(window, "TeamNoteReadTab");
            Await(() => TeamReadingText(window).Contains("Private Root writing."));
            OpenPerson(window, "root@example.com", "manager@example.com");
            NavigateToManager(window, "manager@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Manager context");
            Set(window, "TeamNoteMarkdown", "Manager writing");
            AwaitLocalWorkIdle(window);
            var managerFile = Directory.GetFiles(Path.Combine(workspace, "team-notes"), "*.md")
                .Single(f => File.ReadAllText(f).Contains("manager@example.com"));
            Set(window, "JobTitle", "Director");
            Await(() => Find(window, "TeamNoteReview").Current.Name.Contains("Needs review"));
            using (File.Open(managerFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(window, "ConfirmTeamNote");
                Await(() => Find(window, "TeamNoteStatus").Current.Name.Contains("NOT saved"));
                if (!Find(window, "TeamNoteReview").Current.Name.Contains("Needs review"))
                    throw new InvalidOperationException("Failed confirmation cleared the Team-note warning.");
                Set(window, "TeamNoteMarkdown", "Unsaved Team draft");
                Await(() => Find(window, "TeamNoteStatus").Current.Name.Contains("NOT saved"));
                Click(window, "NavigateManager");
                Await(() => Find(window, "TeamNoteStatus").Current.Name.Contains("before leaving or closing"));
                if (Find(window, "Email").Current.Name != "manager@example.com")
                    throw new InvalidOperationException("Unsaved Team draft failed to block navigation.");
            }
            Click(window, "RetryTeamNoteSave");
            Await(() => Find(window, "TeamNoteStatus").Current.Name.Contains("saved locally"));
            Set(window, "TeamNoteMarkdown", "Manager writing");
            AwaitLocalWorkIdle(window);
            Set(window, "Department", "Research");
            Await(() => Find(window, "TeamNoteReview").Current.Name.Contains("Needs review"));
            OpenPerson(window, "root@example.com", "manager@example.com");
            var inherited = Find(window, "InheritedTeamSummary").Current.Name;
            if (!inherited.Contains("manager@example.com") || !inherited.Contains("root@example.com") ||
                !inherited.Contains("Needs review"))
                throw new InvalidOperationException("All-ancestor ownership/review was not explicit in inherited context.");
            if (Find(window, "PersonNotes").FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count != 0)
                throw new InvalidOperationException("Person notes opted into Team-note inheritance.");
            var inheritedText = ((TextPattern)Find(window, "InheritedTeamReading")
                .GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
            if (!inheritedText.Contains("Organization context") || !inheritedText.Contains("Manager writing") ||
                inheritedText.Contains("Never inherited"))
                throw new InvalidOperationException("Inherited reading failed scope or Markdown rendering.");
            Click(window, "ReviewTeamNote-manager@example.com");
            Await(() => Find(window, "Email").Current.Name == "manager@example.com");
            Click(window, "ConfirmTeamNote");
            Await(() => Find(window, "TeamNoteReview").Current.Name.Contains("Confirmed"));
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "To archive");
            Set(window, "TeamNoteMarkdown", "Retained archived writing");
            AwaitLocalWorkIdle(window);
            Set(window, "JobTitle", "Vice President");
            Await(() => Find(window, "TeamNoteReview").Current.Name.Contains("Needs review"));
            Click(window, "ArchiveTeamNote");
            Await(() => Find(window, "TeamNoteStatus").Current.Name.Contains("archived locally"));
            ((TogglePattern)Find(window, "ShowArchivedTeamNotes").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            SelectTeamNote(window, "To archive");
            AssertValue(window, "TeamNoteMarkdown", "Retained archived writing");
            SelectTreePerson(window, "replacement@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Replacement context");
            Set(window, "TeamNoteMarkdown", "New chain writing");
            AwaitLocalWorkIdle(window);
            OpenPerson(window, "root@example.com", "manager@example.com");
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Avery direct");
            Set(window, "PersonNoteMarkdown", "Direct survives move");
            AwaitLocalWorkIdle(window);
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"90000000-0000-4000-8000-000000000002",
                "observedAt":"2026-11-02T17:00:00Z","people":[
                {"email":"avery@example.com","managerEmail":"replacement@example.com"}]}
                """);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("needs review"))
                throw new InvalidOperationException("Change review omitted flagged Team context.");
            Click(window, "ApplyImport");
            Await(() => Find(window, "InheritedTeamSummary").Current.Name.Contains("replacement@example.com"));
            if (Find(window, "InheritedTeamSummary").Current.Name.Contains("root@example.com") ||
                Find(window, "InheritedTeamSummary").Current.Name.Contains("manager@example.com"))
                throw new InvalidOperationException("Old inherited context followed a current reporting move.");
            AssertValue(window, "PersonNoteMarkdown", "Direct survives move");
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window, "replacement@example.com");
            Await(() => Find(window, "InheritedTeamSummary").Current.Name.Contains("Replacement context"));
            SelectNote(window, "Avery direct");
            AssertValue(window, "PersonNoteMarkdown", "Direct survives move");
            SelectTreePerson(window, "root@example.com");
            ((ExpandCollapsePattern)Find(window, "root@example.com").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            SelectTreePerson(window, "manager@example.com");
            SelectTeamNote(window, "Manager context");
            if (!Find(window, "TeamNoteOwnership").Current.Name.Contains("manager@example.com"))
                throw new InvalidOperationException("Replacement acquired the former Manager's Team notes.");
            AssertValue(window, "TeamNoteMarkdown", "Manager writing");
            if (listener.Pending()) throw new InvalidOperationException("Team Markdown fetched remote content.");
            Close(process);
            Console.WriteLine("PASS: actual WPF explicit Team scope, all ancestors, attribution, review actions, failed-save safety, moves, private rendering and reopen.");
        }
        finally
        {
            if (process is not null && !process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
            Directory.Delete(root, true);
        }
    }

    private static void SelectTeamNote(AutomationElement window, string title)
    {
        AutomationElement? item = null;
        Await(() => (item = Find(window, "TeamNotes").FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty, title)))) is not null);
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Value(window, "TeamNoteTitle") == title);
    }

    private static string TeamReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "TeamNoteReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
}
