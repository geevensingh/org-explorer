using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow : Window
{
    private readonly IApplicationWorkflow workflow = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
    private Guid? preview;
    private string? selectedEmail;
    private bool loading;

    public MainWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        Title = $"Org explorer {version} (Windows x64 / win)";
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
        try { WorkspaceLocation.RequireIndependent(WorkspacePath.Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = ex.Message;
            return;
        }
        workflow.OpenWorkspace(WorkspacePath.Text);
        preview = null;
        ApplyButton.IsEnabled = false;
        selectedEmail = null;
        loading = true;
        PreferredName.Clear();
        loading = false;
        PreferredName.IsEnabled = false;
        ProfileHeading.Text = DirectoryName.Text = Email.Text = Manager.Text = "";
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
        loading = true;
        PreferredName.Text = person.PreferredName ?? "";
        loading = false;
        PreferredName.IsEnabled = true;
        ShowPerson(person);
    }
    private void EditPreferredName(object sender, TextChangedEventArgs e)
    {
        if (loading || selectedEmail is null) return;
        if (workflow.EditPreferredName(selectedEmail, PreferredName.Text.Length == 0 ? null : PreferredName.Text))
        {
            ShowPerson(workflow.People.Single(p => p.Email == selectedEmail));
            RefreshPeople();
        }
        ApplyButton.IsEnabled = false;
        preview = null;
        ShowStatus();
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
    }
    private void ShowStatus() => StatusText.Text = workflow.Status;
}
