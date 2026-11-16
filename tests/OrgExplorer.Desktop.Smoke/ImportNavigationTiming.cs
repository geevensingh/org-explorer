using System.Text.Json.Serialization;

namespace OrgExplorer.Desktop.Smoke;

internal sealed class ImportNavigationTiming(string request, string destination, Func<double> elapsed)
{
    internal enum Field { Email, Heading }
    internal sealed record QueryTiming(
        [property: JsonPropertyName("started")] int Started,
        [property: JsonPropertyName("completed")] int Completed,
        [property: JsonPropertyName("elapsedMs")] double ElapsedMs,
        [property: JsonPropertyName("maxMs")] double MaxMs);
    internal sealed record Sample(
        [property: JsonPropertyName("request")] string Request,
        [property: JsonPropertyName("destination")] string Destination,
        [property: JsonPropertyName("requestStartedMs")] double? RequestStartedMs,
        [property: JsonPropertyName("requestReturnedMs")] double? RequestReturnedMs,
        [property: JsonPropertyName("totalMs")] double TotalMs,
        [property: JsonPropertyName("polls")] int Polls,
        [property: JsonPropertyName("ready")] bool Ready,
        [property: JsonPropertyName("email")] QueryTiming Email,
        [property: JsonPropertyName("heading")] QueryTiming Heading,
        [property: JsonPropertyName("exceptionType")] string? ExceptionType,
        [property: JsonPropertyName("hResult")] int? HResult);

    private readonly int[] started = new int[2], completed = new int[2];
    private readonly double[] queryMs = new double[2], maximumMs = new double[2];
    private double? requestStartedMs, requestReturnedMs, totalMs;
    private double lastElapsedMs;
    private int polls;
    private bool ready;
    private string? exceptionType;
    private int? hResult;

    internal void Request(Action action)
    {
        requestStartedMs = elapsed();
        try { action(); requestReturnedMs = elapsed(); }
        catch (Exception error) { RecordFailure(error); throw; }
        finally { lastElapsedMs = elapsed(); }
    }

    internal bool Observe(Func<bool> observation)
    {
        polls++;
        try { return ready = observation(); }
        catch (Exception error) { RecordFailure(error); throw; }
        finally { lastElapsedMs = elapsed(); }
    }

    internal T Query<T>(Field field, Func<T> query)
    {
        var index = (int)field;
        var begin = elapsed();
        started[index]++;
        try
        {
            var result = query();
            completed[index]++;
            return result;
        }
        catch (Exception error) { RecordFailure(error); throw; }
        finally
        {
            lastElapsedMs = elapsed();
            var duration = lastElapsedMs - begin;
            queryMs[index] += duration;
            maximumMs[index] = Math.Max(maximumMs[index], duration);
        }
    }

    internal void Complete(double originalTotalMs) => totalMs = originalTotalMs;
    internal Sample Snapshot() => new(request, destination, requestStartedMs, requestReturnedMs,
        totalMs ?? lastElapsedMs, polls, ready, QuerySnapshot(0), QuerySnapshot(1), exceptionType, hResult);

    private QueryTiming QuerySnapshot(int index) =>
        new(started[index], completed[index], queryMs[index], maximumMs[index]);

    private void RecordFailure(Exception error)
    {
        exceptionType = error.GetType().FullName;
        hResult = error.HResult;
    }
}
