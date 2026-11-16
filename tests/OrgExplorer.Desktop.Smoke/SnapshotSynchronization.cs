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
using Microsoft.Data.Sqlite;
using OrgExplorer.TestFixtures;

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
                 {"email":"avery@example.com","displayName":"Avery Snapshot","managerEmail":"morgan@example.com"},
                 {"email":"morgan@example.com","displayName":"Morgan Snapshot","managerEmail":null}]}
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
        var clock = new SnapshotClock();
        using var exportEntered = new ManualResetEventSlim();
        using var releaseExport = new ManualResetEventSlim();
        var holdExport = false;
        var publisher = new AutomaticSnapshotPublicationGate(() => holdExport, exportEntered.Set,
            () => releaseExport.Wait(TimeSpan.FromSeconds(15)));
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [ApplicationWorkflow.CreateFileBacked(filePublisher: publisher), chooseFolder,
                (Action<string>)(_ => { }), clock], null)!;
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
            PumpUntil(() => workflow.People.Count == 2 && Control<Button>("SyncNowButton").IsEnabled);
            if (workflow.Synchronization.WorkspaceId != first.Synchronization.WorkspaceId ||
                !Control<TextBlock>("SynchronizationStatus").Text.Contains("delivery elsewhere is unknown"))
                throw new InvalidOperationException("Compiled Sync now lost Workspace identity or falsely acknowledged provider delivery.");
            Invoke("OpenSettingsButton");
            void Select(string email)
            {
                var node = workflow.ReportingRoots.SelectMany(root => new[] { root }.Concat(root.Reports))
                    .Single(item => item.Person.Email == email);
                Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, node,
                    TreeView.SelectedItemChangedEvent));
            }
            Select("avery@example.com");
            if (Control<TextBlock>("Email").Text != "avery@example.com")
                throw new InvalidOperationException("Joined SQLite data did not become browsable through actual compiled selection.");
            if (!first.EditPreferredName("avery@example.com", "Incoming continuation") || !first.SyncNow())
                throw new InvalidOperationException(first.Status);
            Deliver();
            Invoke("EditProfileButton");
            var displayedBasis = workflow.ProfileEdit!.Basis;
            HeldSnapshotAdoption.Run(target, () =>
                {
                    Thread.Sleep(1200);
                    var editor = Control<TextBox>("JobTitle");
                    return new(() => Invoke("OpenSettingsButton"),
                        () => editor.Text = "Compiled later owner draft",
                        () => { Select("morgan@example.com"); Select("avery@example.com"); },
                        () =>
                        {
                            if (Control<TextBlock>("Email").Text != "avery@example.com")
                                throw new InvalidOperationException("Compiled held-adoption selection lost its actual destination.");
                        });
                },
                () => { Invoke("OpenSettingsButton"); Invoke("SyncNowButton"); },
                () => PumpUntil(() => workflow.Synchronization.Phase == WorkspaceSynchronizationPhase.Adopting &&
                    File.Exists(Path.Combine(target, "workspace.sqlite-journal"))),
                () =>
                {
                    if (workflow.Synchronization.Phase != WorkspaceSynchronizationPhase.Adopting || workflow.CanSafelyClose ||
                        Control<TextBox>("JobTitle").Text != "Compiled later owner draft" || !Control<TextBox>("JobTitle").IsEnabled)
                        throw new InvalidOperationException("Compiled capture did not retain the same pending adoption and owner draft.");
                },
                observation => Console.WriteLine("DIAGNOSTIC: compiled-held-snapshot " + JsonSerializer.Serialize(observation)));
            PumpUntil(() => workflow.GetRelationships("avery@example.com")!.Person.PreferredName == "Incoming continuation" &&
                Control<Button>("SyncNowButton").IsEnabled);
            if (Control<TextBox>("JobTitle").Text != "Compiled later owner draft" ||
                !ReferenceEquals(displayedBasis, workflow.ProfileEdit?.Basis))
                throw new InvalidOperationException("Compiled adoption cleared or silently rebased post-seal owner writing.");
            Invoke("CancelProfileButton");
            if (Control<TextBlock>("ProfileHeading").Text != "Incoming continuation" ||
                Control<TextBlock>("Email").Text != "avery@example.com" ||
                workflow.PeopleNavigation.SelectedEmail != "avery@example.com")
                throw new InvalidOperationException("Compiled incoming adoption lost the selected Person or left stale reading.");
            if (!workflow.EditFavorite("avery@example.com", true)) throw new InvalidOperationException(workflow.Status);
            Invoke("OpenSettingsButton");
            Invoke("ReviewWorkspaceVersionsButton");
            PumpUntil(() => Control<ListBox>("WorkspaceVersionChoices").Items.Count != 0 &&
                Control<Button>("ReviewWorkspaceVersionsButton").IsEnabled);
            var choices = Control<ListBox>("WorkspaceVersionChoices");
            choices.SelectedItem = choices.Items.Cast<WorkspaceVersion>().Single(item =>
                item.Revision == workflow.Synchronization.LocalRevision);
            if (!Control<Button>("ChooseWorkspaceVersionButton").IsEnabled)
                throw new InvalidOperationException("The real current review was not selectable before automatic synchronization.");
            Invoke("OpenSettingsButton");
            holdExport = true;
            clock.Advance(TimeSpan.FromSeconds(60));
            PumpUntil(() => exportEntered.IsSet && workflow.IsSynchronizing &&
                Control<TextBlock>("SynchronizationStatus").Text.Contains("Publishing a complete local version."));
            if (new[] { "SyncNowButton", "ReviewWorkspaceVersionsButton", "ChooseWorkspaceVersionButton",
                "DeleteRetainedWorkspaceVersionButton" }.Any(name => Control<Button>(name).IsEnabled))
                throw new InvalidOperationException("Automatic publication did not own the compiled manual/whole-version lifecycle.");
            var surface = Control<UserControl>("PersonNotes");
            T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
            Note<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var automaticNote = workflow.PersonNotesView.SelectedNote!.Id;
            Note<TextBox>("NoteTitle").Text = "Compiled automatic export";
            Note<TextBox>("MarkdownEditor").Text = "Exact writing during automatic export";
            Select("morgan@example.com");
            Select("avery@example.com");
            PumpUntil(() => workflow.GetRetainedNoteDraft(NoteScope.Person, automaticNote) is null);
            using (var reading = ApplicationWorkflow.CreateFileBacked())
            {
                if (!reading.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly) ||
                    reading.BeginNoteDraft(NoteScope.Person, "avery@example.com", automaticNote)?.Markdown !=
                        "Exact writing during automatic export" || !workflow.IsSynchronizing || releaseExport.IsSet ||
                    Note<TextBox>("MarkdownEditor").Text != "Exact writing during automatic export" ||
                    !Note<TextBox>("MarkdownEditor").IsEnabled)
                    throw new InvalidOperationException("Compiled ordinary writing/navigation waited for automatic export or lost its owner.");
            }
            holdExport = false;
            releaseExport.Set();
            PumpUntil(() => !workflow.IsSynchronizing);
            if (!workflow.Synchronization.HasUnpublishedChanges)
                throw new InvalidOperationException("An older automatic export acknowledged newer compiled writing.");
            clock.Advance(TimeSpan.FromSeconds(60));
            PumpUntil(() => !workflow.IsSynchronizing && !workflow.Synchronization.HasUnpublishedChanges);
            foreach (var file in Directory.GetFiles(incoming, "*.oesnapshot"))
                File.Copy(file, Path.Combine(outgoing, Path.GetFileName(file)), true);
            if (!first.SyncNow() || !first.EditPreferredName("avery@example.com", "Automatically refreshed Person") || !first.SyncNow())
                throw new InvalidOperationException(first.Synchronization.Summary);
            Deliver();
            clock.Advance(TimeSpan.FromSeconds(60));
            PumpUntil(() => Control<TextBlock>("ProfileHeading").Text == "Automatically refreshed Person" &&
                !workflow.IsSynchronizing);
            if (workflow.PeopleNavigation.SelectedEmail != "avery@example.com" ||
                Note<TextBox>("MarkdownEditor").Text != "Exact writing during automatic export" ||
                !Control<TextBlock>("SynchronizationStatus").Text.Contains("delivery elsewhere is unknown"))
                throw new InvalidOperationException("Compiled automatic incoming refresh lost selection/writing or invented provider delivery.");
            VerifyCompiledWorkspaceVersions(window, workflow, first, Deliver, Select);
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Snapshot compiled verification must not show or create a native window.");
            Console.WriteLine("PASS: compiled SQLite Settings folder/Join/Sync now and controlled automatic cadence validate real artifacts, preserve identity/selected Person and post-seal owner drafts, allow exact durable writing/navigation during real export, retain newer unpublished revisions and refresh incoming reading; provider delivery remains unknown. No visible window/native handle.");
        }
        finally
        {
            holdExport = false;
            releaseExport.Set();
            if (workflow is not null) PumpUntil(() => !workflow.IsSynchronizing);
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
        BeginProfileEditing(window);
        DateTimeOffset? heldAdmissionAt = null, heldActionsCompletedAt = null;
        AutomationElement? syncStatus = null, syncButton = null;
        var syncState = new CacheRequest { TreeScope = TreeScope.Element };
        syncState.Add(AutomationElement.NameProperty);
        syncState.Add(AutomationElement.IsEnabledProperty);
        var personState = new CacheRequest { TreeScope = TreeScope.Element };
        personState.Add(AutomationElement.NameProperty);
        personState.Add(AutomationElement.IsOffscreenProperty);
        personState.Add(AutomationElement.BoundingRectangleProperty);
        var inputState = new CacheRequest { TreeScope = TreeScope.Element };
        inputState.Add(AutomationElement.IsEnabledProperty);
        inputState.Add(AutomationElement.IsOffscreenProperty);
        inputState.Add(AutomationElement.BoundingRectangleProperty);
        inputState.Add(ValuePattern.Pattern);
        inputState.Add(ValuePattern.IsReadOnlyProperty);
        inputState.Add(ValuePattern.ValueProperty);
        SnapshotVisiblePerson? lastPerson = null, beforeInputPerson = null, afterInputPerson = null, destinationPerson = null;
        SnapshotWritableEditor? lastEditor = null, beforeInputEditor = null, afterInputEditor = null;
        object NativeUsability() => new
        { beforeInputPerson, beforeInputEditor, afterInputPerson, afterInputEditor, destinationPerson, lastPerson, lastEditor };
        var heldObservation = HeldSnapshotAdoption.Run(target,
            () =>
            {
                var settingsAction = (InvokePattern)Find(window, "OpenSettings").GetCurrentPattern(InvokePattern.Pattern);
                var editor = Find(window, "JobTitle");
                var writing = (ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern);
                var tree = Find(window, "People");
                var manager = tree.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "morgan@example.com"))
                    ?? throw new InvalidOperationException("Snapshot preparation could not find the real Manager.");
                var selection = (SelectionItemPattern)manager.GetCurrentPattern(SelectionItemPattern.Pattern);
                var email = Find(window, "Email");
                var heading = Find(window, "ProfileHeading");
                SnapshotVisibleText Reading(AutomationElement element)
                {
                    var observed = element.GetUpdatedCache(personState).Cached;
                    return new(observed.Name, observed.IsOffscreen, observed.BoundingRectangle.Width, observed.BoundingRectangle.Height);
                }
                SnapshotVisiblePerson Person() => lastPerson = new(Reading(email), Reading(heading));
                SnapshotWritableEditor Editor()
                {
                    var observed = editor.GetUpdatedCache(inputState);
                    var bounds = observed.Cached.BoundingRectangle;
                    var supportsValue = observed.TryGetCachedPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern;
                    return lastEditor = new(observed.Cached.IsEnabled, observed.Cached.IsOffscreen,
                        bounds.Width, bounds.Height, supportsValue,
                        !Equals(observed.GetCachedPropertyValue(ValuePattern.IsReadOnlyProperty, true), false),
                        observed.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string);
                }
                return new(
                    () =>
                    {
                        using (HarnessRuntime.Measure(HarnessRuntime.Work.NativeInvoke)) settingsAction.Invoke();
                        Await(() =>
                        {
                            var person = Person();
                            var input = Editor();
                            if (!SnapshotNativeUsability.EditorReady(person, input, "avery@example.com", "Avery reference")) return false;
                            beforeInputPerson = person;
                            beforeInputEditor = input;
                            return true;
                        });
                    },
                    () =>
                    {
                        using (HarnessRuntime.Measure(HarnessRuntime.Work.NativeSetValue))
                            writing.SetValue("Private draft admitted after snapshot seal");
                        Await(() =>
                        {
                            var person = Person();
                            var input = Editor();
                            if (!SnapshotNativeUsability.DraftObserved(person, input, "avery@example.com", "Avery reference",
                                "Private draft admitted after snapshot seal")) return false;
                            afterInputPerson = person;
                            afterInputEditor = input;
                            return true;
                        });
                    },
                    () => { using var action = HarnessRuntime.Measure(HarnessRuntime.Work.NativeInvoke); selection.Select(); },
                    () => Await(() =>
                    {
                        var person = Person();
                        if (!SnapshotNativeUsability.PersonReady(person, "morgan@example.com", "Morgan Example")) return false;
                        destinationPerson = person;
                        return true;
                    }));
            },
            () =>
            {
                SetSettingsVisible(window, true);
                PrepareAction("SyncNow");
                syncButton = Find(window, "SyncNow");
                var synchronization = ControlReadiness.AwaitPattern(() => syncButton,
                    control => control.Current.IsEnabled && !control.Current.IsOffscreen,
                    control => (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern), Await);
                syncStatus = Find(window, "SynchronizationStatus");
                using var action = HarnessRuntime.Measure(HarnessRuntime.Work.NativeInvoke);
                synchronization.Invoke();
            },
            () =>
            {
                Await(() => syncStatus!.GetUpdatedCache(syncState).Cached.Name.Contains("Installing a validated incoming version.") &&
                    !syncButton!.GetUpdatedCache(syncState).Cached.IsEnabled && File.Exists(Path.Combine(target, "workspace.sqlite-journal")));
                heldAdmissionAt = DateTimeOffset.UtcNow;
            },
            () =>
            {
                CaptureVisualEvidence(window, "sqlite-sync-held-adoption-draft");
                heldActionsCompletedAt = DateTimeOffset.UtcNow;
            },
            observation => WriteHeldSnapshotObservation(window, observation, NativeUsability()));
        SetSettingsVisible(window, true);
        Await(() => Find(window, "SyncNow").Current.IsEnabled &&
            Find(window, "SynchronizationStatus").Current.Name.Contains("Locally saved and published.") &&
            !Find(window, "SynchronizationStatus").Current.Name.Contains("Synchronization failed"));
        SetSettingsVisible(window, false);
        SelectTreePerson(window, "avery@example.com", expectNavigation: false);
        Await(() => CurrentPersonEmail(window) == "avery@example.com");
        AssertValue(window, "JobTitle", "Private draft admitted after snapshot seal");
        CancelProfileEditing(window);
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
            heldAdoption = new
            {
                heldAdmissionAt, heldActionsCompletedAt,
                observationFile = Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-held-adoption-observation.json",
                milestones = heldObservation.Milestones,
                timestampFrequency = heldObservation.TimestampFrequency,
                inputRequests = heldObservation.Milestones.Count(value => value.Stage == "enter-draft-started"),
                selectionRequests = heldObservation.Milestones.Count(value => value.Stage == "select-person-started"),
                nativeUsability = NativeUsability(),
                boundary = "Normal Sync now reports sealed validated adoption with a live SQLite journal; native draft input and actual Person selection complete and are captured while the same real reader still prevents commit. Only then is the reader deliberately released.",
                ownerEmail = "avery@example.com", selectedWhileHeld = "morgan@example.com",
                retainedJobTitle = "Private draft admitted after snapshot seal",
                discardedExplicitlyAfterSuccess = true
            },
            artifacts = Directory.GetFiles(incoming, "*.oesnapshot").Select(path => new
            { name = Path.GetFileName(path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray(),
            providerDelivery = "Unknown; deterministic copying of actual complete artifacts substitutes only external provider delivery.",
            captures = new[] { "sqlite-sync-folder-published", "sqlite-sync-joined-person-history",
                "sqlite-sync-held-adoption-draft", "sqlite-sync-incoming-continuation" },
            scope = "Normal native Settings folder, Sync now, fresh join and automatic clean continuation; real independent logical stores/resources/history. Not periodic scheduling, conflict resolution, retention, exit or 10,000-Person export evidence."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: hosted SQLite snapshots: native Settings folder/Sync now/status, independent-store fresh join and selected-Person incoming adoption preserve actual photos/private Team names/frozen history; candidate-bound evidence: " + evidence);
        RunWorkspaceConflictJourney(window, source, target, outgoing, incoming, expected);
        SetSettingsVisible(window, true);
        PrepareAction("OpenWorkspace");
        PickWorkspaceFolder(window, source);
        Await(() => Activity(window) == "Ready" &&
            new WorkspaceSettings(Path.Combine(root, "sqlite-app-settings", "workspace-settings.json")).LastWorkspacePath == source);
        EnsurePeopleBrowsing(window);
        SelectTreePerson(window, "morgan@example.com");
        Await(() => CurrentPersonEmail(window) == "morgan@example.com");
    }

    private static void WriteHeldSnapshotObservation(AutomationElement window, SnapshotAdoptionObservation observation, object nativeUsability)
    {
        using var process = Process.GetProcessById(window.Current.ProcessId);
        using var executable = File.OpenRead(process.MainModule!.FileName);
        var state = new CacheRequest { TreeScope = TreeScope.Element };
        state.Add(AutomationElement.ItemStatusProperty);
        state.Add(AutomationElement.HelpTextProperty);
        var current = window.GetUpdatedCache(state);
        var path = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-held-adoption-observation.json");
        if (File.Exists(path)) throw new InvalidOperationException("Held-adoption observations must not overwrite earlier evidence.");
        File.WriteAllText(path, SnapshotNativeUsability.SerializeDiagnostic(new
        {
            schemaVersion = 1,
            revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            executableSha256 = Convert.ToHexString(SHA256.HashData(executable)),
            processId = window.Current.ProcessId,
            observedAt = DateTimeOffset.UtcNow,
            observation,
            nativeUsability,
            inputRequests = observation.Milestones.Count(value => value.Stage == "enter-draft-started"),
            selectionRequests = observation.Milestones.Count(value => value.Stage == "select-person-started"),
            outcomeObservedBeforeReaderDisposal = new
            {
                itemStatus = current.Cached.ItemStatus,
                helpText = current.Cached.HelpText
            },
            boundary = "Harness monotonic actual preparation/request/observation milestones and real reader/journal state. No database commit timestamp or latency subtraction. UI outcome is observed before disposing the reader; a passing proof has already deliberately released it. Feedback remains non-atomic."
        }));
        Console.WriteLine("DIAGNOSTIC: sqlite-held-adoption outcome=" + observation.Outcome + " evidence=" + path);
    }
}
