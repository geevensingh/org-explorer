using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation;

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
              "department":"Research","managerEmail":"morgan@example.com","photoPath":"avery.png"},
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
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("avery@example.com"))
                throw new InvalidOperationException("The real import preview omitted the new Person.");
            Click(window, "ApplyImport");
            OpenPerson(window, "morgan@example.com");
            AssertPhotoVisible(window);
            AssertProfileValue(window, "OfficeNumber", "007B");
            if (Find(window, "OfficeNumberValue").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("View mode did not expose a readable profile value.");
            BeginProfileEditing(window);
            Set(window, "OfficeNumber", "Cancelled office");
            Click(window, "ClearPhoto");
            CancelProfileEditing(window);
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
            Click(window, "PreviewImport");
            Await(() => Find(window, "Status").Current.Name.Contains("Import rejected"));
            if (Find(window, "ApplyImport").Current.IsEnabled ||
                !Find(window, "Preview").Current.Name.Contains("avery@example.com.managerEmail"))
                throw new InvalidOperationException("Rejected import did not show a concrete error with Apply disabled.");
            Set(window, "PreferredName", "Ave");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            BeginProfileEditing(window);
            Set(window, "LinkedInProfileUrl", link);
            SaveProfileEditing(window);
            Await(() => Find(window, "OpenLinkedIn").Current.IsEnabled);
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-03T17:00:00Z","people":[
                 {"email":"avery@example.com","officeNumber":"0009C","department":null}]}
                """);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Await(() => Find(window, "Preview").Current.Name.Contains("0009C") &&
                Find(window, "Preview").Current.Name.Contains("(clear)"));
            AssertProfileValue(window, "OfficeNumber", "007B");
            Click(window, "ApplyImport");
            Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("007B → 0009C") &&
                Find(window, "ProfileTimeline").Current.Name.Contains("Imported") &&
                Find(window, "ProfileTimeline").Current.Name.Contains("Local edit"));
            Close(process);
            Directory.Delete(producer, true);

            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window, "morgan@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            if (Find(window, "DirectoryName").Current.Name != "Avery Example")
                throw new InvalidOperationException("Preferred name hid the retained Directory name.");
            AssertProfileValue(window, "OfficeNumber", "0009C");
            AssertProfileValue(window, "Department", "");
            AssertProfileValue(window, "Alias", "avery");
            AssertProfileValue(window, "LinkedInProfileUrl", link);
            AssertPhotoVisible(window);
            ExercisePhotoPresentation(window, root);
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
        Await(() =>
        {
            var value = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, field + "Value"));
            if (expected.Length == 0) return value is null;
            return value is not null && value.Current.ControlType == ControlType.Text && value.Current.Name == expected;
        });
    }

    private static AutomationElement ProfileLeaveWindow(AutomationElement window)
    {
        AutomationElement? decision = null;
        Await(() => (decision = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileLeaveDecision")))) is not null);
        return decision!;
    }
}
