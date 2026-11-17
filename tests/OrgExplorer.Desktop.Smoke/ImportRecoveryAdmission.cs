using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyImportRecoveryAdmission(Type windowType, string root)
    {
        var path = Path.Combine(root, "ir");
        using var store = ApplicationWorkflow.CreateFileBacked();
        if (!store.OpenWorkspace(path) || !store.ApplyImport(store.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000109",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null,"jobTitle":"Original title"}]}
            """).Id)) throw new InvalidOperationException(store.Status);
        var note = store.CreatePersonNote("owner@example.com", "Exact independent note", "Original independent writing")!;
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => path), (Action<string>)(_ => { })], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void People() => LogicalDescendants(Control<FrameworkElement>("ImportPanel")).OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "BackToPeople")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        Invoke("OpenSettingsButton");
        Invoke("OpenImportButton");
        var import = Path.Combine(root, "retry.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000110",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"owner@example.com","jobTitle":"Reviewed retry title"}]}
            """);
        Control<TextBox>("ImportPath").Text = import;
        Invoke("PreviewButton");
        PumpUntil(() => Control<Button>("ApplyButton").IsEnabled);
        var revision = model.Synchronization.LocalRevision;
        using (WorkspaceFixtures.LockLocalStore(path))
        {
            Invoke("ApplyButton");
            PumpUntil(() => model.HasFailedImport && model.LocalSave.Phase == LocalSavePhase.Failed);
            People();
        }
        Invoke("NoticeRetryButton");
        PumpUntil(() => model.CanEditLocalWorkspace && model.CanEnterImport);
        var error = model.LocalSaveError;
        if (error is null || !model.HasFailedImport || model.CanSafelyClose)
            throw new InvalidOperationException("Import recovery setup silently discarded its original failure.");
        Invoke("OpenSettingsButton");
        Invoke("OpenImportButton");
        if (!model.IsImportOpen || model.LocalSaveError != error)
            throw new InvalidOperationException("Actual Import's own retained failure blocked its scoped recovery: " +
                $"canEnter={model.CanEnterImport}; retained={model.HasFailedImport}; status={Control<TextBlock>("StatusText").Text}.");
        Invoke("PreviewButton");
        PumpUntil(() => Control<Button>("ApplyButton").IsEnabled);
        using (var unchanged = OpenSmokeWorkspace(path))
            if (model.LocalSaveError != error || model.Synchronization.LocalRevision != revision ||
                unchanged.GetRelationships(note.OwnerEmail)?.Person.JobTitle != "Original title")
                throw new InvalidOperationException("Reentered preview cleared the failure or committed unreviewed data.");
        Invoke("ApplyButton");
        PumpUntil(() => model.LocalSaveError is null && !model.HasFailedImport && model.CanSafelyClose);
        People();
        using (var reopened = OpenSmokeWorkspace(path))
            if (reopened.GetRelationships(note.OwnerEmail)?.Person.JobTitle != "Reviewed retry title" ||
                reopened.BeginNoteDraft(NoteScope.Person, note.OwnerEmail, note.Id)?.Markdown != note.Markdown)
                throw new InvalidOperationException("Explicit compiled Import retry lost exact reviewed profile/note data.");
        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, model.ReportingRoots.Single(), TreeView.SelectedItemChangedEvent));
        var surface = Control<UserControl>("PersonNotes");
        ((TabControl)surface.FindName("Views")).SelectedItem = surface.FindName("EditTab");
        ((TextBox)surface.FindName("NoteTitle")).Text = "";
        PumpUntil(() => model.GetRetainedNoteDraft(NoteScope.Person, note.Id)?.Error is not null);
        Invoke("OpenSettingsButton");
        Invoke("OpenImportButton");
        if (model.IsImportOpen || model.CanEnterImport || model.GetRetainedNoteDraft(NoteScope.Person, note.Id)?.Title != "")
            throw new InvalidOperationException("Import recovery admission also bypassed an independent note failure.");
        ((Button)surface.FindName("DiscardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "irt")) || !model.SyncNow())
            throw new InvalidOperationException(model.Status);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Import admission verification cannot create a native window.");
        model.Dispose();
        window.Close();
        Console.WriteLine("PASS: actual BAML failed Apply/reconcile/People/reenter/preview/Apply preserves Import's own error until exact successful retry; independent invalid note still blocks protected Import entry.");
    }
}
