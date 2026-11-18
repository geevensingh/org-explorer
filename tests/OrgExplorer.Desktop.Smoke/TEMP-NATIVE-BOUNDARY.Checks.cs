// TEMP-NATIVE-BOUNDARY: protocol-only no-window checks; no native acceptance.
using System.IO;
using OrgExplorer.TempNativeBoundary;
using System.Text.Json;
using System.IO.MemoryMappedFiles;
using System.Diagnostics;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static int VerifyTempNativeBoundary()
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "temp-native-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            CheckUnassociatedSpans(root);
            CheckUnassociatedBounds(root);
            CheckClock(root, mismatch: false);
            CheckClock(root, mismatch: true);
            CheckIdentity(root);
            CheckUnrelatedRefresh(root);
            CheckCapturedExit(root);
            CheckIncompleteAndOverflow(root);
            CheckMissingCalls(root);
            CheckMappingFaults(root);
            CheckLayoutAndAction(root);
            CheckFailureCapture(root);
            CheckOwnedProtocolChild(root);
            CheckRunnerClassification(root);
            Console.WriteLine("PASS TEMP-NATIVE-BOUNDARY: ignored selection/unassociated carry-in pairs/immutable Request0+Parent0/censored endpoints/provenance/clock/exact owner+event/no-borrow/immutable exit/overlap/fixed overflow/incomplete commit/missing call/superseded layout/map faults/primary preservation/owned-child abrupt exit. Structural protocol ONLY; no Window/handle/UIA/actual layout or acceptance evidence.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckUnassociatedSpans(string root) => WithTempMap(root, (native, app) =>
    {
        var carryIn = app.BeginDispatch(default, "owner@example.com", navigation: false);
        WriteCompleteTempRequest(native, app, 1);
        var next = native.Arm(2, 2, "owner@example.com", 0, 0, Guid.NewGuid());
        native.Record(next, BoundaryEvent.CallEnter);
        var child = app.BeginDispatch(carryIn, "report@example.com", navigation: true);
        var unknown = app.BeginDispatch(default, "other@example.com", navigation: true);
        app.RecordIgnoredSelection("owner@example.com");
        app.End(unknown);
        app.End(child);
        app.End(carryIn);
        var handler = app.BeginHandler("owner@example.com", 2);
        var dispatch = app.BeginDispatch(handler, "owner@example.com", navigation: true);
        app.End(dispatch);
        app.End(handler);
        app.Layout(handler, "owner@example.com", valid: true, superseded: false);
        native.Record(next, BoundaryEvent.CallReturn);
        native.Record(next, BoundaryEvent.Usable);
        native.Disarm(next);
        var snapshot = native.Snapshot(2);
        var ignored = snapshot.Records.Single(record => record.Event == BoundaryEvent.IgnoredSelection);
        if (ignored.Request != 0 || ignored.Span != 0 || ignored.Parent != 0 || ignored.Target != 1 ||
            ignored.Flags != 1 || snapshot.Records.Any(record => record.Event == BoundaryEvent.Unrelated))
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY ignored/loading selection remained ambiguous with unassociated OpenPerson.");
        if (carryIn.Id <= 0 || child.Id <= 0 || unknown.Id <= 0 ||
            carryIn.Valid || child.Valid || unknown.Valid)
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY unassociated OpenPerson is still ambiguous/entry-only, or borrowed request association.");
        foreach (var (span, target, navigation) in new[] { (carryIn, 1, 0), (child, 2, 1), (unknown, 0, 1) })
        {
            var pair = snapshot.Records.Where(record => record.Span == span.Id).ToArray();
            if (pair.Length != 2 || pair[0].Event.ToString() != "UnassociatedDispatchEnter" ||
                pair[1].Event.ToString() != "UnassociatedDispatchExit" ||
                pair[1].Timestamp < pair[0].Timestamp ||
                pair.Any(record => record.Request != 0 || record.Parent != 0 ||
                    record.Target != target || record.Flags != navigation))
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY unassociated entry/exit lost exact identity/navigation or immutable Request0/Parent0 across a changed header.");
        }
        var entries = snapshot.Records.Where(record => record.Event is BoundaryEvent.HandlerEnter or BoundaryEvent.DispatchEnter ||
            record.Event.ToString() == "UnassociatedDispatchEnter").ToArray();
        if (!snapshot.Complete || entries.Select(record => record.Span).Distinct().Count() != entries.Length ||
            snapshot.Records.Single(record => record.Span == carryIn.Id &&
                record.Event.ToString() == "UnassociatedDispatchEnter").Timestamp >
                snapshot.Records.First(record => record.Event == BoundaryEvent.CallEnter).Timestamp ||
            snapshot.Records.Single(record => record.Span == carryIn.Id &&
                record.Event.ToString() == "UnassociatedDispatchExit").Timestamp <
                snapshot.Records.First(record => record.Event == BoundaryEvent.Usable).Timestamp)
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY carry-in span was clipped to an active request or reused a primary producer span ID.");
    });

    private static void CheckUnassociatedBounds(string root)
    {
        foreach (var mode in new[] { "missing-exit", "capture-censored", "clipped-entry" })
            WithTempMap(root, (native, app) =>
            {
                if (mode == "clipped-entry")
                    app.Record(default(BoundaryRequest) with { Target = 1 },
                        BoundaryEvent.UnassociatedDispatchExit, span: 1);
                else
                {
                    var span = app.BeginDispatch(default, "owner@example.com", navigation: false);
                    if (mode == "capture-censored") { native.Freeze(); app.End(span); }
                }
                var snapshot = native.Snapshot(0);
                if (snapshot.Complete || !snapshot.Faults.HasFlag(BoundaryFault.MissingEvent) ||
                    snapshot.Records.Length != 1)
                    throw new InvalidOperationException($"TEMP-NATIVE-BOUNDARY {mode} unassociated span fabricated completeness instead of remaining unknown.");
            });
        WithTempMap(root, (native, app) =>
        {
            for (var index = 0; index < 1025; index++)
            {
                var span = app.BeginDispatch(default, "owner@example.com", navigation: false);
                app.End(span);
            }
            var snapshot = native.Snapshot(0);
            if (snapshot.Complete || !snapshot.Faults.HasFlag(BoundaryFault.Overflow) ||
                snapshot.RecordCount != 2048)
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY unassociated pairs grew/overwrote the fixed app lane or hid overflow.");
        });
    }

    private static void CheckIdentity(string root)
    {
        var name = "Local\\temp-native-" + Guid.NewGuid().ToString("N");
        using var native = BoundaryMap.Create(Path.Combine(root, Guid.NewGuid() + ".raw.log"), name,
            "owner@example.com", "report@example.com", BoundaryClock.Current);
        using var app = BoundaryMap.OpenApplication(name, BoundaryClock.Current);
        native.Arm(1, 1, "report@example.com", 0, 0, Guid.NewGuid());
        var unrelated = app.BeginDispatch(default, "report@example.com", navigation: false);
        var wrongOwner = app.BeginHandler("owner@example.com", 1);
        if (unrelated.Valid || wrongOwner.Valid || !native.Snapshot(1).Faults.HasFlag(BoundaryFault.UnmatchedIdentity))
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY unrelated refresh or wrong exact event identity obtained a request span.");
    }

    private static void CheckClock(string root, bool mismatch)
    {
        var name = "Local\\temp-native-" + Guid.NewGuid().ToString("N");
        using var native = BoundaryMap.Create(Path.Combine(root, Guid.NewGuid() + ".raw.log"), name,
            "owner@example.com", "report@example.com", BoundaryClock.Current);
        var clock = BoundaryClock.Current;
        using var app = BoundaryMap.OpenApplication(name, mismatch ? clock with { Frequency = clock.Frequency + 1 } : clock);
        WriteCompleteTempRequest(native, app, 1);
        var snapshot = native.Snapshot(1);
        if (mismatch ? snapshot.Complete || !snapshot.Faults.HasFlag(BoundaryFault.Clock) : !snapshot.Complete)
            throw new InvalidOperationException($"TEMP-NATIVE-BOUNDARY clock mismatch={mismatch} incorrectly produced Complete={snapshot.Complete}/faults={snapshot.Faults}.");
    }

    private static void CheckUnrelatedRefresh(string root) => WithTempMap(root, (native, app) =>
    {
        native.Arm(1, 1, "report@example.com", 0, 0, Guid.NewGuid());
        var rootSpan = app.BeginHandler("report@example.com", 1);
        if (app.BeginDispatch(rootSpan, "report@example.com", navigation: false).Valid)
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY same-identity refresh borrowed a navigation dispatch span.");
    });

    private static void WithTempMap(string root, Action<BoundaryMap, BoundaryMap> check)
    {
        var name = "Local\\temp-native-" + Guid.NewGuid().ToString("N");
        using var native = BoundaryMap.Create(Path.Combine(root, Guid.NewGuid() + ".raw.log"), name,
            "owner@example.com", "report@example.com", BoundaryClock.Current);
        using var app = BoundaryMap.OpenApplication(name, BoundaryClock.Current);
        check(native, app);
    }

    private static void CheckCapturedExit(string root) => WithTempMap(root, (native, app) =>
    {
        var first = native.Arm(1, 1, "report@example.com", 0, 0, Guid.NewGuid());
        native.Record(first, BoundaryEvent.CallEnter);
        var handler = app.BeginHandler("report@example.com", 1);
        var dispatch = app.BeginDispatch(handler, "report@example.com", true);
        native.Record(first, BoundaryEvent.CallReturn);
        native.Record(first, BoundaryEvent.Usable);
        native.Disarm(first);
        var second = native.Arm(2, 2, "owner@example.com", 0, 0, Guid.NewGuid());
        app.End(dispatch);
        app.End(handler);
        app.Layout(handler, "report@example.com", true, false);
        native.Record(second, BoundaryEvent.CallEnter);
        var next = app.BeginHandler("owner@example.com", 2);
        var nextDispatch = app.BeginDispatch(next, "owner@example.com", true);
        app.End(nextDispatch);
        app.End(next);
        app.Layout(next, "owner@example.com", true, false);
        native.Record(second, BoundaryEvent.CallReturn);
        native.Record(second, BoundaryEvent.Usable);
        native.Disarm(second);
        var snapshot = native.Snapshot(2);
        if (!snapshot.Complete || snapshot.Records.Single(r => r.Event == BoundaryEvent.HandlerExit &&
                r.Span == handler.Id).Request != first.Sequence)
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY exit borrowed a newer header or lost an immutable span.");
    });

    private static void CheckIncompleteAndOverflow(string root)
    {
        WithTempMap(root, (native, _) =>
        {
            native.Arm(1, 1, "report@example.com", 0, 0, Guid.NewGuid());
            native.Arm(2, 1, "report@example.com", 0, 0, Guid.NewGuid());
            if (!native.Snapshot(2).Faults.HasFlag(BoundaryFault.Overlap))
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY overlapping requests were accepted.");
        });
        WithTempMap(root, (native, _) =>
        {
            native.Reserve(default, BoundaryEvent.Unrelated);
            var snapshot = native.Snapshot(0);
            if (snapshot.Complete || snapshot.IncompleteRecords != 1)
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY uncommitted record fabricated completeness.");
        });
        WithTempMap(root, (native, _) =>
        {
            for (var i = 0; i < 513; i++) native.Record(default, BoundaryEvent.Unrelated);
            var snapshot = native.Snapshot(0);
            if (snapshot.Complete || !snapshot.Faults.HasFlag(BoundaryFault.Overflow))
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY overflow fabricated completeness.");
        });
    }

    private static void CheckMissingCalls(string root) => WithTempMap(root, (native, app) =>
    {
        var request = native.Arm(1, 1, "report@example.com", 0, 0, Guid.NewGuid());
        var handler = app.BeginHandler("report@example.com", 1);
        var dispatch = app.BeginDispatch(handler, "report@example.com", true);
        app.End(dispatch);
        app.End(handler);
        app.Layout(handler, "report@example.com", true, false);
        native.Record(request, BoundaryEvent.Usable);
        native.Disarm(request);
        if (native.Snapshot(1).Complete)
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY missing native call records fabricated completeness.");
    });

    private static void CheckMappingFaults(string root)
    {
        try
        {
            using var invalid = BoundaryMap.OpenApplication("Local\\missing-" + Guid.NewGuid().ToString("N"), BoundaryClock.Current);
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY missing mapping silently succeeded.");
        }
        catch (FileNotFoundException) { }
        var name = "Local\\temp-native-" + Guid.NewGuid().ToString("N");
        using var map = BoundaryMap.Create(Path.Combine(root, Guid.NewGuid() + ".raw.log"), name,
            "owner@example.com", "report@example.com", BoundaryClock.Current);
        if (map.Snapshot(0).Complete)
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY missing application initialization was accepted.");
        map.Dispose();
        try { map.Record(default, BoundaryEvent.Unrelated); throw new InvalidOperationException("Disposed write succeeded."); }
        catch (ObjectDisposedException) { }
        try { map.Snapshot(0); throw new InvalidOperationException("Disposed snapshot succeeded."); }
        catch (ObjectDisposedException) { }
    }

    private static void CheckLayoutAndAction(string root)
    {
        WithTempMap(root, (native, app) =>
        {
            native.Arm(1, 1, "report@example.com", 0, 0, Guid.NewGuid());
            if (app.BeginHandler("report@example.com", 2).Valid)
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY wrong event action obtained a request span.");
        });
        WithTempMap(root, (native, app) =>
        {
            var request = native.Arm(1, 1, "report@example.com", 0, 0, Guid.NewGuid());
            native.Record(request, BoundaryEvent.CallEnter);
            var handler = app.BeginHandler("report@example.com", 1);
            var dispatch = app.BeginDispatch(handler, "report@example.com", true);
            app.End(dispatch);
            app.End(handler);
            app.Layout(handler, "owner@example.com", true, false);
            native.Record(request, BoundaryEvent.CallReturn);
            native.Record(request, BoundaryEvent.Usable);
            native.Disarm(request);
            var snapshot = native.Snapshot(1);
            if (snapshot.Complete || snapshot.Records.Any(r => r.Event == BoundaryEvent.LayoutObservation))
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY a different owner's layout fabricated completion.");
        });
        var name = "Local\\temp-native-" + Guid.NewGuid().ToString("N");
        using var map = BoundaryMap.Create(Path.Combine(root, Guid.NewGuid() + ".raw.log"), name,
            "owner@example.com", "report@example.com", BoundaryClock.Current);
        using (var raw = MemoryMappedFile.OpenExisting(name))
        using (var view = raw.CreateViewAccessor())
            view.Write(512, 999);
        try { using var corrupt = BoundaryMap.OpenApplication(name, BoundaryClock.Current); throw new InvalidOperationException("Corrupt map read succeeded."); }
        catch (InvalidDataException) { }
        if (map.Snapshot(0).Complete)
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY corrupt initialization fabricated completeness.");
    }

    private static void CheckFailureCapture(string root)
    {
        var priorDiagnostics = diagnostics;
        var output = Path.Combine(root, "failure-output");
        Directory.CreateDirectory(output);
        diagnostics = Path.Combine(output, "scenario.log");
        try
        {
            using (var session = new TempBoundarySession())
            {
                var original = new TimeoutException("Original structural timeout, not a product timing claim.");
                try
                {
                    RunTempNativeBoundary<int>(session, () => throw original);
                    throw new InvalidOperationException("Original failure disappeared.");
                }
                catch (TimeoutException observed)
                {
                    if (!ReferenceEquals(original, observed) || observed.StackTrace?.Contains(nameof(CheckFailureCapture), StringComparison.Ordinal) != true)
                        throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY capture replaced the primary exception/stack.");
                }
            }
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "native-boundary-trace-manifest.json")));
            if (manifest.RootElement.GetProperty("acceptanceEligible").GetBoolean() ||
                manifest.RootElement.GetProperty("traceComplete").GetBoolean() ||
                manifest.RootElement.GetProperty("primaryType").GetString() != typeof(TimeoutException).FullName)
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY partial failure was mislabeled as accepting/complete.");
            var provenance = manifest.RootElement;
            if (!provenance.TryGetProperty("shippingBaseRevision", out var shipping) ||
                shipping.GetString() != "1e86c7a8a3a4c11b3298ab966139c8a356243e86" ||
                !provenance.TryGetProperty("parentTraceRevision", out var parent) ||
                parent.GetString() != "47a0c646e1b16fd7ff6d43a71bfecb3224899d06" ||
                provenance.TryGetProperty("baseRevision", out _) ||
                provenance.GetProperty("eventSchemaVersion").GetInt32() != 2 ||
                provenance.GetProperty("recordFormatVersion").GetInt32() != 1 ||
                provenance.GetProperty("revision").GetString() != Environment.GetEnvironmentVariable("GITHUB_SHA") ||
                provenance.GetProperty("runId").GetString() != Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ||
                provenance.GetProperty("runAttempt").GetString() != Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"))
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY provenance still conflates stale base, parent trace, current run or event schema.");
            var blocked = Path.Combine(root, "blocked-output");
            File.WriteAllText(blocked, "fixture blocks map and sidecar creation");
            diagnostics = Path.Combine(blocked, "scenario.log");
            using var broken = new TempBoundarySession();
            broken.Capture(primary: null);
            if (!broken.HasDiagnosticFailure)
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY mapping/snapshot-output fault silently fell through.");
            var originalResult = RunTempNativeBoundary(broken, () => 17);
            if (originalResult != 17)
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY diagnostic failure truncated a successful original phase.");
        }
        finally
        {
            diagnostics = priorDiagnostics;
            tempNativeBoundarySession = null;
        }
    }

    private static void WriteCompleteTempRequest(BoundaryMap native, BoundaryMap app, long sequence)
    {
        var request = native.Arm(sequence, 1, "report@example.com", 0, 0, Guid.NewGuid());
        native.Record(request, BoundaryEvent.CallEnter);
        var handler = app.BeginHandler("report@example.com", 1);
        var dispatch = app.BeginDispatch(handler, "report@example.com", navigation: true);
        app.End(dispatch);
        app.End(handler);
        app.Layout(handler, "report@example.com", valid: true, superseded: false);
        native.Record(request, BoundaryEvent.CallReturn);
        native.Record(request, BoundaryEvent.Usable);
        native.Disarm(request);
    }

    private static void CheckOwnedProtocolChild(string root)
    {
        var name = "Local\\temp-native-" + Guid.NewGuid().ToString("N");
        using var native = BoundaryMap.Create(Path.Combine(root, Guid.NewGuid() + ".raw.log"), name,
            "owner@example.com", "report@example.com", BoundaryClock.Current);
        var request = native.Arm(1, 1, "report@example.com", 0, 0, Guid.NewGuid());
        native.Record(request, BoundaryEvent.CallEnter);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--temp-native-boundary-child");
        start.ArgumentList.Add(name);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Protocol child did not start.");
        native.BindProduct(child.Id);
        if (!child.WaitForExit(10000))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("Owned no-window protocol child did not exit.");
        }
        native.Record(request, BoundaryEvent.CallReturn);
        native.Record(request, BoundaryEvent.Usable);
        native.Disarm(request);
        if (child.ExitCode != 0 || !native.Snapshot(1).Complete)
            throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY same-host child lost committed protocol data without app-side flush.");
    }

    private static void CheckRunnerClassification(string root)
    {
        foreach (var diagnostic in new[] { false, true })
        {
            var output = Path.Combine(root, diagnostic ? "trace-runner" : "default-runner");
            Directory.CreateDirectory(output);
            var result = Path.Combine(output, "scenarios.json");
            var reached = new List<string>();
            var scenarios = new[]
            {
                new SmokeScenario("first", fixture =>
                {
                    using var early = JsonDocument.Parse(File.ReadAllText(result));
                    var hasClassification = early.RootElement.TryGetProperty("diagnosticOnly", out var classified);
                    if (diagnostic ? !hasClassification || !classified.GetBoolean() ||
                        early.RootElement.GetProperty("acceptanceEligible").GetBoolean() : hasClassification ||
                        early.RootElement.TryGetProperty("acceptanceEligible", out _))
                        throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY runner early/default classification changed.");
                    reached.Add("first");
                }),
                new SmokeScenario("later", _ => reached.Add("later"))
            };
            var runner = diagnostic ? new SmokeScenarioRunner(scenarios, diagnosticOnly: true) : new SmokeScenarioRunner(scenarios);
            if (!runner.Run(Path.Combine(output, "fixtures"), result) || !reached.SequenceEqual(new[] { "first", "later" }))
                throw new InvalidOperationException("TEMP-NATIVE-BOUNDARY runner metadata truncated unchanged later scenarios.");
        }
    }

    private static int RunTempNativeBoundaryChild(string name)
    {
        using var app = BoundaryMap.OpenApplication(name, BoundaryClock.Current);
        var handler = app.BeginHandler("report@example.com", 1);
        var dispatch = app.BeginDispatch(handler, "report@example.com", true);
        app.End(dispatch);
        app.End(handler);
        // Synthetic protocol event ONLY: never a WPF layout/usable observation.
        app.Layout(handler, "report@example.com", true, false);
        Environment.Exit(0); // Proves native capture does not require healthy app flush/disposal.
        return 1;
    }
}
