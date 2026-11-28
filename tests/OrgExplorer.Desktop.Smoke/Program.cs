using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static readonly List<Process> Applications = [];
    private static string diagnostics = "";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--local-save-recovery-fixture")
                return RunLocalSaveRecoveryFixture(args);
            if (args.Length > 0 && args[0] is "--automatic-snapshot-fixture" or "--rich-scale-snapshot-fixture")
                return RunAutomaticSnapshotFixture(args);
            if (args.Length > 0 && args[0] == "--workspace-exit-fixture")
                return RunWorkspaceExitFixture(args);
            if (args.Length > 0 && args[0] == "--held-snapshot-fixture")
                return RunHeldSnapshotFixture(args);
            if (args.Length == 1 && args[0] is "--prepare-high-resolution" or "--verify-high-resolution" or "--verify-standard-display")
                return ConfigureDisplay(args[0]);
            var installer = args.Length == 4 && args[2] is "create" or "reopen" ||
                args.Length == 5 && args[2] == "update";
            if ((!installer && args.Length is < 1 or > 2) || !File.Exists(args[0]))
                throw new ArgumentException("Supply the normal built WPF executable path, optionally one named scenario.");
            var executable = Path.GetFullPath(args[0]);
            if (args.Length == 2 && args[1] == "--verify-ui-scale-handoff")
                return VerifyUiScaleLogicalHandoff(executable);
            if (args.Length == 2 && args[1] == "--verify-cutover-publication")
                return VerifyHostedCutoverContracts(executable, VerifyHostedPublicationContract);
            if (args.Length == 2 && args[1] == "--verify-cutover-note-modes")
                return VerifyHostedCutoverContracts(executable, VerifyHostedNoteModeContracts);
            if (args.Length == 2 && args[1] == "--verify-cutover-acknowledgment")
                return VerifyHostedCutoverContracts(executable, VerifyHostedAcknowledgmentContract);
            if (args.Length == 2 && args[1] == "--verify-cutover-failure-feedback")
                return VerifyHostedCutoverContracts(executable, VerifyHostedMutationFeedbackContracts);
            if (args.Length == 2 && args[1] == "--verify-cutover-incoming")
                return VerifyHostedCutoverContracts(executable, VerifyHostedIncomingContract);
            if (args.Length == 2 && args[1] == "--verify-cutover-close-publication")
                return VerifyHostedCutoverContracts(executable, VerifyHostedClosePublicationContracts);
            if (args.Length == 2 && args[1] == "--verify-cutover-recovery-copy")
                return VerifyHostedCutoverContracts(executable, VerifyRecoveredCopyContract);
            if (args.Length == 2 && args[1] == "--verify-local-recovery-cleanup")
                return VerifyHostedCutoverContracts(executable, VerifyLocalRecoveryCleanup);
            if (args.Length == 2 && args[1] == "--verify-independent-note-leave")
                return VerifyHostedCutoverContracts(executable, VerifyIndependentNoteLeaveContract);
            if (args.Length == 2 && args[1] == "--verify-import-recovery-admission")
                return VerifyHostedCutoverContracts(executable, VerifyImportRecoveryAdmission);
            if (args.Length == 2 && args[1] == "--diagnose-rich-navigation")
                return VerifyHostedCutoverContracts(executable, DiagnoseCompiledRichNavigation);
            if (args.Length == 2 && args[1] == "--verify-manager-choice-demand")
                return VerifyHostedCutoverContracts(executable, VerifyManagerChoiceDemand);
            if (args.Length == 2 && args[1] == "--verify-current-search-storage")
                return VerifyHostedCutoverContracts(executable, VerifyCurrentSearchStorage);
            if (args.Length == 2 && args[1] == "--verify-inherited-note-mode")
                return VerifyHostedCutoverContracts(executable, VerifyRetainedInheritedTeamNoteMode);
            if (args.Length == 2 && args[1] == "--verify-people-opening")
                return VerifyHostedCutoverContracts(executable, VerifyAutomaticPeopleReading);
            if (args.Length == 2 && args[1] == "--verify-actual39-functional")
                return VerifyHostedCutoverContracts(executable, VerifyActual39FunctionalContracts);
            if (args.Length == 2 && args[1] == "--verify-note-read-demand")
                return VerifyHostedCutoverContracts(executable, VerifyNoteReadDemand);
            if (args.Length == 2 && args[1] == "--verify-normal-close-publication-preparation")
                return VerifyHostedCutoverContracts(executable, VerifyNormalClosePublicationPreparation);
            if (args.Length == 2 && args[1] == "--verify-rich-host-restoration")
                return VerifyHostedCutoverContracts(executable, VerifyRichHostRestoration);
            if (args.Length == 2 && args[1] == "--verify-compact-identity")
                return VerifyHostedCutoverContracts(executable, VerifyCompactIdentityReplay);
            if (args.Length == 2 && args[1] == "--verify-incoming-smoke-publication")
                return VerifyHostedCutoverContracts(executable, VerifyIncomingSmokePublication);
            if (args.Length == 2 && args[1] == "--verify-actualc38-note-readiness")
                return VerifyHostedCutoverContracts(executable, VerifyActualC38NoteReadinessContracts);
            if (args.Length == 2 && args[1] == "--verify-current1eee-note-contexts")
                return VerifyHostedCutoverContracts(executable, VerifyCurrent1eeeNoteContracts);
            if (args.Length == 2 && args[1] == "--verify-person-search-integration")
                return VerifyHostedCutoverContracts(executable, VerifyPersonSearchIntegration);
            if (args.Length == 2 && args[1] == "--verify-retained-search-overlay")
                return VerifyHostedCutoverContracts(executable,
                    (type, root) => VerifyPersonSearchIntegration(type, root, retainedOnly: true));
            if (args.Length == 2 && args[1] == "--verify-cutover-ordinary-fixture-state")
                return VerifyHostedCutoverContracts(executable, VerifyHostedOrdinaryFixtureStateContracts);
            if (args.Length == 2 && args[1] == "--verify-xaml") return VerifyCompiledXaml(executable);
            if (args.Length == 2 && args[1] == "--verify-exit") return VerifyCompiledXaml(executable, verifyExit: true);
            if (args.Length == 2 && args[1] == "--verify-exit-host-startup")
                return VerifyCompiledXaml(executable, verifyExitStartup: true);
            if (args.Length == 2 && args[1] == "--verify-snapshot-adoption")
                return VerifyCompiledXaml(executable, verifySnapshotAdoption: true);
            if (args.Length == 2 && args[1] == "--verify-legacy-save-feedback")
                return VerifyCompiledXaml(executable, verifyLegacySaveFeedback: true);
            if (args.Length == 2 && args[1] == "--verify-preferred-name-retention")
                return VerifyCompiledXaml(executable, verifyPreferredNameRetention: true);
            if (args.Length == 2 && args[1] == "--verify-preferred-name-snapshot-deferral")
                return VerifyCompiledXaml(executable, verifyPreferredSnapshotDeferral: true);
            if (args.Length == 2 && args[1] == "--verify-profile-note-refresh")
                return HarnessRuntime.Run("compiled-profile-note-refresh",
                    () => VerifyCompiledXaml(executable, verifyProfileNoteRefresh: true));
            if (args.Length == 2 && args[1] == "--verify-profile-refresh")
                return HarnessRuntime.Run("compiled-profile-refresh",
                    () => VerifyCompiledXaml(executable, verifyProfileRefresh: true));
            if (args.Length == 2 && args[1] == "--verify-tree-presentation")
                return HarnessRuntime.Run("compiled-tree-presentation",
                    () => VerifyCompiledXaml(executable, verifyTreePresentation: true));
            if (args.Length == 2 && args[1] == "--verify-update-yield")
                return HarnessRuntime.Run("compiled-update-yield",
                    () => VerifyCompiledXaml(executable, verifyUpdateYield: true));
            var selection = new DesktopScenarioSelection(AppContext.BaseDirectory);
            var actions = new Dictionary<string, Action<string>>
            {
                ["profile-journey"] = root => RunProfileJourney(executable, root),
                ["draft-navigation"] = root => RunNavigationFeedbackFlow(executable, root),
                ["person-note-rendering"] = root => RunPersonNoteFlow(executable, root),
                ["team-context-history"] = root => RunTeamContextFlow(executable, root),
                ["search-favorites"] = root => RunSearchFavoriteFlow(executable, root),
                ["archival-navigation"] = root => RunPersonArchivalFlow(executable, root),
                ["compatibility-settings"] = root => RunWorkspaceCompatibilityFlow(executable, root),
                ["update-settings"] = root => RunUpdateSettingsFlow(executable, root),
                ["external-refresh"] = root => RunExternalRefreshFlow(executable, root),
                ["responsive-workspace"] = root => RunSearchFavoriteFlow(executable, root, responsive: true),
                ["responsive-draft-navigation"] = root => RunNavigationFeedbackFlow(executable, root, responsive: true),
                ["responsive-team-history"] = root => RunTeamContextFlow(executable, root, responsive: true),
                ["scale-navigation-rendering"] = root => RunScaleNavigationFlow(executable, root),
                ["scale-mutation-save-prefix"] = root => RunScaleMutationPrefix(executable, root),
                ["scale-mutation-delivery-tail"] = root => RunScaleMutationTail(executable, root)
            };
            if (!selection.All.Order().SequenceEqual(actions.Keys.Order()))
                throw new InvalidOperationException("The desktop manifests and real scenario implementations differ.");
            if (installer) actions.Add("installer-" + args[2], root =>
            {
                if (args[2] == "update") RunInstalledAutomaticUpdate(args, root);
                else RunInstallerFlow(args, root);
            });
            var selected = installer ? ["installer-" + args[2]] : selection.Select(args.Length == 1 ? null : args[1]);
            if (selected.Intersect(selection.HighResolution.Concat(selection.Scale)).Any())
                HostedDisplayPreparation.Verify(new WindowsHostedDisplay().Read());
            var results = Path.GetFullPath(Environment.GetEnvironmentVariable("DESKTOP_RESULTS_DIRECTORY") ??
                Path.Combine(".ci-results", "desktop"));
            Directory.CreateDirectory(results);
            var fixtures = Path.GetFullPath(Path.Combine(".test-workspaces", "desktop-" + Guid.NewGuid().ToString("N")));
            var scenarios = selected.Select(name => new SmokeScenario(name, root =>
            {
                diagnostics = Path.Combine(results, name + "-ui.log");
                var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
                Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", Path.Combine(root, "app-settings"));
                try { actions[name](root); }
                finally
                {
                    foreach (var process in Applications) { StopApplication(process); process.Dispose(); }
                    Applications.Clear();
                    Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
                }
            })).ToArray();
            return new SmokeScenarioRunner(scenarios).Run(fixtures, Path.Combine(results, "scenarios.json")) ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    internal static Process Launch(string path)
    {
        var settings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(settings) || !Path.IsPathFullyQualified(settings))
            throw new InvalidOperationException("Desktop fixtures require an explicit isolated app-settings directory before launching.");
        var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = false })
            ?? throw new InvalidOperationException("WPF application did not launch.");
        Applications.Add(process);
        return process;
    }

    private static void StopApplication(Process process)
    {
        try
        {
            if (process.HasExited) return;
            var text = new StringBuilder($"Abnormal scenario exit; app PID {process.Id}.{Environment.NewLine}");
            try
            {
                var window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                if (window is not null)
                    foreach (AutomationElement control in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                        if (control.Current.AutomationId.Length > 0)
                            text.AppendLine($"{control.Current.AutomationId}: {control.Current.Name}");
            }
            catch (Exception error) { text.AppendLine("UI diagnostic capture failed: " + error.Message); }
            File.AppendAllText(diagnostics, text.ToString());
            process.Kill(true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { /* A using-scoped scenario already disposed this process. */ }
    }

    internal static AutomationElement Window(Process process)
    {
        AutomationElement? window = null;
        Await(() =>
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException($"App exited with {process.ExitCode}.");
            window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
            return window is not null && window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "OpenSettings")) is not null;
        });
        ((WindowPattern)window!.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
        return window!;
    }
    internal static AutomationElement Find(AutomationElement window, string id, ControlType? controlType = null)
    {
        using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.UiaDiscovery);
        Condition condition = new PropertyCondition(AutomationElement.AutomationIdProperty, id);
        if (controlType is not null)
            condition = new AndCondition(condition, new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
        return window.FindFirst(TreeScope.Descendants, condition)
            ?? throw new InvalidOperationException("Missing actual UI control: " + id);
    }
    internal static void Set(AutomationElement window, string id, string value)
    {
        var pattern = ControlReadiness.AwaitPattern(() => Find(window, id), control => control.Current.IsEnabled,
            control => (ValuePattern)control.GetCurrentPattern(ValuePattern.Pattern), Await);
        using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.NativeSetValue);
        pattern.SetValue(value);
    }
    internal static void Click(AutomationElement window, string id, ControlType? controlType = null)
    {
        var pattern = ControlReadiness.AwaitPattern(() => Find(window, id, controlType), control => control.Current.IsEnabled,
            control => (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern), Await);
        using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.NativeInvoke);
        pattern.Invoke();
    }
    private static string Status(AutomationElement window) => window.Current.HelpText;
    private static string Activity(AutomationElement window) => window.Current.ItemStatus;
    private static string NoteFeedback(AutomationElement window, string scope) =>
        Find(window, scope + "NotesSurface").Current.ItemStatus;
    private static string Freshness(AutomationElement window) => Find(window, "WorkspaceName").Current.ItemStatus;

    private static void PickWorkspaceFolder(AutomationElement window, string? folder,
        string action = "OpenWorkspace", Action? observePicker = null)
    {
        RequireSmokeSelectableFolder(folder);
        ControlReadiness.AwaitPattern(() => Find(window, action),
            control => control.Current.IsEnabled && !control.Current.IsOffscreen,
            control => (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern), Await).Invoke();
        AutomationElement? FindPicker()
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.NameProperty, "Open Workspace"));
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition) ??
                window.FindFirst(TreeScope.Descendants, condition);
        }
        AutomationElement? picker = null;
        Await(() => (picker = FindPicker()) is not null && !picker.Current.IsOffscreen);
        observePicker?.Invoke();
        if (folder is not null)
        {
            AutomationElement FolderEntry() => Find(picker!, "1152", ControlType.Edit);
            var entry = ControlReadiness.AwaitPattern(FolderEntry, control => control.Current.IsEnabled,
                control => (ValuePattern)control.GetCurrentPattern(ValuePattern.Pattern), Await);
            entry.SetValue(Path.GetFullPath(folder));
            if (entry.Current.Value != Path.GetFullPath(folder))
                throw new InvalidOperationException("The native picker did not retain the requested folder entry.");
        }
        Click(picker!, folder is null ? "2" : "1", ControlType.Button);
        Await(() => FindPicker() is null);
    }

    private static void RequireSmokeSelectableFolder(string? folder)
    {
        if (folder is not null && !Directory.Exists(folder))
            throw new InvalidOperationException("The scenario must explicitly prepare a selectable folder before opening the native picker: " + folder);
    }

    private static void ShowTechnicalSettings(AutomationElement window)
    {
        SetSettingsVisible(window, true);
        var details = Find(window, "TechnicalDetails");
        var expansion = (ExpandCollapsePattern)details.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (expansion.Current.ExpandCollapseState != ExpandCollapseState.Expanded) expansion.Expand();
        Await(() => expansion.Current.ExpandCollapseState == ExpandCollapseState.Expanded);
    }
    private static void SetSettingsVisible(AutomationElement window, bool visible)
    {
        bool IsOpen()
        {
            var title = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsTitle"));
            return title is not null && !title.Current.IsOffscreen;
        }
        if (IsOpen() == visible) return;
        Click(window, "OpenSettings");
        Await(() => IsOpen() == visible);
    }
    private static void AwaitLocalWorkIdle(AutomationElement window)
    {
        Await(() => IsLocalWorkIdle(window));
    }
    private static bool IsLocalWorkIdle(AutomationElement window) =>
        LocalWorkObservation.Observe(() => Activity(window), () => Status(window), () => ReadIdleControls(window));

    private static IReadOnlyList<LocalWorkObservation.Control> ReadIdleControls(AutomationElement window)
    {
        using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.UiaDiscovery);
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ItemStatusProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        AutomationElementCollection controls;
        using (cache.Activate())
            controls = window.FindAll(TreeScope.Descendants, new OrCondition(
                new[] { "Email", "WorkspaceStart", "PersonNotesSurface", "TeamNotesSurface" }
                    .Select(id => new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray()));
        return controls.Cast<AutomationElement>().Select(control => new LocalWorkObservation.Control(
            control.Cached.AutomationId, control.Cached.Name, control.Cached.ItemStatus, control.Cached.IsOffscreen)).ToArray();
    }
    private static void AssertValue(AutomationElement window, string id, string expected)
    {
        var actual = Value(window, id);
        if (actual != expected) throw new InvalidOperationException($"{id}: expected '{expected}', got '{actual}'.");
    }
    internal static void OpenPerson(AutomationElement window, params string[] ancestors)
    {
        AwaitLocalWorkIdle(window);
        EnsurePeopleBrowsing(window);
        foreach (var email in ancestors)
        {
            AutomationElement? ancestor = null;
            Await(() => (ancestor = Find(window, "People").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, email))) is not null);
            var expand = (ExpandCollapsePattern)ancestor!.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            expand.Expand();
            Await(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded);
            expand.Collapse();
            Await(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
            expand.Expand();
        }
        SelectTreePerson(window, "avery@example.com");
        Await(() => Find(window, "EditPreferredName").Current.IsEnabled);
    }
    private static void NavigateToManager(AutomationElement window, string email)
    {
        AwaitLocalWorkIdle(window);
        Click(window, "NavigateManager");
        Await(() => Find(window, "Email").Current.Name == email);
    }
    internal static void Close(Process process)
    {
        AwaitLocalWorkIdle(Window(process));
        process.CloseMainWindow();
        if (!process.WaitForExit(10000)) throw new TimeoutException("App did not close normally.");
    }
    internal static void Await(Func<bool> condition)
    {
        using var waiting = HarnessRuntime.Measure(HarnessRuntime.Work.NativeWait);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                using var observation = HarnessRuntime.Measure(HarnessRuntime.Work.Observation);
                if (condition()) return;
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Missing actual UI control:", StringComparison.Ordinal)) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Actual UI condition did not become true within 20 seconds.");
    }
}
