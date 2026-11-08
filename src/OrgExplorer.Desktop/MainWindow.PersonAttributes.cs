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
        foreach (var editor in PersonEditors) editor.Clear();
        AttributeEditors.IsEnabled = false;
        OpenLinkedInButton.IsEnabled = false;
        loading = false;
    }

    private void LoadPersonEditors(PersonProfile person)
    {
        loading = true;
        DirectoryNameEditor.Text = person.DirectoryName ?? "";
        PreferredName.Text = person.PreferredName ?? "";
        Alias.Text = person.Alias ?? "";
        JobTitle.Text = person.JobTitle ?? "";
        Department.Text = person.Department ?? "";
        OfficeBuilding.Text = person.OfficeBuilding ?? "";
        OfficeNumber.Text = person.OfficeNumber ?? "";
        OfficeState.Text = person.OfficeState ?? "";
        OfficeCountry.Text = person.OfficeCountry ?? "";
        LinkedInProfileUrl.Text = person.LinkedInProfileUrl ?? "";
        AttributeEditors.IsEnabled = true;
        OpenLinkedInButton.IsEnabled = person.LinkedInProfileUrl is not null;
        OpenLinkedInButton.ToolTip = person.LinkedInProfileUrl;
        loading = false;
    }

    private void EditPersonAttribute(object sender, TextChangedEventArgs e)
    {
        if (loading || selectedEmail is null || sender is not TextBox editor) return;
        var field = Enum.Parse<PersonTextField>((string)editor.Tag);
        if (workflow.EditPersonText(selectedEmail, field, editor.Text.Length == 0 ? null : editor.Text))
        {
            ShowPerson(workflow.People.Single(p => p.Email == selectedEmail));
            RefreshPeople();
        }
        else if (field == PersonTextField.LinkedInProfileUrl) OpenLinkedInButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        preview = null;
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
