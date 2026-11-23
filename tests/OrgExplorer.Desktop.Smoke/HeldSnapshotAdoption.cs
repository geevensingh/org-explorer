using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal sealed record SnapshotAdoptionActions(Action ShowEditor, Action EnterDraft,
    Action SelectPerson, Action ObservePerson);

internal sealed record SnapshotAdoptionCommitState(Guid OperationId, int ProcessId,
    WorkspaceStoragePhaseKind Kind, long KnownTransactionAt, long HoldEndedAt = 0, long CompletedAt = 0,
    bool HoldExpired = false, long TimestampFrequency = 0)
{
    public bool Held => TimestampFrequency == Stopwatch.Frequency && HoldEndedAt == 0 && CompletedAt == 0 &&
        Stopwatch.GetElapsedTime(KnownTransactionAt) is var elapsed && elapsed >= TimeSpan.Zero &&
        elapsed < SnapshotAdoptionCommitControl.HoldBudget;
}

internal sealed class SnapshotAdoptionCommitControl : IWorkspaceFileObserver, IDisposable
{
    internal static readonly TimeSpan HoldBudget = TimeSpan.FromSeconds(1);
    internal string ArmName { get; }
    internal string ReleaseName { get; }
    private readonly EventWaitHandle arm, release;
    private readonly EventWaitHandle fault;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor state;
    private SnapshotAdoptionCommitState? active;
    private int sequence;

    internal SnapshotAdoptionCommitControl(string? armName = null, string? releaseName = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The owned WPF adoption fixture requires Windows events.");
        if ((armName is null) != (releaseName is null)) throw new ArgumentException("Supply both owned event names or neither.");
        var token = Guid.NewGuid().ToString("N");
        ArmName = armName ?? @"Local\OrgExplorer-adoption-arm-" + token;
        ReleaseName = releaseName ?? @"Local\OrgExplorer-adoption-release-" + token;
        arm = armName is null ? new(false, EventResetMode.ManualReset, ArmName) : EventWaitHandle.OpenExisting(ArmName);
        release = releaseName is null ? new(false, EventResetMode.ManualReset, ReleaseName) : EventWaitHandle.OpenExisting(ReleaseName);
        fault = armName is null ? new(false, EventResetMode.ManualReset, ArmName + "-fault") : EventWaitHandle.OpenExisting(ArmName + "-fault");
        mapping = armName is null ? MemoryMappedFile.CreateNew(ArmName + "-state", 256) :
            MemoryMappedFile.OpenExisting(ArmName + "-state");
        state = mapping.CreateViewAccessor();
    }

    internal void Arm()
    {
        if (state.ReadInt32(0) != 0) throw new InvalidOperationException("The owned adoption observation already exists.");
        release.Reset();
        arm.Set();
    }
    internal void Release() => release.Set();
    internal SnapshotAdoptionCommitState? Read()
    {
        if (fault.WaitOne(0)) throw new InvalidOperationException("The owned adoption observation failed; storage outcome is separate.");
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var before = state.ReadInt32(0);
            if (before == 0) return null;
            if (before < 0 || (before & 1) != 0)
                throw new InvalidOperationException("The owned adoption commit sequence is invalid.");
            var slot = 8 + ((before / 2) % 2) * 64;
            var identity = new byte[16];
            state.ReadArray(slot + 4, identity, 0, identity.Length);
            var observed = new SnapshotAdoptionCommitState(new(identity), state.ReadInt32(slot),
                (WorkspaceStoragePhaseKind)state.ReadInt32(slot + 48), state.ReadInt64(slot + 24), state.ReadInt64(slot + 32),
                state.ReadInt64(slot + 40), state.ReadInt32(slot + 52) != 0, state.ReadInt64(slot + 56));
            Thread.MemoryBarrier();
            if (state.ReadInt32(0) != before) continue;
            if (observed.OperationId == Guid.Empty || observed.ProcessId <= 0 ||
                observed.TimestampFrequency != Stopwatch.Frequency || observed.KnownTransactionAt <= 0 ||
                !Enum.IsDefined(observed.Kind) ||
                observed.HoldEndedAt != 0 && observed.HoldEndedAt < observed.KnownTransactionAt ||
                observed.CompletedAt != 0 && observed.CompletedAt < observed.KnownTransactionAt ||
                observed.CompletedAt != 0 && observed.HoldEndedAt > observed.CompletedAt ||
                observed.Kind == WorkspaceStoragePhaseKind.Begin && observed.CompletedAt != 0 ||
                observed.Kind == WorkspaceStoragePhaseKind.End && (observed.HoldEndedAt == 0 || observed.CompletedAt == 0))
                throw new InvalidOperationException("The owned adoption record has invalid identity/clock/lifetime state.");
            return observed;
        }
        throw new InvalidOperationException("The bounded adoption observation could not obtain a complete committed record.");
    }
    public void Observe(WorkspaceFileEffect effect) { }
    public void ObservePhase(WorkspaceStoragePhase phase)
    {
        if (phase.Name != "snapshot.adopt-commit") return;
        if (phase.Kind == WorkspaceStoragePhaseKind.Begin && arm.WaitOne(0))
        {
            arm.Reset();
            var start = Stopwatch.GetTimestamp();
            active = new(phase.Id, Environment.ProcessId, phase.Kind, start, TimestampFrequency: Stopwatch.Frequency);
            Write();
            var remaining = HoldBudget - Stopwatch.GetElapsedTime(start);
            var released = release.WaitOne(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            active = active with { HoldEndedAt = Stopwatch.GetTimestamp(), HoldExpired = !released };
            Write();
        }
        else if (active?.OperationId == phase.Id && phase.Kind != WorkspaceStoragePhaseKind.Begin)
        {
            active = active with { Kind = phase.Kind, CompletedAt = Stopwatch.GetTimestamp() };
            Write();
        }
    }
    private void Write()
    {
        try
        {
            var value = active!;
            var committed = sequence += 2;
            var slot = 8 + ((committed / 2) % 2) * 64;
            // The previous committed slot remains readable until this complete payload is published.
            state.Write(slot, value.ProcessId);
            state.WriteArray(slot + 4, value.OperationId.ToByteArray(), 0, 16);
            state.Write(slot + 24, value.KnownTransactionAt);
            state.Write(slot + 32, value.HoldEndedAt);
            state.Write(slot + 40, value.CompletedAt);
            state.Write(slot + 48, (int)value.Kind);
            state.Write(slot + 52, value.HoldExpired ? 1 : 0);
            state.Write(slot + 56, value.TimestampFrequency);
            Thread.MemoryBarrier();
            state.Write(0, committed);
        }
        catch (Exception error)
        {
            fault.Set();
            Console.Error.WriteLine($"ADOPTION-OBSERVATION-FAULT type={error.GetType().Name}; hresult=0x{error.HResult:X8}");
        }
    }
    public void Dispose() { state.Dispose(); mapping.Dispose(); fault.Dispose(); arm.Dispose(); release.Dispose(); }
}

internal sealed record SnapshotAdoptionMilestone(string Stage, long Timestamp, double ElapsedMs,
    Guid? OperationId, bool CommitPhaseHeld, bool CommitCompleted);

internal sealed class SnapshotAdoptionObservation
{
    public List<SnapshotAdoptionMilestone> Milestones { get; } = [];
    public long TimestampFrequency { get; } = Stopwatch.Frequency;
    public string Mechanism { get; } = "Injected external-effect gate at the real admitted snapshot.adopt-commit phase, BEFORE SQLite Commit invocation. Not a SQLite engine waiting on an external reader.";
    public double HoldBudgetMs { get; } = SnapshotAdoptionCommitControl.HoldBudget.TotalMilliseconds;
    public string Outcome { get; set; } = "Interrupted";
    public string? ErrorType { get; set; }
    public string? Error { get; set; }
    public bool DiagnosticsComplete { get; set; } = true;
    public string? DiagnosticErrorType { get; set; }
}

internal static class HeldSnapshotAdoption
{
    internal static SnapshotAdoptionObservation Run(SnapshotAdoptionCommitControl control,
        Func<SnapshotAdoptionActions> prepareActions, Action requestSync, Action observeAdopting,
        Action capture, Action<SnapshotAdoptionObservation> report)
    {
        var observation = new SnapshotAdoptionObservation();
        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        Guid? operation = null;
        void Record(string stage)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var state = control.Read();
            observation.Milestones.Add(new(stage, timestamp, Stopwatch.GetElapsedTime(started, timestamp).TotalMilliseconds,
                state?.OperationId, state?.Held == true, state?.Kind == WorkspaceStoragePhaseKind.End && state.CompletedAt != 0));
        }
        void Step(string name, Action action)
        {
            Record(name + "-started");
            try { action(); Record(name + "-completed"); }
            catch { Record(name + "-failed"); throw; }
        }
        void RequireSameHeld()
        {
            var state = control.Read();
            if (state?.OperationId != operation || state?.Held != true)
                throw new InvalidOperationException("Native browsing/draft admission outlasted the actual held adoption Commit phase.");
        }
        try
        {
            SnapshotAdoptionActions? actions = null;
            Step("prepare-controls", () => actions = prepareActions());
            control.Arm();
            Step("sync-request", requestSync);
            Step("adopting-observed", observeAdopting);
            var state = control.Read();
            operation = state?.OperationId;
            if (operation is null) throw new InvalidOperationException("The actual adoption Commit operation was not observed.");
            RequireSameHeld();
            Step("show-editor", actions!.ShowEditor);
            Step("enter-draft", actions.EnterDraft);
            Step("select-person", actions.SelectPerson);
            Step("destination-observed", actions.ObservePerson);
            Record("held-verdict");
            RequireSameHeld();
            Step("capture", capture);
            RequireSameHeld();
            Step("release-commit", control.Release);
            observation.Outcome = "Passed";
            return observation;
        }
        catch (Exception error)
        {
            failure = error;
            observation.Outcome = "Failed";
            observation.ErrorType = error.GetType().Name;
            observation.Error = error.Message;
            throw;
        }
        finally
        {
            control.Release();
            Exception? reportingError = null;
            try { Record("outcome-observed"); }
            catch (Exception error) { reportingError = error; }
            observation.DiagnosticsComplete = reportingError is null;
            observation.DiagnosticErrorType = reportingError?.GetType().Name;
            try { report(observation); }
            catch (Exception error) { reportingError ??= error; }
            if (reportingError is not null)
            {
                if (failure is not null)
                {
                    failure.Data["AdoptionObservationFailure"] = reportingError;
                    Console.Error.WriteLine($"ADOPTION-REPORT-FAULT type={reportingError.GetType().Name}; hresult=0x{reportingError.HResult:X8}");
                }
                else ExceptionDispatchInfo.Capture(reportingError).Throw();
            }
        }
    }
}
