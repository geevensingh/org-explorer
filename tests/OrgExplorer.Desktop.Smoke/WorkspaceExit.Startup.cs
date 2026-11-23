using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static string ExitHostActivity(string activity) => activity switch
    {
        "Ready" => "Ready",
        "Reopening Workspace after update..." => "UpdateStartup",
        "Opening Workspace..." => "Opening",
        _ => "Other"
    };

    private static string RedactExitHostError(string error)
    {
        var message = Regex.Replace(error, @"(?m)(\s+at .+?) in [^\r\n]+:line (\d+)", "$1 (line $2)");
        message = Regex.Replace(message, @"(?i)\b[a-z]:[\\/][^\r\n]*|\\\\[^\r\n]*", "<path>");
        return message[..Math.Min(message.Length, 8192)];
    }

    private static void ReportExitHostObservationFailure(Exception primary, Exception secondary)
    {
        primary.Data["ExitHostObservationFailure"] = RedactExitHostError(secondary.ToString());
        try { Console.Error.WriteLine("EXIT-HOST-OBSERVATION-FAILURE " + primary.Data["ExitHostObservationFailure"]); }
        catch (Exception reporting)
        { primary.Data["ExitHostObservationReportingFailure"] = RedactExitHostError(reporting.ToString()); }
    }

    private sealed class ExitHostObservation(string root, string mode, string display)
    {
        private readonly List<object> stages = [];

        public void Record(string stage, string? activity = null, bool? openEnabled = null,
            int? people = null, int? requests = null, Exception? error = null)
        {
            if (stages.Count >= 24) throw new InvalidOperationException("Exit host exceeded its bounded stage inventory.");
            stages.Add(new
            {
                stage, timestamp = Stopwatch.GetTimestamp(), activity, openEnabled, people, requests,
                error = error is null ? null : new
                { type = error.GetType().Name, message = RedactExitHostError(error.Message), error.HResult }
            });
            var path = Path.Combine(root, "host-observation.json");
            File.WriteAllText(path + ".writing", JsonSerializer.Serialize(new
            {
                schemaVersion = 1, mode, display, processId = Environment.ProcessId,
                timestampFrequency = Stopwatch.Frequency, stages
            }));
            File.Move(path + ".writing", path, true);
        }
    }

    private static void WriteExitHostReadiness(Process process, string root, string mode, string stage,
        Exception? failure, string destination)
    {
        JsonElement? host = null;
        string? observationReadError = null;
        Exception? observationFailure = null;
        var path = Path.Combine(root, "host-observation.json");
        if (File.Exists(path))
            try
            {
                using var observed = JsonDocument.Parse(File.ReadAllText(path));
                host = observed.RootElement.Clone();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                observationFailure = error;
                observationReadError = RedactExitHostError(error.ToString());
            }
        else
        {
            observationReadError = "The child has not published a stage observation.";
            observationFailure = new IOException(observationReadError);
        }
        process.Refresh();
        var exited = process.HasExited;
        var stderr = exited ? RedactExitHostError(process.StandardError.ReadToEnd()) : null;
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            mode, stage, processId = process.Id, exited, exitCode = exited ? process.ExitCode : (int?)null,
            error = failure is null ? null : RedactExitHostError(failure.ToString()),
            stderr, host, observationReadError,
            boundary = "Owned compiled exit-host startup/readiness observation, not native exit acceptance."
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(destination, json);
        if (failure is not null) Console.Error.WriteLine("EXIT-HOST-READINESS-FAILURE " + json);
        if (failure is null && observationFailure is not null)
            throw new IOException("Required exit-host readiness evidence was unavailable; see its recorded observation error.", observationFailure);
    }

    private static void VerifyOwnedExitHostStartup(string executable, string settings)
    {
        var root = Path.Combine(settings, ".test-workspaces", "exit-startup");
        using var process = StartWorkspaceExitFixture(executable, root, "unconfigured", native: false, replayStartup: true);
        try
        {
            if (!process.WaitForExit(10000))
                throw new TimeoutException("The owned invisible exit host did not complete its Loaded/setup/exit sequence.");
            var error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(root, "ready.json")))
                throw new InvalidOperationException("The actual compiled exit host failed readiness after product Loaded: " + error);
            using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ready.json")));
            if (ready.RootElement.GetProperty("peopleCount").GetInt32() != 2 ||
                ready.RootElement.GetProperty("selectedOwner").GetString() != "morgan@example.com")
                throw new InvalidOperationException("Loaded/setup did not reach the real populated selected Workspace.");
            using var observed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "host-observation.json")));
            var stages = observed.RootElement.GetProperty("stages").EnumerateArray().ToArray();
            if (stages.Single(item => item.GetProperty("stage").GetString() == "preparation")
                    .GetProperty("activity").GetString() != "UpdateStartup" ||
                stages.Count(item => item.GetProperty("stage").GetString() == "open-requested") != 1 ||
                stages.Single(item => item.GetProperty("stage").GetString() == "open-requested")
                    .GetProperty("activity").GetString() != "Ready")
                throw new InvalidOperationException("The startup regression must overlap real pending startup and admit exactly one Open only when Ready.");
            var evidence = Path.Combine(root, "startup-readiness.json");
            WriteExitHostReadiness(process, root, "unconfigured", "ready", null, evidence);
            var json = File.ReadAllText(evidence);
            using var saved = JsonDocument.Parse(json);
            if (!saved.RootElement.GetProperty("exited").GetBoolean() ||
                saved.RootElement.GetProperty("exitCode").GetInt32() != 0 ||
                saved.RootElement.GetProperty("host").ValueKind != JsonValueKind.Object || json.Contains(root, StringComparison.Ordinal))
                throw new InvalidOperationException("Readiness evidence must preserve actual process/stages without recording fixture paths.");
            VerifyExitedWorkspace(root, "unconfigured");
            Console.WriteLine("PASS: actual compiled product Loaded plus exit-host preparation reaches populated/selected readiness and owned process death without showing a window/native handle.");
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
    }
}
