using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunNavigationFeedbackFlow(string executable, string root)
    {
        var workspace = Path.Combine(root, "navigation-workspace");
        var import = Path.Combine(root, "navigation.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"21000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
             {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com"}]}
            """);
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Set(window, "ImportPath", import);
            ApplyReviewedHistoryImport(window);
            OpenPerson(window, "casey@example.com");
            var profileEditor = (ValuePattern)Find(window, "OfficeNumber").GetCurrentPattern(ValuePattern.Pattern);
            var navigate = (InvokePattern)Find(window, "NavigateManager").GetCurrentPattern(InvokePattern.Pattern);
            var activity = Find(window, "WorkflowActivity");
            var mainStatus = Find(window, "Status");
            var owner = Find(window, "Email");
            profileEditor.SetValue("0009P");
            if (!activity.Current.Name.Contains("Pending"))
                throw new InvalidOperationException("The pending autosave state was not observed.");
            navigate.Invoke();
            var pendingStatus = mainStatus.Current.Name;
            if (!pendingStatus.StartsWith("Navigation blocked:") || !pendingStatus.Contains("Please wait") ||
                owner.Current.Name != "avery@example.com")
                throw new InvalidOperationException("Pending autosave navigation was not visibly rejected: " + pendingStatus);
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            NavigateToManager(window, "casey@example.com");
            OpenPerson(window, "casey@example.com");
            foreach (var kind in new[] { "Person", "Team" })
            {
                Click(window, $"New{kind}Note");
                Set(window, $"{kind}NoteTitle", "");
                Await(() => Find(window, $"{kind}NoteStatus").Current.Name.Contains("NOT saved"));
                Click(window, "NavigateManager");
                Await(() => Find(window, $"{kind}NoteStatus").Current.Name.Contains("before leaving or closing"));
                var status = Find(window, "Status").Current.Name;
                if (!status.StartsWith("Navigation blocked:") || !status.Contains($"{kind} note") ||
                    !status.Contains("Retry or discard"))
                    throw new InvalidOperationException($"Failed {kind} note navigation lacks a visible, actionable main status: {status}");
                if (Find(window, "Email").Current.Name != "avery@example.com" ||
                    Value(window, $"{kind}NoteTitle") != "")
                    throw new InvalidOperationException($"Rejected navigation changed the owner or discarded the failed {kind} note draft.");
                Click(window, $"Discard{kind}NoteDraft");
                Await(() => Value(window, $"{kind}NoteTitle").Length > 0);
                AwaitLocalWorkIdle(window);
            }
            Close(process);
            Console.WriteLine("PASS: failed Person/Team note navigation explains retry/discard in the main status and retains the owner and draft.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }
}
