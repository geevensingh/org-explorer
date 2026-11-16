using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer profileCopyTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? profileCopyError;

    private void InitializeProfileCopyFeedback()
    {
        profileCopyTimer.Tick += (_, _) =>
        {
            profileCopyTimer.Stop();
            ProfileCopyFeedback.Visibility = Visibility.Collapsed;
            ProfileCopyFeedback.Text = "";
        };
        Closed += (_, _) => profileCopyTimer.Stop();
    }

    private void BeginProfileEdit(object sender, RoutedEventArgs e)
    {
        if (workPending || selectedEmail is null) return;
        if (workflow.BeginProfileEdit(selectedEmail))
        {
            LoadPersonEditors(workflow.ProfileEdit!.Draft);
            ShowPersonArchival(workflow.ProfileEdit.Draft);
            FocusProfileControl(editing: true);
        }
        ShowStatus();
    }

    private async void SaveProfileEdit(object sender, RoutedEventArgs e) => await SaveProfileSessionAsync();

    private async Task<bool> SaveProfileSessionAsync()
    {
        if (workPending) return false;
        var saved = await RunWorkflowAsync(workflow.SaveProfileEdit, "Saving complete profile…");
        RefreshExternalWorkspace();
        if (saved) FocusProfileControl(editing: false);
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
        FocusProfileControl(editing: false);
    }

    private void FocusProfileControl(bool editing)
    {
        var owner = selectedEmail;
        Dispatcher.BeginInvoke(() =>
        {
            if (owner != selectedEmail || (workflow.ProfileEdit is not null) != editing ||
                ExplorerPanel.Visibility != Visibility.Visible) return;
            Control target = editing ? DirectoryNameEditor : EditProfileButton;
            target.BringIntoView();
            FocusManager.SetFocusedElement(this, target);
            if (IsVisible) target.Focus();
        }, DispatcherPriority.Input);
    }

    private void UpdateProfileMode()
    {
        var editing = workflow.ProfileEdit is not null;
        var writable = workflow.IsWorkspaceCurrent && selectedEmail is not null && !workPending;
        PersonIdentity.Margin = new Thickness(0, 8, 12, editing ? 8 : 16);
        EditProfileButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditProfileButton.IsEnabled = writable;
        ProfileSessionActions.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        SaveProfileButton.IsEnabled = writable;
        CancelProfileButton.IsEnabled = !workPending;
        ProfileForm.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        AttributeEditors.Visibility = ManagerEditor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        AttributeEditors.IsEnabled = ManagerEditor.IsEnabled = writable && editing;
        ProfileValues.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        PlaceProfileElement(OpenLinkedInButton, editing ? LinkedInEditLink : LinkedInProfileUrlValueRow);
        PlaceProfileElement(Photo.EditActions, ProfilePhotoActions);
        PlaceProfileElement(TeamNameContext, editing ? TeamNameEditSlot : TeamNameReadingSlot);
        var canEditTeamName = selectedEmail is not null && workflow.CanEditTeamName(selectedEmail);
        var retainedTeamNameDraft = workflow.ProfileEdit is { } edit && edit.Draft.TeamName != edit.Basis.TeamName;
        TeamNameEditorRow.Visibility = editing && (canEditTeamName || retainedTeamNameDraft)
            ? Visibility.Visible : Visibility.Collapsed;
        TeamNameEditor.IsEnabled = writable && editing && canEditTeamName;
        Photo.CanEdit = writable && editing;
        ProfilePhotoDraft.Visibility = workflow.ProfileEdit is { PhotoChanged: true } ? Visibility.Visible : Visibility.Collapsed;
        ProfilePhotoDraft.Text = workflow.ProfileEdit?.PhotoLocalPath is { } path
            ? $"Staged photo: {path}. The saved photo remains shown until Save."
            : "Photo clear staged. The saved photo remains shown until Save.";
        PreferredNameContext.IsEnabled = writable;
        UpdatePreferredNameActions();
    }

    private static void PlaceProfileElement(FrameworkElement element, Panel host)
    {
        if (ReferenceEquals(element.Parent, host)) return;
        if (element.Parent is Panel previous) previous.Children.Remove(element);
        host.Children.Add(element);
    }

    private void ShowProfileValues(PersonProfile? person)
    {
        var names = person is null ? [] : workflow.GetTeamNames(person.Email);
        // Replacing equal items can leave WPF's cached item peer pointing at a detached TextBlock.
        if (TeamNames.ItemsSource is not ObservableCollection<TeamNameItem> displayed)
            TeamNames.ItemsSource = displayed = [];
        var childrenChanged = displayed.Count != names.Count;
        for (var index = 0; index < names.Count; index++)
        {
            if (index == displayed.Count) displayed.Add(new(names[index]));
            else displayed[index].Show(names[index]);
        }
        while (displayed.Count > names.Count) displayed.RemoveAt(displayed.Count - 1);
        // Drop removed UIA children after WPF has generated the current containers.
        if (childrenChanged)
            Dispatcher.BeginInvoke(() => UIElementAutomationPeer.FromElement(TeamNames)?.ResetChildrenCache(),
                DispatcherPriority.Loaded);
        TeamNameEmpty.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowProfileValue(AliasValueRow, AliasValue, person?.Alias);
        ShowProfileValue(JobTitleValueRow, JobTitleValue, person?.JobTitle, reserveSlot: false);
        ShowProfileValue(DepartmentValueRow, DepartmentValue, person?.Department);
        ShowProfileValue(OfficeReadingSlot, OfficeBuildingValue, person?.OfficeBuilding);
        ShowProfileValue(OfficeReadingSlot, OfficeNumberValue, person?.OfficeNumber);
        OfficeBuildingValue.Visibility = person?.OfficeBuilding is not null || person?.OfficeNumber is null
            ? Visibility.Visible : Visibility.Collapsed;
        OfficeNumberValue.Visibility = person?.OfficeNumber is not null ? Visibility.Visible : Visibility.Collapsed;
        OfficeSeparator.Visibility = person?.OfficeBuilding is not null && person.OfficeNumber is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ShowProfileValue(OfficeStateValueRow, OfficeStateValue, person?.OfficeState);
        ShowProfileValue(OfficeCountryValueRow, OfficeCountryValue, person?.OfficeCountry);
        ShowProfileValue(LinkedInProfileUrlValueRow, LinkedInProfileUrlValue, person?.LinkedInProfileUrl);
        DirectoryName.Text = person?.DirectoryName ?? "—";
        DirectoryName.CopyText = person?.DirectoryName;
        Email.Text = person?.Email ?? "";
        Email.CopyText = person?.Email;
        OpenLinkedInButton.Visibility = person?.LinkedInProfileUrl is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class TeamNameItem(TeamNameSegment segment) : INotifyPropertyChanged
    {
        public TeamNameSegment Segment { get; private set; } = segment;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Show(TeamNameSegment value)
        {
            if (Segment == value) return;
            Segment = value;
            PropertyChanged?.Invoke(this, new(nameof(Segment)));
        }
    }

    private void StageTeamName(object sender, TextChangedEventArgs e)
    {
        if (loading || selectedEmail is null) return;
        workflow.StageProfileTeamName(TeamNameEditor.Text.Length == 0 ? null : TeamNameEditor.Text);
        MarkDraftPending();
        InvalidateRelationshipPreview();
    }

    private void ShowTeamNameOwner(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBlock { ToolTip: ToolTip tooltip } segment)
        {
            tooltip.PlacementTarget = segment;
            tooltip.IsOpen = true;
        }
    }

    private void HideTeamNameOwner(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBlock { ToolTip: ToolTip tooltip }) tooltip.IsOpen = false;
    }

    private static void ShowProfileValue(FrameworkElement row, ProfileCopyText value, string? text, bool reserveSlot = true)
    {
        value.Text = text ?? "—";
        value.CopyText = text;
        row.Visibility = reserveSlot || text is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyProfileValue(object sender, RoutedEventArgs e)
    {
        if (sender is ProfileCopyText target)
        {
            if (!target.IsEnabled || target.CopyText is not { Length: > 0 } value) return;
            profileCopyTimer.Stop();
            ProfileCopyFeedback.Visibility = Visibility.Collapsed;
            ProfileCopyFeedback.Text = "";
            try { writeClipboard(value); }
            catch (System.Runtime.InteropServices.ExternalException error)
            {
                profileCopyError = "Copy failed. Retry when the clipboard is available: " + error.Message;
                ShowOperationalNotice();
                return;
            }
            profileCopyError = null;
            ShowOperationalNotice();
            ProfileCopyFeedback.Text = target.CopyLabel + " copied.";
            ProfileCopyFeedback.Visibility = Visibility.Visible;
            profileCopyTimer.Start();
            UIElementAutomationPeer.CreatePeerForElement(ProfileCopyFeedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            e.Handled = true;
            return;
        }
        if (sender is not MenuItem { Tag: string legacyValue }) return;
        try { writeClipboard(legacyValue); }
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
