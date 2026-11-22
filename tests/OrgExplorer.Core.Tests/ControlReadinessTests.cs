using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ControlReadinessTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Diagnostic_timeout_retains_the_last_rejected_observation_and_original_failure()
    {
        var state = "before";
        var reports = new List<string>();
        var captures = 0;
        var original = new TimeoutException("Original 20-second row failure");
        var observed = Assert.Throws<TimeoutException>(() => ControlReadiness.AwaitObservedReadiness(
            () => false,
            _ => { captures++; return state; },
            predicate =>
            {
                state = "first rejected row";
                Assert.False(predicate());
                state = "last rejected row";
                Assert.False(predicate());
                state = "later state after the failed predicate";
                throw original;
            }, reports.Add));

        Assert.Same(original, observed);
        Assert.Equal(["before", "last-rejected: last rejected row"], reports);
        Assert.Equal(2, captures);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Diagnostic_success_reports_only_the_initial_and_success_observations_without_extra_retries()
    {
        var state = "initial row";
        var reports = new List<string>();
        var polls = 0;
        ControlReadiness.AwaitObservedReadiness(
            () => { state = ++polls == 1 ? "rejected row" : "accepted row"; return polls == 2; },
            _ => state, AwaitReady, reports.Add);

        Assert.Equal(2, polls);
        Assert.Equal(["before", "success: accepted row"], reports);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Diagnostic_observation_does_not_turn_a_provider_failure_into_another_retry_or_success()
    {
        var polls = 0;
        var reports = new List<string>();
        var original = new InvalidOperationException("Original clickable-point provider failure");
        var observed = Assert.Throws<InvalidOperationException>(() => ControlReadiness.AwaitObservedReadiness(
            () => { polls++; throw original; }, _ => "provider state", AwaitReady, reports.Add));

        Assert.Same(original, observed);
        Assert.Equal(1, polls);
        Assert.Equal(["before"], reports);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Pre_row_visibility_failure_is_observed_without_running_or_requerying_the_later_row_wait()
    {
        var state = "before overlay wait";
        var reports = new List<string>();
        var observations = new List<string>();
        var original = new TimeoutException("Original overlay visibility failure");
        var laterRowWaits = 0;
        var observed = Assert.Throws<TimeoutException>(() =>
        {
            ControlReadiness.AwaitObservedReadiness(
                () => false,
                accepted => { observations.Add(state); return $"{state}; accepted={accepted}"; },
                predicate =>
                {
                    state = "overlay rejected";
                    Assert.False(predicate());
                    state = "later state";
                    throw original;
                }, reports.Add);
            laterRowWaits++;
        });

        Assert.Same(original, observed);
        Assert.Equal(["overlay rejected"], observations);
        Assert.Equal(["before", "last-rejected: overlay rejected; accepted=False"], reports);
        Assert.Equal(0, laterRowWaits);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Search_result_discovery_waits_for_the_queried_note_instead_of_missing_or_stale_profile_rows()
    {
        var observations = 0;
        var published = new Reading("Current Person note", false);
        var result = ControlReadiness.AwaitControl<Reading>(
            () => ++observations switch
            {
                1 => null!,
                2 => new Reading("Current Profile:", false),
                _ => published
            },
            row => row is not null && row.Name.Contains("Current") && row.Name.Contains("Person note"), AwaitReady);

        Assert.Same(published, result);
        Assert.Equal(3, observations);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Permanently_missing_search_result_fails_the_existing_bounded_observation()
    {
        Assert.Throws<TimeoutException>(() => ControlReadiness.AwaitControl<Reading>(
            () => null!, row => row is not null && row.Name.Contains("Current") &&
                row.Name.Contains("Person note"), AwaitReady));
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData("", false, "")]
    [InlineData("", true, "open")]
    [InlineData("orchid", true, "clear,open")]
    public void Browsing_preparation_waits_for_query_discovery_without_repeating_clear_or_open(
        string query, bool needsOverlay, string expectedActions)
    {
        var queryReads = 0;
        var treeReads = 0;
        var actions = new List<string>();

        ControlReadiness.PreparePeopleBrowsing(
            () => ++queryReads == 1
                ? throw new InvalidOperationException("Missing actual UI control: SearchQuery")
                : query,
            () => actions.Add("clear"),
            () => needsOverlay,
            () => actions.Add("open"),
            () => ++treeReads > 1, AwaitReady);

        Assert.Equal(2, queryReads);
        Assert.Equal(expectedActions, string.Join(",", actions));
        Assert.Equal(2, treeReads);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Permanently_missing_query_fails_bounded_preparation_before_any_clear_or_open()
    {
        var actions = 0;
        Assert.Throws<TimeoutException>(() => ControlReadiness.PreparePeopleBrowsing(
            () => throw new InvalidOperationException("Missing actual UI control: SearchQuery"),
            () => actions++,
            () => { actions++; return true; },
            () => actions++,
            () => { actions++; return true; }, AwaitReady));
        Assert.Equal(0, actions);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Accepted_search_reobserves_layout_with_browsing_readiness_in_the_same_bounded_wait(
        bool initialCompact, bool settledCompact)
    {
        var layoutReads = 0;
        var observedModes = new List<bool>();

        ControlReadiness.AwaitSearchBrowsing(
            () => ++layoutReads == 1 ? initialCompact : settledCompact,
            compact =>
            {
                observedModes.Add(compact);
                return compact == settledCompact;
            }, AwaitReady);

        Assert.Equal(2, layoutReads);
        Assert.Equal([initialCompact, settledCompact], observedModes);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(true)]
    [InlineData(false)]
    public void Team_name_reading_waits_for_post_cancel_resize_discovery_without_repeating_an_action(bool initiallyMissing)
    {
        var observations = 0;
        var pending = new Reading("Previous label", true);
        var displayed = new Reading("Platform", false);
        var observed = ControlReadiness.AwaitControl(
            () => ++observations == 1
                ? initiallyMissing ? throw new InvalidOperationException("Missing actual UI control: TeamNameSegment") : pending
                : displayed,
            control => !control.Offscreen && control.Name == "Platform", AwaitReady);

        Assert.Same(displayed, observed);
        Assert.Equal(2, observations);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Missing_or_wrong_Team_name_reading_still_fails_the_same_bounded_observation()
    {
        Assert.Throws<TimeoutException>(() => ControlReadiness.AwaitControl<Reading>(
            () => throw new InvalidOperationException("Missing actual UI control: TeamNameSegment"),
            _ => true, AwaitReady));
        Assert.Throws<TimeoutException>(() => ControlReadiness.AwaitControl(
            () => new Reading("Wrong owner label", false), control => control.Name == "Platform", AwaitReady));
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Fully_visible_reading_needs_no_scroll_action_but_a_scrollable_axis_is_requested_once(bool scrollable, int expected)
    {
        var actions = 0;
        ControlReadiness.ScrollToTopIfScrollable(() => scrollable, () => actions++);
        Assert.Equal(expected, actions);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void A_scrollable_provider_failure_propagates_without_repeating_the_action()
    {
        var actions = 0;
        var original = new InvalidOperationException("Could not open the process token");
        var observed = Assert.Throws<InvalidOperationException>(() =>
            ControlReadiness.ScrollToTopIfScrollable(() => true, () => { actions++; throw original; }));
        Assert.Same(original, observed);
        Assert.Equal(1, actions);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(true, 100, 100)]
    [InlineData(false, 0, 100)]
    [InlineData(false, 100, 0)]
    public void Viewport_preparation_is_read_only_until_a_visible_positive_provider_is_available(bool offscreen, double width, double height)
    {
        var lookups = 0;
        var patterns = 0;
        var actions = 0;
        Action action = () => actions++;
        var ready = ControlReadiness.AwaitPattern(
            () => ++lookups switch
            {
                1 => throw MissingControl(),
                2 => new Viewport(offscreen, width, height),
                _ => new Viewport(false, 100, 100)
            },
            viewport => !viewport.Offscreen && viewport.Width > 0 && viewport.Height > 0,
            _ => { patterns++; return action; }, AwaitReady);

        Assert.Equal(3, lookups);
        Assert.Equal(1, patterns);
        Assert.Equal(0, actions);
        ready();
        Assert.Equal(1, actions);
    }

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
            candidate => ++patternReads == 1 ? null : candidate.Pattern,
            AwaitReady);

        Assert.Same(pattern, ready);
        Assert.Equal(4, lookups);
        Assert.Equal(2, patternReads);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void A_permanently_unavailable_pattern_fails_the_existing_bounded_preparation()
    {
        Assert.Throws<TimeoutException>(() => ControlReadiness.AwaitPattern<Control, object>(
            () => new Control(true, new object()), candidate => candidate.Enabled, _ => null, AwaitReady));
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
    private sealed record Viewport(bool Offscreen, double Width, double Height);
    private sealed record Reading(string Name, bool Offscreen);
}
