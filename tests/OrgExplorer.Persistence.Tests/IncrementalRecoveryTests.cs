using System.Text.Json;
using System.Collections.Concurrent;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class IncrementalRecoveryTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    private const string Import = """
        {"schemaVersion":1,"batchId":"45000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"}]}
        """;

    [Fact]
    public void Recovery_opens_the_latest_protected_commit_after_current_documents_are_overwritten()
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        var current = Path.Combine(folder, "current");
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        var commit = Commit(current);
        var documents = WorkspaceFixtures.IndexedDocuments(current);
        File.WriteAllText(documents["people:avery@example.com"], "damaged current Person");
        File.WriteAllText(documents["profileHistory:avery@example.com"], "damaged current history");
        Assert.False(app.RefreshWorkspace());
        var restored = Path.Combine(folder, "restored");
        Assert.True(app.RecoverWorkspace(restored + Path.DirectorySeparatorChar), app.Status);
        Assert.Equal(commit, Commit(restored));
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal("morgan@example.com", app.GetRelationships("avery@example.com")!.Manager!.Email);
        Assert.Equal("Ave", Assert.Single(app.GetProfileTimeline("avery@example.com")!.Changes).NewValue.GetString());
        Assert.Contains("no-op", Assert.Single(app.PreviewImport(Import).Changes));
        Assert.Equal("damaged current Person", File.ReadAllText(documents["people:avery@example.com"]));
        Assert.False(app.CanSafelyClose);
        Assert.True(app.RetryStorageMaintenance(), app.Status);
        Assert.True(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(restored), reopened.Status);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    private static Guid Commit(string root)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "workspace.json")));
        return json.RootElement.GetProperty("commit").GetGuid();
    }

    [Fact]
    public void Recovery_rejects_nonempty_destinations_and_preserves_owned_profile_writing()
    {
        var current = Path.Combine(folder, "current");
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var before = WorkspaceFixtures.Bytes(current);
        var destination = Path.Combine(folder, "restored");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "Existing destination content");
        Assert.False(app.RecoverWorkspace(destination));
        Assert.Equal("Existing destination content", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained writing"), app.Status);
        var empty = Path.Combine(folder, "empty");
        Assert.False(app.RecoverWorkspace(empty));
        Assert.False(Directory.Exists(empty));
        Assert.Equal("Retained writing", app.ProfileEdit!.Draft.JobTitle);
        Assert.False(app.CanSafelyClose);
        foreach (var (path, data) in before) Assert.Equal(data, File.ReadAllBytes(Path.Combine(current, path)));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("selector")]
    public void Committed_maintenance_failure_retries_without_replaying_the_edit_even_after_reopen(string boundary)
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        var observer = new RecordingObserver();
        Guid commit;
        using (var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer))
        {
            Assert.True(app.OpenWorkspace(current), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
            var protectedSelector = File.ReadAllBytes(Path.Combine(current, "recovery", "current.json"));
            string? blockedReceipt = null;
            FileStream? held = null;
            publisher.BeforePublish = (staged, destination) =>
            {
                if (boundary == "receipt" && destination == Path.Combine(current, "workspace.json"))
                {
                    blockedReceipt = Path.Combine(Path.GetDirectoryName(staged)!, "committed.json");
                    Directory.CreateDirectory(blockedReceipt);
                }
                if (boundary == "selector" && destination == Path.Combine(current, "recovery", "current.json"))
                    held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            };
            try { Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status); }
            finally { held?.Dispose(); publisher.BeforePublish = null; }
            commit = Commit(current);
            Assert.Equal(protectedSelector, File.ReadAllBytes(Path.Combine(current, "recovery", "current.json")));
            Assert.Contains("Saved locally; recovery maintenance incomplete", app.LocalSaveError);
            Assert.DoesNotContain("NOT saved", app.Status);
            Assert.False(app.CanSafelyClose);
            app.DiscardUnsavedChanges();
            Assert.False(app.CanSafelyClose);
            if (blockedReceipt is not null) Directory.Delete(blockedReceipt);
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.True(reopened.OpenWorkspace(current), reopened.Status);
        Assert.False(reopened.CanSafelyClose);
        var bytes = WorkspaceFixtures.IndexedDocuments(current).ToDictionary(e => e.Value, e => File.ReadAllBytes(e.Value));
        observer.Effects.Clear();
        Assert.True(reopened.RetryStorageMaintenance(), reopened.Status);
        Assert.True(reopened.CanSafelyClose);
        Assert.Equal(commit, Commit(current));
        Assert.Single(reopened.GetProfileTimeline("avery@example.com")!.Changes);
        foreach (var (path, data) in bytes) Assert.Equal(data, File.ReadAllBytes(path));
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish &&
            e.Destination == Path.Combine(current, "workspace.json"));
        Assert.True(reopened.RecoverWorkspace(Path.Combine(folder, "restored")), reopened.Status);
        Assert.Equal(commit, Commit(Path.Combine(folder, "restored")));
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Single(reopened.GetProfileTimeline("avery@example.com")!.Changes);
    }

    [Fact]
    public void Note_only_commit_protects_actual_notes_and_photo_bytes_without_a_profile_commit_or_note_history()
    {
        var current = Path.Combine(folder, "current");
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        var photoFile = Path.Combine(folder, "source.png");
        File.WriteAllBytes(photoFile, photo);
        Assert.True(app.EditPhoto("avery@example.com", photoFile), app.Status);
        var commit = Commit(current);
        var person = app.CreatePersonNote("avery@example.com", "Private", "Protected person writing")!;
        var team = app.CreateTeamNote("morgan@example.com", "Context", "Protected team writing")!;
        Assert.NotNull(person);
        Assert.NotNull(team);
        Assert.Equal(commit, Commit(current));
        var documents = WorkspaceFixtures.IndexedDocuments(current);
        File.WriteAllText(documents.Single(e => e.Key.StartsWith("photos:")).Value, "damaged current photo");
        File.WriteAllText(Path.Combine(current, "notes", person.Id + ".md"), "damaged current note");
        Assert.False(app.RefreshWorkspace());
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal(commit, Commit(Path.Combine(folder, "restored")));
        Assert.Equal(photo, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal("Protected person writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Equal("Protected team writing", Assert.Single(app.GetTeamNotes("morgan@example.com")).Markdown);
        Assert.Single(app.GetProfileTimeline("avery@example.com")!.Changes);
    }

    [Fact]
    public void Note_receipt_failure_is_a_saved_note_and_same_session_retry_does_not_change_its_dates_or_current_bytes()
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher);
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        publisher.BeforePublish = (_, destination) =>
        {
            if (Path.GetFileName(destination) == "committed.json")
                throw new IOException("Receipt publication interrupted after note commit.");
        };
        var note = app.CreatePersonNote("avery@example.com", "Private", "Saved exactly once");
        Assert.NotNull(note);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("Saved locally; recovery maintenance incomplete", app.LocalSaveError);
        Assert.DoesNotContain("NOT saved", app.Status);
        var commit = Commit(current);
        var bytes = File.ReadAllBytes(Path.Combine(current, "notes", note.Id + ".md"));
        publisher.BeforePublish = null;
        var preview = app.PreviewImport(Import.Replace("45000000-0000-4000-8000-000000000001",
            "45000000-0000-4000-8000-000000000002"));
        Assert.True(preview.CanApply);
        Assert.True(app.RetryStorageMaintenance(), app.Status);
        Assert.True(app.CanSafelyClose);
        Assert.Equal(commit, Commit(current));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(current, "notes", note.Id + ".md")));
        Assert.Equal(note, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal(note, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Assert.Empty(app.GetProfileTimeline("avery@example.com")!.Changes);
    }

    [Fact]
    public void Maintenance_retry_protects_new_validated_delivery_instead_of_selecting_an_older_pending_candidate()
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher);
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        publisher.BeforePublish = (_, destination) =>
        {
            if (Path.GetFileName(destination) == "committed.json") throw new IOException("Receipt interrupted.");
        };
        var note = app.CreatePersonNote("avery@example.com", "Private", "Locally saved writing")!;
        publisher.BeforePublish = null;
        var notePath = Path.Combine(current, "notes", note.Id + ".md");
        var delivered = File.ReadAllText(notePath).Replace("Locally saved writing", "New external writing");
        File.WriteAllText(notePath, delivered);
        Assert.True(app.RetryStorageMaintenance(), app.Status);
        Assert.Equal(delivered, File.ReadAllText(notePath));
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal("New external writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
    }

    [Fact]
    public void Valid_external_delivery_automatically_protects_exact_bytes_before_safe_close()
    {
        var current = Path.Combine(folder, "current");
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Private", "Original writing")!;
        var notePath = Path.Combine(current, "notes", note.Id + ".md");
        var delivered = File.ReadAllText(notePath).Replace("Original writing", "Delivered exact writing\r\nwith CRLF");
        File.WriteAllText(notePath, delivered);

        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.CanSafelyClose, app.Status);
        Assert.Equal(delivered, File.ReadAllText(notePath));
        Assert.Empty(app.GetProfileTimeline("avery@example.com")!.Changes);
        var commit = Commit(current);
        File.WriteAllText(notePath, "damaged current writing");
        Assert.False(app.RefreshWorkspace());
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal(commit, Commit(Path.Combine(folder, "restored")));
        Assert.Equal(delivered, File.ReadAllText(Path.Combine(folder, "restored", "notes", note.Id + ".md")));
    }

    [Fact]
    public void Failed_automatic_delivery_protection_stays_explicit_until_intentional_maintenance_retry()
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher);
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Private", "Original writing")!;
        var notePath = Path.Combine(current, "notes", note.Id + ".md");
        var selected = File.ReadAllBytes(Path.Combine(current, "recovery", "current.json"));
        var attempts = 0;
        publisher.BeforePublish = (_, destination) =>
        {
            if (Path.GetFileName(destination) != "committed.json") return;
            attempts++;
            throw new IOException("Automatic delivery receipt interrupted.");
        };
        File.WriteAllText(notePath, File.ReadAllText(notePath).Replace("Original writing", "First delivery"));
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(1, attempts);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("Automatic delivery receipt interrupted", app.LocalSaveError);
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(current, "recovery", "current.json")));

        var delivered = File.ReadAllText(notePath).Replace("First delivery", "Latest delivery");
        File.WriteAllText(notePath, delivered);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(1, attempts);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("Automatic delivery receipt interrupted", app.LocalSaveError);
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(current, "recovery", "current.json")));
        publisher.BeforePublish = null;
        Assert.True(app.RetryStorageMaintenance(), app.Status);
        Assert.True(app.CanSafelyClose, app.Status);
        Assert.Equal(delivered, File.ReadAllText(notePath));
        File.WriteAllText(notePath, "damaged current writing");
        Assert.False(app.RefreshWorkspace());
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal(delivered, File.ReadAllText(Path.Combine(folder, "restored", "notes", note.Id + ".md")));
        Assert.Empty(app.GetProfileTimeline("avery@example.com")!.Changes);
    }

    [Fact]
    public void Accepted_external_note_bytes_and_removal_are_protected_without_inventing_profile_changes()
    {
        var current = Path.Combine(folder, "current");
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Private", "Original writing")!;
        var removed = app.CreateTeamNote("morgan@example.com", "Removed", "Removed externally")!;
        var notePath = Path.Combine(current, "notes", note.Id + ".md");
        var delivered = File.ReadAllText(notePath).Replace("Original writing", "Delivered exact writing\r\nwith CRLF");
        File.WriteAllText(notePath, delivered);
        File.Delete(Path.Combine(current, "team-notes", removed.Id + ".md"));
        var documents = WorkspaceFixtures.IndexedDocuments(current);
        var deliveredDocuments = new Dictionary<string, byte[]>();
        foreach (var identity in new[] { "people:morgan@example.com", "profileHistory:morgan@example.com" })
        {
            File.WriteAllText(documents[identity], "\r\n " + File.ReadAllText(documents[identity]) + "\r\n");
            deliveredDocuments[identity] = File.ReadAllBytes(documents[identity]);
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        var commit = Commit(current);
        Assert.True(app.RetryStorageMaintenance(), app.Status);
        Assert.Equal(commit, Commit(current));
        Assert.Empty(app.GetProfileTimeline("avery@example.com")!.Changes);
        Assert.True(app.EditFavorite("avery@example.com", true), app.Status);
        var protectedCommit = Commit(current);
        File.WriteAllText(notePath, "damaged current writing");
        Assert.False(app.RefreshWorkspace());
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal(protectedCommit, Commit(Path.Combine(folder, "restored")));
        Assert.Equal(delivered, File.ReadAllText(Path.Combine(folder, "restored", "notes", note.Id + ".md")));
        var restoredDocuments = WorkspaceFixtures.IndexedDocuments(Path.Combine(folder, "restored"));
        foreach (var (identity, bytes) in deliveredDocuments)
            Assert.Equal(bytes, File.ReadAllBytes(restoredDocuments[identity]));
        Assert.Equal("Delivered exact writing\r\nwith CRLF", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Empty(app.GetTeamNotes("morgan@example.com"));
        Assert.Equal("isFavorite", Assert.Single(app.GetProfileTimeline("avery@example.com")!.Changes).Field);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreceipted_or_damaged_recovery_is_rejected_without_writing_the_destination_or_source(bool damaged)
    {
        var current = Path.Combine(folder, "current");
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(app.OpenWorkspace(current), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        }
        var selected = Path.Combine(current, "recovery", "current.json");
        using var selector = JsonDocument.Parse(File.ReadAllBytes(selected));
        var transaction = selector.RootElement.GetProperty("transaction").GetGuid();
        var receipt = Path.Combine(current, "transactions", transaction.ToString("N"), "committed.json");
        if (damaged)
            File.WriteAllText(Directory.GetFiles(Path.Combine(current, "recovery", "documents", "people"), "*.json")[0],
                "damaged independent recovery Person");
        else File.Delete(receipt);
        File.WriteAllText(Path.Combine(current, "workspace.json"), "damaged current selector");
        var before = WorkspaceFixtures.Bytes(current);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(current));
        var destination = Path.Combine(folder, "restored");
        Assert.False(reopened.RecoverWorkspace(destination));
        Assert.Contains("Recovery was not opened", reopened.Status);
        Assert.False(Directory.Exists(destination));
        foreach (var (path, data) in before) Assert.Equal(data, File.ReadAllBytes(Path.Combine(current, path)));
    }

    private sealed class InspectingPublisher : IWorkspaceFilePublisher
    {
        public Action<string, string>? BeforePublish { get; set; }
        public void Publish(string staged, string destination, string? backup)
        {
            BeforePublish?.Invoke(staged, destination);
            if (File.Exists(destination)) File.Replace(staged, destination, backup);
            else File.Move(staged, destination);
        }
    }

    private sealed class RecordingObserver : IWorkspaceFileObserver
    {
        public ConcurrentQueue<WorkspaceFileEffect> Effects { get; } = new();
        public void Observe(WorkspaceFileEffect effect) => Effects.Enqueue(effect);
    }

    [Theory]
    [InlineData("recovery-object")]
    [InlineData("recovery-candidate")]
    [InlineData("current-publication")]
    public void Interrupted_uncommitted_work_is_never_selected_on_reopen(string boundary)
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        var observer = new RecordingObserver();
        using (var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer, filePublisher: publisher))
        {
            Assert.True(app.OpenWorkspace(current), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
            var commit = Commit(current);
            var selected = File.ReadAllBytes(Path.Combine(current, "recovery", "current.json"));
            var protectedBytes = Directory.GetFiles(Path.Combine(current, "recovery"), "*", SearchOption.AllDirectories)
                .ToDictionary(p => p, File.ReadAllBytes);
            FileStream? held = null;
            publisher.BeforePublish = (_, destination) =>
            {
                if (boundary == "recovery-object" && destination.Contains(Path.Combine("recovery", "documents")) ||
                    boundary == "recovery-candidate" && Path.GetFileName(destination) == "recovery.next")
                    throw new IOException("Deterministic interruption before current publication.");
                if (boundary == "current-publication" && destination == Path.Combine(current, "workspace.json"))
                    held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            };
            try { Assert.False(app.EditPreferredName("avery@example.com", "Uncommitted")); }
            finally { held?.Dispose(); publisher.BeforePublish = null; }
            Assert.False(app.CanSafelyClose);
            Assert.Contains("NOT saved", app.Status);
            Assert.Equal(commit, Commit(current));
            Assert.Equal(selected, File.ReadAllBytes(Path.Combine(current, "recovery", "current.json")));
            foreach (var (path, bytes) in protectedBytes) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(current), reopened.Status);
        Assert.True(reopened.CanSafelyClose);
        Assert.Null(reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Empty(reopened.GetProfileTimeline("avery@example.com")!.Changes);
        var commitBefore = Commit(current);
        Assert.True(reopened.RecoverWorkspace(Path.Combine(folder, "restored")), reopened.Status);
        Assert.Equal(commitBefore, Commit(Path.Combine(folder, "restored")));
        Assert.Null(reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Empty(reopened.GetProfileTimeline("avery@example.com")!.Changes);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
