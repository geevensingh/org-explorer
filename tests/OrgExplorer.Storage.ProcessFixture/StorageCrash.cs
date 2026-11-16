using OrgExplorer.Workflow;

namespace OrgExplorer.Storage.ProcessFixture;

public sealed class StorageCrash : IWorkspaceFileObserver
{
    private EventWaitHandle ready = null!;
    private WorkspaceStoragePhaseKind boundary;

    private static int Main(string[] args)
    {
        if (args.Length != 4 || !OperatingSystem.IsWindows()) return 3;
        var path = Path.GetFullPath(args[0]);
        if (!path.Contains(Path.DirectorySeparatorChar + ".test-workspaces" + Path.DirectorySeparatorChar,
            StringComparison.Ordinal)) return 3;
        var observer = new StorageCrash
        {
            ready = EventWaitHandle.OpenExisting(args[1]),
            boundary = args[2] == "before" ? WorkspaceStoragePhaseKind.Begin : WorkspaceStoragePhaseKind.End
        };
        using (observer.ready)
        using (var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer))
        {
            if (!app.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly)) throw new IOException(app.Status);
            if (args[3] == "profile")
            {
                if (!app.BeginProfileEdit("manager@example.com") ||
                    !app.StageProfileTeamName("Crash boundary label") ||
                    !app.StageProfileText(PersonTextField.Department, "Crash boundary department") ||
                    !app.SaveProfileEdit()) throw new IOException(app.Status);
            }
            else
            {
                var scope = Enum.Parse<NoteScope>(args[3]);
                var draft = app.BeginNoteDraft(scope, "manager@example.com")!;
                if (app.SaveNoteDraft(draft, "Crash boundary title", "Exact crash boundary writing") is null)
                    throw new IOException(app.Status);
            }
        }
        return 2;
    }

    public void Observe(WorkspaceFileEffect effect) { }
    public void ObservePhase(WorkspaceStoragePhase phase)
    {
        if (phase.Name != "local-sqlite-commit" || phase.Kind != boundary) return;
        ready.Set();
        // The parent terminates this owned process without Dispose at the observed boundary.
        Thread.Sleep(TimeSpan.FromSeconds(30));
        throw new TimeoutException("The owning crash test did not terminate its fixture.");
    }
}
