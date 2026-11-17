using System.ComponentModel;
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
    private static void VerifyIndependentNoteLeaveContract(Type windowType, string root)
    {
        var path = Path.Combine(root, "il");
        var destination = Path.Combine(root, "other");
        using var store = ApplicationWorkflow.CreateFileBacked();
        if (!store.OpenWorkspace(path) || !store.ApplyImport(store.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000107",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null,"officeNumber":"001A"}]}
            """).Id)) throw new InvalidOperationException(store.Status);
        var note = store.CreatePersonNote("owner@example.com", "Exact saved title", "Exact committed writing")!;
        var choice = path;
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => choice), (Action<string>)(_ => { })], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, model.ReportingRoots.Single(), TreeView.SelectedItemChangedEvent));
        var surface = Control<UserControl>("PersonNotes");
        T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        Note<TabControl>("Views").SelectedItem = Note<TabItem>("EditTab");
        Note<TextBox>("NoteTitle").Text = "";
        PumpUntil(() => model.GetRetainedNoteDraft(NoteScope.Person, note.Id)?.Error is not null);
        var retained = model.GetRetainedNoteDraft(NoteScope.Person, note.Id)!;
        Invoke("EditProfileButton");
        Control<TextBox>("OfficeNumber").Text = "Discard this profile only";
        if (!model.TryLeaveProfileEdit(ProfileLeaveDecision.Discard) || model.ProfileEdit is not null)
            throw new InvalidOperationException("The real profile-only Discard was not accepted.");
        var activity = new List<string>();
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler changed = (_, _) => activity.Add(Control<TextBlock>("WorkflowActivity").Text);
        descriptor.AddValueChanged(Control<TextBlock>("WorkflowActivity"), changed);
        choice = destination;
        try
        {
            Invoke("OpenSettingsButton");
            if (!Control<Button>("OpenWorkspaceButton").IsEnabled)
                throw new InvalidOperationException("The actual public Workspace picker action was not enabled.");
            Invoke("OpenWorkspaceButton");
            PumpUntil(() => Control<Button>("OpenWorkspaceButton").IsEnabled);
        }
        finally { descriptor.RemoveValueChanged(Control<TextBlock>("WorkflowActivity"), changed); }
        var feedback = Control<TextBlock>("StatusText").Text;
        var blockedBeforeWork = activity.Count == 0;
        if (!blockedBeforeWork || model.LocalSaveError != retained.Error ||
            Control<FrameworkElement>("OperationalNotice").Visibility != Visibility.Visible ||
            model.GetRetainedNoteDraft(NoteScope.Person, note.Id)?.Title != retained.Title ||
            model.GetRetainedNoteDraft(NoteScope.Person, note.Id)?.Markdown != retained.Markdown ||
            model.GetRetainedNoteDraft(NoteScope.Person, note.Id)?.Error != retained.Error ||
            model.PeopleNavigation.SelectedEmail != "owner@example.com" || Directory.Exists(destination))
            throw new InvalidOperationException("Profile-only Discard bypassed independent-note protection at the actual shared leave guard: " +
                JsonSerializer.Serialize(new { activity, feedback, model.LocalSaveError,
                    owner = model.PeopleNavigation.SelectedEmail, model.CanSafelyClose }));
        using (var independent = OpenSmokeWorkspace(path))
            if (independent.BeginNoteDraft(NoteScope.Person, note.OwnerEmail, note.Id)?.Title != note.Title ||
                independent.GetRelationships(note.OwnerEmail)?.Person.OfficeNumber != "001A")
                throw new InvalidOperationException("Independent-note blocking changed committed profile/note data.");
        Note<Button>("DiscardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "ilt")) || !model.SyncNow())
            throw new InvalidOperationException(model.Status);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Independent-note leave verification cannot create a native window.");
        model.Dispose();
        window.Close();
        Console.WriteLine("PASS: actual BAML shared mutation/replacement guard rejects after real profile-only Discard while exact independent invalid note survives; no admitted mutation work, owner/data loss or native dialog.");
    }
}
