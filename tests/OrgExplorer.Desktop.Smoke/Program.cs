using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Process? process = null;
        try
        {
            if (args.Length is < 1 or > 4 || (args.Length >= 3 && args[2] is not ("create" or "reopen")))
                throw new ArgumentException("Supply <actual WPF executable> [external Workspace] [create|reopen] [expected version].");
            var reopenOnly = args.Length >= 3 && args[2] == "reopen";
            var producer = Path.Combine(root, "producer");
            Directory.CreateDirectory(producer);
            CreatePhotoFixture(Path.Combine(producer, "avery.png"));
            var initialPhoto = File.ReadAllBytes(Path.Combine(producer, "avery.png"));
            var import = Path.Combine(producer, "import.json");
            const string initialImport = """
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
                "observedAt":"2026-11-01T17:00:00Z","people":[
                {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null},
                {"email":"avery@example.com","displayName":"Avery Example","alias":"avery",
                 "jobTitle":"Engineer","department":"Research","officeNumber":"007B","managerEmail":"morgan@example.com","photoPath":"avery.png"},
                {"email":"alex@example.com","displayName":"Alex Example","managerEmail":"casey@example.com"},
                {"email":"jordan@example.com","displayName":"Jordan Example","managerEmail":"alex@example.com"},
                {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":"casey@example.com"}]}
                """;
            File.WriteAllText(import, initialImport);
            var workspace = args.Length >= 2 ? Path.GetFullPath(args[1]) : Path.Combine(root, "workspace");
            process = Launch(args[0]);
            var window = Window(process);
            if (args.Length == 4 && !window.Current.Name.Contains($"Org explorer {args[3]} (Windows x64 / win)"))
                throw new InvalidOperationException("Real desktop version/platform/channel does not match the packaged release.");
            var applicationDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
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
            if (reopenOnly)
            {
                OpenPerson(window, "casey@example.com", "alex@example.com");
                Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
                AssertValue(window, "OfficeNumber", "0002B");
                AssertValue(window, "Department", "");
                AssertValue(window, "PreferredName", "Ave");
                AssertValue(window, "Alias", "avery");
                AssertValue(window, "JobTitle", "Engineer");
                AssertPhotoVisible(window);
                Close(process);
                process = null;
                Console.WriteLine("PASS: actual WPF reopens unchanged external Workspace and retained Person after installation replacement.");
                return 0;
            }
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("avery@example.com"))
                throw new InvalidOperationException("Actual UI did not show the import preview.");
            Click(window, "ApplyImport");
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Avery Example");
            AssertPhotoVisible(window);
            Click(window, "NavigateManager");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Morgan Example");
            var report = Find(window, "ImportedReports").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            if (report is null) throw new InvalidOperationException("Imported report navigation missing.");
            ((SelectionItemPattern)report.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Await(() => Find(window, "ProfileHeading").Current.Name == "Avery Example");
            AssertValue(window, "Alias", "avery");
            AssertValue(window, "JobTitle", "Engineer");
            AssertValue(window, "OfficeNumber", "007B");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000002",
                "observedAt":"2026-11-02T17:00:00Z","people":[
                {"email":"morgan@example.com","archived":true}]}
                """);
            Click(window, "PreviewImport");
            Await(() => Find(window, "Status").Current.Name.Contains("Import rejected"));
            if (Find(window, "ApplyImport").Current.IsEnabled ||
                !Find(window, "Preview").Current.Name.Contains("avery@example.com.managerEmail"))
                throw new InvalidOperationException("Invalid graph was not visibly rejected by actual UI.");
            Set(window, "OfficeNumber", "0009C");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            Set(window, "PreferredName", "Ave");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            Close(process);
            Directory.Delete(producer, true);
            import = Path.Combine(root, "import.json");
            process = Launch(args[0]);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window, "casey@example.com", "morgan@example.com");
            AssertPhotoVisible(window);
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            if (Find(window, "DirectoryName").Current.Name != "Avery Example")
                throw new InvalidOperationException("Directory name was not retained after reopening.");
            if (((ValuePattern)Find(window, "PreferredName").GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "Ave")
                throw new InvalidOperationException("Preferred name did not survive reopening.");
            AssertValue(window, "OfficeNumber", "0009C");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000003",
                "observedAt":"2026-11-03T17:00:00Z","people":[
                {"email":"morgan@example.com","archived":true},
                {"email":"avery@example.com","managerEmail":"alex@example.com","officeNumber":"0002B","department":null}]}
                """);
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("Manager") ||
                !Find(window, "Preview").Current.Name.Contains("0002B"))
                throw new InvalidOperationException("Combined reporting/profile changes were not reviewed.");
            AssertValue(window, "OfficeNumber", "0009C");
            Click(window, "ApplyImport");
            Await(() => Find(window, "Status").Current.Name.Contains("applied"));
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertValue(window, "OfficeNumber", "0002B");
            AssertValue(window, "Department", "");
            AssertValue(window, "Alias", "avery");
            AssertValue(window, "JobTitle", "Engineer");
            AssertPhotoVisible(window);
            File.WriteAllBytes(Path.Combine(root, "avery.png"), initialPhoto);
            File.WriteAllText(import, initialImport);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("no-op"))
                throw new InvalidOperationException("Accepted graph/profile replay was not a visible no-op.");
            Click(window, "ApplyImport");
            Await(() => Find(window, "Status").Current.Name.Contains("no changes"));
            Close(process);
            process = Launch(args[0]);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window, "casey@example.com", "alex@example.com");
            AssertValue(window, "OfficeNumber", "0002B");
            AssertValue(window, "Department", "");
            AssertValue(window, "PreferredName", "Ave");
            AssertValue(window, "Alias", "avery");
            AssertValue(window, "JobTitle", "Engineer");
            AssertPhotoVisible(window);
            ExercisePhotoImports(window, root);
            if (args.Length >= 2)
            {
                var retainedPhotoImport = Path.Combine(root, "retained-photo.json");
                File.WriteAllText(retainedPhotoImport, """
                    {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000028",
                    "observedAt":"2026-11-03T17:00:00Z","people":[
                    {"email":"avery@example.com","photoPath":"avery.png"}]}
                    """);
                Set(window, "ImportPath", retainedPhotoImport);
                Click(window, "PreviewImport");
                Await(() => Find(window, "ApplyImport").Current.IsEnabled);
                Click(window, "ApplyImport");
                AssertPhotoVisible(window);
            }
            Close(process);
            process = null;
            PersonAttributeFlow.Run(args[0], root);
            RunPersonNoteFlow(args[0]);
            Console.WriteLine("PASS: actual WPF whole-batch preview/apply, reporting navigation, invalid graph rejection, replay, textual profiles, PNG/JPEG photo pixels, and private Person-note edit/read/autosave/archive/reopen flows.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (process is not null && !process.HasExited)
                foreach (AutomationElement control in Window(process).FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    if (control.Current.AutomationId.Length > 0)
                        Console.Error.WriteLine($"{control.Current.AutomationId}: {control.Current.Name}");
            return 1;
        }
        finally
        {
            if (process is not null && !process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
            Directory.Delete(root, true);
        }
    }

    internal static Process Launch(string path) => Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = false })
        ?? throw new InvalidOperationException("WPF application did not launch.");
    internal static AutomationElement Window(Process process)
    {
        AutomationElement? window = null;
        Await(() =>
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException($"App exited with {process.ExitCode}.");
            window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
            return window is not null;
        });
        return window!;
    }
    internal static AutomationElement Find(AutomationElement window, string id) =>
        window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
        ?? throw new InvalidOperationException("Missing actual UI control: " + id);
    internal static void Set(AutomationElement window, string id, string value) =>
        ((ValuePattern)Find(window, id).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    internal static void Click(AutomationElement window, string id) =>
        ((InvokePattern)Find(window, id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void AssertValue(AutomationElement window, string id, string expected)
    {
        var actual = ((ValuePattern)Find(window, id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        if (actual != expected) throw new InvalidOperationException($"{id}: expected '{expected}', got '{actual}'.");
    }
    internal static void OpenPerson(AutomationElement window, params string[] ancestors)
    {
        foreach (var email in ancestors)
        {
            AutomationElement? ancestor = null;
            Await(() => (ancestor = Find(window, "People").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, email))) is not null);
            var expand = (ExpandCollapsePattern)ancestor!.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            expand.Expand();
            Await(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded);
            expand.Collapse();
            Await(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
            expand.Expand();
        }
        AutomationElement? item = null;
        Await(() => (item = Find(window, "People").FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "avery@example.com"))) is not null);
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Find(window, "PreferredName").Current.IsEnabled);
    }
    internal static void Close(Process process)
    {
        process.CloseMainWindow();
        if (!process.WaitForExit(10000)) throw new TimeoutException("App did not close normally.");
    }
    internal static void Await(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (condition()) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("Actual UI condition did not become true within 20 seconds.");
    }
}
