using System.IO.Compression;
using System.Text.Json;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

[Trait("Category", "Runner")]
public sealed class InitialsRejectionEvidenceTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, ".test-workspaces",
        "initials-diagnostics-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Timeout_emits_only_the_last_original_rejected_bitmap_without_retesting_or_replacing_the_error()
    {
        Directory.CreateDirectory(root);
        var evidence = new InitialsRejectionEvidence();
        var calls = 0;
        var original = new TimeoutException("Original20-second photo observation");
        byte[] first = [1, 2, 3, 0, 4, 5, 6, 0];
        byte[] last = [10, 20, 30, 0, 40, 50, 60, 0];
        var caught = Assert.Throws<TimeoutException>(() => evidence.Observe(() =>
        {
            calls++;
            var a = new InitialsObservation { Description = "first", Scale = 1 };
            a.ProbeStage = "anchor-left";
            a.Probe(-68, 0, 0x794b21, 0xffffff, false);
            evidence.Reject(a, () => new(455, 235, 2, 1, 8, first, 11, 12, DateTimeOffset.UnixEpoch));
            var b = new InitialsObservation { Description = "last", Scale = 1 };
            b.Check("circle-horizontal-span", false);
            b.CircleEdges = [70, 70, 72, 71];
            evidence.Reject(b, () => new(503, 273, 2, 1, 8, last, 21, 22, DateTimeOffset.UnixEpoch.AddSeconds(1)));
            b.Description = "Changed after rejection";
            b.CircleEdges[0] = 0;
            last[0] = 99;
            throw original;
        }, Path.Combine(root, "rejected"), () => new { revision = "candidate", runId = "run", attempt = "1", pid = 42 }));
        Assert.Same(original, caught);
        Assert.Equal(1, calls);
        var png = File.ReadAllBytes(Path.Combine(root, "rejected.png"));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var idat = FindChunk(png, "IDAT");
        using var compressed = new MemoryStream(idat);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var pixels = new MemoryStream();
        inflater.CopyTo(pixels);
        Assert.Equal(new byte[] { 0, 30, 20, 10, 60, 50, 40 }, pixels.ToArray());
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "rejected.json")));
        Assert.Equal("last", json.RootElement.GetProperty("frame").GetProperty("observation").GetProperty("description").GetString());
        Assert.Equal(21, json.RootElement.GetProperty("frame").GetProperty("bitmap").GetProperty("acquisitionStartedAt").GetInt64());
        Assert.Equal(22, json.RootElement.GetProperty("frame").GetProperty("bitmap").GetProperty("acquisitionCompletedAt").GetInt64());
        Assert.Equal("circle-horizontal-span", json.RootElement.GetProperty("frame").GetProperty("observation").GetProperty("firstFalseStage").GetString());
        Assert.Equal(70, json.RootElement.GetProperty("frame").GetProperty("observation").GetProperty("circleEdges")[0].GetInt32());
    }

    [Fact]
    public void Successful_observation_emits_nothing_even_if_an_earlier_frame_was_rejected()
    {
        Directory.CreateDirectory(root);
        var evidence = new InitialsRejectionEvidence();
        evidence.Observe(() => evidence.Reject(new(), () => new(0, 0, 1, 1, 4,
            [1, 2, 3, 0], 1, 2, DateTimeOffset.UnixEpoch)),
            Path.Combine(root, "successful"), () => throw new InvalidOperationException("Success must not read output binding."));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Output_and_stderr_faults_preserve_the_original_timeout_and_report_incomplete_evidence(bool blockJson)
    {
        Directory.CreateDirectory(root);
        var prefix = Path.Combine(root, "refused-output");
        Directory.CreateDirectory(prefix + ".png");
        if (blockJson) Directory.CreateDirectory(prefix + ".json");
        var evidence = new InitialsRejectionEvidence();
        var original = new TimeoutException("Original observation failed");
        Assert.Same(original, Assert.Throws<TimeoutException>(() => evidence.Observe(() =>
        {
            evidence.Reject(new(), () => new(0, 0, 1, 1, 4, [1, 2, 3, 0], 1, 2, DateTimeOffset.UnixEpoch));
            throw original;
        }, prefix, () => new { revision = "source" }, _ => throw new IOException("Broken supplemental stderr"))));
        var failures = Assert.IsType<HarnessRuntime.FailureSnapshot>(original.Data["InitialsDiagnosticOutputFailures"]);
        Assert.Equal(blockJson ? 2 : 1, failures.Count);
        Assert.Equal(1, failures.FallbackCount);
        if (!blockJson)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(prefix + ".json"));
            Assert.Equal(1, json.RootElement.GetProperty("outputFailures").GetProperty("count").GetInt64());
        }
    }

    [Fact]
    public void Pre_pixel_refusal_and_unavailable_bitmap_never_invent_a_replacement_image()
    {
        Directory.CreateDirectory(root);
        foreach (var captureFailure in new[] { false, true })
        {
            var evidence = new InitialsRejectionEvidence();
            var observation = new InitialsObservation { Description = "Original unavailable viewport" };
            observation.Check("viewport", false);
            var original = new TimeoutException("Original native condition did not settle");
            var prefix = Path.Combine(root, captureFailure ? "copy-fault" : "pre-pixel");
            Assert.Same(original, Assert.Throws<TimeoutException>(() => evidence.Observe(() =>
            {
                evidence.Reject(observation, captureFailure ? () => throw new IOException("Copy of original bitmap failed") : null);
                throw original;
            }, prefix, () => new { runId = "same-run" }, _ => { })));
            Assert.False(File.Exists(prefix + ".png"));
            using var json = JsonDocument.Parse(File.ReadAllText(prefix + ".json"));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("frame").ValueKind);
            Assert.Equal("viewport", json.RootElement.GetProperty("latestObservation").GetProperty("firstFalseStage").GetString());
            Assert.Equal(captureFailure ? 1 : 0, json.RootElement.GetProperty("outputFailures").GetProperty("count").GetInt64());
        }
    }

    [Fact]
    public void Rejected_probe_values_are_retained_but_ordinary_glyph_misses_are_not_a_false_stage()
    {
        var observation = new InitialsObservation { ProbeStage = "glyph-ink" };
        observation.Probe(0, 0, 0xffffff, 0x794b21, false, 10, 20);
        Assert.Null(observation.FirstFalseStage);
        Assert.Empty(observation.Probes);
        observation.ProbeStage = "anchor-background-left";
        observation.Probe(-68, 0, 0x794b21, 0xffffff, false, 14, 82);
        var probe = Assert.Single(observation.Probes);
        Assert.Same(probe, observation.FirstFalseProbe);
        Assert.Equal("214B79", probe.ExpectedRgb);
        Assert.Equal("FFFFFF", probe.ActualRgb);
        Assert.Equal(14, probe.CaptureX);
        Assert.Equal(82, probe.CaptureY);
    }

    [Fact]
    public void Invalid_bitmap_copy_is_a_diagnostic_refusal_not_a_new_verdict()
    {
        Directory.CreateDirectory(root);
        var evidence = new InitialsRejectionEvidence();
        var original = new TimeoutException("Original condition exceeded its unchanged deadline");
        var prefix = Path.Combine(root, "invalid-copy");
        Assert.Same(original, Assert.Throws<TimeoutException>(() => evidence.Observe(() =>
        {
            evidence.Reject(new(), () => new(0, 0, 2, 1, 8, [1], 1, 2, DateTimeOffset.UnixEpoch));
            throw original;
        }, prefix, () => new { source = "same candidate" }, _ => { })));
        Assert.False(File.Exists(prefix + ".png"));
        using var json = JsonDocument.Parse(File.ReadAllText(prefix + ".json"));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("frame").ValueKind);
        Assert.Equal(1, json.RootElement.GetProperty("outputFailures").GetProperty("count").GetInt64());
    }

    [Fact]
    public void Non_timeout_primary_failure_is_not_retested_or_replaced_by_timeout_diagnostics()
    {
        Directory.CreateDirectory(root);
        var evidence = new InitialsRejectionEvidence();
        var original = new InvalidOperationException("Original pixel availability failure");
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => evidence.Observe(() => throw original,
            Path.Combine(root, "immediate"), () => throw new InvalidOperationException("No later reads allowed."))));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void Empty_original_bounds_remain_explicit_in_pre_pixel_refusal_metadata()
    {
        Directory.CreateDirectory(root);
        var evidence = new InitialsRejectionEvidence();
        var original = new TimeoutException("Original empty bounds refused");
        var prefix = Path.Combine(root, "empty-bounds");
        Assert.Same(original, Assert.Throws<TimeoutException>(() => evidence.Observe(() =>
        {
            var observation = new InitialsObservation
            {
                Text = new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity)
            };
            observation.Check("text-center", false);
            evidence.Reject(observation);
            throw original;
        }, prefix, () => new { source = "same" }, _ => { })));
        using var json = JsonDocument.Parse(File.ReadAllText(prefix + ".json"));
        Assert.Equal("Infinity", json.RootElement.GetProperty("latestObservation").GetProperty("text").GetProperty("left").GetString());
        Assert.Equal("text-center", json.RootElement.GetProperty("latestObservation").GetProperty("firstFalseStage").GetString());
        Assert.False(File.Exists(prefix + ".png"));
    }

    private static byte[] FindChunk(byte[] png, string expected)
    {
        for (var offset = 8; offset < png.Length;)
        {
            var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (System.Text.Encoding.ASCII.GetString(png, offset + 4, 4) == expected)
                return png.AsSpan(offset + 8, count).ToArray();
            offset += count + 12;
        }
        throw new InvalidOperationException("Expected captured PNG chunk is missing.");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
