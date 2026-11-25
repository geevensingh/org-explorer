namespace OrgExplorer.Desktop.Smoke;

internal static class TreeItemRealization
{
    internal enum MenuBranchInput { ExpandCollapseProvider, DisclosureArrow }
    internal enum MenuRetentionBoundary { Command, OrdinaryFixtureRestoration }

    internal static bool PrepareArchivedListForMenu(Func<bool> isShown, Action toggle,
        Func<bool> treeReady, Action<Func<bool>> awaitState)
    {
        var initiallyShown = isShown();
        if (initiallyShown) toggle();
        awaitState(() => !isShown() && treeReady());
        return initiallyShown;
    }

    internal static void RestoreArchivedListAfterMenu(bool initiallyShown, Func<bool> isShown,
        Action toggle, Action<Func<bool>> awaitState)
    {
        if (isShown())
            throw new InvalidOperationException("The archived list changed during the menu fixture; no restoration input was replayed.");
        if (initiallyShown) toggle();
        awaitState(() => isShown() == initiallyShown);
    }

    internal static void AssertMenuRetention(MenuRetentionBoundary boundary,
        Action assertContentRetained, Action assertTargetVisible)
    {
        assertContentRetained();
        if (boundary != MenuRetentionBoundary.OrdinaryFixtureRestoration) assertTargetVisible();
    }

    internal static void PrepareMenuBranch(Func<bool> isExpanded, Action<MenuBranchInput> requestInput,
        Action<Func<bool>> awaitState) =>
        AwaitExpanded(true, isExpanded, () => requestInput(MenuBranchInput.DisclosureArrow), awaitState);

    internal static (double X, double Y) ReportingHeaderPoint(
        (double Left, double Top, double Width, double Height) row,
        (double Left, double Top, double Width, double Height) viewport, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        var left = Math.Max(row.Left + 22 * scale, viewport.Left);
        var right = Math.Min(row.Left + row.Width, viewport.Left + viewport.Width);
        var top = Math.Max(row.Top, viewport.Top);
        // A TreeItem's bounds include its descendants; only the first row is the target header.
        var bottom = Math.Min(row.Top + Math.Min(row.Height, 28 * scale), viewport.Top + viewport.Height);
        var x = Math.Floor((left + right) / 2);
        var y = Math.Floor((top + bottom) / 2);
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < left || x >= right || y < top || y >= bottom)
            throw new InvalidOperationException($"The reporting row header has no visible content pixel: row={row}; viewport={viewport}; scale={scale}.");
        return (x, y);
    }

    internal static bool IsBrowsingPositionRestored(BrowsingPosition original, BrowsingPosition current) =>
        original.Rows.Count > 0 && original.Rows.All(expected => current.Rows.Any(actual =>
            string.Equals(expected.Email, actual.Email, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(expected.Left - actual.Left) < 1 && Math.Abs(expected.Top - actual.Top) < 1));

    internal static void PrepareMeasuredScrollPosition(double percent, Action<double> scrollTo,
        Func<double> currentPercent, Action<Func<bool>> awaitState)
    {
        scrollTo(100);
        awaitState(() => Math.Abs(currentPercent() - 100) < 1);
        scrollTo(percent);
        awaitState(() =>
        {
            var observed = currentPercent();
            return observed > 0 && observed < 100;
        });
    }

    internal sealed record BrowsingRow(string Email, double Left, double Top);
    internal sealed record BrowsingPosition(double Percent, IReadOnlyList<BrowsingRow> Rows);

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
