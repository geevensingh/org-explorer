using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyLegacySaveFeedback(Type windowType, string settings, Func<Window, string?> chooseFolder)
    {
        // Keep the required companion selection, exercising only the normal SQLite save path.
        var path = Path.Combine(settings, "sqlite-rejected-leave-save");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"95000000-0000-4000-8000-000000000091",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com"},
                 {"email":"casey@example.com","displayName":"Casey","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Could not prepare the real SQLite rejection fixture: " + seed.Status);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        try
        {
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ChooseCompiledWorkspace(window, path);
            PumpUntil(() => workflow.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
            var person = workflow.ReportingRoots.Single().Reports.Single();
            Control<TreeView>("PeopleList").RaiseEvent(
                new RoutedPropertyChangedEventArgs<object>(null!, person, TreeView.SelectedItemChangedEvent));
            Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<TextBox>("OfficeNumber").Text = "Retained legacy room";
            using (WorkspaceFixtures.LockLocalStore(path))
            {
                var save = Control<Button>("SaveProfileButton");
                if (!save.IsEnabled)
                    throw new InvalidOperationException("The compiled profile Save action is unavailable.");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => workflow.LocalSave.Phase == LocalSavePhase.Failed &&
                    Control<Button>("OpenWorkspaceButton").IsEnabled);
                var idle = false;
                window.Dispatcher.BeginInvoke(() => idle = true, DispatcherPriority.ApplicationIdle);
                PumpUntil(() => idle);
                var status = Control<TextBlock>("StatusText").Text;
                if (!status.Contains("NOT saved", StringComparison.Ordinal) ||
                    !Control<TextBlock>("WorkflowActivity").Text.Contains("browsing remains available", StringComparison.Ordinal) ||
                    workflow.ProfileEdit?.Basis.Email != "avery@example.com" ||
                    workflow.ProfileEdit.Draft.OfficeNumber != "Retained legacy room" ||
                    !workflow.LocalSave.EditsPaused || workflow.CanEditLocalWorkspace ||
                    !workflow.CanNavigatePeople || workflow.CanSafelyClose)
                    throw new InvalidOperationException("Compiled rejected SQLite leave-save feedback was overwritten or its owner/edit policy changed: " +
                        $"status='{status}'; activity='{Control<TextBlock>("WorkflowActivity").Text}'; " +
                        $"saveEnabled={Control<Button>("SaveProfileButton").IsEnabled}; phase={workflow.LocalSave.Phase}.");
            }
            if (!workflow.ReconcileLocalSaves())
                throw new InvalidOperationException(workflow.Status);
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The legacy feedback replay created a native window.");
            Console.WriteLine("PASS: invisible compiled SQLite rejected leave-save retains feedback and exact owner/draft, pauses edits but not browsing, and requires reconciliation; no native modal or pixels claimed.");
        }
        finally
        {
            workflow.CancelProfileEdit();
            ClosePublishedCompiledWindow(window);
        }
    }
}
