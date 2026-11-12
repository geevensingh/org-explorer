using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class TreeItemRealizationTests
{
    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false)]
    [InlineData(true)]
    public void Expanded_browsing_does_not_repeat_a_provider_action_that_navigates_and_closes_the_overlay(bool initializeBranch)
    {
        var owner = "avery@example.com";
        var overlayOpen = true;
        TreeItemRealization.AwaitExpanded(initializeBranch, () => true,
            () => { owner = "morgan@example.com"; overlayOpen = false; }, AwaitProvider);
        Assert.Equal("avery@example.com", owner);
        Assert.True(overlayOpen);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Collapsed_initial_branch_expands_once_then_waits_without_repeating_the_action()
    {
        var expansions = 0;
        var observationsAfterRequest = 0;
        TreeItemRealization.AwaitExpanded(true,
            () => expansions > 0 && ++observationsAfterRequest >= 2,
            () => expansions++, AwaitProvider);
        Assert.Equal(1, expansions);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Collapsed_restored_branch_fails_instead_of_repairing_lost_expansion()
    {
        var expanded = false;
        Assert.Throws<TimeoutException>(() => TreeItemRealization.AwaitExpanded(false, () => expanded,
            () => expanded = true, AwaitProvider));
        Assert.False(expanded);
    }

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
        throw new TimeoutException("The scripted tree provider never reached its required state.");
    }
}
