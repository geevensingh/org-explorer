using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyCompiledWorkspaceExit(Type windowType, string settings)
    {
        var path = Path.Combine(settings, "compiled-exit");
        SeedRecoveryWorkspace(path);
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                !seed.ConfigureSynchronizationFolder(Path.Combine(settings, "compiled-exit-sync")) || !seed.SyncNow())
                throw new InvalidOperationException(seed.Status);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = false;
        var publisher = new AutomaticSnapshotPublicationGate(() => hold, entered.Set,
            () => release.Wait(TimeSpan.FromSeconds(10)));
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(filePublisher: publisher), (Func<Window, string?>)(_ => CompiledWorkspaceChoices.Dequeue())], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ChooseCompiledWorkspace(window, path);
            PumpUntil(() => model.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
            var manager = model.ReportingRoots.Single();
            Control<TreeView>("PeopleList").RaiseEvent(
                new RoutedPropertyChangedEventArgs<object>(null!, manager, TreeView.SelectedItemChangedEvent));
            Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<TextBox>("Department").Text = "Unsubmitted exit department";
            var draft = model.ProfileEdit;
            var selection = model.PeopleNavigation;
            if (window.FindName("ExitDecision") is not FrameworkElement decision ||
                UIElementAutomationPeer.CreatePeerForElement(decision)?.GetAutomationId() != "ExitDecision" ||
                window.FindName("CancelExitButton") is not Button cancel)
                throw new InvalidOperationException("The compiled window has no nonmodal exit decision.");
            window.Close();
            if (window.FindName("ExitDecision") is not FrameworkElement { Visibility: Visibility.Visible } ||
                closed)
                throw new InvalidOperationException("Normal close must expose a nonmodal Exit anyway / Cancel decision, not a secondary profile dialog or silent veto.");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Control<FrameworkElement>("ExitDecision").Visibility == Visibility.Collapsed);
            if (closed || model.ProfileEdit != draft || model.PeopleNavigation != selection ||
                Control<TextBox>("Department").Text != "Unsubmitted exit department" || window.IsVisible ||
                new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Cancel changed the exact profile/selection or created a native window.");
            Control<Button>("CancelProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var notes = Control<UserControl>("TeamNotes");
            ((Button)notes.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBox)notes.FindName("MarkdownEditor")).Text = "Before held exit publication";
            PumpUntil(() => model.CanSafelyClose);
            hold = true;
            window.Close();
            PumpUntil(() => entered.IsSet);
            // Repeated close requests share this intent; Cancel invalidates all of them.
            window.Close();
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBox)notes.FindName("MarkdownEditor")).Text = "Newer writing after canceled exit";
            PumpUntil(() => model.CanSafelyClose);
            hold = false;
            release.Set();
            PumpUntil(() => !model.IsSynchronizing);
            if (closed || Control<FrameworkElement>("ExitDecision").Visibility != Visibility.Collapsed ||
                !model.Synchronization.HasUnpublishedChanges ||
                ((TextBox)notes.FindName("MarkdownEditor")).Text != "Newer writing after canceled exit")
                throw new InvalidOperationException("A repeated/canceled close intent's old publication closed the window or acknowledged newer writing.");
            window.Close();
            PumpUntil(() => closed);
            using var reading = ApplicationWorkflow.CreateFileBacked();
            if (!reading.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                reading.GetRelationships("morgan@example.com")!.Person.Department is not null ||
                reading.Synchronization.HasUnpublishedChanges)
                throw new InvalidOperationException("Normal exit implicitly saved an explicit draft or failed latest publication.");
            Console.WriteLine("PASS: invisible compiled exit decision, exact Cancel, explicit profile nonmutation and latest-publication normal close; no native interaction claimed.");
        }
        finally
        {
            hold = false;
            release.Set();
            if (!closed)
            {
                model.CancelProfileEdit();
                window.Close();
                PumpUntil(() => closed);
            }
        }
    }
}
