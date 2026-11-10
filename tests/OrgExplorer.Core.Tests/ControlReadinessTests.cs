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
