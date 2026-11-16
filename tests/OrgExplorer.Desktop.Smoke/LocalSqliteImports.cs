using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyLocalReviewedImportControls(Window window, WorkspaceViewModel workflow,
        Action<string> select, Action arrange, string root)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var source = Path.Combine(root, "compiled-reviewed-import.json");
        var photo = Path.Combine(root, "compiled-import-photo.png");
        CreatePhotoFixture(photo);
        File.WriteAllText(source, ReviewedLocalImport(Path.GetFileName(photo)));
        Invoke("OpenSettingsButton");
        Invoke("OpenImportButton");
        Control<TextBox>("ImportPath").Text = source;
        Invoke("PreviewButton");
        PumpUntil(() => Control<Button>("ApplyButton").IsEnabled);
        if (workflow.People.Count != 2 || workflow.GetRelationships("avery@example.com")!.Person.OfficeCountry == "Japan")
            throw new InvalidOperationException("Reviewed SQLite updates were accepted before Apply.");
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(workflow.Compatibility.WorkspacePath!, "workspace.sqlite")};Pooling=False");
        connection.Open();
        using var held = connection.BeginTransaction();
        Guid retainedId = default;
        var selections = new List<object>();
        void SelectWhileHeld(string email)
        {
            if (Control<TextBlock>("WorkflowActivity").Text != "Applying reviewed Import…" || held.Connection != connection)
                throw new InvalidOperationException("Compiled Import navigation did not begin while the same Apply/writer remained held.");
            var elapsed = Stopwatch.StartNew();
            select(email);
            var handlerMs = elapsed.Elapsed.TotalMilliseconds;
            arrange();
            var throughLayoutMs = elapsed.Elapsed.TotalMilliseconds;
            if (Control<TextBlock>("WorkflowActivity").Text != "Applying reviewed Import…" || held.Connection != connection ||
                Control<TextBlock>("Email").Text != email || workflow.PeopleNavigation.SelectedEmail != email)
                throw new InvalidOperationException("Compiled Import selection/layout waited for Apply or displayed the wrong Person.");
            selections.Add(new { destination = email, handlerMs, throughLayoutMs, sameApplyAndWriterHeld = true });
        }
        try
        {
            Invoke("ApplyButton");
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Applying reviewed Import…");
            LogicalDescendants(Control<FrameworkElement>("ImportPanel")).OfType<Button>().Single(button =>
                AutomationProperties.GetAutomationId(button) == "BackToPeople").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            arrange();
            if (Control<FrameworkElement>("ExplorerPanel").Visibility != Visibility.Visible || workflow.IsImportOpen)
                throw new InvalidOperationException("Back to People waited behind the held local Import instead of exposing usable browsing.");
            Control<TextBox>("SearchQuery").Text = "morgan";
            PumpUntil(() => Control<ListBox>("SearchResults").Items.OfType<SearchResult>()
                .Any(result => result.Person.Email == "morgan@example.com"));
            Control<TextBox>("SearchQuery").Clear();
            SelectWhileHeld("morgan@example.com");
            var surface = Control<UserControl>("TeamNotes");
            T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
            retainedId = workflow.TeamNotesView.SelectedNote?.Id
                ?? throw new InvalidOperationException("Bulk Import browsing lost the owner's selected note.");
            Note<TabItem>("EditTab").IsSelected = true;
            if (!Note<TextBox>("MarkdownEditor").IsEnabled)
                throw new InvalidOperationException("Held local Import disabled the selected owner's note editor.");
            Note<TextBox>("MarkdownEditor").Text = "# Retained during reviewed Import";
            SelectWhileHeld("avery@example.com");
            SelectWhileHeld("morgan@example.com");
            if (Note<TextBox>("MarkdownEditor").Text != "# Retained during reviewed Import" ||
                workflow.TeamNotesView.SelectedNote?.Id != retainedId || workflow.CanSafelyClose ||
                Control<TextBlock>("WorkflowActivity").Text != "Applying reviewed Import…" || held.Connection != connection)
                throw new InvalidOperationException("Bulk Import navigation lost or falsely acknowledged the held owner draft.");
            using var reading = ApplicationWorkflow.CreateFileBacked();
            if (!reading.OpenWorkspace(workflow.Compatibility.WorkspacePath!, WorkspaceOpenMode.ExistingOnly) ||
                reading.People.Count != 2 || reading.GetRelationships("avery@example.com")!.Person.OfficeCountry == "Japan")
                throw new InvalidOperationException("Held local Import exposed a partially committed population.");
        }
        finally
        {
            held.Commit();
            PumpUntil(() => Control<TextBlock>("WorkflowActivity").Text == "Ready");
        }
        PumpUntil(() => workflow.CanSafelyClose);
        Console.WriteLine("DIAGNOSTIC: compiled SQLite reviewed Import selection " + JsonSerializer.Serialize(new
        {
            selections,
            boundary = "Existing invisible compiled Person selection handlers and layout while the same Apply/SQLite writer remain held; not native provider/observation cost or a native latency verdict."
        }));
        File.Delete(photo);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        if (!reopened.OpenWorkspace(workflow.Compatibility.WorkspacePath!, WorkspaceOpenMode.ExistingOnly) ||
            reopened.People.Count != 3 || reopened.GetRelationships("avery@example.com")!.Person.OfficeCountry != "Japan" ||
            reopened.GetPhoto("avery@example.com")?.Width != 2 ||
            reopened.BeginNoteDraft(NoteScope.Team, "morgan@example.com", retainedId)?.Markdown != "# Retained during reviewed Import" ||
            reopened.BeginNoteDraft(NoteScope.Team, "avery@example.com", retainedId) is not null ||
            reopened.GetHistoricalTeamContexts("avery@example.com").Count != 1)
            throw new InvalidOperationException("Compiled reviewed Import did not reopen coherent People, resources, history and exact owner writing.");
        Console.WriteLine("PASS: compiled SQLite reviewed multi-Person/photo Import permits Back to People and actual Person/note navigation while its transaction remains held, retaining newer owner writing through commit/reopen without a visible window/native handle.");
    }

    private static string ReviewedLocalImport(string photo) => $$"""
        {"schemaVersion":1,"batchId":"94000000-0000-4000-8000-000000000020",
         "observedAt":"2026-11-08T17:00:00Z","producer":"Hosted reviewed Import","people":[
         {"email":"avery@example.com","officeCountry":"Japan","managerEmail":null,"isFavorite":false,"photoPath":"{{photo}}"},
         {"email":"morgan@example.com","jobTitle":"Imported director","photoPath":"{{photo}}"},
         {"email":"riley@example.com","displayName":"Riley Example","managerEmail":"morgan@example.com"}]}
        """;

    private static void RunLocalReviewedImportFlow(string executable, string root, string local)
    {
        var source = Path.Combine(root, "sqlite-reviewed-import.json");
        var photo = Path.Combine(root, "sqlite-import-portrait.png");
        CreatePhotoFixture(photo);
        var photoHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photo)));
        File.WriteAllText(source, ReviewedLocalImport(Path.GetFileName(photo)));
        PersonProfile expectedPerson, expectedManager, formerManager;
        PersonNote originalNote;
        TeamNote[] originalTeamNotes;
        Guid[] priorCaptures;
        using (var reading = ApplicationWorkflow.CreateFileBacked())
        {
            if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly))
                throw new InvalidOperationException(reading.Status);
            expectedPerson = reading.GetRelationships("avery@example.com")!.Person with
            {
                OfficeCountry = "Japan", ManagerEmail = null, IsFavorite = false,
                PhotoPath = photoHash.ToLowerInvariant() + ".png"
            };
            formerManager = reading.GetRelationships("morgan@example.com")!.Person;
            expectedManager = formerManager with
            {
                JobTitle = "Imported director", PhotoPath = expectedPerson.PhotoPath
            };
            originalNote = reading.GetPersonNotes("morgan@example.com").Single();
            originalTeamNotes = reading.GetTeamNotes("morgan@example.com").ToArray();
            priorCaptures = reading.GetHistoricalTeamContexts("avery@example.com").Select(item => item.Id).ToArray();
        }
        var retainedWriting = originalNote.Markdown + "\nRetained during reviewed Import";
        var navigation = new List<double>();
        var navigationTimings = new List<ImportNavigationTiming>();
        double retainedWritingRequestStartedMs, retainedWritingRequestReturnedMs;
        double backMs, applyMs;
        int editingProcessId;
        HistoricalTeamContext capture;
        string committed;
        string State(IApplicationWorkflow workflow) => JsonSerializer.Serialize(new
        {
            workflow.People,
            Timelines = workflow.People.Select(person => workflow.GetProfileTimeline(person.Email)).ToArray(),
            Captures = workflow.People.Select(person => workflow.GetHistoricalTeamContexts(person.Email)).ToArray(),
            PersonNotes = workflow.GetPersonNotes("morgan@example.com"),
            TeamNotes = workflow.GetTeamNotes("morgan@example.com"),
            Photos = workflow.People.Select(person => workflow.GetPhoto(person.Email) is { } image
                ? Convert.ToHexString(SHA256.HashData(image.Bytes.Span)) : null).ToArray()
        });
        using (var process = Launch(executable))
        {
            try
            {
                var window = Window(process);
                editingProcessId = process.Id;
                AwaitRestoredWorkspace(window, local, "morgan@example.com");
                EnsurePeopleBrowsing(window);
                var manager = Find(window, "People").FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "morgan@example.com"))!;
                var expansion = (ExpandCollapsePattern)manager.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) expansion.Expand();
                var managerSelection = (SelectionItemPattern)manager.GetCurrentPattern(SelectionItemPattern.Pattern);
                var reportSelection = (SelectionItemPattern)Find(window, "People").FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "avery@example.com"))!
                    .GetCurrentPattern(SelectionItemPattern.Pattern);
                SelectTab(window, "PersonNoteEditTab");
                var editor = Find(window, "PersonNoteMarkdown");
                var writing = (ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern);
                var email = Find(window, "Email");
                var heading = Find(window, "ProfileHeading");
                var cache = new CacheRequest { TreeScope = TreeScope.Element };
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                bool PersonReady(ImportNavigationTiming timing, string owner, string name) =>
                    timing.Query(ImportNavigationTiming.Field.Email, () =>
                        email.GetUpdatedCache(cache).Cached is { Name: var actual, IsOffscreen: false } && actual == owner) &&
                    timing.Query(ImportNavigationTiming.Field.Heading, () =>
                        heading.GetUpdatedCache(cache).Cached is { Name: var actualName, IsOffscreen: false } && actualName == name);
                OpenImportView(window);
                Set(window, "ImportPath", source);
                Click(window, "PreviewImport");
                Await(() => Find(window, "ApplyImport").Current.IsEnabled);
                using (var unchanged = ApplicationWorkflow.CreateFileBacked())
                    if (!unchanged.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) || unchanged.People.Count != 2 ||
                        unchanged.GetRelationships("avery@example.com")!.Person.OfficeCountry != "Canada")
                        throw new InvalidOperationException("Hosted reviewed Import changed data during preview.");
                CaptureVisualEvidence(window, "sqlite-reviewed-import-preview");
                var apply = (InvokePattern)Find(window, "ApplyImport").GetCurrentPattern(InvokePattern.Pattern);
                var back = (InvokePattern)Find(window, "BackToPeople").GetCurrentPattern(InvokePattern.Pattern);
                var elapsed = Stopwatch.StartNew();
                using (var connection = new SqliteConnection($"Data Source={Path.Combine(local, "workspace.sqlite")};Pooling=False"))
                {
                    connection.Open();
                    using var held = connection.BeginTransaction();
                    elapsed.Restart();
                    apply.Invoke();
                    Await(() => Activity(window) == "Applying reviewed Import…");
                    var browsing = Stopwatch.StartNew();
                    var backTiming = new ImportNavigationTiming("back-to-people", "morgan@example.com",
                        () => browsing.Elapsed.TotalMilliseconds);
                    navigationTimings.Add(backTiming);
                    backTiming.Request(back.Invoke);
                    ObserveNoteLatency(() => backTiming.Observe(() => PersonReady(backTiming, "morgan@example.com", "Morgan Example")));
                    backMs = browsing.Elapsed.TotalMilliseconds;
                    backTiming.Complete(backMs);
                    retainedWritingRequestStartedMs = elapsed.Elapsed.TotalMilliseconds;
                    writing.SetValue(retainedWriting);
                    retainedWritingRequestReturnedMs = elapsed.Elapsed.TotalMilliseconds;
                    browsing.Restart();
                    var reportTiming = new ImportNavigationTiming("select-person", "avery@example.com",
                        () => browsing.Elapsed.TotalMilliseconds);
                    navigationTimings.Add(reportTiming);
                    reportTiming.Request(reportSelection.Select);
                    ObserveNoteLatency(() => reportTiming.Observe(() => PersonReady(reportTiming, "avery@example.com", "Avery reference")));
                    navigation.Add(browsing.Elapsed.TotalMilliseconds);
                    reportTiming.Complete(navigation[^1]);
                    browsing.Restart();
                    var managerTiming = new ImportNavigationTiming("select-person", "morgan@example.com",
                        () => browsing.Elapsed.TotalMilliseconds);
                    navigationTimings.Add(managerTiming);
                    managerTiming.Request(managerSelection.Select);
                    ObserveNoteLatency(() => managerTiming.Observe(() => PersonReady(managerTiming, "morgan@example.com", "Morgan Example")));
                    navigation.Add(browsing.Elapsed.TotalMilliseconds);
                    managerTiming.Complete(navigation[^1]);
                    if (Activity(window) != "Applying reviewed Import…" || held.Connection != connection ||
                        !editor.Current.IsEnabled || writing.Current.Value != retainedWriting)
                        throw new InvalidOperationException("Native bulk browsing completed only after Apply or lost/disabled the retained owner's writing.");
                    using var unchanged = ApplicationWorkflow.CreateFileBacked();
                    if (!unchanged.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) || unchanged.People.Count != 2 ||
                        unchanged.GetRelationships("avery@example.com")!.Person.OfficeCountry != "Canada")
                        throw new InvalidOperationException("Held native Import published partial population/resources.");
                    held.Commit();
                }
                AwaitLocalWorkIdle(window);
                using (var reading = ApplicationWorkflow.CreateFileBacked())
                {
                    if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) || reading.People.Count != 3 ||
                        reading.GetRelationships("avery@example.com")!.Person != expectedPerson ||
                        reading.GetRelationships("morgan@example.com")!.Person != expectedManager ||
                        reading.GetPhoto("avery@example.com")?.Width != 2 || reading.GetPhoto("morgan@example.com")?.Width != 2 ||
                        reading.BeginNoteDraft(NoteScope.Person, originalNote.OwnerEmail, originalNote.Id)?.Markdown != retainedWriting ||
                        reading.BeginNoteDraft(NoteScope.Person, "avery@example.com", originalNote.Id) is not null)
                        throw new InvalidOperationException("Hosted reviewed Apply lost exact partial values, resources or retained note ownership.");
                    capture = reading.GetHistoricalTeamContexts("avery@example.com").Single(item => !priorCaptures.Contains(item.Id));
                    if (capture.ReportingChain.Single() != formerManager with { TeamName = null })
                        throw new InvalidOperationException("Reviewed Import changed the former chain or leaked private Team names.");
                    if (!capture.TeamNotes.Select(note => (note.Id, note.OwnerEmail, note.Title, note.Markdown, note.CreatedAt, note.UpdatedAt, note.NeedsReview))
                            .SequenceEqual(originalTeamNotes.Select(note =>
                                (note.Id, note.OwnerEmail, note.Title, note.Markdown, note.CreatedAt, note.UpdatedAt, note.NeedsReview))) ||
                        capture.Transitions.Single().Change.Evidence.BatchId != Guid.Parse("94000000-0000-4000-8000-000000000020") ||
                        reading.GetRelationships("riley@example.com")!.Manager!.Email != "morgan@example.com" ||
                        reading.GetTeamNames("riley@example.com").Single().Owner.Email != "morgan@example.com")
                        throw new InvalidOperationException("Hosted reviewed Import lost frozen Team writing, causal evidence or attributed current labels.");
                    committed = State(reading);
                }
                applyMs = elapsed.Elapsed.TotalMilliseconds;
                CaptureVisualEvidence(window, "sqlite-reviewed-import-retained-owner");
                SelectTreePerson(window, "avery@example.com");
                Await(() => CurrentPersonEmail(window) == "avery@example.com");
                AssertProfileValue(window, "OfficeCountry", "Japan");
                AssertPhotoVisible(window);
                AssertTeamNameReading(window, null);
                ExpandHistory(window);
                if (!HistoricalEvidenceText(window).Contains(capture.Id.ToString()) ||
                    !HistoricalEvidenceText(window).Contains("94000000-0000-4000-8000-000000000020"))
                    throw new InvalidOperationException("Hosted reviewed Import history omitted its exact capture/batch evidence.");
                CaptureVisualEvidence(window, "sqlite-reviewed-import-history");
                foreach (var replay in new[] { Path.Combine(root, "sqlite-initial.json"), source })
                {
                    OpenImportView(window);
                    Set(window, "ImportPath", replay);
                    Click(window, "PreviewImport");
                    Await(() => Find(window, "ApplyImport").Current.IsEnabled);
                    if (!Find(window, "Preview").Current.Name.Contains("no-op"))
                        throw new InvalidOperationException("Accepted original/later Import replay was not reviewed as an exact no-op.");
                    Click(window, "ApplyImport");
                    Await(() => Status(window) == "Already applied: no changes.");
                    ReturnToPeople(window);
                    using var reading = ApplicationWorkflow.CreateFileBacked();
                    if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) || State(reading) != committed)
                        throw new InvalidOperationException("Hosted exact Import replay duplicated or rolled back retained data.");
                }
                File.Delete(photo);
                Close(process);
            }
            finally { StopApplication(process); }
        }
        using var reopened = Launch(executable);
        try
        {
            var window = Window(reopened);
            AwaitRestoredWorkspace(window, local, "avery@example.com");
            AssertProfileValue(window, "OfficeCountry", "Japan");
            AssertPhotoVisible(window);
            using (var reading = ApplicationWorkflow.CreateFileBacked())
                if (!reading.OpenWorkspace(local, WorkspaceOpenMode.ExistingOnly) || State(reading) != committed)
                    throw new InvalidOperationException("Separate-process reopening lost complete reviewed Import data/history/resources.");
            CaptureVisualEvidence(window, "sqlite-reviewed-import-reopened");
            using var input = File.OpenRead(reopened.MainModule!.FileName);
            var path = Path.Combine(Path.GetDirectoryName(diagnostics)!,
                Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-reviewed-import.json");
            if (File.Exists(path)) throw new InvalidOperationException("Hosted reviewed Import evidence already exists.");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 2, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                executableSha256 = Convert.ToHexString(SHA256.HashData(input)),
                editingProcessId, reopenedProcessId = reopened.Id,
                environment = new { os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                    runtime = Environment.Version.ToString(), window = window.Current.BoundingRectangle.ToString() },
                batchId = "94000000-0000-4000-8000-000000000020", people = 3,
                retainedOwner = originalNote.OwnerEmail, retainedNoteId = originalNote.Id,
                captureId = capture.Id, frozenVersions = capture.TeamNotes.Select(note => new { note.Id, note.Version }).ToArray(),
                photoSha256 = photoHash, reopenedStateSha256 = Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(committed))),
                bulkApply = new { elapsedMs = applyMs,
                    boundary = "Native Apply Invoke through independent coherent reopen plus queued owner-note writing; deliberately held transaction included. No ordinary-edit 1-second bulk budget." },
                heldApplyBrowsing = new { backToPeopleMs = backMs, samplesMs = navigation, timingUse = "diagnostic",
                    boundary = "Native Person Select through exact visible email/heading, while the same Apply activity and actual SQLite writer remain held; no overhead subtracted.",
                    decomposition = new
                    {
                        retainedWritingRequestStartedMs, retainedWritingRequestReturnedMs,
                        samples = navigationTimings.Select(timing => timing.Snapshot()).ToArray(),
                        interpretation = "Monotonic request/query boundaries on the original raw timer. Request includes native provider/dispatcher work; fresh queries can wait for product work and are not pure query overhead. No attribution or subtraction is inferred. Writing boundaries are relative to bulk Apply; sample boundaries are relative to each original Back/Select timer. Fixed counters only; no pre-request observation."
                    },
                    criterion = "Exact usable navigation must complete while the same Apply transaction remains held and unpublished. These two timings are diagnostic, not a p95 estimate or a per-selection deadline; the existing ordinary-navigation workload retains p95<=100ms acceptance." },
                captures = new[] { "sqlite-reviewed-import-preview", "sqlite-reviewed-import-retained-owner",
                    "sqlite-reviewed-import-history", "sqlite-reviewed-import-reopened" },
                scope = "Representative reviewed partial update/resources/history/replay/browse/reopen; rich 10,000-Person combined acceptance remains #100."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: hosted SQLite reviewed Imports preserve exact partial People/resources, frozen history, retained owner writing, original/later no-op replay and separate-process reopening; candidate-bound evidence: " + path);
            Close(reopened);
        }
        finally { StopApplication(reopened); }
    }
}
