using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class NavigationRejectionTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Queued_navigation_is_invoked_once_and_observed_until_the_actual_rejection_feedback_arrives()
    {
        var invocations = 0;
        var reads = 0;
        NavigationRejection.AssertPending(() => invocations++,
            () => ++reads == 1
                ? "Pending local profile edits — not yet saved."
                : "Navigation blocked: profile edits are pending. Please wait for saving to finish before leaving.",
            () => "avery@example.com", "avery@example.com", AwaitFeedback);
        Assert.Equal(1, invocations);
        Assert.True(reads >= 2);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData("Pending local profile edits — not yet saved.")]
    [InlineData("Saved locally.")]
    [InlineData("Navigation blocked: profile edits were NOT saved. Retry or discard unsaved profile edits before leaving.")]
    [InlineData("Navigation blocked: profile edits are pending.")]
    public void Missing_or_wrong_rejection_feedback_fails_without_invoking_navigation_again(string status)
    {
        var invocations = 0;
        var reads = 0;
        var error = Assert.Throws<TimeoutException>(() => NavigationRejection.AssertPending(
            () => invocations++, () => { reads++; return status; },
            () => "avery@example.com", "avery@example.com", AwaitFeedback));
        Assert.Contains(status, error.Message);
        Assert.Equal(1, invocations);
        Assert.Equal(3, reads);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData("Pending local profile edits — not yet saved.")]
    [InlineData("Navigation blocked: profile edits are pending. Please wait for saving to finish before leaving.")]
    public void Changed_destination_fails_immediately_even_while_waiting_for_feedback(string status)
    {
        var invocations = 0;
        var reads = 0;
        var error = Assert.Throws<InvalidOperationException>(() => NavigationRejection.AssertPending(
            () => invocations++, () => { reads++; return status; },
            () => "casey@example.com", "avery@example.com", AwaitFeedback));
        Assert.Contains("casey@example.com", error.Message);
        Assert.Equal(1, invocations);
        Assert.Equal(1, reads);
    }

    private static void AwaitFeedback(Func<bool> condition)
    {
        for (var poll = 0; poll < 3; poll++)
            if (condition()) return;
        throw new TimeoutException("Rejection feedback did not arrive within the observation budget.");
    }
}
