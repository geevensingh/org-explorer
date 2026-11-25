using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Desktop.Smoke;

internal sealed record InitialsBounds(double Left, double Top, double Width, double Height);
internal sealed record InitialsProbe(string Stage, double X, double Y, uint Expected, uint Actual, bool Matched,
    int? CaptureX, int? CaptureY)
{
    public string ExpectedRgb => $"{Expected & 255:X2}{(Expected >> 8) & 255:X2}{(Expected >> 16) & 255:X2}";
    public string ActualRgb => $"{Actual & 255:X2}{(Actual >> 8) & 255:X2}{(Actual >> 16) & 255:X2}";
}
internal sealed record InitialsInk(double Left, double Top, double Right, double Bottom, int Samples);

internal sealed record InitialsObservation
{
    public long StartedAt { get; } = Stopwatch.GetTimestamp();
    public string? Description { get; set; }
    public string? CheckedInitialsName { get; set; }
    public bool? CheckedInitialsOffscreen { get; set; }
    public bool? CheckedViewportOffscreen { get; set; }
    public string? FirstFalseStage { get; private set; }
    public InitialsProbe? FirstFalseProbe { get; private set; }
    public InitialsBounds? Text { get; set; }
    public InitialsBounds? Viewport { get; set; }
    public double? ViewportScale { get; set; }
    public InitialsBounds? PreviousPhoto { get; set; }
    public InitialsBounds? WindowBounds { get; set; }
    public long? WindowMetadataObservedAt { get; set; }
    public long WindowHandle { get; set; }
    public int ProcessId { get; set; }
    public double Scale { get; set; }
    public string ProbeStage { get; set; } = "not-sampled";
    public InitialsInk? Ink { get; set; }
    public int[]? CircleEdges { get; set; }
    public List<InitialsProbe> Probes { get; init; } = [];
    public bool ProbesTruncated { get; private set; }
    internal bool Check(string stage, bool passed)
    {
        if (!passed) FirstFalseStage ??= stage;
        return passed;
    }
    internal void Probe(double x, double y, uint expected, uint actual, bool matched, int? captureX = null, int? captureY = null)
    {
        if (ProbeStage == "glyph-ink") return;
        if (Probes.Count >= 1024) { ProbesTruncated = true; return; }
        var probe = new InitialsProbe(ProbeStage, x, y, expected, actual, matched, captureX, captureY);
        Probes.Add(probe);
        if (!matched && ProbeStage.StartsWith("anchor-", StringComparison.Ordinal))
        {
            FirstFalseStage ??= ProbeStage;
            FirstFalseProbe ??= probe;
        }
    }
}

internal sealed record InitialsBitmap(int Left, int Top, int Width, int Height, int Stride,
    byte[] Bgr32, long AcquisitionStartedAt, long AcquisitionCompletedAt, DateTimeOffset CapturedAt);
internal sealed record InitialsRejectedFrame(InitialsObservation Observation, InitialsBitmap Bitmap);

internal sealed class InitialsRejectionEvidence
{
    private readonly HarnessRuntime.FailureLedger failures = new();
    private InitialsObservation? latest;
    private InitialsRejectedFrame? frame;
    internal InitialsObservation? BindingObservation => frame?.Observation ?? latest;
    internal void RecordFailure(Exception error) => failures.Record(error);

    internal void Reject(InitialsObservation observation, Func<InitialsBitmap>? bitmap = null)
    {
        latest = observation with { Probes = [.. observation.Probes], CircleEdges = observation.CircleEdges?.ToArray() };
        if (bitmap is null) return;
        try
        {
            var captured = bitmap();
            if (captured.Width <= 0 || captured.Height <= 0 || captured.Stride != checked(captured.Width * 4) ||
                captured.Bgr32.Length != checked(captured.Stride * captured.Height) || captured.Bgr32.Length > 2 * 1024 * 1024)
                throw new InvalidOperationException("Rejected bitmap exceeds the bounded Bgr32 diagnostic shape.");
            latest = observation with { Probes = [.. observation.Probes], CircleEdges = observation.CircleEdges?.ToArray() };
            frame = new(latest, captured with { Bgr32 = captured.Bgr32.ToArray() });
        }
        catch (Exception error) { failures.Record(error); }
    }

    internal void Observe(Action observation, string prefix, Func<object> binding, Action<string>? reportError = null)
    {
        try { observation(); }
        catch (TimeoutException primary)
        {
            // Supplemental output never replaces the original observation failure.
            object? identity = null;
            Attempt(() => identity = binding());
            if (frame is not null)
                Attempt(() =>
                {
                    using var output = new FileStream(prefix + ".png", FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    WritePng(output, frame.Bitmap);
                });
            Attempt(() =>
            {
                using var output = new FileStream(prefix + ".json", FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(output, new
                {
                    schemaVersion = 1, identity, timestampFrequency = Stopwatch.Frequency,
                    revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                    runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                    harnessProcessId = Environment.ProcessId,
                    outcome = "Original photo observation failed", latestObservation = latest,
                    frame = frame is null ? null : new
                    {
                        observation = frame.Observation,
                        dpi = frame.Observation.Scale * 96,
                        bitmap = new
                        {
                            frame.Bitmap.Left, frame.Bitmap.Top, frame.Bitmap.Width, frame.Bitmap.Height, frame.Bitmap.Stride,
                            frame.Bitmap.AcquisitionStartedAt, frame.Bitmap.AcquisitionCompletedAt, frame.Bitmap.CapturedAt,
                            pixelFormat = "Bgr32", sha256 = Convert.ToHexString(SHA256.HashData(frame.Bitmap.Bgr32))
                        }
                    },
                    bitmapAbsentReason = frame is null ? "No rejected original pixel buffer retained; no replacement screenshot was taken." : null,
                    outputFailures = failures.Snapshot,
                    interpretation = "Last original rejected captured bitmap only. Unreached probes stay unobserved. No later screenshot, UI reread, retest or time subtraction."
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
                });
            });
            if (failures.Snapshot.Count != 0)
            {
                primary.Data["InitialsDiagnosticOutputFailures"] = failures.Snapshot;
                try { (reportError ?? Console.Error.WriteLine)("INITIALS-DIAGNOSTIC-OUTPUT-FAILURE " + JsonSerializer.Serialize(failures.Snapshot)); }
                catch (Exception error)
                {
                    failures.Record(error, fallback: true);
                    primary.Data["InitialsDiagnosticOutputFailures"] = failures.Snapshot;
                }
            }
            throw;

            void Attempt(Action write)
            {
                try { write(); }
                catch (Exception error) { failures.Record(error); }
            }
        }
    }

    private static void WritePng(Stream image, InitialsBitmap bitmap)
    {
        image.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, bitmap.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), bitmap.Height);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var pixels = new MemoryStream();
        using (var compressed = new ZLibStream(pixels, CompressionLevel.Fastest, true))
            for (var y = 0; y < bitmap.Height; y++)
            {
                compressed.WriteByte(0);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var offset = y * bitmap.Stride + x * 4;
                    compressed.Write(new[] { bitmap.Bgr32[offset + 2], bitmap.Bgr32[offset + 1], bitmap.Bgr32[offset] });
                }
            }
        Chunk("IDAT", pixels.ToArray()); Chunk("IEND", []);

        void Chunk(string type, byte[] data)
        {
            var count = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(count, data.Length);
            image.Write(count);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            image.Write(body);
            uint crc = uint.MaxValue;
            foreach (var b in body)
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            }
            BinaryPrimitives.WriteUInt32BigEndian(count, ~crc); image.Write(count);
        }
    }
}
