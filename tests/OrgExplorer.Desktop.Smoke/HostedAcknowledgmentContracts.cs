using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyHostedAcknowledgmentContract(Type windowType, string root)
    {
        var workspace = Path.Combine(root, "acknowledgment-workspace");
        var transport = Path.Combine(root, "acknowledgment-snapshots");
        using var barrier = new NoteAcknowledgmentCommitBarrier();
        using var store = ApplicationWorkflow.CreateFileBacked(fileObserver: barrier);
        if (!store.OpenWorkspace(workspace) ||
            !store.ApplyImport(store.PreviewImport("""
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000102",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"owner@example.com","displayName":"Owner","managerEmail":null}]}
                """).Id))
            throw new InvalidOperationException(store.Status);
        var note = store.CreateTeamNote("owner@example.com", "Exact owner note", "# Before export")!;
        if (!store.ConfigureSynchronizationFolder(transport))
            throw new InvalidOperationException(store.Status);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => workspace)], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var owner = model.ReportingRoots.Single();
        Control<TreeView>("PeopleList").RaiseEvent(
            new RoutedPropertyChangedEventArgs<object>(null!, owner, TreeView.SelectedItemChangedEvent));
        var surface = Control<UserControl>("TeamNotes");
        T Team<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        Invoke("OpenSettingsButton");
        Invoke("SyncNowButton");
        PumpUntil(() => !model.IsSynchronizing && !model.Synchronization.HasUnpublishedChanges);
        var afterExport = AutomationProperties.GetItemStatus(surface);
        if (!model.CanSafelyClose || afterExport != "Locally saved and published. Provider delivery elsewhere is unknown.")
            throw new InvalidOperationException("Real completed publication did not preserve its truthful local/synchronization feedback: " +
                $"local={model.LocalSave.Phase}/{model.LocalSave.Outcome}; retained={model.HasUnsavedNoteDraft(NoteScope.Team, note.Id)}; " +
                $"feedback='{afterExport}'; published={model.Synchronization.PublishedRevision == model.Synchronization.LocalRevision}.");
        Invoke("OpenSettingsButton");
        Team<TabControl>("Views").SelectedItem = Team<TabItem>("EditTab");
        var bodyPeer = UIElementAutomationPeer.CreatePeerForElement(Team<TextBox>("MarkdownEditor"))!;
        var body = (IValueProvider)bodyPeer.GetPattern(PatternInterface.Value)!;
        const string writing = "# Exact request across unseen pending transition";
        var heldObservation = new NoteCommitAcknowledgment(NoteScope.Team, note.Id, note.OwnerEmail, note.Title, writing);
        var fastObservation = new NoteCommitAcknowledgment(NoteScope.Team, note.Id, note.OwnerEmail, note.Title, writing);
        var afterPublication = new NoteCommitAcknowledgment(NoteScope.Team, note.Id, note.OwnerEmail, note.Title, writing);
        NoteEditorFrame Frame() => new(AutomationProperties.GetHelpText(Team<TextBlock>("NoteHeading")),
            Team<TextBox>("NoteTitle").Text, body.Value, Team<TextBox>("MarkdownEditor").IsEnabled,
            Team<TabControl>("Views").SelectedItem == Team<TabItem>("EditTab"), false, false, 5);
        string Feedback() => AutomationProperties.GetItemStatus(surface);
        barrier.Armed = true;
        try
        {
            body.SetValue(writing);
            PumpUntil(() => barrier.Entered.IsSet);
            if (heldObservation.Observe(Frame(), "owner@example.com", Feedback(), inputReturned: true))
                throw new InvalidOperationException("A previous scoped acknowledgment accepted the exact uncommitted new request.");
            using var unchanged = ApplicationWorkflow.CreateFileBacked();
            if (!unchanged.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly) ||
                unchanged.GetTeamNotes(note.OwnerEmail).Single().Markdown != note.Markdown)
                throw new InvalidOperationException("The actual pre-Commit negative did not preserve original committed writing.");
        }
        finally { barrier.Armed = false; barrier.Release.Set(); }
        PumpUntil(() => model.CanSafelyClose && Feedback().Contains("saved locally", StringComparison.Ordinal));
        if (!fastObservation.Observe(Frame(), "owner@example.com", Feedback(), inputReturned: true) ||
            fastObservation.PendingObservedAt is not null)
            throw new InvalidOperationException("A genuinely committed exact request cannot require observation of a transient Pending state.");
        Invoke("OpenSettingsButton");
        Invoke("SyncNowButton");
        PumpUntil(() => !model.IsSynchronizing && !model.Synchronization.HasUnpublishedChanges);
        Invoke("OpenSettingsButton");
        if (!afterPublication.Observe(Frame(), "owner@example.com", Feedback(), inputReturned: true) ||
            afterPublication.PendingObservedAt is not null)
            throw new InvalidOperationException("An exact durable request cannot lose its acknowledgment merely because legitimate publication feedback replaced the note-save phrase.");
        FinalNoteVerification.Verify(workspace, new(NoteScope.Team, note.Id, note.OwnerEmail,
            "different@example.com", note.Title, writing, writing));
        using (var early = ApplicationWorkflow.CreateFileBacked())
        {
            if (!early.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly))
                throw new InvalidOperationException(early.Status);
            var firstAckAt = fastObservation.AcknowledgmentObservedAt;
            var finalTitle = note.Title + " final generation";
            var finalWriting = writing + "\nThe legitimately later final generation.";
            var expected = new FinalNoteExpectation(NoteScope.Team, note.Id, note.OwnerEmail,
                "different@example.com", finalTitle, writing, finalWriting);
            var titlePeer = UIElementAutomationPeer.CreatePeerForElement(Team<TextBox>("NoteTitle"))!;
            var title = (IValueProvider)titlePeer.GetPattern(PatternInterface.Value)!;
            var successRecords = new List<double>();
            var readers = 0;
            var finalStarted = System.Diagnostics.Stopwatch.StartNew();
            bool FinalCheck()
            {
                if (finalStarted.Elapsed >= TimeSpan.FromSeconds(20))
                    throw new TimeoutException("The original final-note observation exceeded20 seconds.");
                return ObserveFinalRichNote(workspace, expected, Frame(),
                    () => AutomationProperties.GetItemStatus(window), () => Control<TextBlock>("Email").Text,
                    successRecords.Add, () => { readers++; return ApplicationWorkflow.CreateFileBacked(); });
            }
            barrier.Entered.Reset();
            barrier.Release.Reset();
            barrier.Armed = true;
            var heldReaders = 0;
            var pendingAtWorkCompletion = false;
            var readiness = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                AutomationProperties.ItemStatusProperty, windowType)!;
            EventHandler completionCheck = (_, _) =>
            {
                if (!Team<TextBox>("MarkdownEditor").IsEnabled || AutomationProperties.GetItemStatus(window) != "Ready" ||
                    !barrier.Entered.IsSet || barrier.Release.IsSet) return;
                if (Frame().Title != finalTitle || Frame().Markdown != finalWriting || !Frame().Writable || !Frame().Editing ||
                    model.GetRetainedNoteDraft(NoteScope.Team, note.Id) is not { IsSaving: true } ||
                    early.BeginNoteDraft(NoteScope.Team, note.OwnerEmail, note.Id)?.Markdown != writing)
                    throw new InvalidOperationException("The actual work-completion callback did not retain latest usable UI over the held final generation.");
                if (FinalCheck() || FinalCheck() || successRecords.Count != 0)
                    throw new InvalidOperationException("The original final Await must remain pending without success while the actual later Commit is held.");
                heldReaders = readers;
                pendingAtWorkCompletion = true;
            };
            try
            {
                body.SetValue(finalWriting);
                title.SetValue(finalTitle);
                if (!barrier.Entered.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The actual final generation did not reach its held Commit.");
                readiness.AddValueChanged(window, completionCheck);
                var browse = (Task<bool>)windowType.GetMethod("RunWorkflowAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(typeof(bool)).Invoke(window,
                        [(Func<bool>)(() => model.GetRelationships(note.OwnerEmail) is not null),
                            "Reading current owner while final writing is pending", false, false, false])!;
                PumpUntil(() => browse.IsCompleted);
                if (!browse.GetAwaiter().GetResult())
                    throw new InvalidOperationException("The real completed browsing operation did not retain the current owner.");
                if (!pendingAtWorkCompletion || heldReaders != 2)
                    throw new InvalidOperationException("Each pending final check must independently reopen real storage.");
            }
            finally
            {
                readiness.RemoveValueChanged(window, completionCheck);
                barrier.Armed = false;
                barrier.Release.Set();
            }
            var succeeded = false;
            PumpUntil(() => succeeded || (succeeded = FinalCheck()));
            if (successRecords.Count != 1 || readers <= heldReaders ||
                early.BeginNoteDraft(NoteScope.Team, note.OwnerEmail, note.Id)?.Markdown != writing ||
                firstAckAt != fastObservation.AcknowledgmentObservedAt)
                throw new InvalidOperationException("Final success must use fresh exact owner data once, without reusing the early reader or redating the first ACK.");
            Console.WriteLine("FINAL-NOTE-WAIT-CAUSAL " + System.Text.Json.JsonSerializer.Serialize(new
            {
                heldFinalCheckPending = true, heldReaders, finalReaders = readers, successRecords = successRecords.Count,
                staleEarlyReaderPreserved = true, firstAckUnchanged = true, elapsedMs = finalStarted.Elapsed.TotalMilliseconds,
                limitMs = 20000, nativeWindow = false
            }));
        }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The acknowledgment contract cannot create a native window.");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
        Console.WriteLine("PASS: actual provider input cannot acknowledge before held Commit and exact durable success can be observed without Pending or a retired word order after real SyncNow. Publication feedback remains unchanged; exact owner/request independently reopened; no native UI.");
    }
}
