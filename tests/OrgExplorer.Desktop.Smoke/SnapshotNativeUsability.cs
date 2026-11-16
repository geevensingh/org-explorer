using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Desktop.Smoke;

internal sealed record SnapshotVisibleText(string? Text, bool IsOffscreen, double Width, double Height);
internal sealed record SnapshotVisiblePerson(SnapshotVisibleText Identity, SnapshotVisibleText Heading);
internal sealed record SnapshotWritableEditor(bool IsEnabled, bool IsOffscreen, double Width, double Height,
    bool SupportsValuePattern, bool IsReadOnly, string? Value);

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

    private static bool Visible(bool offscreen, double width, double height) =>
        !offscreen && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
}
