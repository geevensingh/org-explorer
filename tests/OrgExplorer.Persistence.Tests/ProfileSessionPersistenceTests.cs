using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class ProfileSessionPersistenceTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private string Folder => Path.Combine(root, "workspace");
    private const string Email = "avery@example.com";
    private const string Batch = """
        {"schemaVersion":1,"batchId":"29000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"},
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
         {"email":"riley@example.com","displayName":"Riley","managerEmail":null}]}
        """;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");

    [Fact]
    public void Reporting_counts_reopen_from_current_people_with_archived_descendants_and_private_context_retained()
    {
        using (var app = Open())
        {
            SeedReporting(app);
            SetTeamName(app, "morgan@example.com", "Private platform");
            Assert.NotNull(app.CreatePersonNote(Email, "Private", "# Saved context"));
            Assert.True(app.EditFavorite(Email, true));
            AssertReporting(app, "riley@example.com", 4, 3, 1);
            AssertReporting(app, "morgan@example.com", 3, 2, 1);
            Assert.True(app.EditPersonArchived("leaf@example.com", true), app.Status);
        }
        using (var reopened = Reopen())
        {
            AssertReporting(reopened, "riley@example.com", 3, 2, 1);
            AssertReporting(reopened, "morgan@example.com", 2, 1, 1);
            Assert.Null(reopened.GetRelationships("v-middle@example.com")!.ReportingSummary);
            Assert.True(reopened.EditPersonArchived("v-middle@example.com", true), reopened.Status);
            Assert.True(reopened.EditPersonArchived(Email, true), reopened.Status);
        }
        using var dormant = Reopen();
        AssertReporting(dormant, "riley@example.com", 1, 1, 0);
        Assert.Null(dormant.GetRelationships("morgan@example.com")!.ReportingSummary);
        Assert.Null(dormant.GetRelationships("v-middle@example.com")!.ReportingSummary);
        Assert.Equal("Private platform", dormant.GetRelationships("morgan@example.com")!.Person.TeamName);
        Assert.Equal("# Saved context", Assert.Single(dormant.GetPersonNotes(Email)).Markdown);
        Assert.True(dormant.GetRelationships(Email)!.Person.IsFavorite);
        Assert.True(dormant.EditPersonArchived(Email, false), dormant.Status);
        AssertReporting(dormant, "morgan@example.com", 1, 1, 0);
    }

    [Fact]
    public void External_reporting_delivery_refreshes_the_selected_profile_and_ancestors_without_losing_note_writing()
    {
        using var source = Open();
        SeedReporting(source);
        const string owner = "morgan@example.com";
        const string ancestor = "riley@example.com";
        SetTeamName(source, ancestor, "Saved ancestor");
        var note = source.CreatePersonNote(owner, "Private", "# Saved context")!;
        var destination = Path.Combine(root, "reporting-receiver");
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(destination), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "reporting-transport"));
        Assert.True(app.NavigatePerson(owner));
        var draft = app.BeginNoteDraft(NoteScope.Person, owner, note.Id)!;
        app.RetainNoteDraft(draft);
        var notifications = 0;
        app.WorkspaceChanged += (_, _) => notifications++;
        AssertReporting(app, owner, 3, 2, 1);
        var review = source.PreviewImport($$"""
            {"schemaVersion":1,"batchId":"{{Guid.NewGuid()}}","observedAt":"2026-11-03T17:00:00Z","people":[
             {"email":"v-middle@example.com","managerEmail":"riley@example.com"},
             {"email":"avery@example.com","archived":true}]}
            """);
        Assert.True(review.CanApply, source.Status);
        Assert.True(source.ApplyImport(review.Id), source.Status);
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        Assert.False(app.SyncNow());
        AssertReporting(app, owner, 3, 2, 1);
        Assert.True(app.HasUnsavedNoteDraft(NoteScope.Person, note.Id));
        Assert.True(app.NavigatePerson(ancestor), app.Status);
        Assert.True(app.NavigatePerson(owner), app.Status);
        app.DiscardUnsavedChanges(note.Id);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Equal(owner, app.PeopleNavigation.SelectedEmail);
        Assert.Null(app.GetRelationships(app.PeopleNavigation.SelectedEmail!)!.ReportingSummary);
        AssertReporting(app, ancestor, 3, 2, 1);
        Assert.Equal("Saved ancestor", Assert.Single(app.GetTeamNames(owner)).Name);
        Assert.Equal("# Saved context", Assert.Single(app.GetPersonNotes(owner)).Markdown);
        Assert.True(source.EditPersonArchived(Email, false), source.Status);
        Assert.True(source.EditPersonManager("v-middle@example.com", owner), source.Status);
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        var artifact = WorkspaceFixtures.LatestSnapshot(source);
        var valid = File.ReadAllBytes(artifact);
        WorkspaceFixtures.RewriteSnapshot(artifact, content =>
            WorkspaceFixtures.EditSnapshotRow(content, "people", "v-middle@example.com",
                person => person["managerEmail"] = "missing@example.com"));
        Assert.False(app.SyncNow());
        Assert.Null(app.GetRelationships(owner)!.ReportingSummary);
        AssertReporting(app, ancestor, 3, 2, 1);
        File.WriteAllBytes(artifact, valid);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        AssertReporting(app, app.PeopleNavigation.SelectedEmail!, 3, 2, 1);
        AssertReporting(app, ancestor, 4, 3, 1);
        draft = app.BeginNoteDraft(NoteScope.Person, owner, note.Id)!;
        app.RetainNoteDraft(draft);
        Assert.NotNull(app.SaveNoteDraft(draft, "Private", "# Retained writing"));
        Assert.True(notifications > 0);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(destination, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        AssertReporting(reopened, owner, 3, 2, 1);
        AssertReporting(reopened, ancestor, 4, 3, 1);
        Assert.Equal("# Retained writing", Assert.Single(reopened.GetPersonNotes(owner)).Markdown);
        Assert.Equal("Saved ancestor", Assert.Single(reopened.GetTeamNames(owner)).Name);
    }

    private static void SeedReporting(WorkspaceViewModel app)
    {
        var review = app.PreviewImport($$"""
            {"schemaVersion":1,"batchId":"{{Guid.NewGuid()}}","observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"morgan@example.com","managerEmail":"riley@example.com"},
             {"email":"v-middle@example.com","displayName":"Vendor middle","managerEmail":"morgan@example.com"},
             {"email":"leaf@example.com","displayName":"Leaf","managerEmail":"v-middle@example.com"}]}
            """);
        Assert.True(review.CanApply, app.Status);
        Assert.True(app.ApplyImport(review.Id), app.Status);
    }

    private static void AssertReporting(IApplicationWorkflow app, string email, int total, int employees, int vendors)
    {
        var summary = Assert.IsType<ReportingSummary>(app.GetRelationships(email)!.ReportingSummary);
        Assert.Equal(total, summary.Total);
        Assert.Equal(new[] { ("Employees", employees), ("Vendors", vendors) },
            summary.Categories.Select(count => (count.Category.PluralDisplayName, count.Count)));
    }

    [Fact]
    public void Category_reopens_for_managers_and_archived_contributors_with_unrelated_private_context_intact()
    {
        using (var app = Open())
        {
            var preview = app.PreviewImport("""
                {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000091",
                 "observedAt":"2026-11-03T17:00:00Z","people":[
                 {"email":"V-manager@example.com","displayName":"Employee manager","managerEmail":null},
                 {"email":"v-contributor@example.com","displayName":"Employee","managerEmail":"V-manager@example.com"},
                 {"email":"avery@example.com","managerEmail":"V-manager@example.com"}]}
                """);
            Assert.True(preview.CanApply, app.Status);
            Assert.True(app.ApplyImport(preview.Id), app.Status);
            SetTeamName(app, "V-manager@example.com", "Private platform");
            Assert.NotNull(app.CreatePersonNote(Email, "Private", "# Retained context"));
            Assert.True(app.EditFavorite(Email, true), app.Status);
            Assert.True(app.EditPersonArchived("v-contributor@example.com", true), app.Status);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(Folder, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        foreach (var (email, category) in new[] { ("V-manager@example.com", "Vendor"),
            ("v-contributor@example.com", "Vendor"), (Email, "Employee") })
        {
            Assert.True(reopened.NavigatePerson(email), reopened.Status);
            var profile = reopened.GetRelationships(reopened.PeopleNavigation.SelectedEmail!)!;
            Assert.Equal(email, profile.Person.Email);
            Assert.Equal(category, profile.Category.DisplayName);
        }
        Assert.True(reopened.GetRelationships("v-contributor@example.com")!.Person.Archived);
        Assert.Equal(Email, Assert.Single(reopened.GetRelationships("V-manager@example.com")!.ImportedReports).Email);
        Assert.Equal("Private platform", Assert.Single(reopened.GetTeamNames(Email)).Name);
        Assert.Equal("# Retained context", Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
        Assert.True(reopened.GetRelationships(Email)!.Person.IsFavorite);
    }

    [Fact]
    public void Accepted_external_refresh_keeps_the_selected_category_and_retained_writing_then_navigates_to_delivered_people()
    {
        using var source = Open();
        var note = source.CreatePersonNote(Email, "Private", "# Saved context")!;
        var destination = Path.Combine(root, "category-receiver");
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(destination), app.Status);
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(root, "category-transport"));
        Assert.True(app.NavigatePerson(Email), app.Status);
        var draft = app.BeginNoteDraft(NoteScope.Person, Email, note.Id)!;
        app.RetainNoteDraft(draft);
        var notifications = 0;
        app.WorkspaceChanged += (_, _) => notifications++;
        var preview = source.PreviewImport("""
            {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000092",
             "observedAt":"2026-11-03T17:00:00Z","people":[
             {"email":"V-delivered@example.com","displayName":"Delivered employee","managerEmail":null},
             {"email":"avery@example.com","displayName":"v-Delivered name","managerEmail":"V-delivered@example.com"},
             {"email":"t-delivered@example.com","displayName":"Delivered fallback","managerEmail":null,"archived":true}]}
            """);
        Assert.True(preview.CanApply, source.Status);
        Assert.True(source.ApplyImport(preview.Id), source.Status);
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        Assert.False(app.SyncNow());
        Assert.Equal("Avery", app.GetRelationships(Email)!.Person.DirectoryName);
        Assert.True(app.HasUnsavedNoteDraft(NoteScope.Person, note.Id));
        app.DiscardUnsavedChanges(note.Id);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        var selected = app.GetRelationships(app.PeopleNavigation.SelectedEmail!)!;
        Assert.Equal("employee", selected.Category.Id);
        Assert.Equal("v-Delivered name", selected.Person.DirectoryName);
        Assert.Equal("V-delivered@example.com", selected.Manager!.Email);
        draft = app.BeginNoteDraft(NoteScope.Person, Email, note.Id)!;
        app.RetainNoteDraft(draft);
        Assert.NotNull(app.SaveNoteDraft(draft, "Private", "# Retained writing"));
        Assert.True(app.NavigatePerson("V-delivered@example.com"), app.Status);
        Assert.Equal("Vendor", app.GetRelationships(app.PeopleNavigation.SelectedEmail!)!.Category.DisplayName);
        Assert.True(app.NavigatePerson("t-delivered@example.com"), app.Status);
        Assert.True(app.GetRelationships(app.PeopleNavigation.SelectedEmail!)!.Person.Archived);
        Assert.Equal("Employee", app.GetRelationships(app.PeopleNavigation.SelectedEmail!)!.Category.DisplayName);
        Assert.True(notifications > 0);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(destination, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal("# Retained writing", Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
        Assert.Equal("vendor", reopened.GetRelationships("V-delivered@example.com")!.Category.Id);
    }

    [Fact]
    public void Rejected_local_profile_save_reports_feedback_and_retains_browsing_without_inventing_a_Workspace_change()
    {
        using var app = Open();
        Assert.True(app.Compatibility.SupportsNonblockingNoteAutosave);
        Assert.True(app.NavigatePerson(Email), app.Status);
        Assert.True(app.BeginProfileEdit(Email), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Retained legacy room"), app.Status);
        var caller = Environment.CurrentManagedThreadId;
        var revision = app.Synchronization.LocalRevision;
        var observedRevisions = new List<Guid?>();
        var statusNotifications = 0;
        app.WorkspaceChanged += (_, _) =>
        {
            if (Environment.CurrentManagedThreadId == caller) observedRevisions.Add(app.Synchronization.LocalRevision);
        };
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(app.Status)) statusNotifications++;
        };
        using (WorkspaceFixtures.LockLocalStore(Folder))
            Assert.False(app.SaveProfileEdit());
        Assert.Contains("NOT saved", app.Status);
        Assert.Equal(Email, app.ProfileEdit!.Basis.Email);
        Assert.Equal("Retained legacy room", app.ProfileEdit.Draft.OfficeNumber);
        Assert.False(app.CanEditLocalWorkspace);
        Assert.Equal(LocalSavePhase.Failed, app.LocalSave.Phase);
        Assert.True(app.CanNavigatePeople);
        Assert.False(app.CanSafelyClose);
        Assert.True(statusNotifications > 0);
        Assert.All(observedRevisions, observed => Assert.Equal(revision, observed));
        using var durable = Reopen();
        Assert.Null(durable.GetRelationships(Email)!.Person.OfficeNumber);
    }

    [Fact]
    public void Inherited_names_reopen_with_duplicate_owners_dormant_ancestors_and_final_imported_relationships()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        const string ancestor = "riley@example.com";
        Assert.True(app.EditPersonManager(owner, ancestor), app.Status);
        SetTeamName(app, owner, "Platform");
        SetTeamName(app, ancestor, "Platform");
        var note = app.CreateTeamNote(ancestor, "Context", "# Original ancestor")!;
        using (var reopened = Reopen())
        {
            Assert.Equal(new[] { ("Platform", owner), ("Platform", ancestor) },
                reopened.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)));
            Assert.Equal(new[] { ("Platform", owner), ("Platform", ancestor) },
                reopened.GetTeamNames(owner).Select(s => (s.Name, s.Owner.Email)));
            Assert.Equal(("Platform", ancestor),
                reopened.GetTeamNames(ancestor).Select(s => (s.Name, s.Owner.Email)).Single());
        }
        SetTeamName(app, owner, null);
        SetTeamName(app, ancestor, "Renamed ancestor");
        using (var cleared = Reopen())
            Assert.Equal(("Renamed ancestor", ancestor),
                cleared.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)).Single());
        SetTeamName(app, owner, "Platform");
        Assert.True(app.EditPersonArchived(Email, true), app.Status);
        using (var dormant = Reopen())
        {
            Assert.Empty(dormant.GetTeamNames(Email));
            Assert.Equal(("Renamed ancestor", ancestor),
                dormant.GetTeamNames(owner).Select(s => (s.Name, s.Owner.Email)).Single());
            Assert.False(dormant.CanEditTeamName(owner));
            Assert.Equal("Platform", dormant.GetRelationships(owner)!.Person.TeamName);
            Assert.True(dormant.EditPersonArchived(owner, true), dormant.Status);
        }
        using (var archived = Reopen())
        {
            Assert.Empty(archived.GetTeamNames(owner));
            Assert.Equal("Platform", archived.GetRelationships(owner)!.Person.TeamName);
            Assert.True(archived.EditPersonArchived(owner, false), archived.Status);
            Assert.Equal("Renamed ancestor", Assert.Single(archived.GetTeamNames(owner)).Name);
            Assert.True(archived.EditPersonArchived(Email, false), archived.Status);
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(new[] { ("Platform", owner), ("Renamed ancestor", ancestor) },
            app.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)));
        var priorContexts = app.GetHistoricalTeamContexts(Email).Select(c => c.Id).ToHashSet();
        var review = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000089",
             "observedAt":"2026-11-03T17:00:00Z","people":[
             {"email":"morgan@example.com","managerEmail":null},
             {"email":"avery@example.com","managerEmail":"riley@example.com"}]}
            """);
        Assert.True(review.CanApply, app.Status);
        Assert.True(app.ApplyImport(review.Id), app.Status);
        var frozen = app.GetHistoricalTeamContexts(Email).Single(c => !priorContexts.Contains(c.Id));
        Assert.Equal("# Original ancestor", Assert.Single(frozen.TeamNotes).Markdown);
        Assert.True(app.EditTeamNote(note.Id, "Context", "# New current writing"), app.Status);
        using (var imported = Reopen())
        {
            Assert.Equal(("Renamed ancestor", ancestor),
                imported.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)).Single());
            Assert.Empty(imported.GetTeamNames(owner));
            Assert.Equal("Platform", imported.GetRelationships(owner)!.Person.TeamName);
            var retained = imported.GetHistoricalTeamContexts(Email).Single(c => c.Id == frozen.Id);
            Assert.Equal("# Original ancestor", Assert.Single(retained.TeamNotes).Markdown);
            Assert.All(imported.GetHistoricalTeamContexts(Email).SelectMany(c => c.ReportingChain),
                manager => Assert.Null(manager.TeamName));
            Assert.DoesNotContain(imported.GetProfileTimeline(owner)!.Changes, c => c.Field == "teamName");
            Assert.DoesNotContain(imported.GetProfileTimeline(Email)!.Changes, c => c.Field == "teamName");
            Assert.True(imported.EditPersonManager(Email, owner), imported.Status);
        }
        using var restored = Reopen();
        Assert.Equal(("Platform", owner), restored.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)).Single());
        Assert.Equal("Renamed ancestor", restored.GetRelationships(ancestor)!.Person.TeamName);
    }

    [Fact]
    public void Delivered_names_and_ancestor_moves_refresh_paths_without_overwriting_retained_profile_or_note_writing()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        const string ancestor = "riley@example.com";
        Assert.True(app.EditPersonManager(owner, ancestor), app.Status);
        SetTeamName(app, owner, "Platform");
        SetTeamName(app, ancestor, "Platform");
        var note = app.CreatePersonNote(Email, "Private", "Saved writing")!;
        var draft = app.BeginNoteDraft(NoteScope.Person, Email, note.Id)!;
        app.RetainNoteDraft(draft);
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Retained local label"));
        Deliver(ancestor, "teamName", "Delivered ancestor");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(new[] { ("Platform", owner), ("Delivered ancestor", ancestor) },
            app.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)));
        Assert.Equal("Retained local label", app.ProfileEdit!.Draft.TeamName);
        Deliver(owner, "teamName", "Delivered owner");
        Assert.True(app.RefreshWorkspace(), app.Status);
        var pointer = app.Synchronization.LocalRevision;
        Assert.False(app.SaveProfileEdit());
        Assert.Contains("changed externally", app.LocalSaveError);
        Assert.Equal("Retained local label", app.ProfileEdit!.Draft.TeamName);
        Assert.Equal(pointer, app.Synchronization.LocalRevision);
        app.CancelProfileEdit();
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.SaveNoteDraft(draft, "Private", "Independent retained writing"));
        Assert.True(app.CanSafelyClose);
        using (var reopened = Reopen())
        {
            Assert.Equal(new[] { ("Delivered owner", owner), ("Delivered ancestor", ancestor) },
                reopened.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)));
            Assert.Equal("Independent retained writing", Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
            Assert.True(reopened.EditPersonManager(owner, null), reopened.Status);
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(("Delivered owner", owner), app.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)).Single());
        Assert.Equal(owner, app.GetRelationships(Email)!.Person.ManagerEmail);
        using (var provider = Reopen())
            Assert.True(provider.EditPersonManager(owner, ancestor), provider.Status);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Deliver(ancestor, "teamName", null);
        Assert.True(app.RefreshWorkspace(), app.Status);
        using var cleared = Reopen();
        Assert.Equal(("Delivered owner", owner), cleared.GetTeamNames(Email).Select(s => (s.Name, s.Owner.Email)).Single());
        Assert.Null(cleared.GetRelationships(ancestor)!.Person.TeamName);
    }

    private static void SetTeamName(WorkspaceViewModel app, string owner, string? name)
    {
        Assert.True(app.BeginProfileEdit(owner), app.Status);
        Assert.True(app.StageProfileTeamName(name), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
    }

    [Fact]
    public void Owned_Team_name_reopens_visible_then_hidden_and_returns_only_with_a_current_report()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Platform"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        using (var visible = Reopen())
            Assert.Equal("Platform", Assert.Single(visible.GetTeamNames(owner)).Name);
        Assert.True(app.EditPersonArchived(Email, true), app.Status);
        using (var hidden = Reopen())
        {
            Assert.Empty(hidden.GetTeamNames(owner));
            Assert.False(hidden.CanEditTeamName(owner));
            Assert.Equal("Platform", hidden.GetRelationships(owner)!.Person.TeamName);
            Assert.True(hidden.EditPersonArchived(owner, true), hidden.Status);
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Empty(app.GetTeamNames(owner));
        Assert.True(app.EditPersonArchived(owner, false), app.Status);
        Assert.Empty(app.GetTeamNames(owner));
        Assert.True(app.EditPersonArchived(Email, false), app.Status);
        using var restored = Reopen();
        var segment = Assert.Single(restored.GetTeamNames(owner));
        Assert.Equal(("Platform", owner), (segment.Name, segment.Owner.Email));
        Assert.True(restored.CanEditTeamName(owner));
        Assert.All(restored.GetHistoricalTeamContexts(Email).SelectMany(c => c.ReportingChain),
            manager => Assert.Null(manager.TeamName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_transfer_retains_visible_and_dormant_owned_Team_names(bool dormant)
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Protected platform"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        if (dormant) Assert.True(app.EditPersonManager(Email, "riley@example.com"), app.Status);
        var destination = Path.Combine(root, "restored");
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(destination), reopened.Status);
        WorkspaceFixtures.JoinSnapshot(app, reopened, Path.Combine(root, "transport"));
        Assert.Equal("Protected platform", reopened.GetRelationships(owner)!.Person.TeamName);
        Assert.Equal(!dormant, reopened.CanEditTeamName(owner));
        Assert.Equal(dormant ? [] : new[] { "Protected platform" }, reopened.GetTeamNames(owner).Select(s => s.Name));
        Assert.Empty(reopened.GetProfileTimeline(owner)!.Changes);
        if (dormant) Assert.True(reopened.EditPersonManager(Email, owner), reopened.Status);
        Assert.Equal("Protected platform", Assert.Single(reopened.GetTeamNames(owner)).Name);
    }

    [Fact]
    public void Rejected_Team_name_transaction_retains_complete_profile_and_independent_note_drafts()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Saved platform"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        var note = app.CreatePersonNote(owner, "Context", "Saved writing")!;
        var noteDraft = app.BeginNoteDraft(NoteScope.Person, owner, note.Id)!;
        app.RetainNoteDraft(noteDraft);
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Renamed platform"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "008"));
        var pointer = app.Synchronization.LocalRevision;
        using (WorkspaceFixtures.LockLocalStore(Folder))
        {
            Assert.False(app.SaveProfileEdit());
            Assert.Contains("NOT saved", app.LocalSaveError);
            Assert.Equal("Renamed platform", app.ProfileEdit!.Draft.TeamName);
            Assert.Equal("008", app.ProfileEdit.Draft.OfficeNumber);
            Assert.Equal(pointer, app.Synchronization.LocalRevision);
            using var unchanged = Reopen();
            Assert.Equal("Saved platform", Assert.Single(unchanged.GetTeamNames(owner)).Name);
            Assert.Null(unchanged.GetRelationships(owner)!.Person.OfficeNumber);
            Assert.Equal("Saved writing", Assert.Single(unchanged.GetPersonNotes(owner)).Markdown);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.SaveNoteDraft(noteDraft, "Context", "Independent retained writing"));
        Assert.True(app.CanSafelyClose);
        using var reopened = Reopen();
        Assert.Equal("Renamed platform", Assert.Single(reopened.GetTeamNames(owner)).Name);
        Assert.Equal("008", reopened.GetRelationships(owner)!.Person.OfficeNumber);
        Assert.Equal("Independent retained writing", Assert.Single(reopened.GetPersonNotes(owner)).Markdown);
        Assert.Equal("officeNumber", Assert.Single(reopened.GetProfileTimeline(owner)!.Changes).Field);
        Assert.Empty(reopened.GetHistoricalTeamContexts(owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Concurrent_Team_name_or_report_delivery_rejects_stale_save_without_explicit_refresh(bool loseReport)
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Original"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName("Retained local label"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "009"));
        using (var provider = Reopen())
        {
            if (loseReport) Assert.True(provider.EditPersonManager(Email, "riley@example.com"), provider.Status);
            else
            {
                Assert.True(provider.BeginProfileEdit(owner));
                Assert.True(provider.StageProfileTeamName("Delivered label"));
                Assert.True(provider.SaveProfileEdit(), provider.Status);
            }
        }
        using var durableBefore = Reopen();
        var pointer = durableBefore.Synchronization.LocalRevision;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.False(app.SaveProfileEdit());
            Assert.Contains(loseReport ? "current direct report" : "changed externally", app.LocalSaveError);
            Assert.Equal("Retained local label", app.ProfileEdit!.Draft.TeamName);
            Assert.Equal("009", app.ProfileEdit.Draft.OfficeNumber);
            Assert.False(app.CanSafelyClose);
            using var durableAfter = Reopen();
            Assert.Equal(pointer, durableAfter.Synchronization.LocalRevision);
        }
        app.CancelProfileEdit();
        using var reopened = Reopen();
        Assert.Equal(loseReport ? "Original" : "Delivered label", reopened.GetRelationships(owner)!.Person.TeamName);
        Assert.Null(reopened.GetRelationships(owner)!.Person.OfficeNumber);
        if (loseReport) Assert.Empty(reopened.GetTeamNames(owner));
        else Assert.Equal("Delivered label", Assert.Single(reopened.GetTeamNames(owner)).Name);
    }

    [Fact]
    public void Private_Team_name_delivery_refreshes_without_history_and_invalid_local_edit_retains_last_valid_state()
    {
        using var app = Open();
        const string owner = "morgan@example.com";
        Assert.Empty(app.GetTeamNames(owner));
        Deliver(owner, "teamName", "Delivered private context");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal("Delivered private context", Assert.Single(app.GetTeamNames(owner)).Name);
        Assert.Empty(app.GetProfileTimeline(owner)!.Changes);
        using (var reopened = Reopen())
            Assert.Equal("Delivered private context", Assert.Single(reopened.GetTeamNames(owner)).Name);
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName(" "));
        Assert.False(app.SaveProfileEdit());
        Assert.Contains("teamName", app.Status);
        Assert.Equal("Delivered private context", Assert.Single(app.GetTeamNames(owner)).Name);
        app.CancelProfileEdit();
        Deliver(owner, "teamName", "Corrected delivery");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal("Corrected delivery", Assert.Single(app.GetTeamNames(owner)).Name);
        Assert.True(app.BeginProfileEdit(owner));
        Assert.True(app.StageProfileTeamName(null));
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var cleared = Reopen();
        Assert.Empty(cleared.GetTeamNames(owner));
    }

    [Fact]
    public void Complete_profile_save_is_one_durable_publication_and_cancel_does_not_publish()
    {
        using var app = Open();
        var image = Path.Combine(root, "local.png");
        File.WriteAllBytes(image, Png);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.True(app.StageProfileManager("riley@example.com"));
        Assert.True(app.StageProfilePhoto(image));
        var pointer = app.Synchronization.LocalRevision;
        using (var before = Reopen())
        {
            Assert.Equal("Engineer", before.GetRelationships(Email)!.Person.JobTitle);
            Assert.Null(before.GetPhoto(Email));
            Assert.Empty(before.GetHistoricalTeamContexts(Email));
        }
        app.CancelProfileEdit();
        Assert.Equal(pointer, app.Synchronization.LocalRevision);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.True(app.StageProfileManager("riley@example.com"));
        Assert.True(app.StageProfilePhoto(image));
        using (WorkspaceFixtures.LockLocalStore(Folder))
        {
            Assert.False(app.SaveProfileEdit());
            Assert.Equal("Principal", app.ProfileEdit!.Draft.JobTitle);
            Assert.Equal(Email, app.ProfileEdit.Basis.Email);
            Assert.Equal(pointer, app.Synchronization.LocalRevision);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.True(app.EditPreferredName(Email, "My Avery"), app.Status);
        Assert.True(app.EditFavorite(Email, true), app.Status);
        Assert.NotNull(app.CreatePersonNote(Email, "Private context", "# Retained"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        File.Delete(image);
        using var reopened = Reopen();
        var person = reopened.GetRelationships(Email)!.Person;
        Assert.Equal("Principal", person.JobTitle);
        Assert.Equal("riley@example.com", person.ManagerEmail);
        Assert.Equal("My Avery", person.PreferredName);
        Assert.True(person.IsFavorite);
        var photo = reopened.GetPhoto(Email)!;
        Assert.Equal((1, 1, "image/png"), (photo.Width, photo.Height, photo.ContentType));
        Assert.Equal(Png, photo.Bytes.ToArray());
        Assert.Equal("# Retained", Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
        Assert.Equal(new[] { "isFavorite", "jobTitle", "managerEmail", "photoPath", "preferredName" },
            reopened.GetProfileTimeline(Email)!.Changes.Select(c => c.Field).Order().ToArray());
        Assert.Equal("morgan@example.com",
            Assert.Single(Assert.Single(reopened.GetHistoricalTeamContexts(Email)).ReportingChain).Email);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfilePhoto(null));
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var cleared = Reopen();
        Assert.Null(cleared.GetPhoto(Email));
    }

    [Fact]
    public void Import_transition_retains_a_failed_leave_save_then_publishes_only_the_explicitly_reviewed_batch()
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "009"));
        var pointer = app.Synchronization.LocalRevision;
        using (WorkspaceFixtures.LockLocalStore(Folder))
        {
            Assert.False(app.TryLeaveProfileEdit(ProfileLeaveDecision.Save));
            Assert.Contains("NOT saved", app.Status);
            Assert.False(app.TryOpenImport());
            Assert.False(app.IsImportOpen);
            Assert.Equal(Email, app.ProfileEdit!.Basis.Email);
            Assert.Equal("Principal", app.ProfileEdit.Draft.JobTitle);
            Assert.Equal(pointer, app.Synchronization.LocalRevision);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.True(app.TryLeaveProfileEdit(ProfileLeaveDecision.Save), app.Status);
        Assert.True(app.TryOpenImport(), app.Status);
        var import = Path.Combine(root, "review.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"32000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"avery@example.com","jobTitle":"Imported title"}]}
            """);
        var review = app.PreviewImportFile(import);
        Assert.True(review.CanApply, app.Status);
        using (var beforeApply = Reopen())
            Assert.Equal("Principal", beforeApply.GetRelationships(Email)!.Person.JobTitle);
        Assert.True(app.ApplyImport(review.Id), app.Status);
        app.ShowPeople();
        Assert.False(app.IsImportOpen);
        Assert.Null(app.ProfileEdit);
        using var reopened = Reopen();
        var person = reopened.GetRelationships(Email)!.Person;
        Assert.Equal("Imported title", person.JobTitle);
        Assert.Equal("009", person.OfficeNumber);
        Assert.Equal("morgan@example.com", person.ManagerEmail);
        Assert.Contains(reopened.GetProfileTimeline(Email)!.Changes, change =>
            change.Field == "jobTitle" && change.NewValue.GetString() == "Imported title");
    }

    [Fact]
    public void Failed_import_can_return_to_people_then_reenter_review_and_apply_without_discarding_its_error()
    {
        using var app = Open();
        Assert.True(app.TryOpenImport(), app.Status);
        var import = Path.Combine(root, "retry-import.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"32000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"avery@example.com","jobTitle":"Reviewed retry title"}]}
            """);
        var review = app.PreviewImportFile(import);
        Assert.True(review.CanApply, app.Status);
        var pointer = app.Synchronization.LocalRevision;
        using (WorkspaceFixtures.LockLocalStore(Folder))
        {
            Assert.False(app.ApplyImport(review.Id));
            Assert.Contains("NOT saved", app.LocalSaveError);
            Assert.False(app.CanSafelyClose);
            Assert.Equal("Engineer", app.GetRelationships(Email)!.Person.JobTitle);
            Assert.Equal(pointer, app.Synchronization.LocalRevision);
            app.ShowPeople();
            Assert.False(app.IsImportOpen);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        var failure = app.LocalSaveError;
        Assert.True(app.TryOpenImport(), app.Status);
        Assert.True(app.IsImportOpen);
        Assert.Equal(failure, app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        Assert.Equal(pointer, app.Synchronization.LocalRevision);
        review = app.PreviewImportFile(import);
        Assert.True(review.CanApply, app.Status);
        using (var beforeApply = Reopen())
            Assert.Equal("Engineer", beforeApply.GetRelationships(Email)!.Person.JobTitle);
        Assert.True(app.ApplyImport(review.Id), app.Status);
        app.ShowPeople();
        Assert.False(app.IsImportOpen);
        Assert.Null(app.LocalSaveError);
        Assert.True(app.CanSafelyClose);
        using var reopened = Reopen();
        Assert.Equal("Reviewed retry title", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("morgan@example.com", reopened.GetRelationships(Email)!.Person.ManagerEmail);
        Assert.Equal("Reviewed retry title",
            Assert.Single(reopened.GetProfileTimeline(Email)!.Changes).NewValue.GetString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("Person")]
    [InlineData("Team")]
    [InlineData("preferred")]
    [InlineData("profile")]
    public void Discarding_failed_import_releases_only_that_batch_and_preserves_independent_writing(string independent)
    {
        using var app = Open();
        var personNote = app.CreatePersonNote(Email, "Person context", "# Saved Person")!;
        var teamNote = app.CreateTeamNote(Email, "Team context", "# Saved Team")!;
        Assert.True(app.TryOpenImport(), app.Status);
        var review = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"32000000-0000-4000-8000-000000000003",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"avery@example.com","jobTitle":"Abandoned import"}]}
            """);
        Assert.True(review.CanApply, app.Status);
        var pointer = app.Synchronization.LocalRevision;
        NoteDraft? noteDraft = null;
        if (independent is "Person" or "Team")
        {
            var scope = independent == "Person" ? NoteScope.Person : NoteScope.Team;
            noteDraft = app.BeginNoteDraft(scope, Email, scope == NoteScope.Person ? personNote.Id : teamNote.Id)!;
            Assert.Null(app.SaveNoteDraft(noteDraft, "", "# Retained writing"));
        }
        else if (independent == "preferred")
            Assert.False(app.EditPreferredName(Email, " "));
        else if (independent == "profile")
        {
            Assert.True(app.BeginProfileEdit(Email));
            Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained profile"));
        }
        using (WorkspaceFixtures.LockLocalStore(Folder))
        {
            Assert.False(app.ApplyImport(review.Id));
            Assert.True(app.HasFailedImport);
            Assert.False(app.CanSafelyClose);
            app.ShowPeople();
            app.DiscardImportChanges();
            Assert.False(app.HasFailedImport);
            Assert.True(app.LocalSave.EditsPaused);
            Assert.False(app.CanSafelyClose);
            var restarted = false;
            Assert.False(app.RestartWhenSafe(_ => restarted = true));
            Assert.False(restarted);
            Assert.Equal(pointer, app.Synchronization.LocalRevision);
            if (independent == "profile")
                Assert.Equal("Retained profile", app.ProfileEdit!.Draft.JobTitle);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.False(app.ApplyImport(review.Id));
        Assert.Contains("missing or stale", app.Status);
        if (noteDraft is not null)
        {
            Assert.False(app.CanSafelyClose);
            Assert.NotNull(app.SaveNoteDraft(noteDraft, "Retained title", "# Retained writing"));
        }
        else if (independent == "preferred") app.DiscardPersonChanges(Email);
        else if (independent == "profile") app.CancelProfileEdit();
        Assert.True(app.CanSafelyClose);
        using var reopened = Reopen();
        Assert.Equal("Engineer", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Empty(reopened.GetProfileTimeline(Email)!.Changes);
        Assert.Equal(independent == "Person" ? "# Retained writing" : "# Saved Person",
            Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
        Assert.Equal(independent == "Team" ? "# Retained writing" : "# Saved Team",
            Assert.Single(reopened.GetTeamNotes(Email)).Markdown);
    }

    [Theory]
    [InlineData("jobTitle", false)]
    [InlineData("department", false)]
    [InlineData("preferredName", false)]
    [InlineData("department", true)]
    public void Same_profile_delivery_blocks_save_and_retry_even_without_view_refresh(string field, bool refresh)
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Local draft"));
        using (var provider = Reopen())
            Assert.True(provider.EditPersonText(Email, field switch
            {
                "jobTitle" => PersonTextField.JobTitle,
                "department" => PersonTextField.Department,
                _ => PersonTextField.PreferredName
            }, "Delivered"), provider.Status);
        using var deliveredReader = Reopen();
        var delivered = deliveredReader.GetRelationships(Email)!.Person;
        if (refresh) Assert.True(app.RefreshWorkspace(), app.Status);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.False(app.SaveProfileEdit());
            Assert.Contains("changed externally", app.Status);
            Assert.Equal("Local draft", app.ProfileEdit!.Draft.JobTitle);
            using var unchanged = Reopen();
            Assert.Equal(delivered, unchanged.GetRelationships(Email)!.Person);
            Assert.False(app.CanSafelyClose);
            Assert.False(app.TryOpenImport());
            Assert.False(app.IsImportOpen);
        }
        app.CancelProfileEdit();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "007"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var reopened = Reopen();
        Assert.Equal("007", reopened.GetRelationships(Email)!.Person.OfficeNumber);
    }

    [Fact]
    public void Incorporated_delivery_and_unrelated_person_delivery_do_not_block_or_get_reverted()
    {
        using var app = Open();
        Deliver(Email, "department", "Already incorporated");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Local draft"));
        Deliver("riley@example.com", "officeNumber", "Delivered elsewhere");
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var reopened = Reopen();
        Assert.Equal("Already incorporated", reopened.GetRelationships(Email)!.Person.Department);
        Assert.Equal("Local draft", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("Delivered elsewhere", reopened.GetRelationships("riley@example.com")!.Person.OfficeNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unavailable_photo_and_invalid_field_retain_the_whole_draft_without_partial_history(bool lockedPhoto)
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.True(app.StageProfileManager("riley@example.com"));
        var image = Path.Combine(root, "photo.png");
        if (lockedPhoto) File.WriteAllBytes(image, Png);
        Assert.True(app.StageProfilePhoto(image));
        using (lockedPhoto ? File.Open(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
            Assert.False(app.SaveProfileEdit());
        Assert.Contains("photo", app.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(app.StageProfilePhoto(null));
        Assert.True(app.StageProfileText(PersonTextField.LinkedInProfileUrl, "http://invalid.example"));
        Assert.False(app.SaveProfileEdit());
        Assert.Equal("Principal", app.ProfileEdit!.Draft.JobTitle);
        using var reopened = Reopen();
        Assert.Equal("Engineer", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("morgan@example.com", reopened.GetRelationships(Email)!.Person.ManagerEmail);
        Assert.Empty(reopened.GetProfileTimeline(Email)!.Changes);
        Assert.Empty(reopened.GetHistoricalTeamContexts(Email));
    }

    [Fact]
    public void Local_quick_change_does_not_authorize_an_existing_external_conflict_and_cancel_keeps_both()
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Local draft"));
        Deliver(Email, "department", "Delivered department");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.EditPreferredName(Email, "My Avery"), app.Status);
        Assert.True(app.EditFavorite(Email, true), app.Status);
        Assert.False(app.SaveProfileEdit());
        Assert.Contains("changed externally", app.Status);
        Assert.Equal("Local draft", app.ProfileEdit!.Draft.JobTitle);
        app.CancelProfileEdit();
        using var reopened = Reopen();
        var saved = reopened.GetRelationships(Email)!.Person;
        Assert.Equal("My Avery", saved.PreferredName);
        Assert.True(saved.IsFavorite);
        Assert.Equal("Delivered department", saved.Department);
        Assert.Equal("Engineer", saved.JobTitle);
    }

    private WorkspaceViewModel Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        return app;
    }

    private IApplicationWorkflow Reopen()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        return app;
    }

    private void Deliver(string email, string field, string? value)
    {
        using var provider = Reopen();
        if (field == "teamName")
        {
            Assert.True(provider.BeginProfileEdit(email));
            Assert.True(provider.StageProfileTeamName(value));
            Assert.True(provider.SaveProfileEdit(), provider.Status);
        }
        else Assert.True(provider.EditPersonText(email, field switch
        {
            "department" => PersonTextField.Department,
            "officeNumber" => PersonTextField.OfficeNumber,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        }, value), provider.Status);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
