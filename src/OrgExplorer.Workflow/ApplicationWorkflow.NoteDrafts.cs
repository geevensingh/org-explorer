using System.Collections.Concurrent;

namespace OrgExplorer.Workflow;

public enum NoteScope { Person, Team }

public sealed class NoteDraft
{
    internal NoteDraft(object workflow, string workspace, NoteScope scope, PersonNote? person, TeamNote? team,
        Guid confirmation, bool isNew = false)
    {
        Workflow = workflow;
        Workspace = workspace;
        Scope = scope;
        Person = person;
        Team = team;
        Confirmation = confirmation;
        IsNew = isNew;
    }

    public NoteScope Scope { get; }
    public Guid Id => Person?.Id ?? Team!.Id;
    public string OwnerEmail => Person?.OwnerEmail ?? Team!.OwnerEmail;
    public string Title => Person?.Title ?? Team!.Title;
    public string Markdown => Person?.Markdown ?? Team!.Markdown;
    internal object Workflow { get; }
    internal string Workspace { get; }
    internal PersonNote? Person { get; }
    internal TeamNote? Team { get; }
    internal Guid Confirmation { get; }
    internal bool IsNew { get; }
}

public partial interface IApplicationWorkflow
{
    NoteDraft? BeginNoteDraft(NoteScope scope, string ownerEmail, Guid? noteId = null);
    void RetainNoteDraft(NoteDraft draft);
    bool HasUnsavedNoteDraft(NoteScope scope, Guid noteId);
    NoteDraft? SaveNoteDraft(NoteDraft draft, string title, string markdown);
}

public sealed partial class ApplicationWorkflow
{
    private readonly ConcurrentDictionary<(NoteScope Scope, Guid Id), NoteDraft> unsavedNoteBases = new();

    // Typing must not wait behind the workspace lock held by an in-flight save.
    public void RetainNoteDraft(NoteDraft draft) => unsavedNoteBases.TryAdd((draft.Scope, draft.Id), draft);
    public bool HasUnsavedNoteDraft(NoteScope scope, Guid noteId) => unsavedNoteBases.ContainsKey((scope, noteId));

    public NoteDraft? BeginNoteDraft(NoteScope scope, string ownerEmail, Guid? noteId = null)
    {
        lock (workspaceGate)
        {
            if (workspace is null || !people.TryGetValue(ownerEmail, out var owner)) return null;
            if (noteId is null)
            {
                var now = DateTimeOffset.UtcNow;
                var newId = Guid.NewGuid();
                return scope switch
                {
                    NoteScope.Person => new(this, workspace, scope,
                        new(newId, owner.Email, "", "", false, now, now), null, Guid.Empty, isNew: true),
                    NoteScope.Team => new(this, workspace, scope, null,
                        new(newId, owner.Email, "", "", false, now, now),
                        teamContextRevisions.GetValueOrDefault(owner.Email), isNew: true),
                    _ => null
                };
            }
            var id = noteId.Value;
            if (scope == NoteScope.Person && personNotes.TryGetValue(id, out var person) &&
                person.OwnerEmail.Equals(ownerEmail, StringComparison.OrdinalIgnoreCase))
                return new(this, workspace, scope, person, null, Guid.Empty);
            if (scope == NoteScope.Team && teamNotes.TryGetValue(id, out var team) &&
                team.OwnerEmail.Equals(ownerEmail, StringComparison.OrdinalIgnoreCase))
                return new(this, workspace, scope, null, team, teamNoteConfirmations[id]);
            return null;
        }
    }

    public NoteDraft? SaveNoteDraft(NoteDraft draft, string title, string markdown)
    {
        lock (workspaceGate)
        {
            RetainNoteDraft(draft);
            try { ValidateNoteDraft(draft.Scope, draft.Id); }
            catch (IOException error)
            {
                SaveFailed(NoteSaveKey(draft.Scope, draft.Id),
                    $"{draft.Scope} note save failed; changes are NOT saved locally: " + error.Message);
                return null;
            }
            var saved = draft.IsNew ? SaveNewNoteDraft(draft, title, markdown) :
                draft.Scope == NoteScope.Person
                    ? EditPersonNote(draft.Id, title, markdown) : EditTeamNote(draft.Id, title, markdown);
            return saved ? BeginNoteDraft(draft.Scope, draft.OwnerEmail, draft.Id) : null;
        }
    }

    private bool SaveNewNoteDraft(NoteDraft draft, string title, string markdown)
    {
        if (string.IsNullOrWhiteSpace(title))
            return SaveFailed(NoteSaveKey(draft.Scope, draft.Id),
                $"{draft.Scope} note title must be nonblank; changes are NOT saved locally.");
        var now = DateTimeOffset.UtcNow;
        if (draft.Scope == NoteScope.Person)
        {
            var note = draft.Person! with { Title = title, Markdown = markdown, UpdatedAt = now };
            if (!SavePersonNote(note)) return false;
            personNotes.Add(note.Id, note);
        }
        else
        {
            var note = draft.Team! with { Title = title, Markdown = markdown, UpdatedAt = now };
            var confirmation = teamContextRevisions.GetValueOrDefault(note.OwnerEmail);
            if (!SaveTeamNote(note, confirmation)) return false;
            teamNotes.Add(note.Id, note);
            teamNoteConfirmations.Add(note.Id, confirmation);
        }
        InvalidateProjections();
        pending = null;
        Status = $"{draft.Scope} note saved locally.";
        return true;
    }

    private static string NoteSaveKey(NoteScope scope, Guid id) =>
        scope == NoteScope.Person ? $"note:{id}" : $"team-note:{id}";

    private void RetainNoteBase(NoteScope scope, Guid id)
    {
        var owner = scope == NoteScope.Person
            ? personNotes.GetValueOrDefault(id)?.OwnerEmail : teamNotes.GetValueOrDefault(id)?.OwnerEmail;
        if (owner is not null && BeginNoteDraft(scope, owner, id) is { } draft)
            unsavedNoteBases.TryAdd((scope, id), draft);
    }

    private void ValidateNoteDraft(NoteScope scope, Guid id)
    {
        if (!unsavedNoteBases.TryGetValue((scope, id), out var draft)) return;
        var matches = scope switch
        {
            NoteScope.Person => draft.IsNew ? !personNotes.ContainsKey(id) : personNotes.GetValueOrDefault(id) == draft.Person,
            NoteScope.Team => draft.IsNew ? !teamNotes.ContainsKey(id) :
                teamNotes.GetValueOrDefault(id) == draft.Team && teamNoteConfirmations.GetValueOrDefault(id) == draft.Confirmation,
            _ => false
        };
        if (!ReferenceEquals(draft.Workflow, this) ||
            !string.Equals(draft.Workspace, workspace, StringComparison.OrdinalIgnoreCase) ||
            !people.ContainsKey(draft.OwnerEmail) || !matches)
            throw new IOException($"{scope} note changed externally while a draft was unsaved. Discard the draft before editing the delivered version.");
    }

    private void AcceptNoteSave(NoteScope scope, Guid id)
    {
        failedSaves.Remove(NoteSaveKey(scope, id));
        unsavedNoteBases.TryRemove((scope, id), out _);
    }
}
