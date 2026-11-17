using OrgExplorer.Workflow;
using OrgExplorer.TestFixtures;
using System.Text.Json;
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

    private IApplicationWorkflow Open(string name = "workspace")
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        workflows.Add(app);
        Assert.True(app.OpenWorkspace(Path.Combine(folder, name)), app.Status);
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
            {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":"casey@example.com"},
            {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
            """, 1));
        return app;
    }

    [Fact]
    public void Frozen_context_reopens_and_transfers_with_exact_identity_version_and_provenance_after_later_edits_and_replay()
    {
        var app = Seed();
        var note = app.CreateTeamNote("casey@example.com", "Former root", "# Original")!;
        var direct = app.CreatePersonNote("avery@example.com", "Individual", "Direct writing")!;
        var move = Batch("""{"email":"morgan@example.com","managerEmail":"alex@example.com"}""", 2, "2026-11-06T17:00:00Z");
        Apply(app, move);
        var before = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(app.EditTeamNote(note.Id, "New title", "# Changed later"), app.Status);
        Assert.True(app.ArchiveTeamNote(note.Id), app.Status);
        Assert.True(app.EditPreferredName("casey@example.com", "Later name"), app.Status);
        Apply(app, move);
        var receiver = Open("receiver");
        WorkspaceFixtures.JoinSnapshot(app, receiver, Path.Combine(folder, "transport"));
        foreach (var reopened in new[] { Open(), receiver })
        {
            var capture = Assert.Single(reopened.GetHistoricalTeamContexts("avery@example.com"));
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(capture));
            Assert.Equal("# Original", Assert.Single(capture.TeamNotes).Markdown);
            Assert.Equal("Casey", capture.ReportingChain[1].Heading);
            Assert.Empty(reopened.GetInheritedTeamNotes("avery@example.com"));
            Assert.Equal(direct, Assert.Single(reopened.GetPersonNotes("avery@example.com")));
            Assert.Equal("# Changed later", Assert.Single(reopened.GetTeamNotes("casey@example.com", true)).Markdown);
            Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), capture.Transitions[0].Change.ReportingInterval!.After);
            Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"), capture.Transitions[0].Change.ReportingInterval!.By);
        }
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
    public void Invalid_snapshot_capture_retains_last_valid_history_without_repairing_delivered_bytes(string path, string json)
    {
        var source = Seed();
        source.CreateTeamNote("casey@example.com", "Original", "Frozen writing");
        Apply(source, Batch("""{"email":"morgan@example.com","managerEmail":"alex@example.com"}""", 2));
        var app = Open("receiver");
        WorkspaceFixtures.JoinSnapshot(source, app, Path.Combine(folder, "transport"));
        var before = JsonSerializer.Serialize(Assert.Single(app.GetHistoricalTeamContexts("avery@example.com")));
        Assert.True(source.EditPreferredName("avery@example.com", "New revision"));
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        var file = WorkspaceFixtures.LatestSnapshot(source);
        var original = File.ReadAllBytes(file);
        WorkspaceFixtures.RewriteSnapshot(file, content => WorkspaceFixtures.EditSnapshotRow(content,
            "historicalContexts", "avery@example.com", document =>
            {
                var segments = path.Split('.');
                JsonNode target = document[0]!;
                foreach (var segment in segments.SkipLast(1))
                    target = int.TryParse(segment, out var index) ? target[index]! : target[segment]!;
                target[segments.Last()] = JsonNode.Parse(json);
            }));
        var rejected = File.ReadAllBytes(file);
        Assert.False(app.SyncNow());
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"))));
        Assert.Equal(rejected, File.ReadAllBytes(file));
        File.WriteAllBytes(file, original);
        Assert.True(app.SyncNow(), app.Synchronization.Error);
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"))));
    }

    [Fact]
    public void Failed_atomic_transfer_creates_neither_capture_nor_context_revision_and_recovery_then_retry_captures_once()
    {
        var app = Seed();
        app.CreateTeamNote("morgan@example.com", "Context", "Original");
        var move = Batch("""{"email":"morgan@example.com","managerEmail":"alex@example.com"}""", 2);
        var preview = app.PreviewImport(move);
        using (WorkspaceFixtures.LockLocalStore(Path.Combine(folder, "workspace")))
        {
            Assert.False(app.ApplyImport(preview.Id));
            Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
            Assert.False(Assert.Single(app.GetTeamNotes("morgan@example.com")).NeedsReview);
            var reopened = Open();
            Assert.Equal("casey@example.com", reopened.GetRelationships("morgan@example.com")!.Person.ManagerEmail);
            Assert.Empty(reopened.GetHistoricalTeamContexts("avery@example.com"));
        }
        Assert.True(app.ReconcileLocalSaves(), app.Status);
        Apply(app, move);
        Assert.Single(Open().GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(Assert.Single(app.GetTeamNotes("morgan@example.com")).NeedsReview);
        Assert.True(app.CanSafelyClose);
    }

    public void Dispose()
    {
        foreach (var app in workflows) app.Dispose();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
