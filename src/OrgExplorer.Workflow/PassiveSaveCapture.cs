using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace OrgExplorer.Workflow;

// ISOLATED DIAGNOSTIC SOURCE ONLY. This file and its ancestry must not enter shipping.
internal enum PassiveSaveStage
{
    RequestBegin, RequestEnd, ContinuedInputBegin, ContinuedInputContent, ContinuedInputEnd, DraftUpdated, SaveSubmitted,
    SaveReturned, SaveAccepted, ActualCommit, FeedbackWrite, NativeEditorBegin, NativeEditorEnd,
    NativeOwnerBegin, NativeOwnerEnd, NativePredicate, CaptureGeometryBegin, CaptureGeometryEnd,
    CaptureAllocationBegin, CaptureAllocationEnd, CaptureBitBltBegin, CaptureBitBltEnd,
    CaptureCopyBegin, CaptureCopyEnd, CapturePngBegin, CapturePngEnd, CaptureCleanupBegin,
    CaptureCleanupEnd, CaptureIdentityBegin, CaptureIdentityEnd, CaptureMetadataBegin,
    CaptureMetadataEnd, CaptureFailed
}

internal enum PassiveSaveFeedback { Other, Pending, ScopedLocalSave, TeamConfirmed, Published, LocalOnly }

internal sealed record PassiveSaveFact(PassiveSaveStage Stage, NoteScope? Scope = null, Guid? Note = null,
    string? OwnerDigest = null, Guid? Incarnation = null, long? Generation = null, Guid? SaveCall = null,
    long? SubmittedGeneration = null, long? LatestGeneration = null, bool? Accepted = null,
    bool? Retained = null, bool? Removed = null, bool? SuccessorBasis = null,
    Guid? Phase = null, WorkspaceStoragePhaseKind? PhaseKind = null, int? Sample = null,
    int? ContinuedInput = null, string? RequestDigest = null, PassiveSaveFeedback? Feedback = null,
    Guid? SelectedNote = null, Guid? DisplayedNote = null, string? DisplayedOwnerDigest = null,
    bool? Predicate = null, bool? InputReturned = null, Guid? HeldOperation = null, string? PrefixDigest = null, uint? Dpi = null)
{
    [JsonIgnore] internal long? SourceTimestamp { get; init; }
}

internal sealed record PassiveNoteSample(NoteScope Scope, Guid Note, string OwnerDigest, int Sample,
    string RequestDigest);

internal sealed record PassiveSaveRecord(int Schema, long Sequence, int Pid, long ProcessStartUtcTicks,
    int Thread, long QpcFrequency, long QpcTicks, string Role, string ClockDomain, bool DiagnosticOnly, PassiveSaveFact Fact);

internal sealed record PassiveCommitLink(Guid Phase, Guid SaveCall, Guid Incarnation, long RequestGeneration,
    long SubmittedGeneration, long LatestGeneration, bool RetainedLatest, bool Removed);

internal sealed class PassiveSaveCapture : TraceListener, IWorkspaceFileObserver
{
    internal const string Category = "ORGEXPLORER-ISOLATED-SAVE-CORRELATION";
    internal const int Capacity = 4096;
    internal const string ShippingRevision = "cae977166df7327b4a2d268a10a20578adf338e6";
    internal const string ShippingTree = "ad625fa53e6592840e1e44e0bc5ef6e4ed65e9de";
    private static readonly AsyncLocal<PassiveNoteSample?> sample = new();
    private static readonly AsyncLocal<Guid?> heldOperation = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly Channel<object> pending = Channel.CreateBounded<object>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly string role;
    private readonly long processStart;
    private readonly int pid = Environment.ProcessId;
    private readonly Func<Stream> output;
    private readonly string? diagnosticPath;
    private readonly Thread drain;
    private readonly ManualResetEventSlim drained = new();
    private long sequence, dropped, overflow, failures, identityFailures, firstProtectedAt;
    private int stopped, nativeRequested;
    private readonly bool previousGlobalLock;
    private readonly string clockDomain;
    private readonly object processIdentity;
    internal static PassiveSaveCapture? Current { get; private set; }
    internal static PassiveNoteSample? Sample => sample.Value;
    internal static Guid? HeldOperation => heldOperation.Value;
    public override bool IsThreadSafe => true;
    internal long OutputFailures => Interlocked.Read(ref failures);
    internal long Dropped => Interlocked.Read(ref dropped);

    private sealed record NativeIdentityRequest;
    private sealed record DesktopIdentityRequest(object Identity);
    private sealed class CombinedObserver(PassiveSaveCapture capture, IWorkspaceFileObserver observer) : IWorkspaceFileObserver
    {
        public void Observe(WorkspaceFileEffect effect) => observer.Observe(effect);
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            capture.ObservePhase(phase);
            observer.ObservePhase(phase);
        }
    }

    internal static IWorkspaceFileObserver? Attach(IWorkspaceFileObserver? observer) =>
        Current is not { } capture || ReferenceEquals(observer, capture) ? observer :
        observer is null ? capture : new CombinedObserver(capture, observer);

    internal static PassiveSaveCapture Install(string role, Assembly? desktop = null, Func<Stream>? output = null)
    {
        if (Current is not null) throw new InvalidOperationException("Only one owned passive capture may run in a process.");
        var capture = new PassiveSaveCapture(role, desktop, output);
        Current = capture;
        Trace.UseGlobalLock = false;
        Trace.Listeners.Add(capture);
        capture.drain.Start();
        return capture;
    }

    private PassiveSaveCapture(string role, Assembly? desktop, Func<Stream>? output)
    {
        if (role is not ("rich-normal-entry-exe" or "workspace-controlled-entry-exe" or
            "rich-controlled-wpf-host" or "workspace-controlled-adoption-host" or "workspace-controlled-wpf-host" or
            "rich-harness" or "workspace-harness" or "support-test"))
            throw new ArgumentException("An explicit diagnostic process role is required.", nameof(role));
        this.role = role;
        clockDomain = $"{Environment.GetEnvironmentVariable("GITHUB_RUN_ID")}/{Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")}/" +
            $"{Environment.GetEnvironmentVariable("ORG_EXPLORER_DIAGNOSTIC_WORKER")}/{Environment.GetEnvironmentVariable("RUNNER_NAME")}";
        using var process = Process.GetCurrentProcess();
        processStart = process.StartTime.ToUniversalTime().Ticks;
        previousGlobalLock = Trace.UseGlobalLock;
        // Startup, before WPF construction or any note/held transaction interval.
        processIdentity = new
        {
            schema = 1,
            stage = "diagnostic-process-identity",
            diagnosticOnly = true,
            pid,
            processStartUtcTicks = processStart,
            role,
            qpcFrequency = Stopwatch.Frequency,
            clockDomain,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runner = Environment.GetEnvironmentVariable("RUNNER_NAME"),
            revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            shippingRevision = ShippingRevision,
            shippingTree = ShippingTree,
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            worker = Environment.GetEnvironmentVariable("ORG_EXPLORER_DIAGNOSTIC_WORKER"),
            workerInvocation = Environment.GetEnvironmentVariable("UI_SCALE_WORKER_RUN_DIRECTORY"),
            executable = IdentifyFile(Environment.ProcessPath!),
            desktop = desktop is null ? null : IdentifyAssembly(desktop),
            workflow = IdentifyAssembly(typeof(ApplicationWorkflow).Assembly),
            entryAssembly = Assembly.GetEntryAssembly() is { } entry ? IdentifyAssembly(entry) : null
        };
        if (output is null)
        {
            var directory = Environment.GetEnvironmentVariable("ORG_EXPLORER_DIAGNOSTIC_DIRECTORY") ??
                Path.Combine(AppContext.BaseDirectory, "isolated-save-diagnostics");
            diagnosticPath = Path.Combine(directory, $"{role}-{pid}-{processStart}.jsonl");
            this.output = () =>
            {
                Directory.CreateDirectory(directory);
                return new FileStream(diagnosticPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 16384, FileOptions.SequentialScan);
            };
        }
        else this.output = output;
        // Only this owned thread performs serialization/output. Exit never waits for diagnostic I/O.
        // Termination before the close marker leaves incomplete evidence, not a successful capture.
        drain = new Thread(Drain) { Name = "Isolated passive save diagnostic drain", IsBackground = true };
    }

    internal static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string OwnerDigest(string owner) => Digest(
        $"{Environment.GetEnvironmentVariable("GITHUB_RUN_ID")}/{Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")}/" +
        owner.ToUpperInvariant());

    internal static PassiveSaveFeedback ClassifyFeedback(string message) =>
        message.StartsWith("Pending private note edits", StringComparison.Ordinal) ? PassiveSaveFeedback.Pending :
        message is "Person note saved locally." or "Team note saved locally." ? PassiveSaveFeedback.ScopedLocalSave :
        message == "Team note saved locally and confirmed for current context." ? PassiveSaveFeedback.TeamConfirmed :
        message == "Locally saved and published. Provider delivery elsewhere is unknown." ? PassiveSaveFeedback.Published :
        message == "Locally saved; latest version is not confirmed published. Provider delivery elsewhere is unknown."
            ? PassiveSaveFeedback.LocalOnly : PassiveSaveFeedback.Other;

    internal static IDisposable BeginSample(NoteScope scope, Guid note, string owner, int index, string request)
    {
        var previous = sample.Value;
        sample.Value = new(scope, note, OwnerDigest(owner), index, Digest(request));
        return new RestoreSample(previous);
    }

    private sealed class RestoreSample(PassiveNoteSample? previous) : IDisposable
    {
        public void Dispose() => sample.Value = previous;
    }

    internal static IDisposable DuringHeldCapture(Guid? operation)
    {
        var previous = heldOperation.Value;
        heldOperation.Value = operation;
        return new RestoreHeldOperation(previous);
    }
    private sealed class RestoreHeldOperation(Guid? previous) : IDisposable
    {
        public void Dispose() => heldOperation.Value = previous;
    }

    internal void IdentifyLoadedDesktop(Assembly assembly)
    {
        // Called at the existing host assembly load, before constructing its WPF application/window.
        if (!pending.Writer.TryWrite(new DesktopIdentityRequest(IdentifyAssembly(assembly)))) RefuseFact();
    }

    internal static void Emit(PassiveSaveFact fact)
    {
        if (Current is not { } capture) return;
        if (!Enum.IsDefined(fact.Stage) || fact.Scope is { } scope && !Enum.IsDefined(scope) ||
            fact.PhaseKind is { } phase && !Enum.IsDefined(phase) ||
            fact.Feedback is { } feedback && !Enum.IsDefined(feedback) ||
            !IsDigest(fact.OwnerDigest) || !IsDigest(fact.DisplayedOwnerDigest) || !IsDigest(fact.RequestDigest) ||
            !IsDigest(fact.PrefixDigest))
        {
            capture.RefuseFact();
            return;
        }
        Trace.WriteLine(fact, Category);
    }

    private static bool IsDigest(string? value) => value is null || value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private void RefuseFact()
    {
        Interlocked.Increment(ref sequence);
        Interlocked.Increment(ref dropped);
    }

    internal static void EmitSample(PassiveSaveStage stage, int? input = null, string? requestDigest = null,
        bool? predicate = null, bool? inputReturned = null)
    {
        if (Sample is not { } value) return;
        Emit(new(stage, value.Scope, value.Note, value.OwnerDigest, Sample: value.Sample,
            ContinuedInput: input, RequestDigest: requestDigest ?? value.RequestDigest,
            Predicate: predicate, InputReturned: inputReturned, PrefixDigest: value.RequestDigest));
    }

    public override void Write(string? message) { }
    public override void WriteLine(string? message) { }
    public override void WriteLine(object? value, string? category)
    {
        if (category != Category || value is not PassiveSaveFact fact) return;
        var index = Interlocked.Increment(ref sequence);
        var record = new PassiveSaveRecord(1, index, pid, processStart, Environment.CurrentManagedThreadId,
            Stopwatch.Frequency, fact.SourceTimestamp ?? Stopwatch.GetTimestamp(), role, clockDomain, true, fact);
        if (fact.Stage is PassiveSaveStage.DraftUpdated or PassiveSaveStage.SaveSubmitted or PassiveSaveStage.RequestBegin)
            Interlocked.CompareExchange(ref firstProtectedAt, record.QpcTicks, 0);
        if (index > Capacity || Volatile.Read(ref stopped) != 0 || !pending.Writer.TryWrite(record))
        {
            Interlocked.Increment(ref dropped);
            if (index > Capacity || Volatile.Read(ref stopped) == 0) Interlocked.Increment(ref overflow);
        }
    }

    public void Observe(WorkspaceFileEffect effect) { }
    public void ObservePhase(WorkspaceStoragePhase phase)
    {
        if (phase.Name is "local-sqlite-commit" or "snapshot.adopt-commit" && phase.Kind == WorkspaceStoragePhaseKind.Begin)
            Interlocked.CompareExchange(ref firstProtectedAt, Stopwatch.GetTimestamp(), 0);
        if (phase.Name == "local-sqlite-read-snapshot" && phase.Kind == WorkspaceStoragePhaseKind.Begin &&
            Interlocked.CompareExchange(ref nativeRequested, 1, 0) == 0)
        {
            if (!pending.Writer.TryWrite(new NativeIdentityRequest()))
            { Interlocked.Increment(ref dropped); Interlocked.Increment(ref overflow); }
        }
        if (phase.Name != "local-sqlite-commit") return;
        Emit(new(PassiveSaveStage.ActualCommit, SaveCall: Trace.CorrelationManager.ActivityId is var call &&
            call != Guid.Empty ? call : null, Phase: phase.Id, PhaseKind: phase.Kind)
        { SourceTimestamp = phase.DiagnosticTimestamp == 0 ? null : phase.DiagnosticTimestamp });
    }

    private void Drain()
    {
        try
        {
            using (var stream = output())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384))
            {
                Write(processIdentity);
                while (pending.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (pending.Reader.TryRead(out var item))
                        if (item is NativeIdentityRequest)
                        {
                            // Requested only after this process has established an actual SQLite read snapshot.
                            // Do not initialize a bundle, load a second native library or inspect business state.
                            if (Interlocked.Read(ref firstProtectedAt) != 0)
                            {
                                Interlocked.Increment(ref identityFailures);
                                Write(new
                                {
                                    schema = 1,
                                    stage = "diagnostic-native-incomplete",
                                    diagnosticOnly = true,
                                    pid,
                                    processStartUtcTicks = processStart,
                                    role,
                                    reason = "ProtectedIntervalAlreadyBegan"
                                });
                            }
                            else
                            {
                                var identityStartedAt = Stopwatch.GetTimestamp();
                                using var process = Process.GetCurrentProcess();
                                var modules = process.Modules.Cast<ProcessModule>().Where(module =>
                                    Path.GetFileName(module.FileName).Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                                if (modules.Length != 1)
                                {
                                    Interlocked.Increment(ref identityFailures);
                                    Write(new
                                    {
                                        schema = 1,
                                        stage = "diagnostic-native-incomplete",
                                        diagnosticOnly = true,
                                        pid,
                                        processStartUtcTicks = processStart,
                                        role,
                                        reason = "LoadedModuleIdentityUnknown"
                                    });
                                }
                                else
                                {
                                    var nativeVersion = SQLitePCL.raw.sqlite3_libversion().utf8_to_string();
                                    var nativeSourceId = SQLitePCL.raw.sqlite3_sourceid().utf8_to_string();
                                    var moduleSha256 = HashFile(modules[0].FileName);
                                    var identityCompletedAt = Stopwatch.GetTimestamp();
                                    var protectedAt = Interlocked.Read(ref firstProtectedAt);
                                    var outsideProtectedInterval = protectedAt == 0 || protectedAt > identityCompletedAt;
                                    var matchesPin = nativeVersion == "3.54.1" && nativeSourceId ==
                                        "2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76" &&
                                        moduleSha256 == "922AB7FF7E47EF2694CA5F62ADBEFCC44C637BF5D082488C286816ED5D06363F";
                                    if (!outsideProtectedInterval || !matchesPin) Interlocked.Increment(ref identityFailures);
                                    Write(new
                                    {
                                        schema = 1,
                                        stage = "diagnostic-loaded-sqlite",
                                        diagnosticOnly = true,
                                        pid,
                                        processStartUtcTicks = processStart,
                                        role,
                                        clockDomain,
                                        qpcFrequency = Stopwatch.Frequency,
                                        identityStartedAt,
                                        identityCompletedAt,
                                        nativeVersion,
                                        nativeSourceId,
                                        moduleSha256,
                                        outsideProtectedInterval,
                                        matchesPin
                                    });
                                }
                            }
                        }
                        else if (item is DesktopIdentityRequest loaded)
                            Write(new
                            {
                                schema = 1,
                                stage = "diagnostic-controlled-desktop-identity",
                                diagnosticOnly = true,
                                pid,
                                processStartUtcTicks = processStart,
                                role,
                                clockDomain,
                                desktop = loaded.Identity
                            });
                        else Write(item);
                    writer.Flush();
                }
                Write(new
                {
                    schema = 1,
                    stage = "diagnostic-stream-end",
                    diagnosticOnly = true,
                    pid,
                    processStartUtcTicks = processStart,
                    role,
                    qpcFrequency = Stopwatch.Frequency,
                    qpcTicks = Stopwatch.GetTimestamp(),
                    sequence,
                    dropped,
                    overflow,
                    outputFailures = failures,
                    identityFailures,
                    nativeIdentityRequested = nativeRequested != 0,
                    diagnosticsComplete = dropped == 0 && overflow == 0 && failures == 0 && identityFailures == 0
                });
                writer.Flush();

                void Write(object value) => writer.WriteLine(JsonSerializer.Serialize(value, Json));
            }
            // A footer written before a failed Flush/Close is not complete transport evidence.
            if (diagnosticPath is not null)
                File.WriteAllText(diagnosticPath + ".complete.json", JsonSerializer.Serialize(new
                {
                    schema = 1,
                    stage = "diagnostic-stream-closed",
                    diagnosticOnly = true,
                    pid,
                    processStartUtcTicks = processStart,
                    role,
                    sequence,
                    dropped,
                    overflow,
                    outputFailures = failures,
                    identityFailures,
                    diagnosticsComplete = dropped == 0 && overflow == 0 && failures == 0 && identityFailures == 0
                }, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            Interlocked.Increment(ref failures);
            try { Console.Error.WriteLine($"ISOLATED-SAVE-DIAGNOSTIC-OUTPUT-FAILURE incomplete=true; pid={pid}; role={role}; type={error.GetType().Name}; hresult={error.HResult}"); }
            catch (Exception reporting) when (reporting is IOException or UnauthorizedAccessException)
            { Interlocked.Increment(ref failures); }
        }
        finally { drained.Set(); }
    }

    private object IdentifyAssembly(Assembly assembly) => new
    { file = IdentifyFile(assembly.Location), mvid = assembly.ManifestModule.ModuleVersionId };
    private object IdentifyFile(string path)
    {
        try { return new { sha256 = HashFile(path) }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref failures);
            return new { identityStatus = "Incomplete", errorType = error.GetType().Name, hresult = error.HResult };
        }
    }
    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    internal bool WaitForDrain(TimeSpan timeout) => drained.Wait(timeout);

    // Offline evidence join, never a save-status predicate. UI feedback alone cannot identify a generation.
    internal static PassiveCommitLink? LinkCommittedPrefix(IReadOnlyList<PassiveSaveRecord> records,
        string targetRole, Guid note, string prefixDigest, bool diagnosticsComplete)
    {
        if (!diagnosticsComplete || records.Count == 0 || note == Guid.Empty || !IsDigest(prefixDigest) ||
            records.Any(record => !record.DiagnosticOnly || record.Schema != 1 || record.Pid <= 0 ||
                record.ProcessStartUtcTicks <= 0 || record.QpcFrequency <= 0 || record.QpcTicks <= 0 ||
                string.IsNullOrWhiteSpace(record.ClockDomain)) ||
            records.Select(record => record.ClockDomain).Distinct().Count() != 1 ||
            records.Select(record => record.QpcFrequency).Distinct().Count() != 1 ||
            records.GroupBy(record => (record.Pid, record.ProcessStartUtcTicks)).Any(group =>
                !group.OrderBy(record => record.Sequence).Select(record => record.Sequence)
                    .SequenceEqual(Enumerable.Range(1, group.Count()).Select(value => (long)value))))
            return null;
        var requests = records.Where(record => record.Fact.Stage == PassiveSaveStage.RequestBegin &&
            record.Fact.Note == note && record.Fact.RequestDigest == prefixDigest).ToArray();
        if (requests.Length != 1) return null;
        var request = requests[0];
        var target = records.Where(record => record.Role == targetRole && record.Fact.Note == note &&
            record.Fact.Scope == request.Fact.Scope && record.Fact.OwnerDigest == request.Fact.OwnerDigest).ToArray();
        if (target.Select(record => (record.Pid, record.ProcessStartUtcTicks)).Distinct().Count() != 1) return null;
        var roots = target.Where(record => record.Fact.Stage == PassiveSaveStage.DraftUpdated &&
            record.Fact.RequestDigest == prefixDigest && record.QpcTicks >= request.QpcTicks).ToArray();
        if (roots.Length != 1 || roots[0].Fact.Incarnation is not { } incarnation || incarnation == Guid.Empty ||
            roots[0].Fact.Generation is not { } generation || generation <= 0) return null;
        var coveredInputs = records.Where(record => record.Pid == request.Pid &&
            record.ProcessStartUtcTicks == request.ProcessStartUtcTicks && record.Fact.Note == note &&
            record.Fact.Sample == request.Fact.Sample && record.Fact.PrefixDigest == prefixDigest &&
            record.Fact.Stage == PassiveSaveStage.ContinuedInputContent && record.Fact.Predicate == true)
            .Select(record => record.Fact.RequestDigest).Append(prefixDigest).ToHashSet();
        var candidates = new List<(PassiveCommitLink Link, long CommittedAt)>();
        foreach (var submission in target.Where(record => record.Fact.Stage == PassiveSaveStage.SaveSubmitted &&
            record.Fact.Incarnation == incarnation && record.Fact.SubmittedGeneration >= generation &&
            coveredInputs.Contains(record.Fact.RequestDigest)))
        {
            if (submission.Fact.SaveCall is not { } call || call == Guid.Empty) continue;
            var phases = records.Where(record => record.Pid == submission.Pid &&
                record.ProcessStartUtcTicks == submission.ProcessStartUtcTicks &&
                record.Fact.Stage == PassiveSaveStage.ActualCommit && record.Fact.SaveCall == call).ToArray();
            var accepted = target.Where(record => record.Fact.Stage == PassiveSaveStage.SaveAccepted &&
                record.Fact.SaveCall == call && record.Fact.Incarnation == incarnation &&
                record.Fact.Accepted == true && record.Fact.SubmittedGeneration == submission.Fact.SubmittedGeneration).ToArray();
            if (phases.Length != 2 || phases[0].Fact.PhaseKind != WorkspaceStoragePhaseKind.Begin ||
                phases[1].Fact.PhaseKind != WorkspaceStoragePhaseKind.End ||
                phases[0].Fact.Phase != phases[1].Fact.Phase || phases[0].Fact.Phase is not { } phase || phase == Guid.Empty ||
                phases[0].QpcTicks < submission.QpcTicks || phases[1].QpcTicks < phases[0].QpcTicks ||
                accepted.Length != 1 || accepted[0].QpcTicks < phases[1].QpcTicks ||
                accepted[0].Fact.LatestGeneration is not { } latest ||
                accepted[0].Fact.Retained is not { } retained || accepted[0].Fact.Removed is not { } removed ||
                retained == removed || latest < submission.Fact.SubmittedGeneration ||
                retained && (accepted[0].Fact.SuccessorBasis != true || latest <= submission.Fact.SubmittedGeneration) ||
                removed && latest != submission.Fact.SubmittedGeneration)
                continue;
            candidates.Add((new(phase, call, incarnation, generation, submission.Fact.SubmittedGeneration!.Value,
                latest, retained, removed), phases[1].QpcTicks));
        }
        var first = candidates.OrderBy(candidate => candidate.CommittedAt).Take(2).ToArray();
        return first.Length == 0 || first.Length == 2 && first[0].CommittedAt == first[1].CommittedAt
            ? null : first[0].Link;
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        Trace.Listeners.Remove(this);
        Trace.UseGlobalLock = previousGlobalLock;
        Current = null;
        pending.Writer.TryComplete();
        base.Dispose(disposing);
    }
}
