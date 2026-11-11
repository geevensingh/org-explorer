using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class WorkspaceRestorationTests
{
    private const string Workspace = @"C:\fixtures\workspace";

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [Trait("Category", "Runner")]
    public void Successful_restore_accepts_absent_or_offscreen_recovery_content(bool? recoveryIsOffscreen)
    {
        WorkspaceRestoration.AssertRestored(() => new(Workspace, "Ready", "Current validated local Workspace.", "", recoveryIsOffscreen),
            Workspace, AwaitReady);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("avery@example.com", null)]
    [Trait("Category", "Runner")]
    public void A_visible_recovery_error_or_restored_person_is_not_a_success(string email, bool? recoveryIsOffscreen)
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceRestoration.AssertRestored(
            () => new(Workspace, "Ready", "Current validated local Workspace.", email, recoveryIsOffscreen),
            Workspace, AwaitReady));
    }

    [Theory]
    [InlineData(@"C:\fixtures\other", "Ready", "Current validated local Workspace.")]
    [InlineData(Workspace, "Opening Workspace...", "Current validated local Workspace.")]
    [InlineData(Workspace, "Ready", "STALE / unavailable")]
    [Trait("Category", "Runner")]
    public void Missing_recovery_content_cannot_substitute_for_the_expected_path_and_completed_validated_open(
        string path, string activity, string freshness)
    {
        Assert.Throws<TimeoutException>(() => WorkspaceRestoration.AssertRestored(
            () => new(path, activity, freshness, "", null), Workspace, AwaitReady));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Pending_restore_is_observed_until_ready_without_reopening_or_rereading_the_completed_observation()
    {
        var observations = new Queue<WorkspaceRestoration.Observation>([
            new(Workspace, "Opening Workspace...", "No Workspace is open.", "", null),
            new(Workspace, "Ready", "Current validated local Workspace.", "", null)
        ]);
        WorkspaceRestoration.AssertRestored(observations.Dequeue, Workspace, AwaitReady);
        Assert.Empty(observations);
    }

    private static void AwaitReady(Func<bool> condition)
    {
        for (var poll = 0; poll < 3; poll++)
            if (condition()) return;
        throw new TimeoutException("The scripted startup observation did not become ready.");
    }
}
