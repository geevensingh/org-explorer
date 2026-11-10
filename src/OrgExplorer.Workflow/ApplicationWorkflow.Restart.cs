namespace OrgExplorer.Workflow;

public partial interface IApplicationWorkflow
{
    bool RestartWhenSafe(Action<string?> restart);
}

public sealed partial class ApplicationWorkflow
{
    public bool RestartWhenSafe(Action<string?> restart)
    {
        lock (workspaceGate)
        {
            if (disposed || !CanSafelyClose)
                return Fail("Local work was NOT saved; automatic restart is blocked. Retry or deliberately discard the retained writing.");
            restart(workspace);
            return true;
        }
    }
}
