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
            AssertValue(window, "OfficeNumber", "007B");
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
            Set(window, "LinkedInProfileUrl", link);
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
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
            AssertValue(window, "OfficeNumber", "007B");
            Click(window, "ApplyImport");
            Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("007B → 0009C") &&
                Find(window, "ProfileTimeline").Current.Name.Contains("Imported") &&
                Find(window, "ProfileTimeline").Current.Name.Contains("Local edit"));
            Close(process);
            Directory.Delete(producer, true);

            process = Launch(executable);
            window = Window(process);
            AwaitRestoredWorkspace(window, workspace);
            OpenPerson(window, "morgan@example.com");
            Await(() => Find(window, "ProfileHeading").Current.Name == "Ave");
            AssertPreferredNameReading(window, "Ave", "Edit");
            if (Find(window, "DirectoryName").Current.Name != "Avery Example")
                throw new InvalidOperationException("Preferred name hid the retained Directory name.");
            AssertValue(window, "OfficeNumber", "0009C");
            AssertValue(window, "Department", "");
            AssertValue(window, "Alias", "avery");
            AssertValue(window, "LinkedInProfileUrl", link);
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
