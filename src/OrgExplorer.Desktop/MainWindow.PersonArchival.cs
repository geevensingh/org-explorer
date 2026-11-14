using System.Windows;
using System.Windows.Automation;
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
            ? "Archived Person"
            : "";
        PersonArchivalStatus.ToolTip = "Profile, photos, notes and history retained. No current inherited context.";
        PersonArchivalStatus.Visibility = person.Archived ? Visibility.Visible : Visibility.Collapsed;
        PersonMoreMenu.IsEnabled = workflow.IsWorkspaceCurrent && !workPending;
        ArchivePersonMenuItem.Visibility = person.Archived ? Visibility.Collapsed : Visibility.Visible;
        ReactivatePersonMenuItem.Visibility = person.Archived ? Visibility.Visible : Visibility.Collapsed;
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
        PersonMoreMenu.IsEnabled = ManagerEditor.IsEnabled = false;
        ManagerChoices.ItemsSource = null;
    }

    private void ArchivePerson(object sender, RoutedEventArgs e) => ChangePersonArchival(true);
    private void ReactivatePerson(object sender, RoutedEventArgs e) => ChangePersonArchival(false);

    private async void ChangePersonArchival(bool archived)
    {
        if (workPending || selectedEmail is not { } email || workflow.GetRelationships(email) is not { } context) return;
        if (!ConfirmPersonArchival(context.Person, archived))
        {
            workflow.EditPersonArchived(email, archived, confirmed: false);
            ShowStatus();
            return;
        }
        if (!TryLeaveLocalEdits()) return;
        if (await RunWorkflowAsync(() => workflow.EditPersonArchived(email, archived, confirmed: true), "Saving Person archival state…"))
        {
            unsavedArchived = null;
            RefreshPeople();
        }
        else if (!workflow.CanSafelyClose) unsavedArchived = archived;
        InvalidateRelationshipPreview();
    }

    private bool ConfirmPersonArchival(PersonProfile person, bool archived)
    {
        var action = archived ? "Archive" : "Reactivate";
        var dialog = new Window
        {
            Owner = this, Title = $"{action} Person?", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize
        };
        AutomationProperties.SetAutomationId(dialog, "PersonArchivalConfirmation");
        var content = new StackPanel { Margin = new Thickness(20), MaxWidth = 480 };
        content.Children.Add(new TextBlock
        {
            Text = $"{action} {person.Heading} ({person.Email})?\n\n" + (archived
                ? "Remove this Person from the current org while retaining their profile, photos, notes and history. " +
                  "Active reports must be explicitly reassigned or archived first; no subtree is archived automatically."
                : "Return this Person to the current org only if their stored reporting relationships are valid. " +
                  "Retained notes, photos and history are preserved.") +
                "\n\nThis action applies immediately after confirmation and any required draft decision, not with profile Save/Cancel.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var confirm = new Button { Content = action, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, IsDefault = true };
        AutomationProperties.SetAutomationId(confirm, "ConfirmPersonArchival");
        AutomationProperties.SetAutomationId(cancel, "CancelPersonArchival");
        confirm.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        content.Children.Add(buttons);
        dialog.Content = content;
        return dialog.ShowDialog() == true;
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
