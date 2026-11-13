using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Automation;
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
             {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null}]}
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
            AssertPhotoVisible(window);
            AssertCompactProfile(window);
            AssertProfileValue(window, "OfficeNumber", "007B");
            if (Find(window, "OfficeNumberValue").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("View mode did not expose a readable profile value.");
            Find(window, "JobTitleValue").SetFocus();
            PressCopyKey(0x09);
            Await(() => Find(window, "DepartmentValue").Current.HasKeyboardFocus);
            AssertKeyboardCopy(window, "Email", "avery@example.com");
            AssertKeyboardCopy(window, "DirectoryName", "Avery Example");
            AssertKeyboardCopy(window, "OfficeNumberValue", "007B");
            BeginProfileEditing(window);
            AssertProfileFieldKeyboard(window, workspace);
            Set(window, "OfficeNumber", "Cancelled office");
            Click(window, "ClearPhoto");
            CaptureVisualEvidence(window, "profile-inline-photo-clear-staged");
            CancelProfileEditing(window);
            AwaitLocalWorkIdle(window);
            AssertProfileValue(window, "OfficeNumber", "007B");
            AssertPhotoVisible(window);
            if (Find(window, "Email").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("Stable email identity became editable.");
            NavigateToManager(window, "morgan@example.com");
            var report = Find(window, "ImportedReports").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                ?? throw new InvalidOperationException("Imported report navigation is missing.");
            ((SelectionItemPattern)report.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
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
            Set(entry, "PreferredName", " ");
            Click(entry, "SavePreferredName");
            Await(() => Find(entry, "PreferredNameError").Current.Name.Contains("preferredName") &&
                Find(entry, "SavePreferredName").Current.IsEnabled);
            AssertValue(entry, "PreferredName", " ");
            CaptureVisualEvidence(window, "preferred-name-retained-failure");
            Set(entry, "PreferredName", "Ave");
            Find(entry, "PreferredName").SetFocus();
            Await(() => Find(entry, "PreferredName").Current.HasKeyboardFocus);
            PressCopyKey(0x0D);
            Await(() => Status(window) == "Saved locally.");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
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
            OpenPerson(window, "morgan@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            AssertPreferredNameReading(window, "Ave", "Edit");
            if (Find(window, "DirectoryName").Current.Name != "Avery Example")
                throw new InvalidOperationException("Preferred name hid the retained Directory name.");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            AssertProfileValue(window, "Department", "");
            AssertProfileValue(window, "Alias", "avery");
            AssertProfileValue(window, "LinkedInProfileUrl", link);
            AssertPhotoVisible(window);
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

    private static void AssertProfileFieldKeyboard(AutomationElement window, string workspace)
    {
        PressCopyKey(0x09);
        Await(() => Find(window, "Alias").Current.HasKeyboardFocus);
        Set(window, "OfficeNumber", "Not submitted by Enter");
        Find(window, "OfficeNumber").SetFocus();
        Await(() => Find(window, "OfficeNumber").Current.HasKeyboardFocus);
        PressCopyKey(0x0D);
        PressCopyKey(0x09);
        Await(() => Find(window, "OfficeState").Current.HasKeyboardFocus);
        AssertValue(window, "OfficeNumber", "Not submitted by Enter");
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

    private static void AssertKeyboardCopy(AutomationElement window, string name, string expected, string copyAction = "Copy value")
    {
        var value = Find(window, name);
        if (!value.Current.IsKeyboardFocusable)
            throw new InvalidOperationException($"{name} does not accept keyboard focus.");
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
        Await(() => System.Windows.Clipboard.GetText() == expected);
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
                    return field is "JobTitle" or "Department" ? value is null :
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
        AssertProfileValue(window, "OfficeBuilding", "");
        AssertProfileValue(window, "OfficeState", "");
        AssertProfileValue(window, "LinkedInProfileUrl", "");
        AssertProfileValue(window, "OfficeNumber", "007B");
        AssertProfileValue(window, "Alias", "avery");
        if (IsVisible(window, "PersonArchivalStatus"))
            throw new InvalidOperationException("Ordinary reading must not imply Active employment or presence.");
        foreach (var id in new[] { "JobTitleValue", "DepartmentValue", "OfficeCountryValue", "Email", "NavigateManager" })
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
