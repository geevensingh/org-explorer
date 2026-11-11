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
        using (var app = ApplicationWorkflow.CreateIncrementalFileBacked(fileObserver: observer))
        {
            Assert.True(app.OpenWorkspace(folder), app.Status);
            Assert.Empty(app.People);
            Assert.True(File.Exists(Path.Combine(folder, "workspace.json")));
            using var pointer = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "workspace.json")));
            Assert.Equal(3, pointer.RootElement.GetProperty("schemaVersion").GetInt32());
            Seed(app);
            observer.Effects.Clear();
            Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
            Assert.True(app.EditPersonText("avery@example.com", PersonTextField.JobTitle, "Principal Engineer"), app.Status);
            Assert.True(app.EditFavorite("avery@example.com", true), app.Status);
            Assert.True(app.CanSafelyClose);
            Assert.All(observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write),
                write => Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == write.Path));
        }
        using var reopened = ApplicationWorkflow.CreateIncrementalFileBacked();
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
    public void Profile_save_reuses_rich_unrelated_data_and_write_cost_does_not_follow_population_size()
    {
        var large = MeasureEdit(Path.Combine(folder, "large"), 256);
        var small = MeasureEdit(Path.Combine(folder, "small"), 4);
        output.WriteLine($"Same Preferred-name edit: 6 People = {small} bytes; 258 People = {large} bytes (all payload/metadata writes).");
        Assert.True(large <= small + 8192, $"Same edit wrote {small} bytes with 6 People and {large} bytes with 258 People.");
        Assert.True(large < 32768, $"A single profile edit wrote {large} bytes, including all transient metadata.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_publication_failure_retains_profile_draft_and_prior_commit_until_retry_or_discard(bool discard)
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateIncrementalFileBacked(fileObserver: observer, filePublisher: publisher);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Seed(app);
        var pointer = File.ReadAllBytes(Path.Combine(folder, "workspace.json"));
        var documents = WorkspaceFixtures.IndexedDocuments(folder).ToDictionary(e => e.Value, e => File.ReadAllBytes(e.Value));
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained draft"), app.Status);
        FileStream? held = null;
        publisher.BeforePublish = (_, destination) =>
            held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
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
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish);
        using (var prior = ApplicationWorkflow.CreateIncrementalFileBacked())
        {
            Assert.True(prior.OpenWorkspace(folder), prior.Status);
            Assert.Equal("Engineer", prior.GetRelationships("avery@example.com")!.Person.JobTitle);
            Assert.Empty(prior.GetProfileTimeline("avery@example.com")!.Changes);
        }
        if (discard) app.CancelProfileEdit();
        else Assert.True(app.SaveProfileEdit(), app.Status);
        Assert.Null(app.ProfileEdit);
        Assert.True(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateIncrementalFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal(discard ? "Engineer" : "Retained draft", reopened.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Equal(discard ? 0 : 1, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
    }

    [Fact]
    public void Text_draft_retry_and_complete_profile_text_save_use_the_existing_view_model_seam()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateIncrementalFileBacked());
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
        using var reopened = ApplicationWorkflow.CreateIncrementalFileBacked();
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

    [Fact]
    public void Shipping_factory_still_creates_version_two_and_does_not_activate_the_tracer_format()
    {
        var legacyFolder = Path.Combine(folder, "shipping");
        using (var shipping = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(shipping.OpenWorkspace(legacyFolder), shipping.Status);
            Seed(shipping);
        }
        using var pointer = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(legacyFolder, "workspace.json")));
        Assert.Equal(2, pointer.RootElement.GetProperty("schemaVersion").GetInt32());
        var newFolder = Path.Combine(folder, "development");
        using (var development = ApplicationWorkflow.CreateIncrementalFileBacked())
        {
            Assert.True(development.OpenWorkspace(newFolder), development.Status);
            Seed(development);
        }
        var before = WorkspaceFixtures.Bytes(newFolder);
        using var ordinary = ApplicationWorkflow.CreateFileBacked();
        Assert.False(ordinary.OpenWorkspace(newFolder));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, ordinary.Compatibility.State);
        var after = WorkspaceFixtures.Bytes(newFolder);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    [Fact]
    public void Receipt_failure_reports_committed_profile_separately_and_blocks_safe_close()
    {
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateIncrementalFileBacked(filePublisher: publisher);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Seed(app);
        publisher.BeforePublish = (staged, _) =>
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(staged)!, "committed.json"));
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        Assert.Contains("Saved locally", app.Status);
        Assert.Contains("maintenance incomplete", app.LocalSaveError);
        Assert.DoesNotContain("NOT saved", app.Status);
        Assert.False(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateIncrementalFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Single(reopened.GetProfileTimeline("avery@example.com")!.Changes);
    }

    private static long MeasureEdit(string root, int unrelatedCount)
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using (var app = ApplicationWorkflow.CreateIncrementalFileBacked(fileObserver: observer, filePublisher: publisher))
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
            observer.Effects.Clear();
            publisher.BeforePublish = (staged, _) =>
            {
                var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
                Assert.NotEmpty(writes);
                // This is a byte bound on real writes, including transient stages, not surviving size.
                Assert.True(writes.Sum(e => e.Bytes) < 32768, $"Profile publication staged {writes.Sum(e => e.Bytes)} bytes.");
                Assert.All(writes, write => Assert.Contains(observer.Effects,
                    e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == write.Path));
            };
            Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
            publisher.BeforePublish = null;
            var written = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
            var current = WorkspaceFixtures.IndexedDocuments(root);
            foreach (var (identity, path) in current.Where(e => e.Key is not "people:avery@example.com" and not "profileHistory:avery@example.com"))
            {
                Assert.Equal(before[Path.GetRelativePath(root, path)], File.ReadAllBytes(path));
                Assert.DoesNotContain(written, e => e.Path == path ||
                    File.Exists(e.Path) && File.ReadAllBytes(e.Path).AsSpan().SequenceEqual(File.ReadAllBytes(path)));
            }
            app.Dispose();
            using var reopened = ApplicationWorkflow.CreateIncrementalFileBacked();
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
            return written.Sum(e => e.Bytes);
        }
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

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
