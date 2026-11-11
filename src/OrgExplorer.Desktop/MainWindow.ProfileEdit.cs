using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private void BeginProfileEdit(object sender, RoutedEventArgs e)
    {
        if (workPending || selectedEmail is null) return;
        if (workflow.BeginProfileEdit(selectedEmail))
        {
            LoadPersonEditors(workflow.ProfileEdit!.Draft);
            ShowPersonArchival(workflow.ProfileEdit.Draft);
            DirectoryNameEditor.BringIntoView();
        }
        ShowStatus();
    }

    private async void SaveProfileEdit(object sender, RoutedEventArgs e) => await SaveProfileSessionAsync();

    private async Task<bool> SaveProfileSessionAsync()
    {
        if (workPending) return false;
        var saved = await RunWorkflowAsync(workflow.SaveProfileEdit, "Saving complete profile…");
        RefreshExternalWorkspace();
        return saved;
    }

    private void CancelProfileEdit(object sender, RoutedEventArgs e)
    {
        if (workPending)
        {
            ShowNavigationBlocked("Cancel blocked: local work is in progress. Please wait for it to finish.");
            return;
        }
        workflow.CancelProfileEdit();
        RefreshPeople();
        ShowStatus();
    }

    private void UpdateProfileMode()
    {
        var editing = workflow.ProfileEdit is not null;
        var writable = workflow.IsWorkspaceCurrent && selectedEmail is not null && !workPending;
        EditProfileButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditProfileButton.IsEnabled = writable;
        ProfileSessionActions.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        SaveProfileButton.IsEnabled = writable;
        CancelProfileButton.IsEnabled = !workPending;
        AttributeEditors.Visibility = ManagerEditor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        AttributeEditors.IsEnabled = ManagerEditor.IsEnabled = writable && editing;
        ProfileValues.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        Photo.CanEdit = writable && editing;
        ProfilePhotoDraft.Visibility = workflow.ProfileEdit is { PhotoChanged: true } ? Visibility.Visible : Visibility.Collapsed;
        ProfilePhotoDraft.Text = workflow.ProfileEdit?.PhotoLocalPath is { } path
            ? $"Staged photo: {path}. The saved photo remains shown until Save."
            : "Photo clear staged. The saved photo remains shown until Save.";
        PreferredNameContext.IsEnabled = PreferredName.IsEnabled = writable;
    }

    private void ShowProfileValues(PersonProfile? person)
    {
        ShowProfileValue(AliasValueRow, AliasValue, person?.Alias);
        ShowProfileValue(JobTitleValueRow, JobTitleValue, person?.JobTitle);
        ShowProfileValue(DepartmentValueRow, DepartmentValue, person?.Department);
        ShowProfileValue(OfficeBuildingValueRow, OfficeBuildingValue, person?.OfficeBuilding);
        ShowProfileValue(OfficeNumberValueRow, OfficeNumberValue, person?.OfficeNumber);
        ShowProfileValue(OfficeStateValueRow, OfficeStateValue, person?.OfficeState);
        ShowProfileValue(OfficeCountryValueRow, OfficeCountryValue, person?.OfficeCountry);
        ShowProfileValue(LinkedInProfileUrlValueRow, LinkedInProfileUrlValue, person?.LinkedInProfileUrl);
        DirectoryName.Visibility = person?.DirectoryName is null ? Visibility.Collapsed : Visibility.Visible;
        OpenLinkedInButton.Visibility = person?.LinkedInProfileUrl is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void ShowProfileValue(FrameworkElement row, TextBlock value, string? text)
    {
        value.Text = text ?? "";
        row.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CopyProfileValue(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value }) return;
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.ExternalException error)
        { StatusText.Text = "Copy failed. Retry when the clipboard is available: " + error.Message; }
    }

    private bool DecideProfileLeave()
    {
        if (workflow.ProfileEdit is null) return true;
        if (!workflow.ProfileEdit.IsDirty)
        {
            var left = workflow.TryLeaveProfileEdit(ProfileLeaveDecision.Discard);
            ShowStatus();
            return left;
        }
        var dialog = new Window
        {
            Owner = this, Title = "Unsaved profile", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize
        };
        AutomationProperties.SetAutomationId(dialog, "ProfileLeaveDecision");
        var content = new StackPanel { Margin = new Thickness(20), MaxWidth = 480 };
        content.Children.Add(new TextBlock
        {
            Text = $"Unsaved profile for {workflow.ProfileEdit.Basis.Heading}. Save the complete profile, discard this profile draft, or stay?",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "Save", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var discard = new Button { Content = "Discard", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var stay = new Button { Content = "Stay", MinWidth = 90, IsCancel = true, IsDefault = true };
        AutomationProperties.SetAutomationId(save, "SaveProfileBeforeLeaving");
        AutomationProperties.SetAutomationId(discard, "DiscardProfileBeforeLeaving");
        AutomationProperties.SetAutomationId(stay, "StayOnProfile");
        var saving = false;
        dialog.Closing += (_, e) => { if (saving) e.Cancel = true; };
        save.Click += async (_, _) =>
        {
            saving = true;
            buttons.IsEnabled = false;
            var saved = await SaveProfileSessionAsync();
            saving = false;
            dialog.DialogResult = saved;
        };
        discard.Click += (_, _) => dialog.DialogResult = workflow.TryLeaveProfileEdit(ProfileLeaveDecision.Discard);
        stay.Click += (_, _) =>
        {
            workflow.TryLeaveProfileEdit(ProfileLeaveDecision.Stay);
            dialog.DialogResult = false;
        };
        buttons.Children.Add(save);
        buttons.Children.Add(discard);
        buttons.Children.Add(stay);
        content.Children.Add(buttons);
        dialog.Content = content;
        var accepted = dialog.ShowDialog() == true;
        if (!accepted) ShowNavigationBlocked("Navigation blocked: " + workflow.Status);
        else
        {
            RefreshPeople();
            ShowStatus();
        }
        return accepted;
    }
}
