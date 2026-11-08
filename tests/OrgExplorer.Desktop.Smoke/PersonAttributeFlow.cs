using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Automation;
using static OrgExplorer.Desktop.Smoke.Program;

namespace OrgExplorer.Desktop.Smoke;

internal static class PersonAttributeFlow
{
    internal static void Run(string executable, string root)
    {
        var workspace = Path.Combine(root, "attributes-workspace");
        var import = Path.Combine(root, "attributes.json");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/profile";
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
            """);
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Await(() => Find(window, "Status").Current.Name.Contains("Workspace opened"));
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            OpenPerson(window);
            (string Id, string Value)[] edits =
            [
                ("DirectoryNameEditor", "Avery Directory"), ("PreferredName", "Ave"),
                ("Alias", "avery"), ("JobTitle", "Engineer"), ("Department", "Research"),
                ("OfficeBuilding", "Building A"), ("OfficeNumber", "007B"),
                ("OfficeState", "Washington"), ("OfficeCountry", "United States"),
                ("LinkedInProfileUrl", url)
            ];
            foreach (var (id, value) in edits)
            {
                Set(window, id, value);
                Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            }
            if (Find(window, "Email").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("Email identity must not be an editable control.");
            if (!Find(window, "OpenLinkedIn").Current.IsEnabled)
                throw new InvalidOperationException("A saved explicit link should offer a deliberate Open action.");
            Set(window, "OfficeNumber", " ");
            Await(() => Find(window, "Status").Current.Name.Contains("officeNumber"));
            Set(window, "OfficeNumber", "007B");
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            Set(window, "LinkedInProfileUrl", "http://127.0.0.1/profile");
            Await(() => Find(window, "Status").Current.Name.Contains("HTTPS"));
            Set(window, "LinkedInProfileUrl", url);
            Await(() => Find(window, "Status").Current.Name == "Saved locally.");
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window);
            foreach (var (id, value) in edits) AssertValue(window, id, value);
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"40000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-02T17:00:00Z","people":[
                 {"email":"AVERY@example.com","officeNumber":"0009C","department":null}]}
                """);
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Await(() => Find(window, "Preview").Current.Name.Contains("0009C") &&
                        Find(window, "Preview").Current.Name.Contains("(clear)"));
            AssertValue(window, "OfficeNumber", "007B");
            Click(window, "ApplyImport");
            Await(() => Find(window, "Status").Current.Name.Contains("applied"));
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window);
            AssertValue(window, "OfficeNumber", "0009C");
            AssertValue(window, "Department", "");
            foreach (var (id, value) in edits.Where(e => e.Id is not "OfficeNumber" and not "Department"))
                AssertValue(window, id, value);
            if (listener.Pending()) throw new InvalidOperationException("Rendering a profile fetched the remote link.");
            Console.WriteLine("PASS: actual WPF textual edits → validation → close/reopen → partial import/clear → reopen; rendering made no remote request.");
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(10000)) { process.Kill(true); process.WaitForExit(5000); }
            }
        }
    }

    private static void AssertValue(AutomationElement window, string id, string expected)
    {
        var actual = ((ValuePattern)Find(window, id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        if (actual != expected) throw new InvalidOperationException($"{id}: expected '{expected}', got '{actual}'.");
    }
}
