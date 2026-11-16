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
    private static int DiagnoseIdleObservation(string executable)
    {
        string? Env(string name) => Environment.GetEnvironmentVariable(name);
        NoteObservationEvidence.RequireHostedProbe(Env("GITHUB_ACTIONS"), Env("RUNNER_OS"), Env("GITHUB_JOB"),
            Env("GITHUB_SHA"), Env("GITHUB_RUN_ID"), Env("GITHUB_RUN_ATTEMPT"));
        var results = Path.GetFullPath(Env("DESKTOP_RESULTS_DIRECTORY") ??
            throw new InvalidOperationException("The idle probe requires its own results directory."));
        Directory.CreateDirectory(results);
        using var output = new StreamWriter(new FileStream(Path.Combine(results, "idle-helper.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Write(object value) => output.WriteLine(JsonSerializer.Serialize(value));
        string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        Write(new
        {
            record = "binding", diagnosticOnly = true, revision = Env("GITHUB_SHA"), runId = Env("GITHUB_RUN_ID"),
            runAttempt = Env("GITHUB_RUN_ATTEMPT"), job = Env("GITHUB_JOB"),
            executableSha256 = Hash(executable), assemblySha256 = Hash(Path.ChangeExtension(executable, ".dll")),
            runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
            processors = Environment.ProcessorCount, budgetSeconds = 180, plannedStages = 12, plannedSamples = 96,
            interpretation = "Actual old/new idle predicates; client discovery requests and inclusive observation time, not provider cost, product latency or CI acceptance. Nothing is subtracted."
        });
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "idle-probe-" + Guid.NewGuid().ToString("N")));
        var previousSettings = Env("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var timer = Stopwatch.StartNew();
        var samples = 0;
        var stages = 0;
        var failures = 0;
        var outcome = "Interrupted";
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
            diagnostics = Path.Combine(results, "idle-diagnostic-ui.log");
            var workspace = Path.Combine(root, "workspace");
            using (var seed = ApplicationWorkflow.CreateFileBacked())
            {
                if (!seed.OpenWorkspace(workspace, WorkspaceOpenMode.CreateLocalSqlite) ||
                    !seed.ApplyImport(seed.PreviewImport("""
                        {"schemaVersion":1,"batchId":"97000000-0000-4000-8000-000000000002",
                         "observedAt":"2026-11-01T17:00:00Z","people":[
                         {"email":"morgan@example.com","displayName":"Duplicate diagnostic owner","managerEmail":null},
                         {"email":"avery@example.com","displayName":"Duplicate diagnostic owner","managerEmail":"morgan@example.com"}]}
                        """).Id))
                    throw new InvalidOperationException("The idle probe's SQLite fixture could not be created.");
                foreach (var owner in new[] { "morgan@example.com", "avery@example.com" })
                    if (seed.CreatePersonNote(owner, "Diagnostic note", "Original diagnostic writing") is null)
                        throw new InvalidOperationException("The idle probe's owned note could not be created.");
            }
            process = Launch(executable);
            var window = Window(process);
            Write(new { record = "process", processId = process.Id, harnessProcessId = Environment.ProcessId });
            Compare(window, "startup", "Idle", "");
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            Compare(window, "opened-start", "Idle", "");
            OpenPerson(window, "morgan@example.com");
            Compare(window, "selected-report", "Idle", "avery@example.com");
            SetSettingsVisible(window, true);
            Compare(window, "settings-transition", "Equivalent", null);
            SetSettingsVisible(window, false);
            NavigateToManager(window, "morgan@example.com");
            Compare(window, "selected-manager", "Idle", "morgan@example.com");
            SelectTreePerson(window, "avery@example.com");
            Compare(window, "duplicate-name-report", "Idle", "avery@example.com");
            NavigateToManager(window, "morgan@example.com");
            SelectTab(window, "PersonNoteEditTab");
            AssertNoteView(window, "Person", editing: true);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(workspace, "workspace.sqlite"), Pooling = false }.ToString()))
            {
                connection.Open();
                using var held = connection.BeginTransaction();
                Set(window, "PersonNoteMarkdown", "Diagnostic held owner writing");
                Await(() => Activity(window).Contains("Pending", StringComparison.Ordinal) ||
                    Activity(window).Contains("Saving", StringComparison.Ordinal));
                Compare(window, "held-write-transition", "Busy", "morgan@example.com");
                Await(() => Find(window, "OperationalNotice").Current.ItemStatus.Contains("Failed", StringComparison.Ordinal));
                if (Find(window, "PersonNoteMarkdown").Current.IsEnabled || Find(window, "EditProfile").Current.IsEnabled)
                    throw new InvalidOperationException("The actual failed save did not pause the native editing controls.");
                Compare(window, "failed-disabled-owner", "Busy", "morgan@example.com");
                SelectTreePerson(window, "avery@example.com", expectNavigation: false);
                Await(() => CurrentPersonEmail(window) == "avery@example.com");
                Compare(window, "failed-duplicate-name-report", "Busy", "avery@example.com");
                SelectTreePerson(window, "morgan@example.com", expectNavigation: false);
                Await(() => CurrentPersonEmail(window) == "morgan@example.com");
                AssertValue(window, "PersonNoteMarkdown", "Diagnostic held owner writing");
                Compare(window, "failed-retained-owner", "Busy", "morgan@example.com");
                held.Rollback();
            }
            Click(window, "RetryPersonNoteSave");
            AwaitLocalWorkIdle(window);
            using (var reading = ApplicationWorkflow.CreateFileBacked())
            {
                if (!reading.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly) ||
                    reading.GetPersonNotes("morgan@example.com").Single().Markdown != "Diagnostic held owner writing" ||
                    reading.GetPersonNotes("avery@example.com").Single().Markdown != "Original diagnostic writing")
                    throw new InvalidOperationException("The native retry did not retain and commit writing under its exact owner.");
            }
            Compare(window, "retry-committed-owner", "Idle", "morgan@example.com");
            Close(process);
            Compare(window, "detached-closed-provider", "Unavailable", null);
            outcome = failures == 0 && stages == 12 && samples == 96 ? "Passed" : "Failed";
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
                    stages, samples, failures, elapsedMs = timer.Elapsed.TotalMilliseconds });
            }
        }

        void Compare(AutomationElement window, string stage, string expected, string? owner)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(180))
                throw new TimeoutException("The idle diagnostic's 180-second execution bound was exhausted.");
            if (expected is "Idle" or "Busy") CaptureVisualEvidence(window, "idle-" + stage);
            var observations = new List<(string Outcome, string? Error)>();
            foreach (var original in new[] { true, false, false, true, false, true, true, false })
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(180))
                    throw new TimeoutException("The idle diagnostic's 180-second execution bound was exhausted.");
                var records = new List<string>(2);
                var actual = "Interrupted";
                string? errorType = null;
                int? hResult = null;
                var elapsed = 0d;
                try
                {
                    var idle = HarnessRuntime.Run(original ? "idle-original" : "idle-fresh", () =>
                    {
                        var started = Stopwatch.GetTimestamp();
                        try { return original ? OriginalIdle(window) : IsLocalWorkIdle(window); }
                        finally { elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
                    }, write: records.Add);
                    actual = idle ? "Idle" : "Busy";
                }
                catch (ElementNotAvailableException error)
                {
                    actual = "Unavailable"; errorType = error.GetType().FullName; hResult = error.HResult;
                }
                catch (InvalidOperationException error) when (
                    error.Message.StartsWith("Missing actual UI control:", StringComparison.Ordinal) ||
                    error.Message == "Startup has neither a Person identity nor a purposeful starting view.")
                {
                    actual = error.Message.StartsWith("Missing", StringComparison.Ordinal) ? error.Message : "InvalidStart";
                    errorType = error.GetType().FullName; hResult = error.HResult;
                }
                using var end = JsonDocument.Parse(records[^1]["DIAGNOSTIC: harness-runtime ".Length..]);
                var report = end.RootElement;
                if (!report.GetProperty("diagnosticsComplete").GetBoolean() ||
                    report.GetProperty("outputFailures").GetProperty("count").GetInt64() != 0 ||
                    report.GetProperty("boundary").GetString() != "end")
                    throw new InvalidOperationException("The idle diagnostic lost its real observation boundary.");
                var queries = report.GetProperty("operations").EnumerateArray()
                    .Single(operation => operation.GetProperty("name").GetString() == "UiaDiscovery")
                    .GetProperty("completed").GetInt64();
                var correct = expected == "Equivalent" && actual != "Unavailable" || actual == expected;
                if (!correct) failures++;
                Write(new { record = "sample", stage, expected, order = observations.Count,
                    arm = original ? "Original" : "Fresh", actual, correct, errorType, hResult,
                    discoveryRequests = queries, elapsedMs = elapsed });
                observations.Add((actual, errorType));
                samples++;
            }
            var equivalent = observations.All(observation => observation == observations[0]);
            var ownerMatches = owner is null || CurrentPersonEmail(window) == owner;
            if (!equivalent || !ownerMatches) failures++;
            Write(new { record = "stage", stage, equivalent, ownerMatches, expected,
                originalOutcome = observations[0].Outcome });
            stages++;
        }
    }

    private static bool OriginalIdle(AutomationElement window) =>
        Activity(window) == "Ready" &&
        !Status(window).StartsWith("Pending", StringComparison.Ordinal) &&
        (OriginalEmail(window).Length == 0 ||
            !NoteFeedback(window, "Person").StartsWith("Pending", StringComparison.Ordinal) &&
            !NoteFeedback(window, "Team").StartsWith("Pending", StringComparison.Ordinal));

    private static string OriginalEmail(AutomationElement window)
    {
        using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.UiaDiscovery);
        return CurrentPersonEmail(window);
    }
}
