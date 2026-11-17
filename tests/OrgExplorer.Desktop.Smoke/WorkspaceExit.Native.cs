using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static bool ExitDecisionVisible(AutomationElement window)
    {
        var decision = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ExitDecision"));
        return decision is not null && !decision.Current.IsOffscreen;
    }

    private static void RequestNativeExit(Process process, AutomationElement window)
    {
        if (!process.CloseMainWindow()) throw new InvalidOperationException("The owned app did not accept its native close request.");
        Await(() => ExitDecisionVisible(window) && new[] { "CancelExit", "ExitAnyway" }.All(id =>
        {
            var button = Find(window, id).Current;
            return button.IsEnabled && !button.IsOffscreen &&
                Inside(window.Current.BoundingRectangle, button.BoundingRectangle);
        }));
    }

    private static bool Inside(System.Windows.Rect viewport, System.Windows.Rect target) =>
        SnapshotNativeUsability.Within(new(viewport.X, viewport.Y, viewport.Width, viewport.Height),
            new(target.X, target.Y, target.Width, target.Height));

    private static void ConfirmNativeExitAnyway(Process process, AutomationElement window)
    {
        try { Click(window, "ExitAnyway"); }
        catch (ElementNotAvailableException) when (process.HasExited) { }
        if (!process.WaitForExit(10000)) throw new TimeoutException("Confirmed Exit anyway did not terminate the owned application.");
        if (process.ExitCode != 0) throw new InvalidOperationException("Confirmed Exit anyway failed: " + process.StandardError.ReadToEnd());
    }

    private static void CloseUnpublishedWorkspace(Process process)
    {
        var window = Window(process);
        AwaitLocalWorkIdle(window);
        RequestNativeExit(process, window);
        Await(() =>
        {
            var risk = Find(window, "ExitRisk").Current.Name;
            return risk.Contains("remains on this computer", StringComparison.Ordinal) &&
                !risk.Contains("may be lost", StringComparison.Ordinal);
        });
        ConfirmNativeExitAnyway(process, window);
    }

    private static void RunHostedWorkspaceExit(string executable, string root)
    {
        var observations = new List<object>();
        VerifyHostedLatestNormalExit(executable, root, observations);
        VerifyHostedUpdateExit(executable, root, observations);
        foreach (var mode in ExitProcessCases)
        {
            var fixture = Path.Combine(root, "exit-" + mode);
            using var process = StartWorkspaceExitFixture(executable, fixture, mode, native: true);
            Applications.Add(process);
            try
            {
                var window = Window(process);
                Await(() => File.Exists(Path.Combine(fixture, "ready.json")));
                EnsurePeopleBrowsing(window);
                RequestNativeExit(process, window);
                if (mode == "active-export") Await(() => File.Exists(Path.Combine(fixture, "held")));
                if (mode == "divergence")
                    Await(() => Find(window, "ExitRisk").Current.Name.Contains("conflict is unresolved", StringComparison.Ordinal));
                if (mode == "failed-export")
                    Await(() => Find(window, "ExitRisk").Current.Name.Contains("unavailable", StringComparison.Ordinal));
                var risk = Find(window, "ExitRisk").Current.Name;
                var unsaved = mode is "pending-save" or "stalled-save" or "committed-unknown" or "failed-save" or "profile-saving";
                if (unsaved ? !risk.Contains("may be lost", StringComparison.Ordinal) :
                    !risk.Contains("remains on this computer", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Native {mode} did not distinguish local loss from durable unpublished work: {risk}");
                var before = CurrentPersonEmail(window);
                var writing = mode is "pending-save" or "stalled-save" or "committed-unknown"
                    ? Value(window, "TeamNoteMarkdown") : null;
                var department = mode is "failed-save" or "profile-saving" ? Value(window, "Department") : null;
                Click(window, "CancelExit");
                Await(() => !ExitDecisionVisible(window));
                if (process.HasExited || CurrentPersonEmail(window) != before ||
                    writing is not null && Value(window, "TeamNoteMarkdown") != writing ||
                    department is not null && Value(window, "Department") != department)
                    throw new InvalidOperationException("Native Cancel lost the exact owner/draft or terminated.");
                var manager = Find(window, "People").FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "morgan@example.com"))!;
                ((ExpandCollapsePattern)manager.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                SelectTreePerson(window, "avery@example.com", PersonSelection.Mode.Recovery);
                Await(() => CurrentPersonEmail(window) == "avery@example.com" &&
                    !Find(window, "Email").Current.IsOffscreen);
                SelectTreePerson(window, "morgan@example.com", PersonSelection.Mode.Recovery);
                if (writing is not null && Value(window, "TeamNoteMarkdown") != writing ||
                    department is not null && Value(window, "Department") != department)
                    throw new InvalidOperationException("Browsing after Cancel lost retained owner writing.");
                RequestNativeExit(process, window);
                var capture = "sqlite-exit-" + mode;
                CaptureVisualEvidence(window, capture);
                var host = process.MainModule!.FileName;
                var requested = Stopwatch.GetTimestamp();
                ConfirmNativeExitAnyway(process, window);
                var exited = Stopwatch.GetTimestamp();
                if (File.Exists(Path.Combine(fixture, "closed")) || File.Exists(Path.Combine(fixture, "force-returned")))
                    throw new InvalidOperationException("Forced exit reentered a secondary Closing/Closed path.");
                VerifyExitedWorkspace(fixture, mode);
                observations.Add(new
                {
                    mode, processId = process.Id, hostExecutable = host,
                    hostSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(host))),
                    risk, writing, department, selectedOwner = before, cancelPreserved = true,
                    browsedTo = "avery@example.com", returnedTo = "morgan@example.com",
                    requested, exited, frequency = Stopwatch.Frequency, actualProcessExited = process.HasExited,
                    process.ExitCode, committedRecoveryAndPublicationVerified = true, capture
                });
            }

            finally { StopApplication(process); }
        }
        var evidence = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-exit-lifecycle.json");
        File.WriteAllText(evidence, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            executable = Path.GetFullPath(executable),
            host = "Actual compiled product MainWindow in owned hosted WPF processes; only real transaction/publication boundaries are controlled. Not ordinary executable startup or latency proof.",
            executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))),
            productAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(executable, ".dll")))),
            observations
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: hosted native exit choices, exact Cancel/browsing, actual forced process termination and transactional reopen/publication matrix.");
    }

    private static void VerifyHostedUpdateExit(string executable, string root, List<object> observations)
    {
        var fixture = Path.Combine(root, "exit-update-handoff");
        using var process = StartWorkspaceExitFixture(executable, fixture, "update-handoff", native: true);
        Applications.Add(process);
        try
        {
            var window = Window(process);
            Await(() => File.Exists(Path.Combine(fixture, "ready.json")));
            RequestNativeExit(process, window);
            Await(() => Find(window, "ExitRisk").Current.Name.Contains("Update handoff is pending", StringComparison.Ordinal));
            var risk = Find(window, "ExitRisk").Current.Name;
            CaptureVisualEvidence(window, "sqlite-exit-update-handoff");
            ConfirmNativeExitAnyway(process, window);
            if (File.Exists(Path.Combine(fixture, "closed")))
                throw new InvalidOperationException("The confirmed native update override reentered the secondary close path.");
            VerifyExitedWorkspace(fixture, "update-handoff");
            observations.Add(new
            {
                mode = "update-handoff", processId = process.Id, risk, actualSignedUpdateSafePointPending = true,
                actualTransactionStillHeldAtExit = true, actualProcessExited = process.HasExited, process.ExitCode,
                committedRecoveryAndPublicationVerified = true, capture = "sqlite-exit-update-handoff"
            });
        }
        finally { StopApplication(process); }
    }

    private static void VerifyHostedLatestNormalExit(string executable, string root, List<object> observations)
    {
        var fixture = Path.Combine(root, "exit-normal-latest");
        using var process = StartWorkspaceExitFixture(executable, fixture, "normal-latest", native: true);
        Applications.Add(process);
        try
        {
            var window = Window(process);
            Await(() => File.Exists(Path.Combine(fixture, "ready.json")));
            EnsurePeopleBrowsing(window);
            RequestNativeExit(process, window);
            Await(() => File.Exists(Path.Combine(fixture, "held")));
            CaptureVisualEvidence(window, "sqlite-exit-normal-held");
            Click(window, "CancelExit");
            Await(() => !ExitDecisionVisible(window));
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Latest normal exit");
            var note = Guid.Parse(Find(window, "TeamNoteHeading").Current.HelpText);
            var editor = Find(window, "TeamNoteMarkdown");
            var viewport = Find(window, "ProfileScroll");
            var scrolling = (ScrollPattern)viewport.GetCurrentPattern(ScrollPattern.Pattern);
            bool EditorVisible() => !editor.Current.IsOffscreen &&
                Inside(viewport.Current.BoundingRectangle, editor.Current.BoundingRectangle);
            MainReadingNavigation.Reveal(EditorVisible, () => new(scrolling.Current.VerticallyScrollable,
                scrolling.Current.VerticalScrollPercent, scrolling.Current.VerticalViewSize),
                percent => scrolling.SetScrollPercent(ScrollPattern.NoScroll, percent), Await);
            Await(() => EditorVisible() && editor.Current.IsEnabled &&
                !((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).Current.IsReadOnly);
            Set(window, "TeamNoteMarkdown", "Newer native writing after canceled close");
            Await(() => CurrentPersonEmail(window) == "morgan@example.com" &&
                Find(window, "TeamNoteHeading").Current.HelpText == note.ToString() &&
                Value(window, "TeamNoteMarkdown") == "Newer native writing after canceled close" && EditorVisible());
            CaptureVisualEvidence(window, "sqlite-exit-normal-newer-writing");
            Guid? latest = null;
            bool ReopenedLatest(bool published)
            {
                using var reading = ApplicationWorkflow.CreateFileBacked();
                if (!reading.OpenWorkspace(Path.Combine(fixture, "local"), WorkspaceOpenMode.ExistingOnly) ||
                    reading.BeginNoteDraft(NoteScope.Team, "morgan@example.com", note)?.Markdown != "Newer native writing after canceled close")
                    return false;
                latest = reading.Synchronization.LocalRevision;
                return !published || !reading.Synchronization.HasUnpublishedChanges;
            }
            Await(() => ReopenedLatest(false));
            File.WriteAllText(Path.Combine(fixture, "release"), "Release the original export, not the close intent.");
            Await(() => File.Exists(Path.Combine(fixture, "export-ended")));
            using (var reading = ApplicationWorkflow.CreateFileBacked())
            {
                if (!reading.OpenWorkspace(Path.Combine(fixture, "local"), WorkspaceOpenMode.ExistingOnly) ||
                    !reading.Synchronization.HasUnpublishedChanges || reading.Synchronization.LocalRevision != latest)
                    throw new InvalidOperationException("The canceled close's older export acknowledged newer native writing.");
            }
            if (process.HasExited || ExitDecisionVisible(window))
                throw new InvalidOperationException("Late publication closed a canceled native intent.");
            if (!process.CloseMainWindow()) throw new InvalidOperationException("The fresh normal close request was not admitted.");
            if (!process.WaitForExit(10000) || process.ExitCode != 0 ||
                !File.Exists(Path.Combine(fixture, "closed")) || !ReopenedLatest(true))
                throw new InvalidOperationException("A fresh native normal close failed to publish the latest revision and terminate.");
            observations.Add(new
            {
                mode = "normal-latest", processId = process.Id, note, latest,
                exactWriting = "Newer native writing after canceled close",
                canceledIntentStayedOpen = true, oldExportLeftNewerWritingUnpublished = true,
                normalClosePublishedLatest = true, actualProcessExited = process.HasExited, process.ExitCode,
                captures = new[] { "sqlite-exit-normal-held", "sqlite-exit-normal-newer-writing" }
            });
        }
        finally { StopApplication(process); }
    }
}
