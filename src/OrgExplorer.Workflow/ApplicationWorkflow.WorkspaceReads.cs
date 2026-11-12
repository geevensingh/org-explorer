namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private static IEnumerable<string> NoteDocumentPaths(string directory) =>
        workspaceReadSnapshot.Value is { } snapshot
            ? snapshot.Keys.Where(path => Path.GetDirectoryName(path)!.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
            : Directory.GetFiles(directory, "*.md");

    private static readonly AsyncLocal<IReadOnlyDictionary<string, byte[]>?> workspaceReadSnapshot = new();

    private sealed class WorkspaceReadScope(IReadOnlyDictionary<string, byte[]> bytes) : IDisposable
    {
        private readonly IReadOnlyDictionary<string, byte[]>? previous = workspaceReadSnapshot.Value;
        private readonly bool active = Set(bytes);
        private static bool Set(IReadOnlyDictionary<string, byte[]> bytes) { workspaceReadSnapshot.Value = bytes; return true; }
        public void Dispose() { if (active) workspaceReadSnapshot.Value = previous; }
    }

    private Dictionary<string, byte[]> CaptureWorkspaceBytes()
    {
        var bytes = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var paths = EnumerateIncrementalWorkspaceFiles(workspace!)
            .Where(p => !p.EndsWith(".workspace.lock", StringComparison.OrdinalIgnoreCase)).ToArray();
        RunBoundedFileIo(paths, path =>
        {
            RejectStoragePathLinks(path);
            bytes[path] = ReadWorkspaceBytesUncached(path);
        });
        return new Dictionary<string, byte[]>(bytes, StringComparer.OrdinalIgnoreCase);
    }

    private void AcceptWorkspaceRevision(IReadOnlyDictionary<string, byte[]> expected)
    {
        // Only the validated base plus our own published bytes describe the state in memory.
        // Provider delivery during publication must remain different until a validated refresh.
        revision = WorkspaceFingerprint(snapshot: expected);
        Volatile.Write(ref refreshNotBefore, Environment.TickCount64 + 100);
    }

    private static byte[] ReadWorkspaceBytesUncached(string path)
    {
        using var file = OpenWorkspaceRead(path);
        using var bytes = new MemoryStream();
        file.CopyTo(bytes);
        return bytes.ToArray();
    }

    // Windows replacement sources and overwritten backups must stay unopened, even with delete sharing.
    private static bool IsWorkspacePublicationFile(string root, string path)
    {
        if (path.Equals(Path.Combine(root, "workspace.next"), StringComparison.OrdinalIgnoreCase) ||
            path.Equals(Path.Combine(root, "workspace.previous.json"), StringComparison.OrdinalIgnoreCase)) return true;
        if (!Path.GetExtension(path).Equals(".next", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) || id == Guid.Empty) return false;
        var directory = Path.GetDirectoryName(path);
        return directory!.Equals(Path.Combine(root, "notes"), StringComparison.OrdinalIgnoreCase) ||
            directory.Equals(Path.Combine(root, "team-notes"), StringComparison.OrdinalIgnoreCase);
    }

    private static FileStream OpenWorkspaceRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

}
