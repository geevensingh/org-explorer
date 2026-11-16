using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyCompiledWorkspaceVersions(Window window, WorkspaceViewModel workflow,
        IApplicationWorkflow first, Action deliver, Action<string> select)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!workflow.EditPreferredName("avery@example.com", "Compiled retained local") ||
            !first.EditPreferredName("avery@example.com", "Compiled deliberate incoming") || !first.SyncNow())
            throw new InvalidOperationException("Could not prepare real compiled divergent versions.");
        var local = workflow.Synchronization.LocalRevision;
        var incoming = first.Synchronization.LocalRevision;
        deliver();
        Invoke("OpenSettingsButton");
        Invoke("SyncNowButton");
        PumpUntil(() => workflow.Synchronization.Diverged && Control<Button>("SyncNowButton").IsEnabled);
        if (!Control<TextBlock>("WorkspaceVersionGuidance").Text.Contains("paused") ||
            Control<FrameworkElement>("WorkspaceVersionsPanel").Visibility != Visibility.Visible)
            throw new InvalidOperationException("Compiled Settings did not expose retained divergence and edit pause.");
        Invoke("OpenSettingsButton");
        select("morgan@example.com");
        if (Control<TextBlock>("Email").Text != "morgan@example.com" || Control<Button>("EditProfileButton").IsEnabled)
            throw new InvalidOperationException("Conflict did not preserve compiled browsing while pausing actual profile editing.");
        Invoke("OpenSettingsButton");
        Invoke("ReviewWorkspaceVersionsButton");
        PumpUntil(() => Control<ListBox>("WorkspaceVersionChoices").Items.Count >= 2 &&
            Control<Button>("ReviewWorkspaceVersionsButton").IsEnabled);
        var choices = Control<ListBox>("WorkspaceVersionChoices");
        if (choices.SelectedItem is not null || Control<Button>("ChooseWorkspaceVersionButton").IsEnabled)
            throw new InvalidOperationException("A whole Workspace version was selected without a deliberate choice.");
        choices.SelectedItem = choices.Items.Cast<WorkspaceVersion>().Single(item => item.Revision == incoming);
        Invoke("ChooseWorkspaceVersionButton");
        PumpUntil(() => !workflow.Synchronization.Diverged && Control<Button>("ReviewWorkspaceVersionsButton").IsEnabled);
        select("avery@example.com");
        if (workflow.GetRelationships("avery@example.com")!.Person.PreferredName != "Compiled deliberate incoming" ||
            workflow.GetWorkspaceVersionReview() is not { } retained ||
            !retained.Versions.Any(item => item.Revision == local && item.Retained) ||
            !retained.Versions.Any(item => item.Revision == incoming && item.Retained))
            throw new InvalidOperationException("Compiled deliberate selection merged data or removed unchosen recovery.");
        Console.WriteLine("PASS: invisible compiled conflict status, edit pause, usable Person selection and deliberate complete-version choice retain both real store versions. Not native interaction evidence.");
    }

    private static void RunWorkspaceConflictJourney(AutomationElement window, string source, string target,
        string outgoing, string incoming, LocalPersonJourney expected)
    {
        SetSettingsVisible(window, false);
        SelectTreePerson(window, "avery@example.com", expectNavigation: false);
        Await(() => CurrentPersonEmail(window) == "avery@example.com");
        BeginProfileEditing(window);
        Set(window, "OfficeNumber", "Local complete conflict 97");
        SaveProfileEditing(window);
        AssertProfileValue(window, "OfficeNumber", "Local complete conflict 97");
        Guid local, remote;
        using (var reading = ApplicationWorkflow.CreateFileBacked())
        {
            if (!reading.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly))
                throw new InvalidOperationException(reading.Status);
            local = reading.Synchronization.LocalRevision!.Value;
        }
        using (var editing = ApplicationWorkflow.CreateFileBacked())
        {
            if (!editing.OpenWorkspace(source, WorkspaceOpenMode.ExistingOnly) ||
                editing.CreatePersonNote("avery@example.com", "Incoming conflict 97", "Complete incoming writing") is null ||
                !editing.SyncNow())
                throw new InvalidOperationException("Could not publish the real divergent incoming version: " + editing.Status);
            remote = editing.Synchronization.LocalRevision!.Value;
        }
        foreach (var file in Directory.GetFiles(outgoing, "*.oesnapshot"))
            File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)), true);
        SetSettingsVisible(window, true);
        Find(window, "SyncNow").SetFocus();
        Await(() => Find(window, "SyncNow").Current.IsEnabled);
        Click(window, "SyncNow");
        Await(() => Find(window, "SyncNow").Current.IsEnabled &&
            Find(window, "WorkspaceVersionGuidance").Current.Name.Contains("New edits are paused"));
        CaptureVisualEvidence(window, "sqlite-conflict-visible");
        SetSettingsVisible(window, false);
        SelectTreePerson(window, "morgan@example.com");
        Await(() => CurrentPersonEmail(window) == "morgan@example.com");
        if (Find(window, "EditProfile").Current.IsEnabled || Find(window, "EditPreferredName").Current.IsEnabled)
            throw new InvalidOperationException("Native conflict left new profile/Preferred-name editing enabled.");
        SelectTreePerson(window, "avery@example.com");
        Await(() => CurrentPersonEmail(window) == "avery@example.com");
        AssertProfileValue(window, "OfficeNumber", "Local complete conflict 97");
        AssertPhotoVisible(window);
        CaptureVisualEvidence(window, "sqlite-conflict-browsing-paused");
        SetSettingsVisible(window, true);
        Find(window, "ReviewWorkspaceVersions").SetFocus();
        Await(() => Find(window, "ReviewWorkspaceVersions").Current.IsEnabled);
        Click(window, "ReviewWorkspaceVersions");
        AutomationElement? choice = null;
        Await(() => Find(window, "ReviewWorkspaceVersions").Current.IsEnabled &&
            (choice = Find(window, "WorkspaceVersionChoices").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, remote.ToString("D")))) is not null);
        if (Find(window, "ChooseWorkspaceVersion").Current.IsEnabled)
            throw new InvalidOperationException("Native resolution defaulted to a version without selection.");
        choice!.SetFocus();
        ((SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Find(window, "ChooseWorkspaceVersion").SetFocus();
        Await(() => Find(window, "ChooseWorkspaceVersion").Current.IsEnabled);
        CaptureVisualEvidence(window, "sqlite-conflict-deliberate-choice");
        Click(window, "ChooseWorkspaceVersion");
        Await(() => Find(window, "ReviewWorkspaceVersions").Current.IsEnabled &&
            !Find(window, "SynchronizationStatus").Current.Name.Contains("Synchronization failed"));
        SetSettingsVisible(window, false);
        SelectTreePerson(window, "avery@example.com", expectNavigation: false);
        Await(() => CurrentPersonEmail(window) == "avery@example.com" && Find(window, "EditProfile").Current.IsEnabled);
        AssertProfileValue(window, "OfficeNumber", "009D");
        AssertPhotoVisible(window);
        CaptureVisualEvidence(window, "sqlite-conflict-selected-complete");
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        if (!reopened.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly) || reopened.Synchronization.Diverged ||
            reopened.GetRelationships("avery@example.com")!.Person.OfficeNumber != "009D" ||
            !reopened.GetPersonNotes("avery@example.com").Any(note => note.Title == "Incoming conflict 97" &&
                note.Markdown == "Complete incoming writing") ||
            reopened.GetWorkspaceVersionReview() is not { } recovery ||
            !recovery.Versions.Any(item => item.Revision == local && item.Retained) ||
            !recovery.Versions.Any(item => item.Revision == remote && item.Retained) ||
            reopened.GetHistoricalTeamContexts("avery@example.com").Single(c => c.Id == expected.CaptureId)
                .TeamNotes.Single(n => n.Id == expected.NoteId).Version != expected.FrozenVersion ||
            Convert.ToHexString(SHA256.HashData(reopened.GetPhoto("avery@example.com")!.Bytes.Span)) != expected.PhotoSha256)
            throw new InvalidOperationException("Native whole-version selection lost exact data/resources/history or unchosen recovery.");
        using var process = Process.GetProcessById(window.Current.ProcessId);
        using var executable = File.OpenRead(process.MainModule!.FileName);
        var evidence = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-workspace-conflicts.json");
        if (File.Exists(evidence)) throw new InvalidOperationException("Conflict evidence already exists.");
        File.WriteAllText(evidence, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            executableSha256 = Convert.ToHexString(SHA256.HashData(executable)), processId = window.Current.ProcessId,
            workspaceId = reopened.Synchronization.WorkspaceId, independentStores = new[] { source, target },
            presentedLocalRevision = local, deliberatelySelectedIncomingRevision = remote,
            resultingRevision = reopened.Synchronization.LocalRevision,
            retainedVersions = recovery.Versions.Where(item => item.Retained).ToArray(),
            selectedPerson = CurrentPersonEmail(window), expected.PhotoSha256, expected.CaptureId, expected.NoteId,
            expected.FrozenVersion,
            captures = new[] { "sqlite-conflict-visible", "sqlite-conflict-browsing-paused",
                "sqlite-conflict-deliberate-choice", "sqlite-conflict-selected-complete" },
            scope = "Normal WPF conflict visibility, actual edit-control pause, continued usable Person browsing, deliberate exact-version native selection and independent durable reopen. Not rich 10000/background-export latency or exit evidence."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: hosted complete Workspace conflict visibility/edit pause/browsing/deliberate choice and retained unchosen resources/history; candidate-bound evidence: " + evidence);
    }
}
