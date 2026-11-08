using System.Diagnostics;
using System.IO;
using static OrgExplorer.Desktop.Smoke.Program;

namespace OrgExplorer.Desktop.Smoke;

internal static class ProfileHistoryFlow
{
    internal static void Run(string executable, string root)
    {
        var workspace = Path.Combine(root, "history-workspace");
        var import = Path.Combine(root, "history.json");
        Process? process = null;
        try
        {
            process = Launch(executable);
            var window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            Await(() => Find(window, "Status").Current.Name.Contains("Workspace opened"));
            Set(window, "ImportPath", import);
            Apply("""
                {"email":"avery@example.com","displayName":"Avery","officeNumber":"007B","managerEmail":"morgan@example.com"},
                {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
                {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
                """, 1, "2026-11-01T17:00:00Z");
            OpenPerson(window, "morgan@example.com");
            if (!Find(window, "ProfileTimeline").Current.Name.Contains("No recorded Profile changes"))
                throw new InvalidOperationException("Initial import fabricated pre-import Profile history.");
            Apply("""{"email":"avery@example.com","officeNumber":"0009C"}""", 2, "2026-11-03T17:00:00Z");
            Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("007B → 0009C"));
            var confirmation = Find(window, "FieldConfirmations").Current.Name;
            if (!confirmation.Contains("Manager:") || !confirmation.Contains("2026-11-01T17:00:00") ||
                !confirmation.Contains("2026-11-03T17:00:00"))
                throw new InvalidOperationException("Field-specific Manager and office dates were not shown.");
            Apply("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 3, "2026-11-06T17:00:00Z");
            OpenPerson(window, "alex@example.com");
            var transfer = Find(window, "ProfileTimeline").Current.Name;
            if (!transfer.Contains("morgan@example.com → alex@example.com") ||
                !transfer.Contains("Observed interval: after 2026-11-01T17:00:00") ||
                !transfer.Contains("by 2026-11-06T17:00:00") || !transfer.Contains("Imported") || !transfer.Contains("Recorded"))
                throw new InvalidOperationException("Actual WPF timeline lost the honest prior Manager interval or provenance.");
            Apply("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 4, "2026-11-07T17:00:00Z");
            if (Find(window, "ProfileTimeline").Current.Name != transfer)
                throw new InvalidOperationException("Identical explicit observation invented a Profile change.");
            Await(() => Find(window, "FieldConfirmations").Current.Name.Contains("2026-11-07T17:00:00"));
            Set(window, "OfficeNumber", "");
            Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("0009C → (clear)") &&
                        Find(window, "ProfileTimeline").Current.Name.Contains("Local edit"));
            Write("""{"email":"avery@example.com","managerEmail":null}""", 5, "2026-11-02T17:00:00Z");
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            if (!Find(window, "Preview").Current.Name.Contains("older evidence") ||
                !Find(window, "Preview").Current.Name.Contains("managerEmail"))
                throw new InvalidOperationException("Older Manager evidence was not highlighted before Apply.");
            Click(window, "ApplyImport");
            Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("Older evidence"));
            var expected = Find(window, "ProfileTimeline").Current.Name;
            if (!expected.Contains("Detected") || !expected.Contains("effective date unknown"))
                throw new InvalidOperationException("Stale reporting data did not show detection-only dates.");
            Close(process);
            process = Launch(executable);
            window = Window(process);
            Set(window, "WorkspacePath", workspace);
            Click(window, "OpenWorkspace");
            OpenPerson(window);
            if (Find(window, "ProfileTimeline").Current.Name != expected)
                throw new InvalidOperationException("Actual WPF Profile timeline did not reopen durably.");
            Console.WriteLine("PASS: WPF structured timeline → field confirmations → honest Manager interval → identical reconfirmation → local clear → stale preview/detection → reopen.");

            void Write(string records, int id, string observed) => File.WriteAllText(import, $$"""
                {"schemaVersion":1,"batchId":"10000000-0010-4000-8000-{{id:000000000000}}",
                 "observedAt":"{{observed}}","people":[{{records}}]}
                """);
            void Apply(string records, int id, string observed)
            {
                Write(records, id, observed);
                Click(window, "PreviewImport");
                Await(() => Find(window, "ApplyImport").Current.IsEnabled);
                Click(window, "ApplyImport");
                Await(() => Find(window, "Status").Current.Name.Contains("applied"));
            }
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
}
