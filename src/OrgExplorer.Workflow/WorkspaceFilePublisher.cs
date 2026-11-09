namespace OrgExplorer.Workflow;

public interface IWorkspaceFilePublisher
{
    void Publish(string staged, string destination, string? backup);
}

internal sealed class WorkspaceFilePublisher : IWorkspaceFilePublisher
{
    public void Publish(string staged, string destination, string? backup)
    {
        if (File.Exists(destination)) File.Replace(staged, destination, backup);
        else File.Move(staged, destination);
    }
}
