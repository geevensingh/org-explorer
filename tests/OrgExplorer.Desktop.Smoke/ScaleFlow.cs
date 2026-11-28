using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunScaleNavigationFlow(string executable, string root)
    {
        using var fixture = new ScaleFixture(root);
        Console.WriteLine("Isolated 10,000-Person fixture: " + fixture.Root);
        fixture.Build();
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            SetSettingsVisible(window, true);
            ScaleFixture.Measure("UI-load", () =>
            {
                PickWorkspaceFolder(window, fixture.Workspace);
                // Settings must work while actual filesystem work is outstanding, not after a blocked dispatcher returns.
                Await(() => Activity(window).Contains("Opening"));
                SetSettingsVisible(window, true);
                Await(() => !Find(window, "SettingsTitle").Current.IsOffscreen &&
                    !Find(window, "WorkspacePath").Current.IsOffscreen);
                if (!Activity(window).Contains("Opening"))
                    throw new InvalidOperationException("Settings was not responsive while the real scale open was pending.");
                AwaitScale(() => Activity(window) == "Ready");
                SetSettingsVisible(window, false);
            });
            ScaleFixture.Measure("UI-tree", () =>
            {
                var treeRoot = Find(window, "People").FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, ScaleFixture.Email(0)))
                    ?? throw new InvalidOperationException("The actual 10,000-Person tree root is missing.");
                var expand = (ExpandCollapsePattern)treeRoot.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                expand.Expand();
                foreach (var email in new[] { ScaleFixture.Email(1), ScaleFixture.Email(11) })
                {
                    AutomationElement? branch = null;
                    AwaitScale(() => (branch = Find(window, "People").FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, email))) is not null);
                    var branchExpand = (ExpandCollapsePattern)branch!.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                    branchExpand.Expand();
                    AwaitScale(() => branchExpand.Current.ExpandCollapseState == ExpandCollapseState.Expanded);
                }
                var tree = Find(window, "People");
                var scroll = (ScrollPattern)tree.GetCurrentPattern(ScrollPattern.Pattern);
                if (!scroll.Current.VerticallyScrollable)
                    throw new InvalidOperationException("Expanded scale tree did not expose actual scrolling.");
                var targetEmail = ScaleFixture.Email(9811);
                var items = (ItemContainerPattern)Find(tree, ScaleFixture.Email(11))
                    .GetCurrentPattern(ItemContainerPattern.Pattern);
                var initialOffset = scroll.Current.VerticalScrollPercent;
                var observation = "Target has not been located by the reporting branch's item provider.";
                AutomationElement lastReport;
                try
                {
                    lastReport = TreeItemRealization.AwaitVisible(
                        () =>
                        {
                            var item = items.FindItemByProperty(null, AutomationElement.AutomationIdProperty, targetEmail);
                            observation = item is null ? "Item provider has not returned the target." : "Target found; awaiting realization.";
                            return item;
                        },
                        item =>
                        {
                            if (item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized))
                                ((VirtualizedItemPattern)virtualized).Realize();
                            ((ScrollItemPattern)item.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
                        },
                        item =>
                        {
                            var identity = item.Current.AutomationId;
                            var offscreen = item.Current.IsOffscreen;
                            var bounds = item.Current.BoundingRectangle;
                            var offset = scroll.Current.VerticalScrollPercent;
                            observation = $"id={identity}; offscreen={offscreen}; bounds={bounds}; tree offset={offset:F2}; " +
                                $"selected={Find(window, "Email").Current.Name}";
                            if (identity != targetEmail)
                                throw new InvalidOperationException("The realized tree row has the wrong identity: " + observation);
                            return !offscreen && !bounds.IsEmpty && offset > initialOffset;
                        }, AwaitScale);
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"UI-tree realization of {targetEmail} did not complete. Last observation: {observation}", error);
                }
                ((SelectionItemPattern)lastReport.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                AwaitScale(() => Find(window, "Email").Current.Name == targetEmail);
                scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
                expand.Collapse();
                AwaitScale(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
                expand.Expand();
                AwaitScale(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded &&
                    Find(window, "People").FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, ScaleFixture.Email(1))) is not null);
                expand.Collapse();
                AwaitScale(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
                expand.Expand();
                AssertScaleSubtreeExpansion(window, fixture);
            });
            ScaleFixture.Measure("UI-current-search", () =>
            {
                Set(window, "SearchQuery", "handle01000");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("People found: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 01000");
                AwaitScale(() => Value(window, "SearchQuery") == "");
            });
            ScaleFixture.Measure("UI-photo-render", () => AssertScalePhotoVisible(window));
            ScaleFixture.Measure("UI-history-search", () =>
            {
                Set(window, "SearchQuery", "Cedar former context 100.");
                AutomationElement? result = null;
                AwaitScale(() =>
                {
                    result = Find(window, "SearchResults").FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                        .Cast<AutomationElement>().FirstOrDefault(r => r.Current.Name.Contains(ScaleFixture.Target) &&
                            r.Current.Name.Contains("Frozen Team note"));
                    return result is not null;
                });
                ActivateSearchRow(result!, AwaitScale);
                AwaitScale(() => OrdinarySearchProfileReady(window, ScaleFixture.Target));
                ExpandHistory(window);
                AssertSearchReading(window, "Cedar former context 100.", readingId: "HistoricalTeamReading");
            });
            Set(window, "SearchQuery", "");
            ScaleFixture.Measure("UI-manager-navigation", () =>
            {
                NavigateToManager(window, ScaleFixture.Email(11));
                AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 00011");
            });
            ScaleFixture.Measure("UI-deep-profile", () =>
            {
                Set(window, "SearchQuery", "handle00150");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("People found: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 00150");
                ExpandCurrentTeamContext(window);
                if (!Find(window, "InheritedTeamSummary").Current.Name.Contains("person00000@example.com"))
                    throw new InvalidOperationException("The deep Reporting chain lost its root Team context.");
                AwaitScale(() => CurrentReadingText(window).Contains("Cedar former context 0."));
            });
            ScaleFixture.Measure("UI-final-person", () =>
            {
                Set(window, "SearchQuery", "handle09999");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("People found: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "Email").Current.Name == ScaleFixture.Email(9999));
                if (!Find(window, "PersonArchivalStatus").Current.Name.Contains("Archived"))
                    throw new InvalidOperationException("The full population's final archived Person was not retained.");
            });
            ScaleFixture.Measure("UI-close", () => Close(process));
            process = null;
            Console.WriteLine("PASS: genuine 10,000-Person WPF realization/scrolling, navigation/search, photo pixels and current/frozen Markdown.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static void AssertScaleSubtreeExpansion(AutomationElement window, ScaleFixture fixture)
    {
        using var expected = ApplicationWorkflow.CreateFileBacked();
        if (!expected.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly))
            throw new InvalidOperationException(expected.Status);
        ScaleFixture.RequireRichPopulation(expected);
        var managerEmails = Enumerable.Range(0, 150).Select(ScaleFixture.Email).ToArray();
        if (managerEmails.Any(email => expected.GetRelationships(email) is not { ImportedReports.Count: > 0 }))
            throw new InvalidOperationException("The independent complete rich tree fixture lost one of its 150 current reporting managers.");
        var managerObservationOrder = TreeItemRealization.ReportingManagerPreorder(expected.ReportingRoots)
            .Select(node => node.Person.Email).ToArray();
        if (managerObservationOrder.Length != managerEmails.Length ||
            !managerObservationOrder.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(managerEmails))
            throw new InvalidOperationException("The rich manager observation order omitted or duplicated a required reporting branch.");
        var tree = Find(window, "People");
        var root = Find(tree, ScaleFixture.Email(0));
        var rootExpansion = (ExpandCollapsePattern)root.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        var query = Find(window, "SearchQuery");
        AutomationElement[] CaptureExpansion()
        {
            using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.UiaDiscovery);
            var cache = new CacheRequest { TreeScope = TreeScope.Element };
            foreach (var property in new[] { AutomationElement.AutomationIdProperty, AutomationElement.NameProperty,
                AutomationElement.ControlTypeProperty,
                AutomationElement.ItemStatusProperty, AutomationElement.IsOffscreenProperty, AutomationElement.IsEnabledProperty,
                AutomationElement.BoundingRectangleProperty, AutomationElement.HasKeyboardFocusProperty,
                ValuePattern.ValueProperty, ExpandCollapsePattern.ExpandCollapseStateProperty })
                cache.Add(property);
            AutomationElementCollection controls;
            using (cache.Activate())
                controls = window.FindAll(TreeScope.Descendants, new OrCondition(
                    new[] { "Email", "People", "PeopleNavigation", "SearchQuery", "SearchResults", "SearchCount",
                        "TreeExpansionStatus", "CancelTreeExpansion", ScaleFixture.Email(0) }
                        .Select(id => new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray()));
            return controls.Cast<AutomationElement>().ToArray();
        }
        AutomationElement Captured(AutomationElement[] controls, string id) =>
            id == ScaleFixture.Email(0)
                ? TreeItemRealization.ReportingTarget(controls, id, control => control.Cached.AutomationId,
                    control => control.Cached.ControlType == ControlType.TreeItem)
                : controls.FirstOrDefault(control => control.Cached.AutomationId == id) ??
                    throw new InvalidOperationException("Missing actual UI control: " + id);
        bool Visible(AutomationElement[] controls, string id) =>
            controls.FirstOrDefault(control => control.Cached.AutomationId == id) is { } control && !control.Cached.IsOffscreen;
        string ExpansionText(AutomationElement[] controls) =>
            controls.FirstOrDefault(control => control.Cached.AutomationId == "TreeExpansionStatus")?.Cached.Name ??
                Captured(controls, "People").Cached.ItemStatus;
        var owner = Captured(CaptureExpansion(), "Email").Cached.Name;
        void AssertRetained(AutomationElement[]? controls = null, string boundary = "retention")
        {
            controls ??= CaptureExpansion();
            var row = Captured(controls, ScaleFixture.Email(0));
            var bounds = row.Cached.BoundingRectangle;
            var viewport = Captured(controls, "People").Cached.BoundingRectangle;
            if (Captured(controls, "Email").Cached.Name != owner || row.Cached.IsOffscreen ||
                bounds.Top < viewport.Top || bounds.Top + 20 > viewport.Bottom)
                throw new InvalidOperationException("Scale subtree expansion changed the open profile or lost its command target: " +
                    $"boundary={boundary}; expectedOwner={owner}; actualOwner={Captured(controls, "Email").Cached.Name}; " +
                    $"targetType={row.Cached.ControlType}; targetOffscreen={row.Cached.IsOffscreen}; " +
                    $"target={bounds}; viewport={viewport}.");
        }
        (int People, int Branches) Progress(AutomationElement[] controls)
        {
            var text = ExpansionText(controls);
            var match = System.Text.RegularExpressions.Regex.Match(text,
                @"(\d+) imported People checked; (\d+) branches expanded\.");
            if (!match.Success) throw new InvalidOperationException("The actual scale tree has no useful expansion progress: " + text);
            return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }
        void Action(string label)
        {
            RightClickReportingRow(window, root, "full-population-" + label);
            AutomationElement? menu = null;
            AwaitScale(() => (menu = ReportingMenu(window)) is not null);
            var actions = menu!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
                .Cast<AutomationElement>().Where(item => !item.Current.IsOffscreen).ToArray();
            var action = actions.SingleOrDefault(item => item.Current.Name == label && item.Current.IsEnabled) ??
                throw new InvalidOperationException("The actual rich tree omitted " + label);
            AssertRetained(boundary: "menu-open-" + label);
            PrimaryClick(() => action);
            AwaitScale(() => ReportingMenu(window) is null);
        }

        // Reset via the real recursive action, not ordinary disclosure or repaired fixture state.
        Action("Collapse subtree");
        AwaitScale(() => rootExpansion.Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
        AssertRetained();
        Action("Expand subtree");
        AutomationElement[] frame = [];
        AwaitScale(() =>
        {
            frame = CaptureExpansion();
            return ExpansionText(frame).StartsWith("Expanding subtree:", StringComparison.Ordinal) &&
                Progress(frame) is { People: > 0, Branches: > 0 };
        });
        var cancel = Captured(frame, "CancelTreeExpansion");
        if (!cancel.Cached.IsEnabled || cancel.Cached.IsOffscreen)
            throw new InvalidOperationException("Full-population expansion has no actionable native cancellation control.");
        AssertRetained(frame);
        query.SetFocus();
        AwaitScale(() => query.Current.HasKeyboardFocus);
        if (!tree.Current.ItemStatus.StartsWith("Expanding subtree:", StringComparison.Ordinal))
            throw new InvalidOperationException("Interaction was observed only after scale expansion finished, not while work was unfinished.");
        AssertRetained();
        ((ValuePattern)query.GetCurrentPattern(ValuePattern.Pattern)).SetValue("handle01000");
        AwaitScale(() =>
        {
            frame = CaptureExpansion();
            return Captured(frame, "SearchQuery").GetCachedPropertyValue(ValuePattern.ValueProperty).Equals("handle01000") &&
                Visible(frame, "SearchResults") && !Visible(frame, "People") &&
                Captured(frame, "SearchCount").Cached.Name.Contains("People found: 1 ", StringComparison.Ordinal);
        });
        cancel = Captured(frame, "CancelTreeExpansion");
        if (!ExpansionText(frame).StartsWith("Expanding subtree:", StringComparison.Ordinal) ||
            Progress(frame) is not { People: > 0 and < 9900, Branches: > 0 } ||
            !Visible(frame, "PeopleNavigation") || !Visible(frame, "TreeExpansionStatus") ||
            cancel.Cached.IsOffscreen || !cancel.Cached.IsEnabled ||
            !Captured(frame, "SearchQuery").Cached.HasKeyboardFocus || Captured(frame, "Email").Cached.Name != owner)
            throw new InvalidOperationException("Actual nonempty search must expose useful progress and actionable cancellation while full-rich expansion is genuinely unfinished.");
        ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        AwaitScale(() =>
        {
            frame = CaptureExpansion();
            return ExpansionText(frame).StartsWith("Expand subtree cancelled;", StringComparison.Ordinal);
        });
        var partial = Progress(frame);
        if (partial.People is <= 0 or >= 9900 || partial.Branches <= 0 || !Visible(frame, "SearchResults") ||
            !Visible(frame, "TreeExpansionStatus") || Captured(frame, "Email").Cached.Name != owner)
            throw new InvalidOperationException("Native scale cancellation did not preserve genuine unfinished expansion.");
        var stable = Stopwatch.StartNew();
        AwaitScale(() => stable.ElapsedMilliseconds >= 200);
        if (Progress(CaptureExpansion()) != partial) throw new InvalidOperationException("Expansion continued after native cancellation.");
        Click(window, "ClearSearch");
        AwaitScale(() =>
        {
            frame = CaptureExpansion();
            return Visible(frame, "People") &&
                Captured(frame, "SearchQuery").GetCachedPropertyValue(ValuePattern.ValueProperty).Equals("");
        });
        if (Progress(frame) != partial || !Captured(frame, ScaleFixture.Email(0))
            .GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty).Equals(ExpandCollapseState.Expanded))
            throw new InvalidOperationException("Search-clear lost cancelled partial expansion.");
        AssertRetained(frame);
        Action("Expand subtree");
        AwaitScale(() => tree.Current.ItemStatus.StartsWith("Expanded all available reporting levels:", StringComparison.Ordinal));
        frame = CaptureExpansion();
        var completed = Progress(frame);
        if (completed.People != 9900 || completed.Branches + partial.Branches != 150)
            throw new InvalidOperationException("The actual WPF expansion truncated the 9,900 current People or 150 reporting managers.");
        AssertRetained(frame);
        RightClickReportingRow(window, root, "full-population-completed");
        AutomationElement? completedMenu = null;
        AwaitScale(() => (completedMenu = ReportingMenu(window)) is not null);
        if (completedMenu!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
            .Cast<AutomationElement>().Any(item => !item.Current.IsOffscreen && item.Current.Name == "Expand subtree"))
            throw new InvalidOperationException("The completed rich subtree still offers a no-op expansion.");
        PressCopyKey(0x1B);
        AwaitScale(() => ReportingMenu(window) is null);
        AssertRetained();

        // Deliberate observation of distant rows follows the action/target-retention boundary.
        var rowTrace = new TreeItemRealization.ReportingRowTrace<ScaleReportingRowObservation>();
        var ownerProbe = Captured(frame, "Email");
        void ObserveRow(string phase, string requestedEmail, string? expectedParent, AutomationElement? row,
            bool realizeRequested, ExpandCollapseState? lastAwaitedState, bool liveLookup = false)
        {
            try
            {
                using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.UiaDiscovery);
                if (liveLookup)
                    row = Find(window, "People").FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, requestedEmail),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)));
                var cache = new CacheRequest { TreeScope = TreeScope.Element };
                foreach (var property in new[] { AutomationElement.AutomationIdProperty,
                    AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                    AutomationElement.IsOffscreenProperty, AutomationElement.ItemStatusProperty,
                    AutomationElement.HelpTextProperty,
                    AutomationElement.IsVirtualizedItemPatternAvailableProperty,
                    ExpandCollapsePattern.ExpandCollapseStateProperty })
                    cache.Add(property);
                var observedRow = row?.GetUpdatedCache(cache);
                var observedTree = tree.GetUpdatedCache(cache);
                var expansion = observedRow?.GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty, true);
                var virtualized = observedRow?.GetCachedPropertyValue(AutomationElement.IsVirtualizedItemPatternAvailableProperty, true);
                var type = observedRow?.GetCachedPropertyValue(AutomationElement.ControlTypeProperty, true);
                var rowBounds = observedRow?.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty, true);
                var viewportBounds = observedTree.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty, true);
                rowTrace.Retain(new(Stopwatch.GetTimestamp(), phase, requestedEmail, expectedParent, realizeRequested,
                    observedRow?.GetCachedPropertyValue(AutomationElement.AutomationIdProperty, true) as string,
                    type is ControlType controlType ? controlType.ProgrammaticName : null,
                    expansion is ExpandCollapseState state ? state.ToString() : null, lastAwaitedState?.ToString(),
                    virtualized is bool available ? available : null,
                    observedRow?.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty, true) is bool offscreen ? offscreen : null,
                    rowBounds is System.Windows.Rect rowRect ? rowRect.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    viewportBounds is System.Windows.Rect viewportRect ? viewportRect.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    observedTree.GetCachedPropertyValue(AutomationElement.ItemStatusProperty, true) as string, ownerProbe.Current.Name,
                    row is null ? "The lookup did not return a row." : null,
                    observedRow?.GetCachedPropertyValue(AutomationElement.HelpTextProperty, true) as string,
                    observedTree.GetCachedPropertyValue(AutomationElement.HelpTextProperty, true) as string));
            }
            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or
                ArgumentException or System.Runtime.InteropServices.COMException)
            {
                rowTrace.RecordFailure(error);
                rowTrace.Retain(new(Stopwatch.GetTimestamp(), phase, requestedEmail, expectedParent, realizeRequested,
                    null, null, null, lastAwaitedState?.ToString(), null, null, null, null, null, null,
                    error.GetType().FullName + ": " + error.Message));
            }
        }
        var rows = new Dictionary<string, AutomationElement>(StringComparer.OrdinalIgnoreCase) { [ScaleFixture.Email(0)] = root };
        foreach (var email in managerObservationOrder)
        {
            AutomationElement? row = null;
            string? expectedParent = null;
            var realizeRequested = false;
            var afterRecorded = false;
            ExpandCollapseState? lastAwaitedState = null;
            try
            {
                if (email == ScaleFixture.Email(0)) row = root;
                else
                {
                    var manager = expected.GetRelationships(email)!.Manager!.Email;
                    expectedParent = manager;
                    var items = (ItemContainerPattern)rows[manager].GetCurrentPattern(ItemContainerPattern.Pattern);
                    row = items.FindItemByProperty(null, AutomationElement.AutomationIdProperty, email) ??
                        throw new InvalidOperationException("Full expansion omitted a rich reporting row: " + email);
                }
                ObserveRow("before-request", email, expectedParent, row, realizeRequested, lastAwaitedState);
                if (email != ScaleFixture.Email(0) && row.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized))
                {
                    realizeRequested = true;
                    ((VirtualizedItemPattern)virtualized).Realize();
                }
                if (email != ScaleFixture.Email(0)) rows[email] = row;
                AwaitScale(() =>
                {
                    if (!afterRecorded)
                    {
                        // This boundary capture is inside the original 180-second observation budget.
                        ObserveRow("after-request", email, expectedParent, row, realizeRequested, lastAwaitedState);
                        ObserveRow("after-request-live", email, expectedParent, row, realizeRequested, lastAwaitedState, liveLookup: true);
                        afterRecorded = true;
                    }
                    lastAwaitedState = ((ExpandCollapsePattern)row.GetCurrentPattern(ExpandCollapsePattern.Pattern))
                        .Current.ExpandCollapseState;
                    return lastAwaitedState == ExpandCollapseState.Expanded;
                });
                ObserveRow("final-expanded", email, expectedParent, row, realizeRequested, lastAwaitedState);
            }
            catch (Exception primary) when (primary is TimeoutException or ElementNotAvailableException or
                InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                if (!afterRecorded)
                    ObserveRow("after-request-failed", email, expectedParent, row, realizeRequested, lastAwaitedState);
                ObserveRow("final-failed", email, expectedParent, row, realizeRequested, lastAwaitedState);
                ObserveRow("final-failed-live", email, expectedParent, row, realizeRequested, lastAwaitedState, liveLookup: true);
                rowTrace.AttemptOutput(primary, () => Console.WriteLine("TREE-SCALE-REALIZATION " +
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                        runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                        runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                        requestedEmail = email, expectedParent, realizeRequested,
                        originalFailure = primary.GetType().FullName, originalMessage = primary.Message,
                        timestampFrequency = Stopwatch.Frequency, observations = rowTrace.Recent,
                        observationFailures = rowTrace.Failures,
                        interpretation = "Bounded non-atomic UIA observations; null fields are unsupported or unreached, not default states. No repair, input replay or time deduction."
                    }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })));
                rowTrace.AttemptOutput(primary, () => CaptureFailureEvidence(window, "tree-scale-realization-failure"));
                if (rowTrace.Failures.Count > 0)
                    rowTrace.AttemptOutput(primary, () => Console.Error.WriteLine("TREE-SCALE-REALIZATION-DIAGNOSTIC-FAULT " +
                        System.Text.Json.JsonSerializer.Serialize(rowTrace.Failures)));
                throw;
            }
        }
        if (rowTrace.Failures.Count > 0)
            rowTrace.AttemptOutput(null, () => Console.Error.WriteLine("TREE-SCALE-REALIZATION-DIAGNOSTIC-FAULT " +
                System.Text.Json.JsonSerializer.Serialize(rowTrace.Failures)));
        Console.WriteLine("PASS: native full-rich expansion visited all 9,900 current People, restored all 150 manager rows, exposed progress/cancellation during actual nonempty search and retained partial expansion through cancellation/search-clear before resuming.");
    }

    private sealed record ScaleReportingRowObservation(long ObservedAt, string Phase, string RequestedEmail,
        string? ExpectedParent, bool RealizeRequested, string? ReturnedEmail, string? ControlType,
        string? ExpansionState, string? LastAwaitedState, bool? VirtualizedPatternAvailable, bool? IsOffscreen,
        string? RowBounds, string? ViewportBounds, string? Operation, string? CurrentOwner, string? ObservationRefusal,
        string? RowDiagnostic = null, string? TreeDiagnostic = null);

    private static void RunScaleMutationPrefix(string executable, string root)
    {
        using var fixture = new ScaleFixture(root);
        Console.WriteLine("Isolated 10,000-Person fixture: " + fixture.Root);
        fixture.Build();
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            SetSettingsVisible(window, true);
            ScaleFixture.Measure("UI-mutation-open", () =>
            {
                PickWorkspaceFolder(window, fixture.Workspace);
                AwaitRestoredWorkspace(window, fixture.Workspace, "person00000@example.com", AwaitScale);
            });
            Set(window, "SearchQuery", "handle01000");
            AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("People found: 1 "));
            SelectScaleResult(window);
            AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 01000");
            AwaitScale(() => Value(window, "SearchQuery") == "");
            ScaleFixture.Measure("UI-ordinary-save-latency", () =>
                RunLocalNoteAutosaveJourney(window, fixture.Workspace, fixture, executable, "normal-executable"));
            ScaleFixture.Measure("UI-publish-before-held-export", () =>
                PrepareSmokeNormalClose(fixture.Workspace, () =>
                    PublishSmokeWorkspaceForClose(fixture.Workspace, fixture.SynchronizationFolder, () =>
                        PublishSmokeWorkspace(window, fixture.Workspace, fixture.SynchronizationFolder))));
            ScaleFixture.Measure("UI-close-before-held-export", () => Close(process));
            process = null;
            ScaleFixture.Measure("UI-background-export-independence", () => RunRichBackgroundExport(executable, fixture));
            ScaleFixture.Measure("UI-handoff-capture", () =>
                UiScaleHandoff.Capture(RequireScaleHandoffDirectory(), fixture.Workspace, fixture.SynchronizationFolder,
                    Path.Combine(Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY")!,
                        "workspace-settings.json"), ReadScaleBinding(executable),
                    Environment.GetEnvironmentVariable("UI_SCALE_WORKER_RUN_DIRECTORY") ??
                        throw new InvalidOperationException("Missing first-worker invocation identity.")));
            Console.WriteLine("PASS: combined normal-executable and same held-export rich saves, latest publication and normal host death; logical handoff captured.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static void RunScaleMutationTail(string executable, string root)
    {
        using var fixture = new ScaleFixture(root);
        Process? process = null;
        try
        {
            ScaleFixture.Measure("UI-handoff-join", () =>
                UiScaleHandoff.Receive(RequireScaleHandoffDirectory(), fixture.Workspace, fixture.SynchronizationFolder,
                    Path.Combine(Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY")!,
                        "workspace-settings.json"), ReadScaleBinding(executable),
                    Path.Combine(Path.GetDirectoryName(diagnostics)!, "handoff-received.json")));
            using (var joined = ApplicationWorkflow.CreateFileBacked())
            {
                if (!joined.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly))
                    throw new InvalidOperationException(joined.Status);
                ScaleFixture.RequireRichPopulation(joined, additionalPersonNotes: 2, additionalTeamNotes: 40);
            }
            var remembered = ReadSmokeWorkspacePreferences();
            process = Launch(executable);
            var window = Window(process);
            AwaitRememberedWorkspace(window, fixture.Workspace, remembered, AwaitScale);
            SelectRichNoteReport(window);
            AssertProfileValue(window, "OfficeNumber", "0009U");
            ScaleFixture.Measure("UI-ordinary-profile-latency", () => RunRichProfileLatency(window, fixture, executable));
            ScaleFixture.Measure("UI-favorite-mutation", () =>
            {
                Click(window, "ToggleFavorite");
                AwaitScale(() => Find(window, "ToggleFavorite").Current.Name == "Mark favorite");
            });
            var import = Path.Combine(fixture.Root, "refresh.json");
            ScaleFixture.Measure("UI-import-file", () => File.WriteAllText(import, ScaleFixture.Batch(Enumerable.Range(0, ScaleFixture.Population).Select(i =>
            {
                var update = new Dictionary<string, object?> { ["email"] = ScaleFixture.Email(i) };
                if (i == 1000) update["managerEmail"] = ScaleFixture.Email(12);
                return update;
            }), 4)));
            ScaleFixture.Measure("UI-import-navigation", () =>
            {
                OpenImportView(window);
                Set(window, "ImportPath", import);
            });
            ScaleFixture.Measure("UI-validated-refresh", () =>
            {
                ScaleFixture.Measure("UI-import-preview", () =>
                {
                    Click(window, "PreviewImport");
                    AwaitScale(() => Find(window, "ApplyImport").Current.IsEnabled);
                    if (!Find(window, "Preview").Current.Name.Contains("person09999@example.com"))
                        throw new InvalidOperationException("The 10,000-record review lost its final Person.");
                });
                ScaleFixture.Measure("UI-import-apply", () =>
                {
                    Click(window, "ApplyImport");
                    AwaitScale(() => Status(window).Contains("Import applied"));
                });
                ScaleFixture.Measure("UI-import-return", () =>
                {
                    ReturnToPeople(window);
                    AwaitScale(() => Find(window, "NavigateManager").Current.Name.Contains("Reference 00012"));
                });
            });
            ScaleFixture.Measure("UI-external-delivery", () =>
            {
                SetSettingsVisible(window, true);
                Click(window, "SyncNow");
                AwaitScale(() => Find(window, "SyncNow").Current.IsEnabled &&
                    Find(window, "SynchronizationStatus").Current.Name.Contains("Locally saved and published."));
                var incoming = Path.Combine(fixture.Root, "incoming-snapshots");
                Directory.CreateDirectory(incoming);
                foreach (var file in Directory.GetFiles(fixture.SynchronizationFolder, "*.oesnapshot"))
                    File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)));
                using (var other = ApplicationWorkflow.CreateFileBacked())
                {
                    if (!other.OpenWorkspace(Path.Combine(fixture.Root, "second-local")) ||
                        !other.ConfigureSynchronizationFolder(incoming, joinExistingWorkspace: true) || !other.SyncNow())
                        throw new InvalidOperationException(other.Status);
                    var note = other.GetPersonNotes(ScaleFixture.Target).Single();
                    if (!other.EditPersonNote(note.Id, note.Title, "# Delivered tulip 1000.") || !other.SyncNow())
                        throw new InvalidOperationException(other.Status);
                }
                foreach (var file in Directory.GetFiles(incoming, "*.oesnapshot"))
                    File.Copy(file, Path.Combine(fixture.SynchronizationFolder, Path.GetFileName(file)), true);
                Click(window, "SyncNow");
                AwaitScale(() => Find(window, "SyncNow").Current.IsEnabled &&
                    Find(window, "SynchronizationStatus").Current.Name.Contains("Locally saved and published."));
                SetSettingsVisible(window, false);
                Set(window, "SearchQuery", "Delivered tulip 1000.");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("People found: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "PersonNoteHeading").Current.Name == "Individual 1000");
                AssertSearchReading(window, "Delivered tulip 1000.");
            });
            var rememberedNote = Find(window, "PersonNoteHeading").Current.HelpText;
            ScaleFixture.Measure("UI-close-before-reopen", () => Close(process));
            process = Launch(executable);
            window = Window(process);
            ScaleFixture.Measure("UI-reopen", () =>
            {
                AwaitRestoredWorkspace(window, fixture.Workspace, ScaleFixture.Target, AwaitScale);
                AwaitScale(() => Find(window, "PersonNoteHeading").Current.HelpText == rememberedNote);
                AssertNoteView(window, "Person", editing: false);
                AwaitScale(() => ReadingText(window).Contains("Delivered tulip 1000."));
                Set(window, "SearchQuery", "handle01000");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("People found: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 01000");
                AssertProfileValue(window, "OfficeNumber", "0009U");
                AwaitScale(() => Value(window, "SearchQuery") == "");
                AssertScalePhotoVisible(window);
                if (!Find(window, "NavigateManager").Current.Name.Contains("Reference 00012"))
                    throw new InvalidOperationException("Durable scale reporting mutation was lost.");
                if (Find(window, "ToggleFavorite").Current.Name != "Mark favorite")
                    throw new InvalidOperationException("Durable scale favorite mutation was lost.");
            });
            ScaleFixture.Measure("UI-close", () => Close(process));
            process = null;
            Console.WriteLine("PASS: separate-worker logical Join, normal-executable rich profile saves, favorite/import/snapshot adoption and durable reopen.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static string RequireScaleHandoffDirectory() =>
        Environment.GetEnvironmentVariable("UI_SCALE_HANDOFF_DIRECTORY") is { Length: > 0 } path &&
        Path.IsPathFullyQualified(path) ? path :
        throw new InvalidOperationException("UI scale requires an explicit candidate-bound logical handoff directory.");

    private static UiScaleHandoff.Binding ReadScaleBinding(string executable)
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value :
            throw new InvalidOperationException("Missing UI-scale binding: " + name);
        return UiScaleHandoff.Identify(Required("UI_SCALE_REVISION"), Required("GITHUB_RUN_ID"),
            Required("GITHUB_RUN_ATTEMPT"), Required("UI_SCALE_SOURCE_TREE"), Required("UI_SCALE_TEST_TREE"),
            Required("UI_SCALE_ENG_TREE"), Required("UI_SCALE_WORKFLOW_BLOB"), executable,
            typeof(Program).Assembly.Location);
    }

    private static void SelectScaleResult(AutomationElement window)
    {
        var result = Find(window, "SearchResults").FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        if (result is null) throw new InvalidOperationException("Actual scale search result missing.");
        ActivateSearchRow(result, AwaitScale);
    }

    private static void AssertScalePhotoVisible(AutomationElement window)
    {
        window.SetFocus();
        ((ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern))
            .SetScrollPercent(ScrollPattern.NoScroll, 0);
        AwaitScale(() =>
        {
            var photo = Find(window, "PersonPhoto");
            var bounds = photo.Current.BoundingRectangle;
            if (photo.Current.IsOffscreen || bounds.IsEmpty) return false;
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var rgb = GetPixel(dc, (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                if (rgb == uint.MaxValue)
                    throw new InvalidOperationException("Actual UI pixels are unavailable. Desktop smoke requires an interactive, unlocked Windows desktop; image assertions cannot be skipped.");
                // The independently known middle pixel of target's 128x128 gradient (seed 8).
                return (rgb & 255) is >= 155 and <= 180 && ((rgb >> 8) & 255) is >= 110 and <= 135 &&
                    ((rgb >> 16) & 255) is >= 115 and <= 140;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        });
    }

    private static void AwaitScale(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(180))
        {
            try { if (condition()) return; }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Missing actual UI control:", StringComparison.Ordinal)) { }
            Thread.Sleep(50);
        }
        throw new TimeoutException("Actual scale UI did not reach the expected state within the bounded 180-second test deadline.");
    }
}
