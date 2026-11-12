using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Persistence.Tests;

public sealed class IncrementalReclamationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    private const string Import = """
        {"schemaVersion":1,"batchId":"46000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"}]}
        """;

    [Fact]
    public void Reviewed_population_publication_bounds_durable_ownership_files_and_preserves_independent_recovery()
    {
        var current = Path.Combine(folder, "current");
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer, filePublisher: publisher);
        Assert.True(app.OpenWorkspace(current), app.Status);
        observer.Effects.Clear();
        publisher.BeforePublish = (_, destination) =>
        {
            if (destination != Path.Combine(current, "workspace.json")) return;
            var effects = observer.Effects.ToArray();
            foreach (var record in effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write &&
                Path.GetFileName(e.Path).StartsWith("created-", StringComparison.Ordinal)))
            {
                var flush = Array.FindIndex(effects, e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == record.Path);
                var objects = InventoryObjects(File.ReadAllBytes(record.Path)).ToArray();
                Assert.InRange(objects.Length, 1, 32);
                foreach (var item in objects)
                {
                    var staged = Path.Combine(current, item["stagingPath"]!.GetValue<string>());
                    var write = Array.FindIndex(effects, e => e.Kind == WorkspaceFileEffectKind.Write && e.Path == staged);
                    Assert.True(flush >= 0 && write > flush, "Ownership must be durable before its first payload write.");
                }
            }
        };
        var import = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, batchId = "46000000-0000-4000-8000-000000000004",
            observedAt = "2026-11-02T17:00:00Z",
            people = Enumerable.Range(0, 128).Select(i => new
            {
                email = $"person{i}@example.com", displayName = $"Person {i}", jobTitle = "Engineer",
                managerEmail = (string?)null
            })
        });
        var preview = app.PreviewImport(import);
        Assert.True(preview.CanApply, string.Join("; ", preview.Errors));
        Assert.True(app.ApplyImport(preview.Id), app.Status);
        Assert.True(app.CanSafelyClose, app.LocalSaveError);
        var inventoryWrites = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write &&
            Path.GetFileName(e.Path).StartsWith("created-", StringComparison.Ordinal)).ToArray();
        Assert.True(inventoryWrites.Length <= 64,
            $"A 128-Person publication created {inventoryWrites.Length} individual ownership files.");
        Assert.All(inventoryWrites, write => Assert.Contains(observer.Effects,
            e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == write.Path));
        foreach (var path in WorkspaceFixtures.IndexedDocuments(current).Values)
            File.WriteAllText(path, "damaged current bytes");
        Assert.False(app.RefreshWorkspace());
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal(128, app.People.Count);
        Assert.Equal("Person 127", app.GetRelationships("person127@example.com")!.Person.DirectoryName);
        Assert.Equal("Engineer", app.GetRelationships("person0@example.com")!.Person.JobTitle);
        Assert.Contains("no-op", Assert.Single(app.PreviewImport(import).Changes));
    }

    [Fact]
    public void Dozens_of_edits_reclaim_redundant_versions_without_losing_history_context_or_independent_recovery()
    {
        var current = Path.Combine(folder, "current");
        var observer = new RecordingObserver();
        using (var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer))
        {
            Assert.True(app.OpenWorkspace(current), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
            var unrelatedImport = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, batchId = "46000000-0000-4000-8000-000000000003",
                observedAt = "2026-11-02T17:00:00Z",
                people = Enumerable.Range(0, 10).Select(i => new
                {
                    email = $"unrelated{i}@example.com", displayName = $"Unrelated {i}",
                    department = new string('d', 4096), managerEmail = "morgan@example.com"
                })
            });
            Assert.True(app.ApplyImport(app.PreviewImport(unrelatedImport).Id), app.Status);
            Assert.NotNull(app.CreateTeamNote("morgan@example.com", "Former team", "Frozen team writing"));
            Assert.NotNull(app.CreatePersonNote("avery@example.com", "Private", "Retained person writing"));
            Assert.True(app.EditPersonManager("avery@example.com", null), app.Status);
            Assert.NotNull(app.CreateTeamNote("morgan@example.com", "Later team", "Not the frozen context"));
            var unrelated = WorkspaceFixtures.IndexedDocuments(current)
                .Where(e => e.Key is not "people:avery@example.com" and not "profileHistory:avery@example.com")
                .ToDictionary(e => e.Value, e => File.ReadAllBytes(e.Value));
            observer.Effects.Clear();
            for (var i = 0; i < 36; i++)
                Assert.True(app.EditPreferredName("avery@example.com", $"Name {i:D2}"), app.Status);
            Assert.True(app.CanSafelyClose, app.LocalSaveError);
            foreach (var (path, bytes) in unrelated) Assert.Equal(bytes, File.ReadAllBytes(path));
            var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
            var historyBytes = writes.Where(e => e.Path.Contains(
                Path.Combine("documents", "profileHistory") + Path.DirectorySeparatorChar)).Sum(e => e.Bytes);
            var otherBytes = writes.Sum(e => e.Bytes) - historyBytes;
            output.WriteLine($"36 edits wrote {writes.Sum(e => e.Bytes)} bytes, including {historyBytes} history bytes.");
            Assert.True(otherBytes < 36 * 32768, $"Non-history writes grew beyond incremental saves: {otherBytes}.");
            Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Delete);

            var active = WorkspaceFixtures.IndexedDocuments(current).Values
                .Concat(WorkspaceFixtures.RecoveryDocuments(current).Values).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var redundantDocuments = Directory.GetFiles(current, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains(Path.DirectorySeparatorChar + "documents" + Path.DirectorySeparatorChar) &&
                    !path.EndsWith(".sha256") && !active.Contains(path)).ToArray();
            Assert.True(redundantDocuments.Length <= 4, $"Retained {redundantDocuments.Length} redundant documents.");
            var retained = WorkspaceFixtures.Bytes(current);
            var liveBytes = active.Sum(path => new FileInfo(path).Length);
            output.WriteLine($"Retained {retained.Values.Sum(b => b.LongLength)} bytes; active/recovery documents {liveBytes} bytes.");
            Assert.True(retained.Values.Sum(b => b.LongLength) <= 2 * liveBytes + 65536,
                "Redundant storage/bookkeeping accumulated beyond the active/recovery sets and bounded recent overhead.");
            var liveIndexes = SelectedClosure(current).Count(path => Path.GetExtension(path) == ".json" &&
                Path.GetDirectoryName(path) is { } directory &&
                Path.GetFileName(directory) == "index");
            var retainedIndexes = Directory.GetFiles(current, "*.json", SearchOption.AllDirectories)
                .Count(path => Path.GetFileName(Path.GetDirectoryName(path)) == "index");
            Assert.True(retainedIndexes <= 2 * liveIndexes, $"Retained {retainedIndexes} nodes for {liveIndexes} live index nodes.");
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(current), reopened.Status);
        Assert.True(reopened.CanSafelyClose, reopened.LocalSaveError);
        Verify(reopened);
        Assert.True(reopened.RecoverWorkspace(Path.Combine(folder, "restored")), reopened.Status);
        Verify(reopened);

        static void Verify(IApplicationWorkflow app)
        {
            Assert.Equal("Name 35", app.GetRelationships("avery@example.com")!.Person.PreferredName);
            Assert.Equal(12, app.People.Count);
            Assert.All(app.People.Where(p => p.Email.StartsWith("unrelated")),
                p => Assert.Equal(new string('d', 4096), p.Department));
            Assert.Null(app.GetRelationships("avery@example.com")!.Manager);
            var changes = app.GetProfileTimeline("avery@example.com")!.Changes;
            Assert.Equal(37, changes.Count);
            Assert.All(changes, c => Assert.Equal(ProfileChangeSource.LocalEdit, c.Evidence.Source));
            Assert.Equal(Enumerable.Range(0, 36).Select(i => $"Name {i:D2}"),
                changes.Where(c => c.Field == "preferredName").Select(c => c.NewValue.GetString()));
            Assert.Equal("Frozen team writing", Assert.Single(Assert.Single(
                app.GetHistoricalTeamContexts("avery@example.com")).TeamNotes).Markdown);
            Assert.Equal("Retained person writing", Assert.Single(app.GetPersonNotes("avery@example.com")).Markdown);
            Assert.Contains("no-op", Assert.Single(app.PreviewImport(Import).Changes));
        }
    }

    private sealed class RecordingObserver : IWorkspaceFileObserver
    {
        public ConcurrentQueue<WorkspaceFileEffect> Effects { get; } = new();
        public void Observe(WorkspaceFileEffect effect) => Effects.Enqueue(effect);
    }

    [Fact]
    public void Locked_cleanup_reports_committed_maintenance_failure_and_resumes_after_restart_without_edit_replay()
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        var observer = new RecordingObserver();
        using (var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer))
        {
            Assert.True(app.OpenWorkspace(current), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
            Assert.True(app.EditPreferredName("avery@example.com", "First"), app.Status);
            string? obsolete = null;
            FileStream? held = null;
            publisher.BeforePublish = (_, destination) =>
            {
                if (Path.GetFileName(destination) == "retired.json")
                {
                    var protectedPaths = SelectedClosure(current);
                    AddIndexClosure(current, JsonNode.Parse(File.ReadAllBytes(
                        Path.Combine(current, "workspace.previous.json")))!["roots"]!.AsObject(), protectedPaths);
                    obsolete = Directory.GetDirectories(Path.Combine(current, "transactions"))
                        .Where(path => File.Exists(Path.Combine(path, "retired.json")))
                        .SelectMany(path => Directory.GetFiles(path, "created-*.json"))
                        .SelectMany(path => InventoryObjects(File.ReadAllBytes(path)))
                        .Select(inventory => Path.Combine(current, inventory["path"]!.GetValue<string>()))
                        .Last(path => File.Exists(path) && !protectedPaths.Contains(path));
                    held = new FileStream(obsolete, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            };
            observer.Effects.Clear();
            try { Assert.True(app.EditPreferredName("avery@example.com", "Second"), app.Status); }
            finally { held?.Dispose(); publisher.BeforePublish = null; }
            output.WriteLine(app.LocalSaveError);
            Assert.Contains("Saved locally; storage maintenance incomplete", app.LocalSaveError);
            Assert.DoesNotContain("NOT saved", app.Status);
            Assert.False(app.CanSafelyClose);
            Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Delete);
            Assert.NotNull(obsolete);
            Assert.True(File.Exists(obsolete));
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.True(reopened.OpenWorkspace(current), reopened.Status);
        Assert.False(reopened.CanSafelyClose);
        Assert.Equal("Second", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        var before = WorkspaceFixtures.IndexedDocuments(current).Values.ToDictionary(path => path, File.ReadAllBytes);
        var preview = reopened.PreviewImport(Import.Replace("46000000-0000-4000-8000-000000000001",
            "46000000-0000-4000-8000-000000000002"));
        observer.Effects.Clear();
        Assert.True(reopened.RetryStorageMaintenance(), reopened.Status);
        Assert.True(reopened.CanSafelyClose, reopened.LocalSaveError);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish &&
            e.Destination == Path.Combine(current, "workspace.json"));
        Assert.Equal(2, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
        Assert.True(reopened.ApplyImport(preview.Id), reopened.Status);
        Assert.True(reopened.RecoverWorkspace(Path.Combine(folder, "restored")), reopened.Status);
        Assert.Equal("Second", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal(2, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
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

    private static HashSet<string> SelectedClosure(string root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIndexClosure(root, JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "workspace.json")))!["roots"]!.AsObject(), result);
        var recoveryRoot = Path.Combine(root, "recovery");
        var recovery = JsonNode.Parse(File.ReadAllBytes(Path.Combine(recoveryRoot, "current.json")))!;
        var recoveryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIndexClosure(recoveryRoot, recovery["workspace"]!["roots"]!.AsObject(), recoveryPaths);
        AddIndexClosure(recoveryRoot, recovery["noteRoots"]!.AsObject(), recoveryPaths);
        result.UnionWith(recoveryPaths);
        result.UnionWith(recoveryPaths.Select(path => path + ".sha256"));
        return result;
    }

    private static void AddIndexClosure(string root, JsonObject roots, HashSet<string> paths)
    {
        foreach (var reference in roots.Select(e => e.Value).OfType<JsonNode>()) Visit(reference.GetValue<string>());
        void Visit(string relative)
        {
            var path = Path.Combine(root, relative);
            if (!paths.Add(path)) return;
            var node = JsonNode.Parse(File.ReadAllBytes(path))!;
            if (node["entries"] is JsonObject entries)
                foreach (var document in entries) paths.Add(Path.Combine(root, document.Value!.GetValue<string>()));
            else
                foreach (var child in node["children"]!.AsObject()) Visit(child.Value!.GetValue<string>());
        }
    }

    [Fact]
    public void Recovery_selector_delivery_after_validation_cannot_be_blessed_by_the_cleanup_root_census()
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        var observer = new RecordingObserver();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer);
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        Assert.True(app.EditPreferredName("avery@example.com", "First"), app.Status);
        var selected = Path.Combine(current, "recovery", "current.json");
        var priorSelector = File.ReadAllBytes(selected);
        var priorBytes = SelectedClosure(current).ToDictionary(path => path, File.ReadAllBytes);
        publisher.BeforePublish = (_, destination) =>
        {
            if (Path.GetFileName(destination) == "retired.json") File.WriteAllBytes(selected, priorSelector);
        };
        observer.Effects.Clear();
        Assert.True(app.EditPreferredName("avery@example.com", "Second"), app.Status);
        publisher.BeforePublish = null;
        Assert.Contains("storage maintenance incomplete", app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Delete);
        foreach (var (path, bytes) in priorBytes) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal("Second", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.True(app.RetryStorageMaintenance(), app.Status);
        Assert.True(app.CanSafelyClose, app.LocalSaveError);
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal("Second", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal(2, app.GetProfileTimeline("avery@example.com")!.Changes.Count);
    }

    [Theory]
    [InlineData("pointer-first")]
    [InlineData("index-first")]
    [InlineData("missing-photo")]
    [InlineData("invalid-person")]
    [InlineData("unsupported-index")]
    public void Delivery_at_cleanup_retains_the_accepted_state_and_recovery_until_the_complete_graph_arrives(string delivery)
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        var observer = new RecordingObserver();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer);
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        var photo = Path.Combine(folder, "source.png");
        File.WriteAllBytes(photo, image);
        Assert.True(app.EditPhoto("avery@example.com", photo), app.Status);
        var donor = Path.Combine(folder, "donor");
        foreach (var (relative, bytes) in WorkspaceFixtures.Bytes(current))
        {
            var path = Path.Combine(donor, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        using (var other = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(other.OpenWorkspace(donor), other.Status);
            Assert.True(other.EditPreferredName("avery@example.com", "Delivered"), other.Status);
        }
        var incoming = WorkspaceFixtures.Bytes(donor).Where(e =>
            e.Key == "workspace.json" || e.Key.StartsWith("index" + Path.DirectorySeparatorChar) ||
            e.Key.StartsWith("documents" + Path.DirectorySeparatorChar)).ToDictionary(e => e.Key, e => e.Value);
        var donorDocuments = WorkspaceFixtures.IndexedDocuments(donor);
        var missingPerson = Path.GetRelativePath(donor, donorDocuments["people:avery@example.com"]);
        var photoRelative = Path.GetRelativePath(donor, donorDocuments.Single(e => e.Key.StartsWith("photos:")).Value);
        var indexRelative = JsonNode.Parse(incoming["workspace.json"])!["roots"]!["people"]!.GetValue<string>();
        publisher.BeforePublish = (_, destination) =>
        {
            if (Path.GetFileName(destination) != "retired.json") return;
            foreach (var (relative, bytes) in incoming)
            {
                if (delivery == "pointer-first" && relative != "workspace.json" ||
                    delivery == "index-first" && relative == missingPerson) continue;
                var path = Path.Combine(current, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
            if (delivery == "missing-photo") File.Delete(Path.Combine(current, photoRelative));
            if (delivery == "invalid-person") File.WriteAllText(Path.Combine(current, missingPerson), "invalid delivered Person");
            if (delivery == "unsupported-index")
            {
                var node = JsonNode.Parse(incoming[indexRelative])!;
                node["schemaVersion"] = 99;
                File.WriteAllText(Path.Combine(current, indexRelative), node.ToJsonString());
            }
        };
        observer.Effects.Clear();
        Assert.True(app.EditPreferredName("avery@example.com", "Local"), app.Status);
        publisher.BeforePublish = null;
        Assert.Contains("storage maintenance incomplete", app.LocalSaveError);
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Delete);
        Assert.False(app.RefreshWorkspace());
        Assert.Equal("Local", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.False(app.EditPersonText("avery@example.com", PersonTextField.JobTitle, "Unsafe"));
        var incomplete = WorkspaceFixtures.Bytes(current);
        Assert.False(app.RetryStorageMaintenance());
        foreach (var (relative, bytes) in incomplete) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(current, relative)));

        foreach (var (relative, bytes) in incoming)
        {
            var path = Path.Combine(current, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal("Delivered", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        app.DiscardUnsavedChanges();
        Assert.True(app.RetryStorageMaintenance(), app.Status);
        Assert.True(app.CanSafelyClose, app.LocalSaveError);
        Assert.Equal(image, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Equal("Delivered", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal(image, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal(2, app.GetProfileTimeline("avery@example.com")!.Changes.Count);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("recovery-object")]
    [InlineData("receipt")]
    [InlineData("legacy-inventory")]
    public void Ambiguous_interrupted_transactions_keep_their_inventories_stages_and_shared_resources(string boundary)
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        using (var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher))
        {
            Assert.True(app.OpenWorkspace(current), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
            var oldTransactions = Directory.GetDirectories(Path.Combine(current, "transactions")).ToHashSet();
            publisher.BeforePublish = (_, destination) =>
            {
                if (boundary is "candidate" or "legacy-inventory" && destination == Path.Combine(current, "workspace.json") ||
                    boundary == "recovery-object" && destination.Contains(Path.Combine("recovery", "documents")) ||
                    boundary == "receipt" && Path.GetFileName(destination) == "committed.json")
                    throw new IOException("Interrupted transaction; receipt absence is ambiguous.");
            };
            Assert.Equal(boundary == "receipt", app.EditPreferredName("avery@example.com", "Interrupted"));
            publisher.BeforePublish = null;
            var transaction = Assert.Single(Directory.GetDirectories(Path.Combine(current, "transactions")),
                path => !oldTransactions.Contains(path));
            if (boundary == "legacy-inventory")
            {
                var index = 90000000;
                foreach (var record in Directory.GetFiles(transaction, "created-*.json"))
                {
                    var objects = InventoryObjects(File.ReadAllBytes(record)).ToArray();
                    File.WriteAllText(record, objects[0].ToJsonString());
                    foreach (var item in objects.Skip(1))
                        File.WriteAllText(Path.Combine(transaction, $"created-{index++:D8}.json"), item.ToJsonString());
                }
            }
            var retained = Directory.GetFiles(transaction).ToDictionary(path => path, File.ReadAllBytes);
            foreach (var record in retained.Keys.Where(path => Path.GetFileName(path).StartsWith("created-")).ToArray())
            {
                foreach (var inventory in InventoryObjects(retained[record]))
                foreach (var key in new[] { "path", "stagingPath" })
                    if (inventory[key] is { } value && File.Exists(Path.Combine(current, value.GetValue<string>())))
                    {
                        var path = Path.Combine(current, value.GetValue<string>());
                        retained[path] = File.ReadAllBytes(path);
                    }
            }
            app.DiscardUnsavedChanges();
            Assert.True(app.EditPreferredName("avery@example.com", "Final"), app.Status);
            Assert.True(app.CanSafelyClose, app.LocalSaveError);
            Assert.True(app.RetryStorageMaintenance(), app.Status);
            foreach (var (path, bytes) in retained) Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Contains("reclamation deferred", app.Status);
            var otherPath = Path.Combine(folder, "other");
            using var other = ApplicationWorkflow.CreateFileBacked();
            Assert.True(other.OpenWorkspace(otherPath), other.Status);
            Assert.True(app.OpenWorkspace(otherPath), app.Status);
            Assert.DoesNotContain("reclamation deferred", app.Status);
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(current), reopened.Status);
        Assert.Contains("reclamation deferred", reopened.Status);
        Assert.Equal("Final", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal(boundary == "receipt" ? 2 : 1, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
        Assert.True(reopened.RecoverWorkspace(Path.Combine(folder, "restored")), reopened.Status);
        Assert.Equal("Final", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":2,"objects":[]}""")]
    [InlineData("""{"schemaVersion":2,"objects":[null]}""")]
    public void Malformed_interrupted_ownership_is_reported_without_deleting_recorded_files(string invalid)
    {
        var current = Path.Combine(folder, "current");
        var publisher = new InspectingPublisher();
        var observer = new RecordingObserver();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer);
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var oldTransactions = Directory.GetDirectories(Path.Combine(current, "transactions")).ToHashSet();
        publisher.BeforePublish = (_, destination) =>
        {
            if (destination == Path.Combine(current, "workspace.json")) throw new IOException("Interrupted current publication.");
        };
        Assert.False(app.EditPreferredName("avery@example.com", "Interrupted"));
        publisher.BeforePublish = null;
        var transaction = Assert.Single(Directory.GetDirectories(Path.Combine(current, "transactions")),
            path => !oldTransactions.Contains(path));
        File.WriteAllText(Directory.GetFiles(transaction, "created-*.json")[0], invalid);
        var retained = Directory.GetFiles(current, "*", SearchOption.AllDirectories)
            .Where(path => path != Path.Combine(current, "recovery", "current.json"))
            .ToDictionary(path => path, File.ReadAllBytes);
        app.DiscardUnsavedChanges();
        observer.Effects.Clear();
        Assert.False(app.RetryStorageMaintenance());
        Assert.False(app.CanSafelyClose);
        Assert.NotEmpty(app.LocalSaveError!);
        Assert.DoesNotContain(observer.Effects, effect => effect.Kind == WorkspaceFileEffectKind.Delete);
        foreach (var (path, bytes) in retained) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static IEnumerable<JsonNode> InventoryObjects(byte[] bytes)
    {
        var record = JsonNode.Parse(bytes)!;
        return record["objects"] is JsonArray objects ? objects.Select(item => item!) : [record];
    }

    [Fact]
    public void Cleanup_preserves_changed_retired_bytes_unknown_files_conflict_copies_and_historical_photos()
    {
        var current = Path.Combine(folder, "current");
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(current), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Import).Id), app.Status);
        var original = WorkspaceFixtures.IndexedDocuments(current)["people:avery@example.com"];
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        var photo = Path.Combine(folder, "source.png");
        File.WriteAllBytes(photo, image);
        Assert.True(app.EditPhoto("morgan@example.com", photo), app.Status);
        Assert.NotNull(app.CreateTeamNote("morgan@example.com", "Team", "Captured photo context"));
        Assert.True(app.EditPersonManager("avery@example.com", null), app.Status);
        Assert.True(app.EditPhoto("morgan@example.com", null), app.Status);
        var altered = WorkspaceFixtures.IndexedDocuments(current)["people:avery@example.com"];
        Assert.True(app.EditPreferredName("avery@example.com", "First"), app.Status);
        File.WriteAllText(altered, "Externally changed superseded bytes");
        var unknown = Path.Combine(current, "documents", "people", Guid.NewGuid().ToString("N") + ".json");
        var conflict = Path.Combine(current, "recovery", "documents", "people", "Person (provider conflict).json");
        var transactionDirectory = Directory.GetDirectories(Path.Combine(current, "transactions"))[0];
        var unknownDirectory = Path.Combine(transactionDirectory, "provider-copy");
        Directory.CreateDirectory(unknownDirectory);
        var nestedUnknown = Path.Combine(unknownDirectory, "keep.txt");
        foreach (var path in new[] { unknown, conflict, nestedUnknown }) File.WriteAllText(path, "Not owned by cleanup");
        for (var i = 0; i < 4; i++) Assert.True(app.EditPreferredName("avery@example.com", $"Later {i}"), app.Status);
        Assert.True(app.CanSafelyClose, app.LocalSaveError);
        Assert.False(File.Exists(original));
        Assert.Equal("Externally changed superseded bytes", File.ReadAllText(altered));
        foreach (var path in new[] { unknown, conflict, nestedUnknown })
            Assert.Equal("Not owned by cleanup", File.ReadAllText(path));
        var historicalPhoto = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).ReportingChain.Single().PhotoPath;
        Assert.NotNull(historicalPhoto);
        var resource = WorkspaceFixtures.IndexedDocuments(current)["photos:" + historicalPhoto];
        Assert.Equal(image, File.ReadAllBytes(resource));
        Assert.True(app.RecoverWorkspace(Path.Combine(folder, "restored")), app.Status);
        Assert.Null(app.GetPhoto("morgan@example.com"));
        Assert.Equal(historicalPhoto, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).ReportingChain.Single().PhotoPath);
        Assert.Equal(image, File.ReadAllBytes(WorkspaceFixtures.IndexedDocuments(Path.Combine(folder, "restored"))["photos:" + historicalPhoto]));
        Assert.Equal(2, app.GetProfileTimeline("morgan@example.com")!.Changes.Count);
        Assert.Equal("Captured photo context", Assert.Single(Assert.Single(
            app.GetHistoricalTeamContexts("avery@example.com")).TeamNotes).Markdown);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
