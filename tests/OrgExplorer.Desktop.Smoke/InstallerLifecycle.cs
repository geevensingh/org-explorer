using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunInstallerFlow(string[] args, string root)
    {
        Process? process = null;
        var executable = args[0];
        var workspace = Path.GetFullPath(args[1]);
        var create = args[2] == "create";
        try
        {
            process = Launch(executable);
            var window = OpenInstalledWorkspace(process, executable, workspace, args[3]);
            if (create)
            {
                var producer = Path.Combine(root, "producer");
                Directory.CreateDirectory(producer);
                CreatePhotoFixture(Path.Combine(producer, "avery.png"));
                var import = Path.Combine(producer, "import.json");
                File.WriteAllText(import, """
                    {"schemaVersion":1,"batchId":"16000000-0000-4000-8000-000000000001",
                    "observedAt":"2026-11-01T17:00:00Z","people":[
                    {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null},
                    {"email":"avery@example.com","displayName":"Avery Example","alias":"avery",
                     "jobTitle":"Engineer","department":"Research","officeNumber":"007B",
                     "managerEmail":"morgan@example.com","photoPath":"avery.png"},
                    {"email":"alex@example.com","displayName":"Alex Example","managerEmail":"casey@example.com"},
                    {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":"casey@example.com"}]}
                    """);
                Set(window, "ImportPath", import);
                ApplyReviewedHistoryImport(window);
                OpenPerson(window, "casey@example.com", "morgan@example.com");
                AssertPhotoVisible(window);
                Set(window, "PreferredName", "Ave");
                AwaitLocalWorkIdle(window);
                Click(window, "NewPersonNote");
                Set(window, "PersonNoteTitle", "Installer lifecycle note");
                Set(window, "PersonNoteMarkdown", "# Private fixture\nRetain this Person writing.");
                AwaitLocalWorkIdle(window);
                NavigateToManager(window, "morgan@example.com");
                Click(window, "NewTeamNote");
                Set(window, "TeamNoteTitle", "Installer Team context");
                Set(window, "TeamNoteMarkdown", "# Frozen fixture\nRetain former Manager writing.");
                AwaitLocalWorkIdle(window);
                File.WriteAllText(import, """
                    {"schemaVersion":1,"batchId":"16000000-0000-4000-8000-000000000002",
                    "observedAt":"2026-11-03T17:00:00Z","people":[
                    {"email":"morgan@example.com","archived":true},
                    {"email":"avery@example.com","managerEmail":"alex@example.com","officeNumber":"0002B","department":null}]}
                    """);
                ApplyReviewedHistoryImport(window);
                OpenPerson(window, "casey@example.com", "alex@example.com");
                AssertInstallerWorkspace(window);
                Close(process);
                process.Dispose();
                process = null;
                Directory.Delete(producer, true);
                process = Launch(executable);
                window = OpenInstalledWorkspace(process, executable, workspace, args[3]);
            }
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertInstallerWorkspace(window);
            Close(process);
            process.Dispose();
            process = null;
            Console.WriteLine(create
                ? "PASS: installed WPF creates/saves/reopens independent profiles, photo, Person note, Profile changes and frozen Team context after producer removal."
                : "PASS: actual installed WPF reopens unchanged Workspace profiles, photo, Person note, Profile changes and frozen Team context.");
        }
        catch
        {
            if (process is not null && !process.HasExited)
                foreach (AutomationElement control in Window(process).FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    if (control.Current.AutomationId.Length > 0)
                        Console.Error.WriteLine($"{control.Current.AutomationId}: {control.Current.Name}");
            throw;
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
                process.Dispose();
            }
        }
    }

    private static AutomationElement OpenInstalledWorkspace(Process process, string executable, string workspace, string version)
    {
        var window = Window(process);
        if (!window.Current.Name.Contains($"Org explorer {version} (Windows x64 / win)"))
            throw new InvalidOperationException("Real desktop version/platform/channel does not match the installed fixture.");
        var applicationDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var installation = File.Exists(Path.Combine(applicationDirectory, "..", "Update.exe"))
            ? Path.GetFullPath(Path.Combine(applicationDirectory, "..")) : applicationDirectory;
        foreach (var unsafeWorkspace in new[] { applicationDirectory, installation, Path.Combine(installation, "private-data"),
            Path.GetDirectoryName(installation)!, Path.GetPathRoot(installation)! })
        {
            Set(window, "WorkspacePath", unsafeWorkspace);
            Click(window, "OpenWorkspace");
            Await(() => Find(window, "Status").Current.Name.Contains("outside the application installation"));
        }
        Set(window, "WorkspacePath", workspace);
        Click(window, "OpenWorkspace");
        Await(() => Find(window, "Status").Current.Name.Contains("Workspace opened"));
        return window;
    }

    private static void AssertInstallerWorkspace(AutomationElement window)
    {
        Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
        AssertValue(window, "OfficeNumber", "0002B");
        AssertValue(window, "Department", "");
        AssertValue(window, "PreferredName", "Ave");
        AssertValue(window, "Alias", "avery");
        AssertValue(window, "JobTitle", "Engineer");
        AssertPhotoVisible(window);
        SelectNote(window, "Installer lifecycle note");
        SelectTab(window, "PersonNoteEditTab");
        AssertValue(window, "PersonNoteMarkdown", "# Private fixture\nRetain this Person writing.");
        if (!Find(window, "ProfileTimeline").Current.Name.Contains("morgan@example.com → alex@example.com") ||
            !HistoricalReadingText(window).Contains("Retain former Manager writing.") ||
            CurrentReadingText(window).Contains("Retain former Manager writing."))
            throw new InvalidOperationException("Installed desktop lost Profile changes or mixed frozen former Team context into current context.");
    }
}
