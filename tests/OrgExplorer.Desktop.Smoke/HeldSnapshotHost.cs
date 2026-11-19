using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static Window CreateHeldSnapshotWindow(Type windowType, IApplicationWorkflow workflow, string workspace) =>
        (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [workflow, (Func<Window, string?>)(_ => workspace)], null)!;

    private static int RunHeldSnapshotFixture(string[] args)
    {
        var startupOnly = args.Length == 6 && args[5] == "startup-only";
        if (args.Length != 5 && !startupOnly || !Path.GetFullPath(args[2]).Contains(
            Path.DirectorySeparatorChar + ".test-workspaces" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The held-adoption host requires its owned isolated test Workspace.");
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var assemblyPath = Path.ChangeExtension(Path.GetFullPath(args[1]), ".dll");
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
            resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            using var control = new SnapshotAdoptionCommitControl(args[3], args[4]);
            using var workflow = ApplicationWorkflow.CreateFileBacked(fileObserver: control);
            var window = CreateHeldSnapshotWindow(assembly.GetType("OrgExplorer.Desktop.MainWindow", true)!, workflow, args[2]);
            if (startupOnly)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var preferences = ReadSmokeWorkspacePreferences();
                var remembered = preferences.GetLastView(args[2]) ??
                    throw new InvalidOperationException("The startup check has no independently remembered view.");
                var model = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                PumpUntil(() => model.IsWorkspaceCurrent && AutomationProperties.GetItemStatus(window) == "Ready" &&
                    ((Button)window.FindName("OpenWorkspaceButton")).IsEnabled);
                if (((TextBlock)window.FindName("Email")).Text != remembered.PersonEmail ||
                    ReadSmokeWorkspacePreferences().GetLastView(args[2]) != remembered ||
                    window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                    throw new InvalidOperationException("The owned no-window host lost its exact remembered startup or created a native view.");
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                PumpUntil(() => closed);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    hostKind = "Owned no-window controlled-WPF startup",
                    processId = Environment.ProcessId, model.Synchronization.WorkspaceId,
                    model.Synchronization.LocalRevision, remembered,
                    revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                    runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                    hostExecutableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath!))),
                    productDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
                    noNativeWindow = true
                }));
                return 0;
            }
            application.Run(window);
            return 0;
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static Process LaunchHeldSnapshotFixture(string executable, string workspace, SnapshotAdoptionCommitControl control,
        bool startupOnly = false)
    {
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = startupOnly };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--held-snapshot-fixture",
            Path.GetFullPath(executable), workspace, control.ArmName, control.ReleaseName })
            start.ArgumentList.Add(argument);
        if (startupOnly) start.ArgumentList.Add("startup-only");
        var process = Process.Start(start)!;
        Applications.Add(process);
        return process;
    }
}
