using System.IO.MemoryMappedFiles;
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
