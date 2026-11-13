namespace OrgExplorer.Workflow;

public sealed record OwnedNote(Guid Id, string OwnerEmail, string Title, string Markdown, bool Archived);

public sealed record OwnedNotesView(
    string? OwnerEmail, IReadOnlyList<OwnedNote> Notes, Guid? OngoingNoteId,
    OwnedNote? SelectedNote, bool IsEditing, bool OtherNotesOpen, string Feedback)
{
    public bool IsOngoing => SelectedNote is not null && SelectedNote.Id == OngoingNoteId;
}

public sealed partial class WorkspaceViewModel
{
    private sealed record NoteView(string? Owner = null, Guid? Selected = null, bool Editing = false,
        bool OtherNotes = false, string? Error = null, bool Exact = false);
    private readonly Dictionary<NoteScope, NoteView> noteViews = [];
    private readonly Dictionary<(string Workspace, NoteScope Scope, string Owner), Guid> ongoingNotes = [];
    private string? ongoingPreferenceError;

    public OwnedNotesView PersonNotesView => GetOwnedNotesView(NoteScope.Person);
    public OwnedNotesView TeamNotesView => GetOwnedNotesView(NoteScope.Team);

    private NoteView NoteViewFor(NoteScope scope) => noteViews.GetValueOrDefault(scope) ?? new();
    private IReadOnlyList<OwnedNote> OwnedNotes(NoteScope scope, string? owner) => scope switch
    {
        NoteScope.Person => owner is null ? [] : workflow.GetPersonNotes(owner, true)
            .Select(n => new OwnedNote(n.Id, n.OwnerEmail, n.Title, n.Markdown, n.Archived)).ToArray(),
        NoteScope.Team => owner is null ? [] : workflow.GetTeamNotes(owner, true)
            .Select(n => new OwnedNote(n.Id, n.OwnerEmail, n.Title, n.Markdown, n.Archived)).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    private (string, NoteScope, string) OngoingKey(NoteScope scope, string owner) =>
        ((Compatibility.WorkspacePath ?? "").ToUpperInvariant(), scope, owner.ToUpperInvariant());

    private Guid? OngoingId(NoteScope scope, string? owner, IReadOnlyList<OwnedNote> notes) =>
        owner is not null && Compatibility.WorkspacePath is { } path &&
            settings?.GetOngoingNote(path, scope, owner) is { } saved ? saved :
        ongoingPreferenceError is not null || settings is { HasValidPreferenceSnapshot: false } ? null :
        owner is not null && ongoingNotes.TryGetValue(OngoingKey(scope, owner), out var id) ? id :
        notes.Count(n => !n.Archived) == 1 ? notes.Single(n => !n.Archived).Id : null;

    public OwnedNotesView GetOwnedNotesView(NoteScope scope)
    {
        var view = NoteViewFor(scope);
        var notes = OwnedNotes(scope, view.Owner);
        var ongoing = OngoingId(scope, view.Owner, notes);
        var selectedId = view.Exact ? view.Selected : ongoing;
        var selected = notes.FirstOrDefault(n => n.Id == selectedId && (view.Exact || !n.Archived));
        var feedback = view.Error ?? ongoingPreferenceError ??
            (settings is { HasValidPreferenceSnapshot: false } ? settings.PreferenceError : null) ?? (view.Owner is null ? "" :
            ongoing is { } id && !notes.Any(n => n.Id == id && !n.Archived)
                ? "The ongoing note is unavailable or archived. Your choice is retained. Use Other notes to restore it or choose another ongoing note."
                : view.Exact && view.Selected is not null && selected is null
                    ? "The selected note is unavailable. Use Other notes or deliberately open the ongoing note. No replacement was opened."
                : ongoing is null && notes.Any(n => !n.Archived)
                    ? "Choose an ongoing note from Other notes. No document has been chosen for you."
                    : notes.Count == 0 ? $"No {scope} notes. Write a new note to begin." : "");
        return new(view.Owner, notes, ongoing, selected, view.Editing && selected is { Archived: false },
            view.OtherNotes, feedback);
    }

    public bool OpenOwnedNotes(NoteScope scope, string? ownerEmail) => Observe(() =>
    {
        if (string.Equals(NoteViewFor(scope).Owner, ownerEmail, StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryLeaveOwnedNote(scope, changingOwner: true)) return false;
        if (ownerEmail is not null && workflow.GetRelationships(ownerEmail) is null)
        {
            noteViews[scope] = NoteViewFor(scope) with { Error = "That Person is unavailable. No replacement was opened." };
            return false;
        }
        var notes = OwnedNotes(scope, ownerEmail);
        noteViews[scope] = new(ownerEmail, OngoingId(scope, ownerEmail, notes));
        return true;
    });

    public bool OpenOwnedNote(NoteScope scope, Guid id, bool editing = false) => Observe(() =>
    {
        var view = NoteViewFor(scope);
        if (view.Selected != id && !TryLeaveOwnedNote(scope)) return false;
        var note = OwnedNotes(scope, view.Owner).FirstOrDefault(n => n.Id == id);
        if (note is null)
        {
            noteViews[scope] = view with { Error = "That note is unavailable. No replacement was opened." };
            return false;
        }
        noteViews[scope] = view with { Selected = id, Editing = editing && !note.Archived, Error = null, Exact = true };
        if (!note.Archived) RememberCurrentView(scope);
        return true;
    });

    public bool OpenOngoingNote(NoteScope scope) => Observe(() =>
    {
        if (!TryLeaveOwnedNote(scope)) return false;
        var view = NoteViewFor(scope);
        var notes = OwnedNotes(scope, view.Owner);
        var id = OngoingId(scope, view.Owner, notes);
        noteViews[scope] = view with
        {
            Selected = notes.FirstOrDefault(n => n.Id == id && !n.Archived)?.Id,
            Editing = false, OtherNotes = false, Error = null, Exact = false
        };
        var opened = GetOwnedNotesView(scope).SelectedNote is not null;
        if (opened) RememberCurrentView(scope);
        return opened;
    });

    public void ShowOtherNotes(NoteScope scope, bool show) => Observe(() =>
    {
        noteViews[scope] = NoteViewFor(scope) with { OtherNotes = show };
        return true;
    });

    public bool DesignateOngoingNote(NoteScope scope, Guid id) => Observe(() =>
    {
        var owner = PrepareOngoingDesignation(scope, id);
        return owner is not null && CompleteOngoingDesignation(scope, owner, id,
            settings?.SetOngoingNote(Compatibility.WorkspacePath!, scope, owner, id) ?? true);
    });

    public async Task<bool> DesignateOngoingNoteAsync(NoteScope scope, Guid id,
        Func<Func<bool>, Task<bool>> writePreference)
    {
        var owner = Observe(() => PrepareOngoingDesignation(scope, id));
        if (owner is null) return false;
        var path = Compatibility.WorkspacePath;
        // Only preference I/O leaves the caller's presentation context.
        var saved = await writePreference(() => settings?.SetOngoingNote(path!, scope, owner, id) ?? true);
        return Observe(() => CompleteOngoingDesignation(scope, owner, id, saved));
    }

    private string? PrepareOngoingDesignation(NoteScope scope, Guid id)
    {
        if (!TryLeaveOwnedNote(scope)) return null;
        var view = NoteViewFor(scope);
        if (view.Owner is null || !OwnedNotes(scope, view.Owner).Any(n => n.Id == id && !n.Archived))
        {
            noteViews[scope] = view with { Error = "Choose an available, active owned note. The ongoing choice is unchanged." };
            return null;
        }
        return view.Owner;
    }

    private bool CompleteOngoingDesignation(NoteScope scope, string owner, Guid id, bool saved)
    {
        var view = NoteViewFor(scope);
        if (!saved)
        {
            foreach (var noteScope in Enum.GetValues<NoteScope>())
                if (GetOwnedNotesView(noteScope).SelectedNote is { } displayed)
                    noteViews[noteScope] = NoteViewFor(noteScope) with { Selected = displayed.Id, Exact = true };
            noteViews[scope] = NoteViewFor(scope) with { Error = ongoingPreferenceError = settings!.PreferenceError };
            return false;
        }
        if (settings is null) ongoingNotes[OngoingKey(scope, owner)] = id;
        ongoingPreferenceError = null;
        noteViews[scope] = view with { Selected = id, Editing = false, OtherNotes = false, Error = null, Exact = false };
        RememberCurrentView(scope);
        return true;
    }

    private bool TryLeaveOwnedNote(NoteScope scope, bool changingOwner = false)
    {
        var id = GetOwnedNotesView(scope).SelectedNote?.Id ?? NoteViewFor(scope).Selected;
        if (changingOwner ? workflow.CanNavigatePeople : id is null || !workflow.HasUnsavedNoteDraft(scope, id.Value))
            return true;
        const string message = "Navigation blocked: local writing is pending or was NOT saved. Retry or discard the draft before leaving or changing the ongoing note.";
        noteViews[scope] = NoteViewFor(scope) with { Error = message };
        navigationError = message;
        return false;
    }
}
