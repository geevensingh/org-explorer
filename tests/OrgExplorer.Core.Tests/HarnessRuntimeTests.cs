using System.Text.Json;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class HarnessRuntimeTests
{
    private const string Prefix = "DIAGNOSTIC: harness-runtime ";

    [Fact]
    public void Start_precedes_work_and_nested_failure_keeps_binding_context_and_original_exception()
    {
        var records = new List<string>();
        var failure = new InvalidOperationException("Private writing must not become diagnostic output");
        var caught = Assert.Throws<InvalidOperationException>(() => HarnessRuntime.Run("compiled-profile-refresh", () =>
        {
            Assert.Single(records);
            return HarnessRuntime.Run<int>("compiled-copy", () =>
            {
                Assert.Equal(2, records.Count);
                throw failure;
            });
        }, new("candidate", "run", "attempt"), records.Add));
        Assert.Same(failure, caught);
        Assert.Equal(4, records.Count);
        Assert.DoesNotContain(records, row => row.Contains(failure.Message, StringComparison.Ordinal));
        using var start = JsonDocument.Parse(records[1][Prefix.Length..]);
        using var end = JsonDocument.Parse(records[2][Prefix.Length..]);
        Assert.Equal("start", start.RootElement.GetProperty("boundary").GetString());
        Assert.Equal("Running", start.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("compiled-profile-refresh", end.RootElement.GetProperty("context").GetString());
        Assert.Equal("compiled-copy", end.RootElement.GetProperty("phase").GetString());
        Assert.Equal("candidate", end.RootElement.GetProperty("revision").GetString());
        Assert.Equal("run", end.RootElement.GetProperty("runId").GetString());
        Assert.Equal("attempt", end.RootElement.GetProperty("runAttempt").GetString());
        Assert.Equal("Failed", end.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Concurrent_observations_keep_fixed_storage_and_emit_only_at_phase_boundaries()
    {
        var records = new List<string>();
        Assert.Equal(17, HarnessRuntime.Run("native-scenario", () =>
        {
            Parallel.For(0, 10000, _ =>
            {
                using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.UiaDiscovery);
                Thread.SpinWait(25);
            });
            Assert.Single(records);
            return 17;
        }, new("candidate", "run", "attempt"), records.Add));
        Assert.Equal(2, records.Count);
        using var result = JsonDocument.Parse(records[1][Prefix.Length..]);
        var operations = result.RootElement.GetProperty("operations");
        Assert.Equal(Enum.GetValues<HarnessRuntime.Work>().Length, operations.GetArrayLength());
        var query = operations.EnumerateArray().Single(row => row.GetProperty("name").GetString() == "UiaDiscovery");
        Assert.Equal(10000, query.GetProperty("started").GetInt64());
        Assert.Equal(10000, query.GetProperty("completed").GetInt64());
        Assert.True(query.GetProperty("elapsedMs").GetDouble() >= query.GetProperty("maxMs").GetDouble());
        Assert.True(query.GetProperty("maxMs").GetDouble() > 0);
    }

    [Fact]
    public void Interruption_and_broken_diagnostic_output_do_not_change_actual_outcomes_or_leak_context()
    {
        var records = new List<string>();
        var interrupted = new OperationCanceledException("Unreported private context");
        Assert.Same(interrupted, Assert.Throws<OperationCanceledException>(() => HarnessRuntime.Run<int>(
            "compiled-sqlite", () =>
            {
                using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.UiaDiscovery);
                HarnessRuntime.Checkpoint("before-interruption");
                throw interrupted;
            }, new("candidate", "run", "attempt"), records.Add)));
        using var pending = JsonDocument.Parse(records[1][Prefix.Length..]);
        var active = pending.RootElement.GetProperty("operations").EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "UiaDiscovery");
        Assert.Equal("Running", pending.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, active.GetProperty("started").GetInt64());
        Assert.Equal(0, active.GetProperty("completed").GetInt64());
        using var result = JsonDocument.Parse(records[^1][Prefix.Length..]);
        Assert.Equal("Interrupted", result.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(42, HarnessRuntime.Run("successful-action", () => 42, write: _ => throw new IOException()));
        var failure = new InvalidOperationException("Original action failure");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => HarnessRuntime.Run<int>(
            "failed-action", () => throw failure, write: _ => throw new IOException())));
        records.Clear();
        HarnessRuntime.Run("next-scenario", () => true, new("next-candidate", "next-run", "1"), records.Add);
        using var next = JsonDocument.Parse(records[0][Prefix.Length..]);
        Assert.Equal("next-scenario", next.RootElement.GetProperty("context").GetString());
        Assert.Equal(JsonValueKind.Null, next.RootElement.GetProperty("parent").ValueKind);
        Assert.Equal("next-candidate", next.RootElement.GetProperty("revision").GetString());
    }
}
