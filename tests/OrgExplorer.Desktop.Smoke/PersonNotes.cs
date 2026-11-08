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
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"20000000-0000-4000-8000-000000000001",
                "observedAt":"2026-11-01T17:00:00Z","people":[
                {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
                """);
            var workspace = Path.Combine(root, "workspace");
            process = Launch(executable);
            var window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            OpenPerson(window);
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
            var noteFile = Directory.GetFiles(workspace, "*.md", SearchOption.AllDirectories).Single();
            File.WriteAllText(noteFile, File.ReadAllText(noteFile) + "\nEdited externally.");
            markdown += "\nEdited externally.";
            Await(() => ReadingText(window).Contains("Edited externally."));
            SelectTab(window, "PersonNoteEditTab");
            using (File.Open(noteFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Set(window, "PersonNoteMarkdown", markdown + "\nRecovered draft.");
                Await(() => Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved"));
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

    private static string Value(AutomationElement window, string id) =>
        ((ValuePattern)Find(window, id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    private static string ReadingText(AutomationElement window) =>
        ((TextPattern)Find(window, "PersonNoteReading").GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
    private static void SelectTab(AutomationElement window, string id) =>
        ((SelectionItemPattern)Find(window, id).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
}
