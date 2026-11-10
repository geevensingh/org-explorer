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
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            OpenPerson(window, "casey@example.com");
            AssertPhotoVisible(window);
            Click(window, "NewPersonNote");
            Await(() => Find(window, "PersonNoteTitle").Current.IsEnabled);
            if (Find(window, "PersonNotes").Current.Name != "Person notes" ||
                Find(window, "PersonNoteTitle").Current.Name != "Person note title" ||
                Find(window, "PersonNoteMarkdown").Current.Name != "Person note Markdown")
                throw new InvalidOperationException("Person note title and writing lack distinct accessible names.");
            Set(window, "PersonNoteTitle", "Private meeting");
            Set(window, "PersonNoteMarkdown", markdown);
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("saved locally"));
            SelectTab(window, "PersonNoteReadTab");
            Await(() => ReadingText(window).Contains("Private context") && ReadingText(window).Contains("Meeting with Avery."));
            if (!ReadingText(window).Contains("R&D \u00a9 \U0001F680 <script>"))
                throw new InvalidOperationException("Markdown entities were lost or interpreted as HTML instead of readable text.");
            if (!Find(window, "PersonNoteOwnership").Current.Name.Contains("avery@example.com"))
                throw new InvalidOperationException("Person-note owner was not explicit.");
            if (!ReadingText(window).Contains("Image not loaded"))
                throw new InvalidOperationException("Image suppression was not visible in the rendered reading view.");
            SelectTab(window, "PersonNoteEditTab");
            AssertValue(window, "PersonNoteTitle", "Private meeting");
            AssertValue(window, "PersonNoteMarkdown", markdown);
            var noteFile = Directory.GetFiles(workspace, "*.md", SearchOption.AllDirectories).Single();
            using (File.Open(noteFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Set(window, "PersonNoteMarkdown", markdown + "\nRecovered draft.");
                Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
                Click(window, "NavigateManager");
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                SelectTreePerson(window, "casey@example.com", expectNavigation: false);
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                var otherWorkspace = Path.Combine(root, "other-workspace");
                Set(window, "WorkspacePath", otherWorkspace);
                Click(window, "OpenWorkspace");
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                if (Directory.Exists(otherWorkspace))
                    throw new InvalidOperationException("Unsaved Person note did not block Workspace navigation.");
                Set(window, "WorkspacePath", workspace);
                process.CloseMainWindow();
                Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("before leaving or closing"));
                if (process.HasExited) throw new InvalidOperationException("App closed despite an unsaved Person note.");
            }
            Click(window, "RetryPersonNoteSave");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("saved locally"));
            markdown += "\nRecovered draft.";
            NavigateToManager(window, "casey@example.com");
            if (!Find(window, "PersonNoteOwnership").Current.Name.Contains("casey@example.com") ||
                Find(window, "PersonNotes").FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count != 0)
                throw new InvalidOperationException("Manager navigation inherited the report's private Person notes.");
            var ownerReport = Find(window, "ImportedReports").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                ?? throw new InvalidOperationException("Manager lost the note owner's imported report.");
            ((SelectionItemPattern)ownerReport.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Await(() => Find(window, "Email").Current.Name == "avery@example.com");
            SelectNote(window, "Private meeting");
            AssertValue(window, "PersonNoteMarkdown", markdown);
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window, "casey@example.com");
            SelectNote(window, "Private meeting");
            if (Value(window, "PersonNoteMarkdown") != markdown)
                throw new InvalidOperationException("Markdown did not survive the actual app restart.");
            SelectTab(window, "PersonNoteReadTab");
            Await(() => ReadingText(window).Contains("Meeting with Avery."));
            Click(window, "ArchivePersonNote");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("archived locally"));
            ((TogglePattern)Find(window, "ShowArchivedPersonNotes").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            SelectNote(window, "Private meeting");
            SelectTab(window, "PersonNoteEditTab");
            if (Value(window, "PersonNoteMarkdown") != markdown)
                throw new InvalidOperationException("Archival discarded the writing.");
            SelectTab(window, "PersonNoteReadTab");
            Await(() => ReadingText(window).Contains("Private context"));
            if (listener.Pending()) throw new InvalidOperationException("Markdown rendering fetched remote content.");
            Close(process);
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static void SelectNote(AutomationElement window, string title)
    {
        AwaitLocalWorkIdle(window);
        AutomationElement? item = null;
        Await(() =>
        {
            item = Find(window, "PersonNotes").FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.NameProperty, title)));
            return item is not null;
        });
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Value(window, "PersonNoteTitle") == title);
    }

    private static void SelectTreePerson(AutomationElement window, string email, bool expectNavigation = true)
    {
        if (expectNavigation) AwaitLocalWorkIdle(window);
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
}
