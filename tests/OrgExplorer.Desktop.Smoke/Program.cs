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
            if (args.Length != 1) throw new ArgumentException("Supply the actual built WPF executable path.");
            var producer = Path.Combine(root, "producer");
            Directory.CreateDirectory(producer);
            CreatePhotoFixture(Path.Combine(producer, "avery.png"));
            var import = Path.Combine(producer, "import.json");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
                "observedAt":"2026-11-01T17:00:00Z","people":[
                {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null,"photoPath":"avery.png"}]}
                """);
            var workspace = Path.Combine(root, "workspace");
            process = Launch(args[0]);
            var window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Await(() => Find(window, "Status").Current.Name.Contains("Workspace opened"));
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("avery@example.com"))
                throw new InvalidOperationException("Actual UI did not show the import preview.");
            Click(window, "ApplyImport");
            OpenPerson(window);
            AssertPhotoVisible(window);
            Set(window, "PreferredName", "Ave");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            Close(process);
            Directory.Delete(producer, true);
            process = Launch(args[0]);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window);
            AssertPhotoVisible(window);
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            if (Find(window, "DirectoryName").Current.Name != "Avery Example")
                throw new InvalidOperationException("Directory name was not retained after reopening.");
            if (((ValuePattern)Find(window, "PreferredName").GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "Ave")
                throw new InvalidOperationException("Preferred name did not survive reopening.");
            ExercisePhotoImports(window, root);
            Close(process);
            process = null;
            PersonAttributeFlow.Run(args[0], root);
            Console.WriteLine("PASS: actual WPF launch → Workspace → preview/apply → Person → edit/save → close/reopen.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
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
    internal static void OpenPerson(AutomationElement window)
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
