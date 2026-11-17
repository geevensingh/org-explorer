using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static readonly string[] ExitProcessCases =
        ["unconfigured", "failed-export", "active-export", "pending-save", "stalled-save", "committed-unknown", "failed-save", "profile-saving", "divergence"];

    private sealed class ExitProcessBoundary(string root, string mode) : IWorkspaceFileObserver, IWorkspaceFilePublisher
    {
        public bool Armed;
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            var boundary = mode == "committed-unknown" ? WorkspaceStoragePhaseKind.End : WorkspaceStoragePhaseKind.Begin;
            if (!Armed || phase.Name != "local-sqlite-commit" || phase.Kind != boundary ||
                mode is not ("pending-save" or "stalled-save" or "committed-unknown" or "profile-saving" or "update-handoff")) return;
            File.WriteAllText(Path.Combine(root, "held"), phase.Name + ":" + phase.Kind);
            Thread.Sleep(Timeout.Infinite);
        }
        public void Publish(string staged, string destination, string? backup,
            WorkspaceFilePublicationMode publicationMode = WorkspaceFilePublicationMode.Automatic)
        {
            if (Armed && mode is "active-export" or "normal-latest" && destination.EndsWith(".oesnapshot", StringComparison.Ordinal))
            {
                File.WriteAllText(Path.Combine(root, "held"), "snapshot-publication");
                if (mode == "active-export") Thread.Sleep(Timeout.Infinite);
                while (!File.Exists(Path.Combine(root, "release"))) Thread.Sleep(10);
            }
            new WorkspaceFilePublisher().Publish(staged, destination, backup, publicationMode);
            if (Armed && mode == "normal-latest" && destination.EndsWith(".oesnapshot", StringComparison.Ordinal))
                File.WriteAllText(Path.Combine(root, "export-ended"), destination);
        }
    }

    private static int RunWorkspaceExitFixture(string[] args)
    {
        if (args.Length != 5 || !ExitProcessCases.Contains(args[3]) && args[3] is not ("normal-latest" or "update-handoff") ||
            args[4] is not ("invisible" or "native" or "startup-invisible") || !Path.IsPathFullyQualified(args[2]) ||
            !args[2].Contains(Path.DirectorySeparatorChar + ".test-workspaces" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Exit host requires its owned isolated Workspace and an explicit display mode.");
        Directory.CreateDirectory(args[2]);
        var observation = new ExitHostObservation(args[2], args[3], args[4]);
        try
        {
            observation.Record("created");
            return RunWorkspaceExitFixtureCore(args, observation);
        }
        catch (Exception primary)
        {
            try { observation.Record("failed", error: primary); }
            catch (Exception secondary)
            { ReportExitHostObservationFailure(primary, secondary); }
            throw;
        }
    }

    private static int RunWorkspaceExitFixtureCore(string[] args, ExitHostObservation observation)
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var executable = Path.GetFullPath(args[1]);
        var root = args[2];
        var mode = args[3];
        var path = Path.Combine(root, "local");
        var folder = Path.Combine(root, "sync");
        SeedRecoveryWorkspace(path);
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                mode != "unconfigured" && (!seed.ConfigureSynchronizationFolder(folder) || !seed.SyncNow()))
                throw new InvalidOperationException(seed.Status);
            if (mode is "active-export" or "failed-export" or "divergence" or "normal-latest")
            {
                if (mode == "divergence")
                {
                    var incoming = Path.Combine(root, "incoming");
                    Directory.CreateDirectory(incoming);
                    foreach (var file in Directory.GetFiles(folder, "*.oesnapshot"))
                        File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)));
                    using var other = ApplicationWorkflow.CreateFileBacked();
                    if (!other.OpenWorkspace(Path.Combine(root, "other"), WorkspaceOpenMode.CreateLocalSqlite) ||
                        !other.ConfigureSynchronizationFolder(incoming, true) || !other.SyncNow() ||
                        !other.EditPreferredName("avery@example.com", "Other complete version") || !other.SyncNow())
                        throw new InvalidOperationException(other.Status);
                    foreach (var file in Directory.GetFiles(incoming, "*.oesnapshot"))
                        File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), true);
                }
                if (!seed.EditPreferredName("morgan@example.com", "Durable unpublished exit writing"))
                    throw new InvalidOperationException(seed.Status);
            }
        }
        if (mode == "failed-export") Directory.Move(folder, Path.Combine(root, "unavailable"));
        observation.Record("seeded");
        var assemblyPath = Path.ChangeExtension(executable, ".dll");
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            resolver.ResolveAssemblyToPath(name) is { } resolved ? context.LoadFromAssemblyPath(resolved) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var effects = new ExitProcessBoundary(root, mode);
        var window = (Window)Activator.CreateInstance(assembly.GetType("OrgExplorer.Desktop.MainWindow", true)!,
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(fileObserver: effects, filePublisher: effects),
                (Func<Window, string?>)(_ => path)], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        observation.Record("window-created");
        window.Closed += (_, _) => File.WriteAllText(Path.Combine(root, "closed"), "Normal Closed event");
        FileStream? storageLock = null;
        SignedReleaseFixture? release = null;
        LocalReleaseServer? server = null;
        Guid? noteId = null;
        var preparationStarted = false;
        void Prepare()
        {
            if (preparationStarted) throw new InvalidOperationException("Exit host preparation was entered more than once.");
            preparationStarted = true;
            observation.Record("preparation", activity: ExitHostActivity(Control<TextBlock>("WorkflowActivity").Text),
                openEnabled: Control<Button>("OpenWorkspaceButton").IsEnabled, people: model.People.Count, requests: 0);
            var openRequested = false;
            PumpUntil(() =>
            {
                if (!openRequested)
                {
                    if (Control<TextBlock>("WorkflowActivity").Text != "Ready" ||
                        !Control<Button>("OpenWorkspaceButton").IsEnabled) return false;
                    openRequested = true;
                    observation.Record("open-requested", activity: "Ready", openEnabled: true, people: model.People.Count, requests: 1);
                    Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Control<Button>("OpenWorkspaceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (Control<TextBlock>("StatusText").Text.StartsWith("Navigation blocked:", StringComparison.Ordinal))
                        throw new InvalidOperationException("Exit host preparation Open was refused after startup readiness.");
                }
                return model.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready";
            });
            observation.Record("workspace-opened", activity: "Ready", openEnabled: true, people: model.People.Count, requests: 1);
            Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
                null!, model.ReportingRoots.Single(), TreeView.SelectedItemChangedEvent));
            effects.Armed = true;
            if (mode is "pending-save" or "stalled-save" or "committed-unknown")
            {
                var notes = Control<UserControl>("TeamNotes");
                ((Button)notes.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((TextBox)notes.FindName("NoteTitle")).Text = "Exit transaction";
                ((TextBox)notes.FindName("MarkdownEditor")).Text = "Exact interrupted owner writing";
                noteId = model.TeamNotesView.SelectedNote!.Id;
                PumpUntil(() => File.Exists(Path.Combine(root, "held")));
                if (mode != "pending-save") PumpUntil(() => model.LocalSave.Phase == LocalSavePhase.Stalled);
            }
            if (mode is "failed-save" or "profile-saving" or "update-handoff")
            {
                Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Control<TextBox>("Department").Text = "Unconfirmed exit department";
                if (mode == "failed-save")
                    storageLock = File.Open(Path.Combine(path, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None);
                Control<Button>("SaveProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => mode == "failed-save" ? model.LocalSave.Phase == LocalSavePhase.Failed :
                    File.Exists(Path.Combine(root, "held")));
            }
            if (mode == "update-handoff")
            {
                var fixture = Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE") ??
                    throw new InvalidOperationException("The update exit proof requires the existing ephemeral signed/effects build.");
                using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "trust.json")));
                var endpoint = new Uri(configuration.RootElement.GetProperty("manifestUrl").GetString()!);
                release = new SignedReleaseFixture { Package = ControlledFullPackage("9999.0.0") };
                release.Key.ImportFromPem(File.ReadAllText(Path.Combine(fixture, "ephemeral-private.pem")));
                File.WriteAllBytes(Path.Combine(fixture, "package.nupkg"), release.Package);
                File.Delete(Path.Combine(fixture, "handoff.json"));
                server = Task.Run(() => new LocalReleaseServer(
                    () => release.Sign(release.Manifest("9999.0.0")), endpoint.Port)).GetAwaiter().GetResult();
                Control<Button>("CheckForUpdatesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !Control<FrameworkElement>("ExplorerPanel").IsEnabled &&
                    !Control<Button>("OpenImportButton").IsEnabled);
                if (File.Exists(Path.Combine(fixture, "handoff.json")))
                    throw new InvalidOperationException("The updater handed off before the held local transaction.");
            }
            observation.Record("ready", people: model.People.Count, requests: 1);
            File.WriteAllText(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(new
            {
                mode, processId = Environment.ProcessId, noteId, revision = model.Synchronization.LocalRevision,
                peopleCount = model.People.Count, selectedOwner = model.PeopleNavigation.SelectedEmail,
                localPhase = model.LocalSave.Phase.ToString(), localOutcome = model.LocalSave.Outcome.ToString()
            }));
        }
        if (args[4] is "native" or "startup-invisible")
            window.Loaded += (_, _) =>
            {
                observation.Record("loaded");
                Prepare();
            };
        if (args[4] == "native")
        {
            // Native setup uses the compiled control path; the parent performs real UIA close/Cancel/Exit anyway.
            application.Run(window);
        }
        else
        {
            if (args[4] == "startup-invisible")
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                PumpUntil(() => File.Exists(Path.Combine(root, "ready.json")));
            }
            else Prepare();
            observation.Record("close-requested");
            window.Close();
            PumpUntil(() => Control<FrameworkElement>("ExitDecision").Visibility == Visibility.Visible &&
                (mode != "active-export" || File.Exists(Path.Combine(root, "held"))) &&
                (mode != "divergence" || model.Synchronization.Diverged) &&
                (mode != "failed-export" || model.Synchronization.Error is not null));
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The non-UI exit fixture created a native window.");
            File.WriteAllText(Path.Combine(root, "decision.json"), JsonSerializer.Serialize(new
            {
                risk = Control<TextBlock>("ExitRisk").Text, model.ExitStatus, model.LocalSave,
                model.Synchronization, noteId, processId = Environment.ProcessId
            }));
            observation.Record("force-requested");
            Control<Button>("ExitAnywayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            File.WriteAllText(Path.Combine(root, "force-returned"), "Exit anyway returned without terminating.");
        }
        GC.KeepAlive(storageLock);
        GC.KeepAlive(release);
        GC.KeepAlive(server);
        return mode == "normal-latest" ? 0 : 31;
    }

    private static Process StartWorkspaceExitFixture(string executable, string root, string mode, bool native,
        bool replayStartup = false)
    {
        var settings = Path.Combine(root, "settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "update-settings.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, checkOnStartup = false }));
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.Environment["ORG_EXPLORER_SETTINGS_DIRECTORY"] = settings;
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--workspace-exit-fixture",
            Path.GetFullPath(executable), root, mode, native ? "native" : replayStartup ? "startup-invisible" : "invisible" })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Owned exit fixture did not launch.");
    }

    private static void VerifyOwnedExitProcesses(string executable, string settings)
    {
        foreach (var mode in ExitProcessCases)
        {
            var root = Path.Combine(settings, ".test-workspaces", "exit-" + mode);
            using var process = StartWorkspaceExitFixture(executable, root, mode, native: false);
            try
            {
                if (!process.WaitForExit(10000))
                    throw new TimeoutException($"Exit anyway did not terminate the owned {mode} process.");
                var error = process.StandardError.ReadToEnd();
                if (process.ExitCode != 0 || !File.Exists(Path.Combine(root, "decision.json")) ||
                    File.Exists(Path.Combine(root, "force-returned")) || File.Exists(Path.Combine(root, "closed")))
                    throw new InvalidOperationException($"Owned {mode} did not terminate through the compiled force action: {process.ExitCode}; {error}");
                VerifyExitedWorkspace(root, mode);
                Console.WriteLine($"PASS: owned invisible compiled Exit anyway terminated PID {process.Id}: {mode}; actual durable recovery/republication verified.");
            }

            finally
            {
                if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
            }
        }
    }

    private static void VerifyOwnedUpdateExit(string executable, string settings)
    {
        var root = Path.Combine(settings, ".test-workspaces", "exit-update-handoff");
        using var process = StartWorkspaceExitFixture(executable, root, "update-handoff", native: false);
        try
        {
            if (!process.WaitForExit(10000) || process.ExitCode != 0 ||
                !File.Exists(Path.Combine(root, "decision.json")) || File.Exists(Path.Combine(root, "closed")) ||
                File.Exists(Path.Combine(root, "force-returned")))
                throw new InvalidOperationException("Exit anyway waited for or was vetoed by the actual signed updater: " +
                    (process.HasExited ? process.StandardError.ReadToEnd() : "owned process remains active"));
            using var decision = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "decision.json")));
            if (!decision.RootElement.GetProperty("risk").GetString()!.Contains("Update handoff is pending", StringComparison.Ordinal))
                throw new InvalidOperationException("The force-exit proof never reached the actual updater safe-point wait.");
            VerifyExitedWorkspace(root, "update-handoff");
            Console.WriteLine($"PASS: owned invisible compiled Exit anyway terminated PID {process.Id} during actual signed update handoff and a held real local transaction; committed recovery/republication verified.");
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
    }

    private static void VerifyExitedWorkspace(string root, string mode)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ready.json")));
        var noteId = state.RootElement.GetProperty("noteId").ValueKind == JsonValueKind.Null ? (Guid?)null :
            state.RootElement.GetProperty("noteId").GetGuid();
        using var reading = ApplicationWorkflow.CreateFileBacked();
        if (!reading.OpenWorkspace(Path.Combine(root, "local"), WorkspaceOpenMode.ExistingOnly))
            throw new InvalidOperationException("Exit damaged the committed Workspace: " + reading.Status);
        if (reading.LocalSave.EditsPaused && !reading.ReconcileLocalSaves())
            throw new InvalidOperationException("Exited original transaction could not reconcile: " + reading.Status);
        if (reading.GetRelationships("morgan@example.com")!.Person.Department is not null ||
            reading.GetPersonNotes("morgan@example.com").Single().Markdown != "Independent exact recovery writing")
            throw new InvalidOperationException("Exit committed a partial profile or lost previously committed writing.");
        if (noteId is { } note)
        {
            var recovered = reading.BeginNoteDraft(NoteScope.Team, "morgan@example.com", note);
            if (mode == "committed-unknown" ? recovered?.Markdown != "Exact interrupted owner writing" : recovered is not null)
                throw new InvalidOperationException("Exit recovery did not distinguish committed from incomplete transactions.");
        }
        if (mode == "divergence")
        {
            if (!reading.Synchronization.Diverged || reading.CanEditLocalWorkspace ||
                reading.GetWorkspaceVersionReview() is not { Versions.Count: >= 2 })
                throw new InvalidOperationException("Exit lost preserved divergence or resumed unsafe editing.");
            return;
        }
        var revision = reading.Synchronization.LocalRevision;
        if (mode == "failed-export") Directory.Move(Path.Combine(root, "unavailable"), Path.Combine(root, "sync"));
        if (mode == "unconfigured" && !reading.ConfigureSynchronizationFolder(Path.Combine(root, "sync")))
            throw new InvalidOperationException(reading.Status);
        if (!reading.SyncNow() || reading.Synchronization.HasUnpublishedChanges ||
            reading.Synchronization.LocalRevision != revision)
            throw new InvalidOperationException("Restart failed publication or replayed a local mutation: " + reading.Status);
    }
}
