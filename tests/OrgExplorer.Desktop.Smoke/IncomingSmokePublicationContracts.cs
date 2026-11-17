using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyIncomingSmokePublication(Type windowType, string root)
    {
        var path = Path.Combine(root, "workspace");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000115",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"owner@example.com","displayName":"Owner","managerEmail":null}]}
                """).Id) || seed.CreatePersonNote("owner@example.com", "Exact note", "# Original writing") is null)
                throw new InvalidOperationException(seed.Status);
        using var peer = new SmokeSnapshotPeer(path);
        var id = SmokeNoteId(path, "owner@example.com", "Exact note");
        using var store = ApplicationWorkflow.CreateFileBacked();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => path), (Action<string>)(_ => { }), new SnapshotClock()], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        if (Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible)
            Invoke("OpenSettingsButton");
        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, model.ReportingRoots.Single(), TreeView.SelectedItemChangedEvent));
        if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Visible)
            Invoke("OpenSettingsButton");
        PumpUntil(() => Control<FrameworkElement>("SettingsPanel").Visibility == Visibility.Visible &&
            Control<Button>("SyncNowButton").IsEnabled);
        var prior = model.Synchronization.LocalRevision;
        peer.Publish(source => source.EditPreferredName("owner@example.com", "Incoming owner") &&
            source.EditPersonNote(id, "Exact note", "# Incoming exact writing"));
        var incoming = peer.Workflow.Synchronization.LocalRevision;
        if (incoming == prior)
            throw new InvalidOperationException("The publication replay requires a genuinely newer independent incoming version.");
        void Synchronize()
        {
            if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Visible ||
                !Control<Button>("SyncNowButton").IsEnabled)
                throw new InvalidOperationException("Incoming publication requires the actual enabled Settings Sync control.");
            Invoke("SyncNowButton");
            PumpUntil(() => !model.IsSynchronizing && !model.Synchronization.HasUnpublishedChanges &&
                model.Synchronization.PublishedRevision == model.Synchronization.LocalRevision);
        }
        try { PublishSmokeWorkspace(path, Synchronize); }
        finally
        {
            Console.WriteLine("INCOMING-SMOKE-PUBLICATION " + JsonSerializer.Serialize(new
            {
                prior, incoming, model.Synchronization,
                selected = model.PeopleNavigation.SelectedEmail,
                person = model.GetRelationships("owner@example.com")?.Person,
                note = model.GetPersonNotes("owner@example.com").Single()
            }));
        }
        Invoke("OpenSettingsButton");
        var notes = Control<UserControl>("PersonNotes");
        var reading = (FlowDocumentScrollViewer)notes.FindName("Reading");
        PumpUntil(() => Control<TextBlock>("ProfileHeading").Text == "Incoming owner" &&
            new TextRange(reading.Document.ContentStart, reading.Document.ContentEnd).Text.Contains("Incoming exact writing", StringComparison.Ordinal));
        using (var independent = OpenSmokeWorkspace(path))
            if (independent.Synchronization.LocalRevision != incoming ||
                independent.Synchronization.PublishedRevision != incoming || independent.Synchronization.HasUnpublishedChanges ||
                independent.GetRelationships("owner@example.com")?.Person.PreferredName != "Incoming owner" ||
                independent.BeginNoteDraft(NoteScope.Person, "owner@example.com", id)?.Markdown != "# Incoming exact writing" ||
                model.PersonNotesView.SelectedNote?.Id != id || model.PersonNotesView.IsEditing)
                throw new InvalidOperationException("Incoming publication did not preserve the exact adopted/published version, data and current Read selection.");
        peer.Publish(source => source.EditPreferredName("owner@example.com", "Second incoming owner") &&
            source.EditPersonNote(id, "Exact note", "# Second incoming exact writing"));
        var secondIncoming = peer.Workflow.Synchronization.LocalRevision;
        Invoke("OpenSettingsButton");
        var rejectedChangedCloseHead = false;
        try { PrepareSmokeNormalClose(path, () => PublishSmokeWorkspace(path, Synchronize)); }
        catch (InvalidOperationException error) when (error.Message ==
            "Normal-close preparation changed or failed to publish the exact independently reopened local revision.")
        {
            rejectedChangedCloseHead = true;
        }
        using (var independent = OpenSmokeWorkspace(path))
            if (!rejectedChangedCloseHead || secondIncoming == incoming ||
                independent.Synchronization.LocalRevision != secondIncoming ||
                independent.Synchronization.PublishedRevision != secondIncoming ||
                independent.Synchronization.HasUnpublishedChanges ||
                independent.GetRelationships("owner@example.com")?.Person.PreferredName != "Second incoming owner" ||
                independent.BeginNoteDraft(NoteScope.Person, "owner@example.com", id)?.Markdown != "# Second incoming exact writing")
                throw new InvalidOperationException("Close-only preparation failed to detect a changed head or lost the correctly adopted/published incoming version.");
        Console.WriteLine("CLOSE-PREPARATION-CHANGED-HEAD " + JsonSerializer.Serialize(new
        {
            incoming, secondIncoming, rejectedChangedCloseHead, model.Synchronization,
            person = model.GetRelationships("owner@example.com")?.Person,
            note = model.GetPersonNotes("owner@example.com").Single()
        }));
        Invoke("OpenSettingsButton");
        PumpUntil(() => Control<TextBlock>("ProfileHeading").Text == "Second incoming owner" &&
            new TextRange(reading.Document.ContentStart, reading.Document.ContentEnd).Text.Contains("Second incoming exact writing", StringComparison.Ordinal));
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Incoming publication verification cannot create a native window.");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
        Console.WriteLine("PASS: general publication accepts real two-store incoming adoption through compiled Sync with exact independent current/published revision/data; close-only preparation still rejects an unexpectedly changed head without losing it. Current Read and normal Closed preserved.");
    }
}
