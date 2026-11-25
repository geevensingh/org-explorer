using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OrgExplorer.Desktop.Smoke;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private RevealLifecycleRecorder? revealLifecycle;
    private bool revealLifecycleFlushQueued;

    partial void ObserveRevealLifecycle(string stage, int request, string? email, FrameworkElement? row, Rect? target)
    {
        var path = Environment.GetEnvironmentVariable("ORG_EXPLORER_REVEAL_LIFECYCLE_FILE");
        if (string.IsNullOrEmpty(path)) return;
        revealLifecycle ??= new(serialized =>
        {
            File.WriteAllText(path + ".pending", serialized);
            File.Move(path + ".pending", path, overwrite: true);
        }, error => Console.Error.WriteLine(
            $"DEBUG-136-REVEAL-LIFECYCLE output-failure type={error.GetType().FullName} hResult={error.HResult}"));
        string? Bounds(FrameworkElement? element)
        {
            if (element is null || !element.IsDescendantOf(PeopleList)) return null;
            return element.TransformToAncestor(PeopleList).TransformBounds(new Rect(element.RenderSize)).ToString();
        }
        try
        {
            var scroll = FindScrollViewer(PeopleList);
            var header = row is TreeViewItem item ? item.Template?.FindName("PART_Header", item) as FrameworkElement : row;
            revealLifecycle.Record(new
            {
                stage, request, currentRequest = peopleBrowseRequest,
                requestCurrent = request == peopleBrowseRequest,
                sameLocalSelection = email == selectedEmail,
                sameWorkflowSelection = email == workflow.PeopleNavigation.SelectedEmail,
                workspaceCurrent = workflow.IsWorkspaceCurrent, browsing = workflow.PeopleNavigation.IsBrowsing,
                importOpen = workflow.IsImportOpen,
                explorerVisible = ExplorerPanel.Visibility == Visibility.Visible,
                peopleVisible = PeoplePanel.Visibility == Visibility.Visible, wideWorkspace,
                treeMeasureValid = PeopleList.IsMeasureValid, treeArrangeValid = PeopleList.IsArrangeValid,
                treeSize = PeopleList.RenderSize.ToString(),
                verticalOffset = scroll?.VerticalOffset, horizontalOffset = scroll?.HorizontalOffset,
                viewportHeight = scroll?.ViewportHeight, extentHeight = scroll?.ExtentHeight,
                targetKind = row is TreeViewItem ? "TreeViewItem" : row is ListBoxItem ? "ListBoxItem" : null,
                target = target?.ToString(), rowBounds = Bounds(row), headerBounds = Bounds(header),
                rowVisible = row?.IsVisible, headerVisible = header?.IsVisible,
                rowSelected = row is TreeViewItem treeItem ? (bool?)treeItem.IsSelected : null,
                rowMeasureValid = row?.IsMeasureValid, rowArrangeValid = row?.IsArrangeValid,
                headerMeasureValid = header?.IsMeasureValid, headerArrangeValid = header?.IsArrangeValid
            });
        }
        catch (Exception error)
        {
            revealLifecycle.Record(new
            {
                stage, request, observationFailureType = error.GetType().FullName, observationFailureHResult = error.HResult
            });
        }
        if (revealLifecycleFlushQueued) return;
        revealLifecycleFlushQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            revealLifecycleFlushQueued = false;
            revealLifecycle.Publish();
        }, DispatcherPriority.ContextIdle);
    }
}
