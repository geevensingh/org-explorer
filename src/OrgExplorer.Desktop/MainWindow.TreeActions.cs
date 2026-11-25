using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    public static RoutedUICommand CollapseSubtreeCommand { get; } =
        new("Collapse subtree", nameof(CollapseSubtreeCommand), typeof(MainWindow));
    private bool changingTreePresentation;
    private IInputElement? reportingMenuReturnFocus;
    private int reportingMenuRequest;

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

    private void CollapseSubtree(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Parameter is not string email ||
            ReportingItemFrom(e.OriginalSource as DependencyObject) is not { DataContext: ReportingNode node } target ||
            !node.Person.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Collapse subtree was not applied: its reporting row is unavailable.";
            return;
        }
        RememberPeopleBrowsing();
        if (!workflow.ExecutePeopleTreeAction(email, PeopleTreeAction.CollapseSubtree))
        {
            StatusText.Text = workflow.PeopleTreeActionFeedback;
            return;
        }
        var wasChanging = changingTreePresentation;
        changingTreePresentation = true;
        try
        {
            var focus = reportingMenuReturnFocus ?? Keyboard.FocusedElement;
            if (ReportingItemFrom(focus as DependencyObject) is { } focused &&
                focused != target && target.IsAncestorOf(focused))
                reportingMenuReturnFocus = target;
            expandedPeople.Clear();
            expandedPeople.UnionWith(workflow.PeopleNavigation.ExpandedPeople);
            pendingSearchRevealEmail = null;
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
