using System.Text.Json;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class RevealLifecycleRecorderTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Bounded_lifecycle_snapshot_retains_the_first_and_last_eight_records()
    {
        var recorder = new RevealLifecycleRecorder(_ => { }, _ => { });
        for (var index = 1; index <= 40; index++) recorder.Record(new { index });
        using var snapshot = JsonDocument.Parse(recorder.Snapshot());
        Assert.True(snapshot.RootElement.GetProperty("diagnosticOnly").GetBoolean());
        Assert.Equal(40, snapshot.RootElement.GetProperty("totalRecords").GetInt64());
        Assert.True(snapshot.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 33, 34, 35, 36, 37, 38, 39, 40 },
            snapshot.RootElement.GetProperty("records").EnumerateArray().Select(record => record.GetProperty("sequence").GetInt64()));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public async Task Lifecycle_output_runs_off_the_caller_and_coalesces_pending_snapshots_without_concurrent_writes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writes = new List<string>();
        var recorder = new RevealLifecycleRecorder(serialized =>
        {
            if (writes.Count == 0)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test writer was not released.");
            }
            writes.Add(serialized);
        }, _ => { });
        try
        {
            recorder.Record(new { stage = "first" });
            recorder.Publish();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (var index = 2; index <= 40; index++)
            {
                recorder.Record(new { index });
                recorder.Publish();
            }
        }
        finally { release.Set(); }
        await recorder.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, writes.Count);
        using var final = JsonDocument.Parse(writes[^1]);
        Assert.Equal(40, final.RootElement.GetProperty("totalRecords").GetInt64());
        Assert.Equal(16, final.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    [Trait("Category", "Runner")]
    public async Task Lifecycle_output_failure_is_visible_bounded_and_never_retried()
    {
        var attempts = 0;
        var failures = new List<Exception>();
        var original = new IOException("Private output context must not enter the snapshot.");
        var recorder = new RevealLifecycleRecorder(_ => { attempts++; throw original; }, failures.Add);
        recorder.Record(new { stage = "bring-call", height = double.NaN });
        recorder.Publish();
        await recorder.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        recorder.Publish();
        await recorder.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempts);
        Assert.Same(original, Assert.Single(failures));
        using var snapshot = JsonDocument.Parse(recorder.Snapshot());
        Assert.Equal(1, snapshot.RootElement.GetProperty("outputFailures").GetInt64());
        Assert.Equal(typeof(IOException).FullName, snapshot.RootElement.GetProperty("failureType").GetString());
        Assert.Equal(original.HResult, snapshot.RootElement.GetProperty("failureHResult").GetInt32());
        Assert.DoesNotContain(original.Message, recorder.Snapshot(), StringComparison.Ordinal);
    }
}
