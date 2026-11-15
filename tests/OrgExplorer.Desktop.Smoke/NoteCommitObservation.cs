using System.Diagnostics;

namespace OrgExplorer.Desktop.Smoke;

internal sealed class NoteCommitObservation : IDisposable
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource stopped = new();
    private readonly Task<double?> observed;

    private NoteCommitObservation(Func<bool> committed)
    {
        // Collection must not postpone observation while the caller performs other native interactions.
        observed = Task.Run(() =>
        {
            while (!stopped.IsCancellationRequested && elapsed.Elapsed < TimeSpan.FromSeconds(20))
            {
                var read = Stopwatch.StartNew();
                var saved = committed();
                ReadMs += read.Elapsed.TotalMilliseconds;
                ReadCount++;
                if (saved) return (double?)elapsed.Elapsed.TotalMilliseconds;
                Thread.Sleep(5);
            }
            if (stopped.IsCancellationRequested) return null;
            throw new TimeoutException("The requested note writing did not become observably durable within 20 seconds.");
        });
    }

    internal double RequestMs { get; private set; }
    internal double ElapsedMs => elapsed.Elapsed.TotalMilliseconds;
    internal int ReadCount { get; private set; }
    internal double ReadMs { get; private set; }

    internal static NoteCommitObservation Begin(Action request, Func<bool> committed)
    {
        var observation = new NoteCommitObservation(committed);
        var admitted = false;
        try
        {
            var native = Stopwatch.StartNew();
            request();
            observation.RequestMs = native.Elapsed.TotalMilliseconds;
            admitted = true;
            return observation;
        }
        finally { if (!admitted) observation.Dispose(); }
    }

    internal double Complete()
    {
        return observed.GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The durable-note observation was cancelled, not confirmed saved.");
    }

    public void Dispose()
    {
        stopped.Cancel();
        try { observed.GetAwaiter().GetResult(); }
        finally { stopped.Dispose(); }
    }
}
