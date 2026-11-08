using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class HistoricalTeamContextTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Batch(string records, int id, string observed = "2026-11-01T17:00:00Z") => $$"""
        {"schemaVersion":1,"batchId":"10000000-0011-4000-8000-{{id:000000000000}}",
         "observedAt":"{{observed}}","producer":"Fictitious directory","people":[{{records}}]}
        """;

    private static void Apply(IApplicationWorkflow app, string json)
    {
        var preview = app.PreviewImport(json);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    private static IApplicationWorkflow Open(Clock clock)
    {
        var app = ApplicationWorkflow.CreateInMemory(timeProvider: clock);
        Assert.True(app.OpenWorkspace("fictitious-historical-context"));
        Apply(app, Batch("""
            {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"casey@example.com"},
            {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
            """, 1));
        return app;
    }

    [Fact]
    public void Direct_transfer_freezes_attributed_old_chain_and_note_versions_separately_from_current_context()
    {
        var clock = new Clock();
        using var app = Open(clock);
        var personNote = app.CreatePersonNote("avery@example.com", "Individual", "Private Person writing")!;
        var direct = app.CreateTeamNote("morgan@example.com", "Morgan context", "# Old team")!;
        app.CreateTeamNote("casey@example.com", "Casey context", "Old ancestor");
        app.CreateTeamNote("alex@example.com", "Alex context", "New team");
        Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
        clock.Now = clock.Now.AddDays(1);
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""",
            2, "2026-11-06T17:00:00Z"));

        var capture = Assert.Single(app.GetHistoricalTeamContexts("AVERY@example.com"));
        Assert.Equal(new[] { "morgan@example.com", "casey@example.com" }, capture.ReportingChain.Select(p => p.Email));
        Assert.Equal(new[] { "# Old team", "Old ancestor" }, capture.TeamNotes.Select(n => n.Markdown));
        var frozen = capture.TeamNotes[0];
        Assert.Equal(direct.Id, frozen.Id);
        Assert.Equal("morgan@example.com", frozen.OwnerEmail);
        Assert.Equal(direct.UpdatedAt, frozen.UpdatedAt);
        Assert.NotEmpty(frozen.Version);
        Assert.Equal(clock.Now, capture.CapturedAt);
        var cause = Assert.Single(capture.Transitions);
        Assert.Equal("avery@example.com", cause.PersonEmail);
        Assert.Equal(ProfileChangeSource.Import, cause.Change.Evidence.Source);
        Assert.Equal("Fictitious directory", cause.Change.Evidence.Producer);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), cause.Change.ReportingInterval!.After);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"), cause.Change.ReportingInterval.By);
        Assert.Equal("New team", Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.Markdown);
        Assert.True(app.EditTeamNote(direct.Id, "Edited title", "Changed later"));
        Assert.True(app.EditPreferredName("morgan@example.com", "Renamed later"));
        Assert.Equal("# Old team", Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).TeamNotes[0].Markdown);
        Assert.Equal("Morgan", capture.ReportingChain[0].Heading);
        Assert.Equal(personNote, Assert.Single(app.GetPersonNotes("avery@example.com")));
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""",
            2, "2026-11-06T17:00:00Z"));
        Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
    }

    [Fact]
    public void Ancestor_move_and_late_same_batch_Manager_close_only_the_coherent_old_chain_once()
    {
        using var app = Open(new Clock());
        app.CreateTeamNote("morgan@example.com", "Morgan context", "Old direct");
        app.CreateTeamNote("casey@example.com", "Casey context", "Old root");
        var transfer = Batch("""
            {"email":"morgan@example.com","managerEmail":"new@example.com","displayName":"Morgan renamed"},
            {"email":"casey@example.com","managerEmail":"alex@example.com"},
            {"email":"new-report@example.com","displayName":"New report","managerEmail":"new@example.com"},
            {"email":"new@example.com","displayName":"New Manager","managerEmail":"alex@example.com"}
            """, 2, "2026-11-06T17:00:00Z");
        Apply(app, transfer);
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal(new[] { "morgan@example.com", "casey@example.com" }, capture.ReportingChain.Select(p => p.Email));
        Assert.Equal("Morgan", capture.ReportingChain[0].Heading);
        Assert.Equal(new[] { "Old direct", "Old root" }, capture.TeamNotes.Select(n => n.Markdown));
        Assert.Equal(new[] { "morgan@example.com", "casey@example.com" }, capture.Transitions.Select(t => t.PersonEmail));
        Assert.All(capture.Transitions, t => Assert.NotNull(t.Change.ReportingInterval));
        Assert.Equal("morgan@example.com", app.GetRelationships("avery@example.com")!.Person.ManagerEmail);
        Assert.Equal("morgan@example.com", Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Owner.Email);
        Assert.True(Assert.Single(app.GetInheritedTeamNotes("avery@example.com")).Note.NeedsReview);
        Assert.False(capture.TeamNotes[0].NeedsReview);
        Assert.Empty(app.GetHistoricalTeamContexts("new@example.com"));
        Assert.Empty(app.GetHistoricalTeamContexts("new-report@example.com"));
        Apply(app, transfer);
        Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Apply(app, Batch("""{"email":"morgan@example.com","managerEmail":"NEW@example.com","officeNumber":"007"}""", 3));
        Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
    }

    [Fact]
    public void Returning_to_a_former_chain_creates_a_new_capture_without_reusing_old_note_text()
    {
        using var app = Open(new Clock());
        var note = app.CreateTeamNote("morgan@example.com", "Context", "Version one")!;
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 2));
        Assert.True(app.EditTeamNote(note.Id, "Context", "Version two"));
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"morgan@example.com"}""", 3));
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":null}""", 4));
        var captures = app.GetHistoricalTeamContexts("avery@example.com");
        Assert.Equal(3, captures.Count);
        Assert.Equal("Version one", captures[0].TeamNotes[0].Markdown);
        Assert.Empty(captures[1].TeamNotes);
        Assert.Equal("Version two", captures[2].TeamNotes[0].Markdown);
        Assert.NotEqual(captures[0].TeamNotes[0].Version, captures[2].TeamNotes[0].Version);
        var chain = Assert.IsAssignableFrom<IList<PersonProfile>>(captures[0].ReportingChain);
        Assert.Throws<NotSupportedException>(() => chain[0] = chain[0] with { DirectoryName = "Tampered" });
        Assert.Equal("Morgan", app.GetHistoricalTeamContexts("avery@example.com")[0].ReportingChain[0].Heading);
    }

    [Fact]
    public void Older_reporting_evidence_shows_detection_and_local_note_processing_not_a_fabricated_transfer_instant()
    {
        var clock = new Clock();
        using var app = Open(clock);
        var note = app.CreateTeamNote("morgan@example.com", "Context", "Saved writing")!;
        Assert.True(app.EditPersonText("morgan@example.com", PersonTextField.JobTitle, "Director"));
        Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"morgan@example.com"}""",
            2, "2026-11-06T17:00:00Z"));
        Apply(app, Batch("""{"email":"avery@example.com","officeNumber":"007B"}""",
            3, "2026-11-07T17:00:00Z"));
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""",
            4, "2026-11-03T17:00:00Z"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal(note.Id, Assert.Single(capture.TeamNotes).Id);
        Assert.True(capture.TeamNotes[0].NeedsReview);
        Assert.Null(capture.Transitions[0].Change.ReportingInterval);
        Assert.True(capture.Transitions[0].Change.IsStaleEvidence);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"),
            capture.Transitions[0].Change.PreviousEvidence!.ObservedAt);
        Assert.Contains("Historical", capture.DisplayProvenance);
        Assert.Contains("locally known at processing time", capture.DisplayProvenance);
        Assert.Contains("unknown actual transfer instant", capture.DisplayProvenance);
        Assert.Contains("Detected 2026-11-08T12:00:00Z; effective date unknown", capture.DisplayProvenance);
        Assert.DoesNotContain("Observed interval:", capture.DisplayProvenance);
    }

    [Fact]
    public void Re_review_after_a_local_note_edit_captures_the_latest_saved_version_not_the_stale_preview()
    {
        using var app = Open(new Clock());
        var note = app.CreateTeamNote("morgan@example.com", "Context", "Before preview")!;
        var move = Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 2);
        var preview = app.PreviewImport(move);
        Assert.True(app.EditTeamNote(note.Id, "Context", "After preview"));
        Assert.False(app.ApplyImport(preview.Id));
        Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
        Apply(app, move);
        Assert.Equal("After preview", Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).TeamNotes[0].Markdown);
    }
}
