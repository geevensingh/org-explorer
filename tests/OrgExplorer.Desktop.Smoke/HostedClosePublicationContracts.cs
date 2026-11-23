using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyHostedClosePublicationContracts(Type windowType, string root)
    {
        foreach (var siblingCommit in new[] { false, true })
        {
            var path = Path.Combine(root, siblingCommit ? "close-sibling" : "close-published");
            var folder = path + "-snapshots";
            var observer = new ClosePublicationObserver();
            using var store = SeedClosePublicationWorkspace(path, folder, observer);
            var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [store, (Func<Window, string?>)(_ => throw new InvalidOperationException("Close must not open a picker."))], null)!;
            var model = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            var expected = model.Synchronization.LocalRevision;
            try
            {
                if (!model.ExitStatus.CanClose)
                    throw new InvalidOperationException("The compiled close contract must begin with cached Ready.");
                if (siblingCommit)
                {
                    using var sibling = ApplicationWorkflow.CreateFileBacked();
                    if (!sibling.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                        !sibling.EditPreferredName("owner@example.com", "Exact sibling writing after cached Ready"))
                        throw new InvalidOperationException("The compiled close contract could not advance the real sibling head: " + sibling.Status);
                    expected = sibling.Synchronization.LocalRevision;
                    if (!model.ExitStatus.CanClose || expected == model.Synchronization.LocalRevision)
                        throw new InvalidOperationException("The real sibling commit did not leave the window's cached head stale.");
                }
                observer.ValidationWrites = 0;
                observer.PublicationReads = 0;
                var artifact = Directory.GetFiles(folder, "*.oesnapshot").Single();
                var original = File.ReadAllBytes(artifact);
                RequireNoNativeCloseWindow(window);
                window.Close();
                PumpUntil(() => closed);
                if (observer.PublicationReads == 0 || !siblingCommit && observer.ValidationWrites != 0)
                    throw new InvalidOperationException("Published clean Closing must freshly verify its receipt without entering real snapshot validation/export.");
                if (!File.ReadAllBytes(artifact).SequenceEqual(original))
                    throw new InvalidOperationException("Normal close changed the prior complete publication.");
                using var receiver = ApplicationWorkflow.CreateFileBacked();
                if (!receiver.OpenWorkspace(path + "-receiver") ||
                    !receiver.ConfigureSynchronizationFolder(folder, joinExistingWorkspace: true) || !receiver.SyncNow() ||
                    receiver.Synchronization.LocalRevision != expected ||
                    siblingCommit && receiver.GetRelationships("owner@example.com")?.Person.PreferredName != "Exact sibling writing after cached Ready")
                    throw new InvalidOperationException("Actual compiled Closing did not confirm the exact current complete head: " + receiver.Status);
                RequireNoNativeCloseWindow(window);
            }
            finally
            {
                if (!closed)
                {
                    ((Button)window.FindName("CancelExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.Close();
                    PumpUntil(() => closed);
                }
            }
        }
        VerifyHostedUnavailableClosePublication(windowType, root);
        VerifyHostedDivergedClosePublication(windowType, root);
        VerifyHostedQueuedSynchronizationClose(windowType, root);
        var existingExitRoot = Path.Combine(root, "close-existing");
        Directory.CreateDirectory(existingExitRoot);
        VerifyCompiledWorkspaceExit(windowType, existingExitRoot);
        Console.WriteLine("PASS: actual compiled Closing freshly verifies cached Ready without a validation/export cycle; confirms a real sibling-local head; refuses unavailable publication and retained divergence; preserves older-export/newer-writing and repeated/canceled intents. Independent real stores confirm exact writing and retained evidence. No native UI.");
    }

    private static void VerifyHostedQueuedSynchronizationClose(Type windowType, string root)
    {
        var path = Path.Combine(root, "close-queued-configuration");
        var originalFolder = path + "-published";
        var selectedFolder = path + "-new-folder";
        using var store = SeedClosePublicationWorkspace(path, originalFolder);
        var proxy = DispatchProxy.Create<IApplicationWorkflow, QueuedSynchronizationBoundary>();
        var boundary = (QueuedSynchronizationBoundary)(object)proxy;
        boundary.Inner = store;
        // The fixture owns the store so a failed Close assertion can still release queued work safely.
        boundary.CallerOwnsLifetime = true;
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [proxy, (Func<Window, string?>)(_ => selectedFolder)], null)!;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var verified = 0;
        boundary.AfterExitVerification = () => Interlocked.Increment(ref verified);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        boundary.BeforeConfigure = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new IOException("Queued close configuration was not released.");
        };
        ((Button)window.FindName("OpenSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)window.FindName("ConfigureSynchronizationFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            PumpUntil(() => entered.IsSet);
            RequireNoNativeCloseWindow(window);
            window.Close();
            PumpUntil(() => closed || Volatile.Read(ref verified) >= 2);
            if (closed)
                throw new InvalidOperationException("Normal close completed before its already accepted synchronization operation.");
        }
        finally { release.Set(); }
        PumpUntil(() => closed);
        using var reopened = OpenSmokeWorkspace(path);
        if (reopened.Synchronization.Folder != selectedFolder || reopened.Synchronization.HasUnpublishedChanges)
            throw new InvalidOperationException("Normal close did not finish and publish its accepted folder configuration.");
        Console.WriteLine("PASS: actual compiled normal close waits for queued synchronization work and its latest publication.");
    }

    private static IApplicationWorkflow SeedClosePublicationWorkspace(string path, string folder,
        IWorkspaceFileObserver? observer = null)
    {
        var store = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        try
        {
            if (!store.OpenWorkspace(path) || !store.ApplyImport(store.PreviewImport("""
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000102",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"owner@example.com","displayName":"Owner","managerEmail":null}]}
                """).Id) || !store.ConfigureSynchronizationFolder(folder) || !store.SyncNow())
                throw new InvalidOperationException("The compiled close contract could not prepare a real published Workspace: " + store.Status);
            return store;
        }
        catch { store.Dispose(); throw; }
    }

    private static void VerifyHostedUnavailableClosePublication(Type windowType, string root)
    {
        var path = Path.Combine(root, "close-unavailable");
        var folder = path + "-snapshots";
        var unavailable = folder + "-unavailable";
        using var store = SeedClosePublicationWorkspace(path, folder);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => throw new InvalidOperationException("Close must not open a picker."))], null)!;
        var model = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var head = model.Synchronization.LocalRevision;
        var artifact = Directory.GetFiles(folder, "*.oesnapshot").Single();
        var bytes = File.ReadAllBytes(artifact);
        if (!model.ExitStatus.CanClose)
            throw new InvalidOperationException("Unavailable-publication Closing must begin with cached Ready.");
        Directory.Move(folder, unavailable);
        try
        {
            RequireNoNativeCloseWindow(window);
            window.Close();
            PumpUntil(() => closed || model.Synchronization.Error is not null && !model.IsSynchronizing);
            if (closed || model.ExitStatus.CanClose || model.ExitStatus.HasUnsavedChanges || !model.CanEditLocalWorkspace ||
                model.Synchronization.LocalRevision != head ||
                ((FrameworkElement)window.FindName("ExitDecision")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Actual Closing treated unavailable publication as fresh Ready or as local data loss.");
            ((Button)window.FindName("CancelExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (closed || ((FrameworkElement)window.FindName("ExitDecision")).Visibility != Visibility.Collapsed ||
                !File.ReadAllBytes(Path.Combine(unavailable, Path.GetFileName(artifact))).SequenceEqual(bytes))
                throw new InvalidOperationException("Cancel changed the complete unavailable publication or exit intent.");
        }
        finally
        {
            ((Button)window.FindName("CancelExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Directory.Move(unavailable, folder);
            if (!closed)
            {
                window.Close();
                PumpUntil(() => closed);
            }
        }
        using var reading = ApplicationWorkflow.CreateFileBacked();
        if (!reading.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
            reading.Synchronization.LocalRevision != head || reading.Synchronization.HasUnpublishedChanges ||
            !File.ReadAllBytes(artifact).SequenceEqual(bytes))
            throw new InvalidOperationException("Fresh close retry replayed local writing or altered retained complete publication.");
    }

    private static void VerifyHostedDivergedClosePublication(Type windowType, string root)
    {
        var path = Path.Combine(root, "close-diverged");
        var folder = path + "-snapshots";
        var remotePath = path + "-remote";
        var remoteFolder = remotePath + "-snapshots";
        using var store = SeedClosePublicationWorkspace(path, folder);
        Directory.CreateDirectory(remoteFolder);
        foreach (var file in Directory.GetFiles(folder, "*.oesnapshot"))
            File.Copy(file, Path.Combine(remoteFolder, Path.GetFileName(file)));
        using var remote = ApplicationWorkflow.CreateFileBacked();
        if (!remote.OpenWorkspace(remotePath) || !remote.ConfigureSynchronizationFolder(remoteFolder, true) ||
            !remote.SyncNow() || !remote.EditPreferredName("owner@example.com", "Other complete retained version") ||
            !remote.SyncNow() || !store.EditPreferredName("owner@example.com", "Current complete retained version") ||
            !store.SyncNow())
            throw new InvalidOperationException("The compiled divergence contract could not prepare independent complete versions.");
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => throw new InvalidOperationException("Close must not open a picker."))], null)!;
        var model = (WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            foreach (var file in Directory.GetFiles(remoteFolder, "*.oesnapshot"))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
            using var sibling = ApplicationWorkflow.CreateFileBacked();
            if (!sibling.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) || sibling.SyncNow() ||
                !sibling.Synchronization.Diverged || !model.ExitStatus.CanClose)
                throw new InvalidOperationException("A real sibling must persist known divergence after the compiled model cached Ready.");
            var review = sibling.GetWorkspaceVersionReview()
                ?? throw new InvalidOperationException("The actual retained versions have no public review.");
            var retained = Directory.GetFiles(Path.Combine(path, "snapshot-conflicts"), "*.oesnapshot")
                .ToDictionary(file => file, File.ReadAllBytes);
            RequireNoNativeCloseWindow(window);
            window.Close();
            PumpUntil(() => closed || model.Synchronization.Diverged);
            if (closed || model.ExitStatus.CanClose || !model.ExitStatus.HasConflict || model.CanEditLocalWorkspace ||
                model.GetRelationships("owner@example.com")?.Person.PreferredName != "Current complete retained version")
                throw new InvalidOperationException("Actual Closing cleared known divergence or chose a complete version.");
            ((Button)window.FindName("CancelExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (closed || ((FrameworkElement)window.FindName("ExitDecision")).Visibility != Visibility.Collapsed ||
                model.GetWorkspaceVersionReview()?.Id != review.Id ||
                retained.Any(entry => !File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value)))
                throw new InvalidOperationException("Divergent Closing/Cancel changed evidence or public version authorization.");
            RequireNoNativeCloseWindow(window);
        }
        finally
        {
            ((Button)window.FindName("CancelExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // The runner shuts down this never-shown window; fixture cleanup must not select a version merely to close.
            model.Dispose();
        }
    }

    private static void RequireNoNativeCloseWindow(Window window)
    {
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The close-publication contract cannot show or create a native window.");
    }

    private sealed class ClosePublicationObserver : IWorkspaceFileObserver
    {
        public int ValidationWrites, PublicationReads;
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Kind != WorkspaceStoragePhaseKind.Begin) return;
            if (phase.Name == "snapshot.validate-write") Interlocked.Increment(ref ValidationWrites);
            if (phase.Name == "exit.publication-read") Interlocked.Increment(ref PublicationReads);
        }
    }
}
