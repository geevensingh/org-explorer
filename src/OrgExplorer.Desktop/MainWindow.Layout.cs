using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private const double WideWorkspaceWidth = 960;
    private const double PeopleMinimum = 240;
    private const double PersonMinimum = 420;
    private const double DividerWidth = 8;
    private bool wideWorkspace;
    private bool arrangingPanes;
    private bool draggingPane;
    private string? pendingPeopleRevealEmail;
    private int peopleBrowseRequest;

    private void ResizeWorkspace(object sender, SizeChangedEventArgs e) => ArrangeWorkspace();
    private void ResizePersonBody(object sender, SizeChangedEventArgs e) => ArrangePersonBody();

    private void ArrangeWorkspace()
    {
        if (PersonBody is null || arrangingPanes || draggingPane || ExplorerPanel.ActualWidth <= 0) return;
        arrangingPanes = true;
        try
        {
            var wide = ExplorerPanel.ActualWidth >= WideWorkspaceWidth;
            var transition = wide != wideWorkspace;
            if (transition)
            {
                if (workflow.PeopleNavigation.IsBrowsing && PeoplePanel.Visibility == Visibility.Visible)
                    RememberPeopleBrowsing();
                workflow.ShowPeopleOverlay(false);
            }
            wideWorkspace = wide;
            PeopleColumn.MinWidth = wide ? PeopleMinimum : 0;
            ExplorerPanel.ColumnDefinitions[2].MinWidth = wide ? PersonMinimum : 0;
            PeopleDividerColumn.Width = new(wide ? DividerWidth : 0);
            var proportions = workflow.PaneProportions;
            // Mixed pixel/star splitter resizing can grow content beyond the viewport instead of constraining both panes.
            PeopleColumn.Width = wide ? new(proportions.PeoplePercent, GridUnitType.Star) : new(0);
            ExplorerPanel.ColumnDefinitions[2].Width = new(wide ? 100 - proportions.PeoplePercent : 100, GridUnitType.Star);
            PersonWorkspace.Margin = new Thickness(wide ? 12 : 0, 0, 0, 0);
            OpenPeopleButton.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            PeopleDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            ClosePeopleButton.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            UpdatePeopleNavigation();
            if (transition && wide && workflow.PeopleNavigation.IsBrowsing)
            {
                if (pendingPeopleRevealEmail is not null) RevealSelectedPerson();
                else RestorePeopleBrowsing();
            }
        }
        finally { arrangingPanes = false; }
        ArrangePersonBody();
    }

    private void ArrangePersonBody()
    {
        if (PersonBody is null || PersonBody.ActualWidth <= 0) return;
        var factColumns = Math.Clamp((int)(PersonBody.ActualWidth / 240), 1, 4);
        FactsGrid.ItemWidth = PersonBody.ActualWidth / factColumns;
        ProfileTextFields.Columns = PersonBody.ActualWidth >= 560 ? 2 : 1;
        var actionsBelow = PersonWorkspace.ActualWidth < 720;
        PlaceProfileElement(IdentityActions, actionsBelow ? IdentityDetails : PersonIdentity);
        ProfileHeading.TextWrapping = actionsBelow ? TextWrapping.NoWrap : TextWrapping.Wrap;
        ProfileHeading.MaxHeight = actionsBelow ? 32 : 64;
        ProfileHeading.TextTrimming = TextTrimming.CharacterEllipsis;
        IdentityActions.Margin = new Thickness(actionsBelow ? 0 : 12, actionsBelow ? 2 : 0, 0, 0);
    }

    private void BeginPaneResize(object sender, DragStartedEventArgs e)
    {
        draggingPane = true;
    }

    private void SavePaneBalance(object sender, DragCompletedEventArgs e)
    {
        draggingPane = false;
        if (!e.Canceled) PersistPaneBalance();
        ArrangeWorkspace();
    }

    private void ResizePaneWithKeyboard(object sender, KeyEventArgs e)
    {
        if (!wideWorkspace || e.Key is not (Key.Left or Key.Right)) return;
        e.Handled = true;
        var column = PeopleColumn;
        var grid = ExplorerPanel;
        var maximum = grid.ActualWidth - DividerWidth - grid.ColumnDefinitions[2].MinWidth;
        // SizeChanged must not restore the previous ratio before the key's new balance is saved.
        arrangingPanes = true;
        try
        {
            column.Width = new(Math.Clamp(column.ActualWidth + (e.Key == Key.Right ? 10 : -10), column.MinWidth, maximum));
            grid.UpdateLayout();
            PersistPaneBalance();
        }
        finally { arrangingPanes = false; }
        ArrangeWorkspace();
    }

    private void PersistPaneBalance()
    {
        if (!wideWorkspace || ExplorerPanel.ActualWidth < WideWorkspaceWidth || workflow.Compatibility.WorkspacePath is null) return;
        var current = workflow.PaneProportions;
        workflow.SavePaneProportions(
            100 * PeopleColumn.ActualWidth / (ExplorerPanel.ActualWidth - DividerWidth),
            current.ProfilePercent);
        ShowPanePreferenceStatus();
        ShowWorkspaceStartup();
    }

    private void ShowPanePreferenceStatus()
    {
        PanePreferenceStatus.Text = workflow.LayoutPreferenceFeedback ?? "";
        PanePreferenceStatus.Visibility = PanePreferenceStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TogglePeople(object sender, RoutedEventArgs e)
    {
        if (workflow.PeopleNavigation.OverlayOpen && workflow.PeopleNavigation.IsBrowsing) RememberPeopleBrowsing();
        workflow.ShowPeopleOverlay(!workflow.PeopleNavigation.OverlayOpen);
        UpdatePeopleNavigation();
        if (workflow.PeopleNavigation.OverlayOpen)
        {
            if (workflow.PeopleNavigation.IsBrowsing)
            {
                if (pendingPeopleRevealEmail is not null) RevealSelectedPerson();
                else RestorePeopleBrowsing();
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (!workflow.PeopleNavigation.OverlayOpen) return;
                var target = workflow.PeopleNavigation.IsBrowsing ? (Control)ClosePeopleButton : SearchResults;
                FocusManager.SetFocusedElement(this, target);
                if (IsVisible) target.Focus();
            }, DispatcherPriority.Input);
        }
        else FocusPeopleEntry();
    }

    private void ClosePeople(object sender, RoutedEventArgs e)
    {
        if (workflow.PeopleNavigation.IsBrowsing) RememberPeopleBrowsing();
        workflow.ShowPeopleOverlay(false);
        UpdatePeopleNavigation();
        FocusPeopleEntry();
    }

    private void FocusPeopleEntry()
    {
        FocusManager.SetFocusedElement(this, OpenPeopleButton);
        if (IsVisible) OpenPeopleButton.Focus();
    }

    private void FocusPersonAfterOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (workflow.PeopleNavigation.OverlayOpen || ExplorerPanel.Visibility != Visibility.Visible) return;
            FocusManager.SetFocusedElement(this, Email);
            if (IsVisible) Email.Focus();
        }, DispatcherPriority.Input);
    }

    private void EscapePeople(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !workflow.PeopleNavigation.OverlayOpen) return;
        e.Handled = true;
        ClosePeople(sender, e);
    }

    private void UpdatePeopleNavigation()
    {
        if (PeoplePanel is null || ReportsSection is null || ReportsList is null) return;
        var overlay = !wideWorkspace && workflow.PeopleNavigation.OverlayOpen;
        PeoplePanel.Visibility = wideWorkspace || overlay ? Visibility.Visible : Visibility.Collapsed;
        PeoplePanel.Width = wideWorkspace ? double.NaN : Math.Min(360, ExplorerPanel.ActualWidth);
        PeoplePanel.HorizontalAlignment = wideWorkspace ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        KeyboardNavigation.SetTabNavigation(PeoplePanel, overlay ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Continue);
        Grid.SetColumnSpan(PeoplePanel, wideWorkspace ? 1 : 3);
        Panel.SetZIndex(PeoplePanel, overlay ? 20 : 0);
        PeopleDismissSurface.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = workflow.PeopleNavigation.IsBrowsing ? Visibility.Collapsed : Visibility.Visible;
        BrowsingPanel.Visibility = workflow.PeopleNavigation.IsBrowsing ? Visibility.Visible : Visibility.Collapsed;
        ReportsSection.Visibility = ReportsList.HasItems && (!wideWorkspace || !workflow.PeopleNavigation.IsBrowsing)
            ? Visibility.Visible : Visibility.Collapsed;
        if (pendingPeopleRevealEmail is not null) RevealSelectedPerson();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
        return null;
    }

    private void RememberPeopleBrowsing()
    {
        var scroll = FindScrollViewer(PeopleList);
        workflow.RememberPeopleBrowsing(expandedPeople, scroll?.VerticalOffset ?? 0, scroll?.HorizontalOffset ?? 0,
            scroll?.ExtentWidth ?? 0);
    }

    private void RestorePeopleBrowsing()
    {
        var request = ++peopleBrowseRequest;
        // Retain accepted Person selections made while search hid the tree.
        expandedPeople.UnionWith(workflow.PeopleNavigation.ExpandedPeople);
        RestoreRealizedTreeState(PeopleList);
        Dispatcher.BeginInvoke(() =>
        {
            if (request == peopleBrowseRequest && workflow.PeopleNavigation.IsBrowsing && FindScrollViewer(PeopleList) is { } scroll)
            {
                scroll.ScrollToHorizontalOffset(workflow.PeopleNavigation.BrowseHorizontalOffset);
                scroll.ScrollToVerticalOffset(workflow.PeopleNavigation.BrowseOffset);
            }
        }, DispatcherPriority.Loaded);
    }

    partial void ObserveRevealLifecycle(string stage, int request, string? email, FrameworkElement? row, Rect? target);

    private void RevealSelectedPerson()
    {
        ObserveRevealLifecycle("request-considered", peopleBrowseRequest, pendingPeopleRevealEmail, null, null);
        if (!workflow.PeopleNavigation.IsBrowsing || ExplorerPanel.Visibility != Visibility.Visible ||
            PeoplePanel.Visibility != Visibility.Visible || workflow.IsImportOpen) return;
        var email = pendingPeopleRevealEmail;
        var request = ++peopleBrowseRequest;
        ObserveRevealLifecycle("request-queued", request, email, null, null);
        Dispatcher.BeginInvoke(() =>
        {
            if (request != peopleBrowseRequest || email is null || email != selectedEmail ||
                email != workflow.PeopleNavigation.SelectedEmail || !workflow.IsWorkspaceCurrent ||
                !workflow.PeopleNavigation.IsBrowsing || workflow.IsImportOpen ||
                ExplorerPanel.Visibility != Visibility.Visible || PeoplePanel.Visibility != Visibility.Visible)
            {
                ObserveRevealLifecycle("callback-rejected", request, email, null, null);
                return;
            }
            ObserveRevealLifecycle("callback-valid", request, email, null, null);
            FrameworkElement? row;
            if (workflow.GetRelationships(email)?.Person is { Archived: true } person)
            {
                ShowArchivedPeople.IsChecked = true;
                RefreshArchivedPeople();
                var item = ArchivedPeopleList.Items.Cast<PersonProfile>().Single(p =>
                    p.Email.Equals(person.Email, StringComparison.OrdinalIgnoreCase));
                ArchivedPeopleList.ScrollIntoView(item);
                ArchivedPeopleList.UpdateLayout();
                row = ArchivedPeopleList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
            }
            else
            {
                expandedPeople.UnionWith(workflow.PeopleNavigation.ExpandedPeople);
                row = RevealReportingRow(email);
            }
            if (row is null)
            {
                ObserveRevealLifecycle("row-unavailable", request, email, null, null);
                StatusText.Text = "The Person is open, but their People row could not be revealed. Reopen People to try again.";
                return;
            }
            ObserveRevealLifecycle("realization-complete", request, email, row, null);
            // Root rows can reserve a wider browsing extent; reveal the header's leading edge, not its trailing blank width.
            var target = new Rect(0, 0, 1, Math.Min(28, row.ActualHeight));
            ObserveRevealLifecycle("bring-call", request, email, row, target);
            row.BringIntoView(target);
            if (wideWorkspace)
            {
                FocusManager.SetFocusedElement(this, row);
                if (IsVisible) row.Focus();
            }
            pendingPeopleRevealEmail = null;
            // BringIntoView queues scroll/layout work; do not remember the preceding offset.
            Dispatcher.BeginInvoke(() =>
            {
                if (request == peopleBrowseRequest && email == selectedEmail &&
                    workflow.PeopleNavigation.IsBrowsing && !workflow.IsImportOpen &&
                    ExplorerPanel.Visibility == Visibility.Visible && PeoplePanel.Visibility == Visibility.Visible)
                    RememberPeopleBrowsing();
                ObserveRevealLifecycle("context-idle-result", request, email, row, target);
            }, DispatcherPriority.ContextIdle);
        }, DispatcherPriority.Loaded);
    }

    private TreeViewItem? RevealReportingRow(string email)
    {
        var path = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var context = workflow.GetRelationships(email); context is not null && visited.Add(context.Person.Email);
            context = context.Manager is { } manager ? workflow.GetRelationships(manager.Email) : null)
            path.Push(context.Person.Email);
        ItemsControl parent = PeopleList;
        TreeViewItem? row = null;
        while (path.TryPop(out var person))
        {
            var index = parent.Items.Cast<ReportingNode>().ToList().FindIndex(n =>
                n.Person.Email.Equals(person, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            parent.ApplyTemplate();
            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem)
            {
                FindItemsHost(parent, parent)?.BringIndexIntoViewPublic(index);
                parent.UpdateLayout();
            }
            if (parent.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem container) return null;
            var wasLoading = loading;
            loading = true;
            try
            {
                container.IsExpanded = expandedPeople.Contains(person);
                container.IsSelected = path.Count == 0;
            }
            finally { loading = wasLoading; }
            row = container;
            parent = container;
        }
        return row;
    }

    private static VirtualizingPanel? FindItemsHost(DependencyObject root, ItemsControl owner)
    {
        if (root is VirtualizingPanel { IsItemsHost: true } panel && ItemsControl.GetItemsOwner(panel) == owner) return panel;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindItemsHost(VisualTreeHelper.GetChild(root, index), owner) is { } child) return child;
        return null;
    }
}
