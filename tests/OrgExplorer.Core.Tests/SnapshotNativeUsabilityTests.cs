using OrgExplorer.Desktop.Smoke;
using System.Text.Json;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class SnapshotNativeUsabilityTests
{
    private static readonly SnapshotVisiblePerson Avery = new(
        new("avery@example.com", false, 180, 20), new("Avery reference", false, 220, 30));
    private static readonly SnapshotWritableEditor Editor = new(true, false, 260, 28, true, false, "Exact held writing");
    private static readonly SnapshotNativeBounds Window = new(0, 0, 1000, 800);
    private static readonly SnapshotNativeNote OwnerNote = new(
        new(Avery, new(20, 20, 180, 20), new(20, 50, 220, 30), Window),
        new("Exact note", false, 200, 30), "exact-note",
        Editor, new(20, 250, 200, 30), new(20, 300, 260, 28), new(10, 200, 900, 500));
    private static readonly SnapshotExportLifetime ActiveExport = new(true, false, false);

    [Fact]
    public void Recorded_post_input_owner_and_body_remain_usable_when_only_the_note_title_moves_above_the_viewport()
    {
        // Actual native sidecar: 43940045839/1, 7b3f574, host8412.
        const string id = "a6706184-db27-4551-a045-dce8f2e3fd90", title = "Automatic publication owner";
        var before = new SnapshotNativeNote(
            new(new(new("morgan@example.com", false, 363, 18), new("Morgan Example", false, 184, 32)),
                new(444, 156, 363, 18), new(444, 124, 184, 32), new(-8, -8, 1040, 736)),
            new(title, false, 691, 27), id, new(true, false, 691, 200, true, false, "Before automatic export"),
            new(284, 301, 691, 27), new(284, 483, 691, 200), new(284, 284, 720, 416));
        Assert.True(SnapshotNativeUsability.NoteReady(before, ActiveExport,
            "morgan@example.com", "Morgan Example", id, title, "Before automatic export"));
        var after = before with
        {
            Note = before.Note with { Height = 26 }, NoteBounds = new(284, 231, 691, 26),
            Editor = before.Editor with { Value = "Newer writing during automatic export" },
            EditorBounds = new(284, 412, 691, 200)
        };
        Assert.True(SnapshotNativeUsability.OwnerReady(after.Owner, "morgan@example.com", "Morgan Example"));
        Assert.True(SnapshotNativeUsability.DraftObserved(after.Owner.Person, after.Editor,
            "morgan@example.com", "Morgan Example", "Newer writing during automatic export"));
        Assert.True(SnapshotNativeUsability.Within(after.Viewport, after.EditorBounds));
        Assert.False(SnapshotNativeUsability.Within(after.Viewport, after.NoteBounds));
        Assert.False(SnapshotNativeUsability.NoteReady(after, ActiveExport,
            "morgan@example.com", "Morgan Example", id, title, "Newer writing during automatic export"));
        Assert.True(SnapshotNativeUsability.NoteWritingObserved(after, ActiveExport,
            "morgan@example.com", "Morgan Example", id, title, "Newer writing during automatic export"));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("hidden-body")]
    [InlineData("disabled")]
    [InlineData("clipped")]
    [InlineData("zero")]
    [InlineData("nonfinite")]
    [InlineData("nonfinite-note")]
    [InlineData("readonly")]
    [InlineData("unsupported")]
    [InlineData("wrong-owner")]
    [InlineData("clipped-owner")]
    [InlineData("wrong-note")]
    [InlineData("wrong-title")]
    [InlineData("wrong-value")]
    [InlineData("ended")]
    [InlineData("released")]
    public void Held_export_note_input_requires_the_same_exact_visible_owner_note_writable_value_and_live_invocation(string damage)
    {
        var note = damage switch
        {
            "hidden" => OwnerNote with { Note = OwnerNote.Note with { IsOffscreen = true },
                NoteBounds = OwnerNote.NoteBounds with { Y = 160 } },
            "hidden-body" => OwnerNote with { Editor = Editor with { IsOffscreen = true } },
            "disabled" => OwnerNote with { Editor = Editor with { IsEnabled = false } },
            "clipped" => OwnerNote with { EditorBounds = OwnerNote.EditorBounds with { Y = 690 } },
            "zero" => OwnerNote with { EditorBounds = OwnerNote.EditorBounds with { Width = 0 } },
            "nonfinite" => OwnerNote with { Viewport = OwnerNote.Viewport with { X = double.NaN } },
            "nonfinite-note" => OwnerNote with { NoteBounds = OwnerNote.NoteBounds with { X = double.NaN } },
            "readonly" => OwnerNote with { Editor = Editor with { IsReadOnly = true } },
            "unsupported" => OwnerNote with { Editor = Editor with { SupportsValuePattern = false } },
            "wrong-owner" => OwnerNote with { Owner = OwnerNote.Owner with
                { Person = Avery with { Identity = Avery.Identity with { Text = "morgan@example.com" } } } },
            "clipped-owner" => OwnerNote with { Owner = OwnerNote.Owner with
                { Identity = OwnerNote.Owner.Identity with { X = 990 } } },
            "wrong-note" => OwnerNote with { NoteId = "different-note" },
            "wrong-title" => OwnerNote with { Note = OwnerNote.Note with { Text = "Different note title" } },
            "wrong-value" => OwnerNote with { Editor = Editor with { Value = "Different writing" } },
            "ended" => OwnerNote,
            "released" => OwnerNote,
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        var lifetime = damage switch
        {
            "ended" => ActiveExport with { Ended = true },
            "released" => ActiveExport with { ReleaseRequested = true },
            _ => ActiveExport
        };
        Assert.True(SnapshotNativeUsability.NoteReady(OwnerNote, ActiveExport,
            "avery@example.com", "Avery reference", "exact-note", "Exact note", "Exact held writing"));
        Assert.False(SnapshotNativeUsability.NoteReady(note, lifetime,
            "avery@example.com", "Avery reference", "exact-note", "Exact note", "Exact held writing"));
        Assert.Equal(damage == "hidden", SnapshotNativeUsability.NoteWritingObserved(note, lifetime,
            "avery@example.com", "Avery reference", "exact-note", "Exact note", "Exact held writing"));
    }

    [Theory]
    [InlineData("offscreen")]
    [InlineData("clipped")]
    [InlineData("enabled")]
    [InlineData("ended")]
    public void Held_publication_feedback_must_be_readable_in_the_actual_viewport_while_that_invocation_is_active(string damage)
    {
        var original = new SnapshotNativePublication(new("Publishing a complete local version.", false, 400, 40),
            new(20, 250, 400, 40), false, false, new(20, 300, 100, 30),
            new(10, 200, 900, 500), Window);
        var frame = damage switch
        {
            "offscreen" => original with { Status = original.Status with { IsOffscreen = true } },
            "clipped" => original with { ButtonBounds = original.ButtonBounds with { Y = 690 } },
            "enabled" => original with { ButtonEnabled = true },
            "ended" => original,
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
        Assert.True(SnapshotNativeUsability.PublicationReady(original, ActiveExport));
        Assert.False(SnapshotNativeUsability.PublicationReady(frame,
            damage == "ended" ? ActiveExport with { Ended = true } : ActiveExport));
    }

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
            nativeUsability = new { lastEditor = editor, lastNote = OwnerNote with
                { Viewport = OwnerNote.Viewport with { X = dimension } } }
        });
        using var record = JsonDocument.Parse(json);
        Assert.Equal("Failed", record.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("InvalidOperationException", record.RootElement.GetProperty("errorType").GetString());
        Assert.Equal(original.Message, record.RootElement.GetProperty("error").GetString());
        var failed = record.RootElement.GetProperty("nativeUsability").GetProperty("lastEditor");
        Assert.Equal(encoded, failed.GetProperty("width").GetString());
        Assert.Equal(encoded, failed.GetProperty("height").GetString());
        Assert.Equal(encoded, record.RootElement.GetProperty("nativeUsability").GetProperty("lastNote")
            .GetProperty("viewport").GetProperty("x").GetString());
        Assert.False(SnapshotNativeUsability.EditorReady(Avery, editor, "avery@example.com", "Avery reference"));
    }
}
