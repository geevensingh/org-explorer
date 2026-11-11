using System.Windows;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private sealed record ManagerChoice(string? Email, string Label);
    private bool? unsavedArchived;
    private bool HasUnsavedRelationship => unsavedArchived.HasValue || unsavedFavorite.HasValue;

    private void ChangeArchivedFilter(object sender, RoutedEventArgs e) => RefreshArchivedPeople();

    private void RefreshArchivedPeople()
    {
        var wasLoading = loading;
        loading = true;
        var archived = workflow.ArchivedPeople;
        ArchivedPeopleList.ItemsSource = archived;
        ArchivedPeopleList.SelectedItem = archived.FirstOrDefault(p =>
            string.Equals(p.Email, selectedEmail, StringComparison.OrdinalIgnoreCase));
        ArchivedPeopleCount.Text = $"Archived profiles: {archived.Count} — retained, not current org";
        ArchivedPeoplePanel.Visibility = ShowArchivedPeople.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        loading = wasLoading;
    }

    private void SelectArchivedPerson(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ArchivedPeopleList.SelectedItem is not PersonProfile person) return;
        if (!OpenPerson(person))
        {
            RefreshArchivedPeople();
            RestorePersonSelection();
        }
        else RestorePersonSelection();
    }

    private void ShowPersonArchival(PersonProfile person)
    {
        PersonArchivalStatus.Text = person.Archived
            ? "Archived — profile, photos, notes and history retained. No current inherited context."
            : "Active — included in the current imported org.";
        ArchivePersonButton.IsEnabled = workflow.IsWorkspaceCurrent && !person.Archived;
        ReactivatePersonButton.IsEnabled = workflow.IsWorkspaceCurrent && person.Archived;
        ManagerEditor.IsEnabled = workflow.IsWorkspaceCurrent;
        var choices = new List<ManagerChoice> { new(null, "(explicit no-Manager — clear relationship)") };
        choices.AddRange(workflow.People.Where(p => !string.Equals(p.Email, person.Email, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ManagerChoice(p.Email, $"{p.Heading} ({p.Email})" + (p.Archived ? " — Archived" : ""))));
        var wasLoading = loading;
        loading = true;
        ManagerChoices.ItemsSource = choices;
        ManagerChoices.SelectedItem = choices.FirstOrDefault(c =>
            string.Equals(c.Email, workflow.ProfileEdit is { } edit ? edit.Draft.ManagerEmail : person.ManagerEmail,
                StringComparison.OrdinalIgnoreCase));
        loading = wasLoading;
    }

    private void ClearPersonArchival()
    {
        PersonArchivalStatus.Text = "Select a Person.";
        ArchivePersonButton.IsEnabled = ReactivatePersonButton.IsEnabled = ManagerEditor.IsEnabled = false;
        ManagerChoices.ItemsSource = null;
    }

    private void ArchivePerson(object sender, RoutedEventArgs e) => ChangePersonArchival(true);
    private void ReactivatePerson(object sender, RoutedEventArgs e) => ChangePersonArchival(false);

    private async void ChangePersonArchival(bool archived)
    {
        if (selectedEmail is null || !TryLeaveLocalEdits()) return;
        var email = selectedEmail;
        if (await RunWorkflowAsync(() => workflow.EditPersonArchived(email, archived), "Saving Person archival state…"))
        {
            unsavedArchived = null;
            RefreshPeople();
        }
        else if (!workflow.CanSafelyClose) unsavedArchived = archived;
        InvalidateRelationshipPreview();
    }

    private void StagePersonManager(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) ChangePersonManager(sender, e);
    }

    private void ChangePersonManager(object sender, RoutedEventArgs e)
    {
        if (loading || workPending || workflow.ProfileEdit is null ||
            ManagerChoices.SelectedItem is not ManagerChoice choice) return;
        workflow.StageProfileManager(choice.Email);
        MarkDraftPending();
        InvalidateRelationshipPreview();
    }

    private void InvalidateRelationshipPreview()
    {
        ApplyButton.IsEnabled = false;
        preview = null;
        ShowStatus();
    }

    private async Task RetryRelationshipSave()
    {
        if (selectedEmail is null) return;
        if (unsavedArchived is { } archived && await RunWorkflowAsync(() => workflow.EditPersonArchived(selectedEmail, archived), "Retrying archival save…")) unsavedArchived = null;
        if (unsavedFavorite is { } favorite && await RunWorkflowAsync(() => workflow.EditFavorite(selectedEmail, favorite), "Retrying favorite save…")) unsavedFavorite = null;
    }
}
