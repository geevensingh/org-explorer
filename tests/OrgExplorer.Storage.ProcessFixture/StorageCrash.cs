using OrgExplorer.Workflow;
using System.Text.Json;

namespace OrgExplorer.Storage.ProcessFixture;

public sealed class StorageCrash : IWorkspaceFileObserver
{
    private EventWaitHandle ready = null!;
    private WorkspaceStoragePhaseKind boundary;
    private string workspace = "";

    private static int Main(string[] args)
    {
        if (args.Length == 5 && args[0] == "stage2-collect")
            return SqliteStage2Capture.Collect(args[1], args[2], args[3], args[4]);
        if (args.Length == 3 && args[0] == "exit-cancellation")
            return ExitCancellation.Run(args[1], Enum.Parse<NoteScope>(args[2]));
        if (args.Length != 4 || !OperatingSystem.IsWindows()) return 3;
        var path = Path.GetFullPath(args[0]);
        if (!path.Contains(Path.DirectorySeparatorChar + ".test-workspaces" + Path.DirectorySeparatorChar,
            StringComparison.Ordinal)) return 3;
        SqliteStage2Capture.BindChild(path);
        var observer = new StorageCrash
        {
            ready = EventWaitHandle.OpenExisting(args[1]),
            workspace = path,
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
        var evidence = Directory.GetFiles(workspace, "local-write-*.json").Select(path =>
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            var intent = document.RootElement;
            return new
            {
                file = Path.GetFileName(path),
                requestId = intent.GetProperty("id").GetGuid(),
                baseRevision = intent.GetProperty("baseRevision").GetString(),
                nextRevision = intent.GetProperty("nextRevision").GetString()
            };
        }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            writerProcessId = Environment.ProcessId, phase = phase.Kind.ToString(), evidence
        }));
        Console.Out.Flush();
        ready.Set();
        // The parent terminates this owned process without Dispose at the observed boundary.
        Thread.Sleep(TimeSpan.FromSeconds(30));
        throw new TimeoutException("The owning crash test did not terminate its fixture.");
    }
}
