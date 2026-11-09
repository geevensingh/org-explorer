namespace OrgExplorer.Workflow;

public enum WorkspaceCompatibilityState { NotOpened, Compatible, Unsupported, RecoveryRequired, Unavailable }

public sealed record WorkspaceCompatibility(WorkspaceCompatibilityState State, string Guidance,
    string? WorkspacePath = null, int? DataVersion = null, string? RecoveryPath = null)
{
    public int SupportedWorkspaceVersion => 2;
}

public partial interface IApplicationWorkflow
{
    WorkspaceCompatibility Compatibility { get; }
}

public sealed partial class ApplicationWorkflow
{
    private WorkspaceCompatibility compatibility = new(WorkspaceCompatibilityState.NotOpened,
        "Choose a Workspace. Settings and upgrade guidance are available without opening one.");
    public WorkspaceCompatibility Compatibility => Volatile.Read(ref compatibility);
}
