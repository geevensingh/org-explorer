using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace OrgExplorer.Desktop.Smoke;

internal static class HarnessRuntime
{
    internal enum Work
    {
        UiaDiscovery, NativeInvoke, NativeSetValue, NativeWait, Observation,
        DispatcherWait, FeedbackExpiry, Capture, Pixels, CompiledLayout, NoteSnapshot
    }

    internal sealed record Binding(string? Revision, string? RunId, string? RunAttempt);
    private static readonly AsyncLocal<Phase?> Current = new();
    private static readonly FailureLedger ProcessFailures = new();
    internal static FailureSnapshot OutputFailures => ProcessFailures.Snapshot;

    internal sealed record OutputFailure(
        [property: JsonPropertyName("exceptionType")] string ExceptionType,
        [property: JsonPropertyName("hResult")] int HResult);
    internal sealed record FailureSnapshot(long Count, OutputFailure? LastFailure,
        long FallbackCount, OutputFailure? LastFallbackFailure);

    internal sealed class FailureLedger
    {
        private readonly object gate = new();
        private FailureSnapshot snapshot = new(0, null, 0, null);
        internal FailureSnapshot Snapshot => Volatile.Read(ref snapshot);

        internal void Record(Exception error, bool fallback = false)
        {
            var failure = new OutputFailure(error.GetType().FullName!, error.HResult);
            lock (gate)
                Volatile.Write(ref snapshot, fallback
                    ? snapshot with { FallbackCount = snapshot.FallbackCount + 1, LastFallbackFailure = failure }
                    : snapshot with { Count = snapshot.Count + 1, LastFailure = failure });
        }
    }

    private static bool IsOutputFailure(Exception error) =>
        error is IOException or ObjectDisposedException or UnauthorizedAccessException or NotSupportedException;

    internal static void Run(string phase, Action action) =>
        Run(phase, () => { action(); return true; });

    internal static T Run<T>(string phase, Func<T> action, Binding? binding = null, Action<string>? write = null,
        FailureLedger? failures = null, Action<string>? writeError = null)
    {
        var parent = Current.Value;
        var observation = new Phase(phase, parent?.Context ?? phase, parent?.Name,
            binding ?? parent?.Binding ?? new(Environment.GetEnvironmentVariable("GITHUB_SHA"),
                Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")),
            write ?? parent?.Write ?? Console.WriteLine, failures ?? parent?.Failures ?? ProcessFailures,
            writeError ?? parent?.WriteError ?? Console.Error.WriteLine);
        Current.Value = observation;
        var started = false;
        var outcome = "Interrupted";
        try
        {
            observation.Emit("start", "Running");
            started = true;
            var result = action();
            outcome = "Passed";
            return result;
        }
        catch (Exception error)
        {
            outcome = error is OperationCanceledException ? "Interrupted" : "Failed";
            throw;
        }
        finally
        {
            try { if (started) observation.Emit("end", outcome); }
            finally { Current.Value = parent; }
        }
    }

    internal static Measurement Measure(Work work) => new(Current.Value, work);
    internal static void Checkpoint(string stage) => Current.Value?.Emit("checkpoint", "Running", stage);

    internal readonly struct Measurement : IDisposable
    {
        private readonly Phase? phase;
        private readonly Work work;
        private readonly long started;

        internal Measurement(Phase? phase, Work work)
        {
            this.phase = phase;
            this.work = work;
            started = Stopwatch.GetTimestamp();
            if (phase is not null) Interlocked.Increment(ref phase.Started[(int)work]);
        }

        public void Dispose()
        {
            if (phase is null) return;
            var ticks = Stopwatch.GetTimestamp() - started;
            var index = (int)work;
            Interlocked.Add(ref phase.Elapsed[index], ticks);
            long maximum;
            do { maximum = Interlocked.Read(ref phase.Maximum[index]); }
            while (ticks > maximum && Interlocked.CompareExchange(ref phase.Maximum[index], ticks, maximum) != maximum);
            Interlocked.Increment(ref phase.Completed[index]);
        }
    }

    internal sealed class Phase(string name, string context, string? parent, Binding binding, Action<string> write,
        FailureLedger failures, Action<string> writeError)
    {
        internal string Name { get; } = name;
        internal string Context { get; } = context;
        internal Binding Binding { get; } = binding;
        internal Action<string> Write { get; } = write;
        internal FailureLedger Failures { get; } = failures;
        internal Action<string> WriteError { get; } = writeError;
        private readonly long started = Stopwatch.GetTimestamp();
        internal readonly long[] Started = new long[Enum.GetValues<Work>().Length];
        internal readonly long[] Completed = new long[Enum.GetValues<Work>().Length];
        internal readonly long[] Elapsed = new long[Enum.GetValues<Work>().Length];
        internal readonly long[] Maximum = new long[Enum.GetValues<Work>().Length];

        internal void Emit(string boundary, string outcome, string? stage = null)
        {
            // Diagnostic output must never replace the observed result or exception.
            try
            {
                var failure = Failures.Snapshot;
                Write("DIAGNOSTIC: harness-runtime " + JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, revision = Binding.Revision, runId = Binding.RunId, runAttempt = Binding.RunAttempt,
                    context = Context, phase = Name, parent, boundary, outcome, stage,
                    diagnosticsComplete = failure.Count == 0,
                    outputFailures = new { count = failure.Count, lastFailure = failure.LastFailure,
                        fallbackCount = failure.FallbackCount, lastFallbackFailure = failure.LastFallbackFailure },
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    interpretation = "Harness observation cost, not database commit or product latency. Nested/inclusive times are not additive; concurrent snapshots are non-atomic. No time is deducted from acceptance.",
                    operations = Enum.GetValues<Work>().Select(work =>
                    {
                        var index = (int)work;
                        return new
                        {
                            name = work.ToString(), started = Interlocked.Read(ref Started[index]),
                            completed = Interlocked.Read(ref Completed[index]),
                            elapsedMs = Interlocked.Read(ref Elapsed[index]) * 1000d / Stopwatch.Frequency,
                            maxMs = Interlocked.Read(ref Maximum[index]) * 1000d / Stopwatch.Frequency
                        };
                    }).ToArray()
                }));
            }
            catch (Exception error) when (IsOutputFailure(error) || error is JsonException)
            {
                Failures.Record(error);
                try { WriteError("DIAGNOSTIC: harness-runtime-output-failure investigationIncomplete=true"); }
                catch (Exception fallback) when (IsOutputFailure(fallback))
                {
                    Failures.Record(fallback, fallback: true);
                }
            }
        }
    }
}
