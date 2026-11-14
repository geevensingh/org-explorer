namespace OrgExplorer.Desktop.Smoke;

internal static class MainReadingNavigation
{
    internal sealed record Position(bool IsScrollable, double Percent, double ViewPercent);

    internal static void Reveal(Func<bool> targetVisible, Func<Position> position, Action<double> scrollTo,
        Action<Func<bool>> awaitState)
    {
        if (targetVisible()) return;
        var initial = position();
        RequireScrollable(initial);
        if (initial.Percent > 0)
        {
            scrollTo(0);
            awaitState(() => position().Percent == 0);
        }
        while (!targetVisible())
        {
            var before = position();
            RequireScrollable(before);
            if (before.Percent >= 100)
                throw new InvalidOperationException("The main reading viewport reached its end without exposing the exact text.");
            // UIA view size uses the full extent; scroll percent uses only the scrollable range.
            var next = Math.Min(100, before.Percent + 50 * before.ViewPercent / (100 - before.ViewPercent));
            if (next <= before.Percent)
                throw new InvalidOperationException("The main reading viewport cannot make a distinct forward step.");
            scrollTo(next);
            awaitState(() => position().Percent > before.Percent);
        }
    }

    private static void RequireScrollable(Position position)
    {
        if (!position.IsScrollable || !double.IsFinite(position.Percent) || position.Percent is < 0 or > 100 ||
            !double.IsFinite(position.ViewPercent) || position.ViewPercent is <= 0 or >= 100)
            throw new InvalidOperationException("The main viewport cannot scroll to expose the requested reading text.");
    }
}
