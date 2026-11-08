namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
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
