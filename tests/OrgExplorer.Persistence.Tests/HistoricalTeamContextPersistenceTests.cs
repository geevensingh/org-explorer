using OrgExplorer.Workflow;
using System.Text.Json.Nodes;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class HistoricalTeamContextPersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private readonly List<IApplicationWorkflow> workflows = [];

    private static string Batch(string records, int id, string observed = "2026-11-01T17:00:00Z") => $$"""
        {"schemaVersion":1,"batchId":"10000000-0011-4000-8000-{{id:000000000000}}",
         "observedAt":"{{observed}}","people":[{{records}}]}
        """;

    private IApplicationWorkflow Open(bool incremental = false)
    {
        var app = incremental ? ApplicationWorkflow.CreateIncrementalFileBacked() : ApplicationWorkflow.CreateFileBacked();
        workflows.Add(app);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        return app;
    }

    private static void Apply(IApplicationWorkflow app, string json)
    {
        var preview = app.PreviewImport(json);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    private IApplicationWorkflow Seed(bool incremental = false)
    {
        var app = Open(incremental);
        Apply(app, Batch("""
            {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"casey@example.com"},
            {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
            """, 1));
        return app;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_context_reopens_after_ancestor_move_later_note_edits_and_replay(bool incremental)
    {
        var app = Seed(incremental);
        var note = app.CreateTeamNote("casey@example.com", "Former root", "# Original")!;
        var direct = app.CreatePersonNote("avery@example.com", "Individual", "Direct writing")!;
        var move = Batch("""{"email":"morgan@example.com","managerEmail":"alex@example.com"}""", 2, "2026-11-06T17:00:00Z");
        Apply(app, move);
        var before = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(app.EditTeamNote(note.Id, "New title", "# Changed later"), app.Status);
        Assert.True(app.ArchiveTeamNote(note.Id), app.Status);
        Assert.True(app.EditPreferredName("casey@example.com", "Later name"), app.Status);
        Apply(app, move);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, new DateTime(2000, 1, 1));

        var reopened = Open(incremental);
        var capture = Assert.Single(reopened.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal(before.Id, capture.Id);
        Assert.Equal(before.CapturedAt, capture.CapturedAt);
        Assert.Equal("# Original", Assert.Single(capture.TeamNotes).Markdown);
        Assert.Equal(before.TeamNotes[0].Version, capture.TeamNotes[0].Version);
        Assert.Equal("Casey", capture.ReportingChain[1].Heading);
        Assert.Empty(reopened.GetInheritedTeamNotes("avery@example.com"));
        Assert.Equal(direct, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
        Assert.Equal("# Changed later", Assert.Single(reopened.GetTeamNotes("casey@example.com", true)).Markdown);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), capture.Transitions[0].Change.ReportingInterval!.After);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"), capture.Transitions[0].Change.ReportingInterval!.By);
    }

    private string CaptureFile()
    {
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "workspace.json")))!;
        var directory = Path.Combine(folder, "generations", metadata["generation"]!.GetValue<string>(), "historical-team-context");
        return Directory.GetFiles(directory, "*.json").Single(f =>
            JsonNode.Parse(File.ReadAllText(f))!["email"]!.GetValue<string>() == "avery@example.com");
    }

    [Theory]
    [InlineData("id", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("capturedAt", "\"2026-11-01T17:00:00+01:00\"")]
    [InlineData("personEmail", "\"other@example.com\"")]
    [InlineData("reportingChain", "null")]
    [InlineData("transitions", "[]")]
    [InlineData("teamNotes", "null")]
    [InlineData("teamNotes.0.markdown", "\"Silently rewritten\"")]
    [InlineData("teamNotes.0.ownerEmail", "\"alex@example.com\"")]
    [InlineData("reportingChain.0.managerEmail", "\"alex@example.com\"")]
    [InlineData("transitions.0.change.evidence.source", "42")]
    public void Invalid_external_capture_retains_last_valid_history_and_blocks_writes(string path, string json)
    {
        var app = Seed();
        app.CreateTeamNote("casey@example.com", "Original", "Frozen writing");
        Apply(app, Batch("""{"email":"morgan@example.com","managerEmail":"alex@example.com"}""", 2));
        var before = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        var file = CaptureFile();
        var original = File.ReadAllText(file);
        var document = JsonNode.Parse(original)!;
        var segments = path.Split('.');
        JsonNode target = document["contexts"]![0]!;
        foreach (var segment in segments.SkipLast(1))
            target = int.TryParse(segment, out var index) ? target[index]! : target[segment]!;
        target[segments.Last()] = JsonNode.Parse(json);
        File.WriteAllText(file, document.ToJsonString());
        Assert.False(app.RefreshWorkspace(), app.Status);
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Contains("STALE", app.Status);
        Assert.Equal(before.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.Equal("Frozen writing", Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).TeamNotes[0].Markdown);
        Assert.False(app.EditPreferredName("avery@example.com", "Unsaved"));
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(folder));
        File.WriteAllText(file, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(before.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
        app.DiscardUnsavedChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_atomic_transfer_publishes_neither_capture_nor_new_context_revision_and_retry_captures_once(bool incremental)
    {
        var app = Seed(incremental);
        app.CreateTeamNote("morgan@example.com", "Context", "Original");
        var move = Batch("""{"email":"morgan@example.com","managerEmail":"alex@example.com"}""", 2);
        var preview = app.PreviewImport(move);
        using (new FileStream(Path.Combine(folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.ApplyImport(preview.Id));
            Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
            Assert.False(Assert.Single(app.GetTeamNotes("morgan@example.com")).NeedsReview);
            var reopened = Open(incremental);
            Assert.Equal("casey@example.com", reopened.GetRelationships("morgan@example.com")!.Person.ManagerEmail);
            Assert.Empty(reopened.GetHistoricalTeamContexts("avery@example.com"));
        }
        Apply(app, move);
        Assert.Single(Open(incremental).GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(Assert.Single(app.GetTeamNotes("morgan@example.com")).NeedsReview);
        Assert.True(app.CanSafelyClose);
    }

    [Fact]
    public void Valid_watcher_delivery_loads_the_same_capture_without_synthesizing_another_transition()
    {
        var writer = Seed();
        var note = writer.CreateTeamNote("casey@example.com", "Context", "Original")!;
        var reader = Open();
        Apply(writer, Batch("""{"email":"morgan@example.com","managerEmail":"alex@example.com"}""", 2));
        Assert.True(SpinWait.SpinUntil(() => reader.GetHistoricalTeamContexts("avery@example.com").Count == 1,
            TimeSpan.FromSeconds(10)), reader.Status);
        var capture = Assert.Single(reader.GetHistoricalTeamContexts("avery@example.com"));
        Assert.Equal(Assert.Single(writer.GetHistoricalTeamContexts("avery@example.com")).Id, capture.Id);
        Assert.True(writer.EditTeamNote(note.Id, "Context", "Later"));
        Assert.True(SpinWait.SpinUntil(() => reader.GetTeamNotes("casey@example.com")[0].Markdown == "Later",
            TimeSpan.FromSeconds(10)), reader.Status);
        Assert.Equal("Original", Assert.Single(reader.GetHistoricalTeamContexts("avery@example.com")).TeamNotes[0].Markdown);
        Assert.Empty(reader.GetInheritedTeamNotes("avery@example.com"));
        Assert.True(reader.RefreshWorkspace());
        Assert.Single(reader.GetHistoricalTeamContexts("avery@example.com"));
    }

    [Fact]
    public void Legacy_unknown_confirmation_does_not_fabricate_observed_transfer_bounds()
    {
        var app = Seed();
        app.Dispose();
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "workspace.json")))!;
        var generation = Path.Combine(folder, "generations", metadata["generation"]!.GetValue<string>());
        Directory.Delete(Path.Combine(generation, "profile-history"), true);
        Directory.Delete(Path.Combine(generation, "historical-team-context"), true);
        OrgExplorer.TestFixtures.WorkspaceFixtures.WriteLegacyPointer(folder, metadata);
        var legacy = Open();
        Assert.Empty(legacy.GetHistoricalTeamContexts("avery@example.com"));
        Apply(legacy, Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 2));
        var capture = Assert.Single(Open().GetHistoricalTeamContexts("avery@example.com"));
        Assert.Null(capture.Transitions[0].Change.PreviousEvidence);
        Assert.Null(capture.Transitions[0].Change.ReportingInterval);
    }

    [Fact]
    public void A_declared_but_missing_historical_document_is_incomplete_delivery_not_a_history_free_Workspace()
    {
        var app = Seed();
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 2));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        var file = CaptureFile();
        var original = File.ReadAllText(file);
        File.Delete(file);
        Assert.False(app.RefreshWorkspace());
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(folder));
        Assert.Contains("waiting for complete delivery", reopened.Status);
        File.WriteAllText(file, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.Equal(capture.Id, Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")).Id);
    }

    public void Dispose()
    {
        foreach (var app in workflows) app.Dispose();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
