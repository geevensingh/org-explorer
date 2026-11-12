using System.Diagnostics;

namespace OrgExplorer.Workflow;

internal static class Storage47Probe
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("ORG_EXPLORER_STORAGE_PROFILE")
        ?? Environment.GetEnvironmentVariable("SCALE_TIMING_FILE");
    private static readonly AsyncLocal<Phase?> Current = new();
    private static readonly object Gate = new();
    internal static Phase? Scope(string name) => Path is null ? null : new(name);
    internal static void Read() { for (var phase = Current.Value; phase is not null; phase = phase.Parent) Interlocked.Increment(ref phase.Reads); }
    internal static void Link() { for (var phase = Current.Value; phase is not null; phase = phase.Parent) Interlocked.Increment(ref phase.Links); }
    internal static void Write()
    {
        for (var phase = Current.Value; phase is not null; phase = phase.Parent)
            if (Interlocked.Increment(ref phase.Writes) % 20000 == 0) phase.Progress();
    }
    internal static void Publish() { for (var phase = Current.Value; phase is not null; phase = phase.Parent) Interlocked.Increment(ref phase.Publications); }
    private static void Log(string message) { lock (Gate) File.AppendAllText(Path!, "[DEBUG-storage47] " + message + Environment.NewLine); }
    internal sealed class Phase : IDisposable
    {
        internal readonly Phase? Parent;
        internal long Reads, Links, Writes, Publications;
        private readonly string name;
        private readonly Stopwatch timer = Stopwatch.StartNew();
        internal Phase(string name)
        {
            this.name = name;
            Parent = Current.Value;
            Current.Value = this;
            Log("START " + name);
        }
        public void Dispose()
        {
            Progress();
            Current.Value = Parent;
        }
        internal void Progress() =>
            Log($"{name}: {timer.Elapsed.TotalSeconds:F3}s reads={Reads} links={Links} writes={Writes} publications={Publications}");
    }
}
