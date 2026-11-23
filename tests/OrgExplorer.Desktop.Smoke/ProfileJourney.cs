using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunProfileJourney(string executable, string root)
    {
        var producer = Path.Combine(root, "producer");
        Directory.CreateDirectory(producer);
        CreatePhotoFixture(Path.Combine(producer, "avery.png"));
        var import = Path.Combine(producer, "import.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery Example","alias":"avery","officeNumber":"007B",
              "department":"Research","jobTitle":"Research engineer","officeCountry":"Canada",
              "managerEmail":"morgan@example.com","photoPath":"avery.png"},
             {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null},
             {"email":"V-reference@example.com","displayName":"Vendor reference","managerEmail":"morgan@example.com"},
             {"email":"v-child@example.com","displayName":"Vendor child","managerEmail":"V-reference@example.com"}]}
            """);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var link = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/profile";
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            CaptureWindowIconEvidence(window, executable, "profile");
            var settings = Find(window, "OpenSettings");
            if (settings.Current.Name != "Settings" || !settings.Current.IsKeyboardFocusable)
                throw new InvalidOperationException("Settings lacks a meaningful, keyboard-accessible action.");
            settings.SetFocus();
            PressCopyKey(0x20);
            try { Await(() => Find(window, "OpenWorkspace").Current.HasKeyboardFocus); }
            catch (TimeoutException error)
            {
                var path = Find(window, "OpenWorkspace").Current;
                var focused = AutomationElement.FocusedElement.Current;
                throw new TimeoutException(
                    "Settings opened but Open Workspace did not receive keyboard focus. " +
                    $"Folder: enabled={path.IsEnabled}, focusable={path.IsKeyboardFocusable}, offscreen={path.IsOffscreen}, bounds={path.BoundingRectangle}. " +
                    $"Focused: id='{focused.AutomationId}', name='{focused.Name}', type={focused.ControlType.ProgrammaticName}.", error);
            }
            CaptureVisualEvidence(window, "settings-grouped-first-run");
            var orientation = (Find(window, "WorkspaceName").Current.Name,
                Find(window, "WorkspaceName").Current.HelpText, Find(window, "WorkspaceName").Current.ItemStatus,
                Find(window, "WorkspacePath").Current.Name);
            var preferencePath = Path.Combine(root, "app-settings", "workspace-settings.json");
            var preference = File.Exists(preferencePath) ? File.ReadAllBytes(preferencePath) : null;
            PickWorkspaceFolder(window, null, observePicker: () => CaptureVisualEvidence(window, "settings-native-picker-cancel"));
            Await(() => Find(window, "OpenWorkspace").Current.HasKeyboardFocus);
            if (orientation != (Find(window, "WorkspaceName").Current.Name,
                    Find(window, "WorkspaceName").Current.HelpText, Find(window, "WorkspaceName").Current.ItemStatus,
                    Find(window, "WorkspacePath").Current.Name) ||
                File.Exists(preferencePath) != (preference is not null) ||
                preference is not null && !File.ReadAllBytes(preferencePath).SequenceEqual(preference) ||
                Directory.GetFileSystemEntries(workspace).Length != 0)
                throw new InvalidOperationException("Cancelling the native picker changed orientation, preferences or initialized a Workspace.");
            PickWorkspaceFolder(window, workspace);
            AwaitRestoredWorkspace(window, workspace);
            AssertEverydayChrome(window, "workspace");
            AssertImportHidden(window);
            SetSettingsVisible(window, true);
            if (Find(window, "WorkspacePath").Current.ControlType != ControlType.Text ||
                Find(window, "WorkspacePath").Current.Name != Path.GetFullPath(workspace))
                throw new InvalidOperationException("Settings did not display the full current Workspace path.");
            bool CopyPathMenuVisible()
            {
                var action = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.NameProperty, "Copy path")));
                return action is not null && !action.Current.IsOffscreen;
            }
            Await(() => !CopyPathMenuVisible());
            System.Windows.Clipboard.SetText("Copy has not been invoked.");
            PrimaryClick(() => Find(window, "WorkspacePath"));
            mouse_event(8, 0, 0, 0, 0);
            mouse_event(16, 0, 0, 0, 0);
            Await(CopyPathMenuVisible);
            if (System.Windows.Clipboard.GetText() != "Copy has not been invoked.")
                throw new InvalidOperationException("Workspace-path primary clicks acquired unintended profile-copy behavior.");
            PressCopyKey(0x1B);
            Await(() => !CopyPathMenuVisible());
            AssertKeyboardCopy(window, "WorkspacePath", Path.GetFullPath(workspace), "Copy path");
            CaptureVisualEvidence(window, "settings-grouped-current-workspace");
            SetSettingsVisible(window, false);
            OpenImportView(window);
            Set(window, "ImportPath", import);
            Find(window, "ImportPath").SetFocus();
            PressCopyKey(0x0D);
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("avery@example.com"))
                throw new InvalidOperationException("The real import preview omitted the new Person.");
            CaptureVisualEvidence(window, "settings-import-enter-preview");
            using (var saved = ApplicationWorkflow.CreateFileBacked())
            {
                if (!saved.OpenWorkspace(workspace) || saved.People.Count != 0)
                    throw new InvalidOperationException("Choosing or previewing an Import changed the saved population.");
            }
            Set(window, "ImportPath", import + ".missing");
            Await(() => !Find(window, "ApplyImport").Current.IsEnabled &&
                Status(window).Contains("Preview again"));
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            using (File.Open(Path.Combine(workspace, ".workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Click(window, "ApplyImport");
                Await(() => Status(window).Contains("NOT saved") &&
                    Find(window, "DiscardImport").Current.IsEnabled);
                ReturnToPeople(window);
                Click(window, "DiscardImport");
                Await(() => Status(window).Contains("Import discarded") &&
                    Activity(window) == "Ready");
            }
            OpenImportView(window);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            Await(() => Status(window).Contains("Import applied"));
            ReturnToPeople(window);
            OpenPerson(window, "morgan@example.com");
            AssertPersonCategory(window, "Employee");
            AssertReportingStructure(window, null);
            SelectTreePerson(window, "V-reference@example.com");
            AssertPersonCategory(window, "Vendor");
            AssertReportingStructure(window, 1, 0, 1);
            NavigateToManager(window, "morgan@example.com");
            AssertPersonCategory(window, "Employee");
            AssertReportingStructure(window, 3, 1, 2);
            SelectTreePerson(window, "avery@example.com");
            AssertPhotoVisible(window);
            AssertCompactProfile(window);
            AssertTeamNameReading(window, null);
            AssertReportingStructure(window, null);
            AssertProfileValue(window, "OfficeNumber", "007B");
            if (Find(window, "OfficeNumberValue").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("View mode did not expose a readable profile value.");
            Find(window, "JobTitleValue").SetFocus();
            PressCopyKey(0x09);
            Await(() => Find(window, "Email").Current.HasKeyboardFocus);
            AssertKeyboardCopy(window, "Email", "avery@example.com");
            AssertKeyboardCopy(window, "DirectoryName", "Avery Example");
            AssertKeyboardCopy(window, "OfficeNumberValue", "007B");
            AssertProfileCopyGestures(window, workspace, link);
            BeginProfileEditing(window);
            if (IsVisible(window, "TeamNameEditor"))
                throw new InvalidOperationException("A Person without current direct reports can edit a Team name.");
            AssertProfileFieldKeyboard(window, workspace);
            Set(window, "OfficeNumber", "Cancelled office");
            StageProfilePhotoClear(window);
            CaptureVisualEvidence(window, "profile-inline-photo-clear-staged");
            CancelProfileEditing(window);
            AwaitLocalWorkIdle(window);
            AssertProfileValue(window, "OfficeNumber", "007B");
            AssertPhotoVisible(window);
            if (Find(window, "Email").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("Stable email identity became editable.");
            NavigateToManager(window, "morgan@example.com");
            AssertOwnedTeamNameEditing(window, workspace);
            AssertInheritedTeamNameEditing(window, import, workspace);
            Await(() => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ImportedReports")) is not { } reports ||
                reports.Current.IsOffscreen);
            SelectTreePerson(window, "avery@example.com");
            Await(() => Find(window, "Email").Current.Name == "avery@example.com");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000003",
                 "observedAt":"2026-11-02T17:00:00Z","people":[{"email":"morgan@example.com","archived":true}]}
                """);
            OpenImportView(window);
            Click(window, "PreviewImport");
            Await(() => Status(window).Contains("Import rejected"));
            if (Find(window, "ApplyImport").Current.IsEnabled ||
                !Find(window, "Preview").Current.Name.Contains("avery@example.com.managerEmail"))
                throw new InvalidOperationException("Rejected import did not show a concrete error with Apply disabled.");
            ReturnToPeople(window);
            AssertPreferredNameReading(window, "", "Add preferred name");
            Click(window, "EditPreferredName");
            var entry = PreferredNameEntry(window);
            Set(entry, "PreferredName", "Unconfirmed name");
            Find(entry, "PreferredName").SetFocus();
            PressCopyKey(0x09);
            Await(() => Find(entry, "SavePreferredName").Current.HasKeyboardFocus);
            Thread.Sleep(650);
            using (var saved = ApplicationWorkflow.CreateFileBacked())
            {
                if (!saved.OpenWorkspace(workspace) || saved.GetRelationships("avery@example.com")!.Person.PreferredName is not null)
                    throw new InvalidOperationException("Typing or blur published an unconfirmed Preferred name.");
            }
            CaptureVisualEvidence(window, "preferred-name-normal-entry");
            var beforeNameFailure = OrgExplorer.TestFixtures.WorkspaceFixtures.Bytes(workspace);
            Set(entry, "PreferredName", " ");
            Click(entry, "SavePreferredName");
            Await(() => Find(entry, "PreferredNameError").Current.HelpText.Contains("preferredName") &&
                Find(entry, "SavePreferredName").Current.IsEnabled);
            AssertValue(entry, "PreferredName", " ");
            CaptureVisualEvidence(window, "preferred-name-retained-failure");
            var failureDetails = Find(entry, "PreferredNameFailureDetails");
            var detailsHeader = Find(failureDetails, "HeaderSite");
            detailsHeader.SetFocus();
            Await(() => detailsHeader.Current.HasKeyboardFocus);
            PressCopyKey(0x20);
            Await(() => ((ExpandCollapsePattern)failureDetails.GetCurrentPattern(ExpandCollapsePattern.Pattern))
                    .Current.ExpandCollapseState == ExpandCollapseState.Expanded &&
                !Find(entry, "PreferredNameErrorDetails").Current.IsOffscreen &&
                Find(entry, "PreferredNameErrorDetails").Current.Name.Contains("preferredName"));
            if (FindPreferredNameEntry(window) is null || Value(entry, "PreferredName") != " " ||
                !Activity(window).StartsWith("Pending local edits", StringComparison.Ordinal))
                throw new InvalidOperationException("Keyboard Details expansion cancelled the retained name or hid its unsafe state.");
            AssertWorkspaceBytes(workspace, beforeNameFailure);
            CaptureVisualEvidence(window, "preferred-name-retained-failure-keyboard-details");
            Set(entry, "PreferredName", "Ave");
            Find(entry, "PreferredName").SetFocus();
            Await(() => Find(entry, "PreferredName").Current.HasKeyboardFocus);
            PressCopyKey(0x0D);
            Await(() => Status(window) == "Saved locally.");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            AssertPersonCategory(window, "Employee");
            AssertPreferredNameReading(window, "Ave", "Edit");
            foreach (var dismissal in new[] { "cancel", "escape", "outside" })
            {
                Click(window, "EditPreferredName");
                entry = PreferredNameEntry(window);
                Set(entry, "PreferredName", "Must not save " + dismissal);
                if (dismissal == "cancel") Click(entry, "CancelPreferredName");
                else if (dismissal == "escape")
                {
                    Find(entry, "PreferredName").SetFocus();
                    Await(() => Find(entry, "PreferredName").Current.HasKeyboardFocus);
                    PressCopyKey(0x1B);
                }
                else
                {
                    var target = Find(window, "SearchQuery").Current.BoundingRectangle;
                    if (target.IsEmpty || !SetCursorPos((int)(target.Left + target.Width / 2), (int)(target.Top + target.Height / 2)))
                        throw new InvalidOperationException("The hosted Preferred-name outside-click target is unavailable.");
                    mouse_event(2, 0, 0, 0, 0);
                    mouse_event(4, 0, 0, 0, 0);
                }
                AssertPreferredNameReading(window, "Ave", "Edit");
                Await(() => Find(window, dismissal == "outside" ? "SearchQuery" : "EditPreferredName").Current.HasKeyboardFocus);
                CaptureVisualEvidence(window, "preferred-name-dismissed-" + dismissal);
                using var saved = ApplicationWorkflow.CreateFileBacked();
                if (!saved.OpenWorkspace(workspace) || saved.GetRelationships("avery@example.com")!.Person.PreferredName != "Ave")
                    throw new InvalidOperationException("Preferred-name dismissal changed the durable value: " + dismissal);
            }
            BeginProfileEditing(window);
            Set(window, "LinkedInProfileUrl", link);
            SaveProfileEditing(window);
            Await(() => Find(window, "OpenLinkedIn").Current.IsEnabled);
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-03T17:00:00Z","people":[
                 {"email":"avery@example.com","officeNumber":"0009C","department":null}]}
                """);
            OpenImportView(window);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Await(() => Find(window, "Preview").Current.Name.Contains("0009C") &&
                Find(window, "Preview").Current.Name.Contains("(clear)"));
            ReturnToPeople(window);
            AssertProfileValue(window, "OfficeNumber", "007B");
            OpenImportView(window);
            if (Find(window, "ApplyImport").Current.IsEnabled)
                throw new InvalidOperationException("Returning to people retained an applicable unreviewed Import.");
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            Await(() => Status(window).Contains("Import applied"));
            ReturnToPeople(window);
            ExpandHistory(window);
            Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("007B → 0009C") &&
                Find(window, "ProfileTimeline").Current.Name.Contains("Imported") &&
                Find(window, "ProfileTimeline").Current.Name.Contains("Local edit"));
            OpenImportView(window);
            process.CloseMainWindow();
            if (!process.WaitForExit(10000))
                throw new TimeoutException("App did not close normally from Import.");
            Directory.Delete(producer, true);

            process = Launch(executable);
            window = Window(process);
            AwaitRestoredWorkspace(window, workspace, "avery@example.com");
            AssertImportHidden(window);
            OpenPerson(window, "rowan@example.com", "morgan@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            AssertPreferredNameReading(window, "Ave", "Edit");
            if (Find(window, "DirectoryName").Current.Name != "Avery Example")
                throw new InvalidOperationException("Preferred name hid the retained Directory name.");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            AssertProfileValue(window, "Department", "");
            AssertProfileValue(window, "Alias", "avery");
            AssertProfileValue(window, "LinkedInProfileUrl", link);
            AssertPhotoVisible(window);
            NavigateToManager(window, "morgan@example.com");
            AssertTeamNamePath(window, "Platform", "Platform");
            AssertReportingStructure(window, 3, 1, 2);
            SelectTreePerson(window, "avery@example.com");
            ExercisePhotoPresentation(window, root);
            Click(window, "EditPreferredName");
            entry = PreferredNameEntry(window);
            AssertValue(entry, "PreferredName", "Ave");
            Click(entry, "CancelPreferredName");
            AssertPreferredNameReading(window, "Ave", "Edit");
            if (listener.Pending()) throw new InvalidOperationException("Profile presentation fetched a remote link.");
            Close(process);
        }
        finally { if (process is not null) StopApplication(process); }
    }

    private static void BeginProfileEditing(AutomationElement window)
    {
        Click(window, "EditProfile");
        Await(() => Find(window, "DirectoryNameEditor").Current.IsEnabled &&
            !Find(window, "DirectoryNameEditor").Current.IsOffscreen &&
            Find(window, "DirectoryNameEditor").Current.HasKeyboardFocus);
    }

    private static void AssertPersonCategory(AutomationElement window, string expected)
    {
        var category = ControlReadiness.AwaitControl(() => Find(window, "PersonCategoryLabel"),
            control => !control.Current.IsOffscreen && control.Current.Name == $"Person category: {expected}", Await);
        var current = category.Current;
        var badge = current.BoundingRectangle;
        var heading = Find(window, "ProfileHeading").Current.BoundingRectangle;
        var photo = Find(window, "Photo").Current.BoundingRectangle;
        var scale = DpiScale(window);
        if (current.ControlType != ControlType.Text || !current.IsKeyboardFocusable ||
            !current.HelpText.Contains("classification", StringComparison.Ordinal) ||
            !current.HelpText.Contains("not verified", StringComparison.Ordinal) ||
            badge.IsEmpty || badge.Width <= 0 || badge.Left < heading.Right - scale ||
            badge.Top < heading.Top - scale || badge.Bottom > heading.Bottom + scale ||
            badge.Right > Find(window, "ProfileScroll").Current.BoundingRectangle.Right + scale ||
            badge.Bottom > photo.Bottom + scale)
            throw new InvalidOperationException($"The read-only {expected} badge must be accessible and readable beside the name within anchored identity: badge={badge}, heading={heading}, photo={photo}.");
    }

    private static void AssertOwnedTeamNameEditing(AutomationElement window, string workspace)
    {
        AssertTeamNameReading(window, null);
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "Cancelled label");
        CancelProfileEditing(window);
        AssertTeamNameReading(window, null);
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "Platform");
        SaveProfileEditing(window);
        AssertTeamNameReading(window, "Platform");
        AssertTeamNameOwnerAttribution(window, "Morgan Example", "morgan@example.com");
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "Renamed platform");
        using (File.Open(Path.Combine(workspace, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Click(window, "SaveProfile");
            Await(() => Status(window).Contains("NOT saved") && Find(window, "SaveProfile").Current.IsEnabled);
            AssertValue(window, "TeamNameEditor", "Renamed platform");
            using var saved = ApplicationWorkflow.CreateFileBacked();
            if (!saved.OpenWorkspace(workspace) ||
                saved.GetRelationships("morgan@example.com")!.Person.TeamName != "Platform")
                throw new InvalidOperationException("Rejected Team-name Save changed the committed owned label.");
            CaptureVisualEvidence(window, "team-name-rejected-save-retained");
        }
        SaveProfileEditing(window);
        AssertTeamNameReading(window, "Renamed platform");
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "");
        SaveProfileEditing(window);
        AssertTeamNameReading(window, null);
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "Platform");
        SaveProfileEditing(window);
        CaptureVisualEvidence(window, "team-name-owned-reading");
    }

    private static void AssertInheritedTeamNameEditing(AutomationElement window, string import, string workspace)
    {
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000089",
             "observedAt":"2026-11-02T16:00:00Z","people":[
             {"email":"morgan@example.com","managerEmail":"rowan@example.com"},
             {"email":"rowan@example.com","displayName":"Rowan Example","managerEmail":null}]}
            """);
        ApplyReviewedHistoryImport(window);
        Await(() => Find(window, "Email").Current.Name == "morgan@example.com");
        NavigateToManager(window, "rowan@example.com");
        AssertTeamNameReading(window, null);
        AssertReportingStructure(window, 4, 2, 2);
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "Platform");
        SaveProfileEditing(window);
        AssertTeamNameReading(window, "Platform");
        SelectTreePerson(window, "morgan@example.com");
        AssertTeamNamePath(window, "Platform", "Platform");
        AssertTeamNameOwnerAttribution(window, "Morgan Example", "morgan@example.com", 0);
        AssertTeamNameOwnerAttribution(window, "Rowan Example", "rowan@example.com", 1);
        AssertMouseCopy(window, FindTeamNameSegment(window, 0), "Platform");
        AssertKeyboardCopy(window, FindTeamNameSegment(window, 1), "Platform");
        foreach (var index in new[] { 0, 1 })
        {
            AssertPrimaryCopy(window, FindTeamNameSegment(window, index), "Platform");
            AssertFocusedCopy(window, FindTeamNameSegment(window, index), "Platform", 0x0D);
            AssertFocusedCopy(window, FindTeamNameSegment(window, index), "Platform", 0x20);
        }
        AwaitCopyFeedbackGone(window);
        CaptureVisualEvidence(window, "team-name-duplicate-owner-reading");
        BeginProfileEditing(window);
        AssertValue(window, "TeamNameEditor", "Platform");
        Set(window, "TeamNameEditor", "Renamed owned label");
        SaveProfileEditing(window);
        AssertTeamNamePath(window, "Renamed owned label", "Platform");
        AssertKeyboardCopy(window, FindTeamNameSegment(window, 0), "Renamed owned label");
        AssertMouseCopy(window, FindTeamNameSegment(window, 1), "Platform");
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "");
        SaveProfileEditing(window);
        AssertTeamNameReading(window, "Platform");
        AssertTeamNameOwnerAttribution(window, "Rowan Example", "rowan@example.com");
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "Platform");
        SaveProfileEditing(window);
        SelectTreePerson(window, "avery@example.com");
        AssertTeamNamePath(window, "Platform", "Platform");
        BeginProfileEditing(window);
        if (IsVisible(window, "TeamNameEditor"))
            throw new InvalidOperationException("Inherited context granted a non-manager an owned-name editor.");
        CancelProfileEditing(window);
        using var saved = ApplicationWorkflow.CreateFileBacked();
        if (!saved.OpenWorkspace(workspace) ||
            saved.GetRelationships("morgan@example.com")!.Person.TeamName != "Platform" ||
            saved.GetRelationships("rowan@example.com")!.Person.TeamName != "Platform" ||
            saved.GetRelationships("avery@example.com")!.Person.TeamName is not null)
            throw new InvalidOperationException("Owner-only editing copied or overwrote the inherited path.");
        NavigateToManager(window, "morgan@example.com");
    }

    private static void AssertTeamNameReading(AutomationElement window, string? expected)
        => AssertTeamNamePath(window, expected is null ? [] : [expected]);

    private static void AssertReportingStructure(AutomationElement window, int? total, int employees = 0, int vendors = 0)
    {
        using var timing = ObserveUiTiming("reporting-summary");
        var scroll = ControlReadiness.AwaitPattern(() => Find(window, "ProfileScroll"),
            control => control.Current.IsEnabled && !control.Current.IsOffscreen,
            control => (ScrollPattern)control.GetCurrentPattern(ScrollPattern.Pattern), Await);
        ControlReadiness.ScrollToTopIfScrollable(() => scroll.Current.VerticallyScrollable,
            () => scroll.SetScrollPercent(ScrollPattern.NoScroll, 0));
        Await(() => !scroll.Current.VerticallyScrollable || scroll.Current.VerticalScrollPercent == 0);
        if (total is null)
        {
            if (IsVisible(window, "ReportingStructureLabel") || IsVisible(window, "ReportingStructureValue") ||
                IsVisible(window, "ReportingStructureScope"))
                throw new InvalidOperationException("An ineligible Person retained a visible Reporting structure fact.");
            return;
        }
        var counts = $"{total} total, {employees} Employees, {vendors} Vendors";
        Await(() =>
        {
            var ready = ControlObservation.Capture(window, "ReportingStructureValue", "ReportingStructureScope");
            return ready.IsVisible("ReportingStructureValue") &&
                ready["ReportingStructureValue"].Cached.Name == $"Reporting structure: {counts}" &&
                ready.IsVisible("ReportingStructureScope");
        });
        var controls = ControlObservation.Capture(window, "ReportingStructureLabel", "ReportingStructureValue",
            "ReportingStructureScope", "ProfileScroll", "TeamNameLabel", "DepartmentValue");
        var label = controls["ReportingStructureLabel"];
        var value = controls["ReportingStructureValue"];
        var scope = controls["ReportingStructureScope"];
        if (label.Cached.Name != "Reporting structure" || scope.Cached.Name != "Known current reports, all levels" ||
            value.Cached.ControlType != ControlType.Edit ||
            !value.TryGetCurrentPattern(ValuePattern.Pattern, out var readOnly) ||
            !((ValuePattern)readOnly).Current.IsReadOnly || !value.Cached.IsKeyboardFocusable)
            throw new InvalidOperationException("Reporting structure lost its ordinary read-only fact, full counts or exact population scope.");
        var scale = DpiScale(window);
        var viewport = controls.Bounds("ProfileScroll");
        var team = controls.Bounds("TeamNameLabel");
        var summary = label.Cached.BoundingRectangle;
        var department = controls.Bounds("DepartmentValue");
        bool Before(Rect first, Rect second) => first.Top < second.Top - scale ||
            Math.Abs(first.Top - second.Top) <= scale && first.Left < second.Left - scale;
        if (!Before(team, summary) || !Before(summary, department) ||
            Math.Abs(team.Top - summary.Top) > scale && Math.Abs(team.Left - summary.Left) > scale)
            throw new InvalidOperationException("Reporting structure must be the next ordinary fact immediately after Team name.");
        foreach (var element in new[] { label, value, scope })
        {
            var bounds = element.Cached.BoundingRectangle;
            if (element.Cached.IsOffscreen || bounds.IsEmpty || scroll.Current.HorizontallyScrollable ||
                bounds.Left < viewport.Left - scale || bounds.Right > viewport.Right + scale ||
                bounds.Top < viewport.Top - scale || bounds.Bottom > viewport.Bottom + scale)
                throw new InvalidOperationException($"Reporting structure is clipped rather than wrapping compactly: {bounds}; viewport={viewport}.");
        }
        using var rangeTiming = ObserveUiTiming("reporting-text-ranges");
        if (!value.TryGetCurrentPattern(TextPattern.Pattern, out var text) ||
            ((TextPattern)text).DocumentRange.GetText(-1) != counts ||
            ((TextPattern)text).DocumentRange.FindText($"{vendors} Vendors", false, false) is not { } end)
            throw new InvalidOperationException("The final zero-inclusive category count is not actually rendered and readable.");
        var finalCategory = $"{vendors} Vendors";
        if (end.GetText(-1) != finalCategory)
            throw new InvalidOperationException("The rendered reporting range lost the full final count/category.");
        var cursor = end.Clone();
        cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, cursor, TextPatternRangeEndpoint.Start);
        for (var index = 0; index < finalCategory.Length; index++)
        {
            var character = cursor.Clone();
            if (character.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) != 1 ||
                character.GetText(-1) != finalCategory[index].ToString())
                throw new InvalidOperationException($"The final reporting category character {index} is missing from the real text range.");
            var rectangles = character.GetBoundingRectangles();
            if (rectangles.Length == 0)
                throw new InvalidOperationException($"The final reporting category character {index} has no rendered glyph rectangle.");
            foreach (var glyph in rectangles)
            {
                var bounds = value.Current.BoundingRectangle;
                if (glyph.IsEmpty || glyph.Width <= 0 || glyph.Height <= 0 ||
                    glyph.Left < bounds.Left - scale || glyph.Right > bounds.Right + scale ||
                    glyph.Top < bounds.Top - scale || glyph.Bottom > bounds.Bottom + scale ||
                    glyph.Left < viewport.Left - scale || glyph.Right > viewport.Right + scale ||
                    glyph.Top < viewport.Top - scale || glyph.Bottom > viewport.Bottom + scale)
                    throw new InvalidOperationException($"The final reporting category character {index} is clipped: glyph={glyph}; value={bounds}; viewport={viewport}.");
            }
            cursor.MoveEndpointByRange(TextPatternRangeEndpoint.Start, character, TextPatternRangeEndpoint.End);
        }
    }

    private static bool IsFirstScrollingTeamName(Rect row, Rect manager, Rect viewport, double scale)
    {
        // ProfileValues' border + padding + FactSlot's top margin.
        const double labelInset = 1 + 8 + 4;
        return !row.IsEmpty && !manager.IsEmpty && !viewport.IsEmpty && manager.Bottom <= viewport.Top + scale &&
            Math.Abs(row.Left - viewport.Left) <= scale &&
            Math.Abs(row.Top - viewport.Top - labelInset * scale) <= scale;
    }

    private static void AssertTeamNamePath(AutomationElement window, params string[] expected)
    {
        var scroll = ControlReadiness.AwaitPattern(() => Find(window, "ProfileScroll"),
            control => control.Current.IsEnabled && !control.Current.IsOffscreen,
            control => (ScrollPattern)control.GetCurrentPattern(ScrollPattern.Pattern), Await);
        ControlReadiness.ScrollToTopIfScrollable(() => scroll.Current.VerticallyScrollable,
            () => scroll.SetScrollPercent(ScrollPattern.NoScroll, 0));
        Await(() => !scroll.Current.VerticallyScrollable || scroll.Current.VerticalScrollPercent == 0);
        var label = Find(window, "TeamNameLabel");
        AutomationElement[] values = [];
        Await(() =>
        {
            var names = Find(window, "TeamNames");
            var segments = names.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "TeamNameSegment"))
                .Cast<AutomationElement>().ToArray();
            if (label.Current.IsOffscreen || !segments.Select(segment => segment.Current.Name).SequenceEqual(expected) ||
                segments.Any(segment => segment.Current.IsOffscreen))
                return false;
            values = expected.Length == 0 ? [Find(window, "TeamNameEmpty")] : segments;
            return values.All(value => !value.Current.IsOffscreen) &&
                (expected.Length != 0 || values[0].Current.Name == "Not set");
        });
        if (values.Any(value => value.Current.ControlType != ControlType.Text ||
            value.TryGetCurrentPattern(ValuePattern.Pattern, out _) ||
            (value.Current.AutomationId == "TeamNameSegment" &&
                !value.TryGetCurrentPattern(InvokePattern.Pattern, out _))) || IsVisible(window, "TeamNameEditor"))
            throw new InvalidOperationException("Team-name reading became an editable field or lost its explicit empty state.");
        var separators = Find(window, "TeamNames").FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TeamNameSeparator"))
            .Cast<AutomationElement>().Where(separator => !separator.Current.IsOffscreen).ToArray();
        if (separators.Length != Math.Max(0, expected.Length - 1) ||
            separators.Any(separator => separator.Current.Name != " > " || separator.Current.IsKeyboardFocusable))
            throw new InvalidOperationException("The rendered Team-name path lost its exact read-only ' > ' separators.");
        var renderedPath = string.Concat(Find(window, "TeamNames").FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "TeamNameSegment"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "TeamNameSeparator")))
            .Cast<AutomationElement>().Where(value => !value.Current.IsOffscreen).Select(value => value.Current.Name));
        if (renderedPath != string.Join(" > ", expected))
            throw new InvalidOperationException($"The actual rendered Team-name path is out of order: '{renderedPath}'.");
        var manager = Find(window, "NavigateManager").Current.BoundingRectangle;
        var row = label.Current.BoundingRectangle;
        var scale = DpiScale(window);
        var viewport = Find(window, "ProfileScroll").Current.BoundingRectangle;
        if (!IsFirstScrollingTeamName(row, manager, viewport, scale))
            throw new InvalidOperationException($"Team name must be the first scrolling fact below anchored Manager: " +
                $"label={row}, Manager={manager}, viewport={viewport}.");
        foreach (var value in values.Concat(separators))
        {
            var bounds = value.Current.BoundingRectangle;
            if (scroll.Current.HorizontallyScrollable || bounds.Left < viewport.Left - scale ||
                bounds.Right > viewport.Right + scale || bounds.Top < viewport.Top - scale || bounds.Bottom > viewport.Bottom + scale)
                throw new InvalidOperationException($"Team-name reading is clipped: value={bounds}, viewport={viewport}.");
            if (value.Current.AutomationId == "TeamNameSegment" && value.TryGetCurrentPattern(TextPattern.Pattern, out var text))
            {
                var lastWord = value.Current.Name.Split(' ')[^1];
                var last = ((TextPattern)text).DocumentRange.FindText(lastWord, false, false);
                if (last is null || last.GetBoundingRectangles().Length == 0)
                    throw new InvalidOperationException("The end of the actual rendered Team name is not readable.");
            }
        }
    }

    private static AutomationElement FindTeamNameSegment(AutomationElement window, int index) =>
        Find(window, "TeamNames").FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TeamNameSegment"))
            .Cast<AutomationElement>().ElementAtOrDefault(index) ??
                throw new InvalidOperationException($"Missing actual UI control: TeamNameSegment {index}");

    private static void AssertTeamNameOwnerAttribution(AutomationElement window, string heading, string email, int index = 0)
    {
        var expected = $"Owned by {heading} ({email})";
        Find(window, "Email").SetFocus();
        var segment = ControlReadiness.AwaitControl(() => FindTeamNameSegment(window, index),
            candidate => !candidate.Current.IsOffscreen && candidate.Current.HelpText == expected, Await);
        bool TooltipVisible()
        {
            var tooltip = FindCopyTooltip(window);
            return tooltip is not null && !tooltip.Current.IsOffscreen &&
                (tooltip.Current.Name == "Click to copy\n" + segment.Current.Name + "\n" + expected ||
                    tooltip.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty,
                        "Click to copy\n" + segment.Current.Name + "\n" + expected)) is not null);
        }
        HoverCopyValue(segment);
        Await(TooltipVisible);
        AssertCopyCursor(true);
        var away = AwaitClickablePoint(() => Find(window, "Email"));
        if (!SetCursorPos((int)away.X, (int)away.Y))
            throw new InvalidOperationException("The hosted desktop could not leave the Team-name segment.");
        Await(() => !TooltipVisible());
        segment.SetFocus();
        Await(() => segment.Current.HasKeyboardFocus && TooltipVisible());
        if (!segment.Current.IsKeyboardFocusable || segment.Current.HelpText != expected)
            throw new InvalidOperationException("The Team-name owner is not independently attributed on keyboard focus.");
        PressCopyKey(0x09);
        Await(() => !segment.Current.HasKeyboardFocus && !TooltipVisible());
        Find(window, "Email").SetFocus();
    }

    private static void AssertProfileFieldKeyboard(AutomationElement window, string workspace)
    {
        PressCopyKey(0x09);
        Await(() => Find(window, "Alias").Current.HasKeyboardFocus);
        Set(window, "OfficeNumber", "Not submitted by Enter");
        Find(window, "OfficeNumber").SetFocus();
        Await(() => Find(window, "OfficeNumber").Current.HasKeyboardFocus);
        System.Windows.Clipboard.SetText("Copy has not been invoked.");
        PressCopyKey(0x0D);
        PressCopyKey(0x09);
        Await(() => Find(window, "OfficeState").Current.HasKeyboardFocus);
        AssertValue(window, "OfficeNumber", "Not submitted by Enter");
        if (System.Windows.Clipboard.GetText() != "Copy has not been invoked.")
            throw new InvalidOperationException("Editor Enter acquired read-only profile-copy behavior.");
        if (!IsVisible(window, "ProfileEditingState") || !IsVisible(window, "SaveProfile") ||
            !IsVisible(window, "CancelProfile") || Find(window, "Email").Current.Name != "avery@example.com")
            throw new InvalidOperationException("Field Enter submitted or displaced the complete contextual profile draft.");
        using var saved = ApplicationWorkflow.CreateFileBacked();
        if (!saved.OpenWorkspace(workspace) || saved.GetRelationships("avery@example.com")!.Person.OfficeNumber != "007B")
            throw new InvalidOperationException("A profile field Enter published staged text before explicit Save.");
        CaptureVisualEvidence(window, "profile-inline-field-enter-retained");
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    private static void PressCopyKey(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
    }

    private static void AwaitCopiedText(string expected)
    {
        try { PumpUntil(() => System.Windows.Clipboard.GetText() == expected); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("The actual clipboard did not receive the expected text within 20 seconds.", error);
        }
    }

    private static void AssertKeyboardCopy(AutomationElement window, string name, string expected, string copyAction = "Copy value")
        => AssertKeyboardCopy(window, Find(window, name), expected, copyAction);

    private static void AssertKeyboardCopy(AutomationElement window, AutomationElement value, string expected, string copyAction = "Copy value")
    {
        var profileFeedback = copyAction == "Copy value" && value.Current.ItemStatus == "Click to copy";
        if (profileFeedback) AwaitCopyFeedbackGone(window);
        if (!value.Current.IsKeyboardFocusable)
            throw new InvalidOperationException($"{value.Current.AutomationId} does not accept keyboard focus.");
        value.SetFocus();
        Await(() => value.Current.HasKeyboardFocus);
        System.Windows.Clipboard.SetText("Copy has not been invoked.");
        PressCopyKey(0x5D);
        Await(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, copyAction))) is not null);
        PressCopyKey(0x28);
        PressCopyKey(0x0D);
        AwaitCopiedText(expected);
        if (profileFeedback)
            AssertCopyFeedback(window, value);
    }

    private static void AssertMouseCopy(AutomationElement window, AutomationElement value, string expected)
    {
        AwaitCopyFeedbackGone(window);
        System.Windows.Clipboard.SetText("Copy has not been invoked.");
        var point = AwaitClickablePoint(() => value);
        if (!SetCursorPos((int)point.X, (int)point.Y))
            throw new InvalidOperationException("The hosted desktop could not right-click the profile value.");
        mouse_event(8, 0, 0, 0, 0);
        mouse_event(16, 0, 0, 0, 0);
        AutomationElement? copy = null;
        Await(() => (copy = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, "Copy value")))) is not null);
        var action = AwaitClickablePoint(() => copy!);
        if (!SetCursorPos((int)action.X, (int)action.Y))
            throw new InvalidOperationException("The hosted desktop could not click Copy value.");
        mouse_event(2, 0, 0, 0, 0);
        mouse_event(4, 0, 0, 0, 0);
        AwaitCopiedText(expected);
        AssertCopyFeedback(window, value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyCursorInfo
    {
        public int Size, Flags;
        public IntPtr Cursor;
        public DisplayPoint Position;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CopyCursorInfo info);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    private static void ReadCopyCursor(ref CopyCursorInfo cursor)
    {
        // GetCursorInfo overwrites cbSize; every observation must initialize it.
        cursor.Size = Marshal.SizeOf<CopyCursorInfo>();
        if (!GetCursorInfo(ref cursor))
            throw new InvalidOperationException("Hosted cursor observation failed: Win32 " + Marshal.GetLastWin32Error());
    }

    private static Point AwaitClickablePoint(Func<AutomationElement> find, bool requireEnabled = true)
    {
        var point = new Point();
        AutomationElement? value = null;
        try
        {
            ControlReadiness.AwaitControl(() => value = find(),
                candidate => (!requireEnabled || candidate.Current.IsEnabled) && !candidate.Current.IsOffscreen &&
                    candidate.TryGetClickablePoint(out point), Await);
        }
        catch (TimeoutException error)
        {
            if (value is null)
                throw new TimeoutException("Copy target was not found before physical input.", error);
            try
            {
                var state = value.Current;
                throw new TimeoutException($"Copy target did not become ready before physical input: {state.AutomationId}; " +
                    $"enabled={state.IsEnabled}; offscreen={state.IsOffscreen}; focus={state.HasKeyboardFocus}; " +
                    $"bounds={state.BoundingRectangle}; lastPoint={point}; itemStatus='{state.ItemStatus}'.", error);
            }
            catch (ElementNotAvailableException unavailable)
            {
                throw new TimeoutException("Copy target became unavailable before physical input: " + unavailable.Message, error);
            }
        }
        return point;
    }

    private static void PrimaryClick(Func<AutomationElement> find, bool requireEnabled = true)
    {
        AutomationElement? value = null;
        var point = AwaitClickablePoint(() => value = find(), requireEnabled);
        if (!SetCursorPos((int)point.X, (int)point.Y))
            throw new InvalidOperationException("The hosted desktop could not primary-click " + value!.Current.AutomationId);
        mouse_event(2, 0, 0, 0, 0);
        mouse_event(4, 0, 0, 0, 0);
    }

    private static void HoverCopyValue(AutomationElement value, bool requireEnabled = true)
    {
        var point = AwaitClickablePoint(() => value, requireEnabled);
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        if (width <= 1 || height <= 1 || point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height)
            throw new InvalidOperationException("The hosted hover point is outside the verified primary display: " + point);
        // Position-only SetCursorPos leaves the hosted touch/pen cursor suppressed.
        mouse_event(0x8001, (uint)Math.Round(point.X * 65535 / (width - 1)),
            (uint)Math.Round(point.Y * 65535 / (height - 1)), 0, 0);
    }

    private static void AssertCopyCursor(bool copyable)
    {
        var cursor = new CopyCursorInfo { Size = Marshal.SizeOf<CopyCursorInfo>() };
        ReadCopyCursor(ref cursor);
        ReadCopyCursor(ref cursor);
        var hand = LoadCursor(IntPtr.Zero, (IntPtr)32649);
        if (hand == IntPtr.Zero) throw new InvalidOperationException("The hosted hand cursor is unavailable.");
        Await(() =>
        {
            ReadCopyCursor(ref cursor);
            return cursor.Flags == 1 && (cursor.Cursor == hand) == copyable;
        });
    }

    private static AutomationElement? FindCopyTooltip(AutomationElement window)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip));
        return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition) ??
            AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition);
    }

    private static void AssertCopyHint(AutomationElement window, AutomationElement value, string expected)
    {
        HoverCopyValue(value);
        Await(() =>
        {
            var tooltip = FindCopyTooltip(window);
            return tooltip is not null && !tooltip.Current.IsOffscreen &&
                (tooltip.Current.Name == "Click to copy\n" + expected ||
                    tooltip.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty,
                        "Click to copy\n" + expected)) is not null);
        });
        AssertCopyCursor(true);
    }

    private static void AssertCopyFeedback(AutomationElement window, AutomationElement value)
    {
        var expected = value.Current.AutomationId switch
        {
            "Email" => "Email copied.",
            "JobTitleValue" => "Job title copied.",
            "OfficeBuildingValue" => "Office building copied.",
            "OfficeNumberValue" => "Office number copied.",
            "DepartmentValue" => "Department copied.",
            "OfficeStateValue" => "Office state / province copied.",
            "OfficeCountryValue" => "Office country copied.",
            "DirectoryName" => "Directory name copied.",
            "AliasValue" => "Alias copied.",
            "LinkedInProfileUrlValue" => "LinkedIn profile URL copied.",
            "TeamNameSegment" => "Team name copied.",
            _ => throw new InvalidOperationException("Unexpected profile-copy target: " + value.Current.AutomationId)
        };
        var request = new CacheRequest { TreeScope = TreeScope.Element };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        Await(() =>
        {
            AutomationElement feedback;
            using (request.Activate()) feedback = Find(window, "ProfileCopyFeedback");
            var state = feedback.Cached;
            var bounds = state.BoundingRectangle;
            return !state.IsOffscreen && state.Name == expected && !bounds.IsEmpty &&
                bounds.Width > 0 && bounds.Height > 0 && ScreenContrast(bounds) > 20;
        });
    }

    private static void AwaitCopyFeedbackGone(AutomationElement window)
    {
        var lifetime = Stopwatch.StartNew();
        Await(() => !IsVisible(window, "ProfileCopyFeedback"));
        if (lifetime.Elapsed > TimeSpan.FromSeconds(3))
            throw new InvalidOperationException("Rendered copy confirmation did not disappear within three seconds.");
    }

    private static void AssertPrimaryCopy(AutomationElement window, AutomationElement value, string expected, bool freshFeedback = false)
    {
        var id = value.Current.AutomationId;
        if (freshFeedback) AwaitCopyFeedbackGone(window);
        if (id == "TeamNameSegment")
        {
            var email = Find(window, "Email");
            email.SetFocus();
            var away = AwaitClickablePoint(() => email);
            if (!SetCursorPos((int)away.X, (int)away.Y))
                throw new InvalidOperationException("The hosted desktop could not dismiss the previous owner tooltip.");
        }
        System.Windows.Clipboard.SetText("Copy has not been invoked.");
        PrimaryClick(() => value);
        AwaitCopiedText(expected);
        AssertCopyFeedback(window, value);
    }

    private static void AssertFocusedCopy(AutomationElement window, AutomationElement value, string expected, byte key)
    {
        AwaitCopyFeedbackGone(window);
        value.SetFocus();
        Await(() => value.Current.HasKeyboardFocus);
        System.Windows.Clipboard.SetText("Copy has not been invoked.");
        PressCopyKey(key);
        AwaitCopiedText(expected);
        AssertCopyFeedback(window, value);
    }

    private static void AssertMissingCopy(AutomationElement window, AutomationElement value)
    {
        System.Windows.Clipboard.SetText("Copy has not been invoked.");
        HoverCopyValue(value, requireEnabled: false);
        Thread.Sleep(700);
        AssertCopyCursor(false);
        if (value.Current.ItemStatus.Contains("Click to copy", StringComparison.Ordinal))
            throw new InvalidOperationException("Missing profile values advertise an unavailable copy action.");
        var tooltip = FindCopyTooltip(window);
        if (tooltip is not null && !tooltip.Current.IsOffscreen &&
            (tooltip.Current.Name.Contains("Click to copy", StringComparison.Ordinal) ||
                tooltip.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Click to copy")) is not null))
            throw new InvalidOperationException("A missing profile value shows a copy hint.");
        PrimaryClick(() => value, requireEnabled: false);
        mouse_event(8, 0, 0, 0, 0);
        mouse_event(16, 0, 0, 0, 0);
        AutomationElement? mouseCopy = null;
        Await(() => (mouseCopy = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, "Copy value")))) is not null);
        if (mouseCopy!.Current.IsEnabled) throw new InvalidOperationException("Mouse Copy value is enabled for an absent value.");
        PressCopyKey(0x1B);
        Find(window, "Email").SetFocus();
        Await(() => Find(window, "Email").Current.HasKeyboardFocus);
        for (var index = 0; index < 40 && !value.Current.HasKeyboardFocus; index++)
        {
            var before = AutomationElement.FocusedElement;
            PressCopyKey(0x09);
            Await(() => !Equals(AutomationElement.FocusedElement, before));
        }
        if (!value.Current.HasKeyboardFocus)
            throw new InvalidOperationException("Physical Tab traversal never reached the labelled missing value.");
        PressCopyKey(0x0D);
        PressCopyKey(0x20);
        PressCopyKey(0x5D);
        AutomationElement? copy = null;
        Await(() => (copy = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, "Copy value")))) is not null);
        if (copy!.Current.IsEnabled) throw new InvalidOperationException("Missing-value Copy value is enabled.");
        PressCopyKey(0x28);
        PressCopyKey(0x0D);
        PressCopyKey(0x1B);
        if (System.Windows.Clipboard.GetText() != "Copy has not been invoked." || IsVisible(window, "ProfileCopyFeedback"))
            throw new InvalidOperationException("A missing-value gesture changed the clipboard or claimed success.");
    }

    private static void AssertProfileCopyGestures(AutomationElement window, string workspace, string link)
    {
        BeginProfileEditing(window);
        const string role = "Research\r\nengineer with a deliberately long role description";
        const string building = "South campus\r\nResearch and collaboration building";
        Set(window, "JobTitle", role);
        Set(window, "OfficeBuilding", building);
        Set(window, "OfficeState", "Ontario");
        Set(window, "LinkedInProfileUrl", link);
        SaveProfileEditing(window);
        var scroll = ControlReadiness.AwaitPattern(() => Find(window, "ProfileScroll"),
            control => control.Current.IsEnabled && !control.Current.IsOffscreen,
            control => (ScrollPattern)control.GetCurrentPattern(ScrollPattern.Pattern), Await);
        ControlReadiness.ScrollToTopIfScrollable(() => scroll.Current.VerticallyScrollable,
            () => scroll.SetScrollPercent(ScrollPattern.NoScroll, 0));
        var bytes = OrgExplorer.TestFixtures.WorkspaceFixtures.Bytes(workspace);
        var fields = new[] { ("JobTitleValue", role), ("Email", "avery@example.com"),
            ("OfficeBuildingValue", building), ("OfficeNumberValue", "007B"),
            ("DepartmentValue", "Research"), ("OfficeStateValue", "Ontario"), ("OfficeCountryValue", "Canada"),
            ("DirectoryName", "Avery Example"), ("AliasValue", "avery"), ("LinkedInProfileUrlValue", link) };
        foreach (var (id, expected) in fields)
        {
            var value = Find(window, id);
            AssertCopyHint(window, value, expected);
            AssertPrimaryCopy(window, value, expected, freshFeedback: id is "Email" or "DepartmentValue");
        }
        foreach (var id in new[] { "Email", "DepartmentValue" })
            foreach (var key in new byte[] { 0x0D, 0x20 })
                AssertFocusedCopy(window, Find(window, id), id == "Email" ? "avery@example.com" : "Research", key);
        CaptureVisualEvidence(window, "profile-copy-transient-confirmation");
        AwaitCopyFeedbackGone(window);
        AssertWorkspaceBytes(workspace, bytes);
        System.Windows.Clipboard.SetText("Copy has not been invoked.");
        PrimaryClick(() => Find(window, "ProfileHeading"));
        PrimaryClick(() => Find(window, "NavigateManager"));
        Await(() => Find(window, "Email").Current.Name == "morgan@example.com");
        if (System.Windows.Clipboard.GetText() != "Copy has not been invoked.")
            throw new InvalidOperationException("Heading or Manager navigation acquired unintended copy activation.");
        SelectTreePerson(window, "avery@example.com");
        Await(() => Find(window, "Email").Current.Name == "avery@example.com");
        BeginProfileEditing(window);
        Set(window, "OfficeNumber", "Retained clipboard-failure draft");
        System.Windows.Clipboard.SetText("Copy has not been invoked.");
        if (!OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("The hosted clipboard-lock prerequisite failed: Win32 " + Marshal.GetLastWin32Error());
        try
        {
            PrimaryClick(() => Find(window, "Email"));
            Await(() => IsVisible(window, "OperationalNotice") &&
                Find(window, "NoticeSummary").Current.Name.Contains("Copy failed", StringComparison.Ordinal));
            if (IsVisible(window, "ProfileCopyFeedback") || !IsVisible(window, "ProfileEditingState") ||
                Value(window, "OfficeNumber") != "Retained clipboard-failure draft" ||
                Find(window, "Email").Current.Name != "avery@example.com")
                throw new InvalidOperationException("Actual OS clipboard failure claimed success or displaced the Person/profile draft.");
            CaptureVisualEvidence(window, "profile-copy-clipboard-failure");
        }
        finally
        {
            if (!CloseClipboard()) throw new InvalidOperationException("The hosted clipboard lock could not be released.");
        }
        if (System.Windows.Clipboard.GetText() != "Copy has not been invoked.")
            throw new InvalidOperationException("Rejected clipboard copying replaced the sentinel.");
        AssertWorkspaceBytes(workspace, bytes);
        CancelProfileEditing(window);
        AssertPrimaryCopy(window, Find(window, "Email"), "avery@example.com");
        AwaitCopyFeedbackGone(window);
        if (IsVisible(window, "OperationalNotice"))
            throw new InvalidOperationException("Successful clipboard retry did not restore quiet healthy reading.");
        BeginProfileEditing(window);
        Set(window, "JobTitle", "Research engineer");
        Set(window, "OfficeBuilding", "");
        Set(window, "OfficeState", "");
        Set(window, "LinkedInProfileUrl", "");
        SaveProfileEditing(window);
        ControlReadiness.ScrollToTopIfScrollable(() => scroll.Current.VerticallyScrollable,
            () => scroll.SetScrollPercent(ScrollPattern.NoScroll, 0));
        AssertMissingCopy(window, Find(window, "OfficeStateValue"));
        AssertMissingCopy(window, Find(window, "LinkedInProfileUrlValue"));
    }

    private static void SaveProfileEditing(AutomationElement window)
    {
        Click(window, "SaveProfile");
        Await(() => Find(window, "EditProfile").Current.IsEnabled &&
            Status(window).StartsWith("Profile saved locally.", StringComparison.Ordinal));
    }

    private static void CancelProfileEditing(AutomationElement window)
    {
        Click(window, "CancelProfile");
        Await(() => Find(window, "EditProfile").Current.IsEnabled &&
            Find(window, "EditProfile").Current.HasKeyboardFocus);
    }

    private static void AssertProfileValue(AutomationElement window, string field, string expected)
    {
        var observed = "not yet observed";
        try
        {
            Await(() =>
            {
                var value = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, field + "Value"));
                observed = value is null ? "missing" :
                    $"{value.Current.ControlType.ProgrammaticName}, name '{value.Current.Name}', offscreen {value.Current.IsOffscreen}";
                if (expected.Length == 0)
                    return field is "JobTitle" ? value is null :
                        value is not null && value.Current.ControlType == ControlType.Text && value.Current.Name == "—";
                return value is not null && value.Current.ControlType == ControlType.Text && value.Current.Name == expected;
            });
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"{field} reading expected '{expected}'; last UI observation: {observed}.", error);
        }
    }

    private static void AssertEverydayChrome(AutomationElement window, string workspaceName)
    {
        Await(() => Find(window, "WorkspaceName").Current.Name == workspaceName &&
            !Find(window, "SearchQuery").Current.IsOffscreen && !Find(window, "OpenSettings").Current.IsOffscreen);
        foreach (var id in new[] { "WorkspacePath", "OpenWorkspace", "ChooseWorkspace", "OpenImport" })
        {
            var control = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (control is not null && !control.Current.IsOffscreen)
                throw new InvalidOperationException($"Infrequent management still occupies everyday chrome: {id}.");
        }
    }

    private static void AssertCompactProfile(AutomationElement window)
    {
        if (window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileDetails")) is not null)
            throw new InvalidOperationException("Ordinary facts still require a More details disclosure.");
        AssertProfileValue(window, "JobTitle", "Research engineer");
        AssertProfileValue(window, "Department", "Research");
        AssertProfileValue(window, "OfficeCountry", "Canada");
        var emptyBuilding = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "OfficeBuildingValue"));
        if (emptyBuilding is not null && !emptyBuilding.Current.IsOffscreen)
            throw new InvalidOperationException("Office with only a room must not display an empty-building placeholder.");
        AssertProfileValue(window, "OfficeState", "");
        AssertProfileValue(window, "LinkedInProfileUrl", "");
        AssertProfileValue(window, "OfficeNumber", "007B");
        AssertProfileValue(window, "Alias", "avery");
        if (IsVisible(window, "PersonArchivalStatus"))
            throw new InvalidOperationException("Ordinary reading must not imply Active employment or presence.");
        foreach (var id in new[] { "JobTitleValue", "DepartmentValue", "OfficeCountryValue", "Email", "NavigateManager", "OfficeNumberValue" })
            if (Find(window, id).Current.IsOffscreen)
                throw new InvalidOperationException($"Essential profile information is not initially visible: {id}.");
        var photo = Find(window, "PersonPhoto").Current.BoundingRectangle;
        var heading = Find(window, "ProfileHeading").Current.BoundingRectangle;
        if (photo.IsEmpty || heading.IsEmpty || photo.Right > heading.Left || Math.Abs(photo.Top - heading.Top) > 20)
            throw new InvalidOperationException("Photo and identity are not rendered together in a compact header.");
        if (Find(window, "DirectoryName").Current.Name != "Avery Example" ||
            Find(window, "Email").Current.Name != "avery@example.com" ||
            Find(window, "Email").Current.ControlType != ControlType.Text)
            throw new InvalidOperationException("The compact header lost Directory name or stable, non-editable email.");
        CaptureVisualEvidence(window, "profile-reading-photo-all-facts");
    }

    private static AutomationElement ProfileLeaveWindow(AutomationElement window)
    {
        AutomationElement? decision = null;
        Await(() => (decision = window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileLeaveDecision")))) is not null);
        return decision!;
    }

    private static void AssertPreferredNameReading(AutomationElement window, string value, string action)
    {
        Await(() =>
        {
            var expectedHeading = value.Length == 0 ? Find(window, "DirectoryName").Current.Name : value;
            if (Find(window, "ProfileHeading").Current.Name != expectedHeading ||
                Find(window, "EditPreferredName").Current.Name != (action == "Edit" ? "Edit preferred name" : action)) return false;
            return FindPreferredNameEntry(window) is null;
        });
    }

    private static AutomationElement? FindPreferredNameEntry(AutomationElement window)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "PreferredNameEntry");
        var entry = window.FindFirst(TreeScope.Descendants, condition);
        if (entry is not null && !entry.Current.IsOffscreen) return entry;
        foreach (AutomationElement root in AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId)))
        {
            entry = root.FindFirst(TreeScope.Descendants, condition);
            if (entry is not null && !entry.Current.IsOffscreen) return entry;
        }
        return null;
    }

    private static AutomationElement PreferredNameEntry(AutomationElement window)
    {
        AutomationElement? entry = null;
        Await(() => (entry = FindPreferredNameEntry(window)) is not null);
        return entry!;
    }
}
