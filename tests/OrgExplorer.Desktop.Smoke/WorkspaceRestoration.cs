using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static class WorkspaceRestoration
{
    internal sealed record Observation(string Path, string Activity, string Freshness, string Email,
        bool? RecoveryIsOffscreen);

    internal static void AssertRemembered(Func<Observation> observe, string expectedPath,
        WorkspaceSettings preferences, Action<Func<bool>> awaitState)
    {
        if (preferences.PreferenceError is { } error) throw new InvalidOperationException(error);
        var view = preferences.GetLastView(expectedPath) ??
            throw new InvalidOperationException("The rich reopen fixture has no independently remembered Person.");
        AssertRestored(observe, expectedPath, awaitState, view.PersonEmail);
    }

    internal static void AssertRestored(Func<Observation> observe, string expectedPath, Action<Func<bool>> awaitState,
        string expectedEmail = "")
    {
        Observation? ready = null;
        awaitState(() =>
        {
            ready = observe();
            return ready.Path == expectedPath && ready.Activity == "Ready" &&
                ready.Freshness == "Current validated local Workspace." && ready.Email == expectedEmail;
        });
        var restored = ready!;
        if (restored.Email != expectedEmail || restored.RecoveryIsOffscreen == false)
            throw new InvalidOperationException("Opening did not restore the expected Person or retained a startup recovery error.");
    }
}
