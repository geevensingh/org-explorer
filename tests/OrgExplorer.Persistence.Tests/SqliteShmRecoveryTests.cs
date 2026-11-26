using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
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
            retainedCommittedFile: file);

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
            Console.WriteLine($"SQLITE-CRASH-STALE-INDEX id={identity}; ownerProcessId={Environment.ProcessId}; bytes={bytes.Length}; restoredOnlyAfterAllSQLiteClientsExited=true; originalDatabaseAndWalPreserved=true");
        }

        public void Dispose()
        {
            view.Dispose();
            mapping.Dispose();
            file.Dispose();
        }
    }
}
