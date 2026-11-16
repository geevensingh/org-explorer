namespace OrgExplorer.Workflow;

public sealed record RetainedNoteDraft(NoteDraft Basis, string Title, string Markdown, bool IsSaving, string? Error);

public sealed partial class WorkspaceViewModel
{
    private sealed class LocalNoteDraft(NoteDraft basis, string title, string markdown, long order, IDisposable adoptionDeferral)
    {
        public NoteDraft Basis = basis;
        public string Title = title;
        public string Markdown = markdown;
        public long Generation = 1;
        public long Order = order;
        public bool Saving;
        public string? Error;
        public IDisposable AdoptionDeferral { get; } = adoptionDeferral;
        public readonly List<(long Generation, long Request)> Requests = [];
        public (long Generation, string Title, string Markdown)? Submitted;
        public bool UnknownOutcome;
    }
    private readonly object noteDraftGate = new();
    private readonly Dictionary<(NoteScope Scope, Guid Id), LocalNoteDraft> localNoteDrafts = [];
    private Task? noteAutosaveTask;
    private SynchronizationContext? noteAutosaveContext;
    private long noteDraftOrder;
    private long noteAutosaveEpoch;
    private volatile bool noteAutosaveStopped;

    private bool HasLocalNoteDrafts { get { lock (noteDraftGate) return localNoteDrafts.Count != 0; } }

    public NoteDraft? CreateOwnedNoteDraft(NoteScope scope, string ownerEmail) => Observe(() =>
    {
        if (!Compatibility.SupportsNonblockingNoteAutosave || !Compatibility.SupportsNoteEdits || !CanEditLocalWorkspace)
        {
            navigationError = "Open a current local SQLite Workspace before creating an autosaved note.";
            return null;
        }
        if (!OpenOwnedNotes(scope, ownerEmail)) return null;
        var basis = workflow.BeginNoteDraft(scope, ownerEmail);
        if (basis is null) return null;
        lock (noteDraftGate)
        {
            var draft = new LocalNoteDraft(basis, $"New {scope} note", "", ++noteDraftOrder,
                workflow.DeferSnapshotAdoption());
            draft.Requests.Add((draft.Generation, localSaveRequests.Begin()));
            localNoteDrafts.Add((scope, basis.Id), draft);
            workflow.RetainNoteDraft(basis);
            ScheduleNoteAutosave();
        }
        noteViews[scope] = NoteViewFor(scope) with { Selected = basis.Id, Editing = true, Exact = true, Error = null };
        return basis;
    });

    public void UpdateNoteDraft(NoteScope scope, Guid id, string title, string markdown)
    {
        if (!Compatibility.SupportsNonblockingNoteAutosave)
            throw new InvalidOperationException("Retained autosave requires a local SQLite Workspace.");
        if (!CanEditLocalWorkspace)
        {
            navigationError = "Editing paused. The exact retained note is unchanged; browsing remains available.";
            Notify();
            return;
        }
        var owner = GetOwnedNotesView(scope).SelectedNote?.OwnerEmail;
        lock (noteDraftGate)
        {
            if (!localNoteDrafts.TryGetValue((scope, id), out var draft))
            {
                var basis = owner is null ? null : workflow.BeginNoteDraft(scope, owner, id);
                if (basis is null) throw new InvalidOperationException("Open the exact owned note before editing.");
                localNoteDrafts.Add((scope, id), draft = new(basis, title, markdown, ++noteDraftOrder,
                    workflow.DeferSnapshotAdoption()));
            }
            draft.Title = title;
            draft.Markdown = markdown;
            draft.Generation++;
            draft.Requests.Add((draft.Generation, localSaveRequests.Begin()));
            draft.Error = null;
            // The admitted writer owns retention until its accepted successor base is installed.
            if (!draft.Saving) workflow.RetainNoteDraft(draft.Basis);
            ScheduleNoteAutosave();
        }
        Notify();
    }

    public RetainedNoteDraft? GetRetainedNoteDraft(NoteScope scope, Guid id)
    {
        lock (noteDraftGate)
            return localNoteDrafts.TryGetValue((scope, id), out var draft)
                ? new(draft.Basis, draft.Title, draft.Markdown, draft.Saving, draft.Error) : null;
    }

    public async Task<bool> FlushNoteDraftsAsync()
    {
        if (LocalSave.EditsPaused) return false;
        Task? saving;
        lock (noteDraftGate)
        {
            ScheduleNoteAutosave();
            saving = noteAutosaveTask;
        }
        if (saving is not null)
        {
            while (!saving.IsCompleted)
            {
                if (LocalSave.EditsPaused) return false;
                await Task.WhenAny(saving, Task.Delay(50));
            }
            await saving;
        }
        return !HasLocalNoteDrafts;
    }

    public async Task<bool> RetryNoteDraftAsync(NoteScope scope, Guid id)
    {
        if (LocalSave.HasUnresolvedOutcome && LocalSave.EditsPaused && !ReconcileLocalSaves()) return false;
        if (workflow.LocalSave.EditsPaused && !ReconcileLocalSaves()) return false;
        lock (noteDraftGate)
        {
            if (localNoteDrafts.TryGetValue((scope, id), out var draft) && !draft.Saving)
            {
                draft.Error = null;
                if (draft.Requests.Count == 0) draft.Requests.Add((draft.Generation, localSaveRequests.Begin()));
            }
        }
        return await FlushNoteDraftsAsync();
    }

    private void ScheduleNoteAutosave()
    {
        noteAutosaveContext ??= SynchronizationContext.Current;
        if (noteAutosaveStopped || localSaveReconciling || viewSaveFault is not null || workflow.LocalSave.EditsPaused || noteAutosaveTask is { IsCompleted: false } ||
            !localNoteDrafts.Values.Any(d => d.Error is null)) return;
        foreach (var draft in localNoteDrafts.Values.Where(draft => draft.Error is null && draft.Requests.Count == 0))
            draft.Requests.Add((draft.Generation, localSaveRequests.Begin()));
        var epoch = noteAutosaveEpoch;
        noteAutosaveTask = Task.Run(() => SaveLocalNoteDraftsAsync(epoch));
    }

    private async Task SaveLocalNoteDraftsAsync(long epoch)
    {
        // The cadence starts with the first edit; further typing cannot postpone it.
        await Task.Delay(250).ConfigureAwait(false);
        while (true)
        {
            LocalNoteDraft draft;
            NoteDraft basis;
            string title, markdown;
            long generation;
            lock (noteDraftGate)
            {
                if (epoch != noteAutosaveEpoch) return;
                if (noteAutosaveStopped || localSaveReconciling || viewSaveFault is not null || workflow.LocalSave.EditsPaused)
                { noteAutosaveTask = null; return; }
                var next = localNoteDrafts.Values.Where(d => d.Error is null).OrderBy(d => d.Order).FirstOrDefault();
                if (next is null)
                {
                    noteAutosaveTask = null;
                    return;
                }
                draft = next;
                if (draft.Requests.Count == 0) draft.Requests.Add((draft.Generation, localSaveRequests.Begin()));
                basis = draft.Basis;
                title = draft.Title;
                markdown = draft.Markdown;
                generation = draft.Generation;
                draft.Submitted = (generation, title, markdown);
                draft.Saving = true;
            }
            NotifyLocalNoteDrafts();
            NoteDraft? accepted = null;
            try { accepted = workflow.SaveNoteDraft(basis, title, markdown); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                UnknownLocalSave(error);
                draft.UnknownOutcome = true;
            }
            lock (noteDraftGate)
            {
                draft.Saving = false;
                foreach (var request in draft.Requests.Where(request => request.Generation <= generation).ToArray())
                {
                    localSaveRequests.Complete(request.Request);
                    draft.Requests.Remove(request);
                }
                if (accepted is null)
                {
                    draft.Error = viewSaveFault?.Message ?? workflow.Status;
                    draft.UnknownOutcome |= workflow.LocalSave.HasUnresolvedOutcome;
                }
                else if (generation == draft.Generation)
                {
                    localNoteDrafts.Remove((basis.Scope, basis.Id));
                    draft.AdoptionDeferral.Dispose();
                }
                else
                {
                    draft.Basis = accepted;
                    draft.Order = ++noteDraftOrder;
                    workflow.RetainNoteDraft(accepted);
                }
            }
            NotifyLocalNoteDrafts();
        }
    }

    private void NotifyLocalNoteDrafts()
    {
        void Changed()
        {
            if (noteAutosaveStopped) return;
            Notify();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
        if (noteAutosaveContext is { } context) context.Post(_ => Changed(), null);
        else Changed();
    }

    private bool ReconcileUnknownNoteDrafts()
    {
        lock (noteDraftGate)
        {
            foreach (var (key, draft) in localNoteDrafts.Where(item => item.Value.UnknownOutcome).ToArray())
            {
                var current = workflow.BeginNoteDraft(key.Scope, draft.Basis.OwnerEmail, key.Id);
                if (draft.Submitted is not { } submitted) return false;
                if (current is not null && current.Title == submitted.Title && current.Markdown == submitted.Markdown)
                {
                    workflow.DiscardUnsavedChanges(key.Id);
                    foreach (var request in draft.Requests) localSaveRequests.Complete(request.Request);
                    draft.Requests.Clear();
                    if (draft.Generation == submitted.Generation)
                    {
                        localNoteDrafts.Remove(key);
                        draft.AdoptionDeferral.Dispose();
                    }
                    else
                    {
                        draft.Basis = current;
                        draft.Error = null;
                        draft.UnknownOutcome = false;
                        draft.Requests.Add((draft.Generation, localSaveRequests.Begin()));
                        workflow.RetainNoteDraft(current);
                    }
                }
                else if (draft.Basis.IsNew && current is null ||
                    current is not null && current.Person == draft.Basis.Person && current.Team == draft.Basis.Team &&
                        current.Confirmation == draft.Basis.Confirmation)
                {
                    draft.UnknownOutcome = false;
                    draft.Error = "The original request did not commit. The exact draft is retained; choose Retry local save.";
                }
                else
                {
                    navigationError = "Local outcome remains unresolved: committed note content differs from both the captured base and submitted writing. Both are retained; do not replay the mutation.";
                    return false;
                }
            }
            return true;
        }
    }

    private IReadOnlyList<OwnedNote> RetainedOwnedNotes(NoteScope scope, string owner)
    {
        lock (noteDraftGate)
            return localNoteDrafts.Where(item => item.Key.Scope == scope &&
                    item.Value.Basis.OwnerEmail.Equals(owner, StringComparison.OrdinalIgnoreCase))
                .Select(item => new OwnedNote(item.Key.Id, item.Value.Basis.OwnerEmail,
                    item.Value.Title, item.Value.Markdown, false)).ToArray();
    }

    private bool DiscardLocalNoteDrafts(Guid? id)
    {
        lock (noteDraftGate)
        {
            var keys = localNoteDrafts.Keys.Where(key => id is null || key.Id == id).ToArray();
            if (keys.Any(key => localNoteDrafts[key].Saving))
            {
                navigationError = "Discard blocked: reconcile the admitted local note save before discarding its draft.";
                return false;
            }
            foreach (var key in keys)
            {
                if (localNoteDrafts[key].Basis.IsNew)
                {
                    var view = NoteViewFor(key.Scope);
                    if (view.Selected == key.Id)
                        noteViews[key.Scope] = view with { Selected = null, Editing = false, Exact = false, Error = null };
                    foreach (var remembered in retainedNoteViews.Keys.Where(remembered => remembered.Scope == key.Scope &&
                        retainedNoteViews[remembered].Selected == key.Id).ToArray())
                        retainedNoteViews.Remove(remembered);
                }
                localNoteDrafts[key].AdoptionDeferral.Dispose();
                foreach (var request in localNoteDrafts[key].Requests) localSaveRequests.Complete(request.Request);
                localNoteDrafts.Remove(key);
            }
            return true;
        }
    }

    public NoteDraft? BeginNoteDraft(NoteScope scope, string ownerEmail, Guid? noteId = null) =>
        workflow.BeginNoteDraft(scope, ownerEmail, noteId);

    public NoteDraft? SaveNoteDraft(NoteDraft draft, string title, string markdown) =>
        CanSubmitLocalSave() ? ObserveLocalSave(() => workflow.SaveNoteDraft(draft, title, markdown)) : null;

    public void RetainNoteDraft(NoteDraft draft)
    {
        workflow.RetainNoteDraft(draft);
        PropertyChanged?.Invoke(this, new(nameof(CanSafelyClose)));
    }

    public bool HasUnsavedNoteDraft(NoteScope scope, Guid noteId) =>
        GetRetainedNoteDraft(scope, noteId) is not null || workflow.HasUnsavedNoteDraft(scope, noteId);
}
