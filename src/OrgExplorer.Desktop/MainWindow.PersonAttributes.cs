using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private TextBox[] PersonEditors =>
        [DirectoryNameEditor, PreferredName, Alias, JobTitle, Department, OfficeBuilding,
         OfficeNumber, OfficeState, OfficeCountry, LinkedInProfileUrl];

    private void ClearPersonEditors()
    {
        loading = true;
        workflow.CancelProfileEdit();
        FinishPreferredNameEntry();
        foreach (var editor in PersonEditors) editor.Clear();
        ShowProfileValues(null);
        preferredNameOwner = null;
        displayedPreferredNameBase = null;
        System.Windows.Automation.AutomationProperties.SetName(EditPreferredNameButton, "Add preferred name");
        PreferredNameContext.IsEnabled = false;
        AttributeEditors.IsEnabled = false;
        OpenLinkedInButton.IsEnabled = false;
        loading = false;
    }

    private void LoadPersonEditors(PersonProfile person)
    {
        loading = true;
        var draft = workflow.ProfileEdit?.Draft ?? person;
        DirectoryNameEditor.Text = draft.DirectoryName ?? "";
        LoadPreferredName(person);
        Alias.Text = draft.Alias ?? "";
        JobTitle.Text = draft.JobTitle ?? "";
        Department.Text = draft.Department ?? "";
        OfficeBuilding.Text = draft.OfficeBuilding ?? "";
        OfficeNumber.Text = draft.OfficeNumber ?? "";
        OfficeState.Text = draft.OfficeState ?? "";
        OfficeCountry.Text = draft.OfficeCountry ?? "";
        LinkedInProfileUrl.Text = draft.LinkedInProfileUrl ?? "";
        AttributeEditors.IsEnabled = true;
        OpenLinkedInButton.IsEnabled = person.LinkedInProfileUrl is not null;
        OpenLinkedInButton.ToolTip = person.LinkedInProfileUrl;
        loading = false;
        ShowProfileValues(person);
        UpdateProfileMode();
    }

    private void EditPersonAttribute(object sender, TextChangedEventArgs e)
    {
        if (loading || selectedEmail is null || sender is not TextBox editor) return;
        var field = Enum.Parse<PersonTextField>((string)editor.Tag);
        var value = editor.Text.Length == 0 ? null : editor.Text;
        if (field != PersonTextField.PreferredName)
        {
            workflow.StageProfileText(field, value);
            MarkDraftPending();
            ApplyButton.IsEnabled = false;
            preview = null;
            ShowStatus();
            return;
        }
        if (!preferredNameEditing) return;
        preferredNameDirty = editor.Text != (preferredNameBasis?.PreferredName ?? "");
        MarkDraftPending();
        ApplyButton.IsEnabled = false;
        preview = null;
        StatusText.Text = "Preferred name is not saved. Choose Save or Cancel.";
        UpdatePreferredNameActions();
    }

    private async void RetryProfileSave(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null) return;
        if (workflow.ProfileEdit is not null)
        {
            await SaveProfileSessionAsync();
            return;
        }
        await RetryRelationshipSave();
        if (!HasUnsavedRelationship) RefreshPeople();
        ShowStatus();
    }

    private void DiscardProfileDraft(object sender, RoutedEventArgs e)
    {
        if (workPending)
        {
            ShowNavigationBlocked("Discard blocked: local work is in progress. Please wait for it to finish.");
            return;
        }
        if (workflow.ProfileEdit is not null)
        {
            CancelProfileEdit(sender, e);
            return;
        }
        CancelPreferredNameEntry();
        unsavedArchived = null;
        unsavedFavorite = null;
        if (selectedEmail is not null) workflow.DiscardPersonChanges(selectedEmail);
        RefreshPeople();
        ShowStatus();
    }

    private void OpenLinkedIn(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is not null) workflow.OpenLinkedInProfile(selectedEmail);
        ShowStatus();
    }
}

internal sealed class BrowserProfileLinkOpener : IProfileLinkOpener
{
    public void Open(Uri profileUrl) =>
        Process.Start(new ProcessStartInfo(profileUrl.AbsoluteUri) { UseShellExecute = true });
}
