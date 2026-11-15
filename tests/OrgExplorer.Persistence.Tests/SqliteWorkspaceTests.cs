using OrgExplorer.Workflow;
using Microsoft.Data.Sqlite;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class SqliteWorkspaceTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "sqlite-" + Guid.NewGuid().ToString("N")));
    private string Workspace => Path.Combine(root, "local");
    private const string Initial = """
        {"schemaVersion":1,"batchId":"91000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","producer":"SQLite acceptance","people":[
         {"email":"report@example.com","displayName":"Report","preferredName":"Reference",
          "alias":"report","jobTitle":"Engineer","department":"Research",
          "officeBuilding":"Building A","officeNumber":"007B","officeState":"Washington",
          "officeCountry":"United States","linkedInProfileUrl":"https://www.linkedin.com/in/report",
          "managerEmail":"manager@example.com","archived":false,"isFavorite":true},
         {"email":"manager@example.com","displayName":"Manager","managerEmail":null}]}
        """;

    [Fact]
    public void Explicit_local_creation_can_reopen_the_committed_empty_Workspace()
    {
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
            Assert.True(app.IsWorkspaceCurrent);
            Assert.Empty(app.People);
            Assert.Contains("SQLite", app.Compatibility.Guidance);
            Assert.Contains("locally", app.Status);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Empty(reopened.People);
        Assert.Contains("SQLite", reopened.Compatibility.Guidance);
    }

    [Fact]
    public void Reviewed_initial_People_Import_and_evidence_survive_startup_reopening()
    {
        var preferences = Path.Combine(root, "settings", "workspace-settings.json");
        var expected = new PersonProfile("report@example.com", "Report", "Reference", "manager@example.com")
        {
            Alias = "report", JobTitle = "Engineer", Department = "Research", OfficeBuilding = "Building A",
            OfficeNumber = "007B", OfficeState = "Washington", OfficeCountry = "United States",
            LinkedInProfileUrl = "https://www.linkedin.com/in/report", IsFavorite = true
        };
        Directory.CreateDirectory(root);
        var import = Path.Combine(root, "initial.json");
        File.WriteAllText(import, Initial);
        var settings = new WorkspaceSettings(preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(app, Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
            Assert.True(app.TryOpenImport(), app.Status);
            var preview = app.PreviewImportFile(import);
            Assert.True(preview.CanApply, app.Status);
            Assert.Empty(app.People);
            Assert.True(app.ApplyImport(preview.Id), app.Status);
            Assert.Equal("Import applied and saved locally.", app.Status);
            app.ShowPeople();
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            Assert.Equal(expected, app.GetRelationships("report@example.com")!.Person);
        }
        var restoredSettings = new WorkspaceSettings(preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), restoredSettings);
        Assert.True(restoredSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal(expected, reopened.GetRelationships("report@example.com")!.Person);
        var manager = Assert.Single(reopened.ReportingRoots);
        Assert.Equal("manager@example.com", manager.Person.Email);
        Assert.Equal(expected, Assert.Single(manager.Reports).Person);
        Assert.Equal(expected, Assert.Single(reopened.FavoritePeople));
        var timeline = reopened.GetProfileTimeline(expected.Email)!;
        Assert.Empty(timeline.Changes);
        Assert.Equal(13, timeline.Observations.Count);
        Assert.All(timeline.Observations, evidence =>
        {
            Assert.Equal(ProfileChangeSource.Import, evidence.Source);
            Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), evidence.ObservedAt);
            Assert.Equal(Guid.Parse("91000000-0000-4000-8000-000000000001"), evidence.BatchId);
            Assert.Equal("SQLite acceptance", evidence.Producer);
        });
        var replay = reopened.PreviewImportFile(import);
        Assert.True(replay.CanApply, reopened.Status);
        Assert.Contains("no-op", Assert.Single(replay.Changes));
        Assert.True(reopened.ApplyImport(replay.Id), reopened.Status);
        Assert.Equal(expected, reopened.GetRelationships(expected.Email)!.Person);
    }

    [Fact]
    public void Staged_Workspace_rejects_unimplemented_changes_without_legacy_fallback()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var before = app.GetRelationships("report@example.com")!.Person;
        var update = app.PreviewImport(Initial.Replace("000000000001", "000000000002"));
        Assert.False(update.CanApply);
        Assert.Contains("initial People-only Import", app.Status);
        Assert.False(app.EditPreferredName(before.Email, "Must not save"));
        Assert.Contains("not implemented", app.Status);
        Assert.False(app.BeginProfileEdit(before.Email));
        Assert.True(app.CanNavigatePeople);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(before, app.GetRelationships(before.Email)!.Person);
        Assert.False(File.Exists(Path.Combine(Workspace, "workspace.json")));
        Assert.False(Directory.Exists(Path.Combine(Workspace, "notes")));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(before, reopened.GetRelationships(before.Email)!.Person);
        Assert.Empty(reopened.GetPersonNotes(before.Email));
        Assert.Empty(reopened.GetTeamNotes(before.Email));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Local_notes_preserve_identity_writing_archival_ongoing_choice_and_scope_on_reopen(NoteScope scope)
    {
        var preferences = Path.Combine(root, "settings", "notes.json");
        Guid id;
        DateTimeOffset created;
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(preferences)))
        {
            Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
            var basis = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, "manager@example.com"));
            var saved = Assert.IsType<NoteDraft>(app.SaveNoteDraft(basis, "Manager context", "# Original\nPrivate writing"));
            id = saved.Id;
            created = scope == NoteScope.Person
                ? Assert.Single(app.GetPersonNotes(saved.OwnerEmail)).CreatedAt
                : Assert.Single(app.GetTeamNotes(saved.OwnerEmail)).CreatedAt;
            Assert.True(app.OpenOwnedNotes(scope, saved.OwnerEmail), app.Status);
            Assert.True(app.DesignateOngoingNote(scope, id), app.Status);
            Assert.NotNull(app.SaveNoteDraft(saved, "Continuing context", "# Updated\nExact latest writing"));
            Assert.True(scope == NoteScope.Person ? app.ArchivePersonNote(id) : app.ArchiveTeamNote(id), app.Status);
            Assert.Empty(scope == NoteScope.Person
                ? app.GetPersonNotes(saved.OwnerEmail).Select(n => n.Id)
                : app.GetTeamNotes(saved.OwnerEmail).Select(n => n.Id));
            Assert.True(scope == NoteScope.Person ? app.ArchivePersonNote(id, false) : app.ArchiveTeamNote(id, false), app.Status);
            if (scope == NoteScope.Team) Assert.True(app.ConfirmTeamNote(id), app.Status);
            Assert.True(app.CanSafelyClose, app.Status);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(preferences));
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.True(reopened.OpenOwnedNotes(scope, "manager@example.com"), reopened.Status);
        var note = reopened.GetOwnedNotesView(scope).SelectedNote!;
        Assert.Equal(id, note.Id);
        Assert.Equal("manager@example.com", note.OwnerEmail);
        Assert.Equal("Continuing context", note.Title);
        Assert.Equal("# Updated\nExact latest writing", note.Markdown);
        Assert.False(note.Archived);
        Assert.Equal(id, reopened.GetOwnedNotesView(scope).OngoingNoteId);
        if (scope == NoteScope.Team)
        {
            var inherited = Assert.Single(reopened.GetInheritedTeamNotes("report@example.com"));
            Assert.Equal(id, inherited.Note.Id);
            Assert.Equal("manager@example.com", inherited.Owner.Email);
            Assert.False(inherited.Note.NeedsReview);
            Assert.Equal(created, inherited.Note.CreatedAt);
            Assert.Empty(reopened.GetPersonNotes("manager@example.com"));
        }
        else
        {
            Assert.Empty(reopened.GetInheritedTeamNotes("report@example.com"));
            Assert.Equal(created, Assert.Single(reopened.GetPersonNotes("manager@example.com")).CreatedAt);
        }
        Assert.Empty(reopened.GetHistoricalTeamContexts("report@example.com"));
        Assert.Empty(reopened.GetProfileTimeline("manager@example.com")!.Changes);
        Assert.False(File.Exists(Path.Combine(Workspace, "workspace.json")));
        Assert.False(Directory.Exists(Path.Combine(Workspace, "notes")));
        Assert.False(Directory.Exists(Path.Combine(Workspace, "team-notes")));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Pending_creation_and_newer_typing_survive_actual_Person_switches_and_late_commit(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        using var held = new SqliteConnection($"Data Source={Path.Combine(Workspace, "workspace.sqlite")};Pooling=False");
        held.Open();
        using var transaction = held.BeginTransaction();
        var first = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.Equal(first.Id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        app.UpdateNoteDraft(scope, first.Id, "First request", "First body");
        await WaitUntil(() => app.GetRetainedNoteDraft(scope, first.Id)?.IsSaving == true);
        app.UpdateNoteDraft(scope, first.Id, "Newest title", "Typed while the first commit was waiting");
        Assert.False(app.CanSafelyClose);
        var restarted = false;
        Assert.False(app.RestartWhenSafe(_ => restarted = true));
        Assert.False(restarted);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        var second = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "report@example.com"));
        app.UpdateNoteDraft(scope, second.Id, "Report context", "Distinct report writing");
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.Equal(first.Id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.Equal("Newest title", app.GetOwnedNotesView(scope).SelectedNote!.Title);
        Assert.Equal("Typed while the first commit was waiting", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.True(app.GetOwnedNotesView(scope).HasUnsavedChanges);
        transaction.Commit();
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        Assert.False(app.GetOwnedNotesView(scope).HasUnsavedChanges);
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Typed while the first commit was waiting", reopened.BeginNoteDraft(scope, "manager@example.com", first.Id)!.Markdown);
        Assert.Equal("Distinct report writing", reopened.BeginNoteDraft(scope, "report@example.com", second.Id)!.Markdown);
        Assert.Single(scope == NoteScope.Person ? reopened.GetPersonNotes("manager@example.com").Select(n => n.Id) :
            reopened.GetTeamNotes("manager@example.com").Select(n => n.Id));
        Assert.Null(reopened.BeginNoteDraft(scope, "report@example.com", first.Id));
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Failed_creation_retains_latest_draft_browsing_and_one_identity_through_explicit_retry(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
        app.UpdateNoteDraft(scope, draft.Id, "Retained title", "Private failed creation");
        using (File.Open(Path.Combine(Workspace, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(await app.FlushNoteDraftsAsync());
        var retained = Assert.IsType<RetainedNoteDraft>(app.GetRetainedNoteDraft(scope, draft.Id));
        Assert.NotNull(retained.Error);
        Assert.Equal("Private failed creation", retained.Markdown);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("NOT saved", app.Status);
        Assert.True(app.NavigatePerson("report@example.com"), app.Status);
        Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        Assert.Equal(draft.Id, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.Equal("Private failed creation", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.True(await app.RetryNoteDraftAsync(scope, draft.Id), app.Status);
        Assert.Null(app.GetRetainedNoteDraft(scope, draft.Id));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Private failed creation", reopened.BeginNoteDraft(scope, "manager@example.com", draft.Id)!.Markdown);
        Assert.Single(scope == NoteScope.Person ? reopened.GetPersonNotes("manager@example.com").Select(n => n.Id) :
            reopened.GetTeamNotes("manager@example.com").Select(n => n.Id));
    }

    [Fact]
    public async Task Discarding_an_uncommitted_creation_restores_empty_reading_without_discarding_another_owner()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var kept = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(NoteScope.Team, "report@example.com"));
        var discarded = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(NoteScope.Team, "manager@example.com"));
        app.DiscardUnsavedChanges(discarded.Id);
        Assert.True(app.TeamNotesView.IsEmpty);
        Assert.False(app.CanSafelyClose);
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Empty(reopened.GetTeamNotes("manager@example.com"));
        Assert.Equal(kept.Id, Assert.Single(reopened.GetTeamNotes("report@example.com")).Id);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Continuous_typing_becomes_durable_before_idle_and_finishes_with_exact_latest_writing(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var observed = new HashSet<string>();
        TimeSpan? firstCommit = null;
        for (var index = 0; index < 25; index++)
        {
            app.UpdateNoteDraft(scope, draft.Id, "Continuous context", $"Continuous writing {index:D2}");
            await Task.Delay(60);
            using var reading = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
            var committed = reading.BeginNoteDraft(scope, "manager@example.com", draft.Id);
            if (committed?.Markdown.StartsWith("Continuous writing ", StringComparison.Ordinal) == true)
            {
                firstCommit ??= elapsed.Elapsed;
                observed.Add(committed.Markdown);
            }
        }
        Assert.NotNull(firstCommit);
        Assert.True(firstCommit <= TimeSpan.FromSeconds(1), $"First ordinary local commit took {firstCommit}.");
        Assert.True(observed.Count >= 3, "Continuous typing must make repeated durable progress, not wait until idle.");
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("Continuous writing 24", reopened.BeginNoteDraft(scope, "manager@example.com", draft.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Durable_note_observation_runs_during_typing_not_after_unrelated_navigation_observations(NoteScope scope)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(scope, "manager@example.com"));
        const string prefix = "# Independently observed ongoing writing";
        using var observation = NoteCommitObservation.Begin(
            () => app.UpdateNoteDraft(scope, draft.Id, "Observed context", prefix),
            () =>
            {
                using var reading = ApplicationWorkflow.CreateFileBacked();
                Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
                return reading.BeginNoteDraft(scope, "manager@example.com", draft.Id)?.Markdown
                    .StartsWith(prefix, StringComparison.Ordinal) == true;
            });
        for (var index = 0; index < 6; index++)
        {
            await Task.Delay(60);
            app.UpdateNoteDraft(scope, draft.Id, "Observed context", prefix + $"\nContinued entry {index}");
            Assert.True(app.NavigatePerson("report@example.com"), app.Status);
            Assert.True(app.NavigatePerson("manager@example.com"), app.Status);
        }
        await Task.Delay(1100);
        var commitMs = observation.Complete();
        Assert.True(commitMs <= 1000,
            $"Native/request-to-reopened-writing observation was delayed by unrelated checks: {commitMs:F1} ms.");
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(prefix + "\nContinued entry 5", reopened.BeginNoteDraft(scope, "manager@example.com", draft.Id)!.Markdown);
    }

    [Fact]
    public async Task Cancelling_observation_of_a_pending_real_write_never_confirms_it_saved()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Workspace, "workspace.sqlite")};Pooling=False");
        connection.Open();
        using var held = connection.BeginTransaction();
        var draft = Assert.IsType<NoteDraft>(app.CreateOwnedNoteDraft(NoteScope.Team, "manager@example.com"));
        var observation = NoteCommitObservation.Begin(
            () => app.UpdateNoteDraft(NoteScope.Team, draft.Id, "Pending context", "Not yet committed"),
            () =>
            {
                using var reading = ApplicationWorkflow.CreateFileBacked();
                Assert.True(reading.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reading.Status);
                return reading.BeginNoteDraft(NoteScope.Team, "manager@example.com", draft.Id)?.Markdown == "Not yet committed";
            });
        try { await WaitUntil(() => app.GetRetainedNoteDraft(NoteScope.Team, draft.Id)?.IsSaving == true); }
        finally { observation.Dispose(); }
        Assert.Contains("cancelled", Assert.Throws<InvalidOperationException>(() => { observation.Complete(); }).Message);
        Assert.False(app.CanSafelyClose);
        held.Commit();
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
    }

    [Fact]
    public void Rejected_creation_preserves_the_existing_legacy_Workspace()
    {
        using (var old = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(old.OpenWorkspace(Workspace), old.Status);
            Assert.True(old.ApplyImport(old.PreviewImport(Initial).Id), old.Status);
        }
        var before = Directory.GetFiles(Workspace, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite));
        Assert.Contains("never converted or overwritten", app.Status);
        Assert.Empty(app.People);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Workspace, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(2, reopened.People.Count);
        Assert.Equal("Reference", reopened.GetRelationships("report@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("mixed")]
    public void Incomplete_or_incompatible_local_storage_never_opens_an_empty_fallback(string failure)
    {
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(seed.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), seed.Status);
            Assert.True(seed.ApplyImport(seed.PreviewImport(Initial).Id), seed.Status);
        }
        var database = Path.Combine(Workspace, "workspace.sqlite");
        switch (failure)
        {
            case "missing": File.Delete(database); break;
            case "corrupt": File.WriteAllText(database, "Not a SQLite database"); break;
            case "mixed": File.WriteAllText(Path.Combine(Workspace, "workspace.json"), "{\"schemaVersion\":99}"); break;
        }
        var before = Directory.GetFiles(Workspace).ToDictionary(path => path, File.ReadAllBytes);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly));
        Assert.False(app.IsWorkspaceCurrent);
        Assert.NotNull(app.OperationError);
        Assert.Empty(app.People);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Workspace).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Failed_initial_Import_can_retry_without_a_partially_accepted_population()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        var preview = app.PreviewImport(Initial);
        Assert.True(preview.CanApply, app.Status);
        using (File.Open(Path.Combine(Workspace, "workspace.sqlite"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(app.ApplyImport(preview.Id));
            Assert.NotNull(app.OperationError);
            Assert.DoesNotContain("saved locally", app.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(app.People);
        }
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(reopened.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            Assert.Empty(reopened.People);
            var retry = reopened.PreviewImport(Initial);
            Assert.True(retry.CanApply, reopened.Status);
            Assert.DoesNotContain("Already applied", string.Join("\n", retry.Changes));
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Initial).Id), app.Status);
        using var saved = ApplicationWorkflow.CreateFileBacked();
        Assert.True(saved.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), saved.Status);
        Assert.Equal(2, saved.People.Count);
        Assert.Equal("manager@example.com", saved.GetRelationships("report@example.com")!.Manager!.Email);
    }

    [Fact]
    public void Stale_initial_review_cannot_overwrite_another_committed_population()
    {
        using var first = ApplicationWorkflow.CreateFileBacked();
        Assert.True(first.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), first.Status);
        using var other = ApplicationWorkflow.CreateFileBacked();
        Assert.True(other.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), other.Status);
        var stale = other.PreviewImport(Initial.Replace("Report", "Unaccepted name"));
        Assert.True(first.ApplyImport(first.PreviewImport(Initial).Id), first.Status);
        Assert.False(other.ApplyImport(stale.Id));
        Assert.Empty(other.People);
        Assert.True(other.RefreshWorkspace(), other.Status);
        Assert.Equal("Report", other.GetRelationships("report@example.com")!.Person.DirectoryName);
        Assert.False(other.PreviewImport(Initial.Replace("Report", "Unaccepted name")).CanApply);
    }

    [Theory]
    [InlineData("\"managerEmail\":null", "\"managerEmail\":\"report@example.com\"", "cycle")]
    [InlineData("\"alias\":\"report\"", "\"alias\":\"report\",\"teamName\":\"Private label\"", "teamName")]
    [InlineData("\"alias\":\"report\"", "\"alias\":\"report\",\"photoPath\":\"local.png\"", "Photos")]
    public void Rejected_initial_Import_preserves_domain_validation_and_never_discards_unsupported_fields(
        string before, string after, string error)
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Workspace, WorkspaceOpenMode.CreateLocalSqlite), app.Status);
        var preview = app.PreviewImport(Initial.Replace(before, after));
        Assert.False(preview.CanApply);
        Assert.Contains(error, string.Join("\n", preview.Errors));
        Assert.False(app.ApplyImport(preview.Id));
        using (var unchanged = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(unchanged.OpenWorkspace(Workspace, WorkspaceOpenMode.ExistingOnly), unchanged.Status);
            Assert.Empty(unchanged.People);
        }
        var valid = app.PreviewImport(Initial);
        Assert.True(valid.CanApply, app.Status);
        Assert.True(app.ApplyImport(valid.Id), app.Status);
        Assert.Equal(2, app.People.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
