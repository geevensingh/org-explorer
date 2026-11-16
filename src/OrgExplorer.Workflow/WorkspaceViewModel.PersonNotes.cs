namespace OrgExplorer.Workflow;

public sealed partial class WorkspaceViewModel
{
    public IReadOnlyList<PersonNote> GetPersonNotes(string email, bool includeArchived = false) =>
        workflow.GetPersonNotes(email, includeArchived);

    public PersonNote? CreatePersonNote(string email, string title, string markdown) =>
        CanSubmitLocalSave() ? ObserveLocalSave(() => workflow.CreatePersonNote(email, title, markdown)) : null;

    public bool EditPersonNote(Guid noteId, string title, string markdown) =>
        CanSubmitLocalSave() && ObserveLocalSave(() => workflow.EditPersonNote(noteId, title, markdown));

    public bool ArchivePersonNote(Guid noteId, bool archived = true) =>
        CanSubmitLocalSave() && ObserveLocalSave(() => workflow.ArchivePersonNote(noteId, archived));

    public bool RefreshPersonNotes() => Observe(workflow.RefreshPersonNotes);
}
