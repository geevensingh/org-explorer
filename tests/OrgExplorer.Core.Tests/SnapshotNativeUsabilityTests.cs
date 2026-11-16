using OrgExplorer.Desktop.Smoke;
using System.Text.Json;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class SnapshotNativeUsabilityTests
{
    private static readonly SnapshotVisiblePerson Avery = new(
        new("avery@example.com", false, 180, 20), new("Avery reference", false, 220, 30));
    private static readonly SnapshotWritableEditor Editor = new(true, false, 260, 28, true, false, "Exact held writing");

    [Theory]
    [InlineData("offscreen")]
    [InlineData("zero-width")]
    [InlineData("zero-height")]
    [InlineData("nonfinite-width")]
    [InlineData("nonfinite-height")]
    [InlineData("read-only")]
    [InlineData("missing-value-pattern")]
    [InlineData("disabled")]
    public void Correct_text_and_enabled_state_cannot_substitute_for_an_actual_usable_native_editor(string damage)
    {
        var editor = damage switch
        {
            "offscreen" => Editor with { IsOffscreen = true },
            "zero-width" => Editor with { Width = 0 },
            "zero-height" => Editor with { Height = 0 },
            "nonfinite-width" => Editor with { Width = double.NaN },
            "nonfinite-height" => Editor with { Height = double.PositiveInfinity },
            "read-only" => Editor with { IsReadOnly = true },
            "missing-value-pattern" => Editor with { SupportsValuePattern = false },
            "disabled" => Editor with { IsEnabled = false },
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        Assert.False(SnapshotNativeUsability.EditorReady(Avery, editor, "avery@example.com", "Avery reference"));
        Assert.False(SnapshotNativeUsability.DraftObserved(Avery, editor, "avery@example.com", "Avery reference", "Exact held writing"));
    }

    [Theory]
    [InlineData("identity-offscreen")]
    [InlineData("identity-zero-width")]
    [InlineData("identity-zero-height")]
    [InlineData("wrong-owner")]
    [InlineData("heading-offscreen")]
    [InlineData("heading-zero-width")]
    [InlineData("heading-zero-height")]
    [InlineData("wrong-heading")]
    public void Current_identity_and_heading_must_both_be_visible_positive_and_exact(string damage)
    {
        var person = damage switch
        {
            "identity-offscreen" => Avery with { Identity = Avery.Identity with { IsOffscreen = true } },
            "identity-zero-width" => Avery with { Identity = Avery.Identity with { Width = 0 } },
            "identity-zero-height" => Avery with { Identity = Avery.Identity with { Height = 0 } },
            "wrong-owner" => Avery with { Identity = Avery.Identity with { Text = "morgan@example.com" } },
            "heading-offscreen" => Avery with { Heading = Avery.Heading with { IsOffscreen = true } },
            "heading-zero-width" => Avery with { Heading = Avery.Heading with { Width = 0 } },
            "heading-zero-height" => Avery with { Heading = Avery.Heading with { Height = 0 } },
            "wrong-heading" => Avery with { Heading = Avery.Heading with { Text = "Morgan Example" } },
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        Assert.False(SnapshotNativeUsability.PersonReady(person, "avery@example.com", "Avery reference"));
        Assert.False(SnapshotNativeUsability.EditorReady(person, Editor, "avery@example.com", "Avery reference"));
        Assert.False(SnapshotNativeUsability.DraftObserved(person, Editor, "avery@example.com", "Avery reference", "Exact held writing"));
    }

    [Fact]
    public void Fresh_valid_native_readback_admits_one_input_and_exact_post_input_owner_value()
    {
        Assert.True(SnapshotNativeUsability.EditorReady(Avery, Editor, "avery@example.com", "Avery reference"));
        Assert.True(SnapshotNativeUsability.DraftObserved(Avery, Editor, "avery@example.com", "Avery reference", "Exact held writing"));
        Assert.False(SnapshotNativeUsability.DraftObserved(Avery, Editor, "avery@example.com", "Avery reference", "Different writing"));
        var morgan = new SnapshotVisiblePerson(new("morgan@example.com", false, 160, 20), new("Morgan Example", false, 210, 30));
        Assert.True(SnapshotNativeUsability.PersonReady(morgan, "morgan@example.com", "Morgan Example"));
    }

    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void Actual_failed_frame_serializer_retains_nonfinite_bounds_and_the_original_failure(double dimension, string encoded)
    {
        var editor = Editor with { Width = dimension, Height = dimension };
        var original = new InvalidOperationException("Native browsing/draft admission outlasted the actual held adoption transaction.");
        var json = SnapshotNativeUsability.SerializeDiagnostic(new
        {
            outcome = "Failed", errorType = original.GetType().Name, error = original.Message,
            nativeUsability = new { lastEditor = editor }
        });
        using var record = JsonDocument.Parse(json);
        Assert.Equal("Failed", record.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("InvalidOperationException", record.RootElement.GetProperty("errorType").GetString());
        Assert.Equal(original.Message, record.RootElement.GetProperty("error").GetString());
        var failed = record.RootElement.GetProperty("nativeUsability").GetProperty("lastEditor");
        Assert.Equal(encoded, failed.GetProperty("width").GetString());
        Assert.Equal(encoded, failed.GetProperty("height").GetString());
        Assert.False(SnapshotNativeUsability.EditorReady(Avery, editor, "avery@example.com", "Avery reference"));
    }
}
