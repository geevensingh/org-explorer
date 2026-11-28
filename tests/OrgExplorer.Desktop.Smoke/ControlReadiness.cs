namespace OrgExplorer.Desktop.Smoke;

internal static class ControlReadiness
{
    internal static TPattern AwaitExpandedPattern<TPattern>(Action expand, Func<TPattern> find,
        Action<Func<bool>> awaitState) where TPattern : class
    {
        expand();
        return AwaitControl(find, pattern => pattern is not null, awaitState);
    }

    internal static TControl AwaitControl<TControl>(Func<TControl> find, Func<TControl, bool> isReady,
        Action<Func<bool>> awaitState) where TControl : class
    {
        TControl? control = null;
        awaitState(() =>
        {
            control = find();
            return isReady(control);
        });
        return control!;
    }

    internal static void ScrollToTopIfScrollable(Func<bool> isScrollable, Action scrollToTop)
    {
        if (isScrollable()) scrollToTop();
    }

    internal static void PreparePeopleBrowsing(Func<string> readQuery, Action clearQuery,
        Func<bool> needsOverlay, Action openOverlay, Func<bool> peopleVisible, Action<Func<bool>> awaitState)
    {
        var query = AwaitControl(readQuery, _ => true, awaitState);
        if (query.Length > 0) clearQuery();
        if (needsOverlay()) openOverlay();
        awaitState(peopleVisible);
    }

    internal static void AwaitSearchBrowsing(Func<bool> isCompact, Func<bool, bool> isReady,
        Action<Func<bool>> awaitState)
    {
        awaitState(() => isReady(isCompact()));
    }

    internal static string AwaitReading(Func<string> read, string expected, Action<Func<bool>> awaitState) =>
        AwaitControl(read, text => text.Contains(expected), awaitState);

    internal static string AwaitFailedTeamDesignation(Func<string> readFeedback, Func<string> readKind,
        Action<Func<bool>> awaitState)
    {
        string? kind = null;
        awaitState(() =>
        {
            if (!readFeedback().Contains("NOT saved")) return false;
            kind = readKind();
            return kind.Contains("Other Team note");
        });
        return kind!;
    }

    internal sealed record SelectedRowHeader<THeader>(bool RowOffscreen, bool Selected, THeader? Header) where THeader : class;

    internal static THeader AwaitSelectedHeader<THeader>(Func<SelectedRowHeader<THeader>?> observe,
        Func<THeader, bool> headerReady, Action<Func<bool>> awaitState) where THeader : class
    {
        THeader? header = null;
        awaitState(() =>
        {
            var row = observe();
            // Logical row metadata does not establish the rendered header's visibility.
            if (row is null || !row.Selected || row.Header is null) return false;
            header = row.Header;
            return headerReady(header);
        });
        return header!;
    }

    internal static void AssertCollapsed(IEnumerable<string> sections, Func<string, bool> isCollapsed,
        Action<Func<bool>> awaitState)
    {
        foreach (var section in sections)
        {
            var collapsed = false;
            awaitState(() =>
            {
                collapsed = isCollapsed(section);
                return true;
            });
            if (!collapsed)
                throw new InvalidOperationException($"{section} must not precede everyday Person writing.");
        }
    }

    internal static TPattern AwaitPattern<TControl, TPattern>(Func<TControl> find, Func<TControl, bool> isEnabled,
        Func<TControl, TPattern?> getPattern, Action<Func<bool>> awaitState) where TPattern : class
    {
        TPattern? pattern = null;
        awaitState(() =>
        {
            var control = find();
            if (!isEnabled(control)) return false;
            pattern = getPattern(control);
            return pattern is not null;
        });
        return pattern!;
    }
}
