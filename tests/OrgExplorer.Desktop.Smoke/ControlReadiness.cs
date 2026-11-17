namespace OrgExplorer.Desktop.Smoke;

internal static class ControlReadiness
{
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
