using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private const double WideWorkspaceWidth = 960;
    private const double PeopleMinimum = 240;
    private const double ProfileMinimum = 240;
    private const double NotesMinimum = 360;
    private const double DividerWidth = 8;
    private bool wideWorkspace;
    private bool arrangingPanes;
    private bool draggingPane;
    private bool draggingPeople;

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
            ExplorerPanel.ColumnDefinitions[2].MinWidth = wide ? ProfileMinimum + NotesMinimum + DividerWidth + 40 : 0;
            PeopleDividerColumn.Width = new(wide ? DividerWidth : 0);
            var proportions = workflow.PaneProportions;
            // Mixed pixel/star splitter resizing can grow content beyond the viewport instead of constraining both panes.
            PeopleColumn.Width = wide ? new(proportions.PeoplePercent, GridUnitType.Star) : new(0);
            ExplorerPanel.ColumnDefinitions[2].Width = new(wide ? 100 - proportions.PeoplePercent : 100, GridUnitType.Star);
            PersonScroll.Padding = new Thickness(wide ? 12 : 0, 0, 0, 0);
            OpenPeopleButton.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            PeopleDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            ProfileDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            ClosePeopleButton.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(ProfileSummary, wide ? 0 : 2);
            Grid.SetRow(NotesPane, wide ? 0 : 1);
            NotesPane.Margin = new Thickness(0, wide ? 0 : 12, 0, 0);
            UpdatePeopleNavigation();
            if (transition && wide && workflow.PeopleNavigation.IsBrowsing) RestorePeopleBrowsing();
        }
        finally { arrangingPanes = false; }
        ArrangePersonBody();
    }

    private void ArrangePersonBody()
    {
        if (PersonBody is null || arrangingPanes || draggingPane && !draggingPeople) return;
        arrangingPanes = true;
        try
        {
            ProfileColumn.MinWidth = wideWorkspace ? ProfileMinimum : 0;
            PersonBody.ColumnDefinitions[2].MinWidth = wideWorkspace ? NotesMinimum : 0;
            ProfileDividerColumn.Width = new(wideWorkspace ? DividerWidth : 0);
            var proportions = workflow.PaneProportions;
            ProfileColumn.Width = wideWorkspace ? new(proportions.ProfilePercent, GridUnitType.Star) : new(0);
            PersonBody.ColumnDefinitions[2].Width = new(wideWorkspace ? 100 - proportions.ProfilePercent : 100, GridUnitType.Star);
        }
        finally { arrangingPanes = false; }
    }

    private void BeginPaneResize(object sender, DragStartedEventArgs e)
    {
        draggingPane = true;
        draggingPeople = ReferenceEquals(sender, PeopleDivider);
    }

    private void SavePaneBalance(object sender, DragCompletedEventArgs e)
    {
        draggingPane = false;
        draggingPeople = false;
        if (!e.Canceled && sender is GridSplitter divider) PersistPaneBalance(divider);
        ArrangeWorkspace();
    }

    private void ResizePaneWithKeyboard(object sender, KeyEventArgs e)
    {
        if (!wideWorkspace || sender is not GridSplitter divider || e.Key is not (Key.Left or Key.Right)) return;
        e.Handled = true;
        var people = ReferenceEquals(divider, PeopleDivider);
        var column = people ? PeopleColumn : ProfileColumn;
        var grid = people ? ExplorerPanel : PersonBody;
        var maximum = grid.ActualWidth - DividerWidth - grid.ColumnDefinitions[2].MinWidth;
        column.Width = new(Math.Clamp(column.ActualWidth + (e.Key == Key.Right ? 10 : -10), column.MinWidth, maximum));
        grid.UpdateLayout();
        PersistPaneBalance(divider);
        ArrangeWorkspace();
    }

    private void PersistPaneBalance(GridSplitter divider)
    {
        if (!wideWorkspace || ExplorerPanel.ActualWidth < WideWorkspaceWidth || workflow.Compatibility.WorkspacePath is null) return;
        var current = workflow.PaneProportions;
        var people = ReferenceEquals(divider, PeopleDivider);
        workflow.SavePaneProportions(
            people ? 100 * PeopleColumn.ActualWidth / (ExplorerPanel.ActualWidth - DividerWidth) : current.PeoplePercent,
            people ? current.ProfilePercent : 100 * ProfileColumn.ActualWidth / (PersonBody.ActualWidth - DividerWidth));
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
            if (workflow.PeopleNavigation.IsBrowsing) RestorePeopleBrowsing();
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
        if (PeoplePanel is null) return;
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
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
        return null;
    }

    private void RememberPeopleBrowsing() => workflow.RememberPeopleBrowsing(expandedPeople,
        FindScrollViewer(PeopleList)?.VerticalOffset ?? 0);

    private void RestorePeopleBrowsing()
    {
        expandedPeople.Clear();
        expandedPeople.UnionWith(workflow.PeopleNavigation.ExpandedPeople);
        RestoreRealizedTreeState(PeopleList);
        Dispatcher.BeginInvoke(() =>
        {
            if (workflow.PeopleNavigation.IsBrowsing)
                FindScrollViewer(PeopleList)?.ScrollToVerticalOffset(workflow.PeopleNavigation.BrowseOffset);
        }, DispatcherPriority.Loaded);
    }
}
