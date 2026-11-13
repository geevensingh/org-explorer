using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunNavigationFeedbackFlow(string executable, string root, bool responsive = false)
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
            SetSettingsVisible(window, true);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenImportView(window);
            Set(window, "ImportPath", import);
            ApplyReviewedHistoryImport(window);
            OpenPerson(window, "casey@example.com");
            BeginProfileEditing(window);
            if (responsive) AssertResponsiveProfileForm(window);
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
                CaptureVisualEvidence(window, "profile-inline-failed-leave-save-retained");
            }
            Click(window, "NavigateManager");
            Click(ProfileLeaveWindow(window), "SaveProfileBeforeLeaving");
            Await(() => Find(window, "Email").Current.Name == "casey@example.com");
            OpenPerson(window, "casey@example.com");
            AssertProfileValue(window, "OfficeNumber", "0009P");
            BeginProfileEditing(window);
            Set(window, "OfficeNumber", "Discard me");
            SetSettingsVisible(window, true);
            Click(window, "OpenImport");
            Click(ProfileLeaveWindow(window), "StayOnProfile");
            Await(() => Find(window, "Status").Current.Name.StartsWith("Navigation blocked:"));
            AssertImportHidden(window);
            AssertValue(window, "OfficeNumber", "Discard me");
            if (Find(window, "Email").Current.Name != "avery@example.com")
                throw new InvalidOperationException("Staying on the profile opened Import or changed its owner.");
            using (File.Open(Path.Combine(workspace, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                SetSettingsVisible(window, true);
                Click(window, "OpenImport");
                Click(ProfileLeaveWindow(window), "SaveProfileBeforeLeaving");
                Await(() => Find(window, "Status").Current.Name.StartsWith("Navigation blocked: Save failed;", StringComparison.Ordinal) &&
                    Find(window, "Status").Current.Name.Contains("NOT saved") &&
                    Find(window, "WorkflowActivity").Current.Name.StartsWith("Pending local edits", StringComparison.Ordinal) &&
                    Find(window, "SaveProfile").Current.IsEnabled &&
                    window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileLeaveDecision")) is null);
                AssertImportHidden(window);
                AssertValue(window, "OfficeNumber", "Discard me");
                if (Find(window, "Email").Current.Name != "avery@example.com")
                    throw new InvalidOperationException("Failed Import leave-save changed the profile owner.");
            }
            SetSettingsVisible(window, true);
            Click(window, "OpenImport");
            Click(ProfileLeaveWindow(window), "SaveProfileBeforeLeaving");
            Await(() => !Find(window, "ImportPath").Current.IsOffscreen);
            ReturnToPeople(window);
            AssertProfileValue(window, "OfficeNumber", "Discard me");
            BeginProfileEditing(window);
            Set(window, "OfficeNumber", "Discard for Import");
            SetSettingsVisible(window, true);
            Click(window, "OpenImport");
            Click(ProfileLeaveWindow(window), "DiscardProfileBeforeLeaving");
            Await(() => !Find(window, "ImportPath").Current.IsOffscreen);
            ReturnToPeople(window);
            AssertProfileValue(window, "OfficeNumber", "Discard me");
            BeginProfileEditing(window);
            Set(window, "OfficeNumber", "Discard for Manager");
            Click(window, "NavigateManager");
            Click(ProfileLeaveWindow(window), "DiscardProfileBeforeLeaving");
            Await(() => Find(window, "Email").Current.Name == "casey@example.com" &&
                Find(window, "WorkflowActivity").Current.Name == "Ready" &&
                Find(window, "Status").Current.Name.Contains("Profile draft discarded", StringComparison.Ordinal));
            OpenPerson(window, "casey@example.com");
            AssertProfileValue(window, "OfficeNumber", "Discard me");
            foreach (var kind in new[] { "Person", "Team" })
            {
                Click(window, $"New{kind}Note");
                Set(window, $"{kind}NoteTitle", "");
                Await(() => Find(window, $"{kind}NoteStatus").Current.Name.Contains("NOT saved"));
                if (responsive && kind == "Person") AssertProtectedPeopleOverlay(window);
                BeginProfileEditing(window);
                Set(window, "DirectoryNameEditor", "Discard this profile only");
                SetSettingsVisible(window, true);
                Click(window, "OpenImport");
                Click(ProfileLeaveWindow(window), "DiscardProfileBeforeLeaving");
                Await(() => Find(window, $"{kind}NoteStatus").Current.Name.Contains("before leaving or closing"));
                if (!Find(window, "WorkflowActivity").Current.Name.StartsWith("Pending local edits", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Discarding the profile hid the retained failed {kind} note.");
                AssertImportHidden(window);
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
