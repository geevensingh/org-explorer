using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private sealed record TeamNoteMetadata(
        int SchemaVersion, string Kind, Guid Id, string OwnerEmail, string Title,
        bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        [property: JsonRequired, JsonPropertyOrder(1)] Guid ConfirmedContextRevision)
        : NoteMetadata(SchemaVersion, Kind, Id, OwnerEmail, Title, Archived, CreatedAt, UpdatedAt);
    private sealed record TeamNoteDocuments(Dictionary<Guid, TeamNote> Notes, Dictionary<Guid, Guid> Confirmations);

    private static TeamNoteDocuments LoadTeamNotes(string path, Dictionary<string, PersonProfile> owners)
    {
        var result = new TeamNoteDocuments([], []);
        var directory = Path.Combine(path, "team-notes");
        if (workspaceReadSnapshot.Value is { } snapshot ? snapshot.ContainsKey(directory) : File.Exists(directory))
            throw new IOException("Team notes folder is not a directory.");
        if (workspaceReadSnapshot.Value is null && !Directory.Exists(directory)) return result;
        RejectNoteLink(directory);
        foreach (var file in NoteDocumentPaths(directory))
        {
            RejectNoteLink(file);
            var (metadata, markdown) = ReadNoteDocument<TeamNoteMetadata>(file, NoteScope.Team, owners);
            var note = new TeamNote(metadata.Id, metadata.OwnerEmail, metadata.Title, markdown,
                metadata.Archived, metadata.CreatedAt, metadata.UpdatedAt);
            if (!result.Notes.TryAdd(note.Id, note))
                throw new FormatException($"Team note {Path.GetFileName(file)}: duplicate note identity {note.Id}.");
            result.Confirmations.Add(note.Id, metadata.ConfirmedContextRevision);
        }
        return result;
    }

    private static Dictionary<string, Guid> LoadTeamContextRevisions(string generation, Dictionary<string, PersonProfile> owners)
    {
        var path = Path.Combine(generation, "team-context.json");
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new IOException("Team context revisions must be a file.");
            return result;
        }
        using var document = JsonDocument.Parse(ReadWorkspaceText(path));
        CheckProperties(document.RootElement, document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().Select(p => p.Name).ToArray() : [], "Team context revisions");
        var loaded = document.RootElement.Deserialize<Dictionary<string, Guid>>(JsonOptions)
            ?? throw new FormatException("Invalid Team context revisions.");
        foreach (var (email, value) in loaded)
            if (!owners.ContainsKey(email) || !result.TryAdd(email, value))
                throw new FormatException($"Team context revision: unknown or duplicate owner {email}.");
        return result;
    }

    private bool SaveTeamNote(TeamNote note, Guid confirmation)
    {
        try
        {
            if (memory)
            {
                ValidateNoteDraft(NoteScope.Team, note.Id);
                AcceptNoteSave(NoteScope.Team, note.Id);
                return true;
            }
            var metadata = new TeamNoteMetadata(1, "team", note.Id, note.OwnerEmail, note.Title,
                note.Archived, note.CreatedAt, note.UpdatedAt, confirmation);
            var content = "---\n" + JsonSerializer.Serialize(metadata, JsonOptions) + "\n---\n" + note.Markdown;
            WriteMarkdownAtomically("team-notes", note.Id, content, () => ValidateNoteDraft(NoteScope.Team, note.Id));
            AcceptNoteSave(NoteScope.Team, note.Id);
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            RetainNoteBase(NoteScope.Team, note.Id);
            return SaveFailed($"team-note:{note.Id}", "Team note save failed; changes are NOT saved locally: " + ex.Message);
        }
    }
}
