using System.Windows;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private string? preferredNameOwner;

    private void LoadPreferredName(PersonProfile person)
    {
        if (!string.Equals(preferredNameOwner, person.Email, StringComparison.OrdinalIgnoreCase))
        {
            preferredNameOwner = person.Email;
            SetPreferredNameEditing(false);
        }
        PreferredNameValue.Text = person.PreferredName ?? "";
        EditPreferredNameButton.Content = person.PreferredName is null ? "Add preferred name" : "Edit";
        if (!unsavedAttributes.ContainsKey(PersonTextField.PreferredName))
            PreferredName.Text = person.PreferredName ?? "";
        PreferredNameContext.IsEnabled = workflow.IsWorkspaceCurrent;
    }

    private void SetPreferredNameEditing(bool editing)
    {
        PreferredNameReadPanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        PreferredNameEditorPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditPreferredName(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null || !workflow.IsWorkspaceCurrent || workPending) return;
        SetPreferredNameEditing(true);
        PreferredName.Focus();
        PreferredName.SelectAll();
    }

    private void ReadPreferredName(object sender, RoutedEventArgs e)
    {
        if (workPending || unsavedAttributes.ContainsKey(PersonTextField.PreferredName))
        {
            ShowNavigationBlocked(workflow.LocalSaveError is null
                ? "Preferred name is not yet saved. Wait for saving to finish before returning to reading."
                : "Preferred name was NOT saved. Retry or deliberately discard the draft before returning to reading.");
            return;
        }
        SetPreferredNameEditing(false);
    }
}
