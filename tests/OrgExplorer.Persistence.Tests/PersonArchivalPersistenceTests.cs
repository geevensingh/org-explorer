using OrgExplorer.Workflow;
using System.Text.Json.Nodes;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class PersonArchivalPersistenceTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private readonly List<IApplicationWorkflow> workflows = [];
    private string Workspace => Path.Combine(root, "workspace");

    private static string Batch(string records, int id) => $$"""
        {"schemaVersion":1,"batchId":"10000000-0012-4000-8000-{{id:000000000000}}",
         "observedAt":"2026-11-06T17:00:00Z","people":[{{records}}]}
        """;

    private IApplicationWorkflow Open()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        workflows.Add(app);
        Assert.True(app.OpenWorkspace(Workspace), app.Status);
        return app;
    }

    private static void Apply(IApplicationWorkflow app, string json)
    {
        var preview = app.PreviewImport(json);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    private IApplicationWorkflow Seed()
    {
        var app = Open();
        Apply(app, Batch("""
            {"email":"avery@example.com","displayName":"Avery","preferredName":"Ave","alias":"avery",
             "jobTitle":"Engineer","officeNumber":"007B","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"casey@example.com"},
            {"email":"casey@example.com","displayName":"Casey","managerEmail":null}
            """, 1));
        var photo = Path.Combine(root, "producer.png");
        File.WriteAllBytes(photo, Png);
        Assert.True(app.EditPhoto("avery@example.com", photo), app.Status);
        File.Delete(photo);
        return app;
    }

    [Fact]
    public void Archived_profile_photo_notes_and_frozen_context_reopen_without_the_producer_and_later_edits_cannot_rewrite_history()
    {
        var app = Seed();
        var before = app.GetRelationships("avery@example.com")!.Person;
        var personNote = app.CreatePersonNote("avery@example.com", "Individual", "# Retained individual writing")!;
        var owned = app.CreateTeamNote("avery@example.com", "Owned", "Owned writing")!;
        var managerNote = app.CreateTeamNote("morgan@example.com", "Manager", "# Frozen Manager writing")!;
        app.CreateTeamNote("casey@example.com", "Ancestor", "Frozen ancestor writing");
        Apply(app, Batch("""{"email":"avery@example.com","archived":true}""", 2));
        var original = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        app.Dispose();

        var reopened = Open();
        Assert.Equal(before with { Archived = true }, reopened.GetRelationships("avery@example.com")!.Person);
        Assert.Equal(Png, reopened.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal(personNote, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        Assert.Equal(owned.Markdown, Assert.Single(reopened.GetTeamNotes("avery@example.com")).Markdown);
        Assert.Empty(reopened.GetInheritedTeamNotes("avery@example.com"));
        var capture = Assert.Single(reopened.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal(original.Id, capture.Id);
        Assert.Equal("# Frozen Manager writing", capture.TeamNotes[0].Markdown);
        Assert.Equal("archived", Assert.Single(capture.Transitions).Change.Field);
        Assert.True(reopened.EditTeamNote(managerNote.Id, "Changed later", "Later Manager writing"));
        Apply(reopened, Batch("""{"email":"avery@example.com","officeCountry":"Canada"}""", 3));
        Apply(reopened, Batch("""{"email":"avery@example.com","archived":false}""", 4));
        var active = Open();
        Assert.False(active.GetRelationships("avery@example.com")!.Person.Archived);
        Assert.Equal(Png, active.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal("Later Manager writing", Assert.Single(active.GetInheritedTeamNotes("avery@example.com"),
            n => n.Owner.Email == "morgan@example.com").Note.Markdown);
        Assert.Equal(original.Id, Assert.Single(active.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.Equal("# Frozen Manager writing", active.GetHistoricalTeamContexts("avery@example.com")[0].TeamNotes[0].Markdown);
        Assert.Equal("Canada", active.GetRelationships("avery@example.com")!.Person.OfficeCountry);
        Assert.Equal(personNote, Assert.Single(active.GetPersonNotes("avery@example.com")));
    }

    [Fact]
    public void Failed_local_archive_and_reactivation_publish_no_partial_state_and_retry_preserves_retained_data()
    {
        var app = Seed();
        var direct = app.CreatePersonNote("avery@example.com", "Individual", "Retained writing")!;
        var owned = app.CreateTeamNote("avery@example.com", "Owned", "Owned writing")!;
        app.CreateTeamNote("morgan@example.com", "Manager", "Current Manager writing");
        var before = app.GetRelationships("avery@example.com")!.Person;
        using (new FileStream(Path.Combine(Workspace, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.EditPersonArchived("avery@example.com", true));
            Assert.False(app.CanSafelyClose);
            Assert.Contains("NOT saved", app.LocalSaveError);
            Assert.Equal(before, app.GetRelationships("avery@example.com")!.Person);
            Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
            Assert.False(Assert.Single(app.GetTeamNotes("avery@example.com")).NeedsReview);
            Assert.Empty(Open().GetHistoricalTeamContexts("avery@example.com"));
        }
        Assert.True(app.EditPersonArchived("avery@example.com", true), app.Status);
        Assert.True(app.CanSafelyClose);
        var archived = Open();
        Assert.Equal(before with { Archived = true }, Assert.Single(archived.ArchivedPeople));
        var capture = Assert.Single(archived.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal("Current Manager writing", Assert.Single(capture.TeamNotes).Markdown);
        Assert.Equal(direct, Assert.Single(archived.GetPersonNotes("avery@example.com")));
        Assert.Equal(owned.Markdown, Assert.Single(archived.GetTeamNotes("avery@example.com")).Markdown);
        Assert.Equal(Png, archived.GetPhoto("avery@example.com")!.Bytes.ToArray());
        using (new FileStream(Path.Combine(Workspace, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(archived.EditPersonArchived("avery@example.com", false));
            Assert.True(Assert.Single(archived.ArchivedPeople).Archived);
            Assert.Empty(archived.GetInheritedTeamNotes("avery@example.com"));
            Assert.True(Open().GetRelationships("avery@example.com")!.Person.Archived);
        }
        Assert.True(archived.EditPersonArchived("avery@example.com", false), archived.Status);
        Assert.True(archived.CanSafelyClose);
        var active = Open();
        Assert.Empty(active.ArchivedPeople);
        Assert.Equal(capture.Id, Assert.Single(active.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.Equal(Png, active.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal(direct, Assert.Single(active.GetPersonNotes("avery@example.com")));
    }

    [Fact]
    public void Complete_imported_Manager_and_report_archival_refreshes_coherently_without_duplicate_captures()
    {
        var writer = Seed();
        var direct = writer.CreatePersonNote("avery@example.com", "Individual", "Retained writing")!;
        var note = writer.CreateTeamNote("morgan@example.com", "Manager", "Frozen writing")!;
        var reader = Open();
        var before = writer.People;
        var invalid = writer.PreviewImport(Batch("""{"email":"morgan@example.com","archived":true}""", 2));
        Assert.False(invalid.CanApply);
        Assert.False(writer.ApplyImport(invalid.Id));
        Assert.Equal(before, Open().People);
        var archive = Batch("""
            {"email":"morgan@example.com","archived":true},
            {"email":"avery@example.com","managerEmail":null,"archived":true}
            """, 3);
        Apply(writer, archive);
        Assert.True(SpinWait.SpinUntil(() => reader.ArchivedPeople.Count == 2 &&
            reader.GetHistoricalTeamContexts("avery@example.com").Count == 1, TimeSpan.FromSeconds(10)), reader.Status);
        Assert.True(reader.IsWorkspaceCurrent);
        Assert.Empty(reader.GetInheritedTeamNotes("avery@example.com"));
        Assert.Equal(Png, reader.GetPhoto("avery@example.com")!.Bytes.ToArray());
        Assert.Equal(direct, Assert.Single(reader.GetPersonNotes("avery@example.com")));
        var capture = Assert.Single(reader.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal(2, capture.Transitions.Count);
        Assert.Equal("Frozen writing", Assert.Single(capture.TeamNotes).Markdown);
        Assert.True(writer.EditTeamNote(note.Id, "Manager", "Later writing"));
        Assert.True(SpinWait.SpinUntil(() => reader.GetTeamNotes("morgan@example.com")[0].Markdown == "Later writing",
            TimeSpan.FromSeconds(10)), reader.Status);
        Assert.Equal("Frozen writing", Assert.Single(reader.GetHistoricalTeamContexts("avery@example.com")).TeamNotes[0].Markdown);
        Apply(writer, archive);
        Apply(writer, Batch("""{"email":"avery@example.com","archived":false}""", 4));
        Assert.True(SpinWait.SpinUntil(() => !reader.GetRelationships("avery@example.com")!.Person.Archived,
            TimeSpan.FromSeconds(10)), reader.Status);
        Assert.Equal(capture.Id, Assert.Single(reader.GetHistoricalTeamContexts("avery@example.com")).Id);
        var reopened = Open();
        Assert.Equal(capture.Id, Assert.Single(reopened.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.Equal(2, reopened.GetHistoricalTeamContexts("avery@example.com")[0].Transitions.Count);
        Assert.Null(reopened.GetRelationships("avery@example.com")!.Person.ManagerEmail);
        Assert.True(reopened.GetRelationships("morgan@example.com")!.Person.Archived);
        Assert.Equal(direct, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
    }

    [Theory]
    [InlineData("personEmail", "\"morgan@example.com\"")]
    [InlineData("change.oldValue", "true")]
    [InlineData("change.newValue", "false")]
    [InlineData("change.newValue", "null")]
    [InlineData("change.evidence.value", "false")]
    [InlineData("change.evidence.field", "\"managerEmail\"")]
    public void Invalid_external_archival_evidence_is_stale_not_silently_accepted_and_valid_delivery_recovers(string path, string value)
    {
        var app = Seed();
        app.CreateTeamNote("morgan@example.com", "Manager", "Frozen writing");
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace, "workspace.json")))!;
        var directory = Path.Combine(Workspace, "generations", metadata["generation"]!.GetValue<string>(), "historical-team-context");
        var file = Directory.GetFiles(directory, "*.json").Single(f =>
            JsonNode.Parse(File.ReadAllText(f))!["email"]!.GetValue<string>() == "avery@example.com");
        var original = File.ReadAllText(file);
        var document = JsonNode.Parse(original)!;
        var segments = path.Split('.');
        JsonNode node = document["contexts"]![0]!["transitions"]![0]!;
        foreach (var segment in segments.SkipLast(1)) node = node[segment]!;
        node[segments.Last()] = JsonNode.Parse(value);
        File.WriteAllText(file, document.ToJsonString());
        Assert.False(app.RefreshWorkspace(), app.Status);
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Contains("STALE", app.Status);
        Assert.True(app.GetRelationships("avery@example.com")!.Person.Archived);
        Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.Equal(Png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        using var invalid = ApplicationWorkflow.CreateFileBacked();
        Assert.False(invalid.OpenWorkspace(Workspace));
        File.WriteAllText(file, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.True(app.EditPersonArchived("avery@example.com", false), app.Status);
        Assert.Equal(capture.Id, Assert.Single(Open().GetHistoricalTeamContexts("avery@example.com")).Id);
    }

    public void Dispose()
    {
        foreach (var workflow in workflows) workflow.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
