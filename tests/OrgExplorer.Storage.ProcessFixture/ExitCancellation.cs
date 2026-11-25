using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OrgExplorer.Workflow;

namespace OrgExplorer.Storage.ProcessFixture;

internal static class ExitCancellation
{
    internal static int Run(string workspace, NoteScope scope)
    {
        var path = Path.GetFullPath(workspace);
        if (!path.Contains(Path.DirectorySeparatorChar + ".test-workspaces" + Path.DirectorySeparatorChar,
            StringComparison.Ordinal)) return 3;
        using var gate = new CommitGate();
        var clock = new RequestClock();
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(fileObserver: gate), timeProvider: clock);
        if (!app.OpenWorkspace(path) || !app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"93000000-0000-4000-8000-000000000099",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null},
             {"email":"other@example.com","displayName":"Other","managerEmail":"owner@example.com"}]}
            """).Id) || !app.ConfigureSynchronizationFolder(path + "-sync") || !app.SyncNow())
            throw new InvalidOperationException(app.Status);
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
        Task? closing = null;
        var completed = false;
        var cancelled = false;
        var elapsed = 0d;
        Guid? noteId = null;
        using var completion = new CompletionGap();
        try
        {
            // Only this owned managed child is constrained, never the test host or desktop.
            if (!ThreadPool.SetMinThreads(2, minIo) || !ThreadPool.SetMaxThreads(2, maxIo))
                throw new InvalidOperationException("The owned cancellation fixture could not constrain its worker.");
            gate.Armed = true;
            var note = app.CreateOwnedNoteDraft(scope, "owner@example.com")!;
            noteId = note.Id;
            app.UpdateNoteDraft(scope, note.Id, "Exact retained title", "Exact retained writing");
            if (!gate.Entered.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The real owned transaction did not reach Commit.");
            using var cancellation = new CancellationTokenSource();
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(completion);
            try { closing = app.PrepareForExitAsync(cancellation.Token); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            clock.Advance(TimeSpan.FromSeconds(3));
            if (app.LocalSave.Phase != LocalSavePhase.Stalled || !app.NavigatePerson("other@example.com"))
                throw new InvalidOperationException("The actual stalled writing did not remain browsable.");
            var draft = app.GetRetainedNoteDraft(scope, note.Id);
            var fault = app.LocalSave;
            var selection = app.PeopleNavigation;
            completion.CompleteAwait();
            var waiting = Stopwatch.StartNew();
            cancellation.Cancel();
            completed = ((IAsyncResult)closing).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            elapsed = waiting.Elapsed.TotalMilliseconds;
            if (completed)
            {
                try { closing.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            if (draft != app.GetRetainedNoteDraft(scope, note.Id) || fault != app.LocalSave ||
                selection != app.PeopleNavigation || gate.Release.IsSet)
                throw new InvalidOperationException("Exit cancellation changed retained writing/fault/browsing or released the real writer.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, scope, completed, cancelled, elapsedMs = elapsed,
                realCommitHeld = gate.Entered.IsSet && !gate.Release.IsSet,
                completedAwaitGap = true,
                phase = app.LocalSave.Phase, preservedDraft = true, preservedFault = true, preservedSelection = true,
                limitMs = 1000, mechanism = "Owned child only: a real admitted SQLite writer and a controlled completed-await posting effect occupy the workers. Cancellation arrives after Flush completion but before the exit continuation."
            }));
        }
        finally
        {
            var restoredMax = ThreadPool.SetMaxThreads(maxWorker, maxIo);
            var restoredMin = ThreadPool.SetMinThreads(minWorker, minIo);
            completion.Release.Set();
            gate.Release.Set();
            if (closing is not null)
            {
                try { closing.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
            if (noteId is { } id && !SpinWait.SpinUntil(() => !app.LocalSave.EditsPaused &&
                app.GetRetainedNoteDraft(scope, id)?.IsSaving != true, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The released owned writer did not settle.");
            if (!app.FlushNoteDraftsAsync().GetAwaiter().GetResult())
                throw new InvalidOperationException("The released owned writer could not flush: " + app.Status);
            if (!restoredMax || !restoredMin)
                throw new InvalidOperationException("The owned cancellation fixture could not restore its managed workers.");
        }
        return completed && cancelled ? 0 : 1;
    }

    private sealed class RequestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }
    private sealed class CommitGate : IWorkspaceFileObserver, IDisposable
    {
        internal bool Armed;
        internal ManualResetEventSlim Entered = new();
        internal ManualResetEventSlim Release = new();
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (!Armed || phase.Name != "local-sqlite-commit" || phase.Kind != WorkspaceStoragePhaseKind.Begin) return;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The owning cancellation fixture did not release Commit.");
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
    private sealed class CompletionGap : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> pending = new();
        private readonly ManualResetEventSlim posted = new();
        internal readonly ManualResetEventSlim Release = new();
        public override void Post(SendOrPostCallback callback, object? state)
        {
            pending.Enqueue((callback, state));
            posted.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The owned completion-post effect was not released.");
        }
        internal void CompleteAwait()
        {
            if (!posted.Wait(TimeSpan.FromSeconds(5)) || !pending.TryDequeue(out var work))
                throw new TimeoutException("The actual Flush await did not reach its owned continuation.");
            var previous = Current;
            SetSynchronizationContext(this);
            try { work.Callback(work.State); }
            finally { SetSynchronizationContext(previous); }
        }
        public void Dispose() { Release.Set(); posted.Dispose(); Release.Dispose(); }
    }
}
