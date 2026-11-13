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
        using (var producer = ApplicationWorkflow.CreateFileBacked())
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
            if (!complete) AssertPreserved(before);
            else
            {
                foreach (var source in documents.Values.Append(Path.Combine(producerFolder, "workspace.json")))
                    Assert.Equal(File.ReadAllBytes(source),
                        File.ReadAllBytes(Path.Combine(Folder, Path.GetRelativePath(producerFolder, source))));
                foreach (var source in stages["index"])
                {
                    var target = Path.Combine(Folder, Path.GetRelativePath(producerFolder, source));
                    if (File.Exists(target)) Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
                }
                Assert.DoesNotContain("maintenance incomplete", app.LocalSaveError);
            }
        }
        Assert.True(app.IsWorkspaceCurrent);
        Assert.Equal(3, app.People.Count);
        Assert.Equal("Delivered engineer", app.GetRelationships(Email)!.Person.JobTitle);
        Assert.Single(app.GetProfileTimeline(Email)!.Changes);
        Assert.Equal(Png, app.GetPhoto("riley@example.com")!.Bytes.ToArray());
        Assert.False(app.CanSafelyClose);
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose, app.Status);
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

    [Fact]
    public void Same_Person_delivery_inside_publication_is_a_durable_explicit_conflict()
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        var basis = app.GetRelationships(Email)!.Person;
        var delivery = PrepareProfileDelivery(Email, PersonTextField.PreferredName, "Delivered Avery");
        publisher.Deliver = () => Deliver(delivery);

        Assert.NotNull(app.SavePersonTextDraft(basis, PersonTextField.PreferredName, "Local Avery"));

        Assert.Equal(WorkspacePublicationOutcome.PublishedConflict, app.PublicationOutcome);
        Assert.False(app.CanSafelyClose);
        var conflict = Assert.Single(app.GetWorkspaceConflicts());
        foreach (var (path, bytes) in delivery)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            var evidence = Assert.Single(conflict.Evidence, value => value.OriginalPath == path);
            Assert.Equal(bytes, File.ReadAllBytes(evidence.Path));
        }
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal("Local Avery", app.GetRelationships(Email)!.Person.PreferredName);
        app.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        Assert.False(app.CanSafelyClose);
        Assert.Equal(WorkspacePublicationOutcome.PublishedConflict, app.PublicationOutcome);
        Assert.Equal(conflict.Id, Assert.Single(app.GetWorkspaceConflicts()).Id);
        app.DiscardUnsavedChanges();
        Assert.False(app.CanSafelyClose);
        using var reopened = Reopen();
        Assert.Equal(WorkspacePublicationOutcome.PublishedConflict, reopened.PublicationOutcome);
        Assert.Equal(conflict.Id, Assert.Single(reopened.GetWorkspaceConflicts()).Id);
        Assert.False(reopened.CanSafelyClose);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Same_note_delivery_inside_publication_preserves_exact_captured_bytes_and_local_writing(NoteScope scope)
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        var original = app.SaveNoteDraft(app.BeginNoteDraft(scope, Email)!, "Context", "Original writing")!;
        var path = Path.Combine(Folder, scope == NoteScope.Person ? "notes" : "team-notes", original.Id + ".md");
        var delivered = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("Original writing", "Delivered writing"));
        publisher.DestinationFileName = Path.GetFileName(path);
        publisher.Deliver = () => File.WriteAllBytes(path, delivered);

        Assert.NotNull(app.SaveNoteDraft(original, "Local title", "Retained local writing"));

        Assert.Equal(WorkspacePublicationOutcome.PublishedConflict, app.PublicationOutcome);
        var conflict = Assert.Single(app.GetWorkspaceConflicts());
        var evidence = Assert.Single(conflict.Evidence, value => value.OriginalPath == path);
        Assert.Equal(delivered, File.ReadAllBytes(evidence.Path));
        Assert.Equal("Retained local writing", app.BeginNoteDraft(scope, Email, original.Id)!.Markdown);
        Assert.False(app.CanSafelyClose);
        Assert.False(app.RetryStorageMaintenance());
        app.DiscardUnsavedChanges(original.Id);
        Assert.False(app.CanSafelyClose);
        using var reopened = Reopen();
        Assert.Equal("Retained local writing", reopened.BeginNoteDraft(scope, Email, original.Id)!.Markdown);
        Assert.Equal(conflict.Id, Assert.Single(reopened.GetWorkspaceConflicts()).Id);
        Assert.Equal(delivered, File.ReadAllBytes(Assert.Single(reopened.GetWorkspaceConflicts()).Evidence.Single().Path));
        Assert.False(reopened.CanSafelyClose);
    }

    [Fact]
    public void Conflict_acknowledgement_is_bound_to_the_exact_reviewed_revision_and_survives_restart()
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        var original = app.CreatePersonNote(Email, "Context", "Original writing")!;
        var path = Path.Combine(Folder, "notes", original.Id + ".md");
        publisher.DestinationFileName = Path.GetFileName(path);
        publisher.Deliver = () => File.WriteAllText(path, File.ReadAllText(path).Replace("Original writing", "Delivered writing"));
        Assert.True(app.EditPersonNote(original.Id, "Local", "Local writing"), app.Status);
        var evidence = Assert.Single(Assert.Single(app.GetWorkspaceConflicts()).Evidence).Path;
        var review = Assert.IsType<WorkspaceConflictReview>(app.ReviewWorkspaceConflicts());
        File.WriteAllText(path, File.ReadAllText(path).Replace("Local writing", "Chosen newer writing"));

        Assert.False(app.ResolveWorkspaceConflicts(review));
        Assert.False(app.CanSafelyClose);
        Assert.Contains("Delivered writing", File.ReadAllText(evidence));
        var updated = Assert.IsType<WorkspaceConflictReview>(app.ReviewWorkspaceConflicts());
        Assert.True(app.ResolveWorkspaceConflicts(updated), app.Status);
        Assert.Empty(app.GetWorkspaceConflicts());
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = Reopen();
        Assert.Empty(reopened.GetWorkspaceConflicts());
        Assert.Equal("Chosen newer writing", Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
        Assert.True(reopened.CanSafelyClose, reopened.Status);
        Assert.Contains("Delivered writing", File.ReadAllText(evidence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_publication_never_claims_clean_rejection_or_adopts_an_orphan_recovery(bool afterPublication)
    {
        var publisher = new DeliveringPublisher { AfterPublication = afterPublication };
        using var app = Open(publisher);
        var recovery = File.ReadAllBytes(Path.Combine(Folder, "recovery", "current.json"));
        publisher.Deliver = () => throw new IOException("Unconfirmed publication interruption");

        Assert.Null(app.SavePersonTextDraft(app.GetRelationships(Email)!.Person, PersonTextField.PreferredName, "Retained candidate"));

        Assert.Equal(WorkspacePublicationOutcome.Indeterminate, app.PublicationOutcome);
        Assert.Contains("outcome unknown", app.Status);
        Assert.DoesNotContain("NOT saved", app.Status);
        app.DiscardPersonTextDraft(app.GetRelationships(Email)!.Person, PersonTextField.PreferredName);
        Assert.Equal(WorkspacePublicationOutcome.Indeterminate, app.PublicationOutcome);
        Assert.False(app.CanSafelyClose);
        Assert.Equal(recovery, File.ReadAllBytes(Path.Combine(Folder, "recovery", "current.json")));
        using var reopened = Reopen();
        Assert.Equal(WorkspacePublicationOutcome.Indeterminate, reopened.PublicationOutcome);
        Assert.False(reopened.CanSafelyClose);
        Assert.Equal(afterPublication ? "Retained candidate" : null, reopened.GetRelationships(Email)!.Person.PreferredName);
        Assert.False(reopened.RetryStorageMaintenance());
        Assert.Equal(recovery, File.ReadAllBytes(Path.Combine(Folder, "recovery", "current.json")));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("deleted")]
    [InlineData("backup-collision")]
    [InlineData("partial-replacement")]
    public void Publication_modes_preserve_real_creation_deletion_collision_and_partial_failure_evidence(string boundary)
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        var original = app.CreatePersonNote(Email, "Context", "Original writing")!;
        var draft = app.BeginNoteDraft(NoteScope.Person, Email, boundary == "created" ? null : original.Id)!;
        var path = Path.Combine(Folder, "notes", draft.Id + ".md");
        var originalBytes = File.ReadAllBytes(Path.Combine(Folder, "notes", original.Id + ".md"));
        var delivered = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(originalBytes)
            .Replace(original.Id.ToString(), draft.Id.ToString()).Replace("Original writing", "Delivered writing"));
        var recovery = File.ReadAllBytes(Path.Combine(Folder, "recovery", "current.json"));
        publisher.DestinationFileName = Path.GetFileName(path);
        publisher.Deliver = () =>
        {
            if (boundary == "created") File.WriteAllBytes(path, delivered);
            if (boundary == "deleted") File.Delete(path);
            if (boundary == "backup-collision") File.WriteAllText(publisher.BackupPath!, "Provider collision bytes");
            if (boundary == "partial-replacement")
            {
                File.Move(path, publisher.BackupPath!);
                throw new IOException("Controlled partial replacement", unchecked((int)0x80070499));
            }
        };

        Assert.Null(app.SaveNoteDraft(draft, "Retained title", "Retained stage writing"));

        Assert.False(app.CanSafelyClose);
        Assert.Equal(recovery, File.ReadAllBytes(Path.Combine(Folder, "recovery", "current.json")));
        if (boundary == "created") Assert.Equal(delivered, File.ReadAllBytes(path));
        if (boundary == "deleted") Assert.False(File.Exists(path));
        if (boundary == "backup-collision")
        {
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Equal("Provider collision bytes", File.ReadAllText(publisher.BackupPath!));
        }
        Assert.Equal(boundary == "partial-replacement" ? WorkspacePublicationOutcome.Indeterminate :
            WorkspacePublicationOutcome.NotPublished, app.PublicationOutcome);
        if (boundary == "partial-replacement")
        {
            using var reopened = Reopen();
            var conflict = Assert.Single(reopened.GetWorkspaceConflicts());
            Assert.Equal(WorkspacePublicationOutcome.Indeterminate, conflict.Outcome);
            Assert.Contains(conflict.LocalWritingPaths, retained => File.ReadAllText(retained).Contains("Retained stage writing"));
            Assert.Equal(originalBytes, File.ReadAllBytes(publisher.BackupPath!));
            Assert.False(reopened.CanSafelyClose);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changed_captured_evidence_blocks_acknowledgement_without_replacing_current_writing(bool replaceSeal)
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        var delivery = PrepareProfileDelivery(Email, PersonTextField.PreferredName, "Delivered Avery");
        publisher.Deliver = () => Deliver(delivery);
        Assert.True(app.EditPreferredName(Email, "Local Avery"), app.Status);
        var review = Assert.IsType<WorkspaceConflictReview>(app.ReviewWorkspaceConflicts());
        var evidence = Assert.Single(app.GetWorkspaceConflicts()).Evidence[0];
        File.WriteAllText(evidence.Path, "Damaged evidence");
        if (replaceSeal)
            File.WriteAllText(evidence.Path + ".sha256",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(evidence.Path))));
        Assert.False(app.ResolveWorkspaceConflicts(review));
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Local Avery", app.GetRelationships(Email)!.Person.PreferredName);
        Assert.Equal("Damaged evidence", File.ReadAllText(evidence.Path));
        Assert.Single(app.GetWorkspaceConflicts());
    }

    [Fact]
    public async Task Review_regression_conflict_snapshot_reads_do_not_wait_for_publication()
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        var delivery = PrepareProfileDelivery(Email, PersonTextField.PreferredName, "Delivered Avery");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        publisher.Deliver = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "Publication was not released.");
            Deliver(delivery);
        };
        var save = Task.Factory.StartNew(() => app.EditPreferredName(Email, "Local Avery"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<IReadOnlyList<WorkspaceConflict>>? read = null;
        var saved = false;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "Save did not reach publication.");
            read = Task.Factory.StartNew(() =>
                {
                    _ = app.CanNavigatePeople;
                    return app.GetWorkspaceConflicts();
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.Empty(await read.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            saved = await save;
            if (read is not null) await read;
        }
        Assert.True(saved, app.Status);
        Assert.Single(app.GetWorkspaceConflicts());
        Assert.Empty(await read!);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("person-note")]
    [InlineData("team-note")]
    public void Review_regression_sibling_unconfirmed_publication_blocks_a_stale_clients_writer(string operation)
    {
        var publisher = new DeliveringPublisher();
        using var owner = Open(publisher);
        var clock = new CallbackClock();
        using var client = ApplicationWorkflow.CreateFileBacked(timeProvider: clock);
        Assert.True(client.OpenWorkspace(Folder), client.Status);
        var scope = operation == "team-note" ? NoteScope.Team : NoteScope.Person;
        var draft = client.BeginNoteDraft(scope, Email)!;
        Dictionary<string, byte[]>? retained = null;
        // The clock callback holds this client's public edit gate, so its watcher cannot refresh first.
        clock.OnRead = () =>
        {
            publisher.Deliver = () => throw new IOException("Unconfirmed sibling publication");
            Assert.False(owner.EditPreferredName(Email, "Unconfirmed writing"));
            Assert.Equal(WorkspacePublicationOutcome.Indeterminate, owner.PublicationOutcome);
            Assert.Empty(client.GetWorkspaceConflicts());
            retained = WorkspaceFixtures.Bytes(Folder);
            if (operation != "profile")
                Assert.Null(client.SaveNoteDraft(draft, "Retained title", "Must remain unsaved"));
        };

        Assert.False(client.EditPreferredName(Email, "Must not publish"));

        Assert.NotNull(retained);
        AssertPreserved(retained);
        Assert.Single(client.GetWorkspaceConflicts());
        Assert.False(client.CanSafelyClose);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("originalPath")]
    [InlineData("localWritingPath")]
    public void Review_regression_null_publication_provenance_is_rejected_without_crashing(string missing)
    {
        Guid conflictId;
        var publisher = new DeliveringPublisher();
        using (var app = Open(publisher))
        {
            var note = app.CreatePersonNote(Email, "Context", "Original writing")!;
            var path = Path.Combine(Folder, "notes", note.Id + ".md");
            publisher.DestinationFileName = Path.GetFileName(path);
            publisher.Deliver = () => File.WriteAllText(path, File.ReadAllText(path).Replace("Original writing", "Delivered writing"));
            Assert.True(app.EditPersonNote(note.Id, "Local", "Local writing"), app.Status);
            conflictId = Assert.Single(app.GetWorkspaceConflicts()).Id;
        }
        var transaction = Path.Combine(Folder, "transactions", conflictId.ToString("N"));
        var record = Path.Combine(transaction, missing == "localWritingPath" ? "publication.json" : "publication.checked.json");
        var data = JsonNode.Parse(File.ReadAllBytes(record))!;
        if (missing == "localWritingPath") data["localWritingPaths"]![0] = null;
        else if (missing == "evidence") data["evidence"]![0] = null;
        else data["evidence"]![0]!["originalPath"] = null;
        File.WriteAllText(record, data.ToJsonString());
        var retained = WorkspaceFixtures.Bytes(Folder);
        using var reopened = ApplicationWorkflow.CreateFileBacked();

        Assert.False(reopened.OpenWorkspace(Folder));

        Assert.False(reopened.CanSafelyClose);
        Assert.Contains("journal", reopened.LocalSaveError);
        AssertPreserved(retained);
    }

    [Theory]
    [InlineData("publication.base.json", false)]
    [InlineData("publication.base.json", true)]
    [InlineData("publication.displaced", false)]
    [InlineData("publication.displaced", true)]
    public void Review_regression_changed_retained_provenance_requires_a_new_acknowledgement(string name, bool delete)
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        var note = app.CreatePersonNote(Email, "Context", "Original writing")!;
        var path = Path.Combine(Folder, "notes", note.Id + ".md");
        publisher.DestinationFileName = Path.GetFileName(path);
        publisher.Deliver = () => File.WriteAllText(path, File.ReadAllText(path).Replace("Original writing", "Delivered writing"));
        Assert.True(app.EditPersonNote(note.Id, "Local", "Local writing"), app.Status);
        var review = Assert.IsType<WorkspaceConflictReview>(app.ReviewWorkspaceConflicts());
        var retained = Path.Combine(Folder, "transactions", Assert.Single(app.GetWorkspaceConflicts()).Id.ToString("N"), name);
        Assert.True(File.Exists(retained));
        if (delete) File.Delete(retained);
        else File.WriteAllText(retained, "Changed retained provenance");

        Assert.False(app.ResolveWorkspaceConflicts(review));

        Assert.Single(app.GetWorkspaceConflicts());
        Assert.False(app.CanSafelyClose);
    }

    [Fact]
    public void Review_regression_newly_arrived_displaced_provenance_requires_a_new_acknowledgement()
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        publisher.Deliver = () => throw new IOException("Unconfirmed publication before replacement");
        Assert.False(app.EditPreferredName(Email, "Retained writing"));
        var review = Assert.IsType<WorkspaceConflictReview>(app.ReviewWorkspaceConflicts());
        var path = Path.Combine(Folder, "transactions", Assert.Single(app.GetWorkspaceConflicts()).Id.ToString("N"), "publication.displaced");
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "Newly delivered provenance");

        Assert.False(app.ResolveWorkspaceConflicts(review));

        Assert.Single(app.GetWorkspaceConflicts());
        Assert.False(app.CanSafelyClose);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Review_regression_resolved_unconfirmed_note_preserves_its_stage_and_allows_the_next_edit(NoteScope scope)
    {
        var publisher = new DeliveringPublisher();
        using var app = Open(publisher);
        var original = app.SaveNoteDraft(app.BeginNoteDraft(scope, Email)!, "Context", "Original writing")!;
        publisher.DestinationFileName = original.Id + ".md";
        publisher.Deliver = () => throw new IOException("Unconfirmed note publication before replacement");
        Assert.Null(app.SaveNoteDraft(original, "Retained title", "Retained stage writing"));
        var stage = publisher.StagedPath!;
        var retained = File.ReadAllBytes(stage);
        app.DiscardUnsavedChanges(original.Id);
        var review = Assert.IsType<WorkspaceConflictReview>(app.ReviewWorkspaceConflicts());
        Assert.True(app.ResolveWorkspaceConflicts(review), app.Status);
        Assert.Equal(retained, File.ReadAllBytes(stage));

        var saved = app.SaveNoteDraft(app.BeginNoteDraft(scope, Email, original.Id)!, "Chosen title", "New intentional writing");

        Assert.NotNull(saved);
        Assert.Equal("New intentional writing", saved.Markdown);
        Assert.Equal(retained, File.ReadAllBytes(stage));
        Assert.True(app.CanSafelyClose, app.Status);
        using var reopened = Reopen();
        Assert.Equal("New intentional writing", reopened.BeginNoteDraft(scope, Email, original.Id)!.Markdown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review_regression_verified_sibling_resolution_clears_only_the_conflict_failure(bool retainDraft)
    {
        var publisher = new DeliveringPublisher();
        using var owner = Open(publisher);
        publisher.Deliver = () => throw new IOException("Unconfirmed publication before replacement");
        Assert.False(owner.EditPreferredName(Email, "Unconfirmed writing"));
        using var client = Reopen();
        Assert.Single(client.GetWorkspaceConflicts());
        var draft = client.BeginNoteDraft(NoteScope.Person, Email)!;
        if (retainDraft) client.RetainNoteDraft(draft);
        var review = Assert.IsType<WorkspaceConflictReview>(owner.ReviewWorkspaceConflicts());
        Assert.True(owner.ResolveWorkspaceConflicts(review), owner.Status);

        Assert.True(client.RefreshWorkspace(), client.Status);

        Assert.Empty(client.GetWorkspaceConflicts());
        Assert.Null(client.LocalSaveError);
        Assert.DoesNotContain("external conflict unresolved", client.Status);
        Assert.Equal(!retainDraft, client.CanSafelyClose);
        client.DiscardUnsavedChanges(draft.Id);
        Assert.True(client.CanSafelyClose, client.Status);
    }

    private Dictionary<string, byte[]> PrepareProfileDelivery(string email, PersonTextField field, string value)
    {
        var producerFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        WorkspaceFixtures.Copy(Folder, producerFolder);
        using var producer = ApplicationWorkflow.CreateFileBacked();
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
        var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher);
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Population).Id), app.Status);
        return app;
    }

    private IApplicationWorkflow Reopen()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
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
        public string DestinationFileName { get; set; } = "workspace.json";
        public string? BackupPath { get; private set; }
        public string? StagedPath { get; private set; }
        public Action? Deliver { get; set; }

        public void Publish(string staged, string destination, string? backup, WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
        {
            var delivery = Path.GetFileName(destination) == DestinationFileName ? Deliver : null;
            if (delivery is not null) { Deliver = null; BackupPath = backup; StagedPath = staged; }
            if (!AfterPublication) delivery?.Invoke();
            new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
            if (AfterPublication) delivery?.Invoke();
        }
    }

    private sealed class CallbackClock : TimeProvider
    {
        public Action? OnRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            var callback = OnRead;
            OnRead = null;
            callback?.Invoke();
            return TimeProvider.System.GetUtcNow();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
