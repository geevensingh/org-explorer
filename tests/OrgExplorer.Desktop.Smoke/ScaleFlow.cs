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

    private static void RunScaleMutationPrefix(string executable, string root)
    {
        var diagnosticBinding = ReadScaleBinding(executable);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(diagnostics)!, "diagnostic-scale-binding.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                diagnosticOnly = true, binding = diagnosticBinding, shippingRevision = PassiveSaveCapture.ShippingRevision,
                shippingTree = PassiveSaveCapture.ShippingTree, productRole = "rich-normal-entry-exe",
                workerInvocation = Environment.GetEnvironmentVariable("UI_SCALE_WORKER_RUN_DIRECTORY"),
                controlledHostRole = "rich-controlled-wpf-host", nativeIdentityRole = "rich-harness-only-not-target-normal-exe"
            }));
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
                AwaitRestoredWorkspace(window, fixture.Workspace, awaitState: AwaitScale);
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
