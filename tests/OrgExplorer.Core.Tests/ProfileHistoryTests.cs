using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ProfileHistoryTests
{
    private const string Avery = "avery@example.com";
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Batch(string records, int id, string observed = "2026-11-01T17:00:00Z") => $$"""
        {"schemaVersion":1,"batchId":"10000000-0010-4000-8000-{{id:000000000000}}",
         "observedAt":"{{observed}}","producer":"Fictitious directory","people":[{{records}}]}
        """;

    private static IApplicationWorkflow Open(Clock clock)
    {
        var app = ApplicationWorkflow.CreateInMemory(timeProvider: clock);
        Assert.True(app.OpenWorkspace("fictitious-history"));
        Apply(app, Batch("""
            {"email":"avery@example.com","displayName":"Avery","officeNumber":"007B","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
            """, 1));
        return app;
    }

    private static void Apply(IApplicationWorkflow app, string batch)
    {
        var preview = app.PreviewImport(batch);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    [Fact]
    public void Imported_Profile_change_retains_structured_values_source_and_application_time_without_pre_import_history()
    {
        var clock = new Clock();
        var app = Open(clock);
        Assert.Empty(app.GetProfileTimeline(Avery)!.Changes);
        clock.Now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        Apply(app, Batch("""{"email":"AVERY@example.com","officeNumber":"0009C"}""", 2, "2026-11-03T17:00:00.1234567Z"));
        var change = Assert.Single(app.GetProfileTimeline(Avery)!.Changes);
        Assert.Equal("officeNumber", change.Field);
        Assert.Equal("007B", change.OldValue.GetString());
        Assert.Equal("0009C", change.NewValue.GetString());
        Assert.Equal(ProfileChangeSource.Import, change.Evidence.Source);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T17:00:00.1234567Z"), change.Evidence.ObservedAt);
        Assert.Equal(clock.Now, change.Evidence.RecordedAt);
        Assert.Equal("Fictitious directory", change.Evidence.Producer);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), change.PreviousEvidence!.ObservedAt);
    }

    [Fact]
    public void Local_edit_and_clear_have_local_provenance_without_a_fabricated_source_Observation()
    {
        var clock = new Clock();
        var app = Open(clock);
        Assert.True(app.EditPersonText(Avery, PersonTextField.OfficeNumber, "0002B"));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(app.EditPersonText(Avery, PersonTextField.OfficeNumber, null));
        Assert.True(app.EditPersonText(Avery, PersonTextField.OfficeNumber, null));
        var changes = app.GetProfileTimeline(Avery)!.Changes;
        Assert.Equal(2, changes.Count);
        Assert.Equal("0002B", changes[1].OldValue.GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, changes[1].NewValue.ValueKind);
        Assert.Equal(ProfileChangeSource.LocalEdit, changes[1].Evidence.Source);
        Assert.Null(changes[1].Evidence.ObservedAt);
        Assert.Null(changes[1].Evidence.BatchId);
        Assert.Equal(clock.Now, changes[1].Evidence.RecordedAt);
        Assert.Equal(ProfileChangeSource.LocalEdit, changes[1].PreviousEvidence!.Source);
    }

    [Fact]
    public void Office_November_3_does_not_reconfirm_Manager_November_1_and_transfer_November_6_uses_prior_Manager_evidence()
    {
        var app = Open(new Clock());
        Apply(app, Batch("""{"email":"avery@example.com","officeNumber":"0009C"}""", 2, "2026-11-03T17:00:00Z"));
        var timeline = app.GetProfileTimeline(Avery)!;
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"),
            timeline.Confirmations.Single(o => o.Field == "managerEmail").ObservedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T17:00:00Z"),
            timeline.Confirmations.Single(o => o.Field == "officeNumber").ObservedAt);
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 3, "2026-11-06T17:00:00Z"));
        var move = app.GetProfileTimeline(Avery)!.Changes.Single(c => c.Field == "managerEmail");
        Assert.Equal("morgan@example.com", move.OldValue.GetString());
        Assert.Equal("alex@example.com", move.NewValue.GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"), move.ReportingInterval!.After);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"), move.ReportingInterval.By);
        Assert.Equal(move.Evidence.RecordedAt, move.DetectedAt);
    }

    [Fact]
    public void Explicit_identical_Observation_reconfirms_only_that_field_and_older_evidence_is_reviewed_without_backwards_intervals()
    {
        var app = Open(new Clock());
        Apply(app, Batch("""{"email":"avery@example.com","managerEmail":"MORGAN@example.com"}""", 2, "2026-11-06T17:00:00Z"));
        Assert.Empty(app.GetProfileTimeline(Avery)!.Changes);
        Apply(app, Batch("""{"email":"avery@example.com"}""", 3, "2026-11-07T17:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"),
            app.GetProfileTimeline(Avery)!.Confirmations.Single(o => o.Field == "managerEmail").ObservedAt);
        var staleSame = app.PreviewImport(Batch(
            """{"email":"avery@example.com","managerEmail":"morgan@example.com"}""", 4, "2026-11-02T17:00:00Z"));
        Assert.True(staleSame.CanApply);
        Assert.Contains(staleSame.Warnings, w => w.Contains("managerEmail") && w.Contains("older evidence"));
        Assert.True(app.ApplyImport(staleSame.Id));
        Assert.Empty(app.GetProfileTimeline(Avery)!.Changes);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"),
            app.GetProfileTimeline(Avery)!.Confirmations.Single(o => o.Field == "managerEmail").ObservedAt);
        var staleMove = app.PreviewImport(Batch(
            """{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 5, "2026-11-03T17:00:00Z"));
        Assert.Contains(staleMove.Warnings, w => w.Contains("managerEmail"));
        Assert.True(app.ApplyImport(staleMove.Id));
        var move = Assert.Single(app.GetProfileTimeline(Avery)!.Changes);
        Assert.True(move.IsStaleEvidence);
        Assert.Null(move.ReportingInterval);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T17:00:00Z"), move.PreviousEvidence!.ObservedAt);
        var replay = app.PreviewImport(Batch(
            """{"email":"avery@example.com","managerEmail":"alex@example.com"}""", 5, "2026-11-03T17:00:00Z"));
        Assert.Empty(replay.Warnings);
        Assert.True(app.ApplyImport(replay.Id));
        Assert.Single(app.GetProfileTimeline(Avery)!.Changes);
    }

    [Fact]
    public void Preview_distinguishes_same_value_evidence_from_an_email_only_no_op()
    {
        var app = Open(new Clock());
        var same = app.PreviewImport(Batch(
            """{"email":"avery@example.com","managerEmail":"morgan@example.com"}""", 2, "2026-11-03T17:00:00Z"));
        Assert.Contains(same.Changes, c => c.Contains("Record explicit evidence") && c.Contains("Manager"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T17:00:00Z"),
            app.GetProfileTimeline(Avery)!.Confirmations.Single(o => o.Field == "managerEmail").ObservedAt);
        var omission = app.PreviewImport(Batch("""{"email":"avery@example.com"}""", 3));
        Assert.DoesNotContain(omission.Changes, c => c.Contains("Record explicit evidence"));
    }
}
