using System.IO;
using System.Reflection;
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
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TreeViewItem, object> treeRestorers = new();

    public MainWindow()
    {
        InitializeComponent();
        InitializeUpdates();
        profileSaveTimer.Tick += SaveProfileDraft;
        SettingsGuidance.Text = workflow.Compatibility.Guidance;
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        Title = $"Org explorer {version} (Windows x64 / win)";
        PersonNotes.Attach(workflow, () =>
        {
            ApplyButton.IsEnabled = false;
            preview = null;
            ShowStatus();
        });
        AttachTeamNotes();
        Photo.PhotoEdited += EditPersonPhoto;
        workflow.WorkspaceChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (workPending) externalRefreshPending = true;
            else RefreshExternalWorkspace();
        });
        Closing += (_, e) =>
        {
            if (updateHandoffPending || workPending || unsavedAttributes.Count > 0 || HasUnsavedRelationship || !workflow.CanSafelyClose)
            {
                e.Cancel = true;
                StatusText.Text = updateHandoffPending ? "Safe update handoff is in progress; wait for restart or a visible failure." :
                    workPending ? "Local work is still pending; closing waits for it to finish." :
                    workflow.Status + "\nUnsaved local changes prevent safe closing. Retry or deliberately discard.";
            }
        };
        Closed += (_, _) => { profileSaveTimer.Stop(); workflow.Dispose(); };
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
    private async void OpenWorkspace(object sender, RoutedEventArgs e)
        => await OpenWorkspaceAtPathAsync(WorkspacePath.Text);

    private void PresentWorkspaceOpen()
    {
        PersonNotes.OpenPerson(null);
        TeamNotes.OpenPerson(null);
        preview = null;
        ApplyButton.IsEnabled = false;
        selectedEmail = null;
        ClearSearchContext();
        FavoriteButton.IsEnabled = false;
        FavoriteStatus.Text = "Select a Person.";
        expandedPeople.Clear();
        ClearPersonEditors();
        ClearPersonArchival();
        ShowProfileTimeline(null);
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
    private async void PreviewImport(object sender, RoutedEventArgs e)
    {
        preview = null;
        ApplyButton.IsEnabled = false;
        try
        {
            var path = ImportPath.Text;
            var result = await RunWorkflowAsync(() => workflow.PreviewImportFile(path), "Validating Import preview…");
            var messages = result.Errors.Concat(result.Warnings).Concat(result.Changes).ToArray();
            PreviewText.ItemsSource = messages;
            System.Windows.Automation.AutomationProperties.SetName(PreviewText, string.Join("\n", messages));
            preview = result.CanApply ? result.Id : null;
            ApplyButton.IsEnabled = result.CanApply;
            ShowStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "Import file could not be read: " + ex.Message;
        }
    }
    private async void ApplyImport(object sender, RoutedEventArgs e)
    {
        if (preview is { } id) await RunWorkflowAsync(() => workflow.ApplyImport(id), "Applying reviewed Import…");
        ApplyButton.IsEnabled = false;
        preview = null;
        RefreshPeople();
        ShowStatus();
    }
    private void SelectPerson(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (loading || e.NewValue is not ReportingNode node) return;
        if (!OpenPerson(node.Person)) RestorePersonSelection();
        else RefreshArchivedPeople();
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
                        container.IsSelected = false;
                        parents.Push(container);
                    }
        }
        finally { loading = false; }
    }
    private bool OpenPerson(PersonProfile person)
    {
        if ((workPending || !string.Equals(selectedEmail, person.Email, StringComparison.OrdinalIgnoreCase)) && !TryLeaveLocalEdits())
            return false;
        selectedEmail = person.Email;
        if (selectedMatch is not null && !string.Equals(selectedMatch.Person.Email, selectedEmail, StringComparison.OrdinalIgnoreCase))
            ClearSearchContext();
        if (unsavedAttributes.Count == 0) LoadPersonEditors(person);
        ShowPerson(person);
        PersonNotes.OpenPerson(person.Email);
        TeamNotes.OpenPerson(person.Email);
        RefreshSearchAndFavorites();
        return true;
    }
    private void RefreshPeople()
    {
        loading = true;
        var roots = workflow.ReportingRoots;
        if (!ReferenceEquals(PeopleList.ItemsSource, roots)) PeopleList.ItemsSource = roots;
        RefreshArchivedPeople();
        loading = false;
        if (selectedEmail is not null && workflow.GetRelationships(selectedEmail) is { } context)
            OpenPerson(context.Person);
        else if (selectedEmail is not null && workflow.CanSafelyClose)
        {
            selectedEmail = null;
            ClearSearchContext();
            FavoriteButton.IsEnabled = false;
            FavoriteStatus.Text = "Person unavailable.";
            ClearPersonEditors();
            ClearPersonArchival();
            ProfileHeading.Text = "Selected Person unavailable in the current Workspace.";
            DirectoryName.Text = Email.Text = "";
            Manager.IsEnabled = false;
            Manager.Content = "Person unavailable";
            ReportsList.ItemsSource = null;
            Photo.Show(null);
            Photo.CanEdit = false;
            PersonNotes.OpenPerson(null);
            TeamNotes.OpenPerson(null);
            ShowProfileTimeline(null);
        }
    }
    private void ShowPerson(PersonProfile person)
    {
        ProfileHeading.Text = person.Heading;
        DirectoryName.Text = person.DirectoryName ?? "(unknown)";
        Email.Text = person.Email;
        ShowFavorite(person);
        ShowPersonArchival(person);
        Photo.Show(workflow.GetPhoto(person.Email));
        Photo.CanEdit = true;
        var context = workflow.GetRelationships(person.Email)!;
        Manager.Content = (person.Archived ? "Last stored Manager: " : "Manager: ") +
            (context.Manager is { } manager ? manager.Heading + (manager.Archived ? " — Archived" : "") : "(explicit no-Manager)");
        Manager.Tag = context.Manager?.Email;
        Manager.IsEnabled = context.Manager is not null;
        ReportsList.ItemsSource = context.ImportedReports;
        ReportCount.Text = $"Imported reports: {context.ImportedReports.Count} (selected population, not a full-company count)";
        ShowProfileTimeline(person.Email);
    }
    private void NavigateManager(object sender, RoutedEventArgs e)
    {
        if (Manager.Tag is string email) NavigateTo(email);
    }
    private void NavigateReport(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && ReportsList.SelectedItem is PersonProfile report) NavigateTo(report.Email);
    }
    private bool NavigateTo(string email)
    {
        var context = workflow.GetRelationships(email);
        if (context is null) return false;
        if (!OpenPerson(context.Person))
        {
            RefreshPeople();
            return false;
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (context.Manager is { } manager && visited.Add(manager.Email))
        {
            expandedPeople.Add(manager.Email);
            context = workflow.GetRelationships(manager.Email)!;
        }
        RefreshPeople();
        RestoreRealizedTreeState(PeopleList);
        return true;
    }
    private void RestoreRealizedTreeState(ItemsControl parent)
    {
        var parents = new Stack<ItemsControl>();
        parents.Push(parent);
        while (parents.TryPop(out var current))
            foreach (var item in current.Items)
                if (current.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
                {
                    RestoreTreeItem(container, null!);
                    if (container.IsExpanded) parents.Push(container);
                }
    }
    private void RestoreTreeItem(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.DataContext is not ReportingNode node) return;
        if (!treeRestorers.TryGetValue(item, out _))
        {
            treeRestorers.Add(item, new object());
            item.DataContextChanged += (_, _) => RestoreTreeItem(item, null!);
        }
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
    private async void EditPersonPhoto(string? localPath)
    {
        if (selectedEmail is null) return;
        var email = selectedEmail;
        if (await RunWorkflowAsync(() => workflow.EditPhoto(email, localPath), "Saving local photo…"))
            ShowPerson(workflow.People.Single(p => p.Email == selectedEmail));
        ApplyButton.IsEnabled = false;
        preview = null;
        ShowStatus();
    }
    private void RefreshExternalWorkspace()
    {
        queryDirty = true;
        if (workflow.IsWorkspaceCurrent) RefreshPeople();
        ApplyButton.IsEnabled = false;
        preview = null;
        AttributeEditors.IsEnabled = workflow.IsWorkspaceCurrent && selectedEmail is not null;
        Photo.CanEdit = workflow.IsWorkspaceCurrent && selectedEmail is not null;
        PersonNotes.RefreshExternalWorkspace();
        TeamNotes.RefreshExternalWorkspace();
        ShowStatus();
    }

    private void ShowStatus()
    {
        RefreshSearchAndFavorites();
        StatusText.Text = workflow.Status;
        SettingsGuidance.Text = workflow.Compatibility.Guidance;
        WorkspaceFreshness.Text = workflow.IsWorkspaceCurrent
            ? "Current validated local Workspace."
            : workflow.Compatibility.State == WorkspaceCompatibilityState.NotOpened || startupRestorePath is not null
                ? "No Workspace is open. Choose a folder or retry the previous Workspace."
                : "STALE / unavailable — displayed information is the last valid local state, not current data.";
        RetryProfileButton.IsEnabled = unsavedAttributes.Count > 0 || HasUnsavedRelationship;
        DiscardProfileButton.IsEnabled = workflow.LocalSaveError is not null;
        if (!workflow.IsWorkspaceCurrent)
        {
            ArchivePersonButton.IsEnabled = ReactivatePersonButton.IsEnabled = ManagerEditor.IsEnabled = false;
            AttributeEditors.IsEnabled = false;
            Photo.CanEdit = false;
            FavoriteButton.IsEnabled = false;
        }
    }

    private bool TryLeaveLocalEdits()
    {
        if (workPending)
        {
            ShowNavigationBlocked("Navigation blocked: local work is in progress. Please wait for it to finish.");
            return false;
        }
        if (!TryLeaveNotes()) return false;
        if (unsavedAttributes.Count == 0 && !HasUnsavedRelationship) return true;
        ShowNavigationBlocked(workflow.LocalSaveError is null
            ? "Navigation blocked: profile edits are pending. Please wait for saving to finish before leaving."
            : "Navigation blocked: profile edits were NOT saved. Retry or discard unsaved profile edits before leaving.");
        return false;
    }

    internal void ShowNavigationBlocked(string message) => StatusText.Text = message;
}
