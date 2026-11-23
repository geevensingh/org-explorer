using OrgExplorer.Workflow;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Core.Tests;

public sealed class PersonNoteTests(ITestOutputHelper output)
{
    public class NoteSaveBoundary : DispatchProxy
    {
        internal IApplicationWorkflow Inner = null!;
        internal Action? BeforeSave;
        private readonly object observationGate = new();
        private readonly Queue<object> observations = new();
        private bool observing;
        private int droppedObservations;
        private Exception? observationFailure;
        internal void StartObserving() => Volatile.Write(ref observing, true);
        internal void StopObserving() => Volatile.Write(ref observing, false);

        private void Observe(string method, string phase, object? value = null)
        {
            if (!Volatile.Read(ref observing)) return;
            try
            {
                object? detail = value switch
                {
                    LocalSaveStatus status => new { phase = status.Phase.ToString(), outcome = status.Outcome.ToString(),
                        status.OutstandingRequests, hasError = status.Error is not null },
                    Exception error => new { type = error.GetType().FullName, hResult = error.HResult },
                    _ => null
                };
                var observation = new { method, phase, detail, ticks = Stopwatch.GetTimestamp(),
                    threadId = Environment.CurrentManagedThreadId };
                lock (observationGate)
                {
                    if (observations.Count == 32) { observations.Dequeue(); droppedObservations++; }
                    observations.Enqueue(observation);
                }
            }
            catch (Exception error) { Volatile.Write(ref observationFailure, error); }
        }

        internal object Snapshot()
        {
            lock (observationGate)
                return new
                {
                    limit = 32, droppedObservations, calls = observations.ToArray(),
                    observationFailure = Volatile.Read(ref observationFailure)?.GetType().FullName,
                    limitation = "Non-atomic public proxy calls, not private autosave-task state or scheduler evidence. " +
                        "No extra workflow getters are invoked; missing callbacks do not establish a cause."
                };
        }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            Observe(method!.Name, "enter");
            if (method!.Name == nameof(IApplicationWorkflow.SaveNoteDraft)) BeforeSave?.Invoke();
            try
            {
                var result = method.Invoke(Inner, arguments);
                Observe(method.Name, "return", result);
                return result;
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                Observe(method.Name, "throw", error.InnerException);
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    [Fact]
    public void Admission_observations_are_bounded_and_do_not_capture_note_arguments_or_returned_writing()
    {
        var proxy = DispatchProxy.Create<IApplicationWorkflow, NoteSaveBoundary>();
        var boundary = (NoteSaveBoundary)(object)proxy;
        using var inner = ApplicationWorkflow.CreateInMemory();
        boundary.Inner = inner;
        Assert.True(proxy.OpenWorkspace("fictitious"));
        Assert.True(proxy.ApplyImport(proxy.PreviewImport(WorkflowTests.Batch).Id));
        boundary.StartObserving();
        for (var i = 0; i < 40; i++) _ = proxy.LocalSave;
        Assert.NotNull(proxy.CreatePersonNote("avery@example.com", "Do not capture this title", "Do not capture this writing"));
        var primary = new TimeoutException("Controlled diagnostic formatting contract, not an admission reproduction.");
        string? recorded = null;
        ReportAdmissionRefusal(primary, () => JsonSerializer.Serialize(boundary.Snapshot()), text => recorded = text);
        var json = Assert.IsType<string>(primary.Data["NOTE-SAVE-CORE-ENTRY-REFUSAL"]);
        Assert.Equal("NOTE-SAVE-CORE-ENTRY-REFUSAL " + json, recorded);
        Assert.DoesNotContain("Do not capture", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(32, document.RootElement.GetProperty("calls").GetArrayLength());
        Assert.True(document.RootElement.GetProperty("droppedObservations").GetInt32() > 0);
        Assert.Contains(document.RootElement.GetProperty("calls").EnumerateArray(),
            call => call.GetProperty("method").GetString() == nameof(IApplicationWorkflow.CreatePersonNote) &&
                call.GetProperty("phase").GetString() == "return");
    }

    private static object WorkerSnapshot()
    {
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
        ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumIo);
        return new
        {
            availableWorkers, availableIo, minimumWorkers, minimumIo, maximumWorkers, maximumIo,
            threads = ThreadPool.ThreadCount, pendingWorkItems = ThreadPool.PendingWorkItemCount
        };
    }

    private static void ReportAdmissionRefusal(TimeoutException failure, Func<string> snapshot, Action<string> write)
    {
        const string marker = "NOTE-SAVE-CORE-ENTRY-REFUSAL";
        try
        {
            var json = snapshot();
            failure.Data[marker] = json;
            write(marker + " " + json);
        }
        catch (Exception diagnosticFailure)
        {
            failure.Data[marker + "-REPORTING-FAULT"] = diagnosticFailure;
            try { Console.Error.WriteLine($"{marker}-REPORTING-FAULT {diagnosticFailure.GetType().FullName}"); }
            catch (Exception fallbackFailure) { failure.Data[marker + "-FALLBACK-FAULT"] = fallbackFailure; }
        }
    }

    [Fact]
    public void Admission_reporting_fault_retains_the_original_timeout_and_its_captured_evidence()
    {
        var primary = new TimeoutException("Controlled reporting-fault contract, not an admission reproduction.");
        var reportingFailure = new IOException("Controlled diagnostic writer refusal.");
        const string snapshot = """{"scope":"controlled-reporting-contract"}""";
        ReportAdmissionRefusal(primary, () => snapshot, _ => throw reportingFailure);
        Assert.Equal(snapshot, primary.Data["NOTE-SAVE-CORE-ENTRY-REFUSAL"]);
        Assert.Same(reportingFailure, primary.Data["NOTE-SAVE-CORE-ENTRY-REFUSAL-REPORTING-FAULT"]);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task In_flight_discard_preserves_exact_writing_and_idle_discard_clears_only_its_scope(NoteScope scope)
    {
        var proxy = DispatchProxy.Create<IApplicationWorkflow, NoteSaveBoundary>();
        var boundary = (NoteSaveBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateInMemory();
        using var app = new WorkspaceViewModel(proxy);
        Assert.True(app.OpenWorkspace("fictitious"), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id), app.Status);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        boundary.BeforeSave = () => { admitted.TrySetResult(); release.Wait(); };
        var requestedAt = Stopwatch.GetTimestamp();
        var requestThreadId = Environment.CurrentManagedThreadId;
        var requestContext = SynchronizationContext.Current?.GetType().FullName;
        var requestWorkers = WorkerSnapshot();
        boundary.StartObserving();
        var basis = app.CreateOwnedNoteDraft(scope, "avery@example.com")!;
        var createdAt = Stopwatch.GetTimestamp();
        app.UpdateNoteDraft(scope, basis.Id, "Exact owner", "Writing admitted for saving");
        var updatedAt = Stopwatch.GetTimestamp();
        try
        {
            try { await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException failure)
            {
                ReportAdmissionRefusal(failure, () => JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, scope = scope.ToString(), noteId = basis.Id, owner = basis.OwnerEmail,
                    entryDeadlineMs = 5000, observedAt = DateTimeOffset.UtcNow,
                    frequency = Stopwatch.Frequency, requestedAt, createdAt, updatedAt,
                    observedTicks = Stopwatch.GetTimestamp(), requestThreadId, requestContext, requestWorkers,
                    observationThreadId = Environment.CurrentManagedThreadId,
                    observationContext = SynchronizationContext.Current?.GetType().FullName,
                    workers = WorkerSnapshot(),
                    admissionSignal = new { status = admitted.Task.Status.ToString(),
                        completedSuccessfully = admitted.Task.IsCompletedSuccessfully },
                    proxy = boundary.Snapshot()
                }), output.WriteLine);
                throw;
            }
            app.DiscardUnsavedChanges(basis.Id);
            var retained = Assert.IsType<RetainedNoteDraft>(app.GetRetainedNoteDraft(scope, basis.Id));
            Assert.True(retained.IsSaving);
            Assert.Equal(basis.Id, retained.Basis.Id);
            Assert.Equal(basis.OwnerEmail, retained.Basis.OwnerEmail);
            Assert.Equal("Writing admitted for saving", retained.Markdown);
            Assert.False(app.CanSafelyClose);
            Assert.Contains("admitted", app.Status);
        }
        finally { boundary.StopObserving(); boundary.BeforeSave = null; release.Set(); }
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        var owner = app.GetRelationships(basis.OwnerEmail)!.Person;
        app.RetainPersonTextDraft(owner, PersonTextField.PreferredName, "Independent unsubmitted name");
        app.UpdateNoteDraft(scope, basis.Id, " ", "Rejected draft retained until deliberate discard");
        Assert.False(await app.FlushNoteDraftsAsync());
        app.DiscardUnsavedChanges(basis.Id);
        Assert.Null(app.GetRetainedNoteDraft(scope, basis.Id));
        Assert.Equal("Writing admitted for saving", app.BeginNoteDraft(scope, basis.OwnerEmail, basis.Id)!.Markdown);
        Assert.Equal("Independent unsubmitted name", app.GetPersonTextDraft(owner.Email, PersonTextField.PreferredName)!.Value);
        Assert.False(app.CanSafelyClose);
        app.DiscardPersonTextDraft(owner, PersonTextField.PreferredName);
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Empty_writing_invitation_distinguishes_absence_from_empty_documents_and_archived_choices(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        Assert.True(app.OpenOwnedNotes(scope, "avery@example.com"));
        Assert.True(app.GetOwnedNotesView(scope).IsEmpty);

        var id = scope == NoteScope.Person
            ? app.CreatePersonNote("avery@example.com", "Exact empty document", "")!.Id
            : app.CreateTeamNote("avery@example.com", "Exact empty document", "")!.Id;
        Assert.True(app.OpenOwnedNote(scope, id));
        Assert.False(app.GetOwnedNotesView(scope).IsEmpty);
        Assert.Equal(id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.Equal("", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.False(app.GetOwnedNotesView(scope).IsEditing);
        Assert.True(app.DesignateOngoingNote(scope, id));
        Assert.True(scope == NoteScope.Person ? app.ArchivePersonNote(id) : app.ArchiveTeamNote(id));
        Assert.False(app.OpenOngoingNote(scope));
        Assert.False(app.GetOwnedNotesView(scope).IsEmpty);
        Assert.Null(app.GetOwnedNotesView(scope).SelectedNote);
        Assert.Equal(id, app.GetOwnedNotesView(scope).OngoingNoteId);
        Assert.Contains("unavailable or archived", app.GetOwnedNotesView(scope).Feedback);
        Assert.Equal(id, Assert.Single(app.GetOwnedNotesView(scope).Notes).Id);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Rejected_new_writing_is_not_presented_as_an_empty_invitation(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        Assert.True(app.OpenOwnedNotes(scope, "avery@example.com"));
        var draft = app.BeginNoteDraft(scope, "avery@example.com")!;
        app.RetainNoteDraft(draft);
        Assert.Null(app.SaveNoteDraft(draft, " ", "Rejected writing"));

        Assert.False(app.GetOwnedNotesView(scope).IsEmpty);
        Assert.Equal(app.LocalSaveError, app.GetOwnedNotesView(scope).Feedback);
        Assert.Empty(app.GetOwnedNotesView(scope).Notes);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        Assert.True(app.GetOwnedNotesView(scope).IsEmpty);
    }

    [Fact]
    public void Independent_profile_draft_does_not_prevent_reading_or_creating_notes_for_the_same_owner()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var first = app.CreatePersonNote("avery@example.com", "First", "Saved")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
        Assert.True(app.BeginProfileEdit(first.OwnerEmail));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained profile draft"));
        var profile = app.ProfileEdit;
        var created = app.CreatePersonNote(first.OwnerEmail, "Independent new writing", "")!;
        Assert.True(app.OpenOwnedNote(NoteScope.Person, created.Id, editing: true));
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, first.Id));
        Assert.Same(profile, app.ProfileEdit);
        Assert.False(app.CanSafelyClose);
    }

    [Fact]
    public void A_second_existing_note_does_not_turn_the_first_listed_note_into_an_implicit_designation()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var first = app.CreatePersonNote("avery@example.com", "First", "Saved")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
        Assert.Equal(first.Id, app.PersonNotesView.SelectedNote!.Id);
        app.CreatePersonNote(first.OwnerEmail, "Another existing note", "");
        Assert.Null(app.PersonNotesView.OngoingNoteId);
        Assert.Null(app.PersonNotesView.SelectedNote);
        Assert.False(app.PersonNotesView.IsEmpty);
        Assert.Contains("Choose", app.PersonNotesView.Feedback);
    }

    [Fact]
    public void Empty_invitation_new_writing_and_secondary_access_are_observable_without_a_permanent_picker()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var notifications = new List<string?>();
        app.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
        Assert.Contains("Write a new note", app.PersonNotesView.Feedback);
        Assert.Empty(app.PersonNotesView.Notes);
        Assert.False(app.PersonNotesView.OtherNotesOpen);
        var created = app.CreatePersonNote("avery@example.com", "New Person note", "")!;
        Assert.True(app.OpenOwnedNote(NoteScope.Person, created.Id, editing: true));
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.False(app.PersonNotesView.IsEmpty);
        app.ShowOtherNotes(NoteScope.Person, true);
        Assert.True(app.PersonNotesView.OtherNotesOpen);
        Assert.Equal(created.Id, Assert.Single(app.PersonNotesView.Notes).Id);
        app.ShowOtherNotes(NoteScope.Person, false);
        Assert.True(app.OpenOngoingNote(NoteScope.Person));
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.False(app.PersonNotesView.OtherNotesOpen);
        Assert.Contains(nameof(app.PersonNotesView), notifications);
    }

    [Fact]
    public async Task Retained_note_writing_survives_secondary_ongoing_and_owner_navigation_without_implicit_save()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var first = app.CreatePersonNote("avery@example.com", "Continuing", "Saved")!;
        var other = app.CreatePersonNote("avery@example.com", "Other", "")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, first.Id));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, first.Id, editing: true));
        app.UpdateNoteDraft(NoteScope.Person, first.Id, first.Title, "Retained writing");

        Assert.True(app.OpenOwnedNote(NoteScope.Person, other.Id));
        Assert.Equal(other.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, null));
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, other.Id));
        Assert.True(app.OpenOngoingNote(NoteScope.Person));
        Assert.Equal(other.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, first.Id, editing: true));
        Assert.Equal(first.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal(first.OwnerEmail, app.PersonNotesView.OwnerEmail);
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.Equal("Retained writing", app.PersonNotesView.SelectedNote.Markdown);
        Assert.Equal("Saved", app.GetPersonNotes(first.OwnerEmail).Single(note => note.Id == first.Id).Markdown);
        Assert.False(app.CanSafelyClose);
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        Assert.Equal("Retained writing", app.GetPersonNotes(first.OwnerEmail).Single(note => note.Id == first.Id).Markdown);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, other.Id));
    }

    [Fact]
    public void Ambiguous_ongoing_choice_is_explicit_and_other_notes_and_search_open_the_exact_document()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        Assert.True(app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id));
        var first = app.CreatePersonNote("avery@example.com", "Continuing context", "Private writing")!;
        var other = app.CreatePersonNote("avery@example.com", "Separately titled empty note", "")!;
        var before = app.GetPersonNotes("avery@example.com").ToArray();

        Assert.True(app.OpenOwnedNotes(NoteScope.Person, "AVERY@example.com"));
        Assert.Null(app.PersonNotesView.SelectedNote);
        Assert.Contains("Choose", app.PersonNotesView.Feedback);
        Assert.Equal(2, app.PersonNotesView.Notes.Count);
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, first.Id));
        Assert.Equal(first.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.False(app.PersonNotesView.OtherNotesOpen);

        Assert.True(app.OpenOwnedNote(NoteScope.Person, other.Id));
        Assert.Equal(other.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal("", app.PersonNotesView.SelectedNote.Markdown);
        Assert.False(app.PersonNotesView.IsOngoing);
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
        Assert.Equal(other.Id, app.PersonNotesView.SelectedNote.Id);
        Assert.True(app.OpenOngoingNote(NoteScope.Person));
        Assert.Equal(first.Id, app.PersonNotesView.SelectedNote!.Id);

        var match = Assert.Single(app.Search("Separately titled"), r => r.Source == SearchSource.PersonNote);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, match.NoteId!.Value));
        Assert.Equal(other.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, other.Id));
        Assert.Equal(other.Id, app.PersonNotesView.OngoingNoteId);
        Assert.Equal(before, app.GetPersonNotes("avery@example.com"));
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("personNotes")]
    [InlineData("teamNotes")]
    [InlineData("noteOperations")]
    [InlineData("createPersonNote")]
    [InlineData("archiveTeamNote")]
    public void Import_note_payloads_and_operations_are_rejected_without_changing_private_Person_notes(string property)
    {
        IApplicationWorkflow app = ApplicationWorkflow.CreateInMemory();
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id);
        var note = app.CreatePersonNote("avery@example.com", "Private context", "Workspace-owned writing")!;
        var invalid = WorkflowTests.Batch.Replace("000000000001", "000000000002")
            .Replace("\"managerEmail\":null", $"\"{property}\":\"Rejected\", \"managerEmail\":null");
        var preview = app.PreviewImport(invalid);
        Assert.False(preview.CanApply);
        Assert.Contains(property, string.Join(" ", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Equal(note, Assert.Single(app.GetPersonNotes("avery@example.com")));
    }

    [Fact]
    public void Edit_and_archive_preserve_identity_ownership_and_writing_without_inheritance()
    {
        IApplicationWorkflow app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        app.OpenWorkspace("fictitious");
        app.ApplyImport(app.PreviewImport(WorkflowTests.Batch).Id);
        var original = app.CreatePersonNote("avery@example.com", "Draft", "Before")!;
        Assert.True(app.EditPersonNote(original.Id, "Context", "After **editing**"), app.Status);
        Assert.True(app.ArchivePersonNote(original.Id), app.Status);
        Assert.Empty(app.GetPersonNotes("avery@example.com"));
        var archived = Assert.Single(app.GetPersonNotes("avery@example.com", includeArchived: true));
        Assert.True(archived.Archived);
        Assert.Equal(original.Id, archived.Id);
        Assert.Equal(original.OwnerEmail, archived.OwnerEmail);
        Assert.Equal(original.CreatedAt, archived.CreatedAt);
        Assert.Equal("After **editing**", archived.Markdown);
        Assert.True(app.ArchivePersonNote(original.Id, archived: false));
        Assert.Equal("Context", Assert.Single(app.GetPersonNotes("avery@example.com")).Title);
        Assert.False(app.EditPersonNote(original.Id, " ", "Unsaved"));
        Assert.Contains("nonblank", app.Status);
        Assert.Equal("After **editing**", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Multiple_private_Person_notes_have_stable_ownership_and_do_not_inherit_to_reports()
    {
        IApplicationWorkflow app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious"));
        var batch = WorkflowTests.Batch.Replace("\"managerEmail\":null}",
            "\"managerEmail\":null},{\"email\":\"report@example.com\",\"displayName\":\"Report Example\",\"managerEmail\":\"avery@example.com\"}");
        Assert.True(app.ApplyImport(app.PreviewImport(batch).Id));
        var first = app.CreatePersonNote("AVERY@example.com", "Meeting", "# Context\nPrivate writing");
        var second = app.CreatePersonNote("avery@example.com", "Follow-up", "Another document");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("avery@example.com", first.OwnerEmail);
        Assert.Equal(2, app.GetPersonNotes("AVERY@example.com").Count);
        Assert.Empty(app.GetPersonNotes("report@example.com"));
    }
}
