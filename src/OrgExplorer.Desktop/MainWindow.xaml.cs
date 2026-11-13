using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow : Window
{
    private readonly WorkspaceViewModel workflow;
    private readonly Func<Window, string?> chooseWorkspaceFolder;
    private Guid? preview;
    private string? selectedEmail;
    private bool loading;
    private readonly HashSet<string> expandedPeople = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TreeViewItem, object> treeRestorers = new();

    public MainWindow() : this(ApplicationWorkflow.CreateFileBacked(new BrowserProfileLinkOpener())) { }

    internal MainWindow(IApplicationWorkflow application) : this(application, ChooseWorkspaceFolder) { }

    internal MainWindow(IApplicationWorkflow application, Func<Window, string?> chooseWorkspaceFolder)
    {
        this.chooseWorkspaceFolder = chooseWorkspaceFolder;
        workflow = new(application, workspaceSettings);
        InitializeComponent();
        InitializeOperationalNotices();
        PeopleList.DataContext = workflow;
        PreviewKeyDown += EscapePeople;
        InitializeUpdates();
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
                SettingsPanel.Visibility = Visibility.Collapsed;
                UpdateMainViews();
                StatusText.Text = updateHandoffPending ? "Safe update handoff is in progress; wait for restart or a visible failure." :
                    workPending ? "Local work is still pending; closing waits for it to finish." :
                    "Unsaved local changes prevent safe closing.\n" + workflow.Status +
                    "\nResolve unsaved drafts with retry or deliberate discard. For committed storage maintenance, use Settings → Retry Workspace validation / storage maintenance; do not replay the edit.";
            }
        };
        Closed += (_, _) => workflow.Dispose();
    }
    private static string? ChooseWorkspaceFolder(Window owner)
    {
        var dialog = new OpenFolderDialog { Title = "Open Workspace" };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
    private void ChooseImport(object sender, RoutedEventArgs e)
    {
        if (!TryLeaveLocalEdits()) return;
        var dialog = new OpenFileDialog { Filter = "Import JSON|*.json", Title = "Choose a version-1 Import batch" };
        if (dialog.ShowDialog(this) == true) ImportPath.Text = dialog.FileName;
    }
    private void OpenImportView(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        UpdateMainViews();
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
    {
        if (chooseWorkspaceFolder(this) is not { } path) return;
        SettingsPanel.Visibility = Visibility.Collapsed;
        UpdateMainViews();
        await OpenWorkspaceAtPathAsync(path);
    }

    private void ConfirmImportPath(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || e.IsRepeat || !PreviewButton.IsEnabled) return;
        e.Handled = true;
        PreviewImport(sender, e);
    }

    private void PresentWorkspaceOpen()
    {
        var destination = workflow.PeopleNavigation;
        if (destination.SelectedEmail is null)
        {
            PersonNotes.OpenPerson(null);
            TeamNotes.OpenPerson(null);
        }
        ClearImportPreview();
        selectedEmail = null;
        ClearSearchContext(clearSelection: false);
        FavoriteButton.IsEnabled = false;
        FavoriteStatus.Text = "Select a Person.";
        expandedPeople.Clear();
        loading = true;
        SearchQuery.Text = destination.Query;
        loading = false;
        queryDirty = true;
        ArrangeWorkspace();
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
        if (destination.SelectedEmail is { } email && workflow.GetRelationships(email) is { } restored)
        {
            OpenPerson(restored.Person, navigation: false);
            PersonNotes.RefreshExternalWorkspace();
            TeamNotes.RefreshExternalWorkspace();
            if (destination.SelectedResult is { } result) ShowSearchContext(result);
        }
        ShowStatus();
        FrameworkElement? surface = destination.LastValidView?.Source switch
        {
            SearchSource.PersonNote => PersonNotes,
            SearchSource.OwnedTeamNote => TeamNotes,
            SearchSource.InheritedTeamNote or SearchSource.HistoricalTeamNote => SearchContextPanel,
            _ => null
        };
        if (surface is not null)
            Dispatcher.BeginInvoke(() =>
            {
                if (selectedEmail == destination.SelectedEmail &&
                    workflow.PeopleNavigation.LastValidView == destination.LastValidView &&
                    !workflow.IsImportOpen && SettingsPanel.Visibility != Visibility.Visible)
                    surface.BringIntoView();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
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
        var overlay = workflow.PeopleNavigation.OverlayOpen;
        RememberPeopleBrowsing();
        if (!OpenPerson(node.Person)) RestorePersonSelection();
        else
        {
            RefreshArchivedPeople();
            if (overlay) FocusPersonAfterOverlay();
        }
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
    private bool OpenPerson(PersonProfile person, bool navigation = true)
    {
        if ((workPending || !string.Equals(selectedEmail, person.Email, StringComparison.OrdinalIgnoreCase)) && !TryLeaveLocalEdits())
            return false;
        if (navigation && !workflow.NavigatePerson(person.Email))
        {
            ShowNavigationBlocked(workflow.Status);
            return false;
        }
        if (!string.Equals(selectedEmail, person.Email, StringComparison.OrdinalIgnoreCase))
        {
            HistoryAccess.IsExpanded = false;
            PersonScroll.ScrollToTop();
        }
        selectedEmail = person.Email;
        if (navigation) ClearSearchContext();
        LoadPersonEditors(person);
        ShowPerson(person);
        PersonNotes.OpenPerson(person.Email);
        TeamNotes.OpenPerson(person.Email);
        UpdateStartView();
        RefreshSearchAndFavorites();
        UpdatePeopleNavigation();
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
            OpenPerson(context.Person, navigation: false);
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
        DirectoryName.Text = person.DirectoryName ?? "—";
        Email.Text = person.Email;
        ShowFavorite(person);
        ShowPersonArchival(person);
        Photo.Show(workflow.GetPhoto(person.Email), person.Heading);
        UpdateProfileMode();
        var context = workflow.GetRelationships(person.Email)!;
        Manager.Content = (person.Archived ? "Last stored: " : "") +
            (context.Manager is { } manager ? manager.Heading + (manager.Archived ? " — Archived" : "") : "—");
        Manager.ToolTip = context.Manager is { } known
            ? $"{known.Heading} ({known.Email})"
            : "No stored Manager relationship.";
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
    private bool NavigateTo(string email, bool searchNavigation = false)
    {
        var context = workflow.GetRelationships(email);
        if (context is null) return false;
        if (!OpenPerson(context.Person, navigation: !searchNavigation))
        {
            RefreshPeople();
            return false;
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (workflow.PeopleNavigation.IsBrowsing && context.Manager is { } manager && visited.Add(manager.Email))
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
        var wasLoading = loading;
        loading = true;
        try
        {
            item.IsExpanded = expandedPeople.Contains(node.Person.Email);
            item.IsSelected = string.Equals(node.Person.Email, selectedEmail, StringComparison.OrdinalIgnoreCase);
        }
        finally { loading = wasLoading; }
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
        ShowViewPreferenceStatus();
        ShowPanePreferenceStatus();
        var path = workflow.Compatibility.WorkspacePath;
        WorkspaceName.Text = path is null ? "No Workspace open" : new DirectoryInfo(path).Name;
        WorkspaceName.ToolTip = WorkspaceName.Text;
        System.Windows.Automation.AutomationProperties.SetHelpText(WorkspaceName, path ?? "");
        WorkspacePath.Text = path ?? "No Workspace open";
        WorkspacePathLabel.Text = workflow.IsWorkspaceCurrent || path is null
            ? "Current Workspace folder" : "Workspace folder (unavailable)";
        System.Windows.Automation.AutomationProperties.SetName(WorkspacePath, WorkspacePath.Text);
        UpdateMainViews();
        RefreshSearchAndFavorites();
        StatusText.Text = workflow.Status;
        if (!workPending)
            WorkflowActivity.Text = workflow.GetWorkspaceConflicts().Count != 0
                ? workflow.PublicationOutcome == Workflow.WorkspacePublicationOutcome.Indeterminate
                    ? "Publication outcome unknown - external resolution required"
                    : "Saved locally - external conflict resolution required"
                : HasPreferredNameDraft || HasUnsavedRelationship || !workflow.CanSafelyClose
                ? "Pending local edits — not yet saved" : "Ready";
        ShowPublicationConflicts();
        SettingsGuidance.Text = workflow.Compatibility.Guidance;
        WorkspaceFreshness.Text = workflow.IsWorkspaceCurrent
            ? "Current validated local Workspace."
            : workflow.Compatibility.State == WorkspaceCompatibilityState.NotOpened || startupRestorePath is not null
                ? "No Workspace is open. Choose a folder or retry the previous Workspace."
                : "STALE / unavailable — displayed information is the last valid local state, not current data.";
        RetryProfileButton.IsEnabled = !workPending &&
            (workflow.ProfileEdit is not null || HasUnsavedRelationship);
        DiscardProfileButton.IsEnabled = !workPending &&
            (workflow.ProfileEdit is not null || HasUnsavedRelationship);
        RetryProfileButton.Visibility = RetryProfileButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        DiscardProfileButton.Visibility = DiscardProfileButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
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
        ShowOperationalNotice();
    }

    internal void ShowViewPreferenceStatus()
    {
        ViewPreferenceStatus.Text = string.Join("\n", new[] { workflow.ViewRecoveryFeedback, workflow.ViewPreferenceFeedback }
            .Where(text => !string.IsNullOrEmpty(text)));
        ViewPreferenceStatus.Visibility = ViewPreferenceStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowWorkspaceStartup();
    }

    private void UpdateMainViews()
    {
        var settingsOpen = SettingsPanel.Visibility == Visibility.Visible;
        ImportPanel.Visibility = !settingsOpen && workflow.IsImportOpen ? Visibility.Visible : Visibility.Collapsed;
        ExplorerPanel.Visibility = !settingsOpen && !workflow.IsImportOpen ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceSurface.Visibility = ExplorerPanel.Visibility;
        UpdateStartView();
        UpdatePeopleNavigation();
    }

    private void UpdateStartView()
    {
        var selected = selectedEmail is not null;
        WorkspaceStartPanel.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        PersonIdentity.Visibility = PersonBody.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceStartGuidance.Text = workflow.IsWorkspaceCurrent
            ? "Search local information, open Favorites or browse People to choose a Person. No replacement writing has been opened."
            : "Open Settings to choose a Workspace, or use the recovery actions above. No Person or note has been restored.";
    }

    private void FindPersonFromStart(object sender, RoutedEventArgs e)
    {
        if (!wideWorkspace) workflow.ShowPeopleOverlay(true);
        UpdatePeopleNavigation();
        SearchQuery.Focus();
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
        if (!HasPreferredNameDraft && !HasUnsavedRelationship)
        {
            if (!DecideProfileLeave()) return false;
            return true;
        }
        ShowNavigationBlocked(workflow.LocalSaveError is null
            ? "Navigation blocked: choose Save or Cancel for the Preferred name, or resolve pending quick changes before leaving."
            : "Navigation blocked: profile edits were NOT saved. Retry or discard unsaved profile edits before leaving.");
        return false;
    }

    internal void ShowNavigationBlocked(string message) => StatusText.Text = message;
}
