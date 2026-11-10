namespace OrgExplorer.Desktop.Smoke;

internal static class ControlReadiness
{
    internal static TPattern AwaitPattern<TControl, TPattern>(Func<TControl> find, Func<TControl, bool> isEnabled,
        Func<TControl, TPattern> getPattern, Action<Func<bool>> awaitState) where TPattern : class
    {
        TPattern? pattern = null;
        awaitState(() =>
        {
            var control = find();
            if (!isEnabled(control)) return false;
            pattern = getPattern(control);
            return true;
        });
        return pattern!;
    }
}
