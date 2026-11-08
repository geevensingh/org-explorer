namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private sealed class WorkspaceIoEntry
    {
        public object Gate { get; } = new();
        public int Users { get; set; }
    }

    // Per-client gates cannot protect publication from sibling clients' open read handles.
    private static readonly object workspaceIoRegistryGate = new();
    private static readonly Dictionary<string, WorkspaceIoEntry> workspaceIoEntries = new(StringComparer.OrdinalIgnoreCase);

    private static IDisposable CoordinateWorkspaceIo(string path)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        WorkspaceIoEntry entry;
        lock (workspaceIoRegistryGate)
        {
            if (!workspaceIoEntries.TryGetValue(key, out entry!))
                workspaceIoEntries.Add(key, entry = new WorkspaceIoEntry());
            entry.Users++;
        }
        Monitor.Enter(entry.Gate);
        return new WorkspaceIoLease(key, entry);
    }

    private sealed class WorkspaceIoLease(string key, WorkspaceIoEntry entry) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Monitor.Exit(entry.Gate);
            lock (workspaceIoRegistryGate)
                if (--entry.Users == 0) workspaceIoEntries.Remove(key);
        }
    }
}
