using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class LocalWorkObservationTests
{
    [Fact]
    public void Held_profile_navigation_keeps_slow_raw_time_diagnostic_without_inventing_a_per_case_budget()
    {
        var held = new LocalWorkObservation.ProfileSaveHold("avery@example.com", "Saving complete profile\u2026", true);
        var result = LocalWorkObservation.HeldProfileNavigationEvidence([154.1758], held, held, held.OwnerEmail, true, true, true);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result);
        Assert.Equal(154.1758, json.GetProperty("samplesMs")[0].GetDouble());
        Assert.Equal("diagnostic", json.GetProperty("timingUse").GetString());
        Assert.False(json.TryGetProperty("budgetMs", out _));
        Assert.False(json.TryGetProperty("p95Ms", out _));
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    public void Held_profile_navigation_rejects_released_writers_wrong_destinations_and_incomplete_storage_transitions(
        bool heldBefore, bool heldAfter, bool exactDestinationReady, bool originalStateDuringHold, bool committedStateAfterRelease)
    {
        var before = new LocalWorkObservation.ProfileSaveHold("avery@example.com", "Saving complete profile\u2026", heldBefore);
        var after = before with { WriterHeld = heldAfter };
        Assert.Throws<InvalidOperationException>(() =>
            LocalWorkObservation.HeldProfileNavigationEvidence([10], before, after, before.OwnerEmail,
                exactDestinationReady, originalStateDuringHold, committedStateAfterRelease));
    }

    [Fact]
    public void Held_profile_navigation_rejects_a_different_save_owner()
    {
        var held = new LocalWorkObservation.ProfileSaveHold("avery@example.com", "Saving complete profile\u2026", true);
        Assert.Throws<InvalidOperationException>(() => LocalWorkObservation.HeldProfileNavigationEvidence(
            [10], held, held with { OwnerEmail = "morgan@example.com" }, held.OwnerEmail, true, true, true));
    }

    [Fact]
    public void Held_profile_navigation_uses_real_storage_transition_not_mutable_UI_feedback_as_save_identity()
    {
        var before = new LocalWorkObservation.ProfileSaveHold("avery@example.com", "Ready", true);
        var after = before with { Activity = "Saving locally - changes are not yet confirmed saved." };
        var result = LocalWorkObservation.HeldProfileNavigationEvidence(
            [154.1758], before, after, before.OwnerEmail, true, true, true);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result);
        Assert.True(json.GetProperty("originalStateDuringHold").GetBoolean());
        Assert.True(json.GetProperty("committedStateAfterRelease").GetBoolean());
    }

    [Fact]
    public void One_fresh_snapshot_serves_each_poll_without_reusing_another_owners_feedback()
    {
        IReadOnlyList<LocalWorkObservation.Control> controls =
            [new("Email", "first@example.com"), new("PersonNotesSurface"), new("TeamNotesSurface")];
        var queries = 0;
        bool Poll() => LocalWorkObservation.Observe(() => "Ready", () => "", () => { queries++; return controls; });
        Assert.True(Poll());
        Assert.Equal(1, queries);
        controls = [new("Email", "second@example.com"),
            new("PersonNotesSurface", Feedback: "Pending private note edits"), new("TeamNotesSurface")];
        Assert.False(Poll());
        Assert.Equal(2, queries);
        controls = [new("Email", "first@example.com"), new("PersonNotesSurface"),
            new("TeamNotesSurface", Feedback: "Pending Team writing")];
        Assert.False(Poll());
        Assert.Equal(3, queries);
    }

    [Theory]
    [InlineData("Saving locally", "Pending private edits", "activity", false)]
    [InlineData("Local save stalled after 3 seconds", "NOT confirmed saved", "activity", false)]
    [InlineData("Failed", "NOT saved", "activity", false)]
    [InlineData("Indeterminate", "Unknown outcome", "activity", false)]
    [InlineData("Ready", "Pending private edits", "activity,status", false)]
    [InlineData("Ready", "Saved locally.", "activity,status,Email,PersonNotesSurface,TeamNotesSurface", true)]
    public void Original_root_guards_short_circuit_before_optional_identity_or_note_discovery(
        string activity, string status, string expectedReads, bool expectedIdle)
    {
        var reads = new List<string>();
        Assert.Equal(expectedIdle, LocalWorkObservation.IsIdle(
            () => { reads.Add("activity"); return activity; },
            () => { reads.Add("status"); return status; },
            id => { reads.Add(id); return new(id, "owner@example.com"); }));
        Assert.Equal(expectedReads, string.Join(",", reads));
    }

    [Fact]
    public void Absent_identity_requires_the_actual_visible_start_view_but_empty_identity_keeps_original_optional_notes()
    {
        Assert.True(LocalWorkObservation.IsIdle(() => "Ready", () => "", id =>
            id == "WorkspaceStart" ? new(id) : null));
        var hidden = Assert.Throws<InvalidOperationException>(() => LocalWorkObservation.IsIdle(
            () => "Ready", () => "", id => id == "WorkspaceStart" ? new(id, Offscreen: true) : null));
        Assert.Equal("Startup has neither a Person identity nor a purposeful starting view.", hidden.Message);
        var missing = Assert.Throws<InvalidOperationException>(() =>
            LocalWorkObservation.IsIdle(() => "Ready", () => "", _ => null));
        Assert.Equal("Missing actual UI control: WorkspaceStart", missing.Message);
        Assert.True(LocalWorkObservation.IsIdle(() => "Ready", () => "", id =>
            id == "Email" ? new(id) : throw new InvalidOperationException("Must not query notes or startup.")));
    }

    [Theory]
    [InlineData("Person", "PersonNotesSurface")]
    [InlineData("Team", "PersonNotesSurface,TeamNotesSurface")]
    public void Either_pending_note_refuses_idle_in_original_scope_order(string pendingScope, string expectedNotes)
    {
        var reads = new List<string>();
        Assert.False(LocalWorkObservation.IsIdle(() => "Ready", () => "Saved locally.", id =>
        {
            if (id == "Email") return new(id, "current@example.com");
            reads.Add(id);
            return new(id, Feedback: id == pendingScope + "NotesSurface" ? "Pending unrelated-owner writing" : "");
        }));
        Assert.Equal(expectedNotes, string.Join(",", reads));
    }

    [Theory]
    [InlineData("PersonNotesSurface")]
    [InlineData("TeamNotesSurface")]
    public void Selected_person_still_requires_both_note_surfaces_even_when_their_reading_is_offscreen(string absent)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LocalWorkObservation.IsIdle(
            () => "Ready", () => "", id => id == absent ? null : new(id, "owner@example.com", Offscreen: true)));
        Assert.Equal("Missing actual UI control: " + absent, error.Message);
    }

    [Fact]
    public void Provider_failures_are_not_reclassified_as_missing_identity_or_ready()
    {
        var unavailable = new IOException("Detached or unavailable provider.");
        Assert.Same(unavailable, Assert.Throws<IOException>(() =>
            LocalWorkObservation.IsIdle(() => "Ready", () => "", _ => throw unavailable)));
        Assert.Same(unavailable, Assert.Throws<IOException>(() =>
            LocalWorkObservation.IsIdle(() => throw unavailable, () => "", _ => null)));
    }

    [Fact]
    public void Paused_root_states_do_not_query_a_missing_disabled_or_detached_note_provider()
    {
        foreach (var activity in new[] { "Pending", "Stalled", "Failed", "Indeterminate" })
            Assert.False(LocalWorkObservation.Observe(() => activity, () => throw new Exception("Must short-circuit."),
                () => throw new IOException("Provider is unavailable.")));
        Assert.False(LocalWorkObservation.Observe(() => "Ready", () => "Pending local edits",
            () => throw new IOException("Provider is unavailable.")));
    }

    [Fact]
    public void An_unavailable_snapshot_propagates_and_the_next_poll_must_query_its_new_live_view()
    {
        var unavailable = new IOException("Detached provider.");
        var calls = 0;
        IReadOnlyList<LocalWorkObservation.Control> Snapshot() => ++calls switch
        {
            1 => throw unavailable,
            2 => [new("WorkspaceStart")],
            _ => [new("Email", "owner@example.com"), new("PersonNotesSurface")]
        };
        Assert.Same(unavailable, Assert.Throws<IOException>(() =>
            LocalWorkObservation.Observe(() => "Ready", () => "", Snapshot)));
        Assert.True(LocalWorkObservation.Observe(() => "Ready", () => "", Snapshot));
        var missing = Assert.Throws<InvalidOperationException>(() =>
            LocalWorkObservation.Observe(() => "Ready", () => "", Snapshot));
        Assert.Equal("Missing actual UI control: TeamNotesSurface", missing.Message);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Duplicate_person_labels_do_not_replace_the_identity_control_or_the_first_pending_surface()
    {
        Assert.False(LocalWorkObservation.Observe(() => "Ready", () => "", () =>
        [
            new("first@example.com", "Duplicate owner"),
            new("Email", "second@example.com"),
            new("second@example.com", "Duplicate owner"),
            new("PersonNotesSurface", Feedback: "Pending private writing"),
            new("PersonNotesSurface"),
            new("TeamNotesSurface")
        ]));
        Assert.True(LocalWorkObservation.Observe(() => "Ready", () => "pending is not the ordinal Pending prefix", () =>
        [
            new("Email", "owner@example.com"), new("PersonNotesSurface"),
            new("TeamNotesSurface", Feedback: "pending is not the ordinal Pending prefix")
        ]));
    }
}
