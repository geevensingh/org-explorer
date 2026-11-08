using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunPersonNoteFlow(string executable)
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Process? process = null;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var markdown = "# Private context\nMeeting with **Avery**.\n\n"
            + $"![Remote portrait](http://127.0.0.1:{port}/portrait.png)\n\n"
            + $"<img src=\"http://127.0.0.1:{port}/html.png\">\n\n"
            + $"[External reference](http://127.0.0.1:{port}/reference)";
        try
        {
            var import = Path.Combine(root, "import.json");
            const string initialImport = """
                {"schemaVersion":1,"batchId":"20000000-0000-4000-8000-000000000001",
                "observedAt":"2026-11-01T17:00:00Z","people":[
                {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null},
                {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"casey@example.com"},
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
            Click(window, "NewPersonNote");
            Await(() => Find(window, "PersonNoteTitle").Current.IsEnabled);
            Set(window, "PersonNoteTitle", "Private meeting");
            Set(window, "PersonNoteMarkdown", markdown);
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("saved locally"));
            SelectTab(window, "PersonNoteReadTab");
            Await(() => ReadingText(window).Contains("Private context") && ReadingText(window).Contains("Meeting with Avery."));
            if (!Find(window, "PersonNoteOwnership").Current.Name.Contains("avery@example.com"))
                throw new InvalidOperationException("Person-note owner was not explicit.");
            if (!ReadingText(window).Contains("Image not loaded"))
                throw new InvalidOperationException("Image suppression was not visible in the rendered reading view.");
            Set(window, "PreferredName", "Ave");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            Set(window, "OfficeNumber", "007B");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            SelectTab(window, "PersonNoteEditTab");
            AssertValue(window, "PersonNoteTitle", "Private meeting");
            AssertValue(window, "PersonNoteMarkdown", markdown);
            SelectTab(window, "PersonNoteReadTab");
            var noteFile = Directory.GetFiles(workspace, "*.md", SearchOption.AllDirectories).Single();
            File.WriteAllText(noteFile, File.ReadAllText(noteFile) + "\nEdited externally.");
            markdown += "\nEdited externally.";
            Await(() => ReadingText(window).Contains("Edited externally."));
            SelectTab(window, "PersonNoteEditTab");
            using (File.Open(noteFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Set(window, "PersonNoteMarkdown", markdown + "\nRecovered draft.");
                Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
                Click(window, "NavigateManager");
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                SelectTreePerson(window, "casey@example.com");
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                ((ExpandCollapsePattern)Find(window, "avery@example.com").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                SelectTreePerson(window, "jordan@example.com");
                AssertBlockedNoteNavigation(window, markdown + "\nRecovered draft.");
                var report = Find(window, "ImportedReports").FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                    ?? throw new InvalidOperationException("Person-note owner has no imported report navigation.");
                ((SelectionItemPattern)report.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
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
            using (File.Open(noteFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Set(window, "PersonNoteMarkdown", markdown + "\nUnsaved competing draft.");
                Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
            }
            File.WriteAllText(noteFile, File.ReadAllText(noteFile) + "\nProvider update.");
            markdown += "\nProvider update.";
            Click(window, "RetryPersonNoteSave");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
            Click(window, "DiscardPersonNoteDraft");
            Await(() => Value(window, "PersonNoteMarkdown") == markdown);
            Click(window, "NavigateManager");
            Await(() => Find(window, "Email").Current.Name == "casey@example.com");
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
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"20000000-0000-4000-8000-000000000002",
                "observedAt":"2026-11-02T17:00:00Z","people":[
                {"email":"casey@example.com","archived":true},
                {"email":"avery@example.com","managerEmail":null,"officeNumber":"0009C"}]}
                """);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            Await(() => Find(window, "Status").Current.Name.Contains("applied"));
            AssertValue(window, "OfficeNumber", "0009C");
            AssertValue(window, "PreferredName", "Ave");
            AssertValue(window, "PersonNoteTitle", "Private meeting");
            AssertValue(window, "PersonNoteMarkdown", markdown);
            if (Find(window, "NavigateManager").Current.IsEnabled)
                throw new InvalidOperationException("Reporting move did not refresh the note owner's Manager.");
            File.WriteAllText(import, initialImport);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("no-op"))
                throw new InvalidOperationException("Person-note/profile changes broke accepted reporting replay.");
            Click(window, "ApplyImport");
            Await(() => Find(window, "Status").Current.Name.Contains("no changes"));
            AssertValue(window, "OfficeNumber", "0009C");
            AssertValue(window, "PersonNoteMarkdown", markdown);
            Click(window, "NewPersonNote");
            Set(window, "PersonNoteTitle", "Second note");
            Set(window, "PersonNoteMarkdown", "Independent writing");
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("saved locally"));
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window);
            AssertValue(window, "PreferredName", "Ave");
            AssertValue(window, "OfficeNumber", "0009C");
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
            if (process is not null && !process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
            Directory.Delete(root, true);
        }
    }

    private static void SelectNote(AutomationElement window, string title)
    {
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

    private static void SelectTreePerson(AutomationElement window, string email)
    {
        AutomationElement? item = null;
        Await(() => (item = Find(window, "People").FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, email))) is not null);
        var selection = (SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern);
        try { selection.Select(); }
        catch (ElementNotAvailableException)
        {
            // Restoring the unsaved-note owner rebuilds the tree and retires the selected provider.
            // Accept that response only after observing the actual blocked-navigation state.
            Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("before leaving or closing") &&
                ((SelectionItemPattern)Find(window, "avery@example.com")
                    .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
        }
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
