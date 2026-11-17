using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyPreferredNameRetention(Type windowType, string settings,
        Func<Window, string?> chooseFolder)
    {
        VerifyPreferredNameReturn(windowType, settings, chooseFolder, cancel: true, newerWhilePending: true, discardNewer: true);
        VerifyPreferredNameReturn(windowType, settings, chooseFolder, cancel: false, newerWhilePending: false);
        VerifyPreferredNameReturn(windowType, settings, chooseFolder, cancel: true, newerWhilePending: true);
        VerifyPreferredNameReturn(windowType, settings, chooseFolder, cancel: false, newerWhilePending: true);
    }

    private static void VerifyPreferredNameReturn(Type windowType, string settings,
        Func<Window, string?> chooseFolder, bool cancel, bool newerWhilePending, bool discardNewer = false)
    {
        var path = Path.Combine(settings, $"preferred-name-return-{cancel}-{newerWhilePending}-{discardNewer}");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"95000000-0000-4000-8000-000000000092",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com"},
                 {"email":"casey@example.com","displayName":"Casey","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Preferred-name fixture failed: " + seed.Status);
        using var publisher = new HeldNotePublisher();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(fileObserver: publisher), chooseFolder], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var tree = Control<TreeView>("PeopleList");
        var workflow = (WorkspaceViewModel)tree.DataContext;
        var editor = Control<TextBox>("PreferredName");
        var panel = Control<FrameworkElement>("PreferredNameEditorPanel");
        void Invoke(string name)
        {
            var button = Control<Button>(name);
            if (!button.IsEnabled) throw new InvalidOperationException(name + " is not enabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void Select(string email)
        {
            if (!tree.IsEnabled || !workflow.CanNavigatePeople)
                throw new InvalidOperationException("Ordinary Person browsing is disabled during the admitted save.");
            var node = workflow.ReportingRoots.SelectMany(root => new[] { root }.Concat(root.Reports))
                .Single(item => item.Person.Email == email);
            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
            if (Control<TextBlock>("Email").Text != email)
                throw new InvalidOperationException("The compiled selection did not display " + email);
        }
        try
        {
            Invoke("OpenSettingsButton");
            ChooseCompiledWorkspace(window, path);
            PumpUntil(() => workflow.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
            Select("avery@example.com");
            Invoke("EditPreferredNameButton");
            editor.Text = "First admitted name";
            publisher.HoldNextProfile = true;
            Invoke("SavePreferredNameButton");
            PumpUntil(() => publisher.Entered.IsSet);
            if (cancel)
            {
                Invoke("CancelPreferredNameButton");
                if (panel.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Cancel did not immediately close the admitted entry.");
            }
            Select("casey@example.com");
            Select("avery@example.com");
            if (panel.Visibility != Visibility.Visible || !editor.IsEnabled || editor.Text != "First admitted name")
                throw new InvalidOperationException("Returning before completion did not restore an enabled retained entry.");
            if (newerWhilePending)
            {
                editor.Text = "Newer restored writing";
                if (workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName)?.Value != "Newer restored writing")
                    throw new InvalidOperationException("Typing did not retain the restored owner's exact newer writing.");
            }
            publisher.Release.Set();
            PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
            using (var reading = OpenSmokeWorkspace(path))
                if (reading.GetRelationships("avery@example.com")!.Person.PreferredName != "First admitted name" ||
                    reading.GetRelationships("casey@example.com")!.Person.PreferredName is not null)
                    throw new InvalidOperationException("The admitted save did not durably affect only its actual owner.");
            if (newerWhilePending)
            {
                if (panel.Visibility != Visibility.Visible || editor.Text != "Newer restored writing" ||
                    workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not
                        { Value: "Newer restored writing", Basis.PreferredName: "First admitted name", Error: null } ||
                    workflow.CanSafelyClose)
                    throw new InvalidOperationException("An older save's deferred Cancel discarded the restored owner's newer writing.");
            }
            else if (panel.Visibility != Visibility.Visible || editor.Text != "First admitted name" || !editor.IsEnabled)
                throw new InvalidOperationException("Save completion replaced or disabled the restored owner entry.");
            if (discardNewer)
            {
                Invoke("CancelPreferredNameButton");
            }
            else
            {
                if (!newerWhilePending) editor.Text = "Newer explicit name";
                Invoke("SavePreferredNameButton");
                PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
            }
            var expected = discardNewer ? "First admitted name" :
                newerWhilePending ? "Newer restored writing" : "Newer explicit name";
            using (var reading = OpenSmokeWorkspace(path))
                if (reading.GetRelationships("avery@example.com")!.Person.PreferredName != expected ||
                    reading.GetRelationships("casey@example.com")!.Person.PreferredName is not null ||
                    panel.Visibility != Visibility.Collapsed || !workflow.CanSafelyClose)
                    throw new InvalidOperationException(discardNewer
                        ? "The restored entry's own Cancel did not discard only its newer writing: " + workflow.Status
                        : "The restored owner's next Save falsely rejected its own accepted base: " + workflow.LocalSaveError);
            if (!cancel && !newerWhilePending)
            {
                Invoke("EditPreferredNameButton");
                editor.Text = "Stale rejected writing";
                using (var external = OpenSmokeWorkspace(path))
                    if (!external.EditPreferredName("avery@example.com", "Genuinely external name"))
                        throw new InvalidOperationException(external.Status);
                Invoke("SavePreferredNameButton");
                PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
                if (panel.Visibility != Visibility.Visible || editor.Text != "Stale rejected writing" ||
                    workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not
                        { Value: "Stale rejected writing", Basis.PreferredName: "Newer explicit name", Error: { } error } ||
                    !error.Contains("changed externally", StringComparison.Ordinal) ||
                    Control<TextBlock>("PreferredNameError").Visibility != Visibility.Visible || workflow.CanSafelyClose)
                    throw new InvalidOperationException("A genuinely changed external base was accepted or its rejected writing/error was lost.");
                using (var reading = OpenSmokeWorkspace(path))
                    if (reading.GetRelationships("avery@example.com")!.Person.PreferredName != "Genuinely external name")
                        throw new InvalidOperationException("A rejected stale entry overwrote the genuine external name.");
                Invoke("CancelPreferredNameButton");
                if (panel.Visibility != Visibility.Collapsed || !workflow.CanSafelyClose)
                    throw new InvalidOperationException("Explicit Cancel did not release the rejected stale entry.");
                Console.WriteLine("PASS: compiled Save still rejects a genuinely changed external Preferred-name base, retains exact writing/error and preserves independently reopened external data.");
            }
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Preferred-name retention created a native window.");
            Console.WriteLine($"PASS: compiled pending Preferred-name A-B-A return; earlierCancel={cancel}; " +
                $"newerWritingWhilePending={newerWhilePending}; ownAction={(discardNewer ? "Cancel" : "Save")}; " +
                "exact accepted base, retained text and independently reopened owner values.");
        }
        finally
        {
            publisher.Release.Set();
            PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
            if (panel.Visibility == Visibility.Visible) Invoke("CancelPreferredNameButton");
            if (workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is { } retained)
                workflow.DiscardPersonTextDraft(retained.Basis, PersonTextField.PreferredName);
            ClosePublishedCompiledWindow(window);
        }
    }
}
