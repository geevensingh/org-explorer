using System.Windows;
using System.Windows.Controls;

namespace OrgExplorer.Desktop;

public sealed class ReportingTreeView : TreeView
{
    internal Action<TreeViewItem>? RestoreItem { get; set; }

    protected override DependencyObject GetContainerForItemOverride() =>
        new ReportingTreeViewItem(RestoreContainer);

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        // WPF restores its item cache after preparation; Loaded is too late for cold rows.
        RestoreContainer(element);
    }

    private void RestoreContainer(DependencyObject element)
    {
        if (element is TreeViewItem container) RestoreItem?.Invoke(container);
    }

    private sealed class ReportingTreeViewItem(Action<DependencyObject> restore) : TreeViewItem
    {
        protected override DependencyObject GetContainerForItemOverride() =>
            new ReportingTreeViewItem(restore);

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            restore(element);
        }
    }
}
