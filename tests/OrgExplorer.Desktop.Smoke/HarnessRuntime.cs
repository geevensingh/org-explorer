using System.Diagnostics;
using System.Text.Json;
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

    internal static void Run(string phase, Action action) =>
        Run(phase, () => { action(); return true; });

    internal static T Run<T>(string phase, Func<T> action, Binding? binding = null, Action<string>? write = null)
    {
        var parent = Current.Value;
        var observation = new Phase(phase, parent?.Context ?? phase, parent?.Name,
            binding ?? parent?.Binding ?? new(Environment.GetEnvironmentVariable("GITHUB_SHA"),
                Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")),
            write ?? parent?.Write ?? Console.WriteLine);
        Current.Value = observation;
        observation.Emit("start", "Running");
        var outcome = "Interrupted";
        try
        {
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
            observation.Emit("end", outcome);
            Current.Value = parent;
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

    internal sealed class Phase(string name, string context, string? parent, Binding binding, Action<string> write)
    {
        internal string Name { get; } = name;
        internal string Context { get; } = context;
        internal Binding Binding { get; } = binding;
        internal Action<string> Write { get; } = write;
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
                Write("DIAGNOSTIC: harness-runtime " + JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, revision = Binding.Revision, runId = Binding.RunId, runAttempt = Binding.RunAttempt,
                    context = Context, phase = Name, parent, boundary, outcome, stage,
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
            catch (Exception) { }
        }
    }
}
