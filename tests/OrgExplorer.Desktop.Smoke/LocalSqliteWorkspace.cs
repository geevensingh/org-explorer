using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
                Note<TextBox>("NoteTitle").Text = $"Retained {scope}";
                Note<TextBox>("MarkdownEditor").Text = $"Original {scope} writing";
                PumpUntil(() => workflow.GetRetainedNoteDraft(scope, id)?.IsSaving == true);
                Note<TextBox>("MarkdownEditor").Text = $"Newer {scope} writing during the admitted save";
                Select("avery@example.com");
                Select("morgan@example.com");
                if (Note<TextBox>("MarkdownEditor").Text != $"Newer {scope} writing during the admitted save" ||
                    !Note<TextBox>("MarkdownEditor").IsEnabled || workflow.CanSafelyClose ||
                    workflow.GetOwnedNotesView(scope).SelectedNote?.Id != id)
                    throw new InvalidOperationException("Compiled Person switching lost, disabled, redirected or falsely saved its retained note.");
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
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("SQLite navigation verification must not show a window or create its native handle.");
            Console.WriteLine("PASS: compiled SQLite Create returns to the ordinary start view and normal Settings Import reaches a real reviewed preview without a visible window/native handle.");
            Console.WriteLine("PASS: compiled SQLite Person/Team editors open before commit, retain newer writing across actual selection events and durably save the original owner without a visible window/native handle.");
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
                    if (Find(window, "EditProfile").Current.IsEnabled ||
                        Find(window, "ToggleFavorite").Current.IsEnabled)
                        throw new InvalidOperationException("The incomplete SQLite path enabled unsupported ordinary edits.");
                    CaptureVisualEvidence(window, "sqlite-initial-import-browsing");
                    savedNotes = RunLocalNoteAutosaveJourney(window, local);
                    SelectTreePerson(window, "avery@example.com");
                    Await(() => CurrentPersonEmail(window) == "avery@example.com");
                    AssertWorkspaceBytes(oldWorkspace, originals);
                    Close(process);
                }
                finally { StopApplication(process); }
            }
            using var reopened = Launch(executable);
            try
            {
                var window = Window(reopened);
                AwaitRestoredWorkspace(window, local, "avery@example.com");
                AssertProfileValue(window, "OfficeNumber", "007B");
                AssertProfileValue(window, "OfficeCountry", "Canada");
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
                AssertWorkspaceBytes(oldWorkspace, originals);
                Close(reopened);
            }
            finally { StopApplication(reopened); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings);
            Environment.SetEnvironmentVariable("ORG_EXPLORER_ENABLE_SQLITE_WORKSPACES", previousOptIn);
        }
    }

    private static IReadOnlyDictionary<NoteScope, (Guid Id, string Title, string Markdown)> RunLocalNoteAutosaveJourney(
        AutomationElement window, string local)
    {
        var editors = new List<double>();
        var navigation = new List<double>();
        var durability = new List<double>();
        var identities = new HashSet<Guid>();
        var saved = new Dictionary<NoteScope, (Guid, string, string)>();
        Click(window, "NavigateManager");
        Await(() => CurrentPersonEmail(window) == "morgan@example.com");
        foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
            for (var sample = 0; sample < (scope == NoteScope.Team ? 20 : 1); sample++)
            {
                var family = scope.ToString();
                var oldHeading = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, family + "NoteHeading"));
                var previous = oldHeading?.Current.HelpText;
                var create = ControlReadiness.AwaitPattern(() => Find(window, "New" + family + "Note"),
                    control => control.Current.IsEnabled,
                    control => (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern), Await);
                var interaction = Stopwatch.StartNew();
                create.Invoke();
                ObserveNoteLatency(() =>
                {
                    var title = window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, family + "NoteTitle"));
                    var body = window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, family + "NoteMarkdown"));
                    var heading = window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, family + "NoteHeading"));
                    return title is not null && body is not null && heading is not null &&
                        heading.Current.HelpText != previous && Guid.TryParse(heading.Current.HelpText, out _) &&
                        title.Current.IsEnabled && body.Current.IsEnabled &&
                        !title.Current.IsOffscreen && title.Current.HasKeyboardFocus &&
                        !((ValuePattern)body.GetCurrentPattern(ValuePattern.Pattern)).Current.IsReadOnly;
                });
                if (scope == NoteScope.Team) editors.Add(interaction.Elapsed.TotalMilliseconds);
                AssertNoteView(window, family, editing: true);
                var id = Guid.Parse(Find(window, family + "NoteHeading").Current.HelpText);
                if (!identities.Add(id)) throw new InvalidOperationException("Repeated Add note reused another creation's identity.");
                var titleText = $"Hosted {family} context {sample:D2}";
                Set(window, family + "NoteTitle", titleText);
                var bodyPattern = (ValuePattern)Find(window, family + "NoteMarkdown").GetCurrentPattern(ValuePattern.Pattern);
                var firstRequest = Stopwatch.StartNew();
                var text = $"# {family} continuous writing {sample:D2}";
                bodyPattern.SetValue(text);

                EnsurePeopleBrowsing(window);
                var report = Find(window, "People").FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "avery@example.com"))
                    ?? throw new InvalidOperationException("The hosted note owner lost its report in People.");
                interaction.Restart();
                ((SelectionItemPattern)report.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                ObserveNoteLatency(() => CurrentPersonEmail(window) == "avery@example.com" &&
                    Find(window, "ProfileHeading").Current.Name == "Avery Example" &&
                    !Find(window, "Email").Current.IsOffscreen);
                navigation.Add(interaction.Elapsed.TotalMilliseconds);
                var manager = (InvokePattern)Find(window, "NavigateManager").GetCurrentPattern(InvokePattern.Pattern);
                interaction.Restart();
                manager.Invoke();
                ObserveNoteLatency(() => CurrentPersonEmail(window) == "morgan@example.com" &&
                    Find(window, "ProfileHeading").Current.Name == "Morgan Example" &&
                    !Find(window, "Email").Current.IsOffscreen);
                navigation.Add(interaction.Elapsed.TotalMilliseconds);
                AssertNoteView(window, family, editing: true);
                AssertValue(window, family + "NoteTitle", titleText);
                AssertValue(window, family + "NoteMarkdown", text);
                if (Find(window, family + "NoteHeading").Current.HelpText != id.ToString())
                    throw new InvalidOperationException("Native Person switches redirected the retained draft to another note.");

                double? firstCommit = null;
                for (var edit = 0; edit < 6; edit++)
                {
                    text += $"\nContinuing entry {edit}";
                    bodyPattern.SetValue(text);
                    Thread.Sleep(60);
                    using var reading = ApplicationWorkflow.CreateFileBacked();
                    if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly))
                        throw new InvalidOperationException("Could not observe the hosted durable local commit: " + reading.Status);
                    if (reading.BeginNoteDraft(scope, "morgan@example.com", id)?.Markdown
                        .StartsWith($"# {family} continuous writing {sample:D2}", StringComparison.Ordinal) == true)
                        firstCommit ??= firstRequest.Elapsed.TotalMilliseconds;
                }
                if (firstCommit is null)
                    throw new InvalidOperationException("Hosted continuous note typing made no durable progress before idle.");
                durability.Add(firstCommit.Value);
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
            workload = "Ordinary two-Person SQLite fixture; 20 distinct Team creations, one Person creation, six continued edits and immediate report/Manager switches per note. Combined rich 10,000-Person/background-export proof belongs to #100.",
            percentile = "Nearest rank: sorted sample at ceil(0.95 * count), one based.",
            addTeamNote = new { boundary = "Native Invoke request through new exact identity, enabled writable editor, visible title and actual keyboard focus.",
                samplesMs = editors, p95Ms = P95(editors), budgetMs = 100 },
            personNavigation = new { boundary = "Native report selection in People or Manager Invoke through exact selected email, heading and visible usable Person.",
                samplesMs = navigation, p95Ms = P95(navigation), budgetMs = 100 },
            localDurability = new { boundary = "First native Markdown edit through independently reopened committed writing covering that request while typing continues; includes dispatch, scheduling and queueing.",
                samplesMs = durability, p95Ms = P95(durability), budgetMs = 1000 }
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (P95(editors) > 100 || P95(navigation) > 100 || P95(durability) > 1000)
            throw new InvalidOperationException($"Hosted ordinary note latency budget failed: editor={P95(editors):F1} ms, " +
                $"navigation={P95(navigation):F1} ms, local durability={P95(durability):F1} ms. See {resultPath}.");
        Console.WriteLine($"PASS: hosted SQLite nonblocking notes: usable Team editor p95={P95(editors):F1} ms; " +
            $"usable Person navigation p95={P95(navigation):F1} ms; continuous-typing durable commit p95={P95(durability):F1} ms; exact note/owner drafts retained.");
        return saved;
    }

    private static void ObserveNoteLatency(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (condition()) return;
            Thread.Sleep(1);
        }
        throw new TimeoutException("The actual usable note/Person condition did not become true within the existing 20-second observation budget.");
    }
}
