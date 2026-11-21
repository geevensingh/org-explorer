using OrgExplorer.Workflow;

namespace OrgExplorer.Persistence.Tests;

internal sealed class SqliteSetupFailureCollector : IWorkspaceFileObserver
{
    public List<object> Captured { get; } = [];
    public void Observe(WorkspaceFileEffect effect) { }
    public void ObserveSqliteSetupFailure(WorkspaceSqliteSetupFailure failure,
        Func<WorkspaceSqliteNativeProbe> readNativeSystemError)
    {
        if (Captured.Count < 8)
            Captured.Add(new { failure, native = readNativeSystemError() });
    }
}
