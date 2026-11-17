using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyNoteReadDemand(Type windowType, string root)
    {
        var path = Path.Combine(root, "nr");
        using var store = ApplicationWorkflow.CreateFileBacked();
        if (!store.OpenWorkspace(path) || !store.ApplyImport(store.PreviewImport("""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000112",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":"manager@example.com"},
             {"email":"manager@example.com","displayName":"Manager","managerEmail":null}]}
            """).Id)) throw new InvalidOperationException(store.Status);
        var ids = new Dictionary<(NoteScope, string), Guid>();
        foreach (var scope in Enum.GetValues<NoteScope>())
            foreach (var owner in new[] { "owner@example.com", "manager@example.com" })
                ids[(scope, owner)] = scope == NoteScope.Person
                    ? store.CreatePersonNote(owner, scope + " " + owner, "# Exact " + scope + " body " + owner)!.Id
                    : store.CreateTeamNote(owner, scope + " " + owner, "# Exact " + scope + " body " + owner)!.Id;
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => path), (Action<string>)(_ => { }), new SnapshotClock()], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var manager = model.ReportingRoots.Single();
        var ownerNode = manager.Reports.Single();
        void Select(string owner) => Control<TreeView>("PeopleList").RaiseEvent(
            new RoutedPropertyChangedEventArgs<object>(null!, owner == manager.Person.Email ? manager : ownerNode,
                TreeView.SelectedItemChangedEvent));
        T Note<T>(NoteScope scope, string name) where T : FrameworkElement =>
            (T)Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes").FindName(name);
        string Reading(NoteScope scope)
        {
            var reading = Note<FlowDocumentScrollViewer>(scope, "Reading");
            return new TextRange(reading.Document.ContentStart, reading.Document.ContentEnd).Text;
        }
        var unexpectedDraft = false;
        var textAssignments = 0;
        var observed = new List<TextBox>();
        TextChangedEventHandler changed = (_, _) =>
        {
            textAssignments++;
            foreach (var (key, id) in ids)
                unexpectedDraft |= model.GetRetainedNoteDraft(key.Item1, id) is not null;
        };
        foreach (var scope in Enum.GetValues<NoteScope>())
            foreach (var name in new[] { "NoteTitle", "MarkdownEditor" })
            {
                var editor = Note<TextBox>(scope, name);
                editor.TextChanged += changed;
                observed.Add(editor);
            }
        foreach (var owner in new[] { "owner@example.com", "manager@example.com", "owner@example.com" })
        {
            Select(owner);
            foreach (var scope in Enum.GetValues<NoteScope>())
                if (Note<TextBlock>(scope, "NoteHeading").Text != scope + " " + owner ||
                    !Reading(scope).Contains("Exact " + scope + " body " + owner, StringComparison.Ordinal) ||
                    Note<TabControl>(scope, "Views").SelectedItem != Note<TabItem>(scope, "ReadTab") ||
                    model.GetOwnedNotesView(scope).SelectedNote?.Id != ids[(scope, owner)])
                    throw new InvalidOperationException("Read navigation exposed an old owner/title/body/identity or wrong view.");
        }
        foreach (var editor in observed) editor.TextChanged -= changed;
        if (unexpectedDraft)
            throw new InvalidOperationException("Programmatic Read population incorrectly retained or mutated note writing despite loading state.");
        foreach (var scope in Enum.GetValues<NoteScope>())
        {
            var revision = model.Synchronization.LocalRevision;
            Note<TabControl>(scope, "Views").SelectedItem = Note<TabItem>(scope, "EditTab");
            if (Note<TextBox>(scope, "NoteTitle").Text != scope + " owner@example.com" ||
                Note<TextBox>(scope, "MarkdownEditor").Text != "# Exact " + scope + " body owner@example.com" ||
                !Note<TextBox>(scope, "MarkdownEditor").IsEnabled ||
                model.GetRetainedNoteDraft(scope, ids[(scope, "owner@example.com")]) is not null ||
                model.Synchronization.LocalRevision != revision)
                throw new InvalidOperationException("Actual Edit did not populate exact current writing before usable editing, or the mode toggle mutated/retained it.");
            Note<TextBox>(scope, "NoteTitle").Text = "";
            Note<TextBox>(scope, "MarkdownEditor").Text = "Exact newer " + scope + " retained writing";
            PumpUntil(() => model.GetRetainedNoteDraft(scope, ids[(scope, "owner@example.com")])?.Error is not null);
            var retained = model.GetRetainedNoteDraft(scope, ids[(scope, "owner@example.com")])!;
            Select("manager@example.com");
            if (!Reading(scope).Contains("Exact " + scope + " body manager@example.com", StringComparison.Ordinal))
                throw new InvalidOperationException("Retained editing leaked another owner's writing into current Read.");
            Select("owner@example.com");
            if (Note<TabControl>(scope, "Views").SelectedItem != Note<TabItem>(scope, "EditTab") ||
                Note<TextBox>(scope, "NoteTitle").Text != "" || Note<TextBox>(scope, "MarkdownEditor").Text != retained.Markdown ||
                !ReferenceEquals(model.GetRetainedNoteDraft(scope, ids[(scope, "owner@example.com")])?.Basis, retained.Basis) ||
                model.GetRetainedNoteDraft(scope, ids[(scope, "owner@example.com")])?.Error != retained.Error)
                throw new InvalidOperationException("Retained Edit owner return changed exact current writing/base/error/view.");
            Note<Button>(scope, "DiscardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Note<TabControl>(scope, "Views").SelectedItem = Note<TabItem>(scope, "ReadTab");
        }
        using (var independent = OpenSmokeWorkspace(path))
            foreach (var ((scope, owner), id) in ids)
                if (independent.BeginNoteDraft(scope, owner, id)?.Markdown != "# Exact " + scope + " body " + owner)
                    throw new InvalidOperationException("Reading/edit-toggle/retained discard changed independently saved owner writing.");
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Reading-demand verification cannot create a native window.");
        if (!model.ConfigureSynchronizationFolder(Path.Combine(root, "nrt")) || !model.SyncNow())
            throw new InvalidOperationException(model.Status);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
        if (textAssignments != 0)
            throw new InvalidOperationException($"Read-only owner navigation unnecessarily assigned hidden edit TextBoxes {textAssignments} times; exact current Read/Edit/data and no-mutation checks passed.");
        Console.WriteLine("PASS: exact current Read uses no hidden editor assignments; actual Edit/retained return is complete with original owner/base/error and no mode-only mutation. No native focus/latency acceptance.");
    }
}
