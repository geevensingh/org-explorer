namespace OrgExplorer.Desktop.Smoke;

internal static class TreeItemRealization
{
    internal static void PrepareMeasuredScrollPosition(double percent, Action<double> scrollTo,
        Func<double> currentPercent, Action<Func<bool>> awaitState)
    {
        scrollTo(100);
        awaitState(() => Math.Abs(currentPercent() - 100) < 1);
        scrollTo(percent);
        awaitState(() => Math.Abs(currentPercent() - percent) < 1);
    }

    internal static void AwaitExpanded(bool initializeBranch, Func<bool> isExpanded, Action expand,
        Action<Func<bool>> awaitState)
    {
        if (initializeBranch && !isExpanded()) expand();
        awaitState(isExpanded);
    }

    internal static T AwaitVisible<T>(Func<T?> find, Action<T> realize, Func<T, bool> isVisible,
        Action<Func<bool>> awaitState) where T : class
    {
        T? item = null;
        awaitState(() =>
        {
            if (item is null)
            {
                var candidate = find();
                if (candidate is null) return false;
                realize(candidate);
                item = candidate;
            }
            return isVisible(item);
        });
        return item!;
    }
}
