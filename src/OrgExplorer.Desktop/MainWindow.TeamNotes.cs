namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private bool TryLeaveNotes() => PersonNotes.TryLeave() && TeamNotes.TryLeave();

    private void AttachTeamNotes()
    {
        TeamNotes.Attach(workflow, () =>
        {
            ApplyButton.IsEnabled = false;
            preview = null;
            ShowStatus();
        });
        TeamNotes.OwnerRequested += (email, noteId) =>
        {
            NavigateTo(email);
            if (string.Equals(selectedEmail, email, StringComparison.OrdinalIgnoreCase))
                TeamNotes.OpenNote(noteId);
        };
    }
}
