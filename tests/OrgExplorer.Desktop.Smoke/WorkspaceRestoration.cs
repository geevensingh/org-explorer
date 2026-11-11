namespace OrgExplorer.Desktop.Smoke;

internal static class WorkspaceRestoration
{
    internal sealed record Observation(string Path, string Activity, string Freshness, string Email,
        bool? RecoveryIsOffscreen);

    internal static void AssertRestored(Func<Observation> observe, string expectedPath, Action<Func<bool>> awaitState)
    {
        Observation? ready = null;
        awaitState(() =>
        {
            ready = observe();
            return ready.Path == expectedPath && ready.Activity == "Ready" &&
                ready.Freshness == "Current validated local Workspace.";
        });
        var restored = ready!;
        if (restored.Email.Length != 0 || restored.RecoveryIsOffscreen == false)
            throw new InvalidOperationException("Opening restored a selected Person or retained a false startup failure.");
    }
}
