using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class SearchFavoritePersistenceTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private string folder => Path.Combine(root, "workspace");
    private readonly List<IApplicationWorkflow> workflows = [];

    private IApplicationWorkflow Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        workflows.Add(app);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        return app;
    }

    private static void Apply(IApplicationWorkflow app, string records, int id)
    {
        var preview = app.PreviewImport($$"""
            {"schemaVersion":1,"batchId":"20000000-0013-4000-8000-{{id:000000000000}}",
             "observedAt":"2026-11-06T17:00:00Z","people":[{{records}}]}
            """);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    private IApplicationWorkflow Seed()
    {
        var app = Open();
        Apply(app, """
            {"email":"avery@example.com","displayName":"Avery Directory","preferredName":"Ave Preferred","alias":"avhandle",
             "jobTitle":"Scientist","department":"Research","officeBuilding":"Juniper","officeNumber":"007B",
             "officeState":"Ontario","officeCountry":"Canada","linkedInProfileUrl":"https://www.linkedin.com/in/fictitious-avery",
             "managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null,"isFavorite":true}
            """, 1);
        return app;
    }

    private void ChangeStoredFixture(string sql, string? local = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(local ?? folder, "workspace.sqlite"), Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Legacy_initialization_refuses_ordering_that_arrives_after_validation(int format)
    {
        var seed = Seed();
        Assert.True(seed.EditFavorite("avery@example.com", true), seed.Status);
        Assert.NotNull(seed.CreatePersonNote("avery@example.com", "Private", "Keep original writing"));
        seed.Dispose();
        ChangeStoredFixture("DROP TABLE favorite_order; UPDATE synchronization SET snapshot_format=1;");
        using var releasedRead = new ReleasedRead();
        using var reader = ApplicationWorkflow.CreateFileBacked(fileObserver: releasedRead);
        var opening = Task.Run(() => reader.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly));
        Dictionary<string, byte[]> before;
        try
        {
            Assert.True(releasedRead.Entered.Wait(TimeSpan.FromSeconds(5)), "Legacy validation did not release its read snapshot.");
            ChangeStoredFixture($$"""
                CREATE TABLE favorite_order (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                    format_version INTEGER NOT NULL,
                    sequence TEXT NOT NULL
                );
                INSERT INTO favorite_order VALUES(1, {{format}}, '["avery@example.com","alex@example.com"]');
                """);
            before = WorkspaceFixtures.Bytes(folder);
        }
        finally { releasedRead.Release.Set(); }

        Assert.False(await opening.WaitAsync(TimeSpan.FromSeconds(20)), reader.Status);
        foreach (var (path, expected) in before)
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(folder, path)));
        Assert.Empty(reader.FavoritePeople);
    }

    [Fact]
    public async Task Snapshot_adoption_refuses_future_local_order_before_replacing_private_content()
    {
        var writer = Seed();
        var note = writer.CreatePersonNote("avery@example.com", "Private", "Original private writing")!;
        var local = Path.Combine(root, "receiver");
        var transport = Path.Combine(root, "transport");
        using var releasedRead = new ReleasedRead { Armed = false, Phase = "snapshot.validate-commit" };
        using var receiver = ApplicationWorkflow.CreateFileBacked(fileObserver: releasedRead);
        Assert.True(receiver.OpenWorkspace(local), receiver.Status);
        WorkspaceFixtures.JoinSnapshot(writer, receiver, transport);
        var revision = receiver.Synchronization.LocalRevision;
        Assert.True(writer.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "Incoming office"), writer.Status);
        Assert.True(writer.EditPersonNote(note.Id, "Incoming", "Incoming private writing"), writer.Status);
        Assert.True(writer.EditFavorite("avery@example.com", true), writer.Status);
        Assert.True(writer.SyncNow(), writer.Synchronization.Error);
        var provider = WorkspaceFixtures.Bytes(transport);
        releasedRead.Armed = true;
        var syncing = Task.Run(receiver.SyncNow);
        try
        {
            Assert.True(releasedRead.Entered.Wait(TimeSpan.FromSeconds(5)), "Adoption did not release its validated local read.");
            ChangeStoredFixture("UPDATE favorite_order SET format_version=2;", local);
        }
        finally { releasedRead.Release.Set(); }

        Assert.False(await syncing.WaitAsync(TimeSpan.FromSeconds(20)), receiver.Status);
        Assert.Equal(revision, receiver.Synchronization.LocalRevision);
        Assert.Equal(["alex@example.com"], receiver.FavoritePeople.Select(person => person.Email));
        Assert.Equal("007B", receiver.GetRelationships("avery@example.com")!.Person.OfficeNumber);
        Assert.Equal("Original private writing", receiver.GetPersonNotes("avery@example.com").Single(item => item.Id == note.Id).Markdown);
        foreach (var (path, expected) in provider)
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(transport, path)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Unordered_legacy_content_remains_browsable_until_explicit_storage_recovery(
        bool blockedWal, bool unresolvedReceipt)
    {
        var seed = Seed();
        Assert.True(seed.EditFavorite("avery@example.com", true), seed.Status);
        var note = seed.CreatePersonNote("avery@example.com", "Private", "Read-only legacy writing")!;
        var original = seed.Synchronization.LocalRevision;
        var profiles = JsonSerializer.Serialize(seed.People);
        seed.Dispose();
        ChangeStoredFixture("DROP TABLE favorite_order; UPDATE synchronization SET snapshot_format=1;");
        var receipt = Path.Combine(folder, $"local-write-{Guid.NewGuid():N}.json");
        if (unresolvedReceipt) File.WriteAllText(receipt, "Unresolved original transaction");
        using var cold = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        using (var external = new SqliteConnection($"Data Source={Path.Combine(folder, "workspace.sqlite")};Pooling=False"))
        {
            external.Open();
            using var command = external.CreateCommand();
            SqliteTransaction? reader = null;
            try
            {
                if (blockedWal)
                {
                    command.CommandText = "PRAGMA journal_mode=DELETE;";
                    Assert.Equal("delete", command.ExecuteScalar());
                    reader = external.BeginTransaction(deferred: true);
                    command.Transaction = reader;
                    command.CommandText = "PRAGMA schema_version;";
                    command.ExecuteScalar();
                }
                Assert.True(cold.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), cold.Status);
                Assert.Equal(profiles, JsonSerializer.Serialize(cold.People));
                Assert.Equal(["alex@example.com", "avery@example.com"], cold.FavoritePeople.Select(person => person.Email));
                Assert.Equal("Read-only legacy writing", cold.GetPersonNotes("avery@example.com").Single(item => item.Id == note.Id).Markdown);
                Assert.Equal(original, cold.Synchronization.LocalRevision);
                Assert.True(cold.CanNavigatePeople);
                Assert.False(cold.CanEditLocalWorkspace);
                Assert.False(cold.ReorderFavorite("avery@example.com", "alex@example.com"));
                if (unresolvedReceipt) Assert.True(cold.LocalSave.HasUnresolvedOutcome);
                if (blockedWal) Assert.False(cold.ReconcileLocalSaves());
            }
            finally { reader?.Dispose(); }
        }
        Assert.True(cold.RefreshWorkspace(), cold.Status);
        Assert.Equal(original, cold.Synchronization.LocalRevision);
        Assert.False(cold.CanEditLocalWorkspace);
        if (unresolvedReceipt)
        {
            Assert.False(cold.ReconcileLocalSaves());
            Assert.Equal("Unresolved original transaction", File.ReadAllText(receipt));
            File.Delete(receipt);
        }
        Assert.True(cold.ReconcileLocalSaves(), cold.Status);
        Assert.True(cold.CanEditLocalWorkspace);
        var initialized = cold.Synchronization.LocalRevision;
        Assert.NotEqual(original, initialized);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), reopened.Status);
        Assert.Equal(initialized, reopened.Synchronization.LocalRevision);
        Assert.Equal(["alex@example.com", "avery@example.com"], reopened.FavoritePeople.Select(person => person.Email));
        Assert.Equal("Read-only legacy writing", reopened.GetPersonNotes("avery@example.com").Single(item => item.Id == note.Id).Markdown);
    }

    [Fact]
    public void Supported_unordered_Workspace_initializes_displayed_names_and_ties_once_without_losing_private_state()
    {
        var app = Seed();
        Assert.True(app.EditFavorite("avery@example.com", true));
        Assert.True(app.EditFavorite("morgan@example.com", true));
        Apply(app, """
            {"email":"z-tie@example.com","displayName":"Directory Z","preferredName":"Beta","managerEmail":null,"isFavorite":true},
            {"email":"a-tie@example.com","displayName":"Beta","managerEmail":null,"isFavorite":true},
            {"email":"cc-fallback@example.com","displayName":"Original","managerEmail":null,"isFavorite":true}
            """, 2);
        Assert.True(app.EditPersonText("cc-fallback@example.com", PersonTextField.DirectoryName, null));
        var note = app.CreatePersonNote("avery@example.com", "Private", "Exact private writing")!;
        var history = app.GetProfileTimeline("avery@example.com")!;
        app.Dispose();
        ChangeStoredFixture("DROP TABLE favorite_order;");

        var initialized = Open();
        Assert.Equal(["alex@example.com", "avery@example.com", "z-tie@example.com", "a-tie@example.com",
            "cc-fallback@example.com", "morgan@example.com"], initialized.FavoritePeople.Select(person => person.Email));
        Assert.Equal("Exact private writing", initialized.GetPersonNotes("avery@example.com").Single(item => item.Id == note.Id).Markdown);
        Assert.Equal(JsonSerializer.Serialize(history.Changes),
            JsonSerializer.Serialize(initialized.GetProfileTimeline("avery@example.com")!.Changes));
        Assert.Equal(JsonSerializer.Serialize(history.Observations),
            JsonSerializer.Serialize(initialized.GetProfileTimeline("avery@example.com")!.Observations));
        var revision = initialized.LocalSave.CommittedRevision;
        Assert.Equal(revision, Open().LocalSave.CommittedRevision);
        Assert.True(initialized.ReorderFavorite("morgan@example.com", "alex@example.com"), initialized.Status);
        Assert.Equal(["morgan@example.com", "alex@example.com", "avery@example.com", "z-tie@example.com",
            "a-tie@example.com", "cc-fallback@example.com"], Open().FavoritePeople.Select(person => person.Email));
    }

    [Fact]
    public void Initialized_empty_order_and_Workspace_switching_keep_later_additions_in_each_saved_sequence()
    {
        var app = Seed();
        Assert.True(app.EditFavorite("alex@example.com", false));
        app.Dispose();
        var reopened = Open();
        var emptyRevision = reopened.LocalSave.CommittedRevision;
        Assert.Empty(reopened.FavoritePeople);
        Assert.Equal(emptyRevision, Open().LocalSave.CommittedRevision);
        Assert.True(reopened.EditFavorite("morgan@example.com", true));
        Assert.True(reopened.EditFavorite("alex@example.com", true));
        var alternate = Path.Combine(root, "alternate");
        Assert.True(reopened.OpenWorkspace(alternate));
        Apply(reopened, """
            {"email":"alex@example.com","displayName":"Alex alternate","managerEmail":null,"isFavorite":true},
            {"email":"morgan@example.com","displayName":"Morgan alternate","managerEmail":null,"isFavorite":true}
            """, 8);
        Assert.Equal(["alex@example.com", "morgan@example.com"], reopened.FavoritePeople.Select(person => person.Email));
        Assert.True(reopened.OpenWorkspace(folder));
        Assert.Equal(["morgan@example.com", "alex@example.com"], reopened.FavoritePeople.Select(person => person.Email));
    }

    [Theory]
    [InlineData("DELETE FROM favorite_order;")]
    [InlineData("UPDATE favorite_order SET sequence='null';")]
    [InlineData("UPDATE favorite_order SET sequence='{}';")]
    [InlineData("UPDATE favorite_order SET sequence='[null]';")]
    [InlineData("UPDATE favorite_order SET sequence='[]';")]
    [InlineData("UPDATE favorite_order SET sequence='[\"alex@example.com\",\"ALEX@example.com\"]';")]
    [InlineData("UPDATE favorite_order SET sequence='[\"unknown@example.com\"]';")]
    [InlineData("UPDATE favorite_order SET sequence='[\"avery@example.com\"]';")]
    [InlineData("UPDATE favorite_order SET format_version=2;")]
    [InlineData("UPDATE synchronization SET snapshot_format=999;")]
    public void Malformed_or_unsupported_order_is_rejected_and_retains_the_last_valid_Favorites(string corruption)
    {
        var app = Seed();
        var note = app.CreatePersonNote("avery@example.com", "Private", "Retain exact note")!;
        ChangeStoredFixture(corruption);
        var bytes = WorkspaceFixtures.Bytes(folder);

        Assert.False(app.RefreshWorkspace());

        Assert.Equal(["alex@example.com"], app.FavoritePeople.Select(person => person.Email));
        Assert.Equal("Retain exact note", app.GetPersonNotes("avery@example.com").Single(item => item.Id == note.Id).Markdown);
        Assert.Contains("STALE", app.Status);
        Assert.False(app.EditFavorite("avery@example.com", true));
        Assert.False(app.ReorderFavorite("alex@example.com", null));
        foreach (var (path, expected) in bytes) Assert.Equal(expected, File.ReadAllBytes(Path.Combine(folder, path)));
        using var rejected = ApplicationWorkflow.CreateFileBacked();
        Assert.False(rejected.OpenWorkspace(folder));
        Assert.Empty(rejected.FavoritePeople);
    }

    [Fact]
    public void A_save_cannot_replace_malformed_order_that_arrived_after_the_last_validated_read()
    {
        var app = Seed();
        var revision = app.LocalSave.CommittedRevision;
        ChangeStoredFixture("UPDATE favorite_order SET sequence='[]';");
        var bytes = WorkspaceFixtures.Bytes(folder);

        Assert.False(app.EditFavorite("avery@example.com", true));

        Assert.Equal(["alex@example.com"], app.FavoritePeople.Select(person => person.Email));
        Assert.Equal(revision, app.LocalSave.CommittedRevision);
        Assert.Contains("Favorites order", app.LocalSaveError);
        foreach (var (path, expected) in bytes) Assert.Equal(expected, File.ReadAllBytes(Path.Combine(folder, path)));
    }

    [Fact]
    public void A_save_cannot_downgrade_future_snapshot_publication_metadata_after_the_last_validated_read()
    {
        var app = Seed();
        var revision = app.LocalSave.CommittedRevision;
        ChangeStoredFixture("UPDATE synchronization SET snapshot_format=999;");
        var bytes = WorkspaceFixtures.Bytes(folder);
        Assert.False(app.EditFavorite("avery@example.com", true));
        Assert.Equal(["alex@example.com"], app.FavoritePeople.Select(person => person.Email));
        Assert.Equal(revision, app.LocalSave.CommittedRevision);
        Assert.False(app.Compatibility.SupportsOrdinaryEdits);
        foreach (var (path, expected) in bytes) Assert.Equal(expected, File.ReadAllBytes(Path.Combine(folder, path)));
    }

    [Fact]
    public void Exact_Favorite_order_reopens_and_survives_profile_Save_Cancel_names_reporting_and_archival()
    {
        var app = Seed();
        Assert.True(app.EditFavorite("avery@example.com", true), app.Status);
        Assert.True(app.EditFavorite("morgan@example.com", true), app.Status);
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Unsubmitted"), app.Status);
        Assert.True(app.ReorderFavorite("morgan@example.com", "alex@example.com"), app.Status);
        Assert.Equal("Unsubmitted", app.ProfileEdit!.Draft.OfficeNumber);
        app.CancelProfileEdit();
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Committed profile"), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.EditPreferredName("morgan@example.com", "AAA"), app.Status);
        Assert.True(app.EditPersonText("alex@example.com", PersonTextField.DirectoryName, "ZZZ"), app.Status);
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"), app.Status);
        Assert.True(app.EditPersonArchived("morgan@example.com", true), app.Status);
        Assert.True(app.EditPersonArchived("morgan@example.com", false), app.Status);
        Assert.True(app.EditPersonArchived("avery@example.com", true), app.Status);

        var reopened = Open();
        Assert.Equal(["morgan@example.com", "alex@example.com", "avery@example.com"],
            reopened.FavoritePeople.Select(person => person.Email));
        Assert.Equal("Committed profile", reopened.GetRelationships("avery@example.com")!.Person.OfficeNumber);
        Assert.True(reopened.FavoritePeople[2].Archived);
        Assert.True(reopened.EditPersonArchived("avery@example.com", false), reopened.Status);
        var revision = reopened.LocalSave.CommittedRevision;
        Assert.True(reopened.ReorderFavorite("morgan@example.com", "morgan@example.com"));
        Assert.True(reopened.ReorderFavorite("morgan@example.com", "alex@example.com"));
        Assert.True(reopened.ReorderFavorite("avery@example.com", null));
        Assert.True(reopened.EditFavorite("morgan@example.com", true));
        Assert.False(reopened.ReorderFavorite("unknown@example.com", null));
        Assert.False(reopened.ReorderFavorite("morgan@example.com", "unknown@example.com"));
        Assert.Equal(revision, reopened.LocalSave.CommittedRevision);
        Assert.Equal(["morgan@example.com", "alex@example.com", "avery@example.com"],
            Open().FavoritePeople.Select(person => person.Email));
    }

    [Fact]
    public void Locked_reorder_retains_the_old_sequence_and_recovery_retry_preserves_independent_failed_and_unsaved_writing()
    {
        var app = (WorkspaceViewModel)Seed();
        Assert.True(app.EditFavorite("avery@example.com", true));
        Assert.True(app.EditFavorite("morgan@example.com", true));
        var note = app.CreatePersonNote("morgan@example.com", "Saved note", "Original writing")!;
        Assert.False(app.EditPersonNote(note.Id, "", "Independent failed writing"));
        var basis = app.GetRelationships("avery@example.com")!.Person;
        app.RetainPersonTextDraft(basis, PersonTextField.PreferredName, "Unsubmitted Preferred name");
        Assert.True(app.BeginProfileEdit("alex@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Unsubmitted profile"));
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            Assert.False(app.ReorderFavorite("morgan@example.com", "alex@example.com"));
            Assert.Equal(["alex@example.com", "avery@example.com", "morgan@example.com"],
                app.FavoritePeople.Select(person => person.Email));
            Assert.Equal(["alex@example.com", "avery@example.com", "morgan@example.com"],
                Open().FavoritePeople.Select(person => person.Email));
            Assert.Contains("NOT saved", app.LocalSaveError);
            Assert.False(app.CanSafelyClose);
            app.CancelProfileEdit();
            Assert.Contains("NOT saved", app.LocalSaveError);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.True(app.ReorderFavorite("morgan@example.com", "alex@example.com"), app.Status);
        Assert.Equal(["morgan@example.com", "alex@example.com", "avery@example.com"],
            Open().FavoritePeople.Select(person => person.Email));
        Assert.Equal("Unsubmitted Preferred name",
            app.GetPersonTextDraft("avery@example.com", PersonTextField.PreferredName)!.Value);
        Assert.Contains("title", app.LocalSaveError, StringComparison.OrdinalIgnoreCase);
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Original writing", app.GetPersonNotes("morgan@example.com").Single(item => item.Id == note.Id).Markdown);
        Assert.True(app.EditPersonNote(note.Id, "Saved after retry", "Independent failed writing"), app.Status);
        Assert.Null(app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_reorder_noop_clears_only_its_failure_after_another_client_saves(bool selfDestination)
    {
        var app = Seed();
        Assert.True(app.EditFavorite("avery@example.com", true), app.Status);
        Assert.True(app.EditFavorite("morgan@example.com", true), app.Status);
        var note = app.CreatePersonNote("morgan@example.com", "Private", "Original note")!;
        Assert.False(app.EditPersonNote(note.Id, "", "Independent failed note"));
        using (WorkspaceFixtures.LockLocalStore(folder))
            Assert.False(app.ReorderFavorite("morgan@example.com", "alex@example.com"));
        using (var writer = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(writer.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), writer.Status);
            Assert.True(writer.ReorderFavorite("morgan@example.com", "alex@example.com"), writer.Status);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        var revision = app.Synchronization.LocalRevision;
        Assert.True(app.ReorderFavorite("morgan@example.com", selfDestination ? "morgan@example.com" : "alex@example.com"), app.Status);
        Assert.Equal(revision, app.Synchronization.LocalRevision);
        Assert.Equal(["morgan@example.com", "alex@example.com", "avery@example.com"], app.FavoritePeople.Select(person => person.Email));
        Assert.Contains("title", app.LocalSaveError, StringComparison.OrdinalIgnoreCase);
        Assert.False(app.CanSafelyClose);
        Assert.True(app.EditPersonNote(note.Id, "Resolved", "Independent failed note"), app.Status);
        Assert.Null(app.LocalSaveError);
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Fact]
    public void Successful_Favorite_noop_clears_only_its_failure_after_another_client_saves()
    {
        var app = Seed();
        var note = app.CreatePersonNote("morgan@example.com", "Private", "Original note")!;
        Assert.False(app.EditPersonNote(note.Id, "", "Independent failed note"));
        using (WorkspaceFixtures.LockLocalStore(folder))
            Assert.False(app.EditFavorite("AVERY@example.com", true));
        using (var writer = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(writer.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), writer.Status);
            Assert.True(writer.EditFavorite("avery@example.com", true), writer.Status);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        var revision = app.Synchronization.LocalRevision;
        var changes = app.GetProfileTimeline("avery@example.com")!.Changes.Count;
        Assert.True(app.EditFavorite("AVERY@example.com", true), app.Status);
        Assert.Equal(revision, app.Synchronization.LocalRevision);
        Assert.Equal(changes, app.GetProfileTimeline("avery@example.com")!.Changes.Count);
        Assert.Contains("title", app.LocalSaveError, StringComparison.OrdinalIgnoreCase);
        Assert.False(app.CanSafelyClose);
        Assert.True(app.EditPersonNote(note.Id, "Resolved", "Independent failed note"), app.Status);
        Assert.Null(app.LocalSaveError);
        Assert.True(app.CanSafelyClose, app.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Writer_locked_legacy_initialization_retains_validated_browsing_until_explicit_recovery(bool emptySeed)
    {
        Guid? revision;
        Guid? noteId = null;
        if (emptySeed)
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "workspace-sqlite-v3-pristine-87c6ade.zip"), folder);
            revision = Guid.Parse("1f32abe5-7cc4-4d72-8722-b3edf1c564a3");
        }
        else
        {
            var seed = Seed();
            Assert.True(seed.EditFavorite("avery@example.com", true), seed.Status);
            noteId = seed.CreatePersonNote("avery@example.com", "Private", "Locked initialization keeps writing")!.Id;
            revision = seed.Synchronization.LocalRevision;
            seed.Dispose();
            ChangeStoredFixture("DROP TABLE favorite_order; UPDATE synchronization SET snapshot_format=1;");
        }
        using var reader = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            Assert.True(reader.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly), reader.Status);
            Assert.Equal(revision, reader.Synchronization.LocalRevision);
            Assert.True(reader.IsWorkspaceCurrent);
            Assert.False(reader.CanEditLocalWorkspace);
            Assert.False(reader.Compatibility.SupportsOrdinaryEdits);
            Assert.False(reader.ReconcileLocalSaves(), reader.Status);
            Assert.Equal(revision, reader.Synchronization.LocalRevision);
            Assert.True(reader.IsWorkspaceCurrent);
            Assert.Equal(LocalSavePhase.Failed, reader.LocalSave.Phase);
            Assert.Equal(LocalSaveOutcome.NotCommitted, reader.LocalSave.Outcome);
            Assert.False(reader.CanEditLocalWorkspace);
            Assert.False(reader.CanSafelyClose);
            Assert.False(reader.Compatibility.SupportsOrdinaryEdits);
            Assert.False(reader.Compatibility.SupportsNoteEdits);
            if (!emptySeed)
            {
                Assert.Equal(["alex@example.com", "avery@example.com"], reader.FavoritePeople.Select(person => person.Email));
                Assert.Equal("Locked initialization keeps writing",
                    reader.GetPersonNotes("avery@example.com").Single(item => item.Id == noteId).Markdown);
                Assert.True(reader.CanNavigatePeople);
            }
        }
        Assert.True(reader.ReconcileLocalSaves(), reader.Status);
        Assert.True(reader.CanEditLocalWorkspace);
        if (emptySeed)
        {
            Assert.Equal(revision, reader.Synchronization.LocalRevision);
            Assert.True(reader.ConfigureSynchronizationFolder(Path.Combine(root, "transport"), true), reader.Status);
        }
        else
        {
            Assert.NotEqual(revision, reader.Synchronization.LocalRevision);
            Assert.Equal("Locked initialization keeps writing",
                reader.GetPersonNotes("avery@example.com").Single(item => item.Id == noteId).Markdown);
        }
    }

    [Fact]
    public void Reopening_rebuilds_local_queries_favorites_and_attributed_current_archived_and_frozen_context()
    {
        var app = Seed();
        var direct = app.CreatePersonNote("avery@example.com", "Meeting",
            new string('a', 240) + "\nOrchid private Person writing\n" + new string('z', 240))!;
        var team = app.CreateTeamNote("morgan@example.com", "Former team", "Cedar former Team writing")!;
        app.CreateTeamNote("alex@example.com", "Current team", "Pine current Team writing");
        Assert.True(app.EditFavorite("avery@example.com", true));
        Apply(app, """{"email":"avery@example.com","officeCountry":"Canada"}""", 2);
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(app.EditTeamNote(team.Id, "Later title", "Willow later writing"));
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        app.Dispose();

        var reopened = Open();
        Assert.Equal(new[] { "alex@example.com", "avery@example.com" }, reopened.FavoritePeople.Select(p => p.Email).Order());
        foreach (var query in new[] { "Directory", "Preferred", "avery@example", "avhandle", "Scientist", "Research",
            "Juniper", "007B", "Ontario", "Canada", "fictitious-avery", "alex@example" })
            Assert.Contains(reopened.Search(query), r => r.Person.Email == "avery@example.com" &&
                r.Source == SearchSource.Profile && r.Context == SearchContext.Archived);
        var directMatch = Assert.Single(reopened.Search("orchid"));
        Assert.Equal(direct.Id, directMatch.NoteId);
        Assert.True(directMatch.Person.Archived);
        Assert.Equal(SearchSource.PersonNote, directMatch.Source);
        Assert.Contains("Orchid private Person writing", directMatch.Excerpt);
        Assert.True(directMatch.Excerpt.Length < 200);
        Assert.Equal(new string('a', 240) + "\nOrchid private Person writing\n" + new string('z', 240),
            reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == direct.Id).Markdown);
        var frozen = Assert.Single(reopened.Search("cedar"));
        Assert.Equal(capture.Id, frozen.HistoricalContextId);
        Assert.Equal(team.Id, frozen.NoteId);
        Assert.Equal("morgan@example.com", frozen.NoteOwner!.Email);
        Assert.Equal("Cedar former Team writing", frozen.MatchText);
        Assert.Contains(reopened.Search("pine"), r => r.Person.Email == "avery@example.com" && r.Context == SearchContext.Historical);
        Assert.DoesNotContain(reopened.Search("willow"), r => r.Person.Email == "avery@example.com");
        Assert.Empty(reopened.GetInheritedTeamNotes("avery@example.com"));
        Assert.True(reopened.EditPersonArchived("avery@example.com", false));
        Assert.Contains(reopened.Search("pine"), r => r.Source == SearchSource.InheritedTeamNote && r.Context == SearchContext.Current);
        Apply(reopened, """{"email":"avery@example.com","isFavorite":false}""", 3);
        Assert.Equal("alex@example.com", Assert.Single(Open().FavoritePeople).Email);
        Assert.True(reopened.EditFavorite("alex@example.com", false));
        Assert.Empty(Open().FavoritePeople);
    }

    [Fact]
    public void Validated_external_delivery_rebuilds_queries_and_favorites_without_rewriting_provider_files()
    {
        var writer = Seed();
        var personNote = writer.CreatePersonNote("avery@example.com", "Individual", "Original orchid")!;
        var teamNote = writer.CreateTeamNote("morgan@example.com", "Team", "Original cedar")!;
        var reader = Open();
        Assert.Contains(reader.Search("cedar"), r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.InheritedTeamNote);
        Assert.True(writer.EditFavorite("avery@example.com", true), writer.Status);
        Apply(writer, """{"email":"alex@example.com","isFavorite":false}""", 2);
        Assert.True(writer.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "0009C"), writer.Status);
        Assert.True(writer.EditPersonNote(personNote.Id, "Revised", "Revised orchid"), writer.Status);
        Assert.True(writer.EditTeamNote(teamNote.Id, "Revised Team", "Revised cedar"), writer.Status);
        Assert.True(SpinWait.SpinUntil(() => reader.RefreshWorkspace() && reader.FavoritePeople.Count == 1 && reader.FavoritePeople[0].Email == "avery@example.com" &&
            reader.Search("0009C").Count > 0 && reader.Search("revised orchid").Count == 1 &&
            reader.Search("revised cedar").Count == 2, TimeSpan.FromSeconds(10)), reader.Status);
        Assert.Empty(reader.Search("original orchid"));
        Assert.DoesNotContain(reader.Search("007B"), r => r.Context == SearchContext.Current);
        Assert.Contains(reader.Search("007B"), r => r.Source == SearchSource.ProfileHistory);
        Assert.True(writer.EditPersonManager("avery@example.com", "alex@example.com"));
        Assert.True(SpinWait.SpinUntil(() => reader.RefreshWorkspace() && reader.Search("revised cedar").Any(r => r.Source == SearchSource.HistoricalTeamNote),
            TimeSpan.FromSeconds(10)), reader.Status);
        Assert.DoesNotContain(reader.Search("cedar"), r => r.Source == SearchSource.InheritedTeamNote);
        var bytes = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);
        Assert.True(reader.RefreshWorkspace());
        for (var i = 0; i < 3; i++) Assert.Contains(reader.Search("cedar"), r => r.Source == SearchSource.HistoricalTeamNote);
        Assert.Single(reader.GetHistoricalTeamContexts("avery@example.com"));
        foreach (var (path, original) in bytes) Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Failed_favorite_save_retains_saved_queries_until_recovery_and_retry_and_import_omission_defaults_to_false()
    {
        var app = Seed();
        using (WorkspaceFixtures.LockLocalStore(folder))
        {
            Assert.False(app.EditFavorite("avery@example.com", true));
            Assert.False(app.CanSafelyClose);
            Assert.Contains("NOT saved", app.LocalSaveError);
            Assert.Equal("alex@example.com", Assert.Single(app.FavoritePeople).Email);
            Assert.Equal("alex@example.com", Assert.Single(Open().FavoritePeople).Email);
            Assert.Empty(app.GetProfileTimeline("avery@example.com")!.Changes);
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Assert.True(app.EditFavorite("avery@example.com", true), app.Status);
        Assert.True(app.CanSafelyClose, app.Status);
        Assert.Equal(2, Open().FavoritePeople.Count);
        Assert.True(app.EditFavorite("avery@example.com", false), app.Status);
        app.Dispose();
        Assert.False(Open().GetRelationships("morgan@example.com")!.Person.IsFavorite);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"false\"")]
    [InlineData("false")]
    public void Invalid_snapshot_favorite_or_inconsistent_evidence_retains_last_valid_search_and_favorites(string invalid)
    {
        var app = Seed();
        using var receiver = ApplicationWorkflow.CreateFileBacked();
        Assert.True(receiver.OpenWorkspace(Path.Combine(root, "receiver")), receiver.Status);
        WorkspaceFixtures.JoinSnapshot(app, receiver, Path.Combine(root, "transport"));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "Next revision"));
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        var file = WorkspaceFixtures.LatestSnapshot(app);
        var original = File.ReadAllBytes(file);
        WorkspaceFixtures.RewriteSnapshot(file, content => WorkspaceFixtures.EditSnapshotRow(content,
            "people", "alex@example.com", person => person["isFavorite"] = JsonNode.Parse(invalid)));
        var rejected = File.ReadAllBytes(file);
        Assert.False(receiver.SyncNow());
        Assert.Equal("alex@example.com", Assert.Single(receiver.FavoritePeople).Email);
        Assert.Contains(receiver.Search("alex"), r => r.Person.IsFavorite);
        Assert.Equal(rejected, File.ReadAllBytes(file));
        File.WriteAllBytes(file, original);
        Assert.True(receiver.SyncNow(), receiver.Synchronization.Error);
        Assert.Equal("alex@example.com", Assert.Single(receiver.FavoritePeople).Email);
    }

    private sealed class ReleasedRead : IWorkspaceFileObserver, IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal bool Armed { get; set; } = true;
        internal string Phase { get; set; } = "local-sqlite-read-snapshot";
        private bool observed;
        private bool localRead;
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (!Armed || observed || phase.Kind != WorkspaceStoragePhaseKind.End) return;
            if (phase.Name == "snapshot.local-read") localRead = true;
            if (phase.Name != Phase || Phase == "snapshot.validate-commit" && !localRead) return;
            observed = true;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("The owning legacy test did not release its completed read.");
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var app in workflows) app.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
