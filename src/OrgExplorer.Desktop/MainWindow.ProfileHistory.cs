namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private void ShowProfileTimeline(string? email)
    {
        var timeline = email is null ? null : workflow.GetProfileTimeline(email);
        ProfileTimelineText.Text = timeline?.DisplayChanges ?? "Select a Person to review Profile changes.";
        FieldConfirmationsText.Text = timeline?.DisplayConfirmations ?? "";
        ProfileObservationsText.Text = timeline?.DisplayObservations ?? "";
        ShowHistoricalTeamContext(email);
    }
}
