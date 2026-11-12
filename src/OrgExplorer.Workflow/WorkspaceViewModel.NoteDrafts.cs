namespace OrgExplorer.Workflow;

public sealed partial class WorkspaceViewModel
{
    public NoteDraft? BeginNoteDraft(NoteScope scope, string ownerEmail, Guid? noteId = null) =>
        workflow.BeginNoteDraft(scope, ownerEmail, noteId);

    public NoteDraft? SaveNoteDraft(NoteDraft draft, string title, string markdown) =>
        Observe(() => workflow.SaveNoteDraft(draft, title, markdown));

    public void RetainNoteDraft(NoteDraft draft)
    {
        workflow.RetainNoteDraft(draft);
        PropertyChanged?.Invoke(this, new(nameof(CanSafelyClose)));
    }

    public bool HasUnsavedNoteDraft(NoteScope scope, Guid noteId) => workflow.HasUnsavedNoteDraft(scope, noteId);
}
