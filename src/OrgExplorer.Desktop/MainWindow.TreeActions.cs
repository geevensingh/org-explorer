using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    public static RoutedUICommand ExpandSubtreeCommand { get; } =
        new("Expand subtree", nameof(ExpandSubtreeCommand), typeof(MainWindow));
    public static RoutedUICommand CollapseSubtreeCommand { get; } =
        new("Collapse subtree", nameof(CollapseSubtreeCommand), typeof(MainWindow));
    public static RoutedUICommand CollapseOtherBranchesCommand { get; } =
        new("Collapse other branches", nameof(CollapseOtherBranchesCommand), typeof(MainWindow));
    private bool changingTreePresentation;
    private string? treeDiagnosticCollapse;
    private IInputElement? reportingMenuReturnFocus;
    private int reportingMenuRequest;
    private TreeViewItem? treeExpansionTarget;
    private readonly Dictionary<string, WeakReference<TreeViewItem>> reportingRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer treeExpansionTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(16) };

    private void InitializeTreeExpansion()
    {
        PeopleList.RestoreItem = item => RestoreTreeItem(item, restoreSelection: false);
        treeExpansionTimer.Tick += (_, _) =>
        {
            var added = workflow.ContinuePeopleTreeExpansion();
            expandedPeople.UnionWith(added);
            var wasChanging = changingTreePresentation;
            changingTreePresentation = true;
            try
            {
                if (workflow.PeopleNavigation.IsBrowsing && added.Count > 0)
                {
                    foreach (var email in added)
                        if (reportingRows.TryGetValue(email, out var reference))
                        {
                            if (reference.TryGetTarget(out var container) &&
                                container.DataContext is ReportingNode node &&
                                node.Person.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
                                RestoreTreeItem(container, null!);
                            else reportingRows.Remove(email);
                        }
                    if (treeExpansionTarget is { DataContext: ReportingNode target } row &&
                        target.Person.Email.Equals(workflow.PeopleTreeExpansion?.TargetEmail, StringComparison.OrdinalIgnoreCase))
                        row.BringIntoView(new Rect(0, 0, 1, Math.Min(28, row.ActualHeight)));
                    RememberPeopleBrowsing();
                }
                ShowTreeExpansion();
            }
            finally { changingTreePresentation = wasChanging; }
        };
        Closed += (_, _) =>
        {
            treeExpansionTimer.Stop();
            workflow.CancelPeopleTreeExpansion();
            treeExpansionTarget = null;
        };
    }

    private void CanExpandSubtree(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = e.Parameter is string email &&
            workflow.GetPeopleTreeActions(email).Contains(PeopleTreeAction.ExpandSubtree);
        e.Handled = true;
    }

    private void ExpandSubtree(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Parameter is not string email ||
            ReportingItemFrom(e.OriginalSource as DependencyObject) is not { DataContext: ReportingNode node } target ||
            !node.Person.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Expand subtree was not applied: its reporting row is unavailable.";
            return;
        }
        RememberPeopleBrowsing();
        if (!workflow.ExecutePeopleTreeAction(email, PeopleTreeAction.ExpandSubtree))
        {
            StatusText.Text = workflow.PeopleTreeActionFeedback;
            return;
        }
        pendingPeopleRevealEmail = null;
        peopleBrowseRequest++;
        treeExpansionTarget = target;
        ShowTreeExpansion();
        treeExpansionTimer.Start();
    }

    private void CancelTreeExpansion(object sender, RoutedEventArgs e)
    {
        workflow.CancelPeopleTreeExpansion();
        ShowTreeExpansion();
    }

    private void CancelOutstandingTreeExpansion()
    {
        if (workflow.PeopleTreeExpansion is not { IsRunning: true }) return;
        workflow.CancelPeopleTreeExpansion();
        ShowTreeExpansion();
    }

    private void ShowTreeExpansion()
    {
        if (workflow.PeopleTreeExpansion is not { } progress)
        {
            treeExpansionTimer.Stop();
            treeExpansionTarget = null;
            TreeExpansionStatus.Text = "";
            TreeExpansionPanel.Visibility = Visibility.Collapsed;
            CancelTreeExpansionButton.Visibility = Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetItemStatus(PeopleList, "");
            return;
        }
        TreeExpansionStatus.Text = progress.Status switch
        {
            PeopleTreeExpansionStatus.Running =>
                $"Expanding subtree: {progress.VisitedPeople} imported People checked; {progress.ExpandedBranches} branches expanded.",
            PeopleTreeExpansionStatus.Completed =>
                $"Expanded all available reporting levels: {progress.VisitedPeople} imported People checked; {progress.ExpandedBranches} branches expanded.",
            _ => $"{workflow.PeopleTreeActionFeedback} {progress.VisitedPeople} imported People checked; {progress.ExpandedBranches} branches expanded."
        };
        System.Windows.Automation.AutomationProperties.SetItemStatus(PeopleList, TreeExpansionStatus.Text);
        TreeExpansionPanel.Visibility = progress.Status == PeopleTreeExpansionStatus.Completed
            ? Visibility.Collapsed : Visibility.Visible;
        CancelTreeExpansionButton.Visibility = progress.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        if (!progress.IsRunning)
        {
            treeExpansionTimer.Stop();
            treeExpansionTarget = null;
        }
        CommandManager.InvalidateRequerySuggested();
        WriteTreeStateDiagnostic();
    }

    private void WriteTreeStateDiagnostic(TreeViewItem? prepared = null)
    {
        var email = Environment.GetEnvironmentVariable("TREE_DIAGNOSTIC_EMAIL");
        if (string.IsNullOrWhiteSpace(email)) return;
        TreeViewItem? retained = null;
        if (reportingRows.TryGetValue(email, out var reference)) reference.TryGetTarget(out retained);
        var state = new
        {
            email,
            localExpanded = expandedPeople.Contains(email),
            coreExpanded = workflow.PeopleNavigation.ExpandedPeople.Contains(email),
            retainedType = retained?.GetType().FullName,
            retainedLoaded = retained?.IsLoaded,
            retainedExpanded = retained?.IsExpanded,
            collapse = treeDiagnosticCollapse
        };
        System.Windows.Automation.AutomationProperties.SetHelpText(PeopleList,
            "[DEBUG-tree-state] " + System.Text.Json.JsonSerializer.Serialize(state));
        if (prepared is not null)
            System.Windows.Automation.AutomationProperties.SetHelpText(prepared,
                "[DEBUG-tree-state] " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    state, preparedType = prepared.GetType().FullName, preparedLoaded = prepared.IsLoaded,
                    preparedExpanded = prepared.IsExpanded,
                    parentType = ItemsControl.ItemsControlFromItemContainer(prepared)?.GetType().FullName
                }));
    }

    private static TreeViewItem? ReportingItemFrom(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TreeViewItem item) return item;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void TargetReportingMenu(object sender, MouseButtonEventArgs e)
    {
        // TreeViewItem normally focuses/selects on right-button down, which opens a Person.
        if (ReferenceEquals(sender, ReportingItemFrom(e.OriginalSource as DependencyObject))) e.Handled = true;
    }

    private void PrepareReportingMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: ReportingNode node } row ||
            !ReferenceEquals(row, ReportingItemFrom(e.OriginalSource as DependencyObject))) return;
        RememberPeopleBrowsing();
        if (workflow.GetPeopleTreeActions(node.Person.Email).Count == 0)
        {
            e.Handled = true;
            return;
        }
        row.ContextMenu.PlacementTarget = row;
        reportingMenuReturnFocus = Keyboard.FocusedElement;
        reportingMenuRequest++;
        changingTreePresentation = true;
    }

    private void CanCollapseSubtree(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = e.Parameter is string email &&
            workflow.GetPeopleTreeActions(email).Contains(PeopleTreeAction.CollapseSubtree);
        e.Handled = true;
    }

    private void CanCollapseOtherBranches(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = e.Parameter is string email &&
            workflow.GetPeopleTreeActions(email).Contains(PeopleTreeAction.CollapseOtherBranches);
        e.Handled = true;
    }

    private void CollapseSubtree(object sender, ExecutedRoutedEventArgs e) =>
        ExecuteReportingCollapse(e, PeopleTreeAction.CollapseSubtree, "Collapse subtree");

    private void CollapseOtherBranches(object sender, ExecutedRoutedEventArgs e) =>
        ExecuteReportingCollapse(e, PeopleTreeAction.CollapseOtherBranches, "Collapse other branches");

    private void ExecuteReportingCollapse(ExecutedRoutedEventArgs e, PeopleTreeAction action, string label)
    {
        e.Handled = true;
        if (e.Parameter is not string email ||
            ReportingItemFrom(e.OriginalSource as DependencyObject) is not { DataContext: ReportingNode node } target ||
            !node.Person.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = $"{label} was not applied: its reporting row is unavailable.";
            return;
        }
        RememberPeopleBrowsing();
        if (!workflow.ExecutePeopleTreeAction(email, action))
        {
            StatusText.Text = workflow.PeopleTreeActionFeedback;
            return;
        }
        TreeExpansionPanel.Visibility = Visibility.Collapsed;
        var wasChanging = changingTreePresentation;
        changingTreePresentation = true;
        try
        {
            var focus = reportingMenuReturnFocus ?? Keyboard.FocusedElement;
            expandedPeople.Clear();
            expandedPeople.UnionWith(workflow.PeopleNavigation.ExpandedPeople);
            if (ReportingItemFrom(focus as DependencyObject) is { DataContext: ReportingNode focused })
            {
                var context = workflow.GetRelationships(focused.Person.Email);
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (context?.Manager is { } manager && visited.Add(manager.Email))
                {
                    if (!expandedPeople.Contains(manager.Email))
                    {
                        reportingMenuReturnFocus = target;
                        break;
                    }
                    context = workflow.GetRelationships(manager.Email);
                }
            }
            pendingPeopleRevealEmail = null;
            peopleBrowseRequest++;
            RestoreRealizedTreeState(PeopleList);
            target.BringIntoView(new Rect(0, 0, 1, Math.Min(28, target.ActualHeight)));
            RememberPeopleBrowsing();
            if (!wasChanging && IsVisible && reportingMenuReturnFocus is UIElement { IsVisible: true } element)
            {
                element.Focus();
                RestoreRealizedTreeState(PeopleList);
            }
        }
        finally
        {
            if (!wasChanging) reportingMenuReturnFocus = null;
            changingTreePresentation = wasChanging;
        }
    }

    private void CloseReportingMenu(object sender, RoutedEventArgs e)
    {
        if (!changingTreePresentation) return;
        var request = reportingMenuRequest;
        var focus = reportingMenuReturnFocus;
        // Restore after WPF's popup focus cleanup; it may otherwise refocus a now-hidden descendant.
        Dispatcher.BeginInvoke(() =>
        {
            if (request != reportingMenuRequest) return;
            try
            {
                if (IsVisible && focus is UIElement { IsVisible: true } element)
                {
                    element.Focus();
                    RestoreRealizedTreeState(PeopleList);
                }
            }
            finally
            {
                reportingMenuReturnFocus = null;
                changingTreePresentation = false;
            }
        }, DispatcherPriority.Input);
    }
}
