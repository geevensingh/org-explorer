using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyManagerChoiceDemand(Type windowType, string root)
    {
        var path = Path.Combine(root, "mc");
        using var store = ApplicationWorkflow.CreateFileBacked();
        if (!store.OpenWorkspace(path) || !store.ApplyImport(store.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000108",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":"manager@example.com"},
             {"email":"manager@example.com","displayName":"Manager","managerEmail":null},
             {"email":"report@example.com","displayName":"Report","managerEmail":"owner@example.com"}]}
            """).Id)) throw new InvalidOperationException(store.Status);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => path), (Action<string>)(_ => { })], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var manager = model.ReportingRoots.Single();
        var owner = manager.Reports.Single();
        void Select(ReportingNode node) => Control<TreeView>("PeopleList").RaiseEvent(
            new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
        var choices = Control<ComboBox>("ManagerChoices");
        var rebuilt = 0;
        var descriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox));
        EventHandler changed = (_, _) => rebuilt++;
        descriptor.AddValueChanged(choices, changed);
        try { Select(owner); Select(manager); Select(owner); }
        finally { descriptor.RemoveValueChanged(choices, changed); }
        if (rebuilt != 0 || choices.Items.Count != 0)
            throw new InvalidOperationException($"Ordinary compiled reading rebuilt the hidden whole-population Manager picker: assignments={rebuilt}; choices={choices.Items.Count}.");
        Invoke("EditProfileButton");
        if (choices.Items.Count != model.People.Count || model.ProfileEdit?.Basis.ManagerEmail != "manager@example.com" ||
            choices.SelectedIndex <= 0)
            throw new InvalidOperationException("On-demand actual profile editing lost complete candidates or its exact current Manager.");
        choices.SelectedIndex = 0;
        if (model.ProfileEdit is not { } edit || edit.Draft.ManagerEmail is not null || !edit.IsDirty)
            throw new InvalidOperationException("The on-demand actual Manager choice did not stage explicit no-Manager.");
        var basis = edit.Basis;
        Select(manager);
        Select(owner);
        if (!ReferenceEquals(model.ProfileEdit?.Basis, basis) || model.ProfileEdit?.Draft.ManagerEmail is not null ||
            choices.SelectedIndex != 0 || choices.Items.Count != model.People.Count)
            throw new InvalidOperationException("Retained owner restore changed its immutable base, staged Manager or full current choices.");
        var source = choices.ItemsSource;
        Invoke("CancelProfileButton");
        using (var independent = OpenSmokeWorkspace(path))
            if (independent.GetRelationships("owner@example.com")?.Person.ManagerEmail != "manager@example.com" ||
                !ReferenceEquals(choices.ItemsSource, source))
                throw new InvalidOperationException("Manager-choice cancellation changed independent saved relationships or unnecessarily rebound the hidden picker.");
        Invoke("EditProfileButton");
        if (choices.Items.Count != model.People.Count || choices.SelectedIndex <= 0 ||
            model.ProfileEdit?.Basis.ManagerEmail != "manager@example.com")
            throw new InvalidOperationException("Reentered editing reused a stale staged Manager instead of the full current base.");
        Invoke("CancelProfileButton");
        if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "mct")) || !model.SyncNow())
            throw new InvalidOperationException(model.Status);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Manager-choice verification cannot create a native window.");
        model.Dispose();
        window.Close();
        Console.WriteLine("PASS: actual compiled ordinary reading avoids whole-population Manager-picker construction; actual Edit still has every candidate/current base, real choice staging and independently nonmutating Cancel.");
    }
}
