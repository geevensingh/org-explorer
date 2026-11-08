using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow : Window
{
    private readonly IApplicationWorkflow workflow = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(new BrowserProfileLinkOpener()));
    private Guid? preview;
    private string? selectedEmail;
    private bool loading;

    public MainWindow()
    {
        InitializeComponent();
        Photo.PhotoEdited += EditPersonPhoto;
    }
    private void ChooseWorkspace(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a local Workspace folder" };
        if (dialog.ShowDialog(this) == true) WorkspacePath.Text = dialog.FolderName;
    }
    private void ChooseImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Import JSON|*.json", Title = "Choose a version-1 Import batch" };
        if (dialog.ShowDialog(this) == true) ImportPath.Text = dialog.FileName;
    }
    private void OpenWorkspace(object sender, RoutedEventArgs e)
    {
        workflow.OpenWorkspace(WorkspacePath.Text);
        preview = null;
        ApplyButton.IsEnabled = false;
        selectedEmail = null;
        ClearPersonEditors();
        ProfileHeading.Text = DirectoryName.Text = Email.Text = Manager.Text = "";
        Photo.Show(null);
        Photo.CanEdit = false;
        RefreshPeople();
        ShowStatus();
    }
    private void PreviewImport(object sender, RoutedEventArgs e)
    {
        preview = null;
        ApplyButton.IsEnabled = false;
        try
        {
            var result = workflow.PreviewImportFile(ImportPath.Text);
            PreviewText.Text = string.Join("\n", result.Errors.Concat(result.Changes));
            preview = result.CanApply ? result.Id : null;
            ApplyButton.IsEnabled = result.CanApply;
            ShowStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "Import file could not be read: " + ex.Message;
        }
    }
    private void ApplyImport(object sender, RoutedEventArgs e)
    {
        if (preview is { } id) workflow.ApplyImport(id);
        ApplyButton.IsEnabled = false;
        preview = null;
        RefreshPeople();
        ShowStatus();
    }
    private void SelectPerson(object sender, SelectionChangedEventArgs e)
    {
        if (PeopleList.SelectedItem is not PersonProfile person) return;
        selectedEmail = person.Email;
        LoadPersonEditors(person);
        ShowPerson(person);
    }
    private void RefreshPeople()
    {
        loading = true;
        PeopleList.ItemsSource = workflow.People;
        PeopleList.SelectedItem = workflow.People.FirstOrDefault(p => p.Email == selectedEmail);
        loading = false;
    }
    private void ShowPerson(PersonProfile person)
    {
        ProfileHeading.Text = person.Heading;
        DirectoryName.Text = person.DirectoryName ?? "(unknown)";
        Email.Text = person.Email;
        Manager.Text = "Manager: " + (person.ManagerEmail ?? "(explicit no-Manager)");
        Photo.Show(workflow.GetPhoto(person.Email));
        Photo.CanEdit = true;
    }
    private void EditPersonPhoto(string? localPath)
    {
        if (selectedEmail is null) return;
        if (workflow.EditPhoto(selectedEmail, localPath))
            ShowPerson(workflow.People.Single(p => p.Email == selectedEmail));
        ApplyButton.IsEnabled = false;
        preview = null;
        ShowStatus();
    }
    private void ShowStatus() => StatusText.Text = workflow.Status;
}
