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
        TeamNotes.OwnerRequested += noteId =>
        {
            if (!TryLeaveLocalEdits()) return;
            if (!workflow.NavigateInheritedTeamNote(noteId))
            {
                ShowNavigationBlocked(workflow.Status);
                return;
            }
            if (NavigateTo(workflow.PeopleNavigation.SelectedEmail!, searchNavigation: true))
            {
                ClearSearchContext();
                TeamNotes.OpenNote(noteId);
                TeamNotes.BringIntoView();
                ShowStatus();
            }
        };
    }
}
