namespace OrgExplorer.Workflow;

public sealed record TeamNote(Guid Id, string OwnerEmail, string Title, string Markdown,
    bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public bool NeedsReview { get; init; }
    public string ReviewStatus => NeedsReview ? "Needs review — role or reporting context changed; writing retained." : "Confirmed current context";
}
public sealed record InheritedTeamNote(TeamNote Note, PersonProfile Owner);

public partial interface IApplicationWorkflow
{
    IReadOnlyList<TeamNote> GetTeamNotes(string email, bool includeArchived = false);
    IReadOnlyList<InheritedTeamNote> GetInheritedTeamNotes(string email);
    TeamNote? CreateTeamNote(string email, string title, string markdown);
    bool EditTeamNote(Guid noteId, string title, string markdown);
    bool ConfirmTeamNote(Guid noteId);
    bool ArchiveTeamNote(Guid noteId, bool archived = true);
    bool RefreshTeamNotes();
}

public sealed partial class ApplicationWorkflow
{
    private Dictionary<Guid, TeamNote> teamNotes = [];
    private Dictionary<Guid, Guid> teamNoteConfirmations = [];
    private Dictionary<string, Guid> teamContextRevisions = new(StringComparer.OrdinalIgnoreCase);
    public bool RefreshTeamNotes() => RefreshPersonNotes();

    public IReadOnlyList<TeamNote> GetTeamNotes(string email, bool includeArchived = false) =>
        teamNotes.Values.Where(n => string.Equals(n.OwnerEmail, email, StringComparison.OrdinalIgnoreCase)
            && (includeArchived || !n.Archived)).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .Select(n => n with { NeedsReview = teamNoteConfirmations[n.Id] != teamContextRevisions.GetValueOrDefault(n.OwnerEmail) }).ToArray();

    public IReadOnlyList<InheritedTeamNote> GetInheritedTeamNotes(string email)
    {
        var result = new List<InheritedTeamNote>();
        if (!people.TryGetValue(email, out var person) || person.Archived) return result;
        foreach (var managerEmail in ReportingChain(person, people))
        {
            var manager = people[managerEmail];
            result.AddRange(GetTeamNotes(managerEmail).Select(n => new InheritedTeamNote(n, manager)));
        }
        return result;
    }

    public TeamNote? CreateTeamNote(string email, string title, string markdown)
    {
        if (workspace is null || !people.TryGetValue(email, out var person))
        {
            Fail("Open a Person before creating a Team note.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            Fail("Team note title must be nonblank.");
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var note = new TeamNote(Guid.NewGuid(), person.Email, title, markdown, false, now, now);
        var confirmation = teamContextRevisions.GetValueOrDefault(person.Email);
        if (!SaveTeamNote(note, confirmation)) return null;
        teamNotes.Add(note.Id, note);
        teamNoteConfirmations.Add(note.Id, confirmation);
        pending = null;
        Status = "Team note saved locally.";
        return note;
    }

    public bool EditTeamNote(Guid noteId, string title, string markdown)
    {
        if (workspace is null || !teamNotes.TryGetValue(noteId, out var note))
            return Fail("Open an existing Team note before editing.");
        if (string.IsNullOrWhiteSpace(title)) return Fail("Team note title must be nonblank.");
        var proposed = note with { Title = title, Markdown = markdown, UpdatedAt = DateTimeOffset.UtcNow };
        var confirmation = teamContextRevisions.GetValueOrDefault(note.OwnerEmail);
        if (!SaveTeamNote(proposed, confirmation)) return false;
        teamNotes[noteId] = proposed;
        teamNoteConfirmations[noteId] = confirmation;
        pending = null;
        Status = "Team note saved locally and confirmed for current context.";
        return true;
    }

    public bool ConfirmTeamNote(Guid noteId)
    {
        if (workspace is null || !teamNotes.TryGetValue(noteId, out var note))
            return Fail("Open an existing Team note before confirmation.");
        return EditTeamNote(noteId, note.Title, note.Markdown);
    }

    public bool ArchiveTeamNote(Guid noteId, bool archived = true)
    {
        if (workspace is null || !teamNotes.TryGetValue(noteId, out var note))
            return Fail("Open an existing Team note before archival.");
        var proposed = note with { Archived = archived, UpdatedAt = DateTimeOffset.UtcNow };
        if (!SaveTeamNote(proposed, teamNoteConfirmations[noteId])) return false;
        teamNotes[noteId] = proposed;
        pending = null;
        Status = archived ? "Team note archived locally; writing retained." : "Team note restored locally.";
        return true;
    }

    private IEnumerable<string> ChangedTeamContexts(Dictionary<string, PersonProfile> proposed)
    {
        if (!people.Values.Any(before => proposed.TryGetValue(before.Email, out var after) &&
            (before.JobTitle != after.JobTitle || before.Department != after.Department || before.Archived != after.Archived ||
             !string.Equals(before.ManagerEmail, after.ManagerEmail, StringComparison.OrdinalIgnoreCase))))
            yield break;
        foreach (var before in people.Values)
            if (proposed.TryGetValue(before.Email, out var after) &&
                (before.JobTitle != after.JobTitle || before.Department != after.Department || before.Archived != after.Archived ||
                 !ReportingChain(before, people).SequenceEqual(ReportingChain(after, proposed), StringComparer.OrdinalIgnoreCase)))
                yield return before.Email;
    }

    private Dictionary<string, Guid> NextTeamContextRevisions(Dictionary<string, PersonProfile> proposed)
    {
        var next = new Dictionary<string, Guid>(teamContextRevisions, StringComparer.OrdinalIgnoreCase);
        foreach (var email in ChangedTeamContexts(proposed)) next[email] = Guid.NewGuid();
        return next;
    }

    private void PreviewTeamNoteReview(Dictionary<string, PersonProfile> proposed, List<string> changes)
    {
        var affected = ChangedTeamContexts(proposed).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var note in teamNotes.Values.Where(n => !n.Archived))
            if (affected.Contains(note.OwnerEmail) || teamNoteConfirmations[note.Id] != teamContextRevisions.GetValueOrDefault(note.OwnerEmail))
                changes.Add($"Team note '{note.Title}' owned by {note.OwnerEmail}: needs review after role or reporting-context changes; writing stays visible. Confirm, edit, or archive on its owner's profile.");
    }

    private static IReadOnlyList<string> ReportingChain(PersonProfile person, Dictionary<string, PersonProfile> population)
    {
        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { person.Email };
        while (person.ManagerEmail is { } email && visited.Add(email) && population.TryGetValue(email, out person!))
            chain.Add(person.Email);
        return chain;
    }
}
