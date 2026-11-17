using System.Text.Json;
using System.Text.Json.Serialization;

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

    private static void ValidateNoteMetadata(NoteMetadata metadata, NoteScope scope,
        Dictionary<string, PersonProfile> owners, string context)
    {
        var kind = scope == NoteScope.Person ? "person" : "team";
        if (metadata.SchemaVersion != 1 || metadata.Kind != kind)
            throw new FormatException($"{context}: expected kind '{kind}'.");
        Email(JsonSerializer.SerializeToElement(metadata.OwnerEmail), $"{scope} note ownerEmail");
        if (!owners.ContainsKey(metadata.OwnerEmail))
            throw new FormatException($"{context}: unknown owner {metadata.OwnerEmail}.");
        if (metadata.Id == Guid.Empty || string.IsNullOrWhiteSpace(metadata.Title) ||
            metadata.CreatedAt == default || metadata.UpdatedAt < metadata.CreatedAt ||
            metadata.CreatedAt.Offset != TimeSpan.Zero || metadata.UpdatedAt.Offset != TimeSpan.Zero)
            throw new FormatException($"{context}: invalid identity, title, or UTC timestamps.");
    }
}
