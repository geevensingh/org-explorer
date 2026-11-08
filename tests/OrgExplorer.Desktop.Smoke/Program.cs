using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static class Program
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
            var import = Path.Combine(root, "import.json");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
                "observedAt":"2026-11-01T17:00:00Z","people":[
                {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
                """);
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
            if (!reopenOnly)
            {
                Set(window, "ImportPath", import);
                Click(window, "PreviewImport");
                Await(() => Find(window, "ApplyImport").Current.IsEnabled);
                if (!Find(window, "Preview").Current.Name.Contains("avery@example.com"))
                    throw new InvalidOperationException("Actual UI did not show the import preview.");
                Click(window, "ApplyImport");
                OpenPerson(window);
                Set(window, "PreferredName", "Ave");
                Await(() => Find(window, "Status").Current.Name == "Saved locally.");
                Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            }
            Close(process);
            process = Launch(args[0]);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window);
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            if (Find(window, "DirectoryName").Current.Name != "Avery Example")
                throw new InvalidOperationException("Directory name was not retained after reopening.");
            if (((ValuePattern)Find(window, "PreferredName").GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "Ave")
                throw new InvalidOperationException("Preferred name did not survive reopening.");
            Console.WriteLine(reopenOnly
                ? "PASS: actual WPF reopens unchanged external Workspace and retained Person after installation replacement."
                : "PASS: actual WPF launch → Workspace → preview/apply → Person → edit/save → close/reopen.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (process is not null && !process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
            Directory.Delete(root, true);
        }
    }

    private static Process Launch(string path) => Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = false })
        ?? throw new InvalidOperationException("WPF application did not launch.");
    private static AutomationElement Window(Process process)
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
    private static AutomationElement Find(AutomationElement window, string id) =>
        window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
        ?? throw new InvalidOperationException("Missing actual UI control: " + id);
    private static void Set(AutomationElement window, string id, string value) =>
        ((ValuePattern)Find(window, id).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    private static void Click(AutomationElement window, string id) =>
        ((InvokePattern)Find(window, id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void OpenPerson(AutomationElement window)
    {
        AutomationElement? item = null;
        Await(() =>
        {
            item = Find(window, "People").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            return item is not null;
        });
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Await(() => Find(window, "PreferredName").Current.IsEnabled);
    }
    private static void Close(Process process)
    {
        process.CloseMainWindow();
        if (!process.WaitForExit(10000)) throw new TimeoutException("App did not close normally.");
    }
    private static void Await(Func<bool> condition)
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
