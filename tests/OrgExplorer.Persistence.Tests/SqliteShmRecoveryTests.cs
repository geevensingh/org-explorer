using System.IO.MemoryMappedFiles;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed partial class SqliteWorkspaceTests
{
    private static byte[] ShmRecoveryPhoto => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");

    [Fact]
    public async Task Abrupt_committed_profile_recovers_while_the_original_external_SHM_section_remains_mapped()
        => await AssertAbruptCommitRecovery("after", "profile", false, false, retainShmMapping: true);

    [Theory]
    [InlineData("before", "profile")]
    [InlineData("after", "Person")]
    [InlineData("after", "Team")]
    public async Task Interrupted_writing_recovers_its_exact_commit_truth_with_the_original_SHM_section_mapped(
        string boundary, string mutation)
        => await AssertAbruptCommitRecovery(boundary, mutation, false, false,
            retainShmMapping: true, retainResources: true);

    [Fact]
    public async Task Stale_SHM_index_cannot_hide_later_committed_profile_or_retained_resources_after_process_death()
        => await AssertAbruptCommitRecovery("after", "profile", false, false,
            retainShmMapping: true, restoreStaleShmIndex: true, retainResources: true);

    [Theory]
    [InlineData("workspace.sqlite")]
    [InlineData("workspace.sqlite-wal")]
    public async Task Stale_SHM_recovery_preserves_committed_bytes_with_a_writable_file_handle_retained(string file)
        => await AssertAbruptCommitRecovery("after", "profile", false, false,
            retainShmMapping: true, restoreStaleShmIndex: true, retainResources: true,
            retainedCommittedFile: file, inspectCommittedWal: file == "workspace.sqlite-wal");

    [Theory]
    [InlineData("before", "profile")]
    [InlineData("after", "profile")]
    [InlineData("after", "Person")]
    [InlineData("after", "Team")]
    public async Task Original_raw_WAL_commit_truth_matches_the_public_crash_recovery_revision(string boundary, string mutation)
        => await AssertAbruptCommitRecovery(boundary, mutation, false, false,
            retainShmMapping: true, restoreStaleShmIndex: true, retainResources: true,
            retainedCommittedFile: "workspace.sqlite-wal", inspectCommittedWal: true, requireRawWalTruth: true);

    private sealed record RawCaptureFault(string File, string Status, string Code, int? HResult);
    private sealed record RawWalEvidence(string Status, string Coverage, RawCommittedWalTruth? Truth,
        RawCaptureFault[] CaptureFaults, string? ErrorType = null, int? ErrorHResult = null);
    private sealed record RawCommittedWalTruth(Guid WorkspaceIdentity, Guid DatabaseRevision, Guid? CommittedRevision,
        int PageSize, int WalBytes, int ValidFrames, int LastCommitFrame, int CommittedDatabasePages,
        bool HeaderValid, string WalState, string TailBoundary, string DatabaseSha256, string WalSha256);
    private sealed class RawCoverageException(string code) : NotSupportedException(code) { }
    private sealed record WalIndexHeader(uint Version, uint Changes, byte Initialized, byte WalChecksumBigEndian,
        int PageSize, uint CommittedFrames, uint DatabasePages, bool HeaderChecksumValid);
    private sealed record WalIndexObservation(string Status, bool NonAtomic, string NativeByteOrder,
        bool? HeaderCopiesMatch, WalIndexHeader? First, WalIndexHeader? Second,
        string? ErrorType = null, int? ErrorHResult = null);
    private sealed record NativeSqliteIdentity(string Status, int? VersionNumber, string? SourceId,
        string? ErrorType = null, int? ErrorHResult = null);

    private static WalIndexObservation CaptureWalIndex(OwnedShmMapping? mapping)
    {
        var order = BitConverter.IsLittleEndian ? "LittleEndian" : "BigEndian";
        if (mapping is null) return new("Absent", true, order, null, null, null);
        try
        {
            // Observe only this already-held view, without a barrier, SQLite reader or lock probe.
            var bytes = mapping.ReadIndex();
            if (bytes.Length < 136) return new("UnsupportedLength", true, order, null, null, null);
            var first = Header(0);
            var second = Header(48);
            return new(first.Version == 3007000 && second.Version == 3007000 ? "Observed" : "UnsupportedVersion",
                true, order, bytes.AsSpan(0, 48).SequenceEqual(bytes.AsSpan(48, 48)), first, second);

            WalIndexHeader Header(int offset)
            {
                uint Native32(int relative) => BitConverter.IsLittleEndian
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + relative, 4))
                    : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + relative, 4));
                var pageSize = BitConverter.IsLittleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 14, 2))
                    : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 14, 2));
                var sum = RawWalChecksum(bytes.AsSpan(offset, 40), (0u, 0u), BitConverter.IsLittleEndian);
                return new(Native32(0), Native32(8), bytes[offset + 12], bytes[offset + 13],
                    pageSize == 1 ? 65536 : pageSize, Native32(16), Native32(20),
                    sum.First == Native32(40) && sum.Second == Native32(44));
            }
        }
        catch (Exception fault) when (fault is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException or Xunit.Sdk.XunitException)
        {
            return new("ObservationError", true, order, null, null, null, fault.GetType().Name, fault.HResult);
        }
    }

    private static NativeSqliteIdentity CaptureNativeSqliteIdentity()
    {
        try
        {
            var version = SQLitePCL.raw.sqlite3_libversion_number();
            var source = SQLitePCL.raw.sqlite3_sourceid().utf8_to_string();
            if (version <= 0 || string.IsNullOrEmpty(source) || source.Length > 128)
                return new("Unavailable", version, null);
            return new("Observed", version, source);
        }
        catch (Exception fault) when (fault is InvalidOperationException or NotSupportedException or
            DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            return new("ObservationError", null, null, fault.GetType().Name, fault.HResult);
        }
    }

    private static RawWalEvidence CaptureRawWalEvidence(Dictionary<string, byte[]> files, List<RawCaptureFault> faults)
    {
        const string coverage = "WalV3007000ChecksumsCommittedPrefix;Utf8NonOverflowSchemaWorkspace";
        if (faults.Count != 0) return new("CaptureIncomplete", coverage, null, faults.ToArray());
        if (!files.TryGetValue("workspace.sqlite", out var database) || !files.TryGetValue("workspace.sqlite-wal", out var wal))
            return new("MissingCapture", coverage, null, []);
        try
        {
            var truth = InspectRawCommittedWal(database, wal);
            var status = truth.WalState == "UnsupportedVersion" ? "Unsupported" :
                wal.Length == 0 || truth.HeaderValid ? "Complete" : "InvalidWalHeader";
            return new(status, coverage, truth, []);
        }
        catch (RawCoverageException fault)
        {
            return new("Unsupported", coverage + ";Limit=" + fault.Message, null, [], fault.GetType().Name, fault.HResult);
        }
        catch (Exception fault) when (fault is InvalidDataException or ArgumentException or OverflowException or
            IndexOutOfRangeException or InvalidOperationException)
        {
            return new("InvalidInput", coverage, null, [], fault.GetType().Name, fault.HResult);
        }
    }

    private static RawCommittedWalTruth InspectRawCommittedWal(byte[] database, byte[] wal)
    {
        // Temporary independent file-format inspection, not another SQLite connection or backup.
        if (database.Length < 100 || !database.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("The original raw database header is invalid.");
        var pageSize = BinaryPrimitives.ReadUInt16BigEndian(database.AsSpan(16, 2));
        var size = pageSize == 1 ? 65536 : pageSize;
        if (size < 512 || (size & (size - 1)) != 0 || database.Length % size != 0)
            throw new InvalidDataException("The original database page size or length is invalid.");
        var basePages = database.Length / size;
        var baseState = ReadRawWorkspace(database, size, basePages, new());
        var committedPages = new Dictionary<uint, ReadOnlyMemory<byte>>();
        var pendingPages = new Dictionary<uint, ReadOnlyMemory<byte>>();
        var committedSize = basePages;
        var validFrames = 0;
        var lastCommit = 0;
        var headerValid = false;
        var tail = wal.Length == 0 ? "empty" : "header";
        if (wal.Length >= 32)
        {
            var magic = RawBigEndian(wal, 0);
            var littleChecksum = magic == 0x377f0682;
            if ((littleChecksum || magic == 0x377f0683) && RawBigEndian(wal, 4) != 3007000)
                tail = "unsupported-version";
            var checksum = (First: 0u, Second: 0u);
            if ((littleChecksum || magic == 0x377f0683) && RawBigEndian(wal, 4) == 3007000 &&
                RawBigEndian(wal, 8) == size)
            {
                checksum = RawWalChecksum(wal.AsSpan(0, 24), checksum, littleChecksum);
                headerValid = checksum.First == RawBigEndian(wal, 24) && checksum.Second == RawBigEndian(wal, 28);
            }
            if (headerValid)
            {
                tail = "end";
                var salt1 = RawBigEndian(wal, 16);
                var salt2 = RawBigEndian(wal, 20);
                var offset = 32;
                for (; offset + 24 + size <= wal.Length; offset += 24 + size)
                {
                    if (RawBigEndian(wal, offset + 8) != salt1 || RawBigEndian(wal, offset + 12) != salt2)
                    { tail = "salt"; break; }
                    var next = RawWalChecksum(wal.AsSpan(offset, 8), checksum, littleChecksum);
                    next = RawWalChecksum(wal.AsSpan(offset + 24, size), next, littleChecksum);
                    if (next.First != RawBigEndian(wal, offset + 16) || next.Second != RawBigEndian(wal, offset + 20))
                    { tail = "checksum"; break; }
                    var page = RawBigEndian(wal, offset);
                    if (page == 0) throw new InvalidDataException("A valid WAL frame names page zero.");
                    checksum = next;
                    validFrames++;
                    pendingPages[page] = wal.AsMemory(offset + 24, size);
                    var pagesAfterCommit = RawBigEndian(wal, offset + 4);
                    if (pagesAfterCommit != 0)
                    {
                        committedSize = checked((int)pagesAfterCommit);
                        committedPages = new(pendingPages);
                        lastCommit = validFrames;
                    }
                }
                if (tail == "end" && offset != wal.Length) tail = "partial-frame";
            }
        }
        var committed = ReadRawWorkspace(database, size, committedSize, committedPages);
        if (committed.Identity != baseState.Identity)
            throw new InvalidDataException("The raw committed Workspace identity changed.");
        var walState = wal.Length == 0 ? "Empty" : tail == "unsupported-version" ? "UnsupportedVersion" :
            !headerValid ? "InvalidHeader" :
            lastCommit == 0 ? "NoValidCommittedWalFrame" : "ValidCommittedPrefix";
        return new(committed.Identity, baseState.Revision,
            wal.Length == 0 || headerValid ? committed.Revision : null, size, wal.Length,
            validFrames, lastCommit, committedSize, headerValid, walState, tail,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(database)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wal)));
    }

    private static uint RawBigEndian(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private static (uint First, uint Second) RawWalChecksum(ReadOnlySpan<byte> bytes,
        (uint First, uint Second) sum, bool littleEndian)
    {
        if (bytes.Length % 8 != 0) throw new InvalidDataException("The WAL checksum input is not aligned.");
        for (var offset = 0; offset < bytes.Length; offset += 8)
        {
            var first = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)) :
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            var second = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4)) :
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4));
            sum.First = unchecked(sum.First + first + sum.Second);
            sum.Second = unchecked(sum.Second + second + sum.First);
        }
        return sum;
    }

    private static (Guid Identity, Guid Revision) ReadRawWorkspace(byte[] database, int pageSize, int databasePages,
        Dictionary<uint, ReadOnlyMemory<byte>> overlay)
    {
        ReadOnlyMemory<byte> Page(uint number)
        {
            if (number == 0 || number > databasePages)
                throw new InvalidDataException("The raw table references a page outside its committed database.");
            return overlay.TryGetValue(number, out var page) ? page :
                database.AsMemory(checked(((int)number - 1) * pageSize), pageSize);
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(Page(1).Span.Slice(56, 4)) != 1)
            throw new RawCoverageException("Utf8Only");
        var schema = Rows(1, new()).Single(row => row.Values.Length == 5 &&
            row.Values[0] is string type && type == "table" && row.Values[1] is string name && name == "workspace");
        if (schema.Values[3] is not long root || root <= 0 || root > uint.MaxValue)
            throw new InvalidDataException("The raw Workspace root page is invalid.");
        var rootPage = (uint)root;
        var workspace = Rows(rootPage, new()).Single(row => row.RowId == 1);
        if (workspace.Values.Length != 4 || workspace.Values[1] is not long version)
            throw new InvalidDataException("The original raw Workspace row shape is invalid.");
        if (version != 3) throw new RawCoverageException("WorkspaceFormat3Only");
        if (workspace.Values[2] is not string identity || !Guid.TryParseExact(identity, "D", out var id) ||
            workspace.Values[3] is not string revision || !Guid.TryParseExact(revision, "D", out var head))
            throw new InvalidDataException("The original raw Workspace row is invalid.");
        return (id, head);

        IEnumerable<(ulong RowId, object?[] Values)> Rows(uint number, HashSet<uint> visited)
        {
            if (!visited.Add(number)) throw new InvalidDataException("The raw table has a cycle.");
            if (visited.Count > 2048) throw new RawCoverageException("TablePageLimit");
            var page = Page(number).ToArray();
            var header = number == 1 ? 100 : 0;
            var kind = page[header];
            if (kind is not (5 or 13)) throw new InvalidDataException("The raw table is not an integer-key b-tree.");
            var cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(header + 3, 2));
            var headerSize = kind == 5 ? 12 : 8;
            for (var index = 0; index < cells; index++)
            {
                var cell = (int)BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(header + headerSize + index * 2, 2));
                if (kind == 5)
                {
                    foreach (var row in Rows(RawBigEndian(page, cell), visited)) yield return row;
                }
                else
                {
                    var payloadBytes = checked((int)RawVarint(page, ref cell));
                    var rowId = RawVarint(page, ref cell);
                    var usable = pageSize - database[20];
                    if (payloadBytes > usable - 35 || cell + payloadBytes > usable)
                        throw new RawCoverageException("NonOverflowRecordsOnly");
                    yield return (rowId, RawRecord(page.AsSpan(cell, payloadBytes)));
                }
            }
            if (kind == 5)
                foreach (var row in Rows(RawBigEndian(page, header + 8), visited)) yield return row;
        }
    }

    private static ulong RawVarint(byte[] bytes, ref int offset)
    {
        ulong value = 0;
        for (var index = 0; index < 8; index++)
        {
            var next = bytes[offset++];
            value = (value << 7) | (uint)(next & 127);
            if (next < 128) return value;
        }
        return (value << 8) | bytes[offset++];
    }

    private static object?[] RawRecord(ReadOnlySpan<byte> payload)
    {
        var bytes = payload.ToArray();
        var offset = 0;
        var headerBytes = checked((int)RawVarint(bytes, ref offset));
        var types = new List<ulong>();
        while (offset < headerBytes) types.Add(RawVarint(bytes, ref offset));
        if (offset != headerBytes || headerBytes > bytes.Length)
            throw new InvalidDataException("The raw record header is invalid.");
        var values = new List<object?>();
        foreach (var type in types)
        {
            if (type == 0) { values.Add(null); continue; }
            if (type is 8 or 9) { values.Add(type == 8 ? 0L : 1L); continue; }
            var length = type switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 6, 6 or 7 => 8,
                >= 12 => checked((int)((type - 12) / 2)), _ => throw new InvalidDataException("Reserved raw record type.") };
            var data = bytes.AsSpan(offset, length);
            offset += length;
            if (type >= 12)
                values.Add((type & 1) != 0 ? Encoding.UTF8.GetString(data) : data.ToArray());
            else if (type == 7)
                values.Add(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data)));
            else
            {
                long integer = (sbyte)data[0];
                for (var index = 1; index < data.Length; index++) integer = unchecked((integer << 8) | data[index]);
                values.Add(integer);
            }
        }
        if (offset != bytes.Length) throw new InvalidDataException("The raw record payload length is inconsistent.");
        return values.ToArray();
    }

    private sealed record CrashFileIdentity(string? VolumeSerial, string? FileId, int? NativeError, string? CaptureFault);
    private sealed record CrashCommittedFileObservation(string Stage, string File, string Sha256, CrashFileIdentity Identity);
    private sealed record ShmHeldResources(Guid Identity, long Capacity, bool FileOpen, bool MappingOpen, bool ViewOpen);

    [StructLayout(LayoutKind.Sequential)]
    private struct CrashNativeFileId
    {
        internal ulong VolumeSerial;
        internal ulong Low;
        internal ulong High;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadCrashFileIdentity(SafeFileHandle file, int informationClass,
        out CrashNativeFileId information, uint size);

    private static CrashFileIdentity CaptureCrashFileIdentity(FileStream file)
    {
        const int fileIdInfo = 18;
        try
        {
            // Query only the already-open observation/holder handle; never reopen the path.
            return ReadCrashFileIdentity(file.SafeFileHandle, fileIdInfo, out var id, (uint)Marshal.SizeOf<CrashNativeFileId>())
                ? new(id.VolumeSerial.ToString("X16"), $"{id.High:X16}{id.Low:X16}", null, null)
                : new(null, null, Marshal.GetLastWin32Error(), null);
        }
        catch (Exception fault) when (fault is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new(null, null, null, $"{fault.GetType().Name}; hresult=0x{fault.HResult:X8}");
        }
    }

    private sealed class ShmReadBoundary : IWorkspaceFileObserver, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name != "local-sqlite-read-snapshot" || phase.Kind != WorkspaceStoragePhaseKind.Begin) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The old public SHM snapshot was not released.");
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class OwnedShmMapping : IDisposable
    {
        private readonly FileStream file;
        private readonly MemoryMappedFile mapping;
        private readonly MemoryMappedViewAccessor view;
        private readonly long capacity;
        private readonly Guid identity = Guid.NewGuid();

        internal OwnedShmMapping(string path)
        {
            file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            try
            {
                capacity = file.Length;
                Assert.True(capacity > 0, "The actual writer must have created its SHM before mapping.");
                mapping = MemoryMappedFile.CreateFromFile(file, null, capacity,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                try { view = mapping.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite); }
                catch { mapping.Dispose(); throw; }
            }
            catch { file.Dispose(); throw; }
            Console.WriteLine($"SQLITE-CRASH-STRUCTURAL-MAPPING id={identity}; ownerProcessId={Environment.ProcessId}; file={Path.GetFileName(path)}; capacity={capacity}; role=controlled-external-section-without-SQLite-DMS; historicalHolderAttributed=false");
            AssertHeld();
        }

        internal void AssertHeld()
        {
            Assert.False(file.SafeFileHandle.IsClosed);
            Assert.False(mapping.SafeMemoryMappedFileHandle.IsClosed);
            Assert.False(view.SafeMemoryMappedViewHandle.IsClosed);
            Assert.Equal(capacity, file.Length);
            _ = view.ReadByte(0);
            GC.KeepAlive(mapping);
        }

        internal ShmHeldResources CaptureHeldResources() => new(identity, capacity,
            !file.SafeFileHandle.IsClosed, !mapping.SafeMemoryMappedFileHandle.IsClosed,
            !view.SafeMemoryMappedViewHandle.IsClosed);

        internal byte[] ReadIndex()
        {
            AssertHeld();
            var bytes = new byte[checked((int)capacity)];
            Assert.Equal(bytes.Length, view.ReadArray(0, bytes, 0, bytes.Length));
            return bytes;
        }

        internal void RestoreIndex(byte[] bytes)
        {
            AssertHeld();
            Assert.Equal(capacity, bytes.LongLength);
            view.WriteArray(0, bytes, 0, bytes.Length);
            view.Flush();
            Console.WriteLine($"SQLITE-CRASH-STALE-INDEX id={identity}; ownerProcessId={Environment.ProcessId}; bytes={bytes.Length}; nativeClientQuiescence=Unknown; originalDatabaseAndWalPreserved=true");
        }

        public void Dispose()
        {
            view.Dispose();
            mapping.Dispose();
            file.Dispose();
        }
    }
}
