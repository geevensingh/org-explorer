using System.Text.Json.Serialization;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private sealed record TeamNoteMetadata(
        int SchemaVersion, string Kind, Guid Id, string OwnerEmail, string Title,
        bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        [property: JsonRequired, JsonPropertyOrder(1)] Guid ConfirmedContextRevision)
        : NoteMetadata(SchemaVersion, Kind, Id, OwnerEmail, Title, Archived, CreatedAt, UpdatedAt);
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
            SaveLocalSqliteNote(NoteScope.Team, metadata, note.Markdown);
            AcceptNoteSave(NoteScope.Team, note.Id);
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            RetainNoteBase(NoteScope.Team, note.Id);
            var outcome = localSaveFault?.Outcome == LocalSaveOutcome.Unknown ? "NOT confirmed saved locally" : "NOT saved locally";
            return SaveFailed($"team-note:{note.Id}", $"Team note save failed; changes are {outcome}: " + ex.Message);
        }
    }
}
