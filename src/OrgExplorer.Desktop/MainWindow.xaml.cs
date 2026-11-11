using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow : Window
{
    private readonly WorkspaceViewModel workflow = new(ApplicationWorkflow.CreateFileBacked(new BrowserProfileLinkOpener()));
    private Guid? preview;
    private string? selectedEmail;
    private bool loading;
    private readonly HashSet<string> expandedPeople = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TreeViewItem, object> treeRestorers = new();

    public MainWindow()
    {
        InitializeComponent();
        InitializeUpdates();
        preferredNameSaveTimer.Tick += SavePreferredNameDraft;
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
            if (updateHandoffPending || !TryLeaveLocalEdits() || !workflow.CanSafelyClose)
            {
                e.Cancel = true;
                StatusText.Text = updateHandoffPending ? "Safe update handoff is in progress; wait for restart or a visible failure." :
                    workPending ? "Local work is still pending; closing waits for it to finish." :
                    workflow.Status + "\nResolve unsaved drafts with retry or deliberate discard. For committed storage maintenance, use Settings → Retry Workspace validation / storage maintenance; do not replay the edit.";
            }
        };
        Closed += (_, _) => { preferredNameSaveTimer.Stop(); workflow.Dispose(); };
    }
    private void ChooseWorkspace(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a local Workspace folder" };
        if (dialog.ShowDialog(this) == true) WorkspacePath.Text = dialog.FolderName;
    }
    private void ChooseImport(object sender, RoutedEventArgs e)
    {
        if (!TryLeaveLocalEdits()) return;
        var dialog = new OpenFileDialog { Filter = "Import JSON|*.json", Title = "Choose a version-1 Import batch" };
        if (dialog.ShowDialog(this) == true) ImportPath.Text = dialog.FileName;
    }
    private void OpenImportView(object sender, RoutedEventArgs e)
    {
        if (workflow.IsImportOpen || !TryLeaveLocalEdits()) return;
        if (!workflow.TryOpenImport())
        {
            ShowNavigationBlocked("Navigation blocked: " + workflow.Status);
            return;
        }
        ShowStatus();
    }
    private void ShowPeopleView(object sender, RoutedEventArgs e)
    {
        if (!TryLeaveLocalEdits()) return;
        workflow.ShowPeople();
        ClearImportPreview();
        RefreshPeople();
        ShowStatus();
    }
    private void ChangeImportPath(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        ClearImportPreview();
        StatusText.Text = "Import file selection changed. Preview again before Apply. No data changes until Apply.";
    }
    private void ClearImportPreview()
    {
        preview = null;
        ApplyButton.IsEnabled = false;
        const string guidance = "Choose a version-1 Import batch and preview again. No data changes until Apply.";
        PreviewText.ItemsSource = new[] { guidance };
        System.Windows.Automation.AutomationProperties.SetName(PreviewText, guidance);
    }
    private void DiscardImportChanges(object sender, RoutedEventArgs e)
    {
        if (workPending)
        {
            ShowNavigationBlocked("Import discard blocked: local work is in progress. Please wait for it to finish.");
            return;
        }
        workflow.DiscardImportChanges();
        ClearImportPreview();
        ShowStatus();
    }
    private async void OpenWorkspace(object sender, RoutedEventArgs e)
        => await OpenWorkspaceAtPathAsync(WorkspacePath.Text);

    private void PresentWorkspaceOpen()
    {
        PersonNotes.OpenPerson(null);
        TeamNotes.OpenPerson(null);
        ClearImportPreview();
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
        if (!TryLeaveLocalEdits()) return;
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
        if (!TryLeaveLocalEdits()) return;
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
        LoadPersonEditors(person);
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
        UpdateProfileMode();
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
    private void EditPersonPhoto(string? localPath)
    {
        if (workPending || selectedEmail is null || workflow.ProfileEdit is null) return;
        workflow.StageProfilePhoto(localPath);
        MarkDraftPending();
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
        UpdateProfileMode();
        PersonNotes.RefreshExternalWorkspace();
        TeamNotes.RefreshExternalWorkspace();
        ShowStatus();
    }

    private void ShowStatus()
    {
        ImportPanel.Visibility = workflow.IsImportOpen ? Visibility.Visible : Visibility.Collapsed;
        ExplorerPanel.Visibility = workflow.IsImportOpen ? Visibility.Collapsed : Visibility.Visible;
        RefreshSearchAndFavorites();
        StatusText.Text = workflow.Status;
        if (!workPending)
            WorkflowActivity.Text = unsavedPreferredNameEdits.Count > 0 || HasUnsavedRelationship || !workflow.CanSafelyClose
                ? "Pending local edits — not yet saved" : "Ready";
        SettingsGuidance.Text = workflow.Compatibility.Guidance;
        WorkspaceFreshness.Text = workflow.IsWorkspaceCurrent
            ? "Current validated local Workspace."
            : workflow.Compatibility.State == WorkspaceCompatibilityState.NotOpened || startupRestorePath is not null
                ? "No Workspace is open. Choose a folder or retry the previous Workspace."
                : "STALE / unavailable — displayed information is the last valid local state, not current data.";
        RetryProfileButton.IsEnabled = !workPending &&
            (workflow.ProfileEdit is not null || unsavedPreferredNameEdits.Count > 0 || HasUnsavedRelationship);
        DiscardProfileButton.IsEnabled = !workPending &&
            (workflow.ProfileEdit is not null || unsavedPreferredNameEdits.Count > 0 || HasUnsavedRelationship);
        DiscardImportButton.Visibility = workflow.HasFailedImport ? Visibility.Visible : Visibility.Collapsed;
        DiscardImportButton.IsEnabled = !workPending && workflow.HasFailedImport;
        UpdateProfileMode();
        if (!workflow.IsWorkspaceCurrent)
        {
            PersonMoreMenu.IsEnabled = ManagerEditor.IsEnabled = false;
            AttributeEditors.IsEnabled = false;
            PreferredNameContext.IsEnabled = false;
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
        if (workflow.ProfileEdit is { IsDirty: true } && !DecideProfileLeave()) return false;
        if (!TryLeaveNotes()) return false;
        if (unsavedPreferredNameEdits.Count == 0 && !HasUnsavedRelationship)
        {
            if (!DecideProfileLeave()) return false;
            return true;
        }
        ShowNavigationBlocked(workflow.LocalSaveError is null
            ? "Navigation blocked: profile edits are pending. Please wait for saving to finish before leaving."
            : "Navigation blocked: profile edits were NOT saved. Retry or discard unsaved profile edits before leaving.");
        return false;
    }

    internal void ShowNavigationBlocked(string message) => StatusText.Text = message;
}
