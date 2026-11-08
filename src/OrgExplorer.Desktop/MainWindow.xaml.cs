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
    private readonly HashSet<string> expandedPeople = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        PersonNotes.Attach(workflow, () =>
        {
            ApplyButton.IsEnabled = false;
            preview = null;
            ShowStatus();
        });
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
        if (!PersonNotes.TryLeave()) return;
        workflow.OpenWorkspace(WorkspacePath.Text);
        PersonNotes.OpenPerson(null);
        preview = null;
        ApplyButton.IsEnabled = false;
        selectedEmail = null;
        expandedPeople.Clear();
        ClearPersonEditors();
        ProfileHeading.Text = DirectoryName.Text = Email.Text = "";
        Manager.Content = "No Person selected";
        Manager.Tag = null;
        Manager.IsEnabled = false;
        ReportsList.ItemsSource = null;
        ReportCount.Text = "Imported reports (not a full-company count)";
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
    private void SelectPerson(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (loading || e.NewValue is not ReportingNode node) return;
        if (!OpenPerson(node.Person)) RestorePersonSelection();
    }
    private void RestorePersonSelection()
    {
        loading = true;
        try
        {
            var parents = new Stack<ItemsControl>();
            parents.Push(PeopleList);
            while (parents.TryPop(out var parent))
                foreach (var item in parent.Items)
                    if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
                    {
                        if (container.DataContext is ReportingNode node &&
                            string.Equals(node.Person.Email, selectedEmail, StringComparison.OrdinalIgnoreCase))
                        {
                            // Keep the invoking automation peer alive while rejecting navigation.
                            container.IsSelected = true;
                            return;
                        }
                        parents.Push(container);
                    }
        }
        finally { loading = false; }
    }
    private bool OpenPerson(PersonProfile person)
    {
        if (!string.Equals(selectedEmail, person.Email, StringComparison.OrdinalIgnoreCase) && !PersonNotes.TryLeave())
            return false;
        selectedEmail = person.Email;
        LoadPersonEditors(person);
        ShowPerson(person);
        PersonNotes.OpenPerson(person.Email);
        return true;
    }
    private void RefreshPeople()
    {
        loading = true;
        PeopleList.ItemsSource = workflow.ReportingRoots;
        loading = false;
        if (selectedEmail is not null && workflow.GetRelationships(selectedEmail) is { } context)
            OpenPerson(context.Person);
    }
    private void ShowPerson(PersonProfile person)
    {
        ProfileHeading.Text = person.Heading;
        DirectoryName.Text = person.DirectoryName ?? "(unknown)";
        Email.Text = person.Email;
        Photo.Show(workflow.GetPhoto(person.Email));
        Photo.CanEdit = true;
        var context = workflow.GetRelationships(person.Email)!;
        Manager.Content = "Manager: " + (context.Manager?.Heading ?? "(explicit no-Manager)");
        Manager.Tag = context.Manager?.Email;
        Manager.IsEnabled = context.Manager is not null;
        ReportsList.ItemsSource = context.ImportedReports;
        ReportCount.Text = $"Imported reports: {context.ImportedReports.Count} (selected population, not a full-company count)";
    }
    private void NavigateManager(object sender, RoutedEventArgs e)
    {
        if (Manager.Tag is string email) NavigateTo(email);
    }
    private void NavigateReport(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && ReportsList.SelectedItem is PersonProfile report) NavigateTo(report.Email);
    }
    private void NavigateTo(string email)
    {
        var context = workflow.GetRelationships(email);
        if (context is null) return;
        if (!OpenPerson(context.Person))
        {
            RefreshPeople();
            return;
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (context.Manager is { } manager && visited.Add(manager.Email))
        {
            expandedPeople.Add(manager.Email);
            context = workflow.GetRelationships(manager.Email)!;
        }
        RefreshPeople();
    }
    private void RestoreTreeItem(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.DataContext is not ReportingNode node) return;
        item.IsExpanded = expandedPeople.Contains(node.Person.Email);
        item.IsSelected = string.Equals(node.Person.Email, selectedEmail, StringComparison.OrdinalIgnoreCase);
    }
    private void ExpandPerson(object sender, RoutedEventArgs e)
    {
        if (!loading && e.OriginalSource is TreeViewItem { DataContext: ReportingNode node }) expandedPeople.Add(node.Person.Email);
    }
    private void CollapsePerson(object sender, RoutedEventArgs e)
    {
        if (!loading && e.OriginalSource is TreeViewItem { DataContext: ReportingNode node }) expandedPeople.Remove(node.Person.Email);
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
