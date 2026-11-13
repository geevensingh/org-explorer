using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ControlReadinessTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Enabled_control_supplies_its_pattern_without_an_unguarded_second_lookup()
    {
        var lookups = 0;
        var pattern = new object();
        var control = new Control(true, pattern);

        var ready = ControlReadiness.AwaitPattern(
            () => ++lookups == 1 ? control : throw MissingControl(),
            candidate => candidate.Enabled, candidate => candidate.Pattern, AwaitReady);

        Assert.Same(pattern, ready);
        Assert.Equal(1, lookups);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Missing_disabled_and_temporarily_unavailable_patterns_share_the_bounded_preparation_wait()
    {
        var lookups = 0;
        var patternReads = 0;
        var pattern = new object();

        var ready = ControlReadiness.AwaitPattern(
            () => ++lookups switch
            {
                1 => throw MissingControl(),
                2 => new Control(false, pattern),
                _ => new Control(true, pattern)
            },
            candidate => candidate.Enabled,
            candidate => ++patternReads == 1 ? throw MissingControl() : candidate.Pattern,
            AwaitReady);

        Assert.Same(pattern, ready);
        Assert.Equal(4, lookups);
        Assert.Equal(2, patternReads);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Collapsed_secondary_context_waits_for_post_resize_discovery_without_changing_expansion()
    {
        var observations = new List<string>();
        ControlReadiness.AssertCollapsed(["CurrentTeamContext", "HistoryAccess"], section =>
        {
            observations.Add(section);
            if (observations.Count == 1)
                throw new InvalidOperationException("Missing actual UI control: CurrentTeamContext");
            return true;
        }, AwaitReady);

        Assert.Equal(["CurrentTeamContext", "CurrentTeamContext", "HistoryAccess"], observations);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Expanded_secondary_context_fails_without_waiting_for_or_repairing_later_collapse()
    {
        var observations = 0;
        var error = Assert.Throws<InvalidOperationException>(() =>
            ControlReadiness.AssertCollapsed(["CurrentTeamContext"], _ => ++observations > 1, AwaitReady));

        Assert.Contains("CurrentTeamContext must not precede everyday Person writing", error.Message);
        Assert.Equal(1, observations);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Permanently_missing_secondary_context_still_fails_the_existing_bounded_wait()
    {
        Assert.Throws<TimeoutException>(() => ControlReadiness.AssertCollapsed(["CurrentTeamContext"],
            _ => throw new InvalidOperationException("Missing actual UI control: CurrentTeamContext"), AwaitReady));
    }

    private static InvalidOperationException MissingControl() => new("Missing actual UI control: CheckForUpdates");

    private static void AwaitReady(Func<bool> condition)
    {
        for (var poll = 0; poll < 4; poll++)
        {
            try { if (condition()) return; }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Missing actual UI control:", StringComparison.Ordinal)) { }
        }
        throw new TimeoutException("The scripted UI provider did not supply an enabled control and pattern.");
    }

    private sealed record Control(bool Enabled, object Pattern);
}
