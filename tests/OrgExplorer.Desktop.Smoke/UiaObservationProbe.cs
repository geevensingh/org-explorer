using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private enum ObservationArm { FullWindowCurrent, FreshFilteredSnapshot }
    private sealed record ProbeControlState(string Id, bool Enabled, bool Offscreen, bool PositiveBounds);
    private sealed class ObservationCalls
    {
        public int FindFirst { get; set; }
        public int FindAll { get; set; }
        public int CurrentProperty { get; set; }
        public int CachedProperty { get; set; }
        public int CurrentPattern { get; set; }
        public int CachedPattern { get; set; }
    }

    private static int DiagnoseUiaObservation(string executable)
    {
        string? Env(string name) => Environment.GetEnvironmentVariable(name);
        NoteObservationEvidence.RequireHostedProbe(Env("GITHUB_ACTIONS"), Env("RUNNER_OS"), Env("GITHUB_JOB"),
            Env("GITHUB_SHA"), Env("GITHUB_RUN_ID"), Env("GITHUB_RUN_ATTEMPT"));
        var results = Path.GetFullPath(Env("DESKTOP_RESULTS_DIRECTORY") ??
            throw new InvalidOperationException("The probe requires its own explicit results directory."));
        Directory.CreateDirectory(results);
        var samples = Path.Combine(results, "uia-observation.jsonl");
        using var output = new StreamWriter(new FileStream(samples, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
        void Write(object value) => output.WriteLine(JsonSerializer.Serialize(value));
        string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        var timer = Stopwatch.StartNew();
        Write(new
        {
            record = "binding", diagnosticOnly = true, revision = Env("GITHUB_SHA"),
            runId = Env("GITHUB_RUN_ID"), runAttempt = Env("GITHUB_RUN_ATTEMPT"), job = Env("GITHUB_JOB"),
            executableSha256 = Hash(executable), assemblySha256 = Hash(Path.ChangeExtension(executable, ".dll")),
            runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
            processors = Environment.ProcessorCount, budgetSeconds = 180, plannedSamples = 64,
            interpretation = "Client API query work and inclusive observation time, not provider callback counts, product latency or CI acceptance. No time is deducted."
        });
        var outcome = "Interrupted";
        var count = 0;
        var failures = 0;
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "uia-probe-" + Guid.NewGuid().ToString("N")));
        var previousSettings = Env("ORG_EXPLORER_SETTINGS_DIRECTORY");
        Process? process = null;
        try
        {
            if (ConfigureDisplay("--verify-standard-display") != 0)
                throw new InvalidOperationException("The diagnostic desktop prerequisite failed.");
            var settings = Path.Combine(root, "app-settings");
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "update-settings.json"),
                """{"schemaVersion":1,"checkOnStartup":false}""");
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
            diagnostics = Path.Combine(results, "diagnostic-ui.log");
            var workspace = Path.Combine(root, "workspace");
            var states = new List<(string Family, NoteObservationEvidence Expected)>();
            using (var seed = ApplicationWorkflow.CreateFileBacked())
            {
                if (!seed.OpenWorkspace(workspace) || !seed.ApplyImport(seed.PreviewImport("""
                    {"schemaVersion":1,"batchId":"97000000-0000-4000-8000-000000000001",
                     "observedAt":"2026-11-01T17:00:00Z","people":[
                     {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"morgan@example.com"},
                     {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":"rowan@example.com"},
                     {"email":"rowan@example.com","displayName":"Rowan Example","managerEmail":null}]}
                    """).Id))
                    throw new InvalidOperationException("The isolated diagnostic fixture could not be created.");
                foreach (var (family, owner, profile) in new[]
                {
                    ("Person", "avery@example.com", "Avery Example"), ("Person", "morgan@example.com", "Morgan Example"),
                    ("Team", "morgan@example.com", "Morgan Example"), ("Team", "rowan@example.com", "Rowan Example")
                })
                {
                    var id = family == "Person"
                        ? seed.CreatePersonNote(owner, "Diagnostic note", "Diagnostic writing")?.Id
                        : seed.CreateTeamNote(owner, "Diagnostic note", "Diagnostic writing")?.Id;
                    if (id is null) throw new InvalidOperationException("The diagnostic note fixture could not be created.");
                    states.Add((family, new(owner, profile, id.ToString()!, "Diagnostic note", "Diagnostic writing",
                        true, true, true, true, false, true, true)));
                }
            }
            process = Launch(executable);
            var window = Window(process);
            Write(new { record = "process", processId = process.Id, harnessProcessId = Environment.ProcessId });
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            OpenPerson(window, "rowan@example.com", "morgan@example.com");
            var orders = new[]
            {
                ObservationArm.FullWindowCurrent, ObservationArm.FreshFilteredSnapshot,
                ObservationArm.FreshFilteredSnapshot, ObservationArm.FullWindowCurrent,
                ObservationArm.FreshFilteredSnapshot, ObservationArm.FullWindowCurrent,
                ObservationArm.FullWindowCurrent, ObservationArm.FreshFilteredSnapshot
            };
            for (var cycle = 0; cycle < 2; cycle++)
            {
                foreach (var index in cycle == 0 ? Enumerable.Range(0, states.Count) : Enumerable.Range(0, states.Count).Reverse())
                {
                    if (timer.Elapsed > TimeSpan.FromSeconds(180))
                        throw new TimeoutException("The diagnostic's 180-second execution bound was exhausted.");
                    var (family, expected) = states[index];
                    SelectTreePerson(window, expected.Owner);
                    SelectTab(window, family + "NoteReadTab");
                    AssertNoteView(window, family, editing: false);
                    SelectTab(window, family + "NoteEditTab");
                    AssertNoteView(window, family, editing: true);
                    expected = expected with
                    {
                        Title = $"Diagnostic {cycle}-{index}", Markdown = $"Changed diagnostic writing {cycle}-{index}"
                    };
                    Set(window, family + "NoteTitle", expected.Title);
                    Set(window, family + "NoteMarkdown", expected.Markdown);
                    AwaitLocalWorkIdle(window);
                    Find(window, family + "NoteTitle").SetFocus();
                    Await(() => Find(window, family + "NoteTitle").Current.HasKeyboardFocus);
                    CaptureVisualEvidence(window, $"diagnostic-state-{cycle}-{index}");
                    for (var order = 0; order < orders.Length; order++)
                    {
                        if (timer.Elapsed > TimeSpan.FromSeconds(180))
                            throw new TimeoutException("The diagnostic's 180-second execution bound was exhausted.");
                        var arm = orders[order];
                        var calls = new ObservationCalls();
                        var started = Stopwatch.GetTimestamp();
                        var observation = ReadProbeNote(window, family, arm, calls, out var controls);
                        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        var checks = observation.Check(expected);
                        count++;
                        if (!checks.Correct) failures++;
                        Write(new
                        {
                            record = "sample", cycle, state = index, family, order, arm = arm.ToString(),
                            elapsedMs, calls, checks, controls, titleFocused = observation.TitleFocused,
                            bodyFocused = observation.BodyFocused
                        });
                    }
                }
            }
            Close(process);
            outcome = failures == 0 && count == 64 ? "Passed" : "Failed";
            return outcome == "Passed" ? 0 : 1;
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
            finally
            {
                Write(new { record = "end", outcome = cleaned ? outcome : "Failed",
                    samples = count, failures, elapsedMs = timer.Elapsed.TotalMilliseconds });
            }
        }
    }

    private static NoteObservationEvidence ReadProbeNote(AutomationElement window, string family, ObservationArm arm,
        ObservationCalls calls, out ProbeControlState[] controls)
    {
        var ids = new[] { "Email", "ProfileHeading", family + "NoteHeading",
            family + "NoteTitle", family + "NoteMarkdown", family + "NoteEditTab" };
        var cached = arm == ObservationArm.FreshFilteredSnapshot;
        AutomationElement[] nodes;
        if (cached)
        {
            var cache = new CacheRequest { TreeScope = TreeScope.Element };
            foreach (var property in new[] { AutomationElement.AutomationIdProperty, AutomationElement.NameProperty,
                AutomationElement.HelpTextProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
                AutomationElement.BoundingRectangleProperty, AutomationElement.HasKeyboardFocusProperty,
                ValuePattern.ValueProperty, ValuePattern.IsReadOnlyProperty, SelectionItemPattern.IsSelectedProperty })
                cache.Add(property);
            cache.Add(ValuePattern.Pattern);
            cache.Add(SelectionItemPattern.Pattern);
            AutomationElementCollection found;
            using (cache.Activate())
            {
                calls.FindAll++;
                found = window.FindAll(TreeScope.Descendants, new OrCondition(ids.Select(id =>
                    new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray()));
            }
            var byId = found.Cast<AutomationElement>().ToDictionary(node => Property<string>(node, AutomationElement.AutomationIdProperty));
            nodes = ids.Select(id => byId.TryGetValue(id, out var node) ? node :
                throw new InvalidOperationException("The fresh diagnostic snapshot is missing a required live control.")).ToArray();
        }
        else nodes = ids.Select(id => { calls.FindFirst++; return Find(window, id); }).ToArray();
        T Property<T>(AutomationElement node, AutomationProperty property)
        {
            object value;
            if (cached) { calls.CachedProperty++; value = node.GetCachedPropertyValue(property, ignoreDefaultValue: true); }
            else { calls.CurrentProperty++; value = node.GetCurrentPropertyValue(property, ignoreDefaultValue: true); }
            return value is T typed ? typed :
                throw new InvalidOperationException("A diagnostic control does not expose a required current property.");
        }
        bool Pattern(AutomationElement node, AutomationPattern pattern)
        {
            if (cached) { calls.CachedPattern++; return node.TryGetCachedPattern(pattern, out _); }
            calls.CurrentPattern++;
            return node.TryGetCurrentPattern(pattern, out _);
        }
        ProbeControlState ControlState(AutomationElement node, int index)
        {
            var enabled = Property<bool>(node, AutomationElement.IsEnabledProperty);
            var offscreen = Property<bool>(node, AutomationElement.IsOffscreenProperty);
            var bounds = Property<Rect>(node, AutomationElement.BoundingRectangleProperty);
            return new(ids[index], enabled, offscreen, !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0);
        }
        var patterns = Pattern(nodes[3], ValuePattern.Pattern) & Pattern(nodes[4], ValuePattern.Pattern) &
            Pattern(nodes[5], SelectionItemPattern.Pattern);
        controls = nodes.Select(ControlState).ToArray();
        return new(Property<string>(nodes[0], AutomationElement.NameProperty),
            Property<string>(nodes[1], AutomationElement.NameProperty),
            Property<string>(nodes[2], AutomationElement.HelpTextProperty),
            Property<string>(nodes[3], ValuePattern.ValueProperty), Property<string>(nodes[4], ValuePattern.ValueProperty),
            controls.All(control => control.Enabled), controls.All(control => !control.Offscreen && control.PositiveBounds),
            !Property<bool>(nodes[3], ValuePattern.IsReadOnlyProperty) & !Property<bool>(nodes[4], ValuePattern.IsReadOnlyProperty),
            Property<bool>(nodes[3], AutomationElement.HasKeyboardFocusProperty),
            Property<bool>(nodes[4], AutomationElement.HasKeyboardFocusProperty),
            Property<bool>(nodes[5], SelectionItemPattern.IsSelectedProperty), patterns);
    }
}
