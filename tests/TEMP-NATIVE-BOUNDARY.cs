// TEMP-NATIVE-BOUNDARY: diagnostic-only shared transport; never shipping ancestry.
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace OrgExplorer.TempNativeBoundary;

[Flags]
internal enum BoundaryFault
{
    None = 0, Clock = 1, UnmatchedIdentity = 2, Overlap = 4, Overflow = 8,
    IncompleteRecord = 16, MissingEvent = 32, Mapping = 64, Initialization = 128
}

internal enum BoundaryEvent
{
    Request = 1, CallEnter, CallReturn, Usable, HandlerEnter, HandlerExit,
    DispatchEnter, DispatchExit, LayoutObservation, Superseded, Unrelated
}

internal readonly record struct BoundaryClock(long Frequency, bool HighResolution)
{
    internal static BoundaryClock Current => new(Stopwatch.Frequency, Stopwatch.IsHighResolution);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly record struct BoundaryRequest(long Sequence, int Action, int Target, int Scope, int Ordinal, Guid NoteId)
{
    internal bool Valid => Sequence > 0;
}

internal readonly record struct BoundarySpan(BoundaryRequest Request, long Id, long Parent, bool Root)
{
    internal bool Valid => Request.Valid && Id > 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
internal struct BoundaryRecord
{
    public long Commit, Timestamp, Request, Span, Parent;
    public BoundaryEvent Event;
    public int Target, Thread, Flags;
    public long Extra;
}

internal readonly record struct BoundaryReservation(int Slot, long Commit);
internal sealed record BoundarySnapshot(bool Complete, BoundaryFault Faults, int Requested,
    int RecordCount, int IncompleteRecords, long NativeFrequency, long AppFrequency,
    int NativePid, int AppPid, BoundaryRecord[] Records)
{
    public bool DiagnosticOnly => true;
    public bool AcceptanceEligible => false;
}

internal sealed class BoundaryMap : IDisposable
{
    internal const int Bytes = 256 * 1024;
    private const long Magic = 0x54454D504E415456;
    private const int NativeCapacity = 512, AppCapacity = 2048, RecordBytes = 64;
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly bool application;
    private readonly string owner, report;
    private long nextSpan;
    private readonly int[] appRequestRecords = new int[43];
    private readonly bool[] handledRequests = new bool[43];
    private bool disposed;

    private BoundaryMap(MemoryMappedFile map, bool application)
    {
        this.map = map;
        this.application = application;
        view = map.CreateViewAccessor(0, Bytes, MemoryMappedFileAccess.ReadWrite);
        try
        {
            if (view.ReadInt64(0) != Magic) throw new InvalidDataException("TEMP-NATIVE-BOUNDARY invalid map header.");
            for (var offset = 0; offset < Bytes; offset += 4096) _ = view.ReadByte(offset);
            owner = ReadTarget(512);
            report = ReadTarget(1024);
        }
        catch { view.Dispose(); throw; }
    }

    internal static BoundaryMap Create(string path, string name, string owner, string report, BoundaryClock clock)
    {
        var map = MemoryMappedFile.CreateFromFile(path, FileMode.CreateNew, name, Bytes);
        try
        {
            using (var view = map.CreateViewAccessor())
            {
                for (var offset = 0; offset < Bytes; offset += 4096) view.Write(offset, (byte)0);
                view.Write(0, Magic);
                view.Write(8, clock.Frequency);
                view.Write(16, clock.HighResolution ? 1 : 0);
                view.Write(24, Environment.ProcessId);
                WriteTarget(view, 512, owner);
                WriteTarget(view, 1024, report);
            }
            return new(map, application: false);
        }
        catch { map.Dispose(); throw; }
    }

    internal static BoundaryMap OpenApplication(string name, BoundaryClock clock)
    {
        var map = MemoryMappedFile.OpenExisting(name);
        try
        {
            var endpoint = new BoundaryMap(map, application: true);
            endpoint.view.Write(40, clock.Frequency);
            endpoint.view.Write(48, clock.HighResolution ? 1 : 0);
            endpoint.view.Write(28, Environment.ProcessId);
            if (clock.Frequency != endpoint.view.ReadInt64(8) ||
                !clock.HighResolution || endpoint.view.ReadInt32(16) != 1)
                endpoint.Fault(BoundaryFault.Clock);
            endpoint.view.Write(20, 1);
            return endpoint;
        }
        catch { map.Dispose(); throw; }
    }

    private static void WriteTarget(MemoryMappedViewAccessor view, int offset, string target)
    {
        var bytes = Encoding.UTF8.GetBytes(target);
        if (bytes.Length is 0 or > 255) throw new ArgumentException("Trace target must fit its fixed identity slot.");
        view.Write(offset, bytes.Length);
        view.WriteArray(offset + 4, bytes, 0, bytes.Length);
    }

    private string ReadTarget(int offset)
    {
        var length = view.ReadInt32(offset);
        if (length is <= 0 or > 255) throw new InvalidDataException("Invalid fixed trace identity.");
        var bytes = new byte[length];
        view.ReadArray(offset + 4, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    internal int Identity(string? exactEmail) => StringComparer.OrdinalIgnoreCase.Equals(owner, exactEmail) ? 1 :
        StringComparer.OrdinalIgnoreCase.Equals(report, exactEmail) ? 2 : 0;
    internal void BindProduct(int processId) => view.Write(56, processId);

    internal void Fault(BoundaryFault fault)
    {
        var offset = application ? 36 : 32;
        view.Write(offset, view.ReadInt32(offset) | (int)fault);
    }

    internal BoundaryRequest Arm(long sequence, int action, string exactTarget, int scope, int ordinal, Guid note)
    {
        var request = new BoundaryRequest(sequence, action, Identity(exactTarget), scope, ordinal, note);
        if (request.Target == 0) { Fault(BoundaryFault.UnmatchedIdentity); return default; }
        var current = ReadRequest();
        if (current.Valid) Fault(BoundaryFault.Overlap);
        var epoch = view.ReadInt64(64);
        view.Write(64, epoch + 1);
        view.Write(72, ref request);
        Thread.MemoryBarrier();
        view.Write(64, epoch + 2);
        Record(request, BoundaryEvent.Request, flags: action);
        return request;
    }

    internal BoundaryRequest ReadRequest()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var epoch = view.ReadInt64(64);
            if ((epoch & 1) != 0) continue;
            view.Read(72, out BoundaryRequest request);
            Thread.MemoryBarrier();
            if (view.ReadInt64(64) == epoch) return request;
        }
        Fault(BoundaryFault.IncompleteRecord);
        return default;
    }

    internal void Disarm(BoundaryRequest captured)
    {
        if (ReadRequest().Sequence != captured.Sequence) { Fault(BoundaryFault.Overlap); return; }
        var epoch = view.ReadInt64(64);
        view.Write(64, epoch + 1);
        var empty = default(BoundaryRequest);
        view.Write(72, ref empty);
        Thread.MemoryBarrier();
        view.Write(64, epoch + 2);
    }

    internal BoundarySpan BeginHandler(string? exactEventIdentity, int expectedAction, long timestamp = 0)
    {
        var entered = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp;
        if ((view.ReadInt32(32) | view.ReadInt32(36)) != 0) return default;
        var request = ReadRequest();
        if (!request.Valid) return default;
        if (request.Target != Identity(exactEventIdentity) || request.Action != expectedAction)
        {
            Fault(BoundaryFault.UnmatchedIdentity);
            return default;
        }
        if (request.Sequence > 42 || handledRequests[(int)request.Sequence])
        {
            Fault(BoundaryFault.Overlap);
            return default;
        }
        handledRequests[(int)request.Sequence] = true;
        var span = new BoundarySpan(request, ++nextSpan, 0, Root: true);
        Record(request, BoundaryEvent.HandlerEnter, span.Id, timestamp: entered);
        return span;
    }

    internal BoundarySpan BeginDispatch(BoundarySpan parent, string? exactPerson, bool navigation, long timestamp = 0)
    {
        var entered = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp;
        if (!parent.Valid || !navigation)
        {
            Record(default, BoundaryEvent.Unrelated, flags: Identity(exactPerson));
            return default;
        }
        if (parent.Request.Target != Identity(exactPerson))
        {
            Fault(BoundaryFault.UnmatchedIdentity);
            return default;
        }
        var span = new BoundarySpan(parent.Request, ++nextSpan, parent.Id, Root: false);
        Record(span.Request, BoundaryEvent.DispatchEnter, span.Id, span.Parent, navigation ? 1 : 0, entered);
        return span;
    }

    internal void End(BoundarySpan captured, long timestamp = 0)
    {
        var exited = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp;
        if (captured.Valid)
            Record(captured.Request, captured.Root ? BoundaryEvent.HandlerExit : BoundaryEvent.DispatchExit,
                captured.Id, captured.Parent, timestamp: exited);
    }

    internal void Layout(BoundarySpan captured, string? actualIdentity, bool valid, bool superseded)
    {
        if (!captured.Valid || !valid) return;
        Record(captured.Request, superseded || Identity(actualIdentity) != captured.Request.Target
                ? BoundaryEvent.Superseded : BoundaryEvent.LayoutObservation,
            captured.Id, captured.Parent);
    }

    internal void Record(BoundaryRequest request, BoundaryEvent kind, long span = 0, long parent = 0, int flags = 0, long timestamp = 0)
    {
        var reservation = Reserve(request, kind, span, parent, flags, timestamp);
        if (reservation.Slot >= 0) Commit(reservation);
    }

    internal BoundaryReservation Reserve(BoundaryRequest request, BoundaryEvent kind, long span = 0,
        long parent = 0, int flags = 0, long timestamp = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (view.ReadInt32(264) != 0) return new(-1, 0);
        if (application && request.Valid &&
            (request.Sequence > 42 || ++appRequestRecords[(int)request.Sequence] > 24))
        {
            Fault(BoundaryFault.Overflow);
            return new(-1, 0);
        }
        var counter = application ? 260 : 256;
        var count = view.ReadInt32(counter);
        var capacity = application ? AppCapacity : NativeCapacity;
        if (count >= capacity) { Fault(BoundaryFault.Overflow); return new(-1, 0); }
        var slot = (application ? NativeCapacity : 0) + count;
        view.Write(counter, count + 1);
        var record = new BoundaryRecord
        {
            Timestamp = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp, Request = request.Sequence, Span = span, Parent = parent,
            Event = kind, Target = request.Target, Thread = Environment.CurrentManagedThreadId, Flags = flags
        };
        view.Write(4096 + slot * RecordBytes, ref record);
        return new(slot, count + 1);
    }

    internal void Commit(BoundaryReservation reservation)
    {
        Thread.MemoryBarrier();
        view.Write(4096 + reservation.Slot * RecordBytes, reservation.Commit);
    }

    internal void Freeze() => view.Write(264, 1);

    internal BoundarySnapshot Snapshot(int expectedRequests)
    {
        var records = new List<BoundaryRecord>();
        var incomplete = 0;
        var faults = (BoundaryFault)(view.ReadInt32(32) | view.ReadInt32(36));
        if (view.ReadInt32(20) != 1) faults |= BoundaryFault.Initialization;
        if (view.ReadInt32(56) is var expectedPid && expectedPid != 0 && view.ReadInt32(28) != expectedPid)
            faults |= BoundaryFault.Initialization;
        if (view.ReadInt64(8) != view.ReadInt64(40) ||
            view.ReadInt32(16) != 1 || view.ReadInt32(48) != 1)
            faults |= BoundaryFault.Clock;
        foreach (var app in new[] { false, true })
        {
            var capacity = app ? AppCapacity : NativeCapacity;
            var count = view.ReadInt32(app ? 260 : 256);
            if (count > capacity || count < 0) { faults |= BoundaryFault.Overflow; count = Math.Clamp(count, 0, capacity); }
            for (var i = 0; i < count; i++)
            {
                var offset = 4096 + ((app ? NativeCapacity : 0) + i) * RecordBytes;
                var first = view.ReadInt64(offset);
                view.Read(offset, out BoundaryRecord record);
                Thread.MemoryBarrier();
                if (first != i + 1 || record.Commit != first || view.ReadInt64(offset) != first) { incomplete++; continue; }
                records.Add(record);
            }
        }
        if (incomplete > 0) faults |= BoundaryFault.IncompleteRecord;
        var requested = records.Where(r => r.Event == BoundaryEvent.Request).ToArray();
        if (requested.Length != expectedRequests || requested.Select(r => r.Request).Distinct().Count() != expectedRequests)
            faults |= BoundaryFault.MissingEvent;
        foreach (var request in requested)
        {
            var own = records.Where(r => r.Request == request.Request).ToArray();
            if (own.Any(r => r.Target != request.Target)) faults |= BoundaryFault.UnmatchedIdentity;
            if (!own.Any(r => r.Event == BoundaryEvent.CallEnter) || !own.Any(r => r.Event == BoundaryEvent.CallReturn) ||
                !own.Any(r => r.Event == BoundaryEvent.Usable) ||
                !own.Any(r => r.Event == BoundaryEvent.LayoutObservation) ||
                !own.Any(r => r.Event == BoundaryEvent.HandlerEnter) ||
                !own.Any(r => r.Event == BoundaryEvent.DispatchEnter))
                faults |= BoundaryFault.MissingEvent;
            foreach (var begin in own.Where(r => r.Event is BoundaryEvent.HandlerEnter or BoundaryEvent.DispatchEnter))
                if (!own.Any(r => r.Span == begin.Span && r.Timestamp >= begin.Timestamp && r.Event ==
                    (begin.Event == BoundaryEvent.HandlerEnter ? BoundaryEvent.HandlerExit : BoundaryEvent.DispatchExit)))
                    faults |= BoundaryFault.MissingEvent;
            var handlerEnd = own.Where(r => r.Event == BoundaryEvent.HandlerExit).Select(r => r.Timestamp).DefaultIfEmpty(long.MaxValue).Max();
            if (!own.Any(r => r.Event == BoundaryEvent.LayoutObservation && r.Timestamp >= handlerEnd))
                faults |= BoundaryFault.MissingEvent;
        }
        return new(faults == BoundaryFault.None, faults, requested.Length, records.Count, incomplete,
            view.ReadInt64(8), view.ReadInt64(40), view.ReadInt32(24), view.ReadInt32(28), records.ToArray());
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        view.Dispose();
        map.Dispose();
    }
}
