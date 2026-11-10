namespace OrgExplorer.Desktop.Smoke;

internal static class TreeItemRealization
{
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
