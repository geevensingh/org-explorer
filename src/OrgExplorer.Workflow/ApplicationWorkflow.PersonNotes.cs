using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed record PersonNote(Guid Id, string OwnerEmail, string Title, string Markdown,
    bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public partial interface IApplicationWorkflow
{
    IReadOnlyList<PersonNote> GetPersonNotes(string email, bool includeArchived = false);
    PersonNote? CreatePersonNote(string email, string title, string markdown);
    bool EditPersonNote(Guid noteId, string title, string markdown);
    bool ArchivePersonNote(Guid noteId, bool archived = true);
    bool RefreshPersonNotes();
}

public sealed partial class ApplicationWorkflow
{
    private Dictionary<Guid, PersonNote> personNotes = [];
    private readonly Dictionary<Guid, PersonNote> failedNoteBases = [];
    private sealed record PersonNoteMetadata(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string Kind,
        [property: JsonRequired] Guid Id,
        [property: JsonRequired] string OwnerEmail,
        [property: JsonRequired] string Title,
        [property: JsonRequired] bool Archived,
        [property: JsonRequired] DateTimeOffset CreatedAt,
        [property: JsonRequired] DateTimeOffset UpdatedAt);

    public IReadOnlyList<PersonNote> GetPersonNotes(string email, bool includeArchived = false)
    { if (queryView is { } view) return view.GetPersonNotes(email, includeArchived);
      lock (workspaceGate) return
        Array.AsReadOnly(PersonNotesOf(email).Where(n => includeArchived || !n.Archived).ToArray()); }

    public PersonNote? CreatePersonNote(string email, string title, string markdown)
    { lock (workspaceGate) return CreatePersonNoteCore(email, title, markdown); }

    private PersonNote? CreatePersonNoteCore(string email, string title, string markdown)
    {
        if (workspace is null || !people.TryGetValue(email, out var person))
        {
            Fail("Open a Person before creating a Person note.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            Fail("Person note title must be nonblank.");
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var note = new PersonNote(Guid.NewGuid(), person.Email, title, markdown, false, now, now);
        if (!SavePersonNote(note)) return null;
        personNotes.Add(note.Id, note);
        InvalidateProjections();
        pending = null;
        Status = "Person note saved locally.";
        return note;
    }

    public bool EditPersonNote(Guid noteId, string title, string markdown)
    { lock (workspaceGate) return EditPersonNoteCore(noteId, title, markdown); }

    private bool EditPersonNoteCore(Guid noteId, string title, string markdown)
    {
        if (workspace is null || !personNotes.TryGetValue(noteId, out var note))
            return Fail("Open an existing Person note before editing.");
        if (string.IsNullOrWhiteSpace(title))
        {
            failedNoteBases.TryAdd(note.Id, note);
            return SaveFailed($"note:{note.Id}", "Person note title must be nonblank; changes are NOT saved locally.");
        }
        var proposed = note with { Title = title, Markdown = markdown, UpdatedAt = DateTimeOffset.UtcNow };
        if (!SavePersonNote(proposed)) return false;
        personNotes[noteId] = proposed;
        InvalidateProjections();
        pending = null;
        Status = "Person note saved locally.";
        return true;
    }

    public bool ArchivePersonNote(Guid noteId, bool archived = true)
    { lock (workspaceGate) return ArchivePersonNoteCore(noteId, archived); }

    private bool ArchivePersonNoteCore(Guid noteId, bool archived)
    {
        if (workspace is null || !personNotes.TryGetValue(noteId, out var note))
            return Fail("Open an existing Person note before archival.");
        var proposed = note with { Archived = archived, UpdatedAt = DateTimeOffset.UtcNow };
        if (!SavePersonNote(proposed)) return false;
        personNotes[noteId] = proposed;
        InvalidateProjections();
        pending = null;
        Status = archived ? "Person note archived locally; writing retained." : "Person note restored locally.";
        return true;
    }

    public bool RefreshPersonNotes() => RefreshWorkspace();

    private static Dictionary<Guid, PersonNote> LoadPersonNotes(string path, Dictionary<string, PersonProfile> owners)
    {
        var result = new Dictionary<Guid, PersonNote>();
        var directory = Path.Combine(path, "notes");
        if (!Directory.Exists(directory))
        {
            if (File.Exists(directory)) throw new IOException("Person notes folder is not a directory.");
            return result;
        }
        RejectNoteLink(directory);
        foreach (var file in Directory.GetFiles(directory, "*.md"))
        {
            RejectNoteLink(file);
            var text = ReadWorkspaceText(file);
            var frontMatter = Regex.Match(text, @"\A---\r?\n(?<metadata>[\s\S]*?)\r?\n---\r?\n");
            if (!frontMatter.Success)
                throw new FormatException($"Person note {Path.GetFileName(file)}: expected JSON front matter.");
            using var document = JsonDocument.Parse(frontMatter.Groups["metadata"].Value);
            RequireVersion(document.RootElement, "Person note", [1]);
            CheckProperties(document.RootElement, ["schemaVersion", "kind", "id", "ownerEmail", "title",
                "archived", "createdAt", "updatedAt"], $"Person note {Path.GetFileName(file)}");
            var metadata = document.RootElement.Deserialize<PersonNoteMetadata>(JsonOptions)
                ?? throw new FormatException($"Person note {Path.GetFileName(file)}: invalid metadata.");
            if (metadata.SchemaVersion != 1)
                throw new FormatException("Unsupported Person note version. Upgrade the app; writes are blocked.");
            if (metadata.Kind != "person")
                throw new FormatException($"Person note {Path.GetFileName(file)}: expected kind 'person'.");
            Email(JsonSerializer.SerializeToElement(metadata.OwnerEmail), "Person note ownerEmail");
            if (!owners.ContainsKey(metadata.OwnerEmail))
                throw new FormatException($"Person note {Path.GetFileName(file)}: unknown owner {metadata.OwnerEmail}.");
            if (metadata.Id == Guid.Empty || string.IsNullOrWhiteSpace(metadata.Title) ||
                metadata.CreatedAt == default || metadata.UpdatedAt < metadata.CreatedAt ||
                metadata.CreatedAt.Offset != TimeSpan.Zero || metadata.UpdatedAt.Offset != TimeSpan.Zero)
                throw new FormatException($"Person note {Path.GetFileName(file)}: invalid identity, title, or UTC timestamps.");
            if (!Path.GetFileName(file).Equals(metadata.Id.ToString("D") + ".md", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Person note {Path.GetFileName(file)}: filename must match its stable note identity.");
            var note = new PersonNote(metadata.Id, metadata.OwnerEmail, metadata.Title, text[frontMatter.Length..],
                metadata.Archived, metadata.CreatedAt, metadata.UpdatedAt);
            if (!result.TryAdd(note.Id, note))
                throw new FormatException($"Person note {Path.GetFileName(file)}: duplicate note identity {note.Id}.");
        }
        return result;
    }

    private bool SavePersonNote(PersonNote note)
    {
        if (memory)
        {
            failedSaves.Remove($"note:{note.Id}");
            failedNoteBases.Remove(note.Id);
            return true;
        }
        try
        {
            var metadata = new PersonNoteMetadata(1, "person", note.Id, note.OwnerEmail, note.Title,
                note.Archived, note.CreatedAt, note.UpdatedAt);
            var content = "---\n" + JsonSerializer.Serialize(metadata, JsonOptions) + "\n---\n" + note.Markdown;
            WriteMarkdownAtomically("notes", note.Id, content, () =>
            {
                if (failedNoteBases.TryGetValue(note.Id, out var previous) && personNotes.GetValueOrDefault(note.Id) != previous)
                    throw new IOException("Person note changed externally while a draft was unsaved. Discard the draft before editing the delivered version.");
            });
            failedSaves.Remove($"note:{note.Id}");
            failedNoteBases.Remove(note.Id);
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            if (personNotes.TryGetValue(note.Id, out var previous)) failedNoteBases.TryAdd(note.Id, previous);
            return SaveFailed($"note:{note.Id}", "Person note save failed; changes are NOT saved locally: " + ex.Message);
        }
    }

    private static void RejectNoteLink(string path)
    {
        FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (entry.LinkTarget is not null)
            throw new IOException("Person note documents must be ordinary local files, not links: " + Path.GetFileName(path));
    }
}
