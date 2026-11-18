using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyConfiguredTransportCloseAndReopen(Type windowType, string root)
    {
        var source = Path.Combine(root, "configured-source");
        var target = Path.Combine(root, "configured-target");
        var outgoing = Path.Combine(root, "original-outgoing");
        var incoming = Path.Combine(root, "original-incoming");
        Directory.CreateDirectory(incoming);
        using var first = ApplicationWorkflow.CreateFileBacked();
        if (!first.OpenWorkspace(source) || !first.ApplyImport(first.PreviewImport(RecoveryPeople).Id))
            throw new InvalidOperationException(first.Status);
        var note = first.CreateTeamNote("morgan@example.com", "Exact transport note", "Original body")!;
        if (!first.ConfigureSynchronizationFolder(outgoing) || !first.SyncNow())
            throw new InvalidOperationException(first.Status);
        void Deliver()
        {
            foreach (var file in Directory.GetFiles(outgoing, "*.oesnapshot"))
                File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)), overwrite: true);
        }
        Deliver();
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(target) || !seed.ConfigureSynchronizationFolder(incoming, true) || !seed.SyncNow())
                throw new InvalidOperationException(seed.Status);
        var expectedRevision = first.Synchronization.LocalRevision;
        var identity = first.Synchronization.WorkspaceId;
        for (var pass = 0; pass < 2; pass++)
        {
            using var adoption = new SnapshotAdoptionCommitControl();
            using var store = ApplicationWorkflow.CreateFileBacked(fileObserver: adoption);
            var choices = new Queue<string>();
            choices.Enqueue(target);
            var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [store, (Func<Window, string?>)(_ => choices.Dequeue())], null)!;
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                Invoke("OpenSettingsButton");
                Invoke("OpenWorkspaceButton");
                PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
                if (model.Synchronization.Folder != incoming || model.Synchronization.WorkspaceId != identity ||
                    model.Synchronization.LocalRevision != expectedRevision)
                    throw new InvalidOperationException("Reopen changed the actual configured transport, Workspace or old committed head.");
                if (pass == 1)
                {
                    if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Visible) Invoke("OpenSettingsButton");
                    adoption.Arm();
                    Invoke("SyncNowButton");
                    PumpUntil(() => adoption.Read()?.Held == true);
                    adoption.Release();
                    PumpUntil(() => !model.IsSynchronizing && Control<Button>("SyncNowButton").IsEnabled &&
                        model.Synchronization.LocalRevision == first.Synchronization.LocalRevision);
                    if (adoption.Read() is not { Kind: WorkspaceStoragePhaseKind.End, CompletedAt: > 0 } ||
                        model.GetTeamNotes("morgan@example.com").Single(item => item.Id == note.Id).Markdown != "Delivered in original incoming")
                        throw new InvalidOperationException("The delivered original-folder continuation did not enter and complete the genuine adoption phase.");
                    expectedRevision = first.Synchronization.LocalRevision;
                }
                PrepareSmokeNormalClose(target, () => PublishSmokeWorkspace(target, () =>
                {
                    if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Visible) Invoke("OpenSettingsButton");
                    var transport = SmokePublicationFolder(target);
                    if (model.Synchronization.Folder != transport)
                    {
                        Directory.CreateDirectory(transport);
                        choices.Enqueue(transport);
                        Invoke("ConfigureSynchronizationFolderButton");
                        PumpUntil(() => model.Synchronization.Folder == transport && Control<Button>("SyncNowButton").IsEnabled);
                    }
                    Invoke("SyncNowButton");
                    PumpUntil(() => !model.IsSynchronizing && Control<Button>("SyncNowButton").IsEnabled &&
                        !model.Synchronization.HasUnpublishedChanges);
                }));
                window.Close();
                PumpUntil(() => closed);
                using var independent = OpenSmokeWorkspace(target);
                if (independent.Synchronization.Folder != incoming ||
                    independent.Synchronization.WorkspaceId != identity ||
                    independent.Synchronization.LocalRevision != expectedRevision ||
                    independent.Synchronization.PublishedRevision != expectedRevision)
                    throw new InvalidOperationException("Normal close preparation redirected the original configured incoming transport.");
            }
            finally
            {
                if (!closed) ClosePublishedCompiledWindow(window);
                if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                    throw new InvalidOperationException("The configured-transport replay created a native window.");
            }
            if (pass == 0)
            {
                if (!first.EditTeamNote(note.Id, note.Title, "Delivered in original incoming") || !first.SyncNow())
                    throw new InvalidOperationException(first.Status);
                Deliver();
            }
        }
        Console.WriteLine("PASS: two real stores and compiled normal-close/Configure/Sync/reopen retain the ORIGINAL configured incoming folder across both closes; delivered continuation enters the real adoption Commit phase and remains exactly published.");
    }
}
