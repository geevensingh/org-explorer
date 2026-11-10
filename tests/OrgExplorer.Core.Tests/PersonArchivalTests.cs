using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class PersonArchivalTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-08T12:00:00Z");
    }

    private static string Batch(string records, int id, string observed = "2026-11-01T17:00:00Z") => $$"""
        {"schemaVersion":1,"batchId":"10000000-0012-4000-8000-{{id:000000000000}}",
         "observedAt":"{{observed}}","producer":"Fictitious directory","people":[{{records}}]}
        """;

    private static void Apply(IApplicationWorkflow app, string json)
    {
        var preview = app.PreviewImport(json);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    private static IApplicationWorkflow Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory(timeProvider: new Clock()));
        Assert.True(app.OpenWorkspace("fictitious-archival"));
        Apply(app, Batch("""
            {"email":"avery@example.com","displayName":"Avery","preferredName":"Ave","alias":"avery",
             "jobTitle":"Engineer","department":"Research","officeNumber":"007B","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"casey@example.com"},
            {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
            """, 1));
        return app;
    }

    [Fact]
    public void Imported_archival_closes_current_context_and_freezes_retained_writing_without_a_reporting_move()
    {
        using var app = Open();
        var direct = app.CreatePersonNote("avery@example.com", "Individual", "Private retained Person writing")!;
        var owned = app.CreateTeamNote("avery@example.com", "Owned", "Retained owned Team writing")!;
        var inherited = app.CreateTeamNote("morgan@example.com", "Manager", "# Original Manager writing")!;
        app.CreateTeamNote("casey@example.com", "Ancestor", "Original ancestor writing");
        var before = app.GetRelationships("avery@example.com")!.Person;
        var archive = Batch("""{"email":"AVERY@example.com","archived":true}""", 2, "2026-11-06T17:00:00Z");
        Apply(app, archive);

        Assert.Equal(before with { Archived = true }, app.GetRelationships("avery@example.com")!.Person);
        Assert.DoesNotContain(app.ReportingRoots.SelectMany(r => r.Reports).SelectMany(r => r.Reports),
            r => r.Person.Email == "avery@example.com");
        Assert.Empty(app.GetInheritedTeamNotes("avery@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal(new[] { "morgan@example.com", "casey@example.com" }, capture.ReportingChain.Select(p => p.Email));
        Assert.Equal(new[] { "# Original Manager writing", "Original ancestor writing" }, capture.TeamNotes.Select(n => n.Markdown));
        var cause = Assert.Single(capture.Transitions);
        Assert.Equal("avery@example.com", cause.PersonEmail);
        Assert.Equal("archived", cause.Change.Field);
        Assert.False(cause.Change.OldValue.GetBoolean());
        Assert.True(cause.Change.NewValue.GetBoolean());
        Assert.Equal(ProfileChangeSource.Import, cause.Change.Evidence.Source);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"), cause.Change.Evidence.ObservedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T12:00:00Z"), capture.CapturedAt);
        Assert.Contains("Archival closure", capture.DisplayProvenance);
        Assert.Contains("effective date unknown", capture.DisplayProvenance);
        Assert.True(app.EditTeamNote(inherited.Id, "Changed later", "Later Manager writing"));
        Assert.Equal("# Original Manager writing", Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).TeamNotes[0].Markdown);
        Assert.Equal(direct, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Assert.Equal(owned.Markdown, Assert.Single(app.GetTeamNotes("avery@example.com")).Markdown);
        Assert.True(Assert.Single(app.GetTeamNotes("avery@example.com")).NeedsReview);
        Apply(app, archive);
        Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
    }

    [Fact]
    public void Local_status_actions_validate_reports_without_cascade_and_explicit_Manager_changes_allow_safe_archival()
    {
        using var app = Open();
        var note = app.CreateTeamNote("morgan@example.com", "Manager", "Old team")!;
        var before = app.People.ToArray();
        Assert.False(app.EditPersonArchived("MORGAN@example.com", true));
        Assert.Contains("avery@example.com.managerEmail", app.Status);
        Assert.Equal(before, app.People);
        Assert.Empty(app.GetHistoricalTeamContexts("morgan@example.com"));
        Assert.Empty(app.GetProfileTimeline("morgan@example.com")!.Changes);
        Assert.False(Assert.Single(app.GetTeamNotes("morgan@example.com")).NeedsReview);
        Assert.True(app.CanSafelyClose);

        Assert.False(app.EditPersonManager("avery@example.com", "unknown@example.com"));
        Assert.False(app.EditPersonManager("morgan@example.com", "avery@example.com"));
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"), app.Status);
        Assert.True(app.EditPersonArchived("morgan@example.com", true), app.Status);
        Assert.Equal("alex@example.com", app.GetRelationships("avery@example.com")!.Person.ManagerEmail);
        Assert.False(app.GetRelationships("avery@example.com")!.Person.Archived);
        Assert.Equal("morgan@example.com", Assert.Single(app.ArchivedPeople).Email);
        Assert.Equal(note.Markdown, Assert.Single(app.GetTeamNotes("morgan@example.com")).Markdown);
        var closure = Assert.Single(app.GetHistoricalTeamContexts("morgan@example.com"));
        Assert.Equal(ProfileChangeSource.LocalEdit, Assert.Single(closure.Transitions).Change.Evidence.Source);
        Assert.Null(closure.Transitions[0].Change.Evidence.ObservedAt);
        var count = app.GetProfileTimeline("morgan@example.com")!.Changes.Count;
        Assert.True(app.EditPersonArchived("morgan@example.com", true));
        Assert.Single(app.GetHistoricalTeamContexts("morgan@example.com"));
        Assert.Equal(count, app.GetProfileTimeline("morgan@example.com")!.Changes.Count);
    }

    [Fact]
    public void Omission_preserves_archived_status_photo_notes_and_history_and_false_requires_a_complete_valid_reactivation()
    {
        using var app = Open();
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        try
        {
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
            var photo = Path.Combine(folder, "avery.png");
            File.WriteAllBytes(photo, png);
            Assert.True(app.EditPhoto("avery@example.com", photo), app.Status);
            File.Delete(photo);
            var note = app.CreatePersonNote("avery@example.com", "Retained", "# Individual")!;
            app.CreateTeamNote("morgan@example.com", "Manager", "Old Manager context");
            Assert.True(app.EditPersonArchived("avery@example.com", true));
            var archivedEvidence = Assert.Single(app.GetProfileTimeline("avery@example.com")!.Confirmations, e => e.Field == "archived");
            Apply(app, Batch("""{"email":"alex@example.com","officeCountry":"Canada"}""", 2));
            Apply(app, Batch("""{"email":"avery@example.com","officeState":"Washington"}""", 3));
            Apply(app, Batch("""{"email":"avery@example.com"}""", 4));
            Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "0009C"));
            Assert.True(Assert.Single(app.ArchivedPeople).Archived);
            Assert.Equal(archivedEvidence, Assert.Single(app.GetProfileTimeline("avery@example.com")!.Confirmations, e => e.Field == "archived"));
            Assert.Equal(png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
            Assert.Equal(note, Assert.Single(app.GetPersonNotes("avery@example.com")));
            var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
            Apply(app, Batch("""{"email":"morgan@example.com","archived":true}""", 5));
            var invalid = app.PreviewImport(Batch("""{"email":"avery@example.com","archived":false}""", 6));
            Assert.False(invalid.CanApply);
            Assert.False(app.ApplyImport(invalid.Id));
            Assert.True(app.GetRelationships("avery@example.com")!.Person.Archived);
            Apply(app, Batch("""
                {"email":"avery@example.com","archived":false},
                {"email":"morgan@example.com","archived":false}
                """, 7));
            Assert.Empty(app.ArchivedPeople);
            Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
            Assert.Equal("Old Manager context", Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.Markdown);
            Assert.Equal("0009C", app.GetRelationships("avery@example.com")!.Person.OfficeNumber);
            Assert.Equal(png, app.GetPhoto("avery@example.com")!.Bytes.ToArray());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("\"archived\":true", "morgan@example.com", true)]
    [InlineData("\"managerEmail\":null", null, false)]
    [InlineData("\"managerEmail\":\"alex@example.com\"", "alex@example.com", false)]
    public void Manager_archival_requires_explicit_report_archival_clear_or_reassignment_without_changing_omitted_people(
        string reportPatch, string? expectedManager, bool expectedArchived)
    {
        using var app = Open();
        app.CreateTeamNote("morgan@example.com", "Manager", "Old Manager context");
        var omitted = app.GetRelationships("casey@example.com")!.Person;
        Apply(app, Batch($$"""
            {"email":"morgan@example.com","archived":true},
            {"email":"avery@example.com",{{reportPatch}}}
            """, 2));
        Assert.True(app.GetRelationships("morgan@example.com")!.Person.Archived);
        Assert.Equal(expectedArchived, app.GetRelationships("avery@example.com")!.Person.Archived);
        Assert.Equal(expectedManager, app.GetRelationships("avery@example.com")!.Person.ManagerEmail);
        Assert.Equal(omitted, app.GetRelationships("casey@example.com")!.Person);
        Assert.Equal(4, app.People.Count);
        Assert.Single(app.GetHistoricalTeamContexts("morgan@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal("Old Manager context", Assert.Single(capture.TeamNotes).Markdown);
        Assert.Equal(expectedArchived ? "archived" : "managerEmail", Assert.Single(capture.Transitions).Change.Field);
    }

    [Fact]
    public void Root_archival_and_combined_move_archival_capture_once_while_archived_creation_and_reactivation_are_baselines()
    {
        using var app = Open();
        app.CreateTeamNote("morgan@example.com", "Manager", "Old context");
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":null,"archived":true}""", 2));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal(new[] { "managerEmail", "archived" }.Order(),
            capture.Transitions.Select(t => t.Change.Field).Order());
        Assert.Equal("Old context", Assert.Single(capture.TeamNotes).Markdown);
        Apply(app, Batch("""
            {"email":"root@example.com","displayName":"Root","managerEmail":null},
            {"email":"former@example.com","displayName":"Former","managerEmail":null,"archived":true}
            """, 3));
        Assert.Empty(app.GetHistoricalTeamContexts("former@example.com"));
        Assert.True(app.EditPersonArchived("root@example.com", true));
        var root = Assert.Single(app.GetHistoricalTeamContexts("root@example.com"));
        Assert.Empty(root.ReportingChain);
        Assert.Empty(root.TeamNotes);
        Assert.Equal("archived", Assert.Single(root.Transitions).Change.Field);
        Assert.True(app.EditPersonArchived("former@example.com", false));
        Assert.Empty(app.GetHistoricalTeamContexts("former@example.com"));
    }

    [Fact]
    public void Explicit_false_does_not_allow_archived_reporting_cycles_to_enter_the_current_org()
    {
        using var app = Open();
        Apply(app, Batch("""
            {"email":"avery@example.com","archived":true},
            {"email":"morgan@example.com","archived":true,"managerEmail":"avery@example.com"}
            """, 2));
        var before = app.People;
        var preview = app.PreviewImport(Batch("""
            {"email":"avery@example.com","archived":false},
            {"email":"morgan@example.com","archived":false}
            """, 3));
        Assert.False(preview.CanApply);
        Assert.Contains("cycle", string.Join("\n", preview.Errors));
        Assert.Equal(before, app.People);
        Assert.False(app.EditPersonArchived("avery@example.com", false));
        Assert.True(app.EditPersonManager("avery@example.com", null));
        Assert.True(app.EditPersonArchived("avery@example.com", false));
        Assert.True(app.EditPersonArchived("morgan@example.com", false));
        Assert.Equal("avery@example.com", app.GetRelationships("morgan@example.com")!.Person.ManagerEmail);
    }

    [Fact]
    public void Archival_invalidates_a_reviewed_import_and_the_view_model_notifies_the_archived_projection()
    {
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious-archival-notifications"));
        Apply(app, Batch("""{"email":"avery@example.com","displayName":"Avery","managerEmail":null}""", 1));
        var preview = app.PreviewImport(Batch("""{"email":"avery@example.com","officeCountry":"Canada"}""", 2));
        var notifications = new List<string?>();
        app.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        Assert.Contains(nameof(app.ArchivedPeople), notifications);
        Assert.Single(app.ArchivedPeople);
        Assert.Empty(app.ReportingRoots);
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Null(app.GetRelationships("avery@example.com")!.Person.OfficeCountry);
    }
}
