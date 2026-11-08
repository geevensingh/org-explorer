using System.Windows;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private sealed record ManagerChoice(string? Email, string Label);
    private bool? unsavedArchived;
    private ManagerChoice? unsavedManager;
    private bool HasUnsavedRelationship => unsavedArchived.HasValue || unsavedManager is not null || unsavedFavorite.HasValue;

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
        ManagerChoices.ItemsSource = choices;
        ManagerChoices.SelectedItem = choices.FirstOrDefault(c =>
            string.Equals(c.Email, unsavedManager is not null ? unsavedManager.Email : person.ManagerEmail,
                StringComparison.OrdinalIgnoreCase));
    }

    private void ClearPersonArchival()
    {
        PersonArchivalStatus.Text = "Select a Person.";
        ArchivePersonButton.IsEnabled = ReactivatePersonButton.IsEnabled = ManagerEditor.IsEnabled = false;
        ManagerChoices.ItemsSource = null;
    }

    private void ArchivePerson(object sender, RoutedEventArgs e) => ChangePersonArchival(true);
    private void ReactivatePerson(object sender, RoutedEventArgs e) => ChangePersonArchival(false);

    private void ChangePersonArchival(bool archived)
    {
        if (selectedEmail is null || !TryLeaveLocalEdits()) return;
        if (workflow.EditPersonArchived(selectedEmail, archived))
        {
            unsavedArchived = null;
            RefreshPeople();
        }
        else if (!workflow.CanSafelyClose) unsavedArchived = archived;
        InvalidateRelationshipPreview();
    }

    private void ChangePersonManager(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null || ManagerChoices.SelectedItem is not ManagerChoice choice || !TryLeaveLocalEdits()) return;
        if (workflow.EditPersonManager(selectedEmail, choice.Email))
        {
            unsavedManager = null;
            RefreshPeople();
        }
        else if (!workflow.CanSafelyClose) unsavedManager = choice;
        InvalidateRelationshipPreview();
    }

    private void InvalidateRelationshipPreview()
    {
        ApplyButton.IsEnabled = false;
        preview = null;
        ShowStatus();
    }

    private void RetryRelationshipSave()
    {
        if (selectedEmail is null) return;
        if (unsavedManager is { } manager && workflow.EditPersonManager(selectedEmail, manager.Email)) unsavedManager = null;
        if (unsavedArchived is { } archived && workflow.EditPersonArchived(selectedEmail, archived)) unsavedArchived = null;
        if (unsavedFavorite is { } favorite && workflow.EditFavorite(selectedEmail, favorite)) unsavedFavorite = null;
    }
}
