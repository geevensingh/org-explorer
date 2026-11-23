using System.Diagnostics;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static bool highResolutionTimingProbe;
    private static readonly object timingProbeGate = new();
    private static readonly Dictionary<string, (int Count, double Milliseconds, double Maximum)> timingProbeTotals = [];
    private static long timingProbeStarted;

    private static IDisposable? ObserveUiTiming(string phase) =>
        highResolutionTimingProbe ? new UiTimingObservation(phase) : null;

    private static void StartUiTiming(string scenario)
    {
        if (!highResolutionTimingProbe) return;
        lock (timingProbeGate)
        {
            timingProbeTotals.Clear();
            timingProbeStarted = Stopwatch.GetTimestamp();
        }
        WriteUiTiming("start-" + scenario);
    }

    private static void WriteUiTiming(string checkpoint)
    {
        if (!highResolutionTimingProbe) return;
        string totals;
        lock (timingProbeGate)
            totals = string.Join("; ", timingProbeTotals.OrderBy(pair => pair.Key).Select(pair =>
                FormattableString.Invariant(
                    $"{pair.Key}: calls={pair.Value.Count}, totalMs={pair.Value.Milliseconds:F1}, maxMs={pair.Value.Maximum:F1}")));
        Console.WriteLine(FormattableString.Invariant(
            $"[DEBUG-pr120-timing] utc={DateTimeOffset.UtcNow:O}; checkpoint={checkpoint}; elapsedMs={Stopwatch.GetElapsedTime(timingProbeStarted).TotalMilliseconds:F1}; inclusiveTimings=[{totals}]"));
    }

    private sealed class UiTimingObservation(string phase) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (timingProbeGate)
            {
                var previous = timingProbeTotals.GetValueOrDefault(phase);
                timingProbeTotals[phase] = (previous.Count + 1, previous.Milliseconds + milliseconds,
                    Math.Max(previous.Maximum, milliseconds));
            }
        }
    }
}
