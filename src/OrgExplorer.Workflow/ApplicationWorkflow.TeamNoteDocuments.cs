using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private sealed record TeamNoteMetadata(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string Kind,
        [property: JsonRequired] Guid Id,
        [property: JsonRequired] string OwnerEmail,
        [property: JsonRequired] string Title,
        [property: JsonRequired] bool Archived,
        [property: JsonRequired] DateTimeOffset CreatedAt,
        [property: JsonRequired] DateTimeOffset UpdatedAt,
        [property: JsonRequired] Guid ConfirmedContextRevision);
    private sealed record TeamNoteDocuments(Dictionary<Guid, TeamNote> Notes, Dictionary<Guid, Guid> Confirmations);

    private static TeamNoteDocuments LoadTeamNotes(string path, Dictionary<string, PersonProfile> owners)
    {
        var result = new TeamNoteDocuments([], []);
        var directory = Path.Combine(path, "team-notes");
        if (!Directory.Exists(directory))
        {
            if (File.Exists(directory)) throw new IOException("Team notes folder is not a directory.");
            return result;
        }
        RejectNoteLink(directory);
        foreach (var file in Directory.GetFiles(directory, "*.md"))
        {
            RejectNoteLink(file);
            var text = ReadWorkspaceText(file);
            var frontMatter = Regex.Match(text, @"\A---\r?\n(?<metadata>[\s\S]*?)\r?\n---\r?\n");
            if (!frontMatter.Success)
                throw new FormatException($"Team note {Path.GetFileName(file)}: expected JSON front matter.");
            using var document = JsonDocument.Parse(frontMatter.Groups["metadata"].Value);
            RequireVersion(document.RootElement, "Team note", [1]);
            CheckProperties(document.RootElement, ["schemaVersion", "kind", "id", "ownerEmail", "title",
                "archived", "createdAt", "updatedAt", "confirmedContextRevision"], $"Team note {Path.GetFileName(file)}");
            var metadata = document.RootElement.Deserialize<TeamNoteMetadata>(JsonOptions)
                ?? throw new FormatException($"Team note {Path.GetFileName(file)}: invalid metadata.");
            if (metadata.SchemaVersion != 1)
                throw new FormatException("Unsupported Team note version. Upgrade the app; writes are blocked.");
            if (metadata.Kind != "team")
                throw new FormatException($"Team note {Path.GetFileName(file)}: expected kind 'team'.");
            Email(JsonSerializer.SerializeToElement(metadata.OwnerEmail), "Team note ownerEmail");
            if (!owners.ContainsKey(metadata.OwnerEmail))
                throw new FormatException($"Team note {Path.GetFileName(file)}: unknown owner {metadata.OwnerEmail}.");
            if (metadata.Id == Guid.Empty || string.IsNullOrWhiteSpace(metadata.Title) ||
                metadata.CreatedAt == default || metadata.UpdatedAt < metadata.CreatedAt ||
                metadata.CreatedAt.Offset != TimeSpan.Zero || metadata.UpdatedAt.Offset != TimeSpan.Zero)
                throw new FormatException($"Team note {Path.GetFileName(file)}: invalid identity, title, or UTC timestamps.");
            if (!Path.GetFileName(file).Equals(metadata.Id.ToString("D") + ".md", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Team note {Path.GetFileName(file)}: filename must match its stable note identity.");
            var note = new TeamNote(metadata.Id, metadata.OwnerEmail, metadata.Title, text[frontMatter.Length..],
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
        if (memory)
        {
            failedSaves.Remove($"team-note:{note.Id}");
            failedTeamNoteBases.Remove(note.Id);
            return true;
        }
        string? staged = null;
        try
        {
            using var access = CoordinateWorkspaceIo(workspace!);
            EnsureWorkspaceWritable();
            using var lease = new FileStream(Path.Combine(workspace!, ".workspace.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (failedTeamNoteBases.TryGetValue(note.Id, out var previous) &&
                (teamNotes.GetValueOrDefault(note.Id) != previous.Note ||
                 teamNoteConfirmations.GetValueOrDefault(note.Id) != previous.Confirmation))
                throw new IOException("Team note changed externally while a draft was unsaved. Discard the draft before editing the delivered version.");
            if (WorkspaceFingerprint() != revision)
                throw new IOException("Workspace changed. Reopen before editing; no note changes saved.");
            var directory = Path.Combine(workspace!, "team-notes");
            Directory.CreateDirectory(directory);
            RejectNoteLink(directory);
            var destination = Path.Combine(directory, note.Id.ToString("D") + ".md");
            if (File.Exists(destination)) RejectNoteLink(destination);
            staged = Path.Combine(directory, note.Id.ToString("D") + ".next");
            var metadata = new TeamNoteMetadata(1, "team", note.Id, note.OwnerEmail, note.Title,
                note.Archived, note.CreatedAt, note.UpdatedAt, confirmation);
            var content = "---\n" + JsonSerializer.Serialize(metadata, JsonOptions) + "\n---\n" + note.Markdown;
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(new UTF8Encoding(false, true).GetBytes(content));
                file.Flush(true);
            }
            if (File.Exists(destination)) File.Replace(staged, destination, null);
            else File.Move(staged, destination);
            staged = null;
            revision = WorkspaceFingerprint();
            CapturePersonNoteRevision();
            failedSaves.Remove($"team-note:{note.Id}");
            failedTeamNoteBases.Remove(note.Id);
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            if (teamNotes.TryGetValue(note.Id, out var previous))
                failedTeamNoteBases.TryAdd(note.Id, (previous, teamNoteConfirmations[note.Id]));
            return SaveFailed($"team-note:{note.Id}", "Team note save failed; changes are NOT saved locally: " + ex.Message);
        }
        finally
        {
            if (staged is not null)
            {
                try { File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
