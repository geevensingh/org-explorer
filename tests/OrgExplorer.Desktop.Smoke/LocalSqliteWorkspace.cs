using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Microsoft.Data.Sqlite;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifySqliteCreationNavigation(Type windowType, string settings,
        Func<Window, string?> chooseFolder)
    {
        var previousOptIn = Environment.GetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES");
        Window? window = null;
        try
        {
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", "1");
            window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [ApplicationWorkflow.CreateFileBacked(), chooseFolder], null)!;
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var legacy = Path.Combine(settings, "sqlite-navigation-legacy");
            WorkspaceFixtures.CopyCurrent(legacy);
            Invoke("OpenSettingsButton");
            ChooseCompiledWorkspace(window, legacy);
            PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
            if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Ordinary Workspace opening must return to browsing.");

            Invoke("OpenSettingsButton");
            if (Control<Button>("CreateLocalSqliteWorkspaceButton").Visibility != Visibility.Visible ||
                !Control<Button>("CreateLocalSqliteWorkspaceButton").IsEnabled)
                throw new InvalidOperationException("The compiled SQLite opt-in did not expose its Create action.");
            Invoke("CreateLocalSqliteWorkspaceButton");
            PumpUntil(() => Control<TextBlock>("StatusText").Text == "SQLite Workspace created and saved locally." &&
                Control<TextBlock>("WorkflowActivity").Text == "Ready");
            if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Collapsed ||
                Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Visible ||
                Control<FrameworkElement>("WorkspaceStartPanel").Visibility != Visibility.Visible)
                throw new InvalidOperationException(
                    "SQLite creation left Settings covering the browsing/start view required before Import navigation.");

            var workflow = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            if (!workflow.IsWorkspaceCurrent || workflow.People.Count != 0)
                throw new InvalidOperationException("SQLite creation did not expose its committed empty Workspace.");
            Invoke("OpenSettingsButton");
            if (!Control<Button>("OpenImportButton").IsEnabled)
                throw new InvalidOperationException("SQLite creation did not leave the normal Import action enabled.");
            Invoke("OpenImportButton");
            if (Control<FrameworkElement>("ImportPanel").Visibility != Visibility.Visible ||
                Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Collapsed ||
                !Control<TextBox>("ImportPath").IsEnabled)
                throw new InvalidOperationException("The normal Settings Import action did not reach a usable Import view after SQLite creation.");
            var import = Path.Combine(settings, "sqlite-navigation-import.json");
            File.WriteAllText(import, """
                {"schemaVersion":1,"batchId":"91000000-0000-4000-8000-000000000002",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"morgan@example.com"},
                 {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null}]}
                """);
            Control<TextBox>("ImportPath").Text = import;
            Invoke("PreviewButton");
            PumpUntil(() => Control<Button>("ApplyButton").IsEnabled);
            if (workflow.People.Count != 0 ||
                !Control<ListBox>("PreviewText").Items.Cast<string>().Any(text => text.Contains("avery@example.com")))
                throw new InvalidOperationException("The compiled SQLite Import preview lost its reviewed Person or accepted data before Apply.");
            Invoke("ApplyButton");
            PumpUntil(() => workflow.People.Count == 2 && Control<TextBlock>("WorkflowActivity").Text == "Ready");
            LogicalDescendants(Control<FrameworkElement>("ImportPanel")).OfType<Button>().Single(button =>
                AutomationProperties.GetAutomationId(button) == "BackToPeople").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var tree = Control<TreeView>("PeopleList");
            void Select(string email)
            {
                var node = workflow.ReportingRoots.SelectMany(root => new[] { root }.Concat(root.Reports))
                    .Single(item => item.Person.Email == email);
                tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
                if (Control<TextBlock>("Email").Text != email)
                    throw new InvalidOperationException("Compiled note navigation did not immediately display its actual Person.");
            }
            Select("morgan@example.com");
            void ArrangeContent()
            {
                using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.CompiledLayout);
                var root = (FrameworkElement)window.Content;
                root.InvalidateMeasure();
                root.Measure(new Size(1024, 768));
                root.Arrange(new Rect(0, 0, 1024, 768));
                root.UpdateLayout();
            }
            ArrangeContent();
            HarnessRuntime.Checkpoint("compiled-sqlite-held-note-navigation");
            foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
            {
                var surface = Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes");
                T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
                using var connection = new SqliteConnection(
                    $"Data Source={Path.Combine(workflow.Compatibility.WorkspacePath!, "workspace.sqlite")};Pooling=False");
                connection.Open();
                using var held = connection.BeginTransaction();
                if (!Note<Button>("NewButton").IsEnabled)
                    throw new InvalidOperationException($"SQLite {scope} note creation remains disabled.");
                Note<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var id = workflow.GetOwnedNotesView(scope).SelectedNote?.Id
                    ?? throw new InvalidOperationException("Pending note creation has no stable selected identity.");
                if (!Note<TextBox>("NoteTitle").IsEnabled || !Note<TextBox>("MarkdownEditor").IsEnabled ||
                    Note<TabControl>("Views").SelectedItem != Note<TabItem>("EditTab"))
                    throw new InvalidOperationException("Add note did not synchronously open a usable compiled editor before persistence.");
                ArrangeContent();
                VerifyCompiledNoteObservation(surface, scope, workflow.GetOwnedNotesView(scope).SelectedNote!);
                Note<TextBox>("NoteTitle").Text = $"Retained {scope}";
                Note<TextBox>("MarkdownEditor").Text = $"Original {scope} writing";
                PumpUntil(() => workflow.GetRetainedNoteDraft(scope, id)?.IsSaving == true);
                Note<TextBox>("MarkdownEditor").Text = $"Newer {scope} writing during the admitted save";
                RetainedNoteDraft RequirePendingWrite(string phase)
                {
                    var retained = workflow.GetRetainedNoteDraft(scope, id);
                    if (held.Connection != connection || retained is not { IsSaving: true, Error: null } ||
                        workflow.LocalSaveError is not null || workflow.CanSafelyClose ||
                        retained.Basis.OwnerEmail != "morgan@example.com" || retained.Basis.Id != id ||
                        retained.Markdown != $"Newer {scope} writing during the admitted save")
                        throw new InvalidOperationException($"Compiled held-note navigation lost its admitted pending write {phase}: " +
                            $"scope={scope}; writerHeld={held.Connection == connection}; " +
                            $"saving={retained?.IsSaving}; draftError={retained?.Error is not null}; " +
                            $"saveError={workflow.LocalSaveError is not null}; safelyClose={workflow.CanSafelyClose}. " +
                            "A completed or failed write cannot prove navigation independence.");
                    return retained;
                }
                void NavigateWhileWriteRemainsHeld(string destination, int injectedDispatchDelayMs)
                {
                    var before = RequirePendingWrite("before selection");
                    var switching = Stopwatch.StartNew();
                    Select(destination);
                    var dispatchMs = switching.Elapsed.TotalMilliseconds;
                    ArrangeContent();
                    var throughLayoutMs = switching.Elapsed.TotalMilliseconds;
                    var after = RequirePendingWrite("after selection and layout");
                    if (!ReferenceEquals(before.Basis, after.Basis) ||
                        workflow.PeopleNavigation.SelectedEmail != destination)
                        throw new InvalidOperationException("Compiled selection changed the admitted save base or navigated owner.");
                    Console.WriteLine("DIAGNOSTIC: compiled held-note navigation " + JsonSerializer.Serialize(new
                    {
                        scope = scope.ToString(), destination, injectedDispatchDelayMs, dispatchMs, throughLayoutMs,
                        writerHeld = held.Connection == connection, savingBefore = before.IsSaving, savingAfter = after.IsSaving,
                        sameSaveBase = ReferenceEquals(before.Basis, after.Basis), safelyClose = workflow.CanSafelyClose,
                        hasSaveError = workflow.LocalSaveError is not null,
                        boundary = "Actual invisible compiled selection and layout returned while the same admitted note write and real SQLite transaction remain held; not a native latency sample."
                    }));
                }
                NavigateWhileWriteRemainsHeld("avery@example.com", 0);
                var dispatchDelayed = false;
                RoutedPropertyChangedEventHandler<object> slowSelection = (_, e) =>
                {
                    if (dispatchDelayed || e.NewValue is not ReportingNode { Person.Email: "morgan@example.com" }) return;
                    dispatchDelayed = true;
                    Thread.Sleep(150);
                };
                tree.SelectedItemChanged += slowSelection;
                try { NavigateWhileWriteRemainsHeld("morgan@example.com", 150); }
                finally { tree.SelectedItemChanged -= slowSelection; }
                if (!dispatchDelayed)
                    throw new InvalidOperationException("The compiled no-write-wait regression did not exercise its controlled selection dispatch delay.");
                if (Note<TextBox>("MarkdownEditor").Text != $"Newer {scope} writing during the admitted save" ||
                    !Note<TextBox>("MarkdownEditor").IsEnabled || workflow.CanSafelyClose ||
                    Note<TabControl>("Views").SelectedItem != Note<TabItem>("EditTab") ||
                    workflow.GetOwnedNotesView(scope).SelectedNote?.Id != id)
                    throw new InvalidOperationException("Compiled Person switching lost, disabled, redirected or falsely saved its retained note.");
                using (var uncommitted = ApplicationWorkflow.CreateFileBacked())
                    if (!uncommitted.OpenWorkspace(workflow.Compatibility.WorkspacePath!, WorkspaceOpenMode.ExistingOnly) ||
                        uncommitted.BeginNoteDraft(scope, "morgan@example.com", id) is not null)
                        throw new InvalidOperationException("Compiled navigation proof did not retain the actual uncommitted note boundary.");
                RequirePendingWrite("before deliberate writer release");
                held.Commit();
                PumpUntil(() => workflow.CanSafelyClose);
                if (Note<TextBox>("MarkdownEditor").Text != $"Newer {scope} writing during the admitted save")
                    throw new InvalidOperationException("A late compiled save completion replaced newer typing.");
                using var reopened = ApplicationWorkflow.CreateFileBacked();
                if (!reopened.OpenWorkspace(workflow.Compatibility.WorkspacePath!, WorkspaceOpenMode.ExistingOnly) ||
                    reopened.BeginNoteDraft(scope, "morgan@example.com", id)?.Markdown != $"Newer {scope} writing during the admitted save" ||
                    reopened.BeginNoteDraft(scope, "avery@example.com", id) is not null)
                    throw new InvalidOperationException("Compiled autosave did not durably preserve the exact note and actual owner.");
            }
            var dispatchMs = new List<double>();
            var layoutMs = new List<double>();
            var selectionMs = new List<double>();
            var selectionDispatchMs = new List<double>();
            var commitMs = new List<double>();
            var reopenMs = new List<double>();
            var teams = Control<UserControl>("TeamNotes");
            T Team<T>(string name) where T : FrameworkElement => (T)teams.FindName(name);
            window.Width = 1024;
            window.Height = 768;
            var content = (FrameworkElement)window.Content;
            void Layout()
            {
                using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.CompiledLayout);
                content.InvalidateMeasure();
                content.InvalidateArrange();
                content.Measure(new Size(1024, 768));
                content.Arrange(new Rect(0, 0, 1024, 768));
                content.UpdateLayout();
            }
            HarnessRuntime.Checkpoint("compiled-sqlite-repeated-note-dispatch");
            for (var sample = 0; sample < 20; sample++)
            {
                var elapsed = Stopwatch.StartNew();
                Team<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dispatchMs.Add(elapsed.Elapsed.TotalMilliseconds);
                Layout();
                layoutMs.Add(elapsed.Elapsed.TotalMilliseconds);
                VerifyCompiledNoteObservation(teams, NoteScope.Team, workflow.TeamNotesView.SelectedNote!);
                var id = workflow.TeamNotesView.SelectedNote!.Id;
                if (!Team<TextBox>("NoteTitle").IsEnabled || !Team<TextBox>("MarkdownEditor").IsEnabled ||
                    Team<TabControl>("Views").SelectedItem != Team<TabItem>("EditTab") ||
                    Team<TextBox>("NoteTitle").ActualWidth <= 0 || Team<TextBox>("MarkdownEditor").ActualHeight <= 0)
                    throw new InvalidOperationException("The real compiled Add handler/layout did not expose its writable editor.");
                var prefix = $"# Compiled independently observed note {sample}";
                using var observation = NoteCommitObservation.Begin(
                    () => Team<TextBox>("MarkdownEditor").Text = prefix,
                    () =>
                    {
                        using var reading = ApplicationWorkflow.CreateFileBacked();
                        if (!reading.OpenWorkspace(workflow.Compatibility.WorkspacePath!, WorkspaceOpenMode.ExistingOnly))
                            throw new InvalidOperationException("Compiled commit observation could not reopen the real Workspace: " + reading.Status);
                        return reading.BeginNoteDraft(NoteScope.Team, "morgan@example.com", id)?.Markdown == prefix;
                    });
                elapsed.Restart();
                Select("avery@example.com");
                selectionDispatchMs.Add(elapsed.Elapsed.TotalMilliseconds);
                Layout();
                selectionMs.Add(elapsed.Elapsed.TotalMilliseconds);
                elapsed.Restart();
                Select("morgan@example.com");
                selectionDispatchMs.Add(elapsed.Elapsed.TotalMilliseconds);
                Layout();
                selectionMs.Add(elapsed.Elapsed.TotalMilliseconds);
                VerifyCompiledNoteObservation(teams, NoteScope.Team, workflow.TeamNotesView.SelectedNote!);
                PumpUntil(() => workflow.CanSafelyClose);
                commitMs.Add(observation.Complete());
                reopenMs.Add(observation.ReadMs);
            }
            double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
            Console.WriteLine("DIAGNOSTIC: compiled SQLite note dispatch/layout/selection and independently reopened commits " +
                JsonSerializer.Serialize(new { dispatchMs, addThroughLayoutMs = layoutMs, selectionDispatchMs, selectionThroughLayoutMs = selectionMs,
                    commitMs, reopenReadMs = reopenMs, p95DispatchMs = P95(dispatchMs), p95AddThroughLayoutMs = P95(layoutMs),
                    p95SelectionThroughLayoutMs = P95(selectionMs), p95CommitMs = P95(commitMs),
                    boundary = "Actual invisible compiled event handlers/layout and first-request-to-real-workflow reopen; not native focus, rendered pixels or hosted latency acceptance." }));
            HarnessRuntime.Run("compiled-sqlite-profile",
                () => VerifyLocalProfileControls(window, workflow, Select, ArrangeContent, settings));
            HarnessRuntime.Run("compiled-sqlite-reviewed-import",
                () => VerifyLocalReviewedImportControls(window, workflow, Select, ArrangeContent, settings));
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("SQLite navigation verification must not show a window or create its native handle.");
            Console.WriteLine("PASS: compiled SQLite Create returns to the ordinary start view and normal Settings Import reaches a real reviewed preview without a visible window/native handle.");
            Console.WriteLine("PASS: compiled SQLite Person/Team editors open before commit, retain newer writing across actual selection events and durably save the original owner without a visible window/native handle.");
            Console.WriteLine("PASS: compiled SQLite Person/Team selection and layout return before the same held note write completes; slow dispatch is diagnostic, not native latency acceptance.");
            Console.WriteLine("PASS: compiled SQLite note observation selects only live current-editor controls and preserves exact identity, writable text and Edit mode across Add and Person switches; native visibility/focus remains hosted evidence.");
        }
        finally
        {
            if (window?.FindName("PeopleList") is TreeView { DataContext: WorkspaceViewModel workflow })
                workflow.Dispose();
            window?.Close();
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", previousOptIn);
        }
    }

    private static void RunLocalSqliteWorkspaceFlow(string executable, string root)
    {
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        var previousOptIn = Environment.GetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES");
        var settingsDirectory = Path.Combine(root, "sqlite-app-settings");
        var oldWorkspace = Path.Combine(root, "sqlite-preserved-old");
        WorkspaceFixtures.CopyCurrent(oldWorkspace);
        var originals = WorkspaceFixtures.Bytes(oldWorkspace);
        var settings = new WorkspaceSettings(Path.Combine(settingsDirectory, "workspace-settings.json"));
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!settings.OpenWorkspace(seed, oldWorkspace))
                throw new InvalidOperationException("Could not prepare the existing startup Workspace: " + seed.Status);
        var import = Path.Combine(root, "sqlite-initial.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"91000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","producer":"Hosted SQLite journey","people":[
             {"email":"avery@example.com","displayName":"Avery Example","alias":"avery","officeNumber":"007B",
              "department":"Research","jobTitle":"Research engineer","officeCountry":"Canada",
              "managerEmail":"morgan@example.com"},
             {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null}]}
            """);
        try
        {
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settingsDirectory);
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", "1");
            string local;
            IReadOnlyDictionary<NoteScope, (Guid Id, string Title, string Markdown)> savedNotes;
            LocalPersonJourney personJourney;
            using (var process = Launch(executable))
            {
                try
                {
                    var window = Window(process);
                    AwaitRestoredWorkspace(window, oldWorkspace);
                    SetSettingsVisible(window, true);
                    Click(window, "CreateLocalSqliteWorkspace");
                    Await(() => Status(window) == "SQLite Workspace created and saved locally.");
                    local = new WorkspaceSettings(Path.Combine(settingsDirectory, "workspace-settings.json")).LastWorkspacePath
                        ?? throw new InvalidOperationException("SQLite creation did not remember its local Workspace.");
                    if (!local.StartsWith(Path.Combine(settingsDirectory, "LocalWorkspaces") + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("SQLite creation did not use app-managed local storage.");
                    OpenImportView(window);
                    Set(window, "ImportPath", import);
                    Click(window, "PreviewImport");
                    Await(() => Find(window, "ApplyImport").Current.IsEnabled);
                    using (var empty = ApplicationWorkflow.CreateFileBacked())
                        if (!empty.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) || empty.People.Count != 0)
                            throw new InvalidOperationException("Preview accepted the SQLite population before Apply.");
                    Click(window, "ApplyImport");
                    Await(() => Status(window) == "Import applied and saved locally.");
                    ReturnToPeople(window);
                    EnsurePeopleBrowsing(window);
                    var manager = Find(window, "People").FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "morgan@example.com"))
                        ?? throw new InvalidOperationException("SQLite Import did not expose the Manager in People.");
                    ((ExpandCollapsePattern)manager.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                    SelectTreePerson(window, "avery@example.com");
                    Await(() => CurrentPersonEmail(window) == "avery@example.com");
                    AssertProfileValue(window, "OfficeNumber", "007B");
                    AssertProfileValue(window, "Department", "Research");
                    Await(() => Find(window, "ProfileHeading").Current.Name == "Avery Example");
                    if (!Find(window, "EditProfile").Current.IsEnabled ||
                        !Find(window, "ToggleFavorite").Current.IsEnabled)
                        throw new InvalidOperationException("The SQLite path did not enable its implemented ordinary Person edits.");
                    CaptureVisualEvidence(window, "sqlite-initial-import-browsing");
                    savedNotes = HarnessRuntime.Run("native-sqlite-notes", () => RunLocalNoteAutosaveJourney(window, local));
                    personJourney = HarnessRuntime.Run("native-sqlite-profile",
                        () => RunLocalPersonSaveJourney(window, local, root, savedNotes[NoteScope.Team]));
                    savedNotes = savedNotes.ToDictionary(pair => pair.Key, pair => pair.Key == NoteScope.Team
                        ? (pair.Value.Id, pair.Value.Title, pair.Value.Markdown + "\nLater current-only writing") : pair.Value);
                    SelectTreePerson(window, "avery@example.com");
                    Await(() => CurrentPersonEmail(window) == "avery@example.com");
                    AssertWorkspaceBytes(oldWorkspace, originals);
                    CloseUnpublishedWorkspace(process);
                }
                finally { StopApplication(process); }
            }
            using var reopened = Launch(executable);
            try
            {
                var window = Window(reopened);
                AwaitRestoredWorkspace(window, local, "avery@example.com");
                AssertProfileValue(window, "OfficeNumber", "008C");
                AssertProfileValue(window, "OfficeCountry", "Canada");
                AssertPhotoVisible(window);
                AssertProfileValue(window, "JobTitle", "Independent researcher");
                AssertTeamNameReading(window, "Platform");
                using (var reading = ApplicationWorkflow.CreateFileBacked())
                {
                    if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) ||
                        reading.GetRelationships("avery@example.com")!.Person.PreferredName != "Avery reference" ||
                        !reading.GetRelationships("avery@example.com")!.Person.IsFavorite ||
                        reading.GetRelationships("avery@example.com")!.Person.Archived ||
                        reading.GetPhoto("avery@example.com") is not { } photo ||
                        Convert.ToHexString(SHA256.HashData(photo.Bytes.Span)) != personJourney.PhotoSha256 ||
                        reading.GetHistoricalTeamContexts("avery@example.com").Single(c => c.Id == personJourney.CaptureId)
                            .TeamNotes.Single(note => note.Id == personJourney.NoteId).Version != personJourney.FrozenVersion)
                        throw new InvalidOperationException("Hosted SQLite profile reopening lost committed personal context or history.");
                }
                CaptureVisualEvidence(window, "sqlite-person-saves-reopened");
                WriteLocalPersonJourneyEvidence(window, personJourney);
                Click(window, "NavigateManager");
                Await(() => CurrentPersonEmail(window) == "morgan@example.com");
                var savedTeam = savedNotes[NoteScope.Team];
                Await(() => Find(window, "TeamNoteHeading").Current.HelpText == savedTeam.Id.ToString() &&
                    Find(window, "TeamNoteHeading").Current.Name == savedTeam.Title &&
                    Find(window, "TeamNoteKind").Current.Name == "Ongoing Team note");
                AssertNoteView(window, "Team", editing: false);
                NavigateMainReadingAndCapture(window, "TeamNoteReading", "Continuing entry 5", "sqlite-nonblocking-notes-reopened");
                using (var reading = ApplicationWorkflow.CreateFileBacked())
                {
                    if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly))
                        throw new InvalidOperationException("Could not reopen hosted note writing: " + reading.Status);
                    foreach (var (scope, saved) in savedNotes)
                        if (reading.BeginNoteDraft(scope, "morgan@example.com", saved.Id)?.Markdown != saved.Markdown ||
                            reading.BeginNoteDraft(scope, "avery@example.com", saved.Id) is not null)
                            throw new InvalidOperationException("Hosted note reopening lost exact writing or its actual owner.");
                }
                Console.WriteLine("PASS: hosted SQLite Person/Team notes survive process close/reopen with exact identities, writing and original ownership; legacy Workspace bytes remain unchanged.");
                HarnessRuntime.Run("native-sqlite-snapshots", () => RunSnapshotSynchronizationJourney(window, local, root, personJourney));
                AssertWorkspaceBytes(oldWorkspace, originals);
                Close(reopened);
            }
            finally { StopApplication(reopened); }
            HarnessRuntime.Run("native-sqlite-reviewed-import",
                () => RunLocalReviewedImportFlow(executable, root, local));
            HarnessRuntime.Run("native-sqlite-local-recovery",
                () => RunHostedLocalSaveRecovery(executable, root));
            HarnessRuntime.Run("native-sqlite-automatic-snapshots",
                () => RunHostedAutomaticSnapshots(executable, root));
            HarnessRuntime.Run("native-sqlite-exit",
                () => RunHostedWorkspaceExit(executable, root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", previousOptIn);
        }
    }

    private static void VerifyLocalProfileControls(Window window, WorkspaceViewModel workflow,
        Action<string> select, Action arrange, string root)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        select("morgan@example.com");
        Invoke("EditProfileButton");
        Control<TextBox>("TeamNameEditor").Text = "Platform";
        Invoke("SaveProfileButton");
        PumpUntil(() => workflow.ProfileEdit is null && Control<TextBlock>("WorkflowActivity").Text == "Ready");
        select("avery@example.com");
        Invoke("EditProfileButton");
        Control<TextBox>("OfficeNumber").Text = "Retained SQLite room";
        select("morgan@example.com");
        if (workflow.ProfileEdit is not null || Control<FrameworkElement>("ProfileForm").Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Compiled browsing exposed another owner's retained profile fields.");
        select("avery@example.com");
        if (Control<TextBox>("OfficeNumber").Text != "Retained SQLite room" ||
            Control<FrameworkElement>("ProfileForm").Visibility != Visibility.Visible || workflow.CanSafelyClose)
            throw new InvalidOperationException("Compiled Person navigation discarded or implicitly saved its retained profile draft.");
        var photo = Path.Combine(root, "sqlite-profile-photo.png");
        CreatePhotoFixture(photo);
        workflow.StageProfilePhoto(photo);
        var submittedProfile = workflow.ProfileEdit!;
        using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(workflow.Compatibility.WorkspacePath!, "workspace.sqlite")};Pooling=False"))
        {
            connection.Open();
            using var held = connection.BeginTransaction();
            Invoke("SaveProfileButton");
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Saving complete profile…");
            var before = new LocalWorkObservation.ProfileSaveHold(submittedProfile.Basis.Email,
                Control<TextBlock>("WorkflowActivity").Text, held.Connection == connection);
            select("morgan@example.com");
            arrange();
            var after = new LocalWorkObservation.ProfileSaveHold(submittedProfile.Basis.Email,
                Control<TextBlock>("WorkflowActivity").Text, held.Connection == connection);
            LocalWorkObservation.RequireSameHeldProfile(before, after, "avery@example.com");
            if (workflow.CanSafelyClose || workflow.LocalSave.Phase != LocalSavePhase.Pending ||
                workflow.LocalSave.OutstandingRequests == 0 || Control<TextBlock>("Email").Text != "morgan@example.com")
                throw new InvalidOperationException("Compiled browsing waited behind or falsely acknowledged an uncommitted profile.");
            using (var unchanged = ApplicationWorkflow.CreateFileBacked())
                if (!unchanged.OpenWorkspace(workflow.Compatibility.WorkspacePath!, WorkspaceOpenMode.ExistingOnly) ||
                    unchanged.GetRelationships(submittedProfile.Basis.Email)!.Person != submittedProfile.Basis)
                    throw new InvalidOperationException("Compiled held profile Save changed its committed owner's data before release.");
            held.Commit();
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready");
        }
        select("avery@example.com");
        using (var reading = ApplicationWorkflow.CreateFileBacked())
            if (!reading.OpenWorkspace(workflow.Compatibility.WorkspacePath!, WorkspaceOpenMode.ExistingOnly) ||
                reading.GetRelationships("avery@example.com")!.Person.OfficeNumber != "Retained SQLite room" ||
                reading.GetPhoto("avery@example.com")?.Width != 2)
                throw new InvalidOperationException("Compiled explicit profile Save did not reopen coherent values and actual photo bytes.");
        Invoke("EditPreferredNameButton");
        Control<TextBox>("PreferredName").Text = "Retained preferred";
        select("morgan@example.com");
        select("avery@example.com");
        if (Control<TextBox>("PreferredName").Text != "Retained preferred" ||
            workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName)?.Value != "Retained preferred")
            throw new InvalidOperationException("Compiled Person switching discarded its unsubmitted Preferred name.");
        Invoke("CancelPreferredNameButton");
        if (!workflow.CanSafelyClose)
            throw new InvalidOperationException("Compiled Preferred-name Cancel did not discard only its retained entry.");
        Invoke("EditPreferredNameButton");
        Control<TextBox>("PreferredName").Text = "Saved preferred";
        select("morgan@example.com");
        select("avery@example.com");
        Invoke("SavePreferredNameButton");
        PumpUntil(() => workflow.GetRelationships("avery@example.com")!.Person.PreferredName == "Saved preferred" &&
            Control<TextBlock>("WorkflowActivity").Text == "Ready");
        Invoke("FavoriteButton");
        PumpUntil(() => workflow.GetRelationships("avery@example.com")!.Person.IsFavorite &&
            Control<TextBlock>("WorkflowActivity").Text == "Ready");
        var personFeedback = AutomationProperties.GetItemStatus(Control<UserControl>("PersonNotes"));
        var teamFeedback = AutomationProperties.GetItemStatus(Control<UserControl>("TeamNotes"));
        if (!workflow.CanSafelyClose || workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not null ||
            personFeedback.StartsWith("Pending", StringComparison.Ordinal) || teamFeedback.StartsWith("Pending", StringComparison.Ordinal))
            throw new InvalidOperationException("Committed SQLite Preferred-name/favorite changes left stale pending note-surface feedback: " +
                $"Person='{personFeedback}'; Team='{teamFeedback}'; safelyClose={workflow.CanSafelyClose}.");
        Invoke("EditPreferredNameButton");
        Control<TextBox>("PreferredName").Text = "Still unsubmitted";
        Invoke("FavoriteButton");
        PumpUntil(() => !workflow.GetRelationships("avery@example.com")!.Person.IsFavorite &&
            Control<Button>("FavoriteButton").IsEnabled);
        if (workflow.CanSafelyClose ||
            workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName)?.Value != "Still unsubmitted" ||
            !AutomationProperties.GetItemStatus(Control<UserControl>("PersonNotes")).StartsWith("Pending", StringComparison.Ordinal) ||
            !AutomationProperties.GetItemStatus(Control<UserControl>("TeamNotes")).StartsWith("Pending", StringComparison.Ordinal))
            throw new InvalidOperationException("Independent favorite success cleared or falsely acknowledged retained Preferred-name writing.");
        Invoke("CancelPreferredNameButton");
        if (!workflow.CanSafelyClose ||
            AutomationProperties.GetItemStatus(Control<UserControl>("PersonNotes")).StartsWith("Pending", StringComparison.Ordinal) ||
            AutomationProperties.GetItemStatus(Control<UserControl>("TeamNotes")).StartsWith("Pending", StringComparison.Ordinal))
            throw new InvalidOperationException("Explicit Preferred-name Cancel left stale pending note feedback.");
        Console.WriteLine("PASS: compiled SQLite Preferred-name/favorite saves refresh truthful note-surface feedback without clearing retained writing; real idle metadata agrees with safe close.");
        using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(workflow.Compatibility.WorkspacePath!, "workspace.sqlite")};Pooling=False"))
        {
            connection.Open();
            using var held = connection.BeginTransaction();
            Invoke("EditPreferredNameButton");
            Control<TextBox>("PreferredName").Text = "Cancelled failing request";
            Invoke("SavePreferredNameButton");
            Invoke("CancelPreferredNameButton");
            if (Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Compiled SQLite Cancel waited for the admitted Preferred-name request.");
            PumpUntil(() => workflow.LocalSave.Phase == LocalSavePhase.Failed &&
                Control<TextBlock>("WorkflowActivity").Text.Contains("NOT saved", StringComparison.Ordinal));
            if (workflow.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName) is not null ||
                workflow.CanSafelyClose || workflow.CanEditLocalWorkspace ||
                Control<Button>("EditProfileButton").IsEnabled)
                throw new InvalidOperationException("Cancelled SQLite Preferred-name failure lost its explicit read-only recovery state.");
            held.Rollback();
            Invoke("NoticeRetryButton");
            PumpUntil(() => workflow.LocalSave.Phase == LocalSavePhase.Recovered && workflow.CanSafelyClose &&
                Control<Button>("EditProfileButton").IsEnabled);
        }
        Console.WriteLine("PASS: compiled SQLite profiles and Preferred names retain owner drafts across actual selection events; explicit held Save permits browsing and coherently reopens the photo without a visible window/native handle.");
    }

    private sealed record LocalPersonJourney(int EditingProcessId, Guid CaptureId, Guid NoteId, string FrozenVersion,
        string PhotoSha256, IReadOnlyList<double> ProfileSaveMs, object HeldNavigation);

    private static LocalPersonJourney RunLocalPersonSaveJourney(AutomationElement window, string local, string root,
        (Guid Id, string Title, string Markdown) team)
    {
        void Browse(string owner)
        {
            SelectTreePerson(window, owner, expectNavigation: false);
            Await(() => CurrentPersonEmail(window) == owner && !Find(window, "Email").Current.IsOffscreen);
        }
        Browse("morgan@example.com");
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "Platform");
        SaveProfileEditing(window);
        AssertTeamNameReading(window, "Platform");
        AssertTeamNameOwnerAttribution(window, "Morgan Example", "morgan@example.com");
        Browse("avery@example.com");
        BeginProfileEditing(window);
        Set(window, "OfficeNumber", "Unsubmitted room");
        Browse("morgan@example.com");
        AssertTeamNameReading(window, "Platform");
        Browse("avery@example.com");
        AssertValue(window, "OfficeNumber", "Unsubmitted room");
        using (var reading = ApplicationWorkflow.CreateFileBacked())
            if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) ||
                reading.GetRelationships("avery@example.com")!.Person.OfficeNumber != "007B")
                throw new InvalidOperationException("Hosted navigation implicitly committed the retained profile.");
        CaptureVisualEvidence(window, "sqlite-profile-retained-after-browse");
        CancelProfileEditing(window);
        AssertProfileValue(window, "OfficeNumber", "007B");

        Click(window, "EditPreferredName");
        Set(window, "PreferredName", "Avery reference");
        Browse("morgan@example.com");
        Browse("avery@example.com");
        AssertValue(window, "PreferredName", "Avery reference");
        Click(window, "SavePreferredName");
        Await(() => Find(window, "ProfileHeading").Current.Name == "Avery reference");
        Click(window, "ToggleFavorite");
        try { AwaitLocalWorkIdle(window); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("SQLite Person favorite did not reach the real idle state: " +
                $"owner='{CurrentPersonEmail(window)}'; activity='{Activity(window)}'; status='{Status(window)}'; " +
                $"Person-note feedback='{NoteFeedback(window, "Person")}'; Team-note feedback='{NoteFeedback(window, "Team")}'.", error);
        }
        Console.WriteLine("PASS: hosted SQLite Preferred-name/favorite commits reach truthful idle feedback after retained-owner browsing.");
        var photo = Path.Combine(root, "sqlite-profile-portrait.png");
        CreatePhotoFixture(photo);
        BeginProfileEditing(window);
        Set(window, "OfficeNumber", "008C");
        Set(window, "JobTitle", "Independent researcher");
        ChooseProfilePhoto(window, photo);
        SelectManagerChoice(window, "(explicit no-Manager — clear relationship)");
        var managerSelection = (SelectionItemPattern)Find(window, "People").FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "morgan@example.com"))!
            .GetCurrentPattern(SelectionItemPattern.Pattern);
        var email = Find(window, "Email");
        var heading = Find(window, "ProfileHeading");
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        bool PersonReady(string expectedEmail, string expectedHeading) =>
            email.GetUpdatedCache(cache).Cached is { Name: var actual, IsOffscreen: false } && actual == expectedEmail &&
            heading.GetUpdatedCache(cache).Cached.Name == expectedHeading;
        var navigation = new List<double>();
        object? heldNavigation = null;
        var submittedOwner = CurrentPersonEmail(window);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(local, "workspace.sqlite")};Pooling=False"))
        {
            connection.Open();
            using var held = connection.BeginTransaction();
            Click(window, "SaveProfile");
            var before = new LocalWorkObservation.ProfileSaveHold(submittedOwner, Activity(window), held.Connection == connection);
            LocalWorkObservation.RequireSameHeldProfile(before, before, "avery@example.com");
            var elapsed = Stopwatch.StartNew();
            managerSelection.Select();
            var destinationReady = false;
            ObserveNoteLatency(() => destinationReady = PersonReady("morgan@example.com", "Morgan Example"));
            navigation.Add(elapsed.Elapsed.TotalMilliseconds);
            var after = new LocalWorkObservation.ProfileSaveHold(submittedOwner, Activity(window), held.Connection == connection);
            heldNavigation = LocalWorkObservation.HeldProfileNavigationEvidence(navigation.ToArray(), before, after,
                "avery@example.com", destinationReady);
            using var reading = ApplicationWorkflow.CreateFileBacked();
            if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) ||
                reading.GetRelationships("avery@example.com")!.Person.OfficeNumber != "007B" ||
                reading.GetPhoto("avery@example.com") is not null)
                throw new InvalidOperationException("Held profile Save published partial facts or photo associations.");
            held.Commit();
        }
        AwaitLocalWorkIdle(window);
        AssertTeamNameReading(window, null);
        Browse("avery@example.com");
        AssertProfileValue(window, "OfficeNumber", "008C");
        AssertPhotoVisible(window);
        HistoricalTeamContext frozen;
        using (var reading = ApplicationWorkflow.CreateFileBacked())
        {
            if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly))
                throw new InvalidOperationException(reading.Status);
            frozen = reading.GetHistoricalTeamContexts("avery@example.com").Single();
            var captured = frozen.TeamNotes.Single(note => note.Id == team.Id);
            if (captured.Markdown != team.Markdown || frozen.ReportingChain.Single().TeamName is not null ||
                reading.GetRelationships("morgan@example.com")!.Person.TeamName != "Platform")
                throw new InvalidOperationException("SQLite Manager Save lost frozen note writing or current-only dormant Team-name semantics.");
        }
        ExpandHistory(window);
        var evidence = HistoricalEvidenceText(window);
        if (!evidence.Contains(frozen.Id.ToString()) || !evidence.Contains(team.Id.ToString()) ||
            !evidence.Contains(frozen.TeamNotes.Single(note => note.Id == team.Id).Version))
            throw new InvalidOperationException("Hosted SQLite Historical disclosure omitted complete capture/note/version evidence.");
        var disclosure = HistoricalCaptureDetails(window).Single();
        var expansion = (ExpandCollapsePattern)disclosure.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (expansion.Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
            throw new InvalidOperationException("SQLite Capture details must remain collapsed before explicit reading.");
        expansion.Expand();
        var capturedEvidence = Find(disclosure, $"HistoricalCaptureEvidence-{frozen.Id}");
        capturedEvidence.SetFocus();
        Await(() => capturedEvidence.Current.HasKeyboardFocus && !capturedEvidence.Current.IsOffscreen);
        var copyable = ((ValuePattern)capturedEvidence.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        Clipboard.SetText("Capture evidence has not been copied.");
        keybd_event(0x11, 0, 0, 0);
        PressCopyKey(0x41);
        PressCopyKey(0x43);
        keybd_event(0x11, 0, 2, 0);
        Await(() => Clipboard.GetText() == copyable);
        CaptureVisualEvidence(window, "sqlite-person-history-capture-details");
        expansion.Collapse();
        Browse("morgan@example.com");
        SelectTab(window, "TeamNoteEditTab");
        Set(window, "TeamNoteMarkdown", team.Markdown + "\nLater current-only writing");
        AwaitLocalWorkIdle(window);
        Browse("avery@example.com");
        if (HistoricalReadingText(window).Contains("Later current-only writing", StringComparison.Ordinal))
            throw new InvalidOperationException("Later SQLite note writing rewrote the frozen Historical reading.");
        RequestPersonArchival(window, archived: true, confirmed: true);
        AwaitLocalWorkIdle(window);
        AssertArchivalOwner(window, "avery@example.com", true);
        RequestPersonArchival(window, archived: false, confirmed: true);
        AwaitLocalWorkIdle(window);
        BeginProfileEditing(window);
        SelectManagerChoice(window, "Morgan Example (morgan@example.com)");
        SaveProfileEditing(window);
        AssertTeamNameReading(window, "Platform");
        var saves = new List<double>();
        for (var sample = 0; sample < 20; sample++)
        {
            BeginProfileEditing(window);
            var expected = sample == 19 ? "008C" : $"SQLite room {sample:D2}";
            Set(window, "OfficeNumber", expected);
            var save = ControlReadiness.AwaitPattern(() => Find(window, "SaveProfile"),
                control => control.Current.IsEnabled,
                control => control.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                    ? pattern as InvokePattern : null, Await);
            using var observation = NoteCommitObservation.Begin(save.Invoke, () =>
            {
                using var reading = ApplicationWorkflow.CreateFileBacked();
                if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly))
                    throw new InvalidOperationException("Hosted profile commit could not reopen: " + reading.Status);
                return reading.GetRelationships("avery@example.com")!.Person.OfficeNumber == expected &&
                    reading.GetPhoto("avery@example.com")?.Width == 2;
            });
            AwaitLocalWorkIdle(window);
            saves.Add(observation.Complete());
        }
        AssertPhotoVisible(window);
        CaptureVisualEvidence(window, "sqlite-person-saves-committed");
        var photoHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo)));
        File.Delete(photo);
        return new(window.Current.ProcessId, frozen.Id, team.Id, frozen.TeamNotes.Single(note => note.Id == team.Id).Version,
            photoHash, saves, heldNavigation ?? throw new InvalidOperationException("Held profile navigation evidence is missing."));
    }

    private static void WriteLocalPersonJourneyEvidence(AutomationElement window, LocalPersonJourney journey)
    {
        using var process = Process.GetProcessById(window.Current.ProcessId);
        using var input = File.OpenRead(process.MainModule!.FileName);
        var path = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-person-saves.json");
        if (File.Exists(path)) throw new InvalidOperationException("Hosted Person evidence already exists.");
        var p95 = journey.ProfileSaveMs.Order().ElementAt((int)Math.Ceiling(journey.ProfileSaveMs.Count * .95) - 1);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 2, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            executableSha256 = Convert.ToHexString(SHA256.HashData(input)),
            editingProcessId = journey.EditingProcessId, reopenedProcessId = window.Current.ProcessId,
            environment = new { os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                runtime = Environment.Version.ToString(), window = window.Current.BoundingRectangle.ToString() },
            workspaceFormat = "SQLite format 3; no legacy authority",
            ownerEmail = "avery@example.com", managerEmail = "morgan@example.com",
            preferredName = "Avery reference", officeNumber = "008C", jobTitle = "Independent researcher", isFavorite = true,
            teamName = "Platform", photoSha256 = journey.PhotoSha256, captureId = journey.CaptureId,
            noteId = journey.NoteId, frozenVersion = journey.FrozenVersion,
            profileDurability = new { boundary = "Native explicit Save Invoke through independently reopened exact complete profile and decoded photo; includes dispatch/queueing/commit.",
                samplesMs = journey.ProfileSaveMs, p95Ms = p95, budgetMs = 1000,
                percentile = "Nearest rank: sorted sample at ceil(0.95 * count), one based." },
            heldSaveNavigation = journey.HeldNavigation,
            captures = new[] { "sqlite-profile-retained-after-browse", "sqlite-person-history-capture-details",
                "sqlite-person-saves-committed", "sqlite-person-saves-reopened" },
            scope = "Focused two-Person native edit/browse/photo/history/archive/reopen journey; not rich 10,000-Person or background-export evidence."
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (p95 > 1000)
            throw new InvalidOperationException("Hosted SQLite Person save latency budget failed. See " + path);
        Console.WriteLine("PASS: hosted SQLite Person saves: native retained profile/Preferred-name drafts, exact durable photo/favorite/archival/Manager history, frozen capture disclosure and process reopening; candidate-bound evidence: " + path);
    }

    private static IReadOnlyDictionary<NoteScope, (Guid Id, string Title, string Markdown)> RunLocalNoteAutosaveJourney(
        AutomationElement window, string local)
    {
        var editors = new List<double>();
        var navigation = new List<double>();
        var durability = new List<double>();
        var editorRequests = new List<double>();
        var editorPolls = new List<int>();
        var editorNodes = new List<int>();
        var navigationRequests = new List<double>();
        var navigationPolls = new List<int>();
        var commitRequests = new List<double>();
        var commitReads = new List<int>();
        var commitReadMs = new List<double>();
        var observerAdmittedMs = new List<double?>();
        var firstReadStartedMs = new List<double?>();
        var successfulReadStartedMs = new List<double?>();
        var continuedEditRequestStartedMs = new List<double[]>();
        var continuedEditRequestCompletedMs = new List<double[]>();
        var typingCompletedMs = new List<double>();
        var identities = new HashSet<Guid>();
        var saved = new Dictionary<NoteScope, (Guid, string, string)>();
        Click(window, "NavigateManager");
        Await(() => CurrentPersonEmail(window) == "morgan@example.com");
        var email = Find(window, "Email");
        var profileHeading = Find(window, "ProfileHeading");
        var personCache = new CacheRequest { TreeScope = TreeScope.Element };
        personCache.Add(AutomationElement.NameProperty);
        personCache.Add(AutomationElement.IsOffscreenProperty);
        bool PersonReady(string expectedEmail, string expectedHeading)
        {
            var currentEmail = email.GetUpdatedCache(personCache);
            var currentHeading = profileHeading.GetUpdatedCache(personCache);
            return currentEmail.Cached.Name == expectedEmail && !currentEmail.Cached.IsOffscreen &&
                currentHeading.Cached.Name == expectedHeading;
        }
        foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
            for (var sample = 0; sample < (scope == NoteScope.Team ? 20 : 1); sample++)
            {
                var family = scope.ToString();
                var editorCache = NoteEditorCache(family);
                EnsurePeopleBrowsing(window);
                var report = Find(window, "People").FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "avery@example.com"))
                    ?? throw new InvalidOperationException("The hosted note owner lost its report in People.");
                var selectReport = (SelectionItemPattern)report.GetCurrentPattern(SelectionItemPattern.Pattern);
                var manager = (InvokePattern)Find(window, "NavigateManager").GetCurrentPattern(InvokePattern.Pattern);
                var surface = Find(window, family + "NotesSurface");
                var previous = ReadNoteEditor(surface, editorCache, family).Identity;
                var create = ControlReadiness.AwaitPattern(() => Find(window, "New" + family + "Note"),
                    control => control.Current.IsEnabled,
                    control => (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern), Await);
                var interaction = Stopwatch.StartNew();
                create.Invoke();
                var nativeMs = interaction.Elapsed.TotalMilliseconds;
                var observedNodes = 0;
                var polls = ObserveNoteLatency(() =>
                {
                    var frame = ReadNoteEditor(surface, editorCache, family);
                    observedNodes = frame.ObservedNodes;
                    return frame.Identity != previous && Guid.TryParse(frame.Identity, out _) &&
                        frame.Writable && frame.TitleVisible && frame.TitleFocused && frame.Editing;
                });
                if (scope == NoteScope.Team)
                {
                    editors.Add(interaction.Elapsed.TotalMilliseconds);
                    editorRequests.Add(nativeMs);
                    editorPolls.Add(polls);
                    editorNodes.Add(observedNodes);
                }
                AssertNoteView(window, family, editing: true);
                var id = Guid.Parse(Find(window, family + "NoteHeading").Current.HelpText);
                if (!identities.Add(id)) throw new InvalidOperationException("Repeated Add note reused another creation's identity.");
                var titleText = $"Hosted {family} context {sample:D2}";
                Set(window, family + "NoteTitle", titleText);
                var bodyPattern = (ValuePattern)Find(window, family + "NoteMarkdown").GetCurrentPattern(ValuePattern.Pattern);
                var text = $"# {family} continuous writing {sample:D2}";
                var prefix = text;
                using var firstCommit = NoteCommitObservation.Begin(() => bodyPattern.SetValue(prefix), () =>
                {
                    using var reading = ApplicationWorkflow.CreateFileBacked();
                    if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly))
                        throw new InvalidOperationException("Could not observe the hosted durable local commit: " + reading.Status);
                    return reading.BeginNoteDraft(scope, "morgan@example.com", id)?.Markdown
                        .StartsWith(prefix, StringComparison.Ordinal) == true;
                });

                interaction.Restart();
                selectReport.Select();
                nativeMs = interaction.Elapsed.TotalMilliseconds;
                polls = ObserveNoteLatency(() => PersonReady("avery@example.com", "Avery Example"));
                navigation.Add(interaction.Elapsed.TotalMilliseconds);
                navigationRequests.Add(nativeMs);
                navigationPolls.Add(polls);
                interaction.Restart();
                manager.Invoke();
                nativeMs = interaction.Elapsed.TotalMilliseconds;
                polls = ObserveNoteLatency(() => PersonReady("morgan@example.com", "Morgan Example"));
                navigation.Add(interaction.Elapsed.TotalMilliseconds);
                navigationRequests.Add(nativeMs);
                navigationPolls.Add(polls);
                ObserveNoteLatency(() =>
                {
                    var frame = ReadNoteEditor(surface, editorCache, family);
                    return frame.Identity == id.ToString() && frame.Title == titleText && frame.Markdown == text &&
                        frame.Writable && frame.Editing;
                });

                var continuedTyping = firstCommit.ContinueTyping(edit =>
                {
                    text += $"\nContinuing entry {edit}";
                    bodyPattern.SetValue(text);
                });
                var completedTyping = continuedTyping.RequestCompletedMs[^1];
                if (continuedTyping.RequestStartedMs[^1] - continuedTyping.RequestCompletedMs[0] < 1000)
                    throw new InvalidOperationException("The predetermined native typing workload did not span the one-second durability observation window.");
                durability.Add(firstCommit.Complete());
                commitRequests.Add(firstCommit.RequestMs);
                commitReads.Add(firstCommit.ReadCount);
                commitReadMs.Add(firstCommit.ReadMs);
                observerAdmittedMs.Add(firstCommit.WorkerAdmittedMs);
                firstReadStartedMs.Add(firstCommit.FirstReadStartedMs);
                successfulReadStartedMs.Add(firstCommit.SuccessfulReadStartedMs);
                continuedEditRequestStartedMs.Add(continuedTyping.RequestStartedMs);
                continuedEditRequestCompletedMs.Add(continuedTyping.RequestCompletedMs);
                typingCompletedMs.Add(completedTyping);
                AssertNoteView(window, family, editing: true);
                AssertValue(window, family + "NoteTitle", titleText);
                AssertValue(window, family + "NoteMarkdown", text);
                if (Find(window, family + "NoteHeading").Current.HelpText != id.ToString())
                    throw new InvalidOperationException("Native Person switches redirected the retained draft to another note.");
                if (sample == 19)
                {
                    var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
                    if (scroll.Current.VerticallyScrollable)
                        scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
                    Await(() => !Find(window, "TeamNoteTitle").Current.IsOffscreen &&
                        !Find(window, "TeamNoteMarkdown").Current.IsOffscreen);
                    CaptureVisualEvidence(window, "sqlite-nonblocking-note-editing");
                }
                Await(() =>
                {
                    using var reading = ApplicationWorkflow.CreateFileBacked();
                    return reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) &&
                        reading.BeginNoteDraft(scope, "morgan@example.com", id)?.Markdown == text &&
                        NoteFeedback(window, family).Contains("saved locally", StringComparison.Ordinal);
                });
                AssertValue(window, family + "NoteMarkdown", text);
                saved[scope] = (id, titleText, text);
            }
        Click(window, "UseAsOngoingTeamNote");
        Await(() => Find(window, "TeamNoteKind").Current.Name.Contains("Ongoing Team note"));
        AssertNoteView(window, "Team", editing: false);
        using (var reading = ApplicationWorkflow.CreateFileBacked())
        {
            if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) ||
                reading.GetTeamNotes("morgan@example.com").Count != 20 ||
                reading.GetPersonNotes("morgan@example.com").Count != 1 ||
                reading.GetInheritedTeamNotes("avery@example.com").Count != 20)
                throw new InvalidOperationException("Repeated hosted creation lost distinct notes or current Team inheritance.");
        }
        double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
        var committedDuringTyping = durability.Zip(typingCompletedMs, (commit, typed) => commit <= typed).ToArray();
        using var process = Process.GetProcessById(window.Current.ProcessId);
        var executable = process.MainModule!.FileName;
        using var input = File.OpenRead(executable);
        var resultPath = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-note-latency.json");
        if (File.Exists(resultPath)) throw new InvalidOperationException("Hosted note timing evidence already exists.");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new
        {
            revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            executableSha256 = Convert.ToHexString(SHA256.HashData(input)),
            environment = new { os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                runtime = Environment.Version.ToString(), window = window.Current.BoundingRectangle.ToString() },
            workload = "Ordinary two-Person SQLite fixture; 20 distinct Team creations, one Person creation, 20 predetermined continued edits at nominal 60-ms spacing and immediate report/Manager switches per note. All edits execute independently of save observation. Combined rich 10,000-Person/background-export proof belongs to #100.",
            percentile = "Nearest rank: sorted sample at ceil(0.95 * count), one based.",
            observation = "Acceptance samples retain all native-request-to-observed-state time, without subtracting instrumentation. Each post-action editor cache selects only the current scope's surface, exact note identity, title, Markdown and Edit tab; it does not request unrelated note reading/list controls. Identity properties come from freshly updated permanent native elements. An independent real-workflow reopen observer starts before the first Markdown request and timestamps its first coherent committed result while the native journey continues.",
            addTeamNote = new { boundary = "Native Invoke request through new exact identity, enabled writable editor, visible title and actual keyboard focus.",
                samplesMs = editors, p95Ms = P95(editors), budgetMs = 100,
                nativeRequestMs = editorRequests,
                observationMs = editors.Zip(editorRequests, (total, request) => total - request).ToArray(),
                observationPolls = editorPolls, observationNodes = editorNodes },
            personNavigation = new { boundary = "Native report selection in People or Manager Invoke through exact selected email, heading and visible usable Person.",
                samplesMs = navigation, p95Ms = P95(navigation), budgetMs = 100,
                nativeRequestMs = navigationRequests,
                observationMs = navigation.Zip(navigationRequests, (total, request) => total - request).ToArray(),
                observationPolls = navigationPolls },
            localDurability = new { boundary = "First native Markdown edit through independently reopened committed writing covering that request while typing continues; includes dispatch, scheduling and queueing.",
                samplesMs = durability, p95Ms = P95(durability), budgetMs = 1000,
                nativeRequestMs = commitRequests, observationReads = commitReads, observationReadMs = commitReadMs,
                observerAdmittedMs, firstReadStartedMs, successfulReadStartedMs,
                continuedEditsPerNote = NoteCommitObservation.ContinuedEditCount,
                nominalEditSpacingMs = NoteCommitObservation.ContinuedEditSpacingMs,
                continuedEditRequestStartedMs, continuedEditRequestCompletedMs,
                typingCompletionBoundary = "Actual completion of the final predetermined native Markdown SetValue; no trailing sleep, idle wait or save-conditioned continuation.",
                typingCompletedMs, committedDuringTyping }
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (P95(editors) > 100 || P95(navigation) > 100 || P95(durability) > 1000 || committedDuringTyping.Any(value => !value))
            throw new InvalidOperationException($"Hosted ordinary note latency budget failed: editor={P95(editors):F1} ms, " +
                $"navigation={P95(navigation):F1} ms, local durability={P95(durability):F1} ms, " +
                $"all commits observed during typing={committedDuringTyping.All(value => value)}. See {resultPath}.");
        Console.WriteLine($"PASS: hosted SQLite nonblocking notes: usable Team editor p95={P95(editors):F1} ms; " +
            $"usable Person navigation p95={P95(navigation):F1} ms; continuous-typing durable commit p95={P95(durability):F1} ms; exact note/owner drafts retained.");
        return saved;
    }

    private static int ObserveNoteLatency(Func<bool> condition)
    {
        using var waiting = HarnessRuntime.Measure(HarnessRuntime.Work.NativeWait);
        var elapsed = Stopwatch.StartNew();
        var polls = 0;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            polls++;
            using (HarnessRuntime.Measure(HarnessRuntime.Work.Observation))
                if (condition()) return polls;
            Thread.Sleep(1);
        }
        throw new TimeoutException("The actual usable note/Person condition did not become true within the existing 20-second observation budget.");
    }

    private sealed record NoteEditorFrame(string? Identity, string? Title, string? Markdown,
        bool Writable, bool Editing, bool TitleVisible, bool TitleFocused, int ObservedNodes);

    private static void VerifyCompiledNoteObservation(UserControl surface, NoteScope scope, OwnedNote expected)
    {
        var family = scope.ToString();
        var cache = NoteEditorCache(family);
        IEnumerable<AutomationPeer> Descendants(AutomationPeer peer)
        {
            yield return peer;
            foreach (var child in peer.GetChildren() ?? [])
                foreach (var descendant in Descendants(child)) yield return descendant;
        }
        bool Includes(System.Windows.Automation.Condition condition, AutomationPeer peer)
        {
            if (condition == Automation.RawViewCondition) return true;
            if (condition is OrCondition either) return either.GetConditions().Any(child => Includes(child, peer));
            if (condition is PropertyCondition property && property.Property == AutomationElement.AutomationIdProperty)
                return Equals(property.Value, peer.GetAutomationId());
            throw new InvalidOperationException("Unsupported note observation view in the compiled contract.");
        }
        var root = UIElementAutomationPeer.CreatePeerForElement(surface)
            ?? throw new InvalidOperationException("The actual compiled note surface has no automation peer.");
        var nodes = Descendants(root).Where(peer => Includes(cache.TreeFilter, peer)).ToArray();
        if (nodes.Any(peer => peer.GetAutomationId() is not { } id ||
            id != family + "NotesSurface" && id != family + "NoteHeading" && id != family + "NoteTitle" &&
            id != family + "NoteMarkdown" && id != family + "NoteEditTab"))
            throw new InvalidOperationException("The note observation requests unrelated controls instead of only the current editor's identity, writing, mode and focus.");
        AutomationPeer Required(string suffix) => nodes.Single(peer => peer.GetAutomationId() == family + suffix);
        var heading = Required("NoteHeading");
        var title = Required("NoteTitle");
        var body = Required("NoteMarkdown");
        var tab = Required("NoteEditTab");
        if (heading.GetHelpText() != expected.Id.ToString() ||
            title.GetPattern(PatternInterface.Value) is not IValueProvider titleValue || titleValue.Value != expected.Title ||
            body.GetPattern(PatternInterface.Value) is not IValueProvider bodyValue || bodyValue.Value != expected.Markdown ||
            !title.IsEnabled() || !body.IsEnabled() || titleValue.IsReadOnly || bodyValue.IsReadOnly ||
            tab.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection || !selection.IsSelected ||
            nodes.OfType<UIElementAutomationPeer>().Any(peer => peer.Owner != surface && !peer.Owner.IsDescendantOf(surface)))
            throw new InvalidOperationException("The compiled observation lost the exact current writable note, selected mode or live owner subtree.");
        if (surface.IsVisible || title.HasKeyboardFocus())
            throw new InvalidOperationException("The compiled observation cannot claim native visible/focused editor acceptance.");
    }

    private static CacheRequest NoteEditorCache(string family)
    {
        var cache = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            TreeFilter = new OrCondition(new[] { "NotesSurface", "NoteHeading", "NoteTitle", "NoteMarkdown", "NoteEditTab" }
                .Select(suffix => new PropertyCondition(AutomationElement.AutomationIdProperty, family + suffix)).ToArray())
        };
        foreach (var property in new[] { AutomationElement.AutomationIdProperty, AutomationElement.HelpTextProperty,
            AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.HasKeyboardFocusProperty, ValuePattern.IsReadOnlyProperty, ValuePattern.ValueProperty,
            SelectionItemPattern.IsSelectedProperty })
            cache.Add(property);
        return cache;
    }

    private static NoteEditorFrame ReadNoteEditor(AutomationElement surface, CacheRequest cache, string family)
    {
        using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.NoteSnapshot);
        var nodes = new List<AutomationElement>();
        var pending = new Stack<AutomationElement>();
        pending.Push(surface.GetUpdatedCache(cache));
        while (pending.TryPop(out var node))
        {
            nodes.Add(node);
            foreach (AutomationElement child in node.CachedChildren) pending.Push(child);
        }
        AutomationElement? FindCached(string suffix) => nodes.FirstOrDefault(node =>
            Equals(node.GetCachedPropertyValue(AutomationElement.AutomationIdProperty), family + suffix));
        object? Property(AutomationElement? node, AutomationProperty property) =>
            node?.GetCachedPropertyValue(property, ignoreDefaultValue: true);
        var title = FindCached("NoteTitle");
        var body = FindCached("NoteMarkdown");
        return new(Property(FindCached("NoteHeading"), AutomationElement.HelpTextProperty) as string,
            Property(title, ValuePattern.ValueProperty) as string, Property(body, ValuePattern.ValueProperty) as string,
            Equals(Property(title, AutomationElement.IsEnabledProperty), true) &&
                Equals(Property(body, AutomationElement.IsEnabledProperty), true) &&
                Equals(Property(title, ValuePattern.IsReadOnlyProperty), false) &&
                Equals(Property(body, ValuePattern.IsReadOnlyProperty), false),
            Equals(Property(FindCached("NoteEditTab"), SelectionItemPattern.IsSelectedProperty), true),
            Equals(Property(title, AutomationElement.IsOffscreenProperty), false),
            Equals(Property(title, AutomationElement.HasKeyboardFocusProperty), true), nodes.Count);
    }
}
