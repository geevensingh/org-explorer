namespace OrgExplorer.Workflow;

public sealed partial class WorkspaceViewModel
{
    public IReadOnlyList<PersonNote> GetPersonNotes(string email, bool includeArchived = false) =>
        workflow.GetPersonNotes(email, includeArchived);

    public PersonNote? CreatePersonNote(string email, string title, string markdown) =>
        Observe(() => workflow.CreatePersonNote(email, title, markdown));

    public bool EditPersonNote(Guid noteId, string title, string markdown) =>
        Observe(() => workflow.EditPersonNote(noteId, title, markdown));

    public bool ArchivePersonNote(Guid noteId, bool archived = true) =>
        Observe(() => workflow.ArchivePersonNote(noteId, archived));

    public bool RefreshPersonNotes() => Observe(workflow.RefreshPersonNotes);
}
