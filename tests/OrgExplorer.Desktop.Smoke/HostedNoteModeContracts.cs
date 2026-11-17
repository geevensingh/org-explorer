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
    private sealed class HostedNoteModeCommitGate : IWorkspaceFileObserver, IDisposable
    {
        internal bool Armed;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (!Armed || phase.Name != "local-sqlite-commit" || phase.Kind != WorkspaceStoragePhaseKind.Begin) return;
            Entered.Set();
            Release.Wait();
        }
        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private static void VerifyHostedNoteModeContracts(Type windowType, string root)
    {
        var path = Path.Combine(root, "hosted-note-modes");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000075",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null},
                 {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"morgan@example.com"}]}
                """).Id))
                throw new InvalidOperationException("Note-mode fixture failed: " + seed.Status);
        using var gate = new HostedNoteModeCommitGate();
        var app = ApplicationWorkflow.CreateFileBacked(fileObserver: gate);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [app, (Func<Window, string?>)(_ => CompiledWorkspaceChoices.Dequeue())], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        var failures = new List<string>();
        Guid? active = null;
        NoteScope activeScope = NoteScope.Person;
        void Select(string email)
        {
            var node = model.ReportingRoots.SelectMany(item => new[] { item }.Concat(item.Reports))
                .Single(item => item.Person.Email == email);
            Control<TreeView>("PeopleList").RaiseEvent(
                new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
            if (Control<TextBlock>("Email").Text != email || model.PeopleNavigation.SelectedEmail != email)
                throw new InvalidOperationException("The compiled note-mode selection did not reach its exact Person.");
        }
        try
        {
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ChooseCompiledWorkspace(window, path);
            PumpUntil(() => model.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
            foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
            {
                activeScope = scope;
                var owner = scope == NoteScope.Person ? "avery@example.com" : "morgan@example.com";
                var away = scope == NoteScope.Person ? "morgan@example.com" : "avery@example.com";
                Select(owner);
                var surface = Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes");
                T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
                void Click(string name) => Note<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Capture(string stage)
                {
                    var view = model.GetOwnedNotesView(scope);
                    var retained = active is { } id ? model.GetRetainedNoteDraft(scope, id) : null;
                    Console.WriteLine("HOSTED-NOTE-MODE " + JsonSerializer.Serialize(new
                    {
                        stage, scope = scope.ToString(), person = Control<TextBlock>("Email").Text,
                        model = new
                        {
                            view.OwnerEmail, view.SelectedNote, view.IsEditing, view.HasUnsavedChanges, view.Feedback
                        },
                        control = new
                        {
                            tab = (Note<TabControl>("Views").SelectedItem as TabItem)?.Name,
                            title = Note<TextBox>("NoteTitle").Text,
                            body = Note<TextBox>("MarkdownEditor").Text,
                            heading = Note<TextBlock>("NoteHeading").Text
                        },
                        retained = retained is null ? null : new
                        {
                            retained.Basis.Id, retained.Basis.OwnerEmail, retained.Title, retained.Markdown,
                            retained.IsSaving, retained.Error
                        },
                        model.LocalSaveError, model.CanSafelyClose
                    }));
                }
                void Require(string stage, string title, string body, bool editing, RetainedNoteDraft? retained = null)
                {
                    Capture(stage);
                    var view = model.GetOwnedNotesView(scope);
                    var current = model.GetRetainedNoteDraft(scope, active!.Value);
                    if (view.OwnerEmail != owner || view.SelectedNote?.Id != active ||
                        view.SelectedNote.OwnerEmail != owner || view.SelectedNote.Title != title ||
                        view.SelectedNote.Markdown != body || view.IsEditing != editing ||
                        Note<TabControl>("Views").SelectedItem != Note<TabItem>(editing ? "EditTab" : "ReadTab") ||
                        Note<TextBox>("NoteTitle").Text != title || Note<TextBox>("MarkdownEditor").Text != body ||
                        view.HasUnsavedChanges != (retained is not null) ||
                        (retained is null ? current is not null : current != retained))
                        failures.Add($"{scope} {stage}: expected exact owner/id/title/body, " +
                            $"{(editing ? "Edit" : "Read")} and {(retained is null ? "clean" : "unchanged retained")} writing.");
                }
                void RoundTrip(string stage)
                {
                    Select(away);
                    Capture(stage + "-away");
                    if (model.GetOwnedNotesView(scope).OwnerEmail != away ||
                        model.GetOwnedNotesView(scope).SelectedNote is not null ||
                        Note<TextBox>("NoteTitle").Text != "" || Note<TextBox>("MarkdownEditor").Text != "")
                        throw new InvalidOperationException("Owner navigation leaked another Person's exact note writing.");
                    Select(owner);
                }

                Click("NewButton");
                active = model.GetOwnedNotesView(scope).SelectedNote!.Id;
                var title = scope == NoteScope.Person ? "Private meeting" : "Casey context";
                Note<TextBox>("NoteTitle").Text = title;
                PumpUntil(() => model.GetRetainedNoteDraft(scope, active.Value) is null && model.CanSafelyClose);
                Require("clean-before-leave", title, "", editing: true);
                RoundTrip("clean");
                Require("clean-return", title, "", editing: false);

                Note<TabControl>("Views").SelectedItem = Note<TabItem>("EditTab");
                gate.Entered.Reset();
                gate.Release.Reset();
                gate.Armed = true;
                var pendingBody = $"Exact pending {scope} writing\nSecond line";
                Note<TextBox>("MarkdownEditor").Text = pendingBody;
                try
                {
                    PumpUntil(() => gate.Entered.IsSet && model.GetRetainedNoteDraft(scope, active.Value)?.IsSaving == true);
                    var pending = model.GetRetainedNoteDraft(scope, active.Value)!;
                    Require("pending-before-leave", title, pendingBody, editing: true, pending);
                    RoundTrip("pending");
                    Require("pending-return", title, pendingBody, editing: true, pending);
                    using var reading = ApplicationWorkflow.CreateFileBacked();
                    if (!reading.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                        reading.BeginNoteDraft(scope, owner, active)?.Markdown != "")
                        throw new InvalidOperationException("Navigation committed or replaced an actually held note write.");
                }
                finally
                {
                    gate.Armed = false;
                    gate.Release.Set();
                }
                PumpUntil(() => model.GetRetainedNoteDraft(scope, active.Value) is null && model.CanSafelyClose);

                var failedBody = $"Exact failed {scope} writing\nRetain original error";
                Note<TextBox>("NoteTitle").Text = " ";
                Note<TextBox>("MarkdownEditor").Text = failedBody;
                PumpUntil(() => model.GetRetainedNoteDraft(scope, active.Value) is { IsSaving: false, Error: not null });
                var failed = model.GetRetainedNoteDraft(scope, active.Value)!;
                var error = model.LocalSaveError;
                Require("failed-before-leave", " ", failedBody, editing: true, failed);
                RoundTrip("failed");
                Require("failed-return", " ", failedBody, editing: true, failed);
                using (var reading = ApplicationWorkflow.CreateFileBacked())
                    if (!reading.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                        reading.BeginNoteDraft(scope, owner, active) is not { } saved ||
                        saved.Title != title || saved.Markdown != pendingBody ||
                        model.LocalSaveError != error || model.CanSafelyClose)
                        throw new InvalidOperationException("Failed note navigation changed saved writing, its error or close safety.");
                Click("DiscardButton");
                RoundTrip("discarded-clean");
                Require("discarded-clean-return", title, pendingBody, editing: false);
                active = null;
            }
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Note-mode contracts created a visible or native window.");
            if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            Console.WriteLine("PASS: invisible compiled Person/Team clean return is Read; admitted pending and failed " +
                "owner drafts retain exact Edit/id/title/body/error, without implicit save/discard or cross-owner leakage.");
        }
        finally
        {
            gate.Armed = false;
            gate.Release.Set();
            PumpUntil(() => active is not { } id || model.GetRetainedNoteDraft(activeScope, id)?.IsSaving != true);
            if (active is { } pending) model.DiscardUnsavedChanges(pending);
            ClosePublishedCompiledWindow(window);
        }
    }
}
