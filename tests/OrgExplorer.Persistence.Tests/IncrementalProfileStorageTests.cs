using System.Collections.Concurrent;
using System.Text.Json;
using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Persistence.Tests;

public sealed class IncrementalProfileStorageTests(ITestOutputHelper output) : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Fact]
    public void New_workspace_saves_profile_text_and_favorite_and_reopens_evidence_and_relationships()
    {
        var observer = new RecordingObserver();
        using (var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer))
        {
            Assert.True(app.OpenWorkspace(folder), app.Status);
            Assert.Empty(app.People);
            Assert.True(File.Exists(Path.Combine(folder, "workspace.json")));
            using var pointer = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "workspace.json")));
            Assert.Equal(3, pointer.RootElement.GetProperty("schemaVersion").GetInt32());
            Seed(app);
            var acceptedPath = WorkspaceFixtures.IndexedDocuments(folder)
                ["acceptedBatches:42000000-0000-4000-8000-000000000001"];
            using (var accepted = JsonDocument.Parse(File.ReadAllBytes(acceptedPath)))
            using (var original = JsonDocument.Parse(Import))
            {
                var batch = accepted.RootElement.GetProperty("42000000-0000-4000-8000-000000000001");
                Assert.True(JsonElement.DeepEquals(original.RootElement, batch.GetProperty("source")));
                Assert.Equal(app.GetProfileTimeline("avery@example.com")!.Confirmations[0].RecordedAt,
                    batch.GetProperty("appliedAt").GetDateTimeOffset());
            }
            observer.Effects.Clear();
            Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
            Assert.True(app.EditPersonText("avery@example.com", PersonTextField.JobTitle, "Principal Engineer"), app.Status);
            Assert.True(app.EditFavorite("avery@example.com", true), app.Status);
            Assert.Equal(acceptedPath, WorkspaceFixtures.IndexedDocuments(folder)
                ["acceptedBatches:42000000-0000-4000-8000-000000000001"]);
            Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Write &&
                e.Path.StartsWith(Path.Combine(folder, "documents", "acceptedBatches")));
            Assert.True(app.CanSafelyClose);
            Assert.All(observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write),
                write => Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == write.Path));
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var relationships = reopened.GetRelationships("avery@example.com")!;
        Assert.Equal("Ave", relationships.Person.Heading);
        Assert.Equal("Principal Engineer", relationships.Person.JobTitle);
        Assert.True(relationships.Person.IsFavorite);
        Assert.Equal("morgan@example.com", relationships.Manager!.Email);
        Assert.Equal("Morgan", relationships.Manager.DirectoryName);
        var timeline = reopened.GetProfileTimeline("avery@example.com")!;
        Assert.Collection(timeline.Changes,
            c => { Assert.Equal("preferredName", c.Field); Assert.Null(c.OldValue.GetString()); Assert.Equal("Ave", c.NewValue.GetString()); },
            c => { Assert.Equal("jobTitle", c.Field); Assert.Equal("Engineer", c.OldValue.GetString()); Assert.Equal("Principal Engineer", c.NewValue.GetString()); },
            c => { Assert.Equal("isFavorite", c.Field); Assert.False(c.OldValue.GetBoolean()); Assert.True(c.NewValue.GetBoolean()); });
        Assert.All(timeline.Changes, c => Assert.Equal(ProfileChangeSource.LocalEdit, c.Evidence.Source));
        var imported = Assert.Single(timeline.Confirmations, e => e.Field == "displayName");
        Assert.Equal(ProfileChangeSource.Import, imported.Source);
        Assert.Equal(Guid.Parse("42000000-0000-4000-8000-000000000001"), imported.BatchId);
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(Import).Changes));
    }

    private const string Import = """
        {"schemaVersion":1,"batchId":"42000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"}]}
        """;

    private static void Seed(IApplicationWorkflow app) =>
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);

    [Fact]
    public void Reviewed_population_import_publishes_once_without_repeatedly_serializing_shared_index_paths()
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer, filePublisher: publisher);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Seed(app);
        var import = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, batchId = "43000000-0000-4000-8000-000000000001",
            observedAt = "2026-11-03T17:00:00Z",
            people = Enumerable.Range(0, 128).Select(i => new
            {
                email = $"report{i:D3}@example.com", displayName = $"Report {i:D3}",
                managerEmail = i == 0 ? "morgan@example.com" : "report000@example.com"
            })
        });
        var preview = app.PreviewImport(import);
        Assert.True(preview.CanApply, string.Join("\n", preview.Errors));
        observer.Effects.Clear();
        var publications = 0;
        publisher.BeforePublish = (_, destination) =>
        {
            if (destination != Path.Combine(folder, "workspace.json")) return;
            publications++;
            using var before = ApplicationWorkflow.CreateFileBacked();
            Assert.True(before.OpenWorkspace(folder), before.Status);
            Assert.Equal(2, before.People.Count);
            Assert.DoesNotContain(before.GetRelationships("morgan@example.com")!.ImportedReports, p => p.Email.StartsWith("report"));
        };
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        publisher.BeforePublish = null;
        Assert.Equal(1, publications);
        var indexBytes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write &&
            e.Path.StartsWith(Path.Combine(folder, "index") + Path.DirectorySeparatorChar)).Sum(e => e.Bytes);
        output.WriteLine($"128-Person Import index writes: {indexBytes} bytes.");
        Assert.True(indexBytes < 128 * 700, $"Import repeatedly rewrote shared index paths: {indexBytes} bytes.");
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(130, reopened.People.Count);
        Assert.Equal(127, reopened.GetRelationships("report000@example.com")!.ImportedReports.Count);
        Assert.Equal("morgan@example.com", reopened.GetRelationships("report000@example.com")!.Manager!.Email);
        Assert.Equal("report000@example.com", reopened.GetRelationships("report127@example.com")!.Manager!.Email);
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(import).Changes));
        var update = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, batchId = "43000000-0000-4000-8000-000000000004",
            observedAt = "2026-11-04T17:00:00Z",
            people = Enumerable.Range(0, 128).Select(i => new { email = $"report{i:D3}@example.com", officeNumber = "007B" })
        });
        observer.Effects.Clear();
        Assert.True(app.ApplyImport(app.PreviewImport(update).Id), app.Status);
        var updateBytes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write &&
            e.Path.StartsWith(Path.Combine(folder, "index") + Path.DirectorySeparatorChar)).Sum(e => e.Bytes);
        Assert.True(updateBytes < 128 * 700, $"Updating indexed People rewrote shared paths: {updateBytes} bytes.");
        using var updated = ApplicationWorkflow.CreateFileBacked();
        Assert.True(updated.OpenWorkspace(folder), updated.Status);
        Assert.Equal(127, updated.GetRelationships("report000@example.com")!.ImportedReports.Count);
        Assert.All(updated.People.Where(p => p.Email.StartsWith("report")), p => Assert.Equal("007B", p.OfficeNumber));
        Assert.Equal("Engineer", updated.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Contains("no-op", Assert.Single(updated.PreviewImport(import).Changes));
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("archive")]
    [InlineData("reactivate")]
    [InlineData("favorite")]
    public void Relationship_archival_and_favorite_writes_reuse_unaffected_documents_and_reopen_expected_context(string mutation)
    {
        var observer = new RecordingObserver();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Seed(app);
        const string extra = """
            {"schemaVersion":1,"batchId":"43000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"alex@example.com","displayName":"Alex","managerEmail":null},
             {"email":"unrelated@example.com","displayName":"Unrelated","managerEmail":null}]}
            """;
        Assert.True(app.ApplyImport(app.PreviewImport(extra).Id), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Private", "Private writing")!;
        app.CreateTeamNote("morgan@example.com", "Former team", "Original context");
        if (mutation == "reactivate") Assert.True(app.EditPersonArchived("avery@example.com", true), app.Status);
        var before = WorkspaceFixtures.IndexedDocuments(folder);
        var bytes = before.ToDictionary(e => e.Key, e => File.ReadAllBytes(e.Value));
        observer.Effects.Clear();
        Assert.True(mutation switch
        {
            "manager" => app.EditPersonManager("avery@example.com", "alex@example.com"),
            "archive" => app.EditPersonArchived("avery@example.com", true),
            "reactivate" => app.EditPersonArchived("avery@example.com", false),
            _ => app.EditFavorite("avery@example.com", true)
        }, app.Status);
        var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        Assert.NotEmpty(writes);
        Assert.True(writes.Sum(e => e.Bytes) < 32768, $"Small {mutation} wrote {writes.Sum(e => e.Bytes)} bytes.");
        var publications = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Publish)
            .ToDictionary(e => e.Path, e => e.Destination!);
        var current = WorkspaceFixtures.IndexedDocuments(folder);
        foreach (var (key, path) in before.Where(e => !e.Key.EndsWith(":avery@example.com")))
        {
            Assert.Equal(path, current[key]);
            Assert.Equal(bytes[key], File.ReadAllBytes(path));
            Assert.DoesNotContain(writes, e => e.Path == path ||
                File.Exists(publications.GetValueOrDefault(e.Path, e.Path)) &&
                File.ReadAllBytes(publications.GetValueOrDefault(e.Path, e.Path)).AsSpan().SequenceEqual(bytes[key]));
        }
        Assert.DoesNotContain(writes, e => e.Path.EndsWith(".md"));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var person = reopened.GetRelationships("avery@example.com")!.Person;
        Assert.Equal(mutation == "manager" ? "alex@example.com" : "morgan@example.com", person.ManagerEmail);
        Assert.Equal(mutation == "archive", person.Archived);
        Assert.Equal(mutation == "favorite", person.IsFavorite);
        Assert.Equal(note, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        if (mutation == "favorite")
            Assert.Empty(reopened.GetHistoricalTeamContexts("avery@example.com"));
        else
            Assert.Equal("Original context", Assert.Single(Assert.Single(
                reopened.GetHistoricalTeamContexts("avery@example.com")).TeamNotes).Markdown);
        Assert.Equal(mutation == "reactivate" ? 2 : 1, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(extra).Changes));
    }

    [Fact]
    public void Profile_save_reuses_rich_unrelated_data_and_write_cost_does_not_follow_population_size()
    {
        var large = MeasureEdit(Path.Combine(folder, "large"), 256);
        var small = MeasureEdit(Path.Combine(folder, "small"), 4);
        output.WriteLine($"Same Preferred-name edit: 6 People = {small.Total} bytes ({small.Provenance} provenance); " +
            $"258 People = {large.Total} bytes ({large.Provenance} provenance), including all transient writes.");
        // #46 adds staging-path inventories and retirement evidence for affected
        // index paths, not population snapshots. Cap that provenance delta too.
        Assert.True(large.Provenance <= small.Provenance + 4096,
            $"Affected-path provenance grew from {small.Provenance} to {large.Provenance} bytes.");
        Assert.True(large.Total - large.Provenance <= small.Total - small.Provenance + 8192,
            "Non-provenance writes exceeded the original incremental allowance.");
        Assert.True(large.Total <= small.Total + 8192 + 4096,
            $"Same edit wrote {small.Total} bytes with 6 People and {large.Total} bytes with 258 People.");
        Assert.True(large.Total < 32768, $"A single profile edit wrote {large.Total} bytes, including all transient metadata.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_publication_failure_retains_profile_draft_and_prior_commit_until_retry_or_discard(bool discard)
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer, filePublisher: publisher);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Seed(app);
        var pointer = File.ReadAllBytes(Path.Combine(folder, "workspace.json"));
        var documents = WorkspaceFixtures.IndexedDocuments(folder).ToDictionary(e => e.Value, e => File.ReadAllBytes(e.Value));
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained draft"), app.Status);
        FileStream? held = null;
        publisher.BeforePublish = (_, destination) =>
        {
            if (destination == Path.Combine(folder, "workspace.json"))
                held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        observer.Effects.Clear();
        try { Assert.False(app.SaveProfileEdit()); }
        finally { held?.Dispose(); }
        publisher.BeforePublish = null;
        Assert.Equal("Retained draft", app.ProfileEdit!.Draft.JobTitle);
        Assert.False(app.CanSafelyClose);
        Assert.NotNull(app.LocalSaveError);
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(folder, "workspace.json")));
        foreach (var (path, bytes) in documents) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Write);
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish &&
            e.Destination == Path.Combine(folder, "workspace.json"));
        using (var prior = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(prior.OpenWorkspace(folder), prior.Status);
            Assert.Equal("Engineer", prior.GetRelationships("avery@example.com")!.Person.JobTitle);
            Assert.Empty(prior.GetProfileTimeline("avery@example.com")!.Changes);
        }
        if (discard) app.CancelProfileEdit();
        else Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.Null(app.ProfileEdit);
        Assert.True(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(discard ? "Engineer" : "Retained draft", reopened.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Equal(discard ? 0 : 1, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
    }

    [Fact]
    public void Text_draft_retry_and_complete_profile_text_save_use_the_existing_view_model_seam()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Seed(app);
        var basis = app.GetRelationships("avery@example.com")!.Person;
        using (File.Open(Path.Combine(folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Ave"));
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RestartWhenSafe(_ => Assert.Fail("Unsaved writing must block restart.")));
        var saved = app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Ave");
        Assert.NotNull(saved);
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        var edits = new Dictionary<PersonTextField, string>
        {
            [PersonTextField.DirectoryName] = "Avery Directory",
            [PersonTextField.Alias] = "avery",
            [PersonTextField.JobTitle] = "Principal Engineer",
            [PersonTextField.Department] = "Research",
            [PersonTextField.OfficeBuilding] = "Building 4",
            [PersonTextField.OfficeNumber] = "009",
            [PersonTextField.OfficeState] = "WA",
            [PersonTextField.OfficeCountry] = "US",
            [PersonTextField.LinkedInProfileUrl] = "https://www.linkedin.com/in/avery"
        };
        foreach (var (field, value) in edits) Assert.True(app.StageProfileText(field, value), app.Status);
        Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.True(app.CanSafelyClose);
        app.Dispose();
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        var person = reopened.GetRelationships("avery@example.com")!.Person;
        Assert.Equal("Ave", person.PreferredName);
        Assert.Equal("Avery Directory", person.DirectoryName);
        Assert.Equal("avery", person.Alias);
        Assert.Equal("Principal Engineer", person.JobTitle);
        Assert.Equal("Research", person.Department);
        Assert.Equal("Building 4", person.OfficeBuilding);
        Assert.Equal("009", person.OfficeNumber);
        Assert.Equal("WA", person.OfficeState);
        Assert.Equal("US", person.OfficeCountry);
        Assert.Equal("https://www.linkedin.com/in/avery", person.LinkedInProfileUrl);
        Assert.Equal(10, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void Unsupported_Workspace_versions_are_rejected_without_creating_or_changing_any_files_or_directories(int version)
    {
        WorkspaceFixtures.CopyVersion(version, folder);
        var before = WorkspaceFixtures.Bytes(folder);
        var directories = Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).Order().ToArray();
        using var ordinary = ApplicationWorkflow.CreateFileBacked();
        Assert.False(ordinary.OpenWorkspace(folder));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, ordinary.Compatibility.State);
        Assert.Equal(version, ordinary.Compatibility.DataVersion);
        Assert.Equal(3, ordinary.Compatibility.SupportedWorkspaceVersion);
        Assert.Contains(version < 3 ? "does not migrate" : "Upgrade", ordinary.Compatibility.Guidance);
        Assert.False(ordinary.RefreshWorkspace());
        Assert.False(ordinary.EditPreferredName("avery@example.com", "Never saved"));
        Assert.False(ordinary.RetryStorageMaintenance());
        Assert.False(File.Exists(Path.Combine(folder, ".workspace.lock")));
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        Assert.Equal(directories, Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public void Receipt_failure_reports_committed_profile_separately_and_blocks_safe_close()
    {
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Seed(app);
        publisher.BeforePublish = (staged, destination) =>
        {
            if (destination == Path.Combine(folder, "workspace.json"))
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(staged)!, "committed.json"));
        };
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        Assert.Contains("Saved locally", app.Status);
        Assert.Contains("maintenance incomplete", app.LocalSaveError);
        Assert.DoesNotContain("NOT saved", app.Status);
        Assert.False(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Single(reopened.GetProfileTimeline("avery@example.com")!.Changes);
    }

    private (long Total, long Provenance) MeasureEdit(string root, int unrelatedCount)
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using (var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer, filePublisher: publisher))
        {
            Assert.True(app.OpenWorkspace(root), app.Status);
            Seed(app);
            var records = Enumerable.Range(0, unrelatedCount).Select(i => new
            {
                email = $"person{i:D4}@example.com", displayName = $"Unrelated Person {i:D4}",
                managerEmail = "morgan@example.com", department = new string('d', 128)
            }).ToArray();
            var import = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, batchId = "42000000-0000-4000-8000-000000000002",
                observedAt = "2026-11-02T17:00:00Z", people = records
            });
            Assert.True(app.ApplyImport(app.PreviewImport(import).Id), app.Status);
            Assert.True(app.EditPersonText("morgan@example.com", PersonTextField.Department, "Research"), app.Status);
            var note = app.CreatePersonNote("morgan@example.com", "Private", "Unrelated personal writing")!;
            var team = app.CreateTeamNote("morgan@example.com", "Team", "Unrelated team writing")!;
            var image = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
            var sourcePhoto = Path.Combine(root, "source.png");
            File.WriteAllBytes(sourcePhoto, image);
            Assert.True(app.RefreshWorkspace(), app.Status);
            Assert.True(app.EditPhoto("morgan@example.com", sourcePhoto), app.Status);
            var before = WorkspaceFixtures.Bytes(root);
            var recoveryBefore = WorkspaceFixtures.RecoveryDocuments(root);
            observer.Effects.Clear();
            publisher.BeforePublish = (staged, destination) =>
            {
                var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
                Assert.NotEmpty(writes);
                // This is a byte bound on real writes, including transient stages, not surviving size.
                Assert.True(writes.Sum(e => e.Bytes) < 32768, $"Profile publication staged {writes.Sum(e => e.Bytes)} bytes.");
                Assert.Contains(writes, write => write.Path == staged);
                Assert.Contains(observer.Effects,
                    e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == staged);
                if (destination == Path.Combine(root, "workspace.json") ||
                    destination == Path.Combine(root, "recovery", "current.json"))
                    Assert.All(writes, write => Assert.Contains(observer.Effects,
                        e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == write.Path));
            };
            Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
            publisher.BeforePublish = null;
            var written = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
            Assert.True(written.Sum(e => e.Bytes) < 32768, $"Profile save wrote {written.Sum(e => e.Bytes)} bytes.");
            Assert.All(written, write => Assert.Contains(observer.Effects,
                e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == write.Path));
            foreach (var group in written.GroupBy(e => Path.GetRelativePath(root, e.Path).Split(Path.DirectorySeparatorChar)[0]))
                output.WriteLine($"{unrelatedCount + 2} People: {group.Key} = {group.Sum(e => e.Bytes)} bytes.");
            var publications = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Publish)
                .ToDictionary(e => e.Path, e => e.Destination!);
            var writtenBytes = written.Select(e => e.Path).Distinct().ToDictionary(path => path, path =>
            {
                var location = File.Exists(path) ? path : publications.GetValueOrDefault(path);
                return location is not null && File.Exists(location) ? File.ReadAllBytes(location) : null;
            });
            bool Copied(WorkspaceFileEffect write, string path) => write.Path == path ||
                writtenBytes[write.Path] is { } bytes && bytes.AsSpan().SequenceEqual(before[Path.GetRelativePath(root, path)]);
            var current = WorkspaceFixtures.IndexedDocuments(root);
            foreach (var (identity, path) in current.Where(e => e.Key is not "people:avery@example.com" and not "profileHistory:avery@example.com"))
            {
                Assert.Equal(before[Path.GetRelativePath(root, path)], File.ReadAllBytes(path));
                Assert.DoesNotContain(written, e => Copied(e, path));
            }
            var recoveryAfter = WorkspaceFixtures.RecoveryDocuments(root);
            foreach (var (identity, path) in recoveryBefore.Where(e =>
                e.Key is not "people:avery@example.com" and not "profileHistory:avery@example.com"))
            {
                Assert.Equal(path, recoveryAfter[identity]);
                Assert.Equal(before[Path.GetRelativePath(root, path)], File.ReadAllBytes(path));
                Assert.DoesNotContain(written, e => Copied(e, path));
            }
            app.Dispose();
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(root), reopened.Status);
            Assert.Equal(unrelatedCount + 2, reopened.People.Count);
            Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.Heading);
            Assert.Equal("morgan@example.com", reopened.GetRelationships("avery@example.com")!.Manager!.Email);
            Assert.Equal("Research", reopened.GetRelationships("morgan@example.com")!.Person.Department);
            Assert.Equal("Unrelated personal writing", reopened.GetPersonNotes("morgan@example.com").Single(n => n.Id == note.Id).Markdown);
            Assert.Equal("Unrelated team writing", reopened.GetTeamNotes("morgan@example.com").Single(n => n.Id == team.Id).Markdown);
            Assert.Equal(image, reopened.GetPhoto("morgan@example.com")!.Bytes.ToArray());
            Assert.Equal("Research", Assert.Single(reopened.GetProfileTimeline("morgan@example.com")!.Changes, c => c.Field == "department").NewValue.GetString());
            Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(import).Changes));
            Assert.True(reopened.RecoverWorkspace(root + "-restored"), reopened.Status);
            Assert.Equal(unrelatedCount + 2, reopened.People.Count);
            Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.Heading);
            Assert.Equal("Unrelated personal writing", reopened.GetPersonNotes("morgan@example.com").Single(n => n.Id == note.Id).Markdown);
            Assert.Equal("Unrelated team writing", reopened.GetTeamNotes("morgan@example.com").Single(n => n.Id == team.Id).Markdown);
            Assert.Equal(image, reopened.GetPhoto("morgan@example.com")!.Bytes.ToArray());
            Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(import).Changes));
            return (written.Sum(e => e.Bytes), written.Where(e =>
                Path.GetFileName(e.Path).StartsWith("created-", StringComparison.Ordinal) ||
                e.Path.EndsWith(".retirement", StringComparison.Ordinal)).Sum(e => e.Bytes));
        }
    }

    private sealed class InspectingPublisher : IWorkspaceFilePublisher
    {
        public Action<string, string>? BeforePublish { get; set; }
        public void Publish(string staged, string destination, string? backup, WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
        {
            BeforePublish?.Invoke(staged, destination);
            new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
        }
    }

    private sealed class RecordingObserver : IWorkspaceFileObserver
    {
        public ConcurrentQueue<WorkspaceFileEffect> Effects { get; } = new();
        public void Observe(WorkspaceFileEffect effect) => Effects.Enqueue(effect);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
