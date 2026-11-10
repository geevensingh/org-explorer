namespace OrgExplorer.Workflow;

public sealed partial class WorkspaceViewModel
{
    public IReadOnlyList<TeamNote> GetTeamNotes(string email, bool includeArchived = false) =>
        workflow.GetTeamNotes(email, includeArchived);
    public IReadOnlyList<InheritedTeamNote> GetInheritedTeamNotes(string email) =>
        workflow.GetInheritedTeamNotes(email);
    public TeamNote? CreateTeamNote(string email, string title, string markdown) =>
        Observe(() => workflow.CreateTeamNote(email, title, markdown));
    public bool EditTeamNote(Guid noteId, string title, string markdown) =>
        Observe(() => workflow.EditTeamNote(noteId, title, markdown));
    public bool ConfirmTeamNote(Guid noteId) => Observe(() => workflow.ConfirmTeamNote(noteId));
    public bool ArchiveTeamNote(Guid noteId, bool archived = true) =>
        Observe(() => workflow.ArchiveTeamNote(noteId, archived));
    public bool RefreshTeamNotes() => Observe(workflow.RefreshTeamNotes);
}
