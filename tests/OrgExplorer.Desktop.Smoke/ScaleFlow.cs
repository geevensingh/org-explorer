using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;

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
            Set(window, "WorkspacePath", fixture.Workspace);
            ScaleFixture.Measure("UI-load", () =>
            {
                Click(window, "OpenWorkspace");
                // Settings must work while actual filesystem work is outstanding, not after a blocked dispatcher returns.
                Await(() => Find(window, "WorkflowActivity").Current.Name.Contains("Opening"));
                Click(window, "OpenSettings");
                Await(() => !Find(window, "SettingsGuidance").Current.IsOffscreen);
                if (!Find(window, "WorkflowActivity").Current.Name.Contains("Opening"))
                    throw new InvalidOperationException("Settings was not responsive while the real scale open was pending.");
                AwaitScale(() => Find(window, "WorkflowActivity").Current.Name == "Ready");
                Click(window, "OpenSettings");
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
            });
            ScaleFixture.Measure("UI-current-search", () =>
            {
                Set(window, "SearchQuery", "handle01000");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("Local matches: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 01000");
                Click(window, "CloseSearchContext");
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
                ((SelectionItemPattern)result!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                AwaitScale(() => Find(window, "SearchContext").Current.Name.Contains("Historical"));
                AssertSearchReading(window, "Cedar former context 100.");
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
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("Local matches: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 00150");
                if (!Find(window, "InheritedTeamSummary").Current.Name.Contains("person00000@example.com"))
                    throw new InvalidOperationException("The deep Reporting chain lost its root Team context.");
                AwaitScale(() => CurrentReadingText(window).Contains("Cedar former context 0."));
            });
            ScaleFixture.Measure("UI-final-person", () =>
            {
                Set(window, "SearchQuery", "handle09999");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("Local matches: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "Email").Current.Name == ScaleFixture.Email(9999));
                if (!Find(window, "PersonArchivalStatus").Current.Name.Contains("Archived"))
                    throw new InvalidOperationException("The full population's final archived Person was not retained.");
            });
            Close(process); process = null;
            Console.WriteLine("PASS: genuine 10,000-Person WPF realization/scrolling, navigation/search, photo pixels and current/frozen Markdown.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static void RunScaleMutationFlow(string executable, string root)
    {
        using var fixture = new ScaleFixture(root);
        Console.WriteLine("Isolated 10,000-Person fixture: " + fixture.Root);
        fixture.Build();
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            Set(window, "WorkspacePath", fixture.Workspace);
            Click(window, "OpenWorkspace");
            AwaitScale(() => Find(window, "WorkflowActivity").Current.Name == "Ready");
            Set(window, "SearchQuery", "handle01000");
            AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("Local matches: 1 "));
            SelectScaleResult(window);
            AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 01000");
            Click(window, "CloseSearchContext");
            ScaleFixture.Measure("UI-profile-mutation", () =>
            {
                BeginProfileEditing(window);
                Set(window, "OfficeNumber", "0009U");
                Click(window, "SaveProfile");
                AwaitScale(() => Find(window, "WorkflowActivity").Current.Name.Contains("Saving complete profile"));
                Click(window, "OpenSettings");
                Await(() => !Find(window, "SettingsGuidance").Current.IsOffscreen);
                if (!Find(window, "WorkflowActivity").Current.Name.Contains("Saving complete profile"))
                    throw new InvalidOperationException("Settings only rendered after the actual scale mutation completed.");
                Click(window, "OpenSettings");
                Click(window, "NavigateManager");
                Await(() => Find(window, "Status").Current.Name.StartsWith("Navigation blocked:"));
                if (!Find(window, "Status").Current.Name.Contains("in progress") ||
                    !Find(window, "Status").Current.Name.Contains("Please wait") ||
                    Find(window, "Email").Current.Name != ScaleFixture.Target)
                    throw new InvalidOperationException("Navigation during a save did not explain waiting or retain the current Person.");
                AwaitScale(() => Find(window, "Status").Current.Name == "Profile saved locally.");
            });
            ScaleFixture.Measure("UI-favorite-mutation", () =>
            {
                Click(window, "ToggleFavorite");
                AwaitScale(() => Find(window, "FavoriteStatus").Current.Name == "Not marked favorite");
            });
            var import = Path.Combine(fixture.Root, "refresh.json");
            File.WriteAllText(import, ScaleFixture.Batch(Enumerable.Range(0, ScaleFixture.Population).Select(i =>
            {
                var update = new Dictionary<string, object?> { ["email"] = ScaleFixture.Email(i) };
                if (i == 1000) update["managerEmail"] = ScaleFixture.Email(12);
                return update;
            }), 4));
            OpenImportView(window);
            Set(window, "ImportPath", import);
            ScaleFixture.Measure("UI-validated-refresh", () =>
            {
                Click(window, "PreviewImport");
                AwaitScale(() => Find(window, "ApplyImport").Current.IsEnabled);
                if (!Find(window, "Preview").Current.Name.Contains("person09999@example.com"))
                    throw new InvalidOperationException("The 10,000-record review lost its final Person.");
                Click(window, "ApplyImport");
                AwaitScale(() => Find(window, "Status").Current.Name.Contains("Import applied"));
                ReturnToPeople(window);
                AwaitScale(() => Find(window, "NavigateManager").Current.Name.Contains("Reference 00012"));
            });
            var notePath = Directory.GetFiles(Path.Combine(fixture.Workspace, "notes"))
                .First(p => File.ReadAllText(p).Contains("Orchid private writing 1000."));
            ScaleFixture.Measure("UI-external-delivery", () =>
            {
                File.WriteAllText(notePath, File.ReadAllText(notePath).Replace("Orchid private writing 1000.", "Delivered tulip 1000."));
                Set(window, "SearchQuery", "Delivered tulip 1000.");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("Local matches: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "SearchContext").Current.Name.Contains("Person note"));
                AssertSearchReading(window, "Delivered tulip 1000.");
            });
            ScaleFixture.Measure("UI-storage-maintenance", () => CompleteStorageMaintenance(window, AwaitScale));
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", fixture.Workspace);
            ScaleFixture.Measure("UI-reopen", () =>
            {
                Click(window, "OpenWorkspace");
                AwaitScale(() => Find(window, "Status").Current.Name.Contains("Workspace opened"));
                Set(window, "SearchQuery", "handle01000");
                AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("Local matches: 1 "));
                SelectScaleResult(window);
                AwaitScale(() => Find(window, "ProfileHeading").Current.Name == "Reference 01000");
                AssertProfileValue(window, "OfficeNumber", "0009U");
                Click(window, "CloseSearchContext");
                AssertScalePhotoVisible(window);
                if (!Find(window, "NavigateManager").Current.Name.Contains("Reference 00012"))
                    throw new InvalidOperationException("Durable scale reporting mutation was lost.");
                if (Find(window, "FavoriteStatus").Current.Name != "Not marked favorite")
                    throw new InvalidOperationException("Durable scale favorite mutation was lost.");
            });
            Close(process); process = null;
            Console.WriteLine("PASS: actual 10,000-Person WPF pending mutation responsiveness, rejection, favorite/import/refresh and durable reopen.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

    private static void SelectScaleResult(AutomationElement window)
    {
        var result = Find(window, "SearchResults").FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        if (result is null) throw new InvalidOperationException("Actual scale search result missing.");
        ((SelectionItemPattern)result.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
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
