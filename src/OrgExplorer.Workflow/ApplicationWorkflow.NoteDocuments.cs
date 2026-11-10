using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private record NoteMetadata(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string Kind,
        [property: JsonRequired] Guid Id,
        [property: JsonRequired] string OwnerEmail,
        [property: JsonRequired] string Title,
        [property: JsonRequired] bool Archived,
        [property: JsonRequired] DateTimeOffset CreatedAt,
        [property: JsonRequired] DateTimeOffset UpdatedAt);

    private static JsonDocument ReadNoteFrontMatter(string text, string family, out int bodyStart)
    {
        var front = Regex.Match(text, @"\A---\r?\n(?<metadata>[\s\S]*?)\r?\n---\r?\n");
        if (!front.Success) throw new FormatException($"{family}: expected JSON front matter.");
        bodyStart = front.Length;
        return JsonDocument.Parse(front.Groups["metadata"].Value);
    }

    private static (T Metadata, string Markdown) ReadNoteDocument<T>(
        string file, NoteScope scope, Dictionary<string, PersonProfile> owners) where T : NoteMetadata
    {
        var family = $"{scope} note";
        var context = $"{family} {Path.GetFileName(file)}";
        var text = ReadWorkspaceText(file);
        using var document = ReadNoteFrontMatter(text, context, out var bodyStart);
        RequireVersion(document.RootElement, family, [1]);
        string[] properties = ["schemaVersion", "kind", "id", "ownerEmail", "title", "archived", "createdAt", "updatedAt"];
        CheckProperties(document.RootElement,
            scope == NoteScope.Team ? [.. properties, "confirmedContextRevision"] : properties, context);
        var metadata = document.RootElement.Deserialize<T>(JsonOptions)
            ?? throw new FormatException($"{context}: invalid metadata.");
        var kind = scope == NoteScope.Person ? "person" : "team";
        if (metadata.Kind != kind)
            throw new FormatException($"{context}: expected kind '{kind}'.");
        Email(JsonSerializer.SerializeToElement(metadata.OwnerEmail), $"{family} ownerEmail");
        if (!owners.ContainsKey(metadata.OwnerEmail))
            throw new FormatException($"{context}: unknown owner {metadata.OwnerEmail}.");
        if (metadata.Id == Guid.Empty || string.IsNullOrWhiteSpace(metadata.Title) ||
            metadata.CreatedAt == default || metadata.UpdatedAt < metadata.CreatedAt ||
            metadata.CreatedAt.Offset != TimeSpan.Zero || metadata.UpdatedAt.Offset != TimeSpan.Zero)
            throw new FormatException($"{context}: invalid identity, title, or UTC timestamps.");
        if (!Path.GetFileName(file).Equals(metadata.Id.ToString("D") + ".md", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"{context}: filename must match its stable note identity.");
        return (metadata, text[bodyStart..]);
    }
}
