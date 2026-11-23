using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        Exception? primary = null;
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
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            application?.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
            AssemblyLoadContext.Default.Resolving -= Resolve;
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception cleanup) when (primary is not null && cleanup is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("HOSTED-CUTOVER-CLEANUP-FAILURE " +
                    $"primary={primary.GetType().Name}; cleanup={cleanup.GetType().Name}: {cleanup.Message}");
            }
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
        VerifySynchronizationConfigurationOwnership(windowType, root);
    }

    public class QueuedSynchronizationBoundary : DispatchProxy
    {
        public IApplicationWorkflow Inner = null!;
        public Action? BeforeConfigure;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IApplicationWorkflow.ConfigureSynchronizationFolder)) BeforeConfigure?.Invoke();
            try { return method.Invoke(Inner, arguments); }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }

    private static void VerifySynchronizationConfigurationOwnership(Type windowType, string root)
    {
        foreach (var join in new[] { false, true })
        {
            var source = Path.Combine(root, "configuration-source-" + join);
            var replacement = Path.Combine(root, "configuration-replacement-" + join);
            var folder = Path.Combine(root, "configuration-folder-" + join);
            using var store = ApplicationWorkflow.CreateFileBacked();
            var proxy = DispatchProxy.Create<IApplicationWorkflow, QueuedSynchronizationBoundary>();
            var boundary = (QueuedSynchronizationBoundary)(object)proxy;
            boundary.Inner = store;
            var choice = source;
            var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [proxy, (Func<Window, string?>)(_ => choice), (Action<string>)(_ => { })], null)!;
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            Invoke("OpenSettingsButton");
            Invoke("OpenWorkspaceButton");
            PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
            var original = model.Synchronization.WorkspaceId;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            boundary.BeforeConfigure = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new IOException("Queued synchronization configuration was not released.");
            };
            choice = folder;
            Invoke(join ? "JoinSynchronizationFolderButton" : "ConfigureSynchronizationFolderButton");
            try
            {
                PumpUntil(() => entered.IsSet);
                choice = replacement;
                Invoke("OpenWorkspaceButton");
                PumpUntil(() => model.Compatibility.WorkspacePath == replacement &&
                    Control<Button>("OpenWorkspaceButton").IsEnabled);
                if (model.Synchronization.WorkspaceId == original)
                    throw new InvalidOperationException("Configuration replacement did not establish an independent Workspace.");
            }
            finally { release.Set(); }
            PumpUntil(() => Control<Button>("SyncNowButton").IsEnabled ||
                Control<Button>("ConfigureSynchronizationFolderButton").IsEnabled);
            boundary.BeforeConfigure = null;
            if (model.Compatibility.WorkspacePath != replacement || model.Synchronization.Folder is not null ||
                Directory.Exists(folder) || !model.Status.Contains("changed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Queued Configure/Join mutated the replacement Workspace instead of refusing its original request.");
            if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "configuration-cleanup-" + join)) || !model.SyncNow())
                throw new InvalidOperationException(model.Status);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Configuration ownership verification cannot create a native window.");
            window.Close();
            PumpUntil(() => closed);
        }
        Console.WriteLine("PASS: actual compiled queued Configure and Join reject a replaced Workspace without configuring or creating the captured folder.");
    }
}
