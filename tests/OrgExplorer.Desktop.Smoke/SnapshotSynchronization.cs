using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifySnapshotSynchronization(Type windowType, string settings, Func<Window, string?> chooseFolder)
    {
        var source = Path.Combine(settings, "snapshot-source");
        var target = Path.Combine(settings, "snapshot-target");
        var outgoing = Path.Combine(settings, "snapshot-out");
        var incoming = Path.Combine(settings, "snapshot-in");
        using var first = ApplicationWorkflow.CreateFileBacked();
        if (!first.OpenWorkspace(source, WorkspaceOpenMode.CreateLocalSqlite) ||
            !first.ApplyImport(first.PreviewImport("""
                {"schemaVersion":1,"batchId":"96000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery Snapshot","managerEmail":null}]}
                """).Id) || !first.ConfigureSynchronizationFolder(outgoing) || !first.SyncNow())
            throw new InvalidOperationException("Could not prepare the real compiled synchronization source: " + first.Status);
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(target, WorkspaceOpenMode.CreateLocalSqlite))
                throw new InvalidOperationException(seed.Status);
        Directory.CreateDirectory(incoming);
        void Deliver()
        {
            foreach (var file in Directory.GetFiles(outgoing, "*.oesnapshot"))
                File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)), true);
        }
        Deliver();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
        WorkspaceViewModel? workflow = null;
        try
        {
            T Control<T>(string name) where T : FrameworkElement => window.FindName(name) as T ??
                throw new InvalidOperationException("Missing compiled synchronization control: " + name);
            void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Invoke("OpenSettingsButton");
            ChooseCompiledWorkspace(window, target);
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready");
            workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            Invoke("OpenSettingsButton");
            if (Control<FrameworkElement>("SynchronizationPanel").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Local SQLite Settings does not expose synchronization.");
            CompiledWorkspaceChoices.Enqueue(incoming);
            Invoke("JoinSynchronizationFolderButton");
            PumpUntil(() => workflow.Synchronization.Folder == incoming &&
                Control<Button>("SyncNowButton").IsEnabled);
            Invoke("SyncNowButton");
            PumpUntil(() => workflow.People.Count == 1 && Control<Button>("SyncNowButton").IsEnabled);
            if (workflow.Synchronization.WorkspaceId != first.Synchronization.WorkspaceId ||
                !Control<TextBlock>("SynchronizationStatus").Text.Contains("delivery elsewhere is unknown"))
                throw new InvalidOperationException("Compiled Sync now lost Workspace identity or falsely acknowledged provider delivery.");
            Invoke("OpenSettingsButton");
            var node = workflow.ReportingRoots.Single();
            Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, node,
                TreeView.SelectedItemChangedEvent));
            if (Control<TextBlock>("Email").Text != "avery@example.com")
                throw new InvalidOperationException("Joined SQLite data did not become browsable through actual compiled selection.");
            if (!first.EditPreferredName("avery@example.com", "Incoming continuation") || !first.SyncNow())
                throw new InvalidOperationException(first.Status);
            Deliver();
            Invoke("OpenSettingsButton");
            Invoke("SyncNowButton");
            PumpUntil(() => workflow.GetRelationships("avery@example.com")!.Person.PreferredName == "Incoming continuation" &&
                Control<Button>("SyncNowButton").IsEnabled);
            Invoke("OpenSettingsButton");
            if (Control<TextBlock>("ProfileHeading").Text != "Incoming continuation" ||
                Control<TextBlock>("Email").Text != "avery@example.com" ||
                workflow.PeopleNavigation.SelectedEmail != "avery@example.com")
                throw new InvalidOperationException("Compiled incoming adoption lost the selected Person or left stale reading.");
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Snapshot compiled verification must not show or create a native window.");
            Console.WriteLine("PASS: compiled SQLite Settings folder/Join/Sync now validates real artifacts, preserves identity and selected Person, and reports publication separately from unknown provider delivery without a visible window/native handle.");
        }
        finally
        {
            workflow?.Dispose();
            window.Close();
        }
    }

    private static void RunSnapshotSynchronizationJourney(AutomationElement window, string source, string root,
        LocalPersonJourney expected)
    {
        var outgoing = Path.Combine(root, "sqlite-snapshot-out");
        var incoming = Path.Combine(root, "sqlite-snapshot-in");
        Directory.CreateDirectory(outgoing);
        Directory.CreateDirectory(incoming);
        void PrepareAction(string id)
        {
            Find(window, id).SetFocus();
            Await(() => !Find(window, id).Current.IsOffscreen && Find(window, id).Current.IsEnabled);
        }
        void Synchronize()
        {
            PrepareAction("SyncNow");
            Click(window, "SyncNow");
            Await(() => Find(window, "SyncNow").Current.IsEnabled &&
                Find(window, "SynchronizationStatus").Current.Name.Contains("Locally saved and published.") &&
                Find(window, "SynchronizationStatus").Current.Name.Contains("delivery elsewhere is unknown"));
        }
        void Deliver()
        {
            foreach (var file in Directory.GetFiles(outgoing, "*.oesnapshot"))
                File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)), true);
        }
        SetSettingsVisible(window, true);
        PrepareAction("ConfigureSynchronizationFolder");
        PickWorkspaceFolder(window, outgoing, "ConfigureSynchronizationFolder");
        Await(() => Find(window, "SynchronizationFolder").Current.Name == outgoing);
        Synchronize();
        CaptureVisualEvidence(window, "sqlite-sync-folder-published");
        Guid? identity, originalRevision;
        using (var reading = ApplicationWorkflow.CreateFileBacked())
        {
            if (!reading.OpenWorkspace(source, WorkspaceOpenMode.ExistingOnly) ||
                reading.Synchronization.HasUnpublishedChanges)
                throw new InvalidOperationException("Native Sync now did not durably record complete local publication.");
            identity = reading.Synchronization.WorkspaceId;
            originalRevision = reading.Synchronization.LocalRevision;
        }
        Deliver();
        PrepareAction("CreateLocalSqliteWorkspace");
        Click(window, "CreateLocalSqliteWorkspace");
        Await(() => Status(window) == "SQLite Workspace created and saved locally.");
        var target = new WorkspaceSettings(Path.Combine(root, "sqlite-app-settings", "workspace-settings.json")).LastWorkspacePath!;
        if (target == source) throw new InvalidOperationException("Fresh join did not create an independent authoritative store.");
        SetSettingsVisible(window, true);
        PrepareAction("JoinSynchronizationFolder");
        PickWorkspaceFolder(window, incoming, "JoinSynchronizationFolder");
        Await(() => Find(window, "SynchronizationFolder").Current.Name == incoming);
        Synchronize();
        SetSettingsVisible(window, false);
        EnsurePeopleBrowsing(window);
        SelectTreePerson(window, "morgan@example.com");
        Await(() => CurrentPersonEmail(window) == "morgan@example.com");
        AssertTeamNameReading(window, "Platform");
        SelectTreePerson(window, "avery@example.com");
        Await(() => CurrentPersonEmail(window) == "avery@example.com");
        AssertProfileValue(window, "OfficeNumber", "008C");
        AssertPhotoVisible(window);
        AssertTeamNameReading(window, "Platform");
        CaptureVisualEvidence(window, "sqlite-sync-joined-person-history");
        using (var reading = ApplicationWorkflow.CreateFileBacked())
        {
            if (!reading.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly) ||
                reading.Synchronization.WorkspaceId != identity || reading.Synchronization.LocalRevision != originalRevision ||
                reading.GetHistoricalTeamContexts("avery@example.com").Single(c => c.Id == expected.CaptureId)
                    .TeamNotes.Single(n => n.Id == expected.NoteId).Version != expected.FrozenVersion ||
                Convert.ToHexString(SHA256.HashData(reading.GetPhoto("avery@example.com")!.Bytes.Span)) != expected.PhotoSha256)
                throw new InvalidOperationException("Native fresh join did not preserve the full real resource/history/identity.");
        }
        Guid? continuation;
        using (var editing = ApplicationWorkflow.CreateFileBacked())
        {
            if (!editing.OpenWorkspace(source, WorkspaceOpenMode.ExistingOnly) ||
                !editing.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "009D") || !editing.SyncNow())
                throw new InvalidOperationException("Could not publish the real second-store continuation: " + editing.Status);
            continuation = editing.Synchronization.LocalRevision;
        }
        Deliver();
        SetSettingsVisible(window, true);
        Synchronize();
        SetSettingsVisible(window, false);
        Await(() => CurrentPersonEmail(window) == "avery@example.com");
        AssertProfileValue(window, "OfficeNumber", "009D");
        AssertPhotoVisible(window);
        AssertTeamNameReading(window, "Platform");
        CaptureVisualEvidence(window, "sqlite-sync-incoming-continuation");
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        if (!reopened.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly) ||
            reopened.Synchronization.LocalRevision != continuation ||
            reopened.GetRelationships("avery@example.com")!.Person.OfficeNumber != "009D" ||
            reopened.GetHistoricalTeamContexts("avery@example.com").Single(c => c.Id == expected.CaptureId)
                .TeamNotes.Single(n => n.Id == expected.NoteId).Version != expected.FrozenVersion)
            throw new InvalidOperationException("Native incoming adoption was not complete and durable.");
        using var process = Process.GetProcessById(window.Current.ProcessId);
        using var executable = File.OpenRead(process.MainModule!.FileName);
        var evidence = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-snapshots.json");
        if (File.Exists(evidence)) throw new InvalidOperationException("Snapshot evidence already exists.");
        File.WriteAllText(evidence, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            executableSha256 = Convert.ToHexString(SHA256.HashData(executable)),
            processId = window.Current.ProcessId,
            interchange = "OrgExplorer complete logical Workspace; envelope 1; SQLite logical format 3",
            workspaceId = identity,
            originalRevision,
            continuationRevision = continuation,
            independentStores = new[] { source, target },
            selectedPerson = CurrentPersonEmail(window),
            officeNumber = "009D",
            teamName = "Platform",
            photoSha256 = expected.PhotoSha256,
            captureId = expected.CaptureId,
            frozenNoteId = expected.NoteId,
            frozenVersion = expected.FrozenVersion,
            artifacts = Directory.GetFiles(incoming, "*.oesnapshot").Select(path => new
            { name = Path.GetFileName(path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray(),
            providerDelivery = "Unknown; deterministic copying of actual complete artifacts substitutes only external provider delivery.",
            captures = new[] { "sqlite-sync-folder-published", "sqlite-sync-joined-person-history", "sqlite-sync-incoming-continuation" },
            scope = "Normal native Settings folder, Sync now, fresh join and automatic clean continuation; real independent logical stores/resources/history. Not periodic scheduling, conflict resolution, retention, exit or 10,000-Person export evidence."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: hosted SQLite snapshots: native Settings folder/Sync now/status, independent-store fresh join and selected-Person incoming adoption preserve actual photos/private Team names/frozen history; candidate-bound evidence: " + evidence);
    }
}
