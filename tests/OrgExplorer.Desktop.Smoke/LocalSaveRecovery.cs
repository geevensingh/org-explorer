using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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
    private const string RecoveryPeople = """
        {"schemaVersion":1,"batchId":"95000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null},
         {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"morgan@example.com"}]}
        """;

    private sealed class RecoveryCommitGate(Func<bool> enabled, Action entered, Func<bool> released) : IWorkspaceFileObserver
    {
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name != "local-sqlite-commit" || phase.Kind != WorkspaceStoragePhaseKind.Begin || !enabled()) return;
            entered();
            if (!released()) throw new TimeoutException("The owning recovery harness did not release its real SQLite commit.");
        }
    }

    private static void SeedRecoveryWorkspace(string path)
    {
        using var seed = ApplicationWorkflow.CreateFileBacked();
        if (!seed.OpenWorkspace(path, WorkspaceOpenMode.OpenOrCreate) ||
            !seed.ApplyImport(seed.PreviewImport(RecoveryPeople).Id) ||
            seed.CreatePersonNote("morgan@example.com", "Independent recovery note", "Independent exact recovery writing") is null)
            throw new InvalidOperationException("Recovery fixture creation failed: " + seed.Status);
    }

    private static void VerifyCompiledLocalSaveRecovery(Type windowType, string settings)
    {
        var path = Path.Combine(settings, "compiled-local-recovery");
        SeedRecoveryWorkspace(path);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = false;
        var observer = new RecoveryCommitGate(() => hold, entered.Set, () => release.Wait(TimeSpan.FromSeconds(15)));
        var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [app, (Func<Window, string?>)(_ => CompiledWorkspaceChoices.Dequeue())], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Guid? pendingNote = null;
        var surface = Control<UserControl>("TeamNotes");
        T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        void Select(string email, PersonSelection.Mode mode = PersonSelection.Mode.Ordinary) =>
            PersonSelection.Request(mode, () =>
        {
            if (workflow.LocalSave.EditsPaused)
                throw new TimeoutException("The actual compiled local fault is not an idle prerequisite for browsing.");
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready");
        }, () =>
        {
            var node = workflow.ReportingRoots.SelectMany(root => new[] { root }.Concat(root.Reports))
                .Single(item => item.Person.Email == email);
            Control<TreeView>("PeopleList").RaiseEvent(
                new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
        }, () =>
        {
            if (Control<TextBlock>("Email").Text != email)
                throw new InvalidOperationException("Recovery blocked actual compiled Person selection.");
            if (mode == PersonSelection.Mode.Recovery &&
                (!workflow.LocalSave.EditsPaused || Note<Button>("NewButton").IsEnabled ||
                    ((UserControl)window.FindName("PersonNotes")).FindName("NewButton") is not Button { IsEnabled: false }))
                throw new InvalidOperationException("Compiled recovery browsing cleared the fault or resumed edits.");
        });
        try
        {
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ChooseCompiledWorkspace(window, path);
            PumpUntil(() => workflow.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
            Select("morgan@example.com");
            Control<Button>("EditProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<TextBox>("TeamNameEditor").Text = "Compiled retained recovery label";
            Control<TextBox>("Department").Text = "Compiled retained recovery department";
            using (File.Open(Path.Combine(path, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Control<Button>("SaveProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => workflow.LocalSave.Phase == LocalSavePhase.Failed &&
                    !Control<TextBox>("Department").IsEnabled);
                Select("avery@example.com", PersonSelection.Mode.Recovery);
                Select("morgan@example.com", PersonSelection.Mode.Recovery);
                if (workflow.LocalSave.Phase != LocalSavePhase.Failed ||
                    workflow.LocalSave.Outcome != LocalSaveOutcome.NotCommitted ||
                    Control<TextBox>("TeamNameEditor").Text != "Compiled retained recovery label" ||
                    Control<TextBox>("Department").Text != "Compiled retained recovery department")
                    throw new InvalidOperationException("Compiled failed browsing lost its exact profile owner/draft or fault.");
            }
            PumpUntil(() => Control<Button>("NoticeRetryButton").IsEnabled);
            Control<Button>("NoticeRetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => workflow.LocalSave.Phase == LocalSavePhase.Recovered &&
                Control<TextBox>("Department").IsEnabled);
            Control<Button>("CancelProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            hold = true;
            Note<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pendingNote = workflow.TeamNotesView.SelectedNote!.Id;
            Note<TextBox>("NoteTitle").Text = "Compiled retained title";
            Note<TextBox>("MarkdownEditor").Text = "Compiled first writing";
            PumpUntil(() => entered.IsSet);
            Note<TextBox>("MarkdownEditor").Text = "Compiled newer exact writing";
            var id = workflow.TeamNotesView.SelectedNote!.Id;
            PumpUntil(() => workflow.LocalSave.Phase == LocalSavePhase.Stalled &&
                Control<FrameworkElement>("OperationalNotice").Visibility == Visibility.Visible &&
                Control<TextBlock>("NoticeSummary").Text.Contains("3 seconds", StringComparison.Ordinal) &&
                !Note<TextBox>("MarkdownEditor").IsEnabled && !Note<Button>("NewButton").IsEnabled &&
                !Control<Button>("EditProfileButton").IsEnabled);
            if (workflow.ReconcileLocalSaves())
                throw new InvalidOperationException("Compiled recovery accepted an unresolved admitted transaction.");
            Select("avery@example.com", PersonSelection.Mode.Recovery);
            Select("morgan@example.com", PersonSelection.Mode.Recovery);
            if (workflow.TeamNotesView.SelectedNote?.Id != id ||
                Note<TextBox>("MarkdownEditor").Text != "Compiled newer exact writing" ||
                Note<TextBox>("MarkdownEditor").IsEnabled)
                throw new InvalidOperationException("Compiled read-only browsing lost the stalled note's owner or exact newer draft.");
            hold = false;
            release.Set();
            PumpUntil(() => workflow.LocalSave.Phase == LocalSavePhase.Recovered &&
                workflow.GetRetainedNoteDraft(NoteScope.Team, id) is null && Note<TextBox>("MarkdownEditor").IsEnabled);
            using var reading = ApplicationWorkflow.CreateFileBacked();
            if (!reading.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                reading.BeginNoteDraft(NoteScope.Team, "morgan@example.com", id)?.Markdown != "Compiled newer exact writing" ||
                window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Compiled late completion lost durable writing or created a native window.");
            Console.WriteLine("PASS: invisible compiled local recovery: failed/stalled non-idle caller selection, retained profile fault/draft, explicit 3-second stall, disabled edits, exact retained note owner/newer writing, and independently reopened late commit. No pixels or native latency claimed.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("COMPILED-LOCAL-RECOVERY-FAILURE " + JsonSerializer.Serialize(new
            {
                phase = workflow.LocalSave.Phase.ToString(), outcome = workflow.LocalSave.Outcome.ToString(),
                saving = pendingNote is { } id && workflow.GetRetainedNoteDraft(NoteScope.Team, id)?.IsSaving == true,
                error = error.ToString(), storageError = workflow.LocalSave.Error
            }));
            throw;
        }
        finally
        {
            hold = false;
            release.Set();
            PumpUntil(() => pendingNote is not { } id || workflow.GetRetainedNoteDraft(NoteScope.Team, id)?.IsSaving != true);
            window.Close();
        }
    }

    private static void SelectRecoveryPerson(AutomationElement window, string email, string heading,
        LocalSavePhase phase, LocalSaveOutcome outcome)
    {
        SelectTreePerson(window, email, PersonSelection.Mode.Recovery);
        Await(() => CurrentPersonEmail(window) == email && !Find(window, "Email").Current.IsOffscreen &&
            Find(window, "ProfileHeading").Current.Name == heading && !Find(window, "ProfileHeading").Current.IsOffscreen &&
            Find(window, "OperationalNotice").Current.ItemStatus == $"Local save: {phase}; outcome: {outcome}" &&
            !Find(window, "OperationalNotice").Current.IsOffscreen &&
            !Find(window, "NewPersonNote").Current.IsEnabled && !Find(window, "NewTeamNote").Current.IsEnabled);
    }

    private static int RunLocalSaveRecoveryFixture(string[] args)
    {
        if (args.Length != 6 || !Path.GetFullPath(args[2]).Contains(
            Path.DirectorySeparatorChar + ".test-workspaces" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The recovery host requires its owned isolated test Workspace.");
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var hold = EventWaitHandle.OpenExisting(args[3]);
        using var entered = EventWaitHandle.OpenExisting(args[4]);
        using var release = EventWaitHandle.OpenExisting(args[5]);
        var assemblyPath = Path.ChangeExtension(Path.GetFullPath(args[1]), ".dll");
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
            resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var observer = new RecoveryCommitGate(() => hold.WaitOne(0), () => entered.Set(),
                () => release.WaitOne(TimeSpan.FromSeconds(30)));
            var window = (Window)Activator.CreateInstance(assembly.GetType("OrgExplorer.Desktop.MainWindow", true)!,
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [ApplicationWorkflow.CreateFileBacked(fileObserver: observer),
                    (Func<Window, string?>)(_ => args[2])], null)!;
            application.Run(window);
            return 0;
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static void RunHostedLocalSaveRecovery(string executable, string root)
    {
        var path = Path.Combine(root, "sqlite-recovery-local");
        SeedRecoveryWorkspace(path);
        var settings = Path.Combine(root, "sqlite-recovery-settings");
        var preferences = new WorkspaceSettings(Path.Combine(settings, "workspace-settings.json"));
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!preferences.OpenWorkspace(seed, path, WorkspaceOpenMode.ExistingOnly) ||
                !seed.ConfigureSynchronizationFolder(Path.Combine(root, "sqlite-recovery-sync")) ||
                !preferences.SetLastView(path, new("morgan@example.com")))
                throw new InvalidOperationException("Could not remember the recovery fixture's actual owner.");
        var token = Guid.NewGuid().ToString("N");
        var names = new[] { @"Local\OrgExplorer-recovery-hold-" + token,
            @"Local\OrgExplorer-recovery-entered-" + token, @"Local\OrgExplorer-recovery-release-" + token };
        using var hold = new EventWaitHandle(false, EventResetMode.ManualReset, names[0]);
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, names[1]);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, names[2]);
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.Environment["ORG_EXPLORER_SETTINGS_DIRECTORY"] = settings;
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--local-save-recovery-fixture",
            Path.GetFullPath(executable), path }.Concat(names)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Applications.Add(process);
        try
        {
            var window = Window(process);
            AwaitRestoredWorkspace(window, path, "morgan@example.com");
            EnsurePeopleBrowsing(window);
            var manager = Find(window, "People").FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "morgan@example.com"));
            if (manager is null) throw new InvalidOperationException("The recovery fixture has no visible Manager.");
            ((ExpandCollapsePattern)manager.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            Click(window, "EditProfile");
            Set(window, "TeamNameEditor", "Retained recovery label");
            Set(window, "Department", "Retained recovery department");
            ((SelectionItemPattern)Find(window, "PersonNoteEditTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var independentNoteId = Find(window, "PersonNoteHeading").Current.HelpText;
            string failed;
            string failedNotice;
            using (File.Open(Path.Combine(path, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click(window, "SaveProfile");
                Await(() => Find(window, "OperationalNotice").Current.ItemStatus.Contains("Failed", StringComparison.Ordinal));
                failed = Find(window, "OperationalNotice").Current.ItemStatus;
                if (!failed.Contains("NotCommitted", StringComparison.Ordinal))
                    throw new InvalidOperationException("The locked local database did not report confirmed noncommit.");
                Await(() => !Find(window, "Department").Current.IsEnabled && !Find(window, "NewTeamNote").Current.IsEnabled);
                SelectRecoveryPerson(window, "avery@example.com", "Avery Example", LocalSavePhase.Failed, LocalSaveOutcome.NotCommitted);
                SelectRecoveryPerson(window, "morgan@example.com", "Morgan Example", LocalSavePhase.Failed, LocalSaveOutcome.NotCommitted);
                AssertValue(window, "TeamNameEditor", "Retained recovery label");
                AssertValue(window, "Department", "Retained recovery department");
                SelectTab(window, "PersonNoteEditTab");
                AssertNoteView(window, "Person", editing: true, writable: false);
                if (Find(window, "PersonNoteHeading").Current.HelpText != independentNoteId ||
                    !Find(window, "PersonNoteOwnership").Current.Name.Contains("morgan@example.com"))
                    throw new InvalidOperationException("Explicit Edit after failed-profile browsing lost the exact clean note owner/id.");
                AssertValue(window, "PersonNoteMarkdown", "Independent exact recovery writing");
                if (!Find(window, "NoticeSummary").Current.Name.Contains("NOT saved", StringComparison.Ordinal))
                    throw new InvalidOperationException("The native failure notice did not explain unsaved local writing.");
                failedNotice = Find(window, "NoticeSummary").Current.Name;
                CaptureVisualEvidence(window, "sqlite-recovery-failed-browsing");
            }
            Await(() => Find(window, "NoticeRetry").Current.IsEnabled);
            Click(window, "NoticeRetry");
            Await(() => Find(window, "Department").Current.IsEnabled);
            Click(window, "SaveProfile");
            Await(() => Find(window, "EditProfile").Current.IsEnabled);
            hold.Set();
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Retained stalled Team title");
            Set(window, "TeamNoteMarkdown", "First stalled Team writing");
            if (!entered.WaitOne(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("The hosted public-storage seam did not enter its actual SQLite commit.");
            Set(window, "TeamNoteMarkdown", "Newer exact stalled Team writing");
            var id = Guid.Parse(Find(window, "TeamNoteHeading").Current.HelpText);
            Await(() => Find(window, "OperationalNotice").Current.ItemStatus.Contains("Stalled", StringComparison.Ordinal));
            if (Find(window, "TeamNoteMarkdown").Current.IsEnabled || Find(window, "NewPersonNote").Current.IsEnabled ||
                Find(window, "EditProfile").Current.IsEnabled ||
                !Find(window, "NoticeSummary").Current.Name.Contains("3 seconds", StringComparison.Ordinal))
                throw new InvalidOperationException("The native stalled notice did not pause editing while retaining browsing.");
            var stalled = Find(window, "OperationalNotice").Current.ItemStatus;
            var stalledNotice = Find(window, "NoticeSummary").Current.Name;
            SelectRecoveryPerson(window, "avery@example.com", "Avery Example", LocalSavePhase.Stalled, LocalSaveOutcome.Unknown);
            SelectRecoveryPerson(window, "morgan@example.com", "Morgan Example", LocalSavePhase.Stalled, LocalSaveOutcome.Unknown);
            AssertValue(window, "TeamNoteTitle", "Retained stalled Team title");
            AssertValue(window, "TeamNoteMarkdown", "Newer exact stalled Team writing");
            if (Find(window, "TeamNoteHeading").Current.HelpText != id.ToString() ||
                Find(window, "TeamNoteMarkdown").Current.IsEnabled)
                throw new InvalidOperationException("Native stalled browsing lost the exact retained Team owner/draft.");
            CaptureVisualEvidence(window, "sqlite-recovery-stalled-retained-owner");
            hold.Reset();
            release.Set();
            Await(() =>
            {
                using var reading = ApplicationWorkflow.CreateFileBacked();
                return reading.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) &&
                    reading.BeginNoteDraft(NoteScope.Team, "morgan@example.com", id)?.Markdown == "Newer exact stalled Team writing" &&
                    Find(window, "TeamNoteMarkdown").Current.IsEnabled &&
                    Find(window, "OperationalNotice").Current.ItemStatus.Contains("Recovered", StringComparison.Ordinal);
            });
            using (var reading = ApplicationWorkflow.CreateFileBacked())
                if (!reading.OpenWorkspace(path, WorkspaceOpenMode.ExistingOnly) ||
                    reading.GetRelationships("morgan@example.com")!.Person.TeamName != "Retained recovery label" ||
                    reading.GetRelationships("morgan@example.com")!.Person.Department != "Retained recovery department" ||
                    reading.GetPersonNotes("morgan@example.com").Single().Markdown != "Independent exact recovery writing" ||
                    reading.BeginNoteDraft(NoteScope.Team, "avery@example.com", id) is not null)
                    throw new InvalidOperationException("Recovery did not preserve the independent complete profile and exact owned notes.");
            CaptureVisualEvidence(window, "sqlite-recovery-late-commit");
            var hostExecutable = process.MainModule?.FileName
                ?? throw new InvalidOperationException("Cannot identify the owned recovery host executable.");
            using var host = File.OpenRead(hostExecutable);
            using var product = File.OpenRead(executable);
            using var assembly = File.OpenRead(Path.ChangeExtension(executable, ".dll"));
            var evidence = Path.Combine(Path.GetDirectoryName(diagnostics)!,
                Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-local-recovery.json");
            File.WriteAllText(evidence, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), processId = process.Id,
                hostExecutable, hostExecutableSha256 = Convert.ToHexString(SHA256.HashData(host)),
                productExecutable = Path.GetFullPath(executable),
                productAssembly = Path.GetFullPath(Path.ChangeExtension(executable, ".dll")),
                productExeSha256 = Convert.ToHexString(SHA256.HashData(product)),
                productAssemblySha256 = Convert.ToHexString(SHA256.HashData(assembly)),
                host = "Actual compiled product MainWindow in the owned smoke host; real SQLite, with only the public storage-observation commit boundary held. Not the ordinary executable startup/latency journey.",
                failed, failedNotice, stalled, stalledNotice,
                recovered = Find(window, "OperationalNotice").Current.ItemStatus,
                recoveredNotice = Find(window, "NoticeSummary").Current.Name,
                owner = "morgan@example.com", noteId = id,
                retainedTitle = "Retained stalled Team title", retainedMarkdown = "Newer exact stalled Team writing",
                captures = new[] { "sqlite-recovery-failed-browsing", "sqlite-recovery-stalled-retained-owner", "sqlite-recovery-late-commit" },
                completion = "Pre-close observations only. Require the terminal hosted PASS after actual idle, normal close and process exit."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close(process);
            Console.WriteLine("PASS: hosted local-save recovery: visible failure/stall, paused edits, actual Person browsing, exact independent drafts/owner and late durable commit; " + evidence);
        }
        finally
        {
            hold.Reset();
            release.Set();
            StopApplication(process);
        }
    }
}
