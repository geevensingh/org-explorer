using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace OrgExplorer.Desktop.Smoke;

internal sealed record AutomaticHostFailure(string Stage, Exception Error);

internal static class AutomaticHostLifecycle
{
    internal static void Run(Process process, Action body, Action<Exception?> writeObservation,
        Action release, Action<Exception?> writeCleanupDiagnostic, Action<string> report, Action<string> fallback)
    {
        Exception? primary = null;
        var failures = new List<AutomaticHostFailure>();
        var pid = process.Id;
        void Attempt(string stage, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(new(stage, error)); }
        }

        try { body(); }
        catch (Exception error) { primary = error; }
        Attempt("observation", () => writeObservation(primary));
        Attempt("release", release);
        var alive = true;
        Attempt("process-state", () => alive = !process.HasExited);
        if (alive)
        {
            if (primary is null)
                failures.Add(new("unexpected-live-host", new InvalidOperationException("Automatic host returned without normal process exit.")));
            Attempt("cleanup-diagnostic", () => writeCleanupDiagnostic(primary));
            Attempt("terminate", () => process.Kill(true));
            Attempt("termination-wait", () =>
            {
                if (!process.WaitForExit(5000))
                    throw new TimeoutException($"Owned automatic host pid={pid} did not terminate within the existing cleanup wait.");
            });
        }

        string Describe(AutomaticHostFailure failure) =>
            $"AUTOMATIC-HOST-SECONDARY pid={pid} stage={failure.Stage} primary={primary?.ToString() ?? "<none>"}\n{failure.Error}";
        var reportingFailed = false;
        foreach (var failure in failures.ToArray())
            try { report(Describe(failure)); }
            catch (Exception error)
            {
                failures.Add(new("secondary-report", error));
                reportingFailed = true;
            }
        if (reportingFailed)
            Attempt("fallback-report", () => fallback(string.Join("\n", failures.Select(Describe))));

        if (primary is null && failures.Count == 0) return;
        var final = primary ?? (failures.Count == 1 ? failures[0].Error :
            new AggregateException("Automatic host output/cleanup failed.", failures.Select(failure => failure.Error)));
        final.Data["AutomaticHostSecondaryFailures"] = failures.ToArray();
        ExceptionDispatchInfo.Capture(final).Throw();
    }
}
