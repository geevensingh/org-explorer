using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;

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
            CaptureWindowIconEvidence(window, executable, create ? "installed-create" : "installed-reopen");
            if (create)
            {
                var producer = Path.Combine(root, "producer");
                Directory.CreateDirectory(producer);
                CreatePhotoFixture(Path.Combine(producer, "avery.png"));
                var import = Path.Combine(producer, "import.json");
                File.WriteAllText(import, InstallerWorkspacePreservation.InitialImport);
                OpenImportView(window);
                Set(window, "ImportPath", import);
                ApplyReviewedHistoryImport(window);
                OpenPerson(window, "casey@example.com", "morgan@example.com");
                AssertPhotoVisible(window);
                Click(window, "EditPreferredName");
                var nameEntry = PreferredNameEntry(window);
                Set(nameEntry, "PreferredName", "Ave");
                Click(nameEntry, "SavePreferredName");
                AwaitLocalWorkIdle(window);
                AssertPreferredNameReading(window, "Ave", "Edit");
                Click(window, "NewPersonNote");
                Set(window, "PersonNoteTitle", "Installer lifecycle note");
                Set(window, "PersonNoteMarkdown", "# Private fixture\nRetain this Person writing.");
                AwaitLocalWorkIdle(window);
                NavigateToManager(window, "morgan@example.com");
                Click(window, "NewTeamNote");
                Set(window, "TeamNoteTitle", "Installer Team context");
                Set(window, "TeamNoteMarkdown", "# Frozen fixture\nRetain former Manager writing.");
                AwaitLocalWorkIdle(window);
                File.WriteAllText(import, InstallerWorkspacePreservation.TransferImport);
                ApplyReviewedHistoryImport(window);
                OpenPerson(window, "casey@example.com", "alex@example.com");
                AssertInstallerWorkspace(window);
                ClosePublishedWorkspace(process, workspace);
                process.Dispose();
                process = null;
                Directory.Delete(producer, true);
                process = Launch(executable);
                window = OpenInstalledWorkspace(process, executable, workspace, args[3]);
            }
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertInstallerWorkspace(window);
            ClosePublishedWorkspace(process, workspace);
            process.Dispose();
            process = null;
            Console.WriteLine(create
                ? "PASS: installed WPF creates/saves/reopens independent profiles, photo, Person note, Profile changes and frozen Team context after producer removal."
                : "PASS: actual installed WPF reopens unchanged Workspace profiles, photo, Person note, Profile changes and frozen Team context.");
        }
        catch (Exception original)
        {
            Console.Error.WriteLine("Original installed desktop failure: " + original);
            try
            {
                if (process is not null && !process.HasExited)
                {
                    var observed = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                    if (observed is not null)
                        foreach (AutomationElement control in observed.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                            if (control.Current.AutomationId.Length > 0)
                                Console.Error.WriteLine($"{control.Current.AutomationId}: {control.Current.Name}");
                }
            }
            catch (Exception diagnostic) when (diagnostic is ElementNotAvailableException or InvalidOperationException or
                System.Runtime.InteropServices.COMException)
            {
                Console.Error.WriteLine("Read-only installed desktop diagnostics failed: " + diagnostic);
            }
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
            Directory.CreateDirectory(unsafeWorkspace);
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, unsafeWorkspace);
            Await(() => Status(window).Contains("outside the application installation"));
        }
        Directory.CreateDirectory(workspace);
        SetSettingsVisible(window, true);
        PickWorkspaceFolder(window, workspace);
        AwaitRestoredWorkspace(window, workspace,
            ReadSmokeWorkspacePreferences().GetLastView(workspace)?.PersonEmail ?? "");
        return window;
    }

    private static void AssertInstallerWorkspace(AutomationElement window)
    {
        Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
        AssertProfileValue(window, "OfficeNumber", "0002B");
        AssertProfileValue(window, "Department", "");
        AssertPreferredNameReading(window, "Ave", "Edit");
        AssertProfileValue(window, "Alias", "avery");
        AssertProfileValue(window, "JobTitle", "Engineer");
        AssertPhotoVisible(window);
        SelectNote(window, "Installer lifecycle note");
        SelectTab(window, "PersonNoteEditTab");
        AssertValue(window, "PersonNoteMarkdown", "# Private fixture\nRetain this Person writing.");
        ExpandHistory(window);
        if (!Find(window, "ProfileTimeline").Current.Name.Contains("morgan@example.com → alex@example.com") ||
            !HistoricalReadingText(window).Contains("Retain former Manager writing.") ||
            CurrentReadingText(window).Contains("Retain former Manager writing."))
            throw new InvalidOperationException("Installed desktop lost Profile changes or mixed frozen former Team context into current context.");
    }
}
