// TEMP-NATIVE-BOUNDARY: diagnostic-only harness; NEVER candidate acceptance.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using OrgExplorer.TempNativeBoundary;
using OrgExplorer.Workflow;
using System.Runtime.InteropServices;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static TempBoundarySession? tempNativeBoundarySession;
    internal static bool TempNativeBoundaryEver { get; private set; } = true;

    private sealed class TempBoundarySession : IDisposable
    {
        private readonly string directory, mapName;
        private readonly BoundaryRequest[] requests = new BoundaryRequest[42];
        private BoundaryMap? map;
        private BoundarySnapshot? snapshot;
        private string? failure;
        private int requestCount, productPid, stderrFaults;
        private string? productPath, productHash, assemblyHash;
        private bool captured, disposed;
        private bool manifestUnavailable;
        private readonly long started = Stopwatch.GetTimestamp();
        private long captureStarted, captureEnded;
        private Process? cpuWaitProcess;
        private EventWaitHandle? cpuWaitReady, cpuWaitStop;
        private Task<string>? cpuWaitOutput, cpuWaitErrors;
        private DateTimeOffset cpuWaitDeadline;
        internal bool HasDiagnosticFailure => failure is not null;
        internal void Secondary(Exception error) => Fail(error);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        internal void StartCpuWait()
        {
            var script = Environment.GetEnvironmentVariable("TEMP_CPU_WAIT_SCRIPT");
            if (string.IsNullOrEmpty(script)) return;
            if (!Path.IsPathFullyQualified(script) || !File.Exists(script) || productPid == 0 || !Stopwatch.IsHighResolution)
                throw new InvalidOperationException("CPU/wait opt-in requires the exact prepared helper and owned product PID.");
            var budget = DateTimeOffset.Parse(Environment.GetEnvironmentVariable("SCALE_STARTED_AT") ??
                throw new InvalidOperationException("CPU/wait requires the original setup-inclusive budget start.")).AddSeconds(660);
            cpuWaitDeadline = budget;
            var maximum = Math.Min(180, (int)(budget - DateTimeOffset.UtcNow).TotalSeconds - 165);
            if (maximum < 1) throw new TimeoutException("No original660 budget remains for bounded CPU/wait recording and stop.");
            var output = Path.Combine(directory, "cpu-wait");
            Directory.CreateDirectory(output);
            var profile = Path.Combine(output, "cpu-wait.wprp");
            File.Copy(Path.Combine(Path.GetDirectoryName(script)!, "TEMP-CPU-WAIT.wprp"), profile, overwrite: false);
            using var product = Process.GetProcessById(productPid);
            var uiThread = GetWindowThreadProcessId(product.MainWindowHandle, out var windowPid);
            if (uiThread == 0 || windowPid != productPid)
                throw new InvalidOperationException("Cannot bind the owned product window to its native UI thread; no recording.");
            var nonce = Guid.NewGuid().ToString("N");
            var readyName = "Local\\OrgExplorerCpuWaitReady_" + nonce;
            var stopName = "Local\\OrgExplorerCpuWaitStop_" + nonce;
            cpuWaitReady = new(false, EventResetMode.ManualReset, readyName, out var readyNew);
            cpuWaitStop = new(false, EventResetMode.ManualReset, stopName, out var stopNew);
            if (!readyNew || !stopNew) throw new InvalidOperationException("CPU/wait event ownership collision.");
            var request = Path.Combine(output, "cpu-wait-request.json");
            File.WriteAllText(request, JsonSerializer.Serialize(new
            {
                diagnosticOnly = true, acceptanceEligible = false, directory = output, profile,
                instance = "OrgExplorerCpuWait_" + nonce, readyEvent = readyName, stopEvent = stopName,
                harnessPid = Environment.ProcessId, productPid, productUiOsThreadId = uiThread,
                callerOsThreadId = GetCurrentThreadId(), callerManagedThreadId = Environment.CurrentManagedThreadId,
                qpc = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency, highResolution = Stopwatch.IsHighResolution,
                primitiveThreadMeaning = "Managed thread IDs, not OS IDs; use owned HWND thread mapping and exact PID/QPC.",
                budgetDeadline = budget, maximumCaptureSeconds = maximum,
                revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                shippingBaseRevision = "732c9faeceaaa7fd9f9178797d16b6e62d15b594",
                productHash, assemblyHash,
                harnessHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location)))
            }));
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-File", script, "-RequestPath", request })
                start.ArgumentList.Add(argument);
            cpuWaitProcess = Process.Start(start) ?? throw new InvalidOperationException("CPU/wait helper did not start.");
            cpuWaitOutput = cpuWaitProcess.StandardOutput.ReadToEndAsync();
            cpuWaitErrors = cpuWaitProcess.StandardError.ReadToEndAsync();
            var waiting = Stopwatch.StartNew();
            while (!cpuWaitReady.WaitOne(100))
                if (cpuWaitProcess.HasExited || waiting.Elapsed.TotalSeconds > 75)
                    throw new InvalidOperationException("Owned CPU/wait recorder failed readiness; no native phase retry.");
        }

        internal void StopCpuWait(Exception? primary)
        {
            if (cpuWaitProcess is null)
            {
                cpuWaitReady?.Dispose();
                cpuWaitStop?.Dispose();
                return;
            }
            try
            {
                cpuWaitStop!.Set();
                var remaining = Math.Max(0, Math.Min(80000, (int)(cpuWaitDeadline - DateTimeOffset.UtcNow).TotalMilliseconds));
                if (!cpuWaitProcess.WaitForExit(remaining))
                {
                    cpuWaitProcess.Kill(entireProcessTree: true);
                    throw new TimeoutException("Owned CPU/wait helper stop watchdog expired; capture incomplete; outer owned cleanup required.");
                }
                if (cpuWaitProcess.ExitCode != 0)
                    throw new InvalidOperationException("Owned CPU/wait capture incomplete; see raw session/tool evidence.");
            }
            catch (Exception error) when (primary is not null && error is InvalidOperationException or TimeoutException or
                System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                Fail(error);
            }
            finally
            {
                if (cpuWaitOutput?.IsCompletedSuccessfully == true) Console.Write(cpuWaitOutput.Result);
                if (cpuWaitErrors?.IsCompletedSuccessfully == true) Console.Error.Write(cpuWaitErrors.Result);
                cpuWaitProcess.Dispose();
                cpuWaitProcess = null;
                cpuWaitReady?.Dispose();
                cpuWaitStop?.Dispose();
            }
        }

        internal TempBoundarySession()
        {
            TempNativeBoundaryEver = true;
            directory = Path.GetDirectoryName(diagnostics)!;
            mapName = "Local\\OrgExplorer.NativeBoundary." + Guid.NewGuid().ToString("N");
            try
            {
                map = BoundaryMap.Create(Path.Combine(directory, "native-boundary.raw.log"), mapName,
                    OrgExplorer.TestFixtures.ScaleFixture.Email(11), OrgExplorer.TestFixtures.ScaleFixture.Target,
                    BoundaryClock.Current);
            }
            catch (Exception error) when (Expected(error)) { Fail(error); }
            WriteManifest(primary: null);
        }

        internal static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or
            ObjectDisposedException or ArgumentException or NotSupportedException or InvalidOperationException;

        private void Fail(Exception error)
        {
            failure ??= $"{error.GetType().Name}; hresult={error.HResult}; " +
                error.Message[..Math.Min(error.Message.Length, 512)];
            try { Console.Error.WriteLine("TEMP-NATIVE-BOUNDARY diagnosticFailure=true " + failure); }
            catch (IOException output)
            {
                failure = failure[..Math.Min(failure.Length, 400)] +
                    $"; stderrFailure={output.GetType().Name}/{output.HResult}";
            }
        }

        internal void Configure(ProcessStartInfo start)
        {
            if (map is null || captured) return;
            productPath = start.FileName;
            start.Environment["TEMP_NATIVE_BOUNDARY_MAP"] = mapName;
            start.RedirectStandardError = true;
        }

        internal void Attach(Process process)
        {
            productPid = process.Id;
            if (map is null || captured) return;
            try
            {
                map.BindProduct(productPid);
                productHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(productPath!)));
                assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(productPath!, ".dll"))));
            }
            catch (Exception error) when (Expected(error)) { Fail(error); }
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } data)
                {
                    if (data.StartsWith("TEMP-NATIVE-BOUNDARY", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref stderrFaults);
                        Console.Error.WriteLine(data[..Math.Min(data.Length, 512)]);
                    }
                    else Console.Error.WriteLine(data);
                }
            };
            try { process.BeginErrorReadLine(); }
            catch (Exception error) when (Expected(error)) { Fail(error); }
        }

        internal BoundaryRequest Arm(NoteScope scope, int ordinal, Guid note, int action, string exactTarget)
        {
            if (map is null || captured) return default;
            try
            {
                if (requestCount >= requests.Length) { map.Fault(BoundaryFault.Overflow); return default; }
                var request = map.Arm(requestCount + 1, action, exactTarget, (int)scope, ordinal, note);
                requests[requestCount++] = request;
                map.Record(request, BoundaryEvent.CallEnter);
                return request;
            }
            catch (Exception error) when (Expected(error)) { Fail(error); return default; }
        }

        internal void Returned(BoundaryRequest capturedRequest)
        {
            if (map is null || captured || !capturedRequest.Valid) return;
            try { map.Record(capturedRequest, BoundaryEvent.CallReturn); }
            catch (Exception error) when (Expected(error)) { Fail(error); }
        }

        internal void Usable(BoundaryRequest capturedRequest)
        {
            if (map is null || captured || !capturedRequest.Valid) return;
            try
            {
                map.Record(capturedRequest, BoundaryEvent.Usable);
                map.Disarm(capturedRequest);
            }
            catch (Exception error) when (Expected(error)) { Fail(error); }
        }

        internal void Capture(Exception? primary)
        {
            if (captured) return;
            captured = true;
            captureStarted = Stopwatch.GetTimestamp();
            try
            {
                if (map is not null)
                {
                    map.Freeze();
                    snapshot = map.Snapshot(42);
                    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { IncludeFields = true });
                    if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024)
                        throw new InvalidDataException("TEMP-NATIVE-BOUNDARY trace JSON exceeded its fixed output bound.");
                    File.WriteAllText(Path.Combine(directory, "native-boundary-records.json"), json);
                }
            }
            catch (Exception error) when (Expected(error)) { Fail(error); }
            finally { captureEnded = Stopwatch.GetTimestamp(); WriteManifest(primary); }
        }

        private void WriteManifest(Exception? primary)
        {
            if (manifestUnavailable) return; // Prior output failure is retained; no uncertain-write retry.
            try
            {
                File.WriteAllText(Path.Combine(directory, "native-boundary-trace-manifest.json"),
                    JsonSerializer.Serialize(new
                    {
                        diagnosticOnly = true, acceptanceEligible = false, instrumentedSource = true,
                        shippingBaseRevision = "732c9faeceaaa7fd9f9178797d16b6e62d15b594",
                        parentTraceRevision = "a0ea10265b27570670719328a3603960c94de100",
                        revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                        runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                        runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                        callerPid = Environment.ProcessId, productPid, started,
                        captureStarted, captureEnded,
                        productPath, productHash, assemblyHash,
                        harnessHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))),
                        frequency = Stopwatch.Frequency, highResolution = Stopwatch.IsHighResolution,
                        mapBytes = BoundaryMap.Bytes, recordBytes = 64,
                        recordFormatVersion = 1, eventSchemaVersion = 2,
                        additionalEventKinds = new
                        {
                            ignoredSelection = (int)BoundaryEvent.IgnoredSelection,
                            unassociatedDispatchEnter = (int)BoundaryEvent.UnassociatedDispatchEnter,
                            unassociatedDispatchExit = (int)BoundaryEvent.UnassociatedDispatchExit
                        },
                        unassociatedSpanContract = "Request0/Parent0 forever; app-producer unique span ID. Target is entry-captured identity slot (0 unknown,1 owner,2 report); Flags is entry-captured navigation (0 false,1 true). IgnoredSelection is a distinct instant, Flags1 means ignored/loading. Missing/clipped/capture-censored endpoints remain unknown, never fabricated durations. Same-thread intersections only; nested/inclusive spans are not additive or causal attribution.",
                        rawFile = "native-boundary.raw.log", requestCount, requests = requests.Take(requestCount),
                        traceComplete = captured && failure is null && stderrFaults == 0 && snapshot is { Complete: true },
                        failure, stderrFaults, snapshotFaults = snapshot?.Faults.ToString(),
                        primaryType = primary?.GetType().FullName, primaryHResult = primary?.HResult,
                        interpretation = "NONACCEPTING. LayoutUpdated is only a managed layout observation, not render/usable completion. Original native PersonReady/clocks/targets/full samples unchanged; overhead retained, not deducted. Missing/superseded/partial/clock/identity/fault records never imply a span or success."
                    }));
            }
            catch (Exception error) when (Expected(error)) { manifestUnavailable = true; Fail(error); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Capture(primary: null);
            try { map?.Dispose(); }
            catch (Exception error) when (Expected(error)) { Fail(error); }
        }
    }

    private static T RunTempNativeBoundary<T>(TempBoundarySession trace, Func<T> original)
    {
        Exception? primary = null;
        try { trace.StartCpuWait(); return original(); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { trace.Capture(primary); }
            finally
            {
                tempNativeBoundarySession = null;
                trace.StopCpuWait(primary);
            }
        }
    }

    private static void StopTempNativeBoundaryApplication(Process process, Exception? primary, TempBoundarySession trace)
    {
        try { StopApplication(process); }
        catch (Exception cleanup) when (primary is not null && TempBoundarySession.Expected(cleanup))
        {
            trace.Secondary(cleanup);
        }
    }
}
