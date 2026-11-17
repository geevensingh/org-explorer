namespace OrgExplorer.Workflow;

public sealed record PersonNote(Guid Id, string OwnerEmail, string Title, string Markdown,
    bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public partial interface IApplicationWorkflow
{
    IReadOnlyList<PersonNote> GetPersonNotes(string email, bool includeArchived = false);
    PersonNote? CreatePersonNote(string email, string title, string markdown);
    bool EditPersonNote(Guid noteId, string title, string markdown);
    bool ArchivePersonNote(Guid noteId, bool archived = true);
    bool RefreshPersonNotes();
}

public sealed partial class ApplicationWorkflow
{
    private Dictionary<Guid, PersonNote> personNotes = [];

    public IReadOnlyList<PersonNote> GetPersonNotes(string email, bool includeArchived = false)
    { if (queryView is { } view) return view.GetPersonNotes(email, includeArchived);
      lock (workspaceGate) return
        Array.AsReadOnly(PersonNotesOf(email).Where(n => includeArchived || !n.Archived).ToArray()); }

    public PersonNote? CreatePersonNote(string email, string title, string markdown)
    { lock (workspaceGate) return CreatePersonNoteCore(email, title, markdown); }

    private PersonNote? CreatePersonNoteCore(string email, string title, string markdown)
    {
        if (workspace is null || !people.TryGetValue(email, out var person))
        {
            Fail("Open a Person before creating a Person note.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            Fail("Person note title must be nonblank.");
            return null;
        }
        var draft = BeginNoteDraft(NoteScope.Person, person.Email);
        return draft is not null && SaveNoteDraft(draft, title, markdown) is { } saved ? personNotes[saved.Id] : null;
    }

    public bool EditPersonNote(Guid noteId, string title, string markdown)
        => CanBeginLocalSave() && TrackLocalSave(() => EditPersonNoteLocked(noteId, title, markdown));

    private bool EditPersonNoteLocked(Guid noteId, string title, string markdown)
    { lock (workspaceGate) return EditPersonNoteCore(noteId, title, markdown); }

    private bool EditPersonNoteCore(Guid noteId, string title, string markdown)
    {
        if (workspace is null || !personNotes.TryGetValue(noteId, out var note))
            return Fail("Open an existing Person note before editing.");
        if (string.IsNullOrWhiteSpace(title))
        {
            RetainNoteBase(NoteScope.Person, note.Id);
            return SaveFailed($"note:{note.Id}", "Person note title must be nonblank; changes are NOT saved locally.");
        }
        var proposed = note with { Title = title, Markdown = markdown, UpdatedAt = DateTimeOffset.UtcNow };
        if (!SavePersonNote(proposed)) return false;
        personNotes[noteId] = proposed;
        InvalidateProjections();
        pending = null;
        Status = "Person note saved locally.";
        return true;
    }

    public bool ArchivePersonNote(Guid noteId, bool archived = true)
        => CanBeginLocalSave() && TrackLocalSave(() => ArchivePersonNoteLocked(noteId, archived));

    private bool ArchivePersonNoteLocked(Guid noteId, bool archived)
    { lock (workspaceGate) return ArchivePersonNoteCore(noteId, archived); }

    private bool ArchivePersonNoteCore(Guid noteId, bool archived)
    {
        if (workspace is null || !personNotes.TryGetValue(noteId, out var note))
            return Fail("Open an existing Person note before archival.");
        var proposed = note with { Archived = archived, UpdatedAt = DateTimeOffset.UtcNow };
        if (!SavePersonNote(proposed)) return false;
        personNotes[noteId] = proposed;
        InvalidateProjections();
        pending = null;
        Status = archived ? "Person note archived locally; writing retained." : "Person note restored locally.";
        return true;
    }

    public bool RefreshPersonNotes() => RefreshWorkspace();

    private bool SavePersonNote(PersonNote note)
    {
        try
        {
            if (memory)
            {
                ValidateNoteDraft(NoteScope.Person, note.Id);
                AcceptNoteSave(NoteScope.Person, note.Id);
                return true;
            }
            var metadata = new NoteMetadata(1, "person", note.Id, note.OwnerEmail, note.Title,
                note.Archived, note.CreatedAt, note.UpdatedAt);
            SaveLocalSqliteNote(NoteScope.Person, metadata, note.Markdown);
            AcceptNoteSave(NoteScope.Person, note.Id);
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            RetainNoteBase(NoteScope.Person, note.Id);
            return SaveFailed($"note:{note.Id}", "Person note save failed; changes are NOT saved locally: " + ex.Message);
        }
    }

}
