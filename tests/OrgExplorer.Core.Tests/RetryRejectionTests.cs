using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class RetryRejectionTests
{
    private const string Pending = "Pending local edits — not yet saved";

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData("Saving complete profile…")]
    [InlineData("Saving private Person note…")]
    [InlineData("Saving private Team note…")]
    public void Queued_retry_waits_for_its_saving_and_retained_pending_completion(string saving)
    {
        using var activity = new ActivityEvents();
        var invocations = 0;
        var reads = 0;
        RetryRejection.AssertCompleted(activity.Subscribe, () =>
        {
            Assert.NotNull(activity.Listener);
            invocations++;
        }, saving, () => true, condition =>
        {
            Assert.False(condition());
            reads++;
            activity.Listener!(saving);
            Assert.False(condition());
            reads++;
            activity.Listener("Ready");
            Assert.False(condition());
            reads++;
            activity.Listener(Pending);
            Assert.True(condition());
            reads++;
        });
        Assert.Equal(1, invocations);
        Assert.Equal(4, reads);
        Assert.Null(activity.Listener);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(null, null)]
    [InlineData("Saving complete profile…", null)]
    [InlineData("Saving complete profile…", "Ready")]
    [InlineData("Saving private Person note…", Pending)]
    public void Preexisting_rejection_without_this_retrys_completion_times_out_once(string? started, string? finished)
    {
        using var activity = new ActivityEvents();
        var invocations = 0;
        var error = Assert.Throws<TimeoutException>(() => RetryRejection.AssertCompleted(activity.Subscribe, () =>
        {
            invocations++;
            if (started is not null) activity.Listener!(started);
            if (finished is not null) activity.Listener!(finished);
        }, "Saving complete profile…", () => true, AwaitState));
        Assert.Contains("Retry", error.Message);
        Assert.Equal(1, invocations);
        Assert.Null(activity.Listener);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(true)]
    [InlineData(false)]
    public void Fast_retry_events_are_retained_but_do_not_replace_the_settled_rejection_assertions(bool rejected)
    {
        using var activity = new ActivityEvents();
        var invocations = 0;
        void Run() => RetryRejection.AssertCompleted(activity.Subscribe, () =>
        {
            invocations++;
            activity.Listener!("Saving complete profile…");
            activity.Listener(Pending);
        }, "Saving complete profile…", () => rejected, AwaitState);
        if (rejected) Run();
        else Assert.Throws<TimeoutException>(Run);
        Assert.Equal(1, invocations);
        Assert.Null(activity.Listener);
    }

    private static void AwaitState(Func<bool> condition)
    {
        for (var poll = 0; poll < 3; poll++)
            if (condition()) return;
        throw new TimeoutException("Retry completion did not arrive within the observation budget.");
    }

    private sealed class ActivityEvents : IDisposable
    {
        internal Action<string>? Listener { get; private set; }
        internal IDisposable Subscribe(Action<string> listener)
        {
            Assert.Null(Listener);
            Listener = listener;
            return this;
        }
        public void Dispose() => Listener = null;
    }
}
