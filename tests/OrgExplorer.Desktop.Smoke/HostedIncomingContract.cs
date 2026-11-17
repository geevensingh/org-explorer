using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyHostedIncomingContract(Type windowType, string root)
    {
        var workspace = Path.Combine(root, "incoming-workspace");
        using var store = ApplicationWorkflow.CreateFileBacked();
        if (!store.OpenWorkspace(workspace) ||
            !store.ApplyImport(store.PreviewImport("""
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000105",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"owner@example.com","displayName":"Owner","managerEmail":null}]}
                """).Id)) throw new InvalidOperationException(store.Status);
        using var peer = new SmokeSnapshotPeer(workspace);
        var clock = new SnapshotClock();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => workspace), (Action<string>)(_ => { }), clock], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, model.ReportingRoots.Single(), TreeView.SelectedItemChangedEvent));
        peer.Publish(source => source.EditPreferredName("owner@example.com", "Complete incoming continuation"));
        clock.Advance(TimeSpan.FromSeconds(59));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (Control<TextBlock>("ProfileHeading").Text != "Owner" || model.IsSynchronizing)
            throw new InvalidOperationException("The scheduled check ran before its sixty-second cadence.");
        clock.Advance(TimeSpan.FromSeconds(1));
        PumpUntil(() => Control<TextBlock>("ProfileHeading").Text == "Complete incoming continuation" &&
            !model.IsSynchronizing);
        peer.Publish(source => source.EditPreferredName("owner@example.com", "Manually received continuation"));
        Invoke("OpenSettingsButton");
        Invoke("SyncNowButton");
        PumpUntil(() => Control<TextBlock>("ProfileHeading").Text == "Manually received continuation" &&
            Control<Button>("SyncNowButton").IsEnabled);
        using (var independent = OpenSmokeWorkspace(workspace))
            if (independent.GetRelationships("owner@example.com")?.Person.PreferredName != "Manually received continuation" ||
                independent.Synchronization.LocalRevision != peer.Workflow.Synchronization.LocalRevision ||
                model.PeopleNavigation.SelectedEmail != "owner@example.com" || model.LocalSaveError is not null)
                throw new InvalidOperationException("Scheduled/manual snapshot reception lost the exact owner, complete head or independent local data.");
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The incoming contract cannot create a native window.");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
        Console.WriteLine("PASS: actual compiled incoming continuation waits for its sixty-second synchronization cadence; actual Sync now receives the next complete version immediately, preserving exact owner/head and independent reopened data. Save/three-second clocks remain real; no native UI.");
    }
}
