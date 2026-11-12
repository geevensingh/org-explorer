using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<PersonTextField, string?> unsavedPreferredNameEdits = [];
    private readonly Dictionary<PersonTextField, PersonProfile> displayedAttributeBases = [];
    private readonly Dictionary<PersonTextField, PersonProfile> preferredNameDraftBases = [];
    private readonly System.Windows.Threading.DispatcherTimer preferredNameSaveTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };
    private TextBox[] PersonEditors =>
        [DirectoryNameEditor, PreferredName, Alias, JobTitle, Department, OfficeBuilding,
         OfficeNumber, OfficeState, OfficeCountry, LinkedInProfileUrl];

    private void ClearPersonEditors()
    {
        loading = true;
        workflow.CancelProfileEdit();
        displayedAttributeBases.Clear();
        preferredNameDraftBases.Clear();
        foreach (var editor in PersonEditors) editor.Clear();
        ShowProfileValues(null);
        preferredNameOwner = null;
        PreferredNameValue.Text = "";
        EditPreferredNameButton.Content = "Add preferred name";
        SetPreferredNameEditing(false);
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
        foreach (var field in Enum.GetValues<PersonTextField>())
            if (!unsavedPreferredNameEdits.ContainsKey(field)) displayedAttributeBases[field] = person;
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
        preferredNameDraftBases.TryAdd(field, displayedAttributeBases[field]);
        unsavedPreferredNameEdits[field] = value;
        if (field == PersonTextField.LinkedInProfileUrl) OpenLinkedInButton.IsEnabled = false;
        preferredNameSaveTimer.Stop();
        if (workflow.GetWorkspaceConflicts().Count != 0) { ShowStatus(); return; }
        preferredNameSaveTimer.Start();
        MarkDraftPending();
        ApplyButton.IsEnabled = false;
        preview = null;
        StatusText.Text = "Pending local profile edits — not yet saved.";
    }

    private async void SavePreferredNameDraft(object? sender, EventArgs e) => await FlushPreferredNameDraft();

    private async Task FlushPreferredNameDraft()
    {
        preferredNameSaveTimer.Stop();
        if (workflow.GetWorkspaceConflicts().Count != 0) { ShowStatus(); return; }
        if (selectedEmail is null || unsavedPreferredNameEdits.Count == 0) return;
        if (workPending) { preferredNameSaveTimer.Start(); return; }
        var failed = false;
        foreach (var (field, value) in unsavedPreferredNameEdits.ToArray())
        {
            var basis = preferredNameDraftBases[field];
            var accepted = await RunWorkflowAsync(() => workflow.SavePersonTextDraft(basis, field, value),
                "Saving local profile…", keepPreferredNameDraft: true);
            if (accepted is not null)
            {
                displayedAttributeBases[field] = preferredNameDraftBases[field] = accepted;
                if (unsavedPreferredNameEdits.TryGetValue(field, out var current) && current == value)
                {
                    unsavedPreferredNameEdits.Remove(field);
                    preferredNameDraftBases.Remove(field);
                }
            }
            else { failed = true; break; }
        }
        if (unsavedPreferredNameEdits.Count == 0) RefreshPeople();
        ShowStatus();
        if (!failed && unsavedPreferredNameEdits.Count > 0 && workflow.GetWorkspaceConflicts().Count == 0)
        {
            preferredNameSaveTimer.Start();
            MarkDraftPending();
            StatusText.Text = "Pending newer local profile edits — not yet saved.";
        }
    }

    private async void RetryProfileSave(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null) return;
        if (workflow.ProfileEdit is not null)
        {
            await SaveProfileSessionAsync();
            return;
        }
        await FlushPreferredNameDraft();
        if (workPending) return;
        await RetryRelationshipSave();
        if (unsavedPreferredNameEdits.Count == 0 && !HasUnsavedRelationship) RefreshPeople();
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
        unsavedPreferredNameEdits.Clear();
        preferredNameDraftBases.Clear();
        preferredNameSaveTimer.Stop();
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
