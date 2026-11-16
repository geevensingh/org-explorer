using System.Text.Json;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class HarnessRuntimeTests
{
    private const string Prefix = "DIAGNOSTIC: harness-runtime ";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovered_output_reports_incomplete_evidence_instead_of_silently_losing_a_boundary(bool serializationFailure)
    {
        var records = new List<string>();
        var errors = new List<string>();
        var failures = new HarnessRuntime.FailureLedger();
        var writes = 0;
        Exception outputFailure = serializationFailure
            ? new JsonException("Private output context") : new IOException("Private output context");
        Assert.Equal(42, HarnessRuntime.Run("compiled-copy", () => 42, write: line =>
        {
            if (++writes == 1) throw outputFailure;
            records.Add(line);
        }, failures: failures, writeError: errors.Add));
        using var report = JsonDocument.Parse(Assert.Single(records)[Prefix.Length..]);
        Assert.True(report.RootElement.TryGetProperty("diagnosticsComplete", out var complete));
        Assert.False(complete.GetBoolean());
        var failure = report.RootElement.GetProperty("outputFailures");
        Assert.Equal(1, failure.GetProperty("count").GetInt64());
        Assert.Equal(outputFailure.GetType().FullName, failure.GetProperty("lastFailure").GetProperty("exceptionType").GetString());
        Assert.Equal(outputFailure.HResult, failure.GetProperty("lastFailure").GetProperty("hResult").GetInt32());
        Assert.DoesNotContain(outputFailure.Message, records[0], StringComparison.Ordinal);
        Assert.Equal("DIAGNOSTIC: harness-runtime-output-failure investigationIncomplete=true", Assert.Single(errors));
        Assert.Equal(1, failures.Snapshot.Count);
    }

    [Fact]
    public void Broken_stderr_still_retains_bounded_failure_evidence_and_preserves_return_and_original_exception()
    {
        var failures = new HarnessRuntime.FailureLedger();
        var output = new IOException("Private output context");
        var stderr = new ObjectDisposedException("Private stderr context");
        var writeAttempts = 0;
        var fallbackAttempts = 0;
        void Write(string _) { writeAttempts++; throw output; }
        void Fallback(string _) { fallbackAttempts++; throw stderr; }
        Assert.Equal(42, HarnessRuntime.Run("successful-action", () => 42, write: Write,
            failures: failures, writeError: Fallback));
        Assert.Equal(2, failures.Snapshot.Count);
        Assert.Equal(2, failures.Snapshot.FallbackCount);
        var original = new InvalidOperationException("Original private action failure");
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => HarnessRuntime.Run<int>(
            "failed-action", () => throw original, write: Write, failures: failures, writeError: Fallback)));
        var retained = failures.Snapshot;
        Assert.Equal(4, retained.Count);
        Assert.Equal(4, retained.FallbackCount);
        Assert.Equal(4, writeAttempts);
        Assert.Equal(4, fallbackAttempts);
        Assert.Equal(new(typeof(IOException).FullName!, output.HResult), retained.LastFailure);
        Assert.Equal(new(typeof(ObjectDisposedException).FullName!, stderr.HResult), retained.LastFallbackFailure);
        var serialized = JsonSerializer.Serialize(retained);
        Assert.DoesNotContain("Private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Message, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_writer_errors_surface_without_retrying_or_leaking_phase_context()
    {
        var attempts = 0;
        var unexpected = new ArgumentException("Writer programming error");
        Assert.Same(unexpected, Assert.Throws<ArgumentException>(() => HarnessRuntime.Run<int>("before-action",
            () => throw new InvalidOperationException("Action must not run"),
            write: _ => { attempts++; throw unexpected; })));
        Assert.Equal(1, attempts);
        var records = new List<string>();
        HarnessRuntime.Run("next-action", () => true, write: records.Add);
        using var next = JsonDocument.Parse(records[0][Prefix.Length..]);
        Assert.Equal(JsonValueKind.Null, next.RootElement.GetProperty("parent").ValueKind);
    }

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
        Assert.True(result.RootElement.GetProperty("diagnosticsComplete").GetBoolean());
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
        var failures = new HarnessRuntime.FailureLedger();
        Assert.Equal(42, HarnessRuntime.Run("successful-action", () => 42, write: _ => throw new IOException(),
            failures: failures, writeError: _ => { }));
        var failure = new InvalidOperationException("Original action failure");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => HarnessRuntime.Run<int>(
            "failed-action", () => throw failure, write: _ => throw new IOException(),
            failures: failures, writeError: _ => { })));
        records.Clear();
        HarnessRuntime.Run("next-scenario", () => true, new("next-candidate", "next-run", "1"), records.Add);
        using var next = JsonDocument.Parse(records[0][Prefix.Length..]);
        Assert.Equal("next-scenario", next.RootElement.GetProperty("context").GetString());
        Assert.Equal(JsonValueKind.Null, next.RootElement.GetProperty("parent").ValueKind);
        Assert.Equal("next-candidate", next.RootElement.GetProperty("revision").GetString());
    }
}
