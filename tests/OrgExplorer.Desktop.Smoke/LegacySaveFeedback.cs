using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyLegacySaveFeedback(Type windowType, string settings, Func<Window, string?> chooseFolder)
    {
        var path = Path.Combine(settings, "legacy-rejected-leave-save");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"95000000-0000-4000-8000-000000000091",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com"},
                 {"email":"casey@example.com","displayName":"Casey","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Could not prepare the real legacy rejection fixture: " + seed.Status);
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
            using (File.Open(Path.Combine(path, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // Replay the real leave handler's save/feedback continuation without opening its native modal dialog.
                async Task RejectLeaveSave()
                {
                    var saving = (Task<bool>)windowType.GetMethod("SaveProfileSessionAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                    if (await saving) throw new InvalidOperationException("A locked legacy leave-save unexpectedly committed.");
                    windowType.GetMethod("ShowNavigationBlocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, ["Navigation blocked: " + workflow.Status, false]);
                }
                var rejected = RejectLeaveSave();
                PumpUntil(() => rejected.IsCompleted);
                rejected.GetAwaiter().GetResult();
                var idle = false;
                window.Dispatcher.BeginInvoke(() => idle = true, DispatcherPriority.ApplicationIdle);
                PumpUntil(() => idle);
                var status = Control<TextBlock>("StatusText").Text;
                if (!status.StartsWith("Navigation blocked: Save failed;", StringComparison.Ordinal) ||
                    !status.Contains("NOT saved", StringComparison.Ordinal) ||
                    !Control<TextBlock>("WorkflowActivity").Text.StartsWith("Pending local edits", StringComparison.Ordinal) ||
                    !Control<Button>("SaveProfileButton").IsEnabled ||
                    workflow.ProfileEdit?.Basis.Email != "avery@example.com" ||
                    workflow.ProfileEdit.Draft.OfficeNumber != "Retained legacy room" ||
                    workflow.LocalSave.Phase != LocalSavePhase.Ready || !workflow.CanEditLocalWorkspace ||
                    workflow.CanNavigatePeople || workflow.CanSafelyClose)
                    throw new InvalidOperationException("Compiled legacy leave-save feedback was overwritten or its owner/Save policy changed: " +
                        $"status='{status}'; activity='{Control<TextBlock>("WorkflowActivity").Text}'; " +
                        $"saveEnabled={Control<Button>("SaveProfileButton").IsEnabled}; phase={workflow.LocalSave.Phase}.");
            }
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The legacy feedback replay created a native window.");
            Console.WriteLine("PASS: invisible compiled legacy rejected leave-save retains its navigation feedback, enabled Save, exact owner/draft and legacy guards after dispatcher refresh settlement; no native modal or pixels claimed.");
        }
        finally
        {
            workflow.CancelProfileEdit();
            window.Close();
        }
    }
}
