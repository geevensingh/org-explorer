using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class NoteDraftPersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private const string Owner = "avery@example.com";
    private const string Population = """
        {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000001",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"avery@example.com","displayName":"Avery Example","managerEmail":null}]}
        """;

    [Fact]
    public void Failed_secondary_draft_retains_exact_owner_and_editor_when_ongoing_navigation_is_rejected()
    {
        using var app = Open();
        var ongoing = app.CreatePersonNote(Owner, "Ongoing", "Continuing writing")!;
        var secondary = app.CreatePersonNote(Owner, "Secondary", "Original secondary writing")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, Owner));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, ongoing.Id));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id, editing: true));
        var draft = app.BeginNoteDraft(NoteScope.Person, Owner, secondary.Id)!;
        var file = Path.Combine(folder, "notes", secondary.Id + ".md");
        app.RetainNoteDraft(draft);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Null(app.SaveNoteDraft(draft, "Retained secondary draft", "Unsaved writing"));
        Assert.False(app.OpenOngoingNote(NoteScope.Person));
        Assert.Equal(secondary.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal(Owner, app.PersonNotesView.OwnerEmail);
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.Contains("Retry or discard", app.PersonNotesView.Feedback);
        File.WriteAllText(file, File.ReadAllText(file).Replace("Original secondary writing", "Delivered secondary writing"));
        var delivered = File.ReadAllBytes(file);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Retained secondary draft", "Unsaved writing"));
        Assert.Equal(delivered, File.ReadAllBytes(file));
        Assert.Equal("Original secondary writing", draft.Markdown);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(secondary.Id);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id));
        Assert.Equal("Delivered secondary writing", app.PersonNotesView.SelectedNote!.Markdown);
        Assert.True(app.OpenOngoingNote(NoteScope.Person));
        Assert.Equal(ongoing.Id, app.PersonNotesView.SelectedNote!.Id);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void First_autosave_and_retry_reject_delivered_revision_without_losing_either_version(NoteScope scope)
    {
        using var app = Open();
        var id = scope == NoteScope.Person
            ? app.CreatePersonNote(Owner, "Context", "Original writing")!.Id
            : app.CreateTeamNote(Owner, "Context", "Original writing")!.Id;
        var draft = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner, id));
        const string localWriting = "Still typing my private draft";
        app.RetainNoteDraft(draft);
        Assert.False(app.CanSafelyClose);
        var restarted = false;
        Assert.False(app.RestartWhenSafe(_ => restarted = true));
        Assert.False(restarted);
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", id + ".md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Original writing", "Delivered writing"));
        var delivered = File.ReadAllBytes(file);
        Assert.True(app.RefreshWorkspace(), app.Status);

        Assert.Null(app.SaveNoteDraft(draft, "Local title", localWriting));
        Assert.Contains("changed externally", app.Status);
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Original writing", draft.Markdown);
        Assert.Equal(delivered, File.ReadAllBytes(file));
        Assert.Null(app.SaveNoteDraft(draft, "Local title", localWriting));
        Assert.Equal(delivered, File.ReadAllBytes(file));

        app.DiscardUnsavedChanges(id);
        Assert.True(app.CanSafelyClose);
        var accepted = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner, id));
        Assert.Equal("Delivered writing", accepted.Markdown);
        Assert.NotNull(app.SaveNoteDraft(accepted, "Deliberate edit", "Intentional delivered-version edit"));
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Intentional delivered-version edit",
            reopened.BeginNoteDraft(scope, Owner, id)!.Markdown);
    }

    private WorkspaceViewModel Open(IWorkspaceFilePublisher? publisher = null)
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(filePublisher: publisher));
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Population).Id), app.Status);
        return app;
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void New_note_draft_requires_absence_until_publication_and_discard_accepts_external_creation(NoteScope scope)
    {
        using var app = Open();
        var draft = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner));
        var directory = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, draft.Id + ".md");
        var confirmation = scope == NoteScope.Team
            ? ",\"confirmedContextRevision\":\"00000000-0000-0000-0000-000000000000\"" : "";
        var delivered = "---\r\n" + $$"""
            {"schemaVersion":1,"kind":"{{scope.ToString().ToLowerInvariant()}}","id":"{{draft.Id}}",
            "ownerEmail":"avery@example.com","title":"External creation","archived":false,
            "createdAt":"2026-11-01T17:00:00Z","updatedAt":"2026-11-01T17:00:00Z"{{confirmation}}}
            """ + "\r\n---\r\n# Delivered new document\r\n";
        File.WriteAllText(file, delivered);
        var bytes = File.ReadAllBytes(file);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Local creation", "Unsaved new writing"));
        Assert.Contains("changed externally", app.Status);
        Assert.Null(app.SaveNoteDraft(draft, "Local creation", "Unsaved new writing"));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(draft.Id);
        var accepted = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner, draft.Id));
        Assert.Equal("# Delivered new document\r\n", accepted.Markdown);
        Assert.NotNull(app.SaveNoteDraft(accepted, accepted.Title, "Deliberate edit of delivered creation"));
        var separate = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner));
        Assert.NotNull(app.SaveNoteDraft(separate, "Intentional new note", "Independent writing"));
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Independent writing", reopened.BeginNoteDraft(scope, Owner, separate.Id)!.Markdown);
        Assert.Equal("Deliberate edit of delivered creation", reopened.BeginNoteDraft(scope, Owner, draft.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Plain_IO_failure_retries_the_same_base_and_success_returns_the_base_for_newer_typing(NoteScope scope)
    {
        using var app = Open();
        var empty = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner));
        var original = Assert.IsType<NoteDraft>(app.SaveNoteDraft(empty, "Context", "Original"));
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", original.Id + ".md");
        var bytes = File.ReadAllBytes(file);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Null(app.SaveNoteDraft(original, "First edit", "First writing"));
        Assert.False(app.CanSafelyClose);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        var accepted = Assert.IsType<NoteDraft>(app.SaveNoteDraft(original, "First edit", "First writing"));
        Assert.True(app.CanSafelyClose);
        Assert.Equal("First writing", accepted.Markdown);
        var newer = Assert.IsType<NoteDraft>(app.SaveNoteDraft(accepted, "Newer edit", "Typed while saving"));
        Assert.Equal("Typed while saving", newer.Markdown);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Typed while saving", reopened.BeginNoteDraft(scope, Owner, original.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Delivered_deletion_before_first_autosave_is_not_recreated_by_the_retained_draft(NoteScope scope)
    {
        using var app = Open();
        var empty = Assert.IsType<NoteDraft>(app.BeginNoteDraft(scope, Owner));
        var original = Assert.IsType<NoteDraft>(app.SaveNoteDraft(empty, "Context", "Original"));
        var file = Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", original.Id + ".md");
        File.Delete(file);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Null(app.SaveNoteDraft(original, "Retained title", "Retained local writing"));
        Assert.Null(app.SaveNoteDraft(original, "Retained title", "Retained local writing"));
        Assert.Contains("changed externally", app.Status);
        Assert.False(File.Exists(file));
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(original.Id);
        Assert.True(app.CanSafelyClose);
        Assert.Null(app.BeginNoteDraft(scope, Owner, original.Id));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task Retaining_newer_typing_does_not_wait_behind_in_flight_IO_and_uses_the_successful_base(NoteScope scope)
    {
        using var publisher = new HeldPublisher();
        using var app = Open(publisher);
        var original = app.SaveNoteDraft(app.BeginNoteDraft(scope, Owner)!, "Context", "Original")!;
        app.RetainNoteDraft(original);
        publisher.HoldNextNote = true;
        var saving = Task.Run(() => app.SaveNoteDraft(original, "First edit", "First writing"));
        try
        {
            Assert.True(publisher.Entered.Wait(TimeSpan.FromSeconds(5)), "The real note publication did not start.");
            await Task.Run(() => app.RetainNoteDraft(original)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(app.CanSafelyClose);
        }
        finally { publisher.Release.Set(); }
        var accepted = Assert.IsType<NoteDraft>(await saving);
        app.RetainNoteDraft(accepted);
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.SaveNoteDraft(accepted, "Newer edit", "Typed while the first save was held"));
        Assert.True(app.CanSafelyClose);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Typed while the first save was held", reopened.BeginNoteDraft(scope, Owner, original.Id)!.Markdown);
    }

    [Fact]
    public void Delivered_Team_confirmation_change_alone_invalidates_the_first_dirty_base()
    {
        using var app = Open();
        var original = app.SaveNoteDraft(app.BeginNoteDraft(NoteScope.Team, Owner)!, "Context", "Original")!;
        app.RetainNoteDraft(original);
        var file = Path.Combine(folder, "team-notes", original.Id + ".md");
        File.WriteAllText(file, File.ReadAllText(file).Replace(
            "\"confirmedContextRevision\": \"00000000-0000-0000-0000-000000000000\"",
            "\"confirmedContextRevision\": \"12345678-0000-4000-8000-000000000001\""));
        var bytes = File.ReadAllBytes(file);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(Assert.Single(app.GetTeamNotes(Owner)).NeedsReview);
        Assert.Null(app.SaveNoteDraft(original, original.Title, "Retained local writing"));
        Assert.Contains("changed externally", app.Status);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        app.DiscardUnsavedChanges(original.Id);
        var accepted = app.BeginNoteDraft(NoteScope.Team, Owner, original.Id)!;
        Assert.NotNull(app.SaveNoteDraft(accepted, accepted.Title, "Explicitly confirmed writing"));
        Assert.False(Assert.Single(app.GetTeamNotes(Owner)).NeedsReview);
    }

    [Fact]
    public void Discarding_one_pending_scope_does_not_discard_the_other_and_global_discard_writes_neither()
    {
        using var app = Open();
        var person = app.SaveNoteDraft(app.BeginNoteDraft(NoteScope.Person, Owner)!, "Person", "Person writing")!;
        var team = app.SaveNoteDraft(app.BeginNoteDraft(NoteScope.Team, Owner)!, "Team", "Team writing")!;
        app.RetainNoteDraft(person);
        app.RetainNoteDraft(team);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges(person.Id);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Person writing", reopened.BeginNoteDraft(NoteScope.Person, Owner, person.Id)!.Markdown);
        Assert.Equal("Team writing", reopened.BeginNoteDraft(NoteScope.Team, Owner, team.Id)!.Markdown);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void New_note_draft_cannot_publish_after_valid_delivery_removes_its_owner(NoteScope scope)
    {
        using var app = Open();
        var draft = app.BeginNoteDraft(scope, Owner)!;
        app.RetainNoteDraft(draft);
        var delivery = folder + "-delivery";
        try
        {
            using (var source = ApplicationWorkflow.CreateFileBacked())
            {
                Assert.True(source.OpenWorkspace(delivery), source.Status);
                var preview = source.PreviewImport(Population.Replace("avery@example.com", "casey@example.com"));
                Assert.True(source.ApplyImport(preview.Id), source.Status);
            }
            foreach (var source in Directory.GetFiles(delivery, "*", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p) != ".workspace.lock")
                .OrderBy(p => Path.GetFileName(p) == "workspace.json"))
            {
                var target = Path.Combine(folder, Path.GetRelativePath(delivery, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }
            Assert.True(app.RefreshWorkspace(), app.Status);
            Assert.Equal("casey@example.com", Assert.Single(app.People).Email);
            Assert.Null(app.SaveNoteDraft(draft, "Retained title", "Retained local writing"));
            Assert.False(File.Exists(Path.Combine(folder, scope == NoteScope.Person ? "notes" : "team-notes", draft.Id + ".md")));
            Assert.Contains("changed externally", app.Status);
            Assert.False(app.CanSafelyClose);
            app.DiscardUnsavedChanges(draft.Id);
            Assert.True(app.CanSafelyClose);
        }
        finally { if (Directory.Exists(delivery)) Directory.Delete(delivery, true); }
    }

    private sealed class HeldPublisher : IWorkspaceFilePublisher, IDisposable
    {
        public bool HoldNextNote { get; set; }
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public void Publish(string staged, string destination, string? backup)
        {
            if (HoldNextNote && Path.GetExtension(destination) == ".md")
            {
                HoldNextNote = false;
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Controlled publication was not released.");
            }
            if (File.Exists(destination)) File.Replace(staged, destination, backup);
            else File.Move(staged, destination);
        }

        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
