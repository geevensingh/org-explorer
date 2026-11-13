using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OrgExplorer.Desktop;

// [DEBUG-71-HSCROLL] Temporary native-boundary observations; remove this file and its call sites before delivery.
public partial class MainWindow
{
    private readonly string? hscrollDiagnosticFile =
        Environment.GetEnvironmentVariable("ORG_EXPLORER_DEBUG_71_HSCROLL_LOG");
    private ScrollViewer? hscrollDiagnosticViewer;
    private int hscrollDiagnosticRestore;
    private int hscrollDiagnosticActiveRestore;
    private int hscrollDiagnosticLine;

    private int BeginHscrollDiagnosticRestore()
    {
        if (string.IsNullOrEmpty(hscrollDiagnosticFile)) return 0;
        var id = ++hscrollDiagnosticRestore;
        hscrollDiagnosticActiveRestore = id;
        TraceHscroll("restore-enqueue", id);
        return id;
    }

    private void ObserveSettledHscroll(int restoreId)
    {
        if (restoreId == 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            TraceHscroll("restore-settled-context-idle", restoreId);
            if (hscrollDiagnosticActiveRestore == restoreId) hscrollDiagnosticActiveRestore = 0;
        }, DispatcherPriority.ContextIdle);
    }

    private void TraceHscroll(string stage, int restoreId = 0, ScrollChangedEventArgs? change = null)
    {
        if (string.IsNullOrEmpty(hscrollDiagnosticFile)) return;
        var scroll = PeopleList is null ? null : FindScrollViewer(PeopleList);
        if (scroll is not null && !ReferenceEquals(scroll, hscrollDiagnosticViewer))
        {
            if (hscrollDiagnosticViewer is not null) hscrollDiagnosticViewer.ScrollChanged -= TraceHscrollChange;
            hscrollDiagnosticViewer = scroll;
            scroll.ScrollChanged += TraceHscrollChange;
        }
        var navigation = workflow.PeopleNavigation;
        var observation = new
        {
            timestamp = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId,
            sequence = ++hscrollDiagnosticLine,
            stage,
            restoreId,
            lastRestoreId = hscrollDiagnosticRestore,
            activeRestoreId = hscrollDiagnosticActiveRestore,
            bookmarkH = navigation.BrowseHorizontalOffset,
            bookmarkV = navigation.BrowseOffset,
            horizontalOffset = scroll?.HorizontalOffset,
            verticalOffset = scroll?.VerticalOffset,
            extentWidth = scroll?.ExtentWidth,
            extentHeight = scroll?.ExtentHeight,
            viewportWidth = scroll?.ViewportWidth,
            viewportHeight = scroll?.ViewportHeight,
            scrollableWidth = scroll?.ScrollableWidth,
            horizontalScrollbar = scroll?.ComputedHorizontalScrollBarVisibility.ToString(),
            scrollIsVisible = scroll?.IsVisible,
            peopleIsVisible = PeoplePanel?.IsVisible,
            peopleVisibility = PeoplePanel?.Visibility.ToString(),
            windowIsVisible = IsVisible,
            query = navigation.Query,
            displayedQuery = SearchQuery?.Text,
            overlay = navigation.OverlayOpen,
            owner = navigation.SelectedEmail,
            keyboardFocus = HscrollDiagnosticIdentity(Keyboard.FocusedElement),
            logicalFocus = HscrollDiagnosticIdentity(FocusManager.GetFocusedElement(this)),
            horizontalChange = change?.HorizontalChange,
            verticalChange = change?.VerticalChange,
            extentWidthChange = change?.ExtentWidthChange,
            extentHeightChange = change?.ExtentHeightChange,
            viewportWidthChange = change?.ViewportWidthChange,
            viewportHeightChange = change?.ViewportHeightChange
        };
        try
        {
            File.AppendAllText(hscrollDiagnosticFile,
                "[DEBUG-71-HSCROLL] " + JsonSerializer.Serialize(observation) + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("[DEBUG-71-HSCROLL] Diagnostic write failed: " +
                error.Message.Replace("\r", "\\r").Replace("\n", "\\n"));
        }
    }

    private void TraceHscrollChange(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, hscrollDiagnosticViewer)) return;
        if (hscrollDiagnosticActiveRestore != 0 || e.HorizontalChange != 0 || e.ExtentWidthChange != 0 ||
            e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
            TraceHscroll("scroll-changed", hscrollDiagnosticActiveRestore, e);
    }

    private static string? HscrollDiagnosticIdentity(IInputElement? element)
    {
        if (element is TreeViewItem { DataContext: OrgExplorer.Workflow.ReportingNode node }) return node.Person.Email;
        if (element is not DependencyObject control) return null;
        var id = AutomationProperties.GetAutomationId(control);
        return id.Length > 0 ? id : element is FrameworkElement field ? field.Name : element.GetType().Name;
    }
}
