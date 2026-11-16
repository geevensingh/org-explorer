using System.Text.Json;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class NoteObservationEvidenceTests
{
    private static readonly NoteObservationEvidence Expected = new(
        "owner@example.com", "Private profile", "note-identity", "Private title", "Private writing",
        true, true, true, true, false, true, true);

    [Fact]
    public void Freshness_requires_current_owner_profile_note_identity_and_both_complete_values()
    {
        Assert.True(Expected.Check(Expected).Correct);
        foreach (var stale in new[] { Expected with { Owner = "previous@example.com" },
            Expected with { Profile = "Previous profile" }, Expected with { NoteId = "old-identity" },
            Expected with { Title = "Old title" }, Expected with { Markdown = "Old writing" } })
            Assert.False(stale.Check(Expected).Correct);
        var evidence = JsonSerializer.Serialize(Expected.Check(Expected));
        foreach (var value in new[] { Expected.Owner, Expected.Profile, Expected.NoteId, Expected.Title, Expected.Markdown })
            Assert.DoesNotContain(value, evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Bound_manual_probe_can_proceed_but_invalid_revision_cannot()
    {
        NoteObservationEvidence.RequireHostedProbe("true", "Windows", "uia-observation-probe", new string('a', 40), "123", "1");
        Assert.Throws<InvalidOperationException>(() => NoteObservationEvidence.RequireHostedProbe(
            "true", "Windows", "uia-observation-probe", new string('z', 40), "123", "1"));
    }

    [Fact]
    public void Correct_values_cannot_replace_enabled_visible_writable_focused_editing_patterns()
    {
        foreach (var unusable in new[] { Expected with { Enabled = false }, Expected with { Visible = false },
            Expected with { Writable = false }, Expected with { TitleFocused = false }, Expected with { BodyFocused = true },
            Expected with { EditSelected = false }, Expected with { PatternsAvailable = false } })
            Assert.False(unusable.Check(Expected).Correct);
    }

    [Theory]
    [InlineData(null, "Windows", "uia-observation-probe", "123", "1")]
    [InlineData("true", "Linux", "uia-observation-probe", "123", "1")]
    [InlineData("true", "Windows", "desktop", "123", "1")]
    [InlineData("true", "Windows", "uia-observation-probe", null, "1")]
    [InlineData("true", "Windows", "uia-observation-probe", "123", "0")]
    public void Shared_desktop_and_unbound_invocations_are_rejected_before_native_execution(
        string? actions, string? runner, string? job, string? run, string? attempt)
    {
        Assert.Throws<InvalidOperationException>(() => NoteObservationEvidence.RequireHostedProbe(
            actions, runner, job, new string('a', 40), run, attempt));
    }
}
