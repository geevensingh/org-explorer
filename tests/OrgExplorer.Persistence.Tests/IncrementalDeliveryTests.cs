using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class IncrementalDeliveryTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private string Folder => Path.Combine(root, "workspace");
    private const string Email = "avery@example.com";
    private const string Population = """
        {"schemaVersion":1,"batchId":"44000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"},
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null}]}
        """;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
    private static readonly byte[] OtherPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII=");

    [Fact]
    public void Delivery_cannot_omit_a_photo_still_required_by_recorded_Profile_history()
    {
        using var app = Open();
        var image = Path.Combine(root, "source.png");
        File.WriteAllBytes(image, Png);
        Assert.True(app.EditPhoto(Email, image), app.Status);
        Assert.True(app.EditPhoto(Email, null), app.Status);
        var pointerPath = Path.Combine(Folder, "workspace.json");
        var original = File.ReadAllBytes(pointerPath);
        var pointer = JsonNode.Parse(original)!;
        pointer["roots"]!["photos"] = null;
        File.WriteAllText(pointerPath, pointer.ToJsonString());
        var delivered = WorkspaceFixtures.Bytes(Folder);

        Assert.False(app.RefreshWorkspace());
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Contains("STALE", app.Status);
        Assert.Equal(2, app.GetProfileTimeline(Email)!.Changes.Count);
        Assert.False(app.EditPreferredName(Email, "Must not publish"));
        AssertPreserved(delivered);
        File.WriteAllBytes(pointerPath, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        using var reopened = Reopen();
        Assert.Null(reopened.GetPhoto(Email));
        Assert.Equal(2, reopened.GetProfileTimeline(Email)!.Changes.Count);
    }

    [Fact]
    public void Delivery_cannot_omit_accepted_Import_evidence_referenced_by_Profile_history()
    {
        using var app = Open();
        var path = Path.Combine(Folder, "workspace.json");
        var original = File.ReadAllBytes(path);
        var pointer = JsonNode.Parse(original)!;
        pointer["roots"]!["acceptedBatches"] = null;
        File.WriteAllText(path, pointer.ToJsonString());
        var delivered = WorkspaceFixtures.Bytes(Folder);
        Assert.False(app.RefreshWorkspace());
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Contains("STALE", app.Status);
        Assert.Equal("Engineer", app.GetRelationships(Email)!.Person.JobTitle);
        Assert.False(app.EditPreferredName(Email, "Unsafe overwrite"));
        AssertPreserved(delivered);
        File.WriteAllBytes(path, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        using var reopened = Reopen();
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(Population).Changes));
    }

    [Fact]
    public void A_captured_Profile_photo_is_required_even_without_a_current_Person_reference()
    {
        using var app = Open();
        Assert.True(app.EditPersonManager(Email, null), app.Status);
        var path = WorkspaceFixtures.IndexedDocuments(Folder)["historicalTeamContext:" + Email];
        var original = File.ReadAllBytes(path);
        var data = JsonNode.Parse(original)!;
        data["contexts"]![0]!["reportingChain"]![0]!["photoPath"] = new string('a', 64) + ".png";
        File.WriteAllText(path, data.ToJsonString());
        var delivered = WorkspaceFixtures.Bytes(Folder);
        Assert.False(app.RefreshWorkspace());
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Null(Assert.Single(Assert.Single(app.GetHistoricalTeamContexts(Email)).ReportingChain).PhotoPath);
        Assert.False(app.EditPreferredName(Email, "Unsafe overwrite"));
        AssertPreserved(delivered);
        File.WriteAllBytes(path, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        using var reopened = Reopen();
        Assert.Null(reopened.GetRelationships(Email)!.Manager);
        Assert.Equal("Morgan", Assert.Single(Assert.Single(reopened.GetHistoricalTeamContexts(Email)).ReportingChain).DirectoryName);
    }

    [Fact]
    public void Direct_Person_history_and_note_edits_require_consistency_and_invalidate_a_reviewed_import()
    {
        using var app = Open();
        var note = app.CreatePersonNote(Email, "Private", "Original writing")!;
        var notePath = Path.Combine(Folder, "notes", note.Id + ".md");
        var preview = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"44000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[{"email":"avery@example.com","department":"Reviewed"}]}
            """);
        Assert.True(preview.CanApply, app.Status);
        var delivery = PrepareProfileDelivery(Email, PersonTextField.JobTitle, "Delivered engineer");
        var person = WorkspaceFixtures.IndexedDocuments(Folder)["people:" + Email];
        File.WriteAllBytes(person, delivery[person]);
        var incomplete = WorkspaceFixtures.Bytes(Folder);
        Assert.False(app.ApplyImport(preview.Id));
        Assert.False(app.RefreshWorkspace());
        Assert.Equal("Engineer", app.GetRelationships(Email)!.Person.JobTitle);
        Assert.Empty(app.GetProfileTimeline(Email)!.Changes);
        AssertPreserved(incomplete);

        Deliver(delivery);
        File.WriteAllText(notePath, File.ReadAllText(notePath).Replace("Original writing", "Delivered writing"));
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal("Delivered engineer", app.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("Delivered writing", Assert.Single(app.GetPersonNotes(Email)).Markdown);
        var change = Assert.Single(app.GetProfileTimeline(Email)!.Changes);
        Assert.Equal("Engineer", change.OldValue.GetString());
        Assert.Equal("Delivered engineer", change.NewValue.GetString());
        Assert.Equal(ProfileChangeSource.LocalEdit, change.Evidence.Source);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 17, 0, 0, TimeSpan.Zero),
            Assert.Single(app.GetProfileTimeline(Email)!.Confirmations, e => e.Field == "displayName").ObservedAt);
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Null(app.GetRelationships(Email)!.Person.Department);
        using var reopened = Reopen();
        Assert.Equal("Delivered engineer", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("Delivered writing", Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
        Assert.Single(reopened.GetProfileTimeline(Email)!.Changes);
    }

    [Theory]
    [InlineData("pointer-index-documents-history-photo")]
    [InlineData("index-pointer-photo-documents-history")]
    [InlineData("documents-history-photo-pointer-index")]
    public void Out_of_order_delivery_never_accepts_a_partial_population_or_missing_history_and_resources(string order)
    {
        using var app = Open();
        var producerFolder = Path.Combine(root, "producer");
        WorkspaceFixtures.Copy(Folder, producerFolder);
        using (var producer = ApplicationWorkflow.CreateIncrementalFileBacked())
        {
            Assert.True(producer.OpenWorkspace(producerFolder), producer.Status);
            Assert.True(producer.ApplyImport(producer.PreviewImport("""
                {"schemaVersion":1,"batchId":"44000000-0000-4000-8000-000000000003",
                 "observedAt":"2026-11-02T17:00:00Z","people":[
                 {"email":"avery@example.com","jobTitle":"Delivered engineer"},
                 {"email":"riley@example.com","displayName":"Riley","managerEmail":"morgan@example.com"}]}
                """).Id), producer.Status);
            var image = Path.Combine(root, "source.png");
            File.WriteAllBytes(image, Png);
            Assert.True(producer.EditPhoto("riley@example.com", image), producer.Status);
        }
        var documents = WorkspaceFixtures.IndexedDocuments(producerFolder);
        var stages = new Dictionary<string, string[]>
        {
            ["pointer"] = [Path.Combine(producerFolder, "workspace.json")],
            ["index"] = Directory.GetFiles(Path.Combine(producerFolder, "index"), "*.json"),
            ["documents"] = documents.Where(e => !e.Key.StartsWith("photos:") && !e.Key.StartsWith("profileHistory:"))
                .Select(e => e.Value).ToArray(),
            ["history"] = documents.Where(e => e.Key.StartsWith("profileHistory:")).Select(e => e.Value).ToArray(),
            ["photo"] = documents.Where(e => e.Key.StartsWith("photos:")).Select(e => e.Value).ToArray()
        };
        var pointerDelivered = false;
        var sequence = order.Split('-');
        for (var i = 0; i < sequence.Length; i++)
        {
            foreach (var source in stages[sequence[i]])
            {
                var target = Path.Combine(Folder, Path.GetRelativePath(producerFolder, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }
            pointerDelivered |= sequence[i] == "pointer";
            var before = WorkspaceFixtures.Bytes(Folder);
            var complete = i == sequence.Length - 1;
            Assert.Equal(!pointerDelivered || complete, app.RefreshWorkspace());
            if (!complete)
            {
                Assert.Equal(2, app.People.Count);
                Assert.Equal("Engineer", app.GetRelationships(Email)!.Person.JobTitle);
                Assert.Null(app.GetRelationships("riley@example.com"));
                Assert.Empty(app.GetProfileTimeline(Email)!.Changes);
                if (pointerDelivered)
                {
                    Assert.Contains("STALE", app.Status);
                    Assert.False(app.EditPreferredName(Email, "Unsafe overwrite"));
                }
            }
            AssertPreserved(before);
        }
        Assert.True(app.IsWorkspaceCurrent);
        Assert.Equal(3, app.People.Count);
        Assert.Equal("Delivered engineer", app.GetRelationships(Email)!.Person.JobTitle);
        Assert.Single(app.GetProfileTimeline(Email)!.Changes);
        Assert.Equal(Png, app.GetPhoto("riley@example.com")!.Bytes.ToArray());
        using var reopened = Reopen();
        Assert.Equal(3, reopened.People.Count);
        Assert.Equal("morgan@example.com", reopened.GetRelationships("riley@example.com")!.Manager!.Email);
        Assert.Equal(Png, reopened.GetPhoto("riley@example.com")!.Bytes.ToArray());
    }

    [Theory]
    [InlineData("malformed-selector")]
    [InlineData("future-selector")]
    [InlineData("future-family")]
    [InlineData("missing-family")]
    [InlineData("missing-index")]
    [InlineData("future-index")]
    [InlineData("wrong-index-family")]
    [InlineData("cyclic-index")]
    [InlineData("invalid-edge")]
    [InlineData("excessive-leaf")]
    [InlineData("duplicate-identity")]
    [InlineData("duplicate-document")]
    [InlineData("escaping-document")]
    [InlineData("null-document")]
    [InlineData("missing-person")]
    [InlineData("future-person")]
    [InlineData("inconsistent-person")]
    [InlineData("missing-history")]
    [InlineData("inconsistent-history")]
    [InlineData("invalid-batch")]
    public void Invalid_delivery_is_stale_nonmutating_and_retryable_after_complete_repair_outside_the_app(string invalidity)
    {
        using var app = Open();
        var documents = WorkspaceFixtures.IndexedDocuments(Folder);
        var pointerPath = Path.Combine(Folder, "workspace.json");
        var pointer = JsonNode.Parse(File.ReadAllBytes(pointerPath))!;
        var indexPath = Path.Combine(Folder, pointer["roots"]!["people"]!.GetValue<string>());
        var path = invalidity switch
        {
            "malformed-selector" or "future-selector" or "future-family" or "missing-family" => pointerPath,
            "missing-person" or "future-person" or "inconsistent-person" => documents["people:" + Email],
            "missing-history" or "inconsistent-history" => documents["profileHistory:" + Email],
            "invalid-batch" => documents.Single(e => e.Key.StartsWith("acceptedBatches:")).Value,
            _ => indexPath
        };
        var original = File.ReadAllBytes(path);
        var data = JsonNode.Parse(original)!;
        switch (invalidity)
        {
            case "future-selector":
            case "future-index":
            case "future-person": data["schemaVersion"] = 99; break;
            case "future-family": data["recordVersions"]!["photos"] = 99; break;
            case "missing-family": data["roots"]!.AsObject().Remove("profileHistory"); break;
            case "wrong-index-family": data["family"] = "profileHistory"; break;
            case "cyclic-index":
            case "invalid-edge":
                data["entries"] = null;
                data["children"] = new JsonObject
                {
                    [invalidity == "invalid-edge" ? "z" : "a"] = pointer["roots"]!["people"]!.GetValue<string>()
                };
                break;
            case "excessive-leaf":
                for (var i = 0; i < 17; i++)
                    data["entries"]![$"extra{i}@example.com"] = data["entries"]![Email]!.GetValue<string>();
                break;
            case "duplicate-document":
                data["entries"]!["morgan@example.com"] = data["entries"]![Email]!.GetValue<string>();
                break;
            case "escaping-document": data["entries"]![Email] = @"documents\people\..\..\outside.json"; break;
            case "null-document": data["entries"]![Email] = null; break;
            case "inconsistent-person": data["jobTitle"] = "Unconfirmed delivery"; break;
            case "inconsistent-history": data["observations"]![0]!["value"] = "Unconfirmed delivery"; break;
            case "invalid-batch":
                data.AsObject().First().Value!["source"]!["observedAt"] = "2026-02-29T17:00:00Z";
                break;
        }
        var invalid = data.ToJsonString();
        if (invalidity == "malformed-selector") invalid = "{";
        if (invalidity == "duplicate-identity")
        {
            var entry = data["entries"]!.AsObject().First();
            var property = new JsonObject { [entry.Key] = entry.Value!.DeepClone() }.ToJsonString()[1..^1];
            invalid = invalid.Replace("\"entries\":{", "\"entries\":{" + property + ",");
        }
        if (invalidity is "missing-index" or "missing-person" or "missing-history") File.Delete(path);
        else File.WriteAllText(path, invalid);
        var unknown = Path.Combine(Folder, "documents", "people", "provider-conflict.json");
        File.WriteAllText(unknown, "Unknown provider conflict bytes must survive.");
        var delivered = WorkspaceFixtures.Bytes(Folder);

        Assert.False(app.RefreshWorkspace());
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Contains("STALE", app.Status);
        Assert.Equal(2, app.People.Count);
        Assert.Equal("Engineer", app.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("Morgan", app.GetRelationships(Email)!.Manager!.DirectoryName);
        Assert.Empty(app.GetProfileTimeline(Email)!.Changes);
        Assert.False(app.EditPreferredName(Email, "Unsafe overwrite"));
        AssertPreserved(delivered);
        File.WriteAllBytes(path, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        using var reopened = Reopen();
        Assert.Equal("Engineer", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Empty(reopened.GetProfileTimeline(Email)!.Changes);
        Assert.Equal("Unknown provider conflict bytes must survive.", File.ReadAllText(unknown));
    }

    [Theory]
    [InlineData("profile", false, false)]
    [InlineData("profile", false, true)]
    [InlineData("profile", true, false)]
    [InlineData("profile", true, true)]
    [InlineData("photo", false, false)]
    [InlineData("photo", false, true)]
    [InlineData("photo", true, false)]
    [InlineData("photo", true, true)]
    public void Publication_accounts_for_only_validated_bytes_when_Person_history_or_same_path_photo_arrives(
        string kind, bool invalid, bool afterPublication)
    {
        var publisher = new DeliveringPublisher { AfterPublication = afterPublication };
        using var app = Open(publisher);
        const string manager = "morgan@example.com";
        Dictionary<string, byte[]> validDelivery;
        if (kind == "photo")
        {
            var source = Path.Combine(root, "photo.png");
            File.WriteAllBytes(source, Png);
            Assert.True(app.EditPhoto(manager, source), app.Status);
            var reference = app.GetRelationships(manager)!.Person.PhotoPath!;
            var path = WorkspaceFixtures.IndexedDocuments(Folder)["photos:" + reference];
            validDelivery = new() { [path] = OtherPng };
        }
        else validDelivery = PrepareProfileDelivery(manager, PersonTextField.DirectoryName, "Delivered Morgan");
        var delivery = new Dictionary<string, byte[]>(validDelivery);
        if (invalid)
        {
            if (kind == "photo") delivery[delivery.Keys.Single()] = "Not a decoded image"u8.ToArray();
            else delivery.Remove(WorkspaceFixtures.IndexedDocuments(Folder)["profileHistory:" + manager]);
        }
        publisher.Deliver = () => Deliver(delivery);
        Assert.True(app.EditFavorite(Email, true), app.Status);
        Assert.Null(publisher.Deliver);
        Assert.True(app.GetRelationships(Email)!.Person.IsFavorite);
        foreach (var (path, bytes) in delivery) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(!invalid, app.RefreshWorkspace());
        Assert.Equal(!invalid, app.IsWorkspaceCurrent);
        if (invalid)
        {
            Assert.Contains("STALE", app.Status);
            Assert.Equal("Morgan", app.GetRelationships(manager)!.Person.DirectoryName);
            if (kind == "photo") Assert.Equal(Png, app.GetPhoto(manager)!.Bytes.ToArray());
            var before = WorkspaceFixtures.Bytes(Folder);
            Assert.False(app.EditPersonText(manager, PersonTextField.Department, "Unsafe overwrite"));
            AssertPreserved(before);
            Deliver(validDelivery);
            Assert.True(app.RefreshWorkspace(), app.Status);
        }
        if (kind == "profile")
        {
            Assert.Equal("Delivered Morgan", app.GetRelationships(manager)!.Person.DirectoryName);
            Assert.Single(app.GetProfileTimeline(manager)!.Changes);
        }
        else Assert.Equal(OtherPng, app.GetPhoto(manager)!.Bytes.ToArray());
        using var reopened = Reopen();
        Assert.True(reopened.GetRelationships(Email)!.Person.IsFavorite);
        Assert.Single(reopened.GetProfileTimeline(Email)!.Changes);
        if (kind == "profile") Assert.Equal("Delivered Morgan", reopened.GetRelationships(manager)!.Person.DirectoryName);
        else Assert.Equal(OtherPng, reopened.GetPhoto(manager)!.Bytes.ToArray());
    }

    private Dictionary<string, byte[]> PrepareProfileDelivery(string email, PersonTextField field, string value)
    {
        var producerFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        WorkspaceFixtures.Copy(Folder, producerFolder);
        using var producer = ApplicationWorkflow.CreateIncrementalFileBacked();
        Assert.True(producer.OpenWorkspace(producerFolder), producer.Status);
        Assert.True(producer.EditPersonText(email, field, value), producer.Status);
        var before = WorkspaceFixtures.IndexedDocuments(Folder);
        var after = WorkspaceFixtures.IndexedDocuments(producerFolder);
        return new[] { "people:", "profileHistory:" }.ToDictionary(
            family => before[family + email], family => File.ReadAllBytes(after[family + email]));
    }

    private static void Deliver(Dictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files) File.WriteAllBytes(path, bytes);
    }

    private IApplicationWorkflow Open(IWorkspaceFilePublisher? publisher = null)
    {
        var app = ApplicationWorkflow.CreateIncrementalFileBacked(filePublisher: publisher);
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Population).Id), app.Status);
        return app;
    }

    private IApplicationWorkflow Reopen()
    {
        var app = ApplicationWorkflow.CreateIncrementalFileBacked();
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        return app;
    }

    private void AssertPreserved(Dictionary<string, byte[]> expected)
    {
        var actual = WorkspaceFixtures.Bytes(Folder);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, actual[path]);
    }

    private sealed class DeliveringPublisher : IWorkspaceFilePublisher
    {
        public bool AfterPublication { get; init; }
        public Action? Deliver { get; set; }

        public void Publish(string staged, string destination, string? backup)
        {
            var delivery = Path.GetFileName(destination) == "workspace.json" ? Deliver : null;
            if (delivery is not null) Deliver = null;
            if (!AfterPublication) delivery?.Invoke();
            if (File.Exists(destination)) File.Replace(staged, destination, backup);
            else File.Move(staged, destination);
            if (AfterPublication) delivery?.Invoke();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
