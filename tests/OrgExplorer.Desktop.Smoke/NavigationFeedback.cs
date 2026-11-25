using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunNavigationFeedbackFlow(string executable, string root, bool responsive = false)
    {
        var workspace = Path.Combine(root, "navigation-workspace");
        Directory.CreateDirectory(workspace);
        var import = Path.Combine(root, "navigation.json");
        var importData = JsonNode.Parse("""
            {"schemaVersion":1,"batchId":"21000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
             {"email":"avery@example.com","displayName":"Avery","managerEmail":"casey@example.com"}]}
            """) as JsonObject ?? throw new InvalidOperationException("Navigation fixture is not an object.");
        if (responsive)
            foreach (var person in CompactIdentityFixturePeople())
                importData["people"]!.AsArray().Add(person?.DeepClone()
                    ?? throw new InvalidOperationException("Compact identity fixture is missing a Person."));
        File.WriteAllText(import, importData.ToJsonString());
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            OpenImportView(window);
            Set(window, "ImportPath", import);
            ApplyReviewedHistoryImport(window);
            OpenPerson(window, "casey@example.com");
            BeginProfileEditing(window);
            if (responsive) AssertResponsiveProfileForm(window);
            Set(window, "OfficeNumber", "0009P");
            Click(window, "NavigateManager");
            Await(() => Find(window, "Email").Current.Name == "casey@example.com");
            SelectTreePerson(window, "avery@example.com", PersonSelection.Mode.Recovery);
            AssertValue(window, "OfficeNumber", "0009P");
            if (Find(window, "Email").Current.Name != "avery@example.com")
                throw new InvalidOperationException("Stay changed the profile owner.");
            process.CloseMainWindow();
            Await(() => ExitDecisionVisible(window) &&
                Find(window, "ExitRisk").Current.Name.Contains("may be lost"));
            if (process.HasExited) throw new InvalidOperationException("Stay did not protect the draft on close.");
            AssertValue(window, "OfficeNumber", "0009P");
            Click(window, "CancelExit");
            Await(() => !ExitDecisionVisible(window));
            using (WorkspaceFixtures.LockLocalStore(workspace))
            {
                Click(window, "SaveProfile");
                Await(() =>
                    Status(window).Contains("NOT saved") &&
                    Activity(window).Contains("browsing remains available", StringComparison.Ordinal) &&
                    window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileLeaveDecision")) is null);
                Click(window, "NavigateManager");
                Await(() => Find(window, "Email").Current.Name == "casey@example.com");
                SelectTreePerson(window, "avery@example.com", PersonSelection.Mode.Recovery);
                AssertValue(window, "OfficeNumber", "0009P");
                if (Find(window, "Email").Current.Name != "avery@example.com")
                    throw new InvalidOperationException("Failed leave-save changed the profile owner.");
                CaptureVisualEvidence(window, "profile-inline-failed-leave-save-retained");
            }
            ReconcileSmokeLocalSaves(window);
            SaveProfileEditing(window);
            Click(window, "NavigateManager");
            Await(() => Find(window, "Email").Current.Name == "casey@example.com");
            OpenPerson(window, "casey@example.com");
            AssertProfileValue(window, "OfficeNumber", "0009P");
            BeginProfileEditing(window);
            Set(window, "OfficeNumber", "Discard me");
            SetSettingsVisible(window, true);
            Click(window, "OpenImport");
            Click(ProfileLeaveWindow(window), "StayOnProfile");
            Await(() => Status(window).StartsWith("Navigation blocked:"));
            AssertImportHidden(window);
            AssertValue(window, "OfficeNumber", "Discard me");
            if (Find(window, "Email").Current.Name != "avery@example.com")
                throw new InvalidOperationException("Staying on the profile opened Import or changed its owner.");
            using (WorkspaceFixtures.LockLocalStore(workspace))
            {
                SetSettingsVisible(window, true);
                Click(window, "OpenImport");
                Click(ProfileLeaveWindow(window), "SaveProfileBeforeLeaving");
                Await(() => IsSmokeRejectedSave(Status(window), Find(window, "OperationalNotice").Current.ItemStatus) &&
                    Activity(window).Contains("browsing remains available", StringComparison.Ordinal) &&
                    window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileLeaveDecision")) is null);
                AssertImportHidden(window);
                AssertValue(window, "OfficeNumber", "Discard me");
                if (Find(window, "Email").Current.Name != "avery@example.com")
                    throw new InvalidOperationException("Failed Import leave-save changed the profile owner.");
            }
            ReconcileSmokeLocalSaves(window);
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
            CancelProfileEditing(window);
            Click(window, "NavigateManager");
            Await(() => Find(window, "Email").Current.Name == "casey@example.com" &&
                Activity(window) == "Ready");
            OpenPerson(window, "casey@example.com");
            AssertProfileValue(window, "OfficeNumber", "Discard me");
            foreach (var kind in new[] { "Person", "Team" })
            {
                Click(window, $"New{kind}Note");
                var noteId = Guid.Parse(Find(window, $"{kind}NoteHeading").Current.HelpText);
                AwaitLocalWorkIdle(window);
                var savedNote = kind == "Person" ? (object)ReadSmokePersonNote(workspace, noteId) :
                    ReadSmokeTeamNote(workspace, noteId);
                using (var reading = OpenSmokeWorkspace(workspace))
                {
                    var saved = reading.BeginNoteDraft(kind == "Person" ? OrgExplorer.Workflow.NoteScope.Person :
                        OrgExplorer.Workflow.NoteScope.Team, "avery@example.com", noteId);
                    if (saved?.OwnerEmail != "avery@example.com" || saved.Id != noteId ||
                        saved.Title != $"New {kind} note" || saved.Markdown != "")
                        throw new InvalidOperationException($"The initial {kind} note was not committed before invalid editing.");
                }
                Set(window, $"{kind}NoteTitle", "");
                Await(() => Find(window, $"{kind}NoteStatus").Current.Name.Contains("NOT saved"));
                if (responsive && kind == "Person") AssertProtectedPeopleOverlay(window);
                BeginProfileEditing(window);
                Set(window, "DirectoryNameEditor", "Discard this profile only");
                if (responsive)
                    AssertReportingTreeMenu(window, "casey@example.com", "avery@example.com", $"retained-{kind.ToLowerInvariant()}-and-profile");
                SetSettingsVisible(window, true);
                Click(window, "OpenImport");
                Click(ProfileLeaveWindow(window), "DiscardProfileBeforeLeaving");
                Await(() => Status(window).Contains("NOT saved"));
                if (!Activity(window).StartsWith("Pending local edits", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Discarding the profile hid the retained failed {kind} note.");
                AssertImportHidden(window);
                var status = Status(window);
                if (!status.Contains("navigation blocked:", StringComparison.OrdinalIgnoreCase) ||
                    !status.Contains("Retry or discard"))
                    throw new InvalidOperationException($"Failed {kind} note navigation lacks a visible, actionable main status: {status}");
                if (Find(window, "Email").Current.Name != "avery@example.com" ||
                    Value(window, $"{kind}NoteTitle") != "" || Find(window, "DirectoryName").Current.Name != "Avery")
                    throw new InvalidOperationException($"Rejected navigation changed the owner or discarded the failed {kind} note draft.");
                Click(window, $"Discard{kind}NoteDraft");
                Await(() => Find(window, $"{kind}NoteHeading").Current.Name == $"New {kind} note" &&
                    Find(window, $"{kind}NoteHeading").Current.HelpText == noteId.ToString());
                if (!Equals(savedNote, kind == "Person" ? (object)ReadSmokePersonNote(workspace, noteId) :
                    ReadSmokeTeamNote(workspace, noteId)))
                    throw new InvalidOperationException($"Discard changed the saved exact {kind} note.");
                SelectTab(window, $"{kind}NoteReadTab");
                AssertNoteView(window, kind, editing: false);
                SelectTab(window, $"{kind}NoteEditTab");
                AssertNoteView(window, kind, editing: true);
                AssertValue(window, $"{kind}NoteTitle", $"New {kind} note");
                AwaitLocalWorkIdle(window);
            }
            ClosePublishedWorkspace(process, workspace);
            Console.WriteLine("PASS: SQLite failures pause editing, retain exact profile/Person/Team writing through browsing, and explain protected Import/Workspace transitions.");
        }
        finally
        {
            if (process is not null) StopApplication(process);
        }
    }

}
