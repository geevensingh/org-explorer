using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyLocalRecoveryCleanup(Type windowType, string root)
    {
        var before = Application.Current.Windows.Cast<Window>().ToHashSet();
        VerifyCompiledLocalSaveRecovery(windowType, root);
        var remaining = Application.Current.Windows.Cast<Window>().Where(window => !before.Contains(window)).ToArray();
        if (remaining.Length != 0)
            throw new InvalidOperationException("The owning compiled recovery fixture returned with a live normal-close intent: " +
                System.Text.Json.JsonSerializer.Serialize(remaining.Select(window => new
                {
                    decision = ((FrameworkElement)window.FindName("ExitDecision")).Visibility.ToString(),
                    exit = ((WorkspaceViewModel)((TreeView)window.FindName("PeopleList")).DataContext).ExitStatus
                })));
        Console.WriteLine("PASS: actual compiled recovery fixture joins its own latest publication and normal Closed before returning; no live close probe remains for root cleanup.");
    }

    private static void VerifyRecoveredCopyContract(Type windowType, string root)
    {
        var path = Path.Combine(root, "rc");
        using var store = ApplicationWorkflow.CreateFileBacked();
        if (!store.OpenWorkspace(path) || !store.ApplyImport(store.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000106",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null,"officeNumber":"001A"}]}
            """).Id)) throw new InvalidOperationException(store.Status);
        var copied = "";
        var failCopy = true;
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => path), (Action<string>)(value =>
            {
                if (failCopy) throw new System.Runtime.InteropServices.ExternalException("Controlled clipboard refusal.");
                copied = value;
            })], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, model.ReportingRoots.Single(), TreeView.SelectedItemChangedEvent));
        Invoke("EditProfileButton");
        Control<TextBox>("OfficeNumber").Text = "Retained recovery draft";
        using (WorkspaceFixtures.LockLocalStore(path))
        {
            Invoke("SaveProfileButton");
            PumpUntil(() => model.LocalSave.Phase == LocalSavePhase.Failed && Control<Button>("NoticeRetryButton").IsEnabled);
        }
        Invoke("NoticeRetryButton");
        PumpUntil(() => model.CanEditLocalWorkspace && Control<Button>("CancelProfileButton").IsEnabled);
        Invoke("CancelProfileButton");
        var before = WorkspaceFixtures.Bytes(path);
        var copy = (IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(Control<TextBlock>("Email"))!
            .GetPattern(PatternInterface.Invoke)!;
        copy.Invoke();
        PumpUntil(() => Control<TextBlock>("NoticeSummary").Text.Contains("Copy failed", StringComparison.Ordinal));
        if (!Control<TextBlock>("NoticeSummary").Text.Contains("Copy failed", StringComparison.Ordinal) || copied != "")
            throw new InvalidOperationException("The actual recovered clipboard refusal was not visible.");
        failCopy = false;
        copy.Invoke();
        PumpUntil(() => copied == "owner@example.com");
        PumpUntil(() => Control<TextBlock>("ProfileCopyFeedback").Visibility == Visibility.Collapsed);
        AssertWorkspaceBytes(path, before);
        if (Control<FrameworkElement>("OperationalNotice").Visibility == Visibility.Visible ||
            copied != "owner@example.com" || model.LocalSaveError is not null || !model.CanSafelyClose)
            throw new InvalidOperationException("Successful clipboard retry did not restore quiet recovered reading: " +
                $"summary={Control<TextBlock>("NoticeSummary").Text}; local={model.LocalSave}; " +
                $"status={model.Status}; owner={model.PeopleNavigation.SelectedEmail}; exactCopy={copied == "owner@example.com"}.");
        if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "rct")) || !model.SyncNow())
            throw new InvalidOperationException(model.Status);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Recovered-copy verification cannot create a native window.");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
        Console.WriteLine("PASS: actual BAML copy failure/retry after real SQLite recovery restores exact quiet healthy reading without changing bytes or actual clipboard/native UI.");
    }
}
