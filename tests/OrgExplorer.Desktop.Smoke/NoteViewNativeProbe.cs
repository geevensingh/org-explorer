using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static int DiagnoseNoteView(string executable)
    {
        string? Env(string name) => Environment.GetEnvironmentVariable(name);
        NoteObservationEvidence.RequireHostedProbe(Env("GITHUB_ACTIONS"), Env("RUNNER_OS"), Env("GITHUB_JOB"),
            Env("GITHUB_SHA"), Env("GITHUB_RUN_ID"), Env("GITHUB_RUN_ATTEMPT"));
        if (Env("NOTE_VIEW_PRODUCT_SOURCE_TREE") != "172f977b4ea6c77f4227f6ab804322032a004f5d")
            throw new InvalidOperationException("The hosted job did not verify the required unchanged product source tree.");
        var results = Path.GetFullPath(Env("DESKTOP_RESULTS_DIRECTORY") ??
            throw new InvalidOperationException("The note-view probe requires its own results directory."));
        Directory.CreateDirectory(results);
        using var output = new StreamWriter(new FileStream(Path.Combine(results, "note-view.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Write(object value) => output.WriteLine(JsonSerializer.Serialize(value));
        string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        Write(new
        {
            record = "binding", diagnosticOnly = true, probeRevision = Env("GITHUB_SHA"),
            integrationBase = "d9254c3c5870a0f1d6b7b028c5af6b28a7fbb5e2",
            productSourceTree = Env("NOTE_VIEW_PRODUCT_SOURCE_TREE"),
            runId = Env("GITHUB_RUN_ID"), runAttempt = Env("GITHUB_RUN_ATTEMPT"), job = Env("GITHUB_JOB"),
            executableSha256 = Hash(executable), assemblySha256 = Hash(Path.ChangeExtension(executable, ".dll")),
            harnessSha256 = Hash(typeof(Program).Assembly.Location), harnessProcessId = Environment.ProcessId,
            runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
            processors = Environment.ProcessorCount, budgetSeconds = 180, plannedStages = 13, plannedSamples = 104,
            interpretation = "Client observation and unchanged-caller accounting only; nested spans overlap. Not provider cost, product latency or normal CI acceptance. Nothing is deducted."
        });
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "note-view-probe-" + Guid.NewGuid().ToString("N")));
        var previousSettings = Env("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var timer = Stopwatch.StartNew();
        var outcome = "Interrupted";
        var sampleCount = 0;
        var stageCount = 0;
        var failures = 0;
        Process? process = null;
        try
        {
            if (ConfigureDisplay("--verify-standard-display") != 0)
                throw new InvalidOperationException("The diagnostic desktop prerequisite failed.");
            foreach (var name in new[] { "person-note-rendering", "draft-navigation" })
            {
                Bound();
                var fixture = Path.Combine(root, name);
                var settings = Path.Combine(fixture, "app-settings");
                PrepareSettings(settings);
                diagnostics = Path.Combine(results, "account-" + name + "-ui.log");
                var records = new List<string>(2);
                try
                {
                    HarnessRuntime.Run("account-" + name, () =>
                    {
                        if (name == "person-note-rendering") RunPersonNoteFlow(executable, fixture);
                        else RunNavigationFeedbackFlow(executable, fixture);
                        return true;
                    }, write: records.Add);
                }
                finally
                {
                    foreach (var record in records) Write(new { record = "runtime", caller = name, raw = record });
                    foreach (var app in Applications) { StopApplication(app); app.Dispose(); }
                    Applications.Clear();
                }
                using var end = ParseEnd(records);
                var report = end.RootElement;
                var calls = Operation(report, HarnessRuntime.Work.NoteViewCall);
                if (calls.GetProperty("completed").GetInt64() == 0)
                    throw new InvalidOperationException("The unchanged native caller never exercised AssertNoteView.");
                Write(new { record = "feedback", caller = name, actualNativeBoundaryExercised = true,
                    command = "OrgExplorer.Desktop.Smoke <normal-executable> --diagnose-note-view",
                    outcome = report.GetProperty("outcome").GetString(), outer = calls,
                    poll = Operation(report, HarnessRuntime.Work.NoteViewPoll),
                    tabLookup = Operation(report, HarnessRuntime.Work.NoteViewTabLookup),
                    rawEditorLookup = Operation(report, HarnessRuntime.Work.NoteViewEditorLookup) });
                Console.WriteLine($"NOTE-VIEW FEEDBACK: unchanged {name} PASS; actual helper calls={calls.GetProperty("completed")}; ms={calls.GetProperty("elapsedMs")}.");
            }
            Write(new
            {
                record = "ranked-hypotheses-before-candidate-tests",
                hypotheses = new[]
                {
                    "1. Repeated discovery dominates this helper: one fresh filtered request will lower matched ready-poll cost while preserving exact outcomes.",
                    "2. Product/provider waiting dominates: reducing explicit client query count will not consistently lower paired elapsed cost.",
                    "3. This helper is too small to resolve the gate: measured whole-helper contribution times actual workload frequency will remain below the missing full-tail budget even if its entire cost vanished.",
                    "4. Snapshot semantics differ in transition states: missing/disabled/offscreen/owner-change or detached-provider outcomes will disagree; any disagreement rejects the candidate."
                }
            });
            Console.WriteLine("NOTE-VIEW HYPOTHESES BEFORE CANDIDATE TESTS: 1 repeated discovery dominates; 2 provider/product waiting dominates; 3 contribution too small for900s; 4 transition semantics disagree. Predictions retained in note-view.jsonl.");
            Bound();
            var workspace = Path.Combine(root, "comparison");
            PrepareSettings(Path.Combine(root, "comparison-settings"));
            diagnostics = Path.Combine(results, "note-view-ui.log");
            var identities = new Dictionary<(string, string), Guid>();
            using (var seed = ApplicationWorkflow.CreateFileBacked())
            {
                if (!seed.OpenWorkspace(workspace, WorkspaceOpenMode.CreateLocalSqlite) ||
                    !seed.ApplyImport(seed.PreviewImport("""
                        {"schemaVersion":1,"batchId":"97000000-0000-4000-8000-000000000003",
                         "observedAt":"2026-11-01T17:00:00Z","people":[
                         {"email":"morgan@example.com","displayName":"Diagnostic owner","managerEmail":null},
                         {"email":"avery@example.com","displayName":"Diagnostic owner","managerEmail":"morgan@example.com"}]}
                        """).Id))
                    throw new InvalidOperationException("The note-view comparison fixture could not be created.");
                foreach (var owner in new[] { "morgan@example.com", "avery@example.com" })
                {
                    var note = seed.CreatePersonNote(owner, "Diagnostic note", "Original diagnostic writing") ??
                        throw new InvalidOperationException("The comparison Person note could not be created.");
                    identities.Add((owner, "Person"), note.Id);
                }
                var team = seed.CreateTeamNote("morgan@example.com", "Diagnostic Team note", "Original diagnostic Team writing") ??
                    throw new InvalidOperationException("The comparison Team note could not be created.");
                identities.Add(("morgan@example.com", "Team"), team.Id);
            }
            process = Launch(executable);
            var window = Window(process);
            Write(new { record = "process", processId = process.Id, harnessProcessId = Environment.ProcessId,
                windowHandle = window.Current.NativeWindowHandle, executableSha256 = Hash(process.MainModule!.FileName!) });
            Compare("startup-missing-tab", "Person", false, "Missing", null);
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            OpenPerson(window, "morgan@example.com");
            Compare("report-read", "Person", false, "Ready", "avery@example.com");
            Compare("report-unselected-edit", "Person", true, "NotReady", "avery@example.com");
            SelectTab(window, "PersonNoteEditTab");
            Compare("report-edit", "Person", true, "Ready", "avery@example.com");
            Compare("report-unselected-read", "Person", false, "NotReady", "avery@example.com");
            NavigateToManager(window, "morgan@example.com");
            SelectTab(window, "PersonNoteEditTab");
            Compare("duplicate-owner-edit", "Person", true, "Ready", "morgan@example.com");
            SelectTab(window, "TeamNoteEditTab");
            Compare("team-edit", "Team", true, "Ready", "morgan@example.com");
            var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
            if (!scroll.Current.VerticallyScrollable)
                throw new InvalidOperationException("The existing two-note editor fixture is not vertically scrollable.");
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
            Await(() => Find(window, "PersonNoteTitle").Current.IsOffscreen);
            Write(new { record = "offscreen-precondition", actualPersonTitleOffscreen = true });
            Compare("offscreen-person-edit", "Person", true, "Ready", "morgan@example.com");
            SelectTab(window, "TeamNoteReadTab");
            Compare("team-read", "Team", false, "Ready", "morgan@example.com");
            SetSettingsVisible(window, true);
            Compare("settings-missing-tab", "Team", false, "Missing", null);
            SetSettingsVisible(window, false);
            SelectTab(window, "PersonNoteEditTab");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(workspace, "workspace.sqlite"), Pooling = false }.ToString()))
            {
                connection.Open();
                using var held = connection.BeginTransaction();
                Set(window, "PersonNoteMarkdown", "Diagnostic failed writing");
                Await(() => Find(window, "OperationalNotice").Current.ItemStatus.Contains("Failed", StringComparison.Ordinal));
                if (Find(window, "PersonNoteTitle").Current.IsEnabled || Find(window, "PersonNoteMarkdown").Current.IsEnabled)
                    throw new InvalidOperationException("The actual failed save did not disable both editors.");
                Compare("failed-disabled-edit", "Person", true, "NotReady", "morgan@example.com");
                SelectTreePerson(window, "avery@example.com", expectNavigation: false);
                Await(() => CurrentPersonEmail(window) == "avery@example.com");
                Compare("failed-other-owner-read", "Person", false, "Ready", "avery@example.com");
                held.Rollback();
            }
            // This process intentionally retains the failed draft; termination is diagnostic cleanup, not safe-close proof.
            process.Kill(true);
            if (!process.WaitForExit(5000)) throw new TimeoutException("The owned failed-draft process did not terminate.");
            Compare("detached-provider", "Person", true, "Unavailable", null);
            outcome = failures == 0 && sampleCount == 104 && stageCount == 13 ? "Passed" : "Failed";
            return outcome == "Passed" ? 0 : 1;

            void Compare(string stage, string family, bool editing, string expected, string? owner)
            {
                Bound();
                var ownerMatches = owner is null || CurrentPersonEmail(window) == owner;
                var identityMatches = owner is null ||
                    Find(window, family + "NoteHeading").Current.HelpText == identities[(owner, family)].ToString();
                if (!ownerMatches || !identityMatches) throw new InvalidOperationException("The native stage has the wrong current note owner.");
                if (owner is not null) CaptureVisualEvidence(window, "note-view-" + stage);
                var observations = new List<string>(8);
                foreach (var original in new[] { true, false, false, true, false, true, true, false })
                {
                    Bound();
                    var records = new List<string>(2);
                    var actual = "Interrupted";
                    string? errorType = null;
                    int? hResult = null;
                    var elapsed = 0d;
                    try
                    {
                        var ready = HarnessRuntime.Run(original ? "note-view-original" : "note-view-fresh", () =>
                        {
                            var started = Stopwatch.GetTimestamp();
                            try { return original ? ObserveOriginalNoteView(window, family, editing) : ObserveFreshNoteView(window, family, editing); }
                            finally { elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
                        }, write: records.Add);
                        actual = ready ? "Ready" : "NotReady";
                    }
                    catch (ElementNotAvailableException error)
                    {
                        actual = "Unavailable"; errorType = error.GetType().FullName; hResult = error.HResult;
                    }
                    catch (InvalidOperationException error) when (error.Message.StartsWith("Missing actual UI control:", StringComparison.Ordinal))
                    {
                        actual = "Missing"; errorType = error.GetType().FullName; hResult = error.HResult;
                    }
                    using var end = ParseEnd(records);
                    var operations = end.RootElement.GetProperty("operations");
                    var correct = actual == expected;
                    if (!correct) failures++;
                    Write(new { record = "sample", stage, family, editing, expected, actual, correct,
                        order = observations.Count, arm = original ? "Original" : "Fresh", elapsedMs = elapsed,
                        errorType, hResult, operations });
                    observations.Add(actual);
                    sampleCount++;
                }
                var equivalent = observations.All(value => value == observations[0]);
                if (!equivalent) failures++;
                Write(new { record = "stage", stage, expected, equivalent, ownerMatches, identityMatches });
                stageCount++;
            }
        }
        finally
        {
            var cleaned = false;
            try
            {
                if (process is not null) { StopApplication(process); process.Dispose(); }
                Applications.Clear();
                Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
                if (outcome == "Passed") Directory.Delete(root, true);
                cleaned = true;
            }
            finally { Write(new { record = "end", outcome = cleaned ? outcome : "Failed",
                stageCount, sampleCount, failures, elapsedMs = timer.Elapsed.TotalMilliseconds }); }
        }

        void Bound()
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(180))
                throw new TimeoutException("The note-view experiment exhausted its unchanged 180-second diagnostic bound.");
        }
        void PrepareSettings(string settings)
        {
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "update-settings.json"), """{"schemaVersion":1,"checkOnStartup":false}""");
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
        }
    }

    private static JsonDocument ParseEnd(IReadOnlyList<string> records)
    {
        const string prefix = "DIAGNOSTIC: harness-runtime ";
        var report = JsonDocument.Parse(records[^1][prefix.Length..]);
        if (report.RootElement.GetProperty("boundary").GetString() != "end" ||
            !report.RootElement.GetProperty("diagnosticsComplete").GetBoolean() ||
            report.RootElement.GetProperty("outputFailures").GetProperty("count").GetInt64() != 0)
        {
            report.Dispose();
            throw new InvalidOperationException("The native helper diagnostic lost complete end evidence.");
        }
        return report;
    }

    private static JsonElement Operation(JsonElement report, HarnessRuntime.Work work) =>
        report.GetProperty("operations").EnumerateArray().Single(row => row.GetProperty("name").GetString() == work.ToString());

    private static bool ObserveFreshNoteView(AutomationElement window, string kind, bool editing)
    {
        using var poll = HarnessRuntime.Measure(HarnessRuntime.Work.NoteViewPoll);
        var tabId = $"{kind}Note{(editing ? "Edit" : "Read")}Tab";
        var titleId = kind + "NoteTitle";
        var bodyId = kind + "NoteMarkdown";
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(SelectionItemPattern.IsSelectedProperty);
        cache.Add(SelectionItemPattern.Pattern);
        AutomationElementCollection nodes;
        using (HarnessRuntime.Measure(HarnessRuntime.Work.NoteViewFreshLookup))
        using (cache.Activate())
            nodes = window.FindAll(TreeScope.Descendants, new OrCondition(new[] { tabId, titleId, bodyId }
                .Select(id => new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray()));
        AutomationElement? FindCached(string id) => nodes.Cast<AutomationElement>().FirstOrDefault(node => node.Cached.AutomationId == id);
        var tab = FindCached(tabId) ?? throw new InvalidOperationException("Missing actual UI control: " + tabId);
        if (!((SelectionItemPattern)tab.GetCachedPattern(SelectionItemPattern.Pattern)).Cached.IsSelected) return false;
        foreach (var id in new[] { titleId, bodyId })
        {
            var editor = FindCached(id);
            if (editing ? editor is null || !editor.Cached.IsEnabled : editor is not null && !editor.Cached.IsOffscreen)
                return false;
        }
        return true;
    }
}
