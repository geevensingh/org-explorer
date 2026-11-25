using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Desktop.Smoke;

internal sealed class RevealLifecycleRecorder(Action<string> write, Action<Exception> reportFailure)
{
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly object gate = new();
    private readonly List<object> first = [];
    private readonly Queue<object> last = [];
    private long count;
    private object? pending;
    private Task? writer;
    private long outputFailures;
    private string? failureType;
    private int? failureHResult;

    internal void Record(object observation)
    {
        lock (gate)
        {
            var entry = new { sequence = ++count, observation };
            if (first.Count < 8) first.Add(entry);
            else
            {
                if (last.Count == 8) last.Dequeue();
                last.Enqueue(entry);
            }
        }
    }

    internal string Snapshot()
    {
        lock (gate) return JsonSerializer.Serialize(CreateSnapshot(), Json);
    }

    private object CreateSnapshot() => new
    {
        schemaVersion = 1, tag = "DEBUG-136-REVEAL-LIFECYCLE", diagnosticOnly = true,
        totalRecords = count, truncated = count > 16, records = first.Concat(last).ToArray(),
        outputFailures, failureType, failureHResult
    };

    internal void Publish()
    {
        lock (gate)
        {
            if (outputFailures != 0) return;
            pending = CreateSnapshot();
            writer ??= Task.Run(Drain);
        }
    }

    internal Task WhenIdle { get { lock (gate) return writer ?? Task.CompletedTask; } }

    private void Drain()
    {
        while (true)
        {
            object? snapshot;
            lock (gate)
            {
                snapshot = pending;
                pending = null;
                if (snapshot is null) { writer = null; return; }
            }
            try { write(JsonSerializer.Serialize(snapshot, Json)); }
            catch (Exception error)
            {
                RecordFailure(error);
                try { reportFailure(error); }
                catch (Exception reportingError) { RecordFailure(reportingError); }
                lock (gate) { pending = null; writer = null; }
                return;
            }
        }
    }

    private void RecordFailure(Exception error)
    {
        lock (gate)
        {
            outputFailures++;
            failureType = error.GetType().FullName;
            failureHResult = error.HResult;
        }
    }
}
