using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class MainReadingNavigationTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Main_viewport_exposes_the_exact_deep_text_with_distinct_progress_checked_positions()
    {
        var viewport = new Viewport();

        MainReadingNavigation.Reveal(() => viewport.TargetVisible, viewport.Read, viewport.Move, AwaitPosition);

        Assert.True(viewport.TargetVisible);
        Assert.Equal(0, viewport.Requests[0]);
        Assert.Equal(viewport.Requests.Count, viewport.Requests.Distinct().Count());
        Assert.All(viewport.Requests.Skip(1).Zip(viewport.Requests), step => Assert.True(step.First > step.Second));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Already_visible_text_does_not_request_an_invalid_scroll_on_a_fitting_page()
    {
        MainReadingNavigation.Reveal(() => true,
            () => throw new InvalidOperationException("No scroll metadata is needed."),
            _ => throw new InvalidOperationException("No scroll action is needed."), AwaitPosition);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Unchanged_viewport_times_out_without_replaying_the_position_request()
    {
        var requests = new List<double>();

        Assert.Throws<TimeoutException>(() => MainReadingNavigation.Reveal(() => false,
            () => new(true, 17, 4), requests.Add, AwaitPosition));

        Assert.Equal(0, Assert.Single(requests));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Scroll_failure_propagates_unchanged_after_one_action()
    {
        var original = new InvalidOperationException("Native scroll failed.");
        var actions = 0;

        var actual = Assert.Throws<InvalidOperationException>(() => MainReadingNavigation.Reveal(() => false,
            () => new(true, 17, 4), _ => { actions++; throw original; }, AwaitPosition));

        Assert.Same(original, actual);
        Assert.Equal(1, actions);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Text_that_never_becomes_visible_fails_at_the_end_without_repeating_the_endpoint()
    {
        var viewport = new Viewport();

        Assert.Throws<InvalidOperationException>(() =>
            MainReadingNavigation.Reveal(() => false, viewport.Read, viewport.Move, AwaitPosition));

        Assert.Equal(100, viewport.Requests[^1]);
        Assert.Equal(viewport.Requests.Count, viewport.Requests.Distinct().Count());
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false, 100)]
    [InlineData(true, 0)]
    [InlineData(true, 100)]
    [InlineData(true, double.NaN)]
    public void Missing_text_with_unusable_scroll_metadata_is_not_reported_as_visible(bool scrollable, double viewPercent)
    {
        var actions = 0;

        Assert.Throws<InvalidOperationException>(() => MainReadingNavigation.Reveal(() => false,
            () => new(scrollable, 0, viewPercent), _ => actions++, AwaitPosition));

        Assert.Equal(0, actions);
    }

    private static void AwaitPosition(Func<bool> condition)
    {
        for (var poll = 0; poll < 3; poll++)
            if (condition()) return;
        throw new TimeoutException("The exact reading target or requested viewport position was not reached.");
    }

    private sealed class Viewport
    {
        private const double Extent = 5900;
        private const double Height = 255;
        private double offset = 970;

        internal List<double> Requests { get; } = [];
        internal bool TargetVisible => offset <= 2180 && offset + Height >= 2202;
        internal MainReadingNavigation.Position Read() =>
            new(true, 100 * offset / (Extent - Height), 100 * Height / Extent);
        internal void Move(double percent)
        {
            Requests.Add(percent);
            offset = percent * (Extent - Height) / 100;
        }
    }
}
