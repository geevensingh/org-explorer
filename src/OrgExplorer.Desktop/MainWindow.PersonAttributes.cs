using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<PersonTextField, string?> unsavedAttributes = [];
    private readonly Dictionary<PersonTextField, PersonProfile> displayedAttributeBases = [];
    private readonly Dictionary<PersonTextField, PersonProfile> profileDraftBases = [];
    private readonly System.Windows.Threading.DispatcherTimer profileSaveTimer =
        new() { Interval = TimeSpan.FromMilliseconds(300) };
    private TextBox[] PersonEditors =>
        [DirectoryNameEditor, PreferredName, Alias, JobTitle, Department, OfficeBuilding,
         OfficeNumber, OfficeState, OfficeCountry, LinkedInProfileUrl];

    private void ClearPersonEditors()
    {
        loading = true;
        displayedAttributeBases.Clear();
        profileDraftBases.Clear();
        foreach (var editor in PersonEditors) editor.Clear();
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
        DirectoryNameEditor.Text = person.DirectoryName ?? "";
        LoadPreferredName(person);
        Alias.Text = person.Alias ?? "";
        JobTitle.Text = person.JobTitle ?? "";
        Department.Text = person.Department ?? "";
        OfficeBuilding.Text = person.OfficeBuilding ?? "";
        OfficeNumber.Text = person.OfficeNumber ?? "";
        OfficeState.Text = person.OfficeState ?? "";
        OfficeCountry.Text = person.OfficeCountry ?? "";
        LinkedInProfileUrl.Text = person.LinkedInProfileUrl ?? "";
        foreach (var field in Enum.GetValues<PersonTextField>()) displayedAttributeBases[field] = person;
        AttributeEditors.IsEnabled = true;
        OpenLinkedInButton.IsEnabled = person.LinkedInProfileUrl is not null;
        OpenLinkedInButton.ToolTip = person.LinkedInProfileUrl;
        loading = false;
    }

    private void EditPersonAttribute(object sender, TextChangedEventArgs e)
    {
        if (loading || selectedEmail is null || sender is not TextBox editor) return;
        var field = Enum.Parse<PersonTextField>((string)editor.Tag);
        var value = editor.Text.Length == 0 ? null : editor.Text;
        profileDraftBases.TryAdd(field, displayedAttributeBases[field]);
        unsavedAttributes[field] = value;
        if (field == PersonTextField.LinkedInProfileUrl) OpenLinkedInButton.IsEnabled = false;
        profileSaveTimer.Stop();
        profileSaveTimer.Start();
        MarkDraftPending();
        ApplyButton.IsEnabled = false;
        preview = null;
        StatusText.Text = "Pending local profile edits — not yet saved.";
    }

    private async void SaveProfileDraft(object? sender, EventArgs e) => await FlushProfileDraft();

    private async Task FlushProfileDraft()
    {
        profileSaveTimer.Stop();
        if (selectedEmail is null || unsavedAttributes.Count == 0) return;
        if (workPending) { profileSaveTimer.Start(); return; }
        var failed = false;
        foreach (var (field, value) in unsavedAttributes.ToArray())
        {
            var basis = profileDraftBases[field];
            var accepted = await RunWorkflowAsync(() => workflow.SavePersonTextDraft(basis, field, value),
                "Saving local profile…", keepProfileDraft: true);
            if (accepted is not null)
            {
                displayedAttributeBases[field] = profileDraftBases[field] = accepted;
                if (unsavedAttributes.TryGetValue(field, out var current) && current == value)
                {
                    unsavedAttributes.Remove(field);
                    profileDraftBases.Remove(field);
                }
            }
            else { failed = true; break; }
        }
        if (unsavedAttributes.Count == 0) RefreshPeople();
        ShowStatus();
        if (!failed && unsavedAttributes.Count > 0)
        {
            profileSaveTimer.Start();
            MarkDraftPending();
            StatusText.Text = "Pending newer local profile edits — not yet saved.";
        }
    }

    private async void RetryProfileSave(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null) return;
        await FlushProfileDraft();
        if (workPending) return;
        await RetryRelationshipSave();
        if (unsavedAttributes.Count == 0 && !HasUnsavedRelationship) RefreshPeople();
        ShowStatus();
    }

    private void DiscardProfileDraft(object sender, RoutedEventArgs e)
    {
        if (workPending)
        {
            ShowNavigationBlocked("Discard blocked: local work is in progress. Please wait for it to finish.");
            return;
        }
        unsavedAttributes.Clear();
        profileDraftBases.Clear();
        profileSaveTimer.Stop();
        unsavedArchived = null;
        unsavedManager = null;
        unsavedFavorite = null;
        workflow.DiscardUnsavedChanges();
        PersonNotes.DiscardLocalDraft();
        TeamNotes.DiscardLocalDraft();
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
