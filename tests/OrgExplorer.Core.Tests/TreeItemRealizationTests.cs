using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class TreeItemRealizationTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Restored_rows_keep_their_positions_when_a_larger_viewport_exposes_another_row()
    {
        var original = new TreeItemRealization.BrowsingPosition(70,
            [new("browse04@example.com", 12, -3), new("browse05@example.com", 12, 21)]);
        var restored = new TreeItemRealization.BrowsingPosition(76.79611650485437,
            [new("browse04@example.com", 12, -3), new("browse05@example.com", 12, 21),
             new("browse06@example.com", 12, 45)]);

        Assert.True(TreeItemRealization.IsBrowsingPositionRestored(original, restored));
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData("browse04@example.com", 14, -3)]
    [InlineData("browse04@example.com", 12, 0)]
    [InlineData("replacement@example.com", 12, -3)]
    public void Matching_percentages_cannot_hide_a_moved_or_missing_original_Person(string email, double left, double top)
    {
        var original = new TreeItemRealization.BrowsingPosition(70,
            [new("browse04@example.com", 12, -3), new("browse05@example.com", 12, 21)]);
        var changed = new TreeItemRealization.BrowsingPosition(70,
            [new(email, left, top), new("browse05@example.com", 12, 21)]);

        Assert.False(TreeItemRealization.IsBrowsingPositionRestored(original, changed));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Browse_baseline_measures_the_fixture_end_once_before_the_single_target_scroll()
    {
        var actions = new List<double>();
        var observations = 0;
        var destination = 0d;
        TreeItemRealization.PrepareMeasuredScrollPosition(70, percent =>
        {
            actions.Add(percent);
            destination = percent;
            observations = 0;
        }, () => ++observations < 3 ? 0 : destination, AwaitProvider);

        Assert.Equal([100d, 70d], actions);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreached_browse_preparation_positions_fail_without_replaying_scroll_actions(bool endReached)
    {
        var actions = new List<double>();
        Assert.Throws<TimeoutException>(() => TreeItemRealization.PrepareMeasuredScrollPosition(70,
            actions.Add, () => endReached ? 100 : 0, AwaitProvider));

        Assert.Equal(endReached ? [100d, 70d] : [100d], actions);
    }

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
