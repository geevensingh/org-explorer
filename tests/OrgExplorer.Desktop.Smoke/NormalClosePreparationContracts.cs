using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyNormalClosePublicationPreparation(Type windowType, string root)
    {
        var path = Path.Combine(root, "workspace");
        var choices = new Queue<string>();
        choices.Enqueue(path);
        using var store = ApplicationWorkflow.CreateFileBacked();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => choices.Dequeue())], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var preview = model.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000113",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null,"officeNumber":"Original"}]}
            """);
        if (!preview.CanApply || !model.ApplyImport(preview.Id))
            throw new InvalidOperationException(model.Status);
        Invoke("OpenSettingsButton");
        var owner = model.ReportingRoots.Single();
        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, owner, TreeView.SelectedItemChangedEvent));
        Invoke("OpenImportButton");
        var selected = model.PeopleNavigation.SelectedEmail;
        Invoke("OpenSettingsButton");
        var transport = SmokeSnapshotFolder(path);
        ConfigureSmokeSnapshotFolder(path, folder =>
        {
            choices.Enqueue(folder);
            Invoke("ConfigureSynchronizationFolderButton");
        });
        PumpUntil(() => model.Synchronization.Folder == transport && Control<Button>("SyncNowButton").IsEnabled);
        void Publish()
        {
            Invoke("SyncNowButton");
            PumpUntil(() => !model.IsSynchronizing && !model.Synchronization.HasUnpublishedChanges &&
                model.Synchronization.PublishedRevision == model.Synchronization.LocalRevision);
        }
        Publish();
        var earlierPublished = model.Synchronization.PublishedRevision;
        var newer = model.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000114",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"owner@example.com","officeNumber":"Latest reviewed Import"}]}
            """);
        if (!newer.CanApply || !model.ApplyImport(newer.Id) ||
            model.Synchronization.LocalRevision == earlierPublished || !model.Synchronization.HasUnpublishedChanges)
            throw new InvalidOperationException("Normal-close setup did not retain a genuinely newer committed Import.");
        Publish();
        Invoke("OpenSettingsButton");
        var revision = model.Synchronization.LocalRevision;
        if (!model.IsImportOpen || Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Visible ||
            model.PeopleNavigation.SelectedEmail != selected)
            throw new InvalidOperationException("Publication preparation lost the actual Import closing view or selected owner.");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        try { PumpUntil(() => closed); }
        catch (TimeoutException)
        {
            Console.WriteLine("NORMAL-CLOSE-PREPARATION " + JsonSerializer.Serialize(new
            {
                selected, revision, model.IsImportOpen, model.IsSynchronizing,
                model.Synchronization, model.LocalSave,
                exitDecision = Control<FrameworkElement>("ExitDecision").Visibility.ToString(),
                exitRisk = Control<TextBlock>("ExitRisk").Text,
                settingsOpen = Control<FrameworkElement>("SettingsPanel").Visibility.ToString(),
                importOpen = Control<FrameworkElement>("ImportPanel").Visibility.ToString()
            }));
            throw;
        }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Normal-close preparation verification cannot create a native window.");
        using var reading = OpenSmokeWorkspace(path);
        if (reading.Synchronization.LocalRevision != revision || reading.Synchronization.HasUnpublishedChanges ||
            reading.Synchronization.PublishedRevision != revision ||
            reading.GetRelationships(selected!)?.Person.OfficeNumber != "Latest reviewed Import")
            throw new InvalidOperationException("Normal close did not preserve and actually publish its exact local revision.");
        using var receiver = ApplicationWorkflow.CreateFileBacked();
        if (!receiver.OpenWorkspace(Path.Combine(root, "receiver")) ||
            !receiver.ConfigureSynchronizationFolder(transport, joinExistingWorkspace: true) || !receiver.SyncNow() ||
            receiver.Synchronization.LocalRevision != revision ||
            receiver.GetRelationships(selected!)?.Person.OfficeNumber != "Latest reviewed Import")
            throw new InvalidOperationException("Independent complete-snapshot joining did not receive the exact latest Import.");
        Console.WriteLine("PASS: actual Configure/Sync publishes a genuinely newer Import before normal close, preserves its Import view/owner and independently joins the exact complete version. No Exit anyway or native process-exit acceptance.");
        VerifyConfiguredTransportCloseAndReopen(windowType, root);
    }
}
