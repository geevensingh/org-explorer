using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class TreeItemRealizationTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Virtualized_target_must_be_realized_before_waiting_for_visible_geometry()
    {
        var target = new object();
        var realizations = 0;
        var pollsAfterRealization = 0;
        var result = TreeItemRealization.AwaitVisible(() => target,
            item => { Assert.Same(target, item); realizations++; },
            item => realizations > 0 && ++pollsAfterRealization >= 2,
            AwaitProvider);
        Assert.Same(target, result);
        Assert.Equal(1, realizations);
        Assert.Equal(2, pollsAfterRealization);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_target_or_unrendered_geometry_still_times_out(bool targetExists)
    {
        var target = new object();
        var realizations = 0;
        Assert.Throws<TimeoutException>(() => TreeItemRealization.AwaitVisible(
            () => targetExists ? target : null,
            _ => realizations++,
            _ => false,
            AwaitProvider));
        Assert.Equal(targetExists ? 1 : 0, realizations);
    }

    private static void AwaitProvider(Func<bool> condition)
    {
        for (var poll = 0; poll < 3; poll++)
            if (condition()) return;
        throw new TimeoutException("The virtualized target never acquired visible geometry.");
    }
}
