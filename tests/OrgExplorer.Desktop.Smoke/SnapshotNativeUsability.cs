using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Desktop.Smoke;

internal sealed record SnapshotVisibleText(string? Text, bool IsOffscreen, double Width, double Height);
internal sealed record SnapshotVisiblePerson(SnapshotVisibleText Identity, SnapshotVisibleText Heading);
internal sealed record SnapshotWritableEditor(bool IsEnabled, bool IsOffscreen, double Width, double Height,
    bool SupportsValuePattern, bool IsReadOnly, string? Value);
internal sealed record SnapshotNativeBounds(double X, double Y, double Width, double Height);
internal sealed record SnapshotNativeOwner(SnapshotVisiblePerson Person, SnapshotNativeBounds Identity,
    SnapshotNativeBounds Heading, SnapshotNativeBounds Window);
internal sealed record SnapshotNativeNote(SnapshotNativeOwner Owner, SnapshotVisibleText Note, string? NoteId,
    SnapshotWritableEditor Editor, SnapshotNativeBounds NoteBounds, SnapshotNativeBounds EditorBounds,
    SnapshotNativeBounds Viewport);
internal sealed record SnapshotExportLifetime(bool Entered, bool Ended, bool ReleaseRequested);
internal sealed record SnapshotNativePublication(SnapshotVisibleText Status, SnapshotNativeBounds StatusBounds,
    bool ButtonOffscreen, bool ButtonEnabled, SnapshotNativeBounds ButtonBounds,
    SnapshotNativeBounds Viewport, SnapshotNativeBounds Window);

internal static class SnapshotNativeUsability
{
    private static readonly JsonSerializerOptions DiagnosticJson = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    internal static string SerializeDiagnostic(object observation) => JsonSerializer.Serialize(observation, DiagnosticJson);

    internal static bool PersonReady(SnapshotVisiblePerson person, string email, string heading) =>
        person.Identity.Text == email && Visible(person.Identity.IsOffscreen, person.Identity.Width, person.Identity.Height) &&
        person.Heading.Text == heading && Visible(person.Heading.IsOffscreen, person.Heading.Width, person.Heading.Height);

    internal static bool EditorReady(SnapshotVisiblePerson person, SnapshotWritableEditor editor,
        string email, string heading) =>
        PersonReady(person, email, heading) && editor.IsEnabled &&
        Visible(editor.IsOffscreen, editor.Width, editor.Height) && editor.SupportsValuePattern && !editor.IsReadOnly;

    internal static bool DraftObserved(SnapshotVisiblePerson person, SnapshotWritableEditor editor,
        string email, string heading, string value) =>
        EditorReady(person, editor, email, heading) && editor.Value == value;

    internal static bool OwnerReady(SnapshotNativeOwner owner, string email, string heading) =>
        PersonReady(owner.Person, email, heading) && Within(owner.Window, owner.Identity) &&
        Within(owner.Window, owner.Heading);

    internal static bool NoteReady(SnapshotNativeNote note, SnapshotExportLifetime lifetime,
        string email, string heading, string noteId, string title, string value) =>
        NoteWritingObserved(note, lifetime, email, heading, noteId, title, value) &&
        Visible(note.Note.IsOffscreen, note.Note.Width, note.Note.Height) &&
        Within(note.Viewport, note.NoteBounds);

    internal static bool NoteWritingObserved(SnapshotNativeNote note, SnapshotExportLifetime lifetime,
        string email, string heading, string noteId, string title, string value) =>
        ExportHeld(lifetime) && OwnerReady(note.Owner, email, heading) &&
        DraftObserved(note.Owner.Person, note.Editor, email, heading, value) &&
        note.NoteId == noteId && note.Note.Text == title &&
        Visible(false, note.Note.Width, note.Note.Height) && BoundsValid(note.NoteBounds) &&
        Within(note.Owner.Window, note.NoteBounds) &&
        Within(note.Viewport, note.EditorBounds) && Within(note.Owner.Window, note.EditorBounds);

    internal static bool PublicationReady(SnapshotNativePublication publication, SnapshotExportLifetime lifetime) =>
        ExportHeld(lifetime) &&
        publication.Status.Text?.Contains("Publishing a complete local version.", StringComparison.Ordinal) == true &&
        Visible(publication.Status.IsOffscreen, publication.Status.Width, publication.Status.Height) &&
        !publication.ButtonOffscreen && !publication.ButtonEnabled &&
        Within(publication.Viewport, publication.StatusBounds) && Within(publication.Viewport, publication.ButtonBounds) &&
        Within(publication.Window, publication.StatusBounds) && Within(publication.Window, publication.ButtonBounds);

    internal static bool ExportHeld(SnapshotExportLifetime lifetime) =>
        lifetime.Entered && !lifetime.Ended && !lifetime.ReleaseRequested;

    internal static bool BoundsValid(SnapshotNativeBounds bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        Visible(false, bounds.Width, bounds.Height) &&
        double.IsFinite(bounds.X + bounds.Width) && double.IsFinite(bounds.Y + bounds.Height);

    internal static bool Within(SnapshotNativeBounds viewport, SnapshotNativeBounds bounds) =>
        BoundsValid(viewport) && BoundsValid(bounds) && bounds.X >= viewport.X && bounds.Y >= viewport.Y &&
        bounds.X + bounds.Width <= viewport.X + viewport.Width &&
        bounds.Y + bounds.Height <= viewport.Y + viewport.Height;

    private static bool Visible(bool offscreen, double width, double height) =>
        !offscreen && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
}
