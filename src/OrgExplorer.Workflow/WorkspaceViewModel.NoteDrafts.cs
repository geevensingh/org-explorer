namespace OrgExplorer.Workflow;

public sealed record RetainedNoteDraft(NoteDraft Basis, string Title, string Markdown, bool IsSaving, string? Error);

public sealed partial class WorkspaceViewModel
{
    private sealed class LocalNoteDraft(NoteDraft basis, string title, string markdown, long order)
    {
        public NoteDraft Basis = basis;
        public string Title = title;
        public string Markdown = markdown;
        public long Generation = 1;
        public long Order = order;
        public bool Saving;
        public string? Error;
    }
    private readonly object noteDraftGate = new();
    private readonly Dictionary<(NoteScope Scope, Guid Id), LocalNoteDraft> localNoteDrafts = [];
    private Task? noteAutosaveTask;
    private SynchronizationContext? noteAutosaveContext;
    private long noteDraftOrder;
    private volatile bool noteAutosaveStopped;

    private bool HasLocalNoteDrafts { get { lock (noteDraftGate) return localNoteDrafts.Count != 0; } }

    public NoteDraft? CreateOwnedNoteDraft(NoteScope scope, string ownerEmail) => Observe(() =>
    {
        if (!Compatibility.SupportsNonblockingNoteAutosave || !Compatibility.SupportsNoteEdits || !IsWorkspaceCurrent)
        {
            navigationError = "Open a current local SQLite Workspace before creating an autosaved note.";
            return null;
        }
        if (!OpenOwnedNotes(scope, ownerEmail)) return null;
        var basis = workflow.BeginNoteDraft(scope, ownerEmail);
        if (basis is null) return null;
        lock (noteDraftGate)
        {
            localNoteDrafts.Add((scope, basis.Id), new(basis, $"New {scope} note", "", ++noteDraftOrder));
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
        var owner = GetOwnedNotesView(scope).SelectedNote?.OwnerEmail;
        lock (noteDraftGate)
        {
            if (!localNoteDrafts.TryGetValue((scope, id), out var draft))
            {
                var basis = owner is null ? null : workflow.BeginNoteDraft(scope, owner, id);
                if (basis is null) throw new InvalidOperationException("Open the exact owned note before editing.");
                localNoteDrafts.Add((scope, id), draft = new(basis, title, markdown, ++noteDraftOrder));
            }
            draft.Title = title;
            draft.Markdown = markdown;
            draft.Generation++;
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
        Task? saving;
        lock (noteDraftGate)
        {
            ScheduleNoteAutosave();
            saving = noteAutosaveTask;
        }
        if (saving is not null) await saving;
        return !HasLocalNoteDrafts;
    }

    public async Task<bool> RetryNoteDraftAsync(NoteScope scope, Guid id)
    {
        lock (noteDraftGate)
        {
            if (localNoteDrafts.TryGetValue((scope, id), out var draft) && !draft.Saving)
                draft.Error = null;
        }
        return await FlushNoteDraftsAsync();
    }

    private void ScheduleNoteAutosave()
    {
        noteAutosaveContext ??= SynchronizationContext.Current;
        if (noteAutosaveStopped || noteAutosaveTask is { IsCompleted: false } ||
            !localNoteDrafts.Values.Any(d => d.Error is null)) return;
        noteAutosaveTask = Task.Run(SaveLocalNoteDraftsAsync);
    }

    private async Task SaveLocalNoteDraftsAsync()
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
                if (noteAutosaveStopped) return;
                var next = localNoteDrafts.Values.Where(d => d.Error is null).OrderBy(d => d.Order).FirstOrDefault();
                if (next is null)
                {
                    noteAutosaveTask = null;
                    return;
                }
                draft = next;
                basis = draft.Basis;
                title = draft.Title;
                markdown = draft.Markdown;
                generation = draft.Generation;
                draft.Saving = true;
            }
            NotifyLocalNoteDrafts();
            var accepted = workflow.SaveNoteDraft(basis, title, markdown);
            lock (noteDraftGate)
            {
                draft.Saving = false;
                if (accepted is null) draft.Error = workflow.Status;
                else if (generation == draft.Generation) localNoteDrafts.Remove((basis.Scope, basis.Id));
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
                localNoteDrafts.Remove(key);
            }
            return true;
        }
    }

    public NoteDraft? BeginNoteDraft(NoteScope scope, string ownerEmail, Guid? noteId = null) =>
        workflow.BeginNoteDraft(scope, ownerEmail, noteId);

    public NoteDraft? SaveNoteDraft(NoteDraft draft, string title, string markdown) =>
        Observe(() => workflow.SaveNoteDraft(draft, title, markdown));

    public void RetainNoteDraft(NoteDraft draft)
    {
        workflow.RetainNoteDraft(draft);
        PropertyChanged?.Invoke(this, new(nameof(CanSafelyClose)));
    }

    public bool HasUnsavedNoteDraft(NoteScope scope, Guid noteId) =>
        GetRetainedNoteDraft(scope, noteId) is not null || workflow.HasUnsavedNoteDraft(scope, noteId);
}
