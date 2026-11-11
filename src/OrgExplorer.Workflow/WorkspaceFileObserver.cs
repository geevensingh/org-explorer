namespace OrgExplorer.Workflow;

public enum WorkspaceFileEffectKind { Write, Flush, Publish, Delete }

public sealed record WorkspaceFileEffect(WorkspaceFileEffectKind Kind, string Path, long Bytes = 0,
    string? Destination = null, string? Backup = null);

/// <summary>
/// Observes completed filesystem effects without replacing them. Calls can arrive concurrently.
/// Write bytes are payload bytes, including staging and repeated overwrites, not filesystem metadata.
/// Observers must not throw or modify the Workspace.
/// </summary>
public interface IWorkspaceFileObserver
{
    void Observe(WorkspaceFileEffect effect);
}
