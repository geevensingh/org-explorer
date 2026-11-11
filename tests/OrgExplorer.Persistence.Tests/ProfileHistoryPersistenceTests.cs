using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class ProfileHistoryPersistenceTests : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private readonly List<IApplicationWorkflow> workflows = [];

    private static string Batch(string records, int id, string observed) => $$"""
        {"schemaVersion":1,"batchId":"10000000-0010-4000-8000-{{id:000000000000}}",
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structured_changes_confirmations_and_separate_dates_survive_reopen_and_failed_save_without_mtime_inference(bool incremental)
    {
        var app = Open(incremental);
        Apply(app, Batch("""
            {"email":"avery@example.com","displayName":"Avery","officeNumber":"007B","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
            """, 1, "2026-11-01T17:00:00Z"));
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"));
        Apply(app, Batch("""{"email":"avery@example.com","officeNumber":"0009C"}""", 2, "2026-11-03T17:00:00.1234567Z"));
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 3, "2026-11-06T17:00:00Z"));
        Apply(app, Batch("""{"email":"avery@example.com","officeNumber":"0009C"}""", 4, "2026-11-07T17:00:00Z"));
        var before = app.GetProfileTimeline("avery@example.com")!;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, new DateTime(2000, 1, 1));
        var reopened = Open(incremental);
        var timeline = reopened.GetProfileTimeline("AVERY@example.com")!;
        Assert.Equal(3, timeline.Changes.Count);
        Assert.Equal(ProfileChangeSource.LocalEdit, timeline.Changes[0].Evidence.Source);
        Assert.Equal(before.Changes.Select(c => c.Evidence.RecordedAt), timeline.Changes.Select(c => c.Evidence.RecordedAt));
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T17:00:00.1234567Z"), timeline.Changes[1].Evidence.ObservedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), timeline.Changes[2].ReportingInterval!.After);
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T17:00:00Z"),
            timeline.Confirmations.Single(o => o.Field == "officeNumber").ObservedAt);
        Assert.Equal(before.Observations.Count, timeline.Observations.Count);
        var stale = reopened.PreviewImport(Batch(
            """{"email":"avery@example.com","officeNumber":null}""", 5, "2026-11-02T17:00:00Z"));
        Assert.Contains(stale.Warnings, w => w.Contains("older evidence"));
        using (var lease = new FileStream(Path.Combine(folder, ".workspace.lock"), FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(reopened.ApplyImport(stale.Id));
            Assert.False(reopened.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "Unsaved"));
            Assert.Equal(3, reopened.GetProfileTimeline("avery@example.com")!.Changes.Count);
        }
        Assert.Equal("0009C", Open(incremental).GetRelationships("avery@example.com")!.Person.OfficeNumber);
        Apply(reopened, Batch("""{"email":"avery@example.com","officeNumber":null}""", 6, "2026-11-08T17:00:00Z"));
        var cleared = Open(incremental).GetProfileTimeline("avery@example.com")!;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, cleared.Changes.Last().NewValue.ValueKind);
        Assert.Equal(4, cleared.Changes.Count);
    }

    [Theory]
    [InlineData("observedAt", "null")]
    [InlineData("source", "42")]
    [InlineData("field", "\"notes\"")]
    [InlineData("value", "true")]
    public void Invalid_external_Profile_evidence_blocks_open_instead_of_fabricating_dates_or_provenance(string property, string jsonValue)
    {
        var app = Open();
        Apply(app, Batch("""{"email":"avery@example.com","displayName":"Avery","managerEmail":null}""",
            1, "2026-11-01T17:00:00Z"));
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "workspace.json")))!;
        var historyDirectory = Path.Combine(folder, "generations", metadata["generation"]!.GetValue<string>(), "profile-history");
        var file = Assert.Single(Directory.GetFiles(historyDirectory, "*.json"));
        var history = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        history["observations"]![0]![property] = System.Text.Json.Nodes.JsonNode.Parse(jsonValue);
        File.WriteAllText(file, history.ToJsonString());
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.False(reopened.OpenWorkspace(folder));
        Assert.Contains("Profile history", reopened.Status);
        Assert.Empty(reopened.People);
        Assert.False(reopened.EditPreferredName("avery@example.com", "Unsaved"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Person_notes_stay_separate_and_Profile_timeline_survives_note_edits_import_replay_and_reopen(bool incremental)
    {
        var app = Open(incremental);
        var initial = Batch("""{"email":"avery@example.com","displayName":"Avery","managerEmail":null}""",
            1, "2026-11-01T17:00:00Z");
        Apply(app, initial);
        var note = app.CreatePersonNote("avery@example.com", "Private context", "Before")!;
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"));
        var timeline = app.GetProfileTimeline("avery@example.com")!.DisplayChanges;
        Assert.True(app.EditPersonNote(note.Id, "Private context", "After"));
        Apply(app, initial);
        var reopened = Open(incremental);
        Assert.Equal(timeline, reopened.GetProfileTimeline("avery@example.com")!.DisplayChanges);
        Assert.Equal("After", Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Single(reopened.GetProfileTimeline("avery@example.com")!.Changes);
        Assert.DoesNotContain(reopened.GetProfileTimeline("avery@example.com")!.Observations, o => o.Field.Contains("note"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_generation_without_field_history_keeps_prior_evidence_unknown_instead_of_reconstructing_from_envelopes(bool incremental)
    {
        var app = Open(incremental);
        Apply(app, Batch("""
            {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null}
            """, 1, "2026-11-01T17:00:00Z"));
        if (incremental)
        {
            foreach (var file in OrgExplorer.TestFixtures.WorkspaceFixtures.IndexedDocuments(folder)
                .Where(e => e.Key.StartsWith("profileHistory:")).Select(e => e.Value))
            {
                var history = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
                history["observations"] = new System.Text.Json.Nodes.JsonArray();
                File.WriteAllText(file, history.ToJsonString());
            }
        }
        else
        {
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "workspace.json")))!;
            Directory.Delete(Path.Combine(folder, "generations", metadata["generation"]!.GetValue<string>(), "profile-history"), true);
            OrgExplorer.TestFixtures.WorkspaceFixtures.WriteLegacyPointer(folder, metadata);
        }
        var legacy = Open(incremental);
        Assert.Empty(legacy.GetProfileTimeline("avery@example.com")!.Confirmations);
        Apply(legacy, Batch("""{"email":"avery@example.com","managerEmail":null}""", 2, "2026-11-06T17:00:00Z"));
        var change = Assert.Single(Open(incremental).GetProfileTimeline("avery@example.com")!.Changes);
        Assert.Equal("morgan@example.com", change.OldValue.GetString());
        Assert.Null(change.PreviousEvidence);
        Assert.Null(change.ReportingInterval);
        Assert.Contains("effective date unknown", Open(incremental).GetProfileTimeline("avery@example.com")!.DisplayChanges);
    }

    public void Dispose()
    {
        foreach (var app in workflows) app.Dispose();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
