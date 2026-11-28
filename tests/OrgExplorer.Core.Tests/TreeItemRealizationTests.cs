using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class TreeItemRealizationTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Reporting_row_reads_retain_live_state_without_repeating_discovery()
    {
        var identity = "morgan@example.com";
        var row = new ReportingRowState();
        var discoveries = 0;
        var rows = TreeItemRealization.ReportingRows(_ => { discoveries++; return row; },
            (current, email) => current.Email == email);
        Assert.False(rows(identity).Expanded);
        row.Expanded = true;
        Assert.True(rows(identity).Expanded);
        Assert.Equal(1, discoveries);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Recycled_reporting_identity_is_rediscovered_before_input()
    {
        var old = new ReportingRowState();
        var replacement = new ReportingRowState();
        var current = old;
        var discoveries = 0;
        var rows = TreeItemRealization.ReportingRows(_ => { discoveries++; return current; },
            (row, email) => row.Email == email);
        Assert.Same(old, rows("morgan@example.com"));
        old.Email = "avery@example.com";
        current = replacement;
        Assert.Same(replacement, rows("morgan@example.com"));
        Assert.Equal(2, discoveries);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Missing_reporting_identity_refuses_input_without_lookup_replay()
    {
        var discoveries = 0;
        var rows = TreeItemRealization.ReportingRows(_ => { discoveries++; return new object(); }, (_, _) => false);
        var inputs = 0;
        Assert.Throws<InvalidOperationException>(() => { rows("missing@example.com"); inputs++; });
        Assert.Equal(1, discoveries);
        Assert.Equal(0, inputs);
    }

    private sealed class ReportingRowState
    {
        public string Email { get; set; } = "morgan@example.com";
        public bool Expanded { get; set; }
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Reporting_row_diagnostics_retain_only_the_latest_boundaries_including_the_failed_row()
    {
        var trace = new TreeItemRealization.ReportingRowTrace<string>();
        foreach (var row in Enumerable.Range(0, 6))
            foreach (var phase in new[] { "before", "after", "final" }) trace.Retain($"{row}:{phase}");

        Assert.Equal(Enumerable.Range(2, 4).SelectMany(row =>
            new[] { $"{row}:before", $"{row}:after", $"{row}:final" }), trace.Recent);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Reporting_row_diagnostic_output_failures_do_not_replace_the_original_timeout_or_skip_the_failure_frame()
    {
        var trace = new TreeItemRealization.ReportingRowTrace<string>();
        var original = new TimeoutException("Original 180-second row observation failed.");
        var outputs = new List<string>();
        trace.AttemptOutput(original, () =>
        {
            outputs.Add("trace");
            throw new IOException("Trace output refused.");
        });
        trace.AttemptOutput(original, () => outputs.Add("failure-frame"));

        Assert.Equal(["trace", "failure-frame"], outputs);
        Assert.Equal(1, trace.Failures.Count);
        Assert.Equal("System.IO.IOException", trace.Failures.LastFailure!.ExceptionType);
        Assert.Equal(trace.Failures, original.Data["ReportingRowDiagnosticFailures"]);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Reporting_row_diagnostics_retain_observation_refusal_even_if_error_reporting_also_fails()
    {
        var trace = new TreeItemRealization.ReportingRowTrace<string>();
        var original = new TimeoutException("Original row state did not arrive.");
        trace.RecordFailure(new InvalidOperationException("Row cache unavailable."));
        trace.AttemptOutput(original, () => throw new IOException("Metadata output unavailable."));
        trace.AttemptOutput(original, () => throw new UnauthorizedAccessException("Failure frame unavailable."));

        Assert.Equal(3, trace.Failures.Count);
        Assert.Equal(trace.Failures, original.Data["ReportingRowDiagnosticFailures"]);
        Assert.Equal("System.UnauthorizedAccessException", trace.Failures.LastFailure!.ExceptionType);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(92, 2)]
    [InlineData(185, 2)]
    [InlineData(92, 1)]
    public void Outside_fixture_preparation_keeps_the_first_leaf_menu_header_reachable_in_a_constrained_viewport(
        double height, int branchCount)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new List<(string Email, TreeItemRealization.MenuBranchInput Input)>();
        var row = (Left: 131d, Top: 509d, Width: 303d, Height: 28d);
        var viewport = (Left: 112d, Top: 452d, Width: 340d, Height: height);
        var managerTop = 481d;
        var clicks = 0;
        var realizations = 0;
        TreeItemRealization.PrepareMenuBranches(branchCount == 2
            ? ["alex@example.com", "menu-outside@example.com"] : ["alex@example.com"],
            expanded.Contains, (email, input) =>
            {
                inputs.Add((email, input));
                expanded.Add(email);
                row.Top += 28;
                managerTop += 28;
            }, AwaitProvider);
        bool ContextVisible() => managerTop >= 452 && managerTop + 20 <= 452 + height &&
            row.Top >= 452 && row.Top + 20 <= 452 + height;
        var target = new object();
        TreeItemRealization.AwaitVisible(() => target, _ =>
        {
            if (ContextVisible()) return;
            realizations++;
            managerTop = 488;
            row.Top = 516;
        }, _ => ContextVisible(), AwaitProvider);

        var point = TreeItemRealization.ReportingHeaderPoint(row, viewport, 1);
        clicks++;

        Assert.InRange(point.X, 112, 451);
        Assert.InRange(point.Y, 452, 452 + height - 1);
        Assert.Equal(1, clicks);
        Assert.Equal(height == 92 ? 1 : 0, realizations);
        Assert.Equal(branchCount == 2
            ? [("alex@example.com", TreeItemRealization.MenuBranchInput.DisclosureToggle),
                ("menu-outside@example.com", TreeItemRealization.MenuBranchInput.DisclosureToggle)]
            : [("alex@example.com", TreeItemRealization.MenuBranchInput.DisclosureToggle)], inputs);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Refused_initial_menu_realization_never_replays_fixture_disclosures_or_issues_a_mouse_request()
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var disclosures = 0;
        var realizations = 0;
        var mouseRequests = 0;
        var target = new object();
        var row = (Left: 131d, Top: 509d, Width: 303d, Height: 28d);
        var viewport = (Left: 112d, Top: 452d, Width: 340d, Height: 92d);

        Assert.Throws<TimeoutException>(() =>
        {
            TreeItemRealization.PrepareMenuBranches(["alex@example.com", "menu-outside@example.com"],
                expanded.Contains, (email, _) => { disclosures++; expanded.Add(email); row.Top += 28; }, AwaitProvider);
            TreeItemRealization.AwaitVisible(() => target, _ => realizations++,
                _ => row.Top >= 452 && row.Top + 20 <= 544, AwaitProvider);
            TreeItemRealization.ReportingHeaderPoint(row, viewport, 1);
            mouseRequests++;
        });

        Assert.Equal(2, disclosures);
        Assert.Equal(1, realizations);
        Assert.Equal(0, mouseRequests);
        Assert.Equal(565, row.Top);
        Assert.Throws<InvalidOperationException>(() => TreeItemRealization.ReportingHeaderPoint(row, viewport, 1));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Scale_retention_targets_the_reporting_row_not_the_preceding_Favorite_with_the_same_email()
    {
        const string email = "person00000@example.com";
        // Run45748466369's UI dump lists Favorites before People, with both owning this identity.
        var favorite = new ObservedRow(email, false);
        var reporting = new ObservedRow(email, true);
        var selected = TreeItemRealization.ReportingTarget([favorite, reporting], email,
            row => row.Email, row => row.IsReportingRow);

        Assert.Same(reporting, selected);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Scale_retention_does_not_substitute_a_Favorite_when_the_reporting_target_is_missing()
    {
        var favorite = new ObservedRow("person00000@example.com", false);

        Assert.Throws<InvalidOperationException>(() => TreeItemRealization.ReportingTarget([favorite],
            favorite.Email, row => row.Email, row => row.IsReportingRow));
    }

    private sealed record ObservedRow(string Email, bool IsReportingRow);

    [Fact]
    [Trait("Category", "Runner")]
    public void Menu_retention_captures_once_per_boundary_and_never_reuses_a_frame_after_input()
    {
        var captures = 0;
        var writing = "  Exact retained profile\r\n";
        var contentFrames = new List<(int Capture, string Writing)>();
        var targetFrames = new List<(int Capture, string Writing)>();
        (int Capture, string Writing) Capture() => (++captures, writing);

        TreeItemRealization.AssertMenuRetention(TreeItemRealization.MenuRetentionBoundary.Command,
            Capture, contentFrames.Add, targetFrames.Add);
        writing = "New writing after a later input";
        TreeItemRealization.AssertMenuRetention(TreeItemRealization.MenuRetentionBoundary.Command,
            Capture, contentFrames.Add, targetFrames.Add);

        Assert.Equal(2, captures);
        Assert.Equal([(1, "  Exact retained profile\r\n"), (2, "New writing after a later input")], contentFrames);
        Assert.Equal(contentFrames, targetFrames);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false)]
    [InlineData(true)]
    public void Batched_retention_still_rejects_lost_content_and_an_invisible_command_target(bool contentLost)
    {
        var captures = 0;
        var error = Assert.Throws<InvalidOperationException>(() =>
            TreeItemRealization.AssertMenuRetention(TreeItemRealization.MenuRetentionBoundary.Command,
                () => { captures++; return (ContentLost: contentLost, TargetTop: 461d); },
                frame => { if (frame.ContentLost) throw new InvalidOperationException("Profile/writing or People changed."); },
                frame => { if (frame.TargetTop < 478) throw new InvalidOperationException("Collapse subtree lost its target row."); }));

        Assert.Equal(1, captures);
        Assert.Equal(contentLost ? "Profile/writing or People changed." : "Collapse subtree lost its target row.", error.Message);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Menu_preparation_targets_the_disclosure_identity_without_a_scrolling_pointer_request()
    {
        var expanded = false;
        var targetTop = 479d;
        var focus = "SearchQuery";
        var inputs = new List<TreeItemRealization.MenuBranchInput>();
        TreeItemRealization.PrepareMenuBranch(() => expanded && targetTop >= 478, input =>
        {
            inputs.Add(input);
            if (input == TreeItemRealization.MenuBranchInput.DisclosureArrow)
            {
                // Captured run45717484494: pointer return refocused V-header and moved Casey offscreen without toggling.
                targetTop = 433;
                focus = "V-header@example.com";
            }
            else if (input == TreeItemRealization.MenuBranchInput.DisclosureToggle) expanded = true;
        }, AwaitProvider);

        Assert.Equal([TreeItemRealization.MenuBranchInput.DisclosureToggle], inputs);
        Assert.True(expanded);
        Assert.Equal("SearchQuery", focus);
        Assert.Equal(479, targetTop);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Initially_shown_archived_list_is_hidden_for_real_menu_checks_and_restored_once()
    {
        var archivedShown = true;
        var inputs = new List<bool>();
        void Toggle()
        {
            archivedShown = !archivedShown;
            inputs.Add(archivedShown);
        }
        var initiallyShown = TreeItemRealization.PrepareArchivedListForMenu(() => archivedShown,
            Toggle, () => !archivedShown, AwaitProvider);

        TreeItemRealization.AssertMenuRetention(TreeItemRealization.MenuRetentionBoundary.Command, () => { }, () =>
        {
            // Run45707651375's shown archived list left the real reporting viewport Empty.
            if (archivedShown) throw new InvalidOperationException("Collapse subtree lost its target row.");
        });
        TreeItemRealization.RestoreArchivedListAfterMenu(initiallyShown, () => archivedShown, Toggle, AwaitProvider);

        Assert.True(initiallyShown);
        Assert.True(archivedShown);
        Assert.Equal([false, true], inputs);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Initially_hidden_archived_list_needs_no_setup_or_restoration_input()
    {
        var inputs = 0;
        var initiallyShown = TreeItemRealization.PrepareArchivedListForMenu(() => false,
            () => inputs++, () => true, AwaitProvider);
        TreeItemRealization.RestoreArchivedListAfterMenu(initiallyShown, () => false, () => inputs++, AwaitProvider);

        Assert.False(initiallyShown);
        Assert.Equal(0, inputs);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_archived_preparation_does_not_retry_a_hide_or_repair_the_view(bool hideObserved)
    {
        var shown = true;
        var inputs = 0;
        Assert.Throws<TimeoutException>(() => TreeItemRealization.PrepareArchivedListForMenu(() => shown,
            () => { inputs++; if (hideObserved) shown = false; }, () => false, AwaitProvider));

        Assert.Equal(1, inputs);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Unexpected_archived_visibility_during_a_menu_is_rejected_without_a_repair_toggle()
    {
        var inputs = 0;
        Assert.Throws<InvalidOperationException>(() =>
            TreeItemRealization.RestoreArchivedListAfterMenu(true, () => true, () => inputs++, AwaitProvider));

        Assert.Equal(0, inputs);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Menu_target_visibility_does_not_constrain_later_ordinary_fixture_restoration()
    {
        var contentChecks = 0;
        var targetChecks = 0;
        var targetTop = 479d;
        void CheckTarget()
        {
            targetChecks++;
            // Run45698927097 kept the command target at479, then ordinary cleanup moved it to461.
            if (targetTop < 478 || targetTop + 20 > 570)
                throw new InvalidOperationException("Collapse subtree lost its target row.");
        }
        TreeItemRealization.AssertMenuRetention(TreeItemRealization.MenuRetentionBoundary.Command,
            () => contentChecks++, CheckTarget);
        targetTop = 461;

        TreeItemRealization.AssertMenuRetention(TreeItemRealization.MenuRetentionBoundary.OrdinaryFixtureRestoration,
            () => contentChecks++, CheckTarget);

        Assert.Equal(2, contentChecks);
        Assert.Equal(1, targetChecks);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void A_command_target_outside_the_viewport_still_fails_retention()
    {
        var targetTop = 461d;
        Assert.Throws<InvalidOperationException>(() =>
            TreeItemRealization.AssertMenuRetention(TreeItemRealization.MenuRetentionBoundary.Command, () => { }, () =>
            {
                if (targetTop < 478 || targetTop + 20 > 570)
                    throw new InvalidOperationException("Collapse subtree lost its target row.");
            }));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Ordinary_fixture_restoration_still_rejects_lost_writing_or_panel_state()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            TreeItemRealization.AssertMenuRetention(TreeItemRealization.MenuRetentionBoundary.OrdinaryFixtureRestoration,
                () => throw new InvalidOperationException("Retained writing or People panel changed."), () => { }));

        Assert.Equal("Retained writing or People panel changed.", error.Message);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData("morgan@example.com")]
    [InlineData("casey@example.com")]
    public void Menu_focus_preparation_avoids_the_observed_expansion_provider_navigation(string manager)
    {
        var owner = "avery@example.com";
        var overlayOpen = true;
        var expanded = false;
        var requests = new List<TreeItemRealization.MenuBranchInput>();
        TreeItemRealization.PrepareMenuBranch(() => overlayOpen && expanded, input =>
        {
            requests.Add(input);
            expanded = true;
            // Ordinary provider outcomes captured at run 45693688714's request boundary.
            if (input == TreeItemRealization.MenuBranchInput.ExpandCollapseProvider)
            {
                owner = manager;
                overlayOpen = false;
            }
        }, AwaitProvider);

        Assert.Equal([TreeItemRealization.MenuBranchInput.DisclosureToggle], requests);
        Assert.Equal("avery@example.com", owner);
        Assert.True(overlayOpen);
        Assert.True(expanded);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Already_expanded_menu_preparation_does_not_toggle_the_disclosure_closed()
    {
        var expanded = true;
        var requests = 0;
        TreeItemRealization.PrepareMenuBranch(() => expanded,
            _ => { requests++; expanded = !expanded; }, AwaitProvider);

        Assert.Equal(0, requests);
        Assert.True(expanded);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Unreached_menu_preparation_fails_without_repeating_disclosure_input()
    {
        var requests = 0;
        Assert.Throws<TimeoutException>(() =>
            TreeItemRealization.PrepareMenuBranch(() => false, _ => requests++, AwaitProvider));

        Assert.Equal(1, requests);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Reporting_header_request_uses_the_visible_header_when_native_browsing_is_horizontally_scrolled()
    {
        // UI Automation bounds from run 45688751967, reading attempt 1 at 96 DPI.
        var point = TreeItemRealization.ReportingHeaderPoint((71, 416, 588, 56), (112, 359, 340, 185), 1);

        Assert.Equal((282d, 430d), point);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Reporting_header_request_respects_DPI_and_vertical_clipping_without_targeting_a_descendant()
    {
        var point = TreeItemRealization.ReportingHeaderPoint((100, 100, 500, 400), (150, 130, 320, 100), 1.5);

        Assert.Equal((310d, 136d), point);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void An_offscreen_header_cannot_be_substituted_with_visible_descendants()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TreeItemRealization.ReportingHeaderPoint((100, 300, 500, 400), (112, 359, 340, 185), 1));
    }

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

    [Fact]
    [Trait("Category", "Runner")]
    public void Browse_baseline_accepts_an_interior_position_after_virtualized_extent_remeasurement()
    {
        var actions = new List<double>();
        var destination = 0d;
        TreeItemRealization.PrepareMeasuredScrollPosition(70, percent =>
        {
            actions.Add(percent);
            destination = percent;
        }, () => destination == 100 ? 100 : 39.9053, AwaitProvider);

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
