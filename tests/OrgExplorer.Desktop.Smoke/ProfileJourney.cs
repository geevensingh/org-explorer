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
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var link = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/profile";
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            var settings = Find(window, "OpenSettings");
            if (settings.Current.Name != "Settings" || !settings.Current.IsKeyboardFocusable)
                throw new InvalidOperationException("Settings lacks a meaningful, keyboard-accessible action.");
            settings.SetFocus();
            PressCopyKey(0x20);
            try { Await(() => Find(window, "WorkspacePath").Current.HasKeyboardFocus); }
            catch (TimeoutException error)
            {
                var path = Find(window, "WorkspacePath").Current;
                var focused = AutomationElement.FocusedElement.Current;
                throw new TimeoutException(
                    "Settings opened but Workspace folder did not receive keyboard focus. " +
                    $"Folder: enabled={path.IsEnabled}, focusable={path.IsKeyboardFocusable}, offscreen={path.IsOffscreen}, bounds={path.BoundingRectangle}. " +
                    $"Focused: id='{focused.AutomationId}', name='{focused.Name}', type={focused.ControlType.ProgrammaticName}.", error);
            }
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            AwaitLocalWorkIdle(window);
            AssertEverydayChrome(window, "workspace");
            AssertImportHidden(window);
            OpenImportView(window);
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("avery@example.com"))
                throw new InvalidOperationException("The real import preview omitted the new Person.");
            using (var saved = ApplicationWorkflow.CreateFileBacked())
            {
                if (!saved.OpenWorkspace(workspace) || saved.People.Count != 0)
                    throw new InvalidOperationException("Choosing or previewing an Import changed the saved population.");
            }
            Set(window, "ImportPath", import + ".missing");
            Await(() => !Find(window, "ApplyImport").Current.IsEnabled &&
                Find(window, "Status").Current.Name.Contains("Preview again"));
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            using (File.Open(Path.Combine(workspace, ".workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Click(window, "ApplyImport");
                Await(() => Find(window, "Status").Current.Name.Contains("NOT saved") &&
                    Find(window, "DiscardImport").Current.IsEnabled);
                ReturnToPeople(window);
                Click(window, "DiscardImport");
                Await(() => Find(window, "Status").Current.Name.Contains("Import discarded") &&
                    Find(window, "WorkflowActivity").Current.Name == "Ready");
            }
            OpenImportView(window);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            Await(() => Find(window, "Status").Current.Name.Contains("Import applied"));
            ReturnToPeople(window);
            OpenPerson(window, "morgan@example.com");
            AssertPhotoVisible(window);
            AssertCompactProfile(window);
            AssertProfileValue(window, "OfficeNumber", "007B");
            if (Find(window, "OfficeNumberValue").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("View mode did not expose a readable profile value.");
            Find(window, "DirectoryName").SetFocus();
            PressCopyKey(0x09);
            Await(() => Find(window, "Email").Current.HasKeyboardFocus);
            AssertKeyboardCopy(window, "Email", "avery@example.com");
            AssertKeyboardCopy(window, "DirectoryName", "Avery Example");
            AssertKeyboardCopy(window, "OfficeNumberValue", "007B");
            BeginProfileEditing(window);
            Set(window, "OfficeNumber", "Cancelled office");
            Click(window, "ClearPhoto");
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
            Await(() => Find(window, "Status").Current.Name.Contains("Import rejected"));
            if (Find(window, "ApplyImport").Current.IsEnabled ||
                !Find(window, "Preview").Current.Name.Contains("avery@example.com.managerEmail"))
                throw new InvalidOperationException("Rejected import did not show a concrete error with Apply disabled.");
            ReturnToPeople(window);
            AssertPreferredNameReading(window, "", "Add preferred name");
            Click(window, "EditPreferredName");
            Set(window, "PreferredName", " ");
            Await(() => Find(window, "Status").Current.Name.Contains("preferredName"));
            AssertValue(window, "PreferredName", " ");
            Click(window, "ReadPreferredName");
            AssertValue(window, "PreferredName", " ");
            Set(window, "PreferredName", "Ave");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            Click(window, "ReadPreferredName");
            AssertPreferredNameReading(window, "Ave", "Edit");
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
            Await(() => Find(window, "Status").Current.Name.Contains("Import applied"));
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
            AwaitRestoredWorkspace(window, workspace);
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
            Await(() => Value(window, "PreferredName") == "Ave");
            Click(window, "ReadPreferredName");
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
            !Find(window, "DirectoryNameEditor").Current.IsOffscreen);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    private static void PressCopyKey(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
    }

    private static void AssertKeyboardCopy(AutomationElement window, string name, string expected)
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
            new PropertyCondition(AutomationElement.NameProperty, "Copy value"))) is not null);
        PressCopyKey(0x28);
        PressCopyKey(0x0D);
        Await(() => System.Windows.Clipboard.GetText() == expected);
    }

    private static void SaveProfileEditing(AutomationElement window)
    {
        Click(window, "SaveProfile");
        Await(() => Find(window, "EditProfile").Current.IsEnabled &&
            Find(window, "Status").Current.Name.StartsWith("Profile saved locally.", StringComparison.Ordinal));
    }

    private static void CancelProfileEditing(AutomationElement window)
    {
        Click(window, "CancelProfile");
        Await(() => Find(window, "EditProfile").Current.IsEnabled);
    }

    private static void AssertProfileValue(AutomationElement window, string field, string expected)
    {
        if (field is "Alias" or "OfficeNumber" or "LinkedInProfileUrl") ExpandProfileDetails(window);
        var observed = "not yet observed";
        try
        {
            Await(() =>
            {
                var value = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, field + "Value"));
                observed = value is null ? "missing" :
                    $"{value.Current.ControlType.ProgrammaticName}, name '{value.Current.Name}', offscreen {value.Current.IsOffscreen}";
                if (expected.Length == 0) return value is null;
                return value is not null && value.Current.ControlType == ControlType.Text && value.Current.Name == expected;
            });
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"{field} reading expected '{expected}'; last UI observation: {observed}.", error);
        }
    }

    private static void ExpandProfileDetails(AutomationElement window)
    {
        var details = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileDetails"));
        if (details is null) return;
        var expansion = (ExpandCollapsePattern)details.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            expansion.Expand();
        Await(() => expansion.Current.ExpandCollapseState == ExpandCollapseState.Expanded);
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
        var details = (ExpandCollapsePattern)Find(window, "ProfileDetails").GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (details.Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
            throw new InvalidOperationException("A Person did not open with a concise initial summary.");
        AssertProfileValue(window, "JobTitle", "Research engineer");
        AssertProfileValue(window, "Department", "Research");
        AssertProfileValue(window, "OfficeCountry", "Canada");
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
            if (Find(window, "PreferredNameValue").Current.Name != value ||
                Find(window, "EditPreferredName").Current.Name != action) return false;
            var editor = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "PreferredName"));
            return editor is null || editor.Current.IsOffscreen;
        });
    }
}
