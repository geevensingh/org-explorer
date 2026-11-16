using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ImportNavigationTimingTests
{
    [Fact]
    public void Slow_fresh_observation_stays_in_the_original_total_without_repeating_or_warming_the_request()
    {
        double now = 0;
        var requests = 0;
        var reads = 0;
        var timing = new ImportNavigationTiming("select-person", "avery@example.com", () => now);
        Assert.Equal(0, reads);
        timing.Request(() => { requests++; now += 20; });
        Assert.False(timing.Observe(() => timing.Query(ImportNavigationTiming.Field.Email, () =>
        {
            reads++;
            now += 180;
            return false;
        }) && timing.Query<bool>(ImportNavigationTiming.Field.Heading, () => throw new InvalidOperationException())));
        now += 1;
        Assert.True(timing.Observe(() =>
            timing.Query(ImportNavigationTiming.Field.Email, () => { reads++; now += 3; return true; }) &&
            timing.Query(ImportNavigationTiming.Field.Heading, () => { reads++; now += 4; return true; })));
        timing.Complete(now);
        var result = timing.Snapshot();
        Assert.Equal(1, requests);
        Assert.Equal(3, reads);
        Assert.Equal(208, result.TotalMs);
        Assert.Equal(0, result.RequestStartedMs);
        Assert.Equal(20, result.RequestReturnedMs);
        Assert.Equal(2, result.Polls);
        Assert.True(result.Ready);
        Assert.Equal(new ImportNavigationTiming.QueryTiming(2, 2, 183, 180), result.Email);
        Assert.Equal(new ImportNavigationTiming.QueryTiming(1, 1, 4, 4), result.Heading);
        Assert.Null(result.ExceptionType);
        Assert.True(result.TotalMs > 100);
    }

    [Fact]
    public void Interrupted_fresh_read_retains_bounded_incomplete_evidence_and_the_original_exception()
    {
        double now = 0;
        var timing = new ImportNavigationTiming("select-person", "avery@example.com", () => now);
        timing.Request(() => now += 10);
        var failure = new InvalidOperationException("Private writing must not become diagnostics");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => timing.Observe(() =>
            timing.Query<bool>(ImportNavigationTiming.Field.Email, () => { now += 125; throw failure; }))));
        var result = timing.Snapshot();
        Assert.Equal(135, result.TotalMs);
        Assert.False(result.Ready);
        Assert.Equal(new ImportNavigationTiming.QueryTiming(1, 0, 125, 125), result.Email);
        Assert.Equal(new ImportNavigationTiming.QueryTiming(0, 0, 0, 0), result.Heading);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.ExceptionType);
        Assert.Equal(failure.HResult, result.HResult);
        Assert.DoesNotContain(failure.Message, System.Text.Json.JsonSerializer.Serialize(result));
    }
}
