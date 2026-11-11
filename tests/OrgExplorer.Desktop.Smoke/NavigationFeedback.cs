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
            BeginProfileEditing(window);
            Set(window, "OfficeNumber", "0009P");
            Click(window, "NavigateManager");
            Click(ProfileLeaveWindow(window), "StayOnProfile");
            Await(() => Find(window, "Status").Current.Name.StartsWith("Navigation blocked:"));
            AssertValue(window, "OfficeNumber", "0009P");
            if (Find(window, "Email").Current.Name != "avery@example.com")
                throw new InvalidOperationException("Stay changed the profile owner.");
            process.CloseMainWindow();
            Click(ProfileLeaveWindow(window), "StayOnProfile");
            Await(() => Find(window, "Status").Current.Name.Contains("Unsaved local changes prevent safe closing"));
            if (process.HasExited) throw new InvalidOperationException("Stay did not protect the draft on close.");
            AssertValue(window, "OfficeNumber", "0009P");
            using (File.Open(Path.Combine(workspace, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Click(window, "NavigateManager");
                Click(ProfileLeaveWindow(window), "SaveProfileBeforeLeaving");
                Await(() => Find(window, "Status").Current.Name.StartsWith("Navigation blocked: Save failed;", StringComparison.Ordinal) &&
                    Find(window, "Status").Current.Name.Contains("NOT saved") &&
                    Find(window, "WorkflowActivity").Current.Name.StartsWith("Pending local edits", StringComparison.Ordinal) &&
                    Find(window, "SaveProfile").Current.IsEnabled &&
                    window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileLeaveDecision")) is null);
                AssertValue(window, "OfficeNumber", "0009P");
                if (Find(window, "Email").Current.Name != "avery@example.com")
                    throw new InvalidOperationException("Failed leave-save changed the profile owner.");
            }
            Click(window, "NavigateManager");
            Click(ProfileLeaveWindow(window), "SaveProfileBeforeLeaving");
            Await(() => Find(window, "Email").Current.Name == "casey@example.com");
            OpenPerson(window, "casey@example.com");
            AssertProfileValue(window, "OfficeNumber", "0009P");
            BeginProfileEditing(window);
            Set(window, "OfficeNumber", "Discard me");
            Click(window, "PreviewImport");
            Click(ProfileLeaveWindow(window), "StayOnProfile");
            AssertValue(window, "OfficeNumber", "Discard me");
            Click(window, "NavigateManager");
            Click(ProfileLeaveWindow(window), "DiscardProfileBeforeLeaving");
            Await(() => Find(window, "Email").Current.Name == "casey@example.com");
            OpenPerson(window, "casey@example.com");
            AssertProfileValue(window, "OfficeNumber", "0009P");
            foreach (var kind in new[] { "Person", "Team" })
            {
                Click(window, $"New{kind}Note");
                Set(window, $"{kind}NoteTitle", "");
                Await(() => Find(window, $"{kind}NoteStatus").Current.Name.Contains("NOT saved"));
                BeginProfileEditing(window);
                Set(window, "DirectoryNameEditor", "Discard this profile only");
                Click(window, "NavigateManager");
                Click(ProfileLeaveWindow(window), "DiscardProfileBeforeLeaving");
                Await(() => Find(window, $"{kind}NoteStatus").Current.Name.Contains("before leaving or closing"));
                var status = Find(window, "Status").Current.Name;
                if (!status.StartsWith("Navigation blocked:") || !status.Contains($"{kind} note") ||
                    !status.Contains("Retry or discard"))
                    throw new InvalidOperationException($"Failed {kind} note navigation lacks a visible, actionable main status: {status}");
                if (Find(window, "Email").Current.Name != "avery@example.com" ||
                    Value(window, $"{kind}NoteTitle") != "" || Find(window, "DirectoryName").Current.Name != "Avery")
                    throw new InvalidOperationException($"Rejected navigation changed the owner or discarded the failed {kind} note draft.");
                Click(window, $"Discard{kind}NoteDraft");
                Await(() => Find(window, $"{kind}NoteHeading").Current.Name == $"New {kind} note");
                AssertNoteView(window, kind, editing: false);
                SelectTab(window, $"{kind}NoteEditTab");
                AssertNoteView(window, kind, editing: true);
                AssertValue(window, $"{kind}NoteTitle", $"New {kind} note");
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
