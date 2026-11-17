using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static int VerifyHostedCutoverContracts(string executable, Action<Type, string> verify)
    {
        AssertDisplayInterop();
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var assemblyPath = Path.ChangeExtension(Path.GetFullPath(executable), ".dll");
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
            resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "hosted-cutover-" + Guid.NewGuid().ToString("N")));
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var previousContext = SynchronizationContext.Current;
        Application? application = null;
        Directory.CreateDirectory(root);
        AssemblyLoadContext.Default.Resolving += Resolve;
        Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", Path.Combine(root, "settings"));
        try
        {
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            verify(assembly.GetType("OrgExplorer.Desktop.MainWindow", throwOnError: true)!, root);
            return 0;
        }
        finally
        {
            application?.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
            AssemblyLoadContext.Default.Resolving -= Resolve;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyHostedPublicationContract(Type windowType, string root)
    {
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var choices = new Queue<string>();
        choices.Enqueue(workspace);
        using var store = ApplicationWorkflow.CreateFileBacked();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => choices.Dequeue())], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var preview = model.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000101",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null}]}
            """);
        if (!preview.CanApply || !model.ApplyImport(preview.Id))
            throw new InvalidOperationException("The publication contract did not seed actual local writing: " + model.Status);
        if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Visible) Invoke("OpenSettingsButton");
        var transport = SmokeSnapshotFolder(workspace);
        ConfigureSmokeSnapshotFolder(workspace, folder =>
        {
            RequireSmokeSelectableFolder(folder);
            choices.Enqueue(folder);
            Invoke("ConfigureSynchronizationFolderButton");
        });
        PumpUntil(() => model.Synchronization.Folder == transport && Control<Button>("SyncNowButton").IsEnabled);
        Invoke("SyncNowButton");
        PumpUntil(() => !model.IsSynchronizing && !model.Synchronization.HasUnpublishedChanges);
        if (!model.EditFavorite("owner@example.com", true))
            throw new InvalidOperationException("The close contract did not create its latest unpublished writing: " + model.Status);
        var latest = model.Synchronization.LocalRevision;
        if (!model.Synchronization.HasUnpublishedChanges)
            throw new InvalidOperationException("Normal close was not tested with an actually newer local revision.");
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The publication contract cannot create a native window.");
        window.Close();
        PumpUntil(() => closed);
        using var receiver = ApplicationWorkflow.CreateFileBacked();
        if (!receiver.OpenWorkspace(Path.Combine(root, "receiver")) ||
            !receiver.ConfigureSynchronizationFolder(transport, joinExistingWorkspace: true) || !receiver.SyncNow() ||
            receiver.Synchronization.LocalRevision != latest ||
            receiver.GetRelationships("owner@example.com")?.Person.IsFavorite != true)
            throw new InvalidOperationException("Actual normal close did not publish the exact latest complete writing: " + receiver.Status);
        Console.WriteLine("PASS: shared snapshot chooser preparation reaches actual compiled Configure/Sync and normal latest-publication close; independent real-store joining verifies the committed writing. No native UI or Exit anyway.");
    }
}
