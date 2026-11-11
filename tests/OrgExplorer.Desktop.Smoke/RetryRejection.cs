namespace OrgExplorer.Desktop.Smoke;

internal static class RetryRejection
{
    internal static void AssertCompleted(Func<Action<string>, IDisposable> subscribe, Action invoke,
        string savingActivity, Func<bool> isRejected, Action<Func<bool>> awaitState)
    {
        var phase = 0;
        var lastActivity = "";
        using var observation = subscribe(activity =>
        {
            Volatile.Write(ref lastActivity, activity);
            if (activity == savingActivity) Interlocked.CompareExchange(ref phase, 1, 0);
            else if (activity.StartsWith("Pending local edits", StringComparison.Ordinal))
                Interlocked.CompareExchange(ref phase, 2, 1);
        });
        invoke();
        try { awaitState(() => Volatile.Read(ref phase) == 2 && isRejected()); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Retry did not complete '{savingActivity}' with retained-writing rejection. " +
                "Last observed activity: " + Volatile.Read(ref lastActivity), error);
        }
    }
}
