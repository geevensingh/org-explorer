using System.Diagnostics;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal sealed class NoteCommitObservation
{
    internal const int ContinuedEditCount = 20;
    internal const int ContinuedEditSpacingMs = 60;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource stopped = new();
    private readonly Task<double?> observed;

    private NoteCommitObservation(Func<bool> committed)
    {
        // Collection must not postpone observation while the caller performs other native interactions.
        observed = Task.Run(() =>
        {
            WorkerAdmittedMs = elapsed.Elapsed.TotalMilliseconds;
            while (!stopped.IsCancellationRequested && elapsed.Elapsed < TimeSpan.FromSeconds(20))
            {
                var readStarted = elapsed.Elapsed.TotalMilliseconds;
                FirstReadStartedMs ??= readStarted;
                var read = Stopwatch.StartNew();
                var saved = committed();
                ReadMs += read.Elapsed.TotalMilliseconds;
                ReadCount++;
                if (saved)
                {
                    SuccessfulReadStartedMs = readStarted;
                    return (double?)elapsed.Elapsed.TotalMilliseconds;
                }
                Thread.Sleep(5);
            }
            if (stopped.IsCancellationRequested) return null;
            throw new TimeoutException("The requested note writing did not become observably durable within 20 seconds. " +
                $"Observer admitted at {WorkerAdmittedMs:F1} ms; first read at {FirstReadStartedMs:F1} ms; " +
                $"reads={ReadCount}; cumulative read time={ReadMs:F1} ms.");
        });
    }

    internal double RequestMs { get; private set; }
    internal double? WorkerAdmittedMs { get; private set; }
    internal double? FirstReadStartedMs { get; private set; }
    internal double? SuccessfulReadStartedMs { get; private set; }
    internal Task<double?> Completion => observed;
    internal double ElapsedMs => elapsed.Elapsed.TotalMilliseconds;
    internal int ReadCount { get; private set; }
    internal double ReadMs { get; private set; }

    internal (double[] RequestStartedMs, double[] RequestCompletedMs) ContinueTyping(Action<int> edit,
        int editCount = ContinuedEditCount, int spacingMs = ContinuedEditSpacingMs)
    {
        var started = new double[editCount];
        var completed = new double[started.Length];
        for (var index = 0; index < started.Length; index++)
        {
            if (index != 0) Thread.Sleep(spacingMs);
            started[index] = elapsed.Elapsed.TotalMilliseconds;
            PassiveSaveCapture.EmitSample(PassiveSaveStage.ContinuedInputBegin, index);
            edit(index);
            completed[index] = elapsed.Elapsed.TotalMilliseconds;
            PassiveSaveCapture.EmitSample(PassiveSaveStage.ContinuedInputEnd, index);
        }
        return (started, completed);
    }

    internal static void Run(Action request, Func<bool> committed, Action<NoteCommitObservation> body)
    {
        var observation = new NoteCommitObservation(committed);
        Exception? primary = null;
        try
        {
            observation.Request(request);
            body(observation);
        }
        catch (Exception error) { primary = error; throw; }
        finally { observation.Stop(primary); }
    }

    internal static async Task RunAsync(Action request, Func<bool> committed, Func<NoteCommitObservation, Task> body)
    {
        var observation = new NoteCommitObservation(committed);
        Exception? primary = null;
        try
        {
            observation.Request(request);
            await body(observation).ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; throw; }
        finally { observation.Stop(primary); }
    }

    private void Request(Action request)
    {
        var native = Stopwatch.StartNew();
        PassiveSaveCapture.EmitSample(PassiveSaveStage.RequestBegin);
        request();
        RequestMs = native.Elapsed.TotalMilliseconds;
        PassiveSaveCapture.EmitSample(PassiveSaveStage.RequestEnd, inputReturned: true);
    }

    internal double Complete()
    {
        return observed.GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The durable-note observation was cancelled, not confirmed saved.");
    }

    internal void Cancel() => stopped.Cancel();

    private void Stop(Exception? primary)
    {
        var complete = false;
        stopped.Cancel();
        try
        {
            if (observed.GetAwaiter().GetResult() is null && primary is null)
                throw new InvalidOperationException("The durable-note observation was cancelled, not confirmed saved.");
            complete = primary is null;
        }
        catch (Exception secondary) when (primary is not null)
        {
            if (!ReferenceEquals(primary, secondary))
            {
                primary.Data["NoteCommitObservationSecondaryFailure"] = secondary;
                try { Console.Error.WriteLine("NOTE-COMMIT-OBSERVATION-SECONDARY " + secondary); }
                catch (Exception reporting) { primary.Data["NoteCommitObservationReportingFailure"] = reporting; }
            }
        }
        finally
        {
            PassiveSaveCapture.EmitSample(PassiveSaveStage.HarnessSampleClosed,
                outcome: complete ? PassiveSaveOutcome.Completed : PassiveSaveOutcome.Failed);
            stopped.Dispose();
        }
    }
}
