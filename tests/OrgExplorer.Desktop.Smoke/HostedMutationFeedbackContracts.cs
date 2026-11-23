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
    private static void VerifyHostedMutationFeedbackContracts(Type windowType, string root)
    {
        var failures = new List<string>();
        VerifyRejectedProfileSaveFeedback(windowType, root, failures);
        VerifyImportDiscardFeedback(windowType, root, failures);
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("\n", failures));
        Console.WriteLine("PASS: actual failed profile saving and successful Import discard preserve local fault/owner/data independently of global English outcome text; no native UI.");
    }

    private static void VerifyRejectedProfileSaveFeedback(Type windowType, string root, List<string> failures)
    {
        var workspace = Path.Combine(root, "profile-feedback");
        using var store = ApplicationWorkflow.CreateFileBacked();
        if (!store.OpenWorkspace(workspace) || !store.ApplyImport(store.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000103",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null,"officeNumber":"001A"}]}
            """).Id)) throw new InvalidOperationException(store.Status);
        var before = store.GetRelationships("owner@example.com")!.Person;
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => workspace)], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, model.ReportingRoots.Single(), TreeView.SelectedItemChangedEvent));
        Invoke("EditProfileButton");
        Control<TextBox>("OfficeNumber").Text = "Retained whole draft";
        using (WorkspaceFixtures.LockLocalStore(workspace))
        {
            Invoke("SaveProfileButton");
            PumpUntil(() => model.LocalSave.Phase == LocalSavePhase.Failed && Control<Button>("NoticeRetryButton").IsEnabled);
            var status = Control<TextBlock>("StatusText").Text;
            var local = AutomationProperties.GetItemStatus(Control<FrameworkElement>("OperationalNotice"));
            if (!IsSmokeRejectedSave(status, local))
                failures.Add("Rejected-profile observation: " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    status, local, model.LocalSave, owner = model.ProfileEdit?.Basis.Email,
                    dirty = model.ProfileEdit?.IsDirty, draft = model.ProfileEdit?.Draft.OfficeNumber,
                    importOpen = model.IsImportOpen,
                    form = Control<FrameworkElement>("ProfileForm").Visibility.ToString(),
                    retry = Control<Button>("NoticeRetryButton").IsEnabled
                }));
            using var unchanged = ApplicationWorkflow.CreateFileBacked();
            if (!unchanged.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly) ||
                unchanged.GetRelationships(before.Email)!.Person != before ||
                model.ProfileEdit?.Draft.OfficeNumber != "Retained whole draft" ||
                model.ProfileEdit.Basis != before || model.IsImportOpen)
                throw new InvalidOperationException("The failed-profile observation lost exact owner/draft or changed committed data.");
        }
        Invoke("NoticeRetryButton");
        PumpUntil(() => model.CanEditLocalWorkspace && Control<Button>("CancelProfileButton").IsEnabled);
        Invoke("CancelProfileButton");
        if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "profile-feedback-snapshots")) || !model.SyncNow())
            throw new InvalidOperationException(model.Status);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The feedback contract cannot create a native window.");
        window.Close();
        PumpUntil(() => closed);
    }

    private static void VerifyImportDiscardFeedback(Type windowType, string root, List<string> failures)
    {
        var workspace = Path.Combine(root, "import-feedback");
        Directory.CreateDirectory(workspace);
        using var store = ApplicationWorkflow.CreateFileBacked();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => workspace)], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        Invoke("OpenSettingsButton");
        Invoke("OpenImportButton");
        var import = Path.Combine(root, "failed-import.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000104",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"imported@example.com","displayName":"Never partially accepted","managerEmail":null}]}
            """);
        Control<TextBox>("ImportPath").Text = import;
        Invoke("PreviewButton");
        PumpUntil(() => Control<Button>("ApplyButton").IsEnabled);
        using (WorkspaceFixtures.LockLocalStore(workspace))
        {
            Invoke("ApplyButton");
            PumpUntil(() => model.HasFailedImport && Control<Button>("DiscardImportButton").IsEnabled);
            Invoke("DiscardImportButton");
            PumpUntil(() => !model.HasFailedImport && Control<Button>("DiscardImportButton").Visibility == Visibility.Collapsed);
            var status = Control<TextBlock>("StatusText").Text;
            if (!IsSmokeImportDiscardComplete(Control<Button>("DiscardImportButton").Visibility == Visibility.Visible,
                Control<Button>("ApplyButton").IsEnabled))
                failures.Add("Discarded-import observation: " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    status, model.HasFailedImport, model.LocalSave, model.IsWorkspaceCurrent,
                    discard = Control<Button>("DiscardImportButton").Visibility.ToString(),
                    apply = Control<Button>("ApplyButton").IsEnabled
                }));
            if (model.LocalSave.Phase != LocalSavePhase.Failed || model.CanSafelyClose || model.People.Count != 0)
                throw new InvalidOperationException("Import discard cleared the independent local fault or accepted partial data.");
            using var unchanged = OpenSmokeWorkspace(workspace);
            if (unchanged.People.Count != 0)
                throw new InvalidOperationException("Discarded failed Import left independently reopened partial People.");
        }
        Invoke("NoticeRetryButton");
        PumpUntil(() => model.CanSafelyClose && model.CanEditLocalWorkspace);
        if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "import-feedback-snapshots")) || !model.SyncNow())
            throw new InvalidOperationException(model.Status);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
    }
}
