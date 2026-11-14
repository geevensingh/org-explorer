namespace OrgExplorer.Workflow;

public enum SearchContext { Current, Archived, Historical }
public enum SearchSource { Profile, PersonNote, OwnedTeamNote, InheritedTeamNote, ProfileHistory, FormerReportingChain, HistoricalTeamNote }

public sealed record SearchResult(PersonProfile Person, SearchContext Context, SearchSource Source,
    string? Field, string MatchText)
{
    public Guid? HistoricalContextId { get; init; }
    public HistoricalTeamContext? HistoricalContext { get; init; }
    public Guid? NoteId { get; init; }
    public PersonProfile? NoteOwner { get; init; }
    public string? NoteTitle { get; init; }
    public bool NoteArchived { get; init; }
    public bool NoteNeedsReview { get; init; }
    public ProfileChange? ProfileChange { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public bool IsWorkspaceCurrent { get; init; } = true;
    public string Excerpt { get; init; } = MatchText;
    public string Label => (IsWorkspaceCurrent ? "" : "STALE retained — ") + $"{Person.Heading} ({Person.Email}) — " + DestinationLabel;
    public string SourceLabel => (IsWorkspaceCurrent ? "" : "STALE retained — ") + DestinationLabel;
    private string DestinationLabel =>
        (Context == SearchContext.Historical ? "Historical" : Person.Archived ? "Archived person" :
            NoteArchived ? "Archived note — not current writing" : "Current") +
        (Context == SearchContext.Historical && Person.Archived ? " / Archived person" : "") +
        (Person.Archived && NoteArchived ? " / Archived note — not current writing" : "") + " — " +
        (Source switch
        {
            SearchSource.Profile => "Profile: " + ApplicationWorkflow.ProfileFieldLabel(Field!),
            SearchSource.PersonNote => "Person note",
            SearchSource.OwnedTeamNote => "Owned Team note",
            SearchSource.InheritedTeamNote => "Inherited Team note",
            SearchSource.ProfileHistory => "Profile history: " + ApplicationWorkflow.ProfileFieldLabel(Field!),
            SearchSource.FormerReportingChain => "Former Reporting chain: " + ApplicationWorkflow.ProfileFieldLabel(Field!),
            _ => "Frozen Team note"
        }) +
        (NoteTitle is null ? "" : $" '{NoteTitle}'") +
        (NoteOwner is null ? "" : $" — {(Source == SearchSource.FormerReportingChain ? "former Manager" : "owned by")} {NoteOwner.Heading} ({NoteOwner.Email})") +
        (CapturedAt is null ? "" : $" — captured locally {CapturedAt.Value:O}") +
        (ProfileChange is null ? "" : $" — recorded locally {ProfileChange.DetectedAt:O}") +
        (NoteNeedsReview ? (Context == SearchContext.Historical ? " — Needed review when captured" : " — Needs review; writing retained") : "");
}

public partial interface IApplicationWorkflow
{
    IReadOnlyList<SearchResult> Search(string query);
}

public sealed partial class ApplicationWorkflow
{
    public IReadOnlyList<SearchResult> Search(string query)
    {
        if (queryView is { } view)
            return view.Search(query).Select(r => r with { IsWorkspaceCurrent = Volatile.Read(ref workspaceCurrent) }).ToArray();
        lock (workspaceGate)
        {
            var text = query.Trim();
            if (text.Length == 0) return [];
            searchIndex ??= BuildSearchIndex();
            var excerpts = new Dictionary<string, string>(ReferenceEqualityComparer.Instance);
            string CachedExcerpt(string value)
            {
                if (!excerpts.TryGetValue(value, out var excerpt))
                {
                    excerpt = Excerpt(value, text);
                    excerpts.Add(value, excerpt);
                }
                return excerpt;
            }
            return searchIndex.Where(r => r.MatchText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    r.NoteTitle?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                .Select(r => r with { IsWorkspaceCurrent = workspaceCurrent, Excerpt = CachedExcerpt(r.MatchText) }).ToArray();
        }
    }

    private static string Excerpt(string value, string query)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
        if (text.Length == 0) return "Empty note";
        var match = text.IndexOf(System.Text.RegularExpressions.Regex.Replace(query, @"\s+", " "), StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, match - 48);
        var length = Math.Min(160, text.Length - start);
        return (start > 0 ? "…" : "") + text.Substring(start, length) + (start + length < text.Length ? "…" : "");
    }

    private IReadOnlyList<SearchResult> BuildSearchIndex()
    {
            var results = new List<SearchResult>();
            foreach (var person in People)
            {
                foreach (var (field, value) in SearchableProfile(person))
                    if (value is not null)
                        results.Add(new SearchResult(person, person.Archived ? SearchContext.Archived : SearchContext.Current,
                            SearchSource.Profile, field, value));
                foreach (var note in GetPersonNotes(person.Email, true))
                    AddNote(person, SearchSource.PersonNote, person, note.Id, note.Title, note.Markdown, note.Archived);
                foreach (var note in GetTeamNotes(person.Email, true))
                    AddNote(person, SearchSource.OwnedTeamNote, person, note.Id, note.Title, note.Markdown, note.Archived, note.NeedsReview);
                foreach (var inherited in GetInheritedTeamNotes(person.Email))
                    AddNote(person, SearchSource.InheritedTeamNote, inherited.Owner, inherited.Note.Id,
                        inherited.Note.Title, inherited.Note.Markdown, false, inherited.Note.NeedsReview);
                foreach (var change in profileHistory.GetValueOrDefault(person.Email)?.Changes ?? [])
                {
                    var oldValue = change.OldValue.ValueKind == System.Text.Json.JsonValueKind.String
                        ? change.OldValue.GetString() : change.OldValue.GetRawText();
                    if (oldValue is not null)
                        results.Add(new SearchResult(person, SearchContext.Historical, SearchSource.ProfileHistory,
                            change.Field, oldValue) { ProfileChange = change });
                }
                foreach (var capture in GetHistoricalTeamContexts(person.Email))
                {
                    foreach (var owner in capture.ReportingChain)
                        foreach (var (field, value) in SearchableProfile(owner))
                            if (value is not null)
                                results.Add(new SearchResult(person, SearchContext.Historical, SearchSource.FormerReportingChain,
                                    field, value)
                                { HistoricalContextId = capture.Id, HistoricalContext = capture, NoteOwner = owner, CapturedAt = capture.CapturedAt });
                    foreach (var note in capture.TeamNotes)
                            results.Add(new SearchResult(person, SearchContext.Historical, SearchSource.HistoricalTeamNote, null,
                                note.Markdown)
                            {
                                HistoricalContextId = capture.Id, HistoricalContext = capture, NoteId = note.Id, NoteTitle = note.Title,
                                NoteOwner = capture.ReportingChain.Single(p =>
                                    string.Equals(p.Email, note.OwnerEmail, StringComparison.OrdinalIgnoreCase)),
                                CapturedAt = capture.CapturedAt, NoteNeedsReview = note.NeedsReview
                            });
                }
            }
            return results;

            void AddNote(PersonProfile person, SearchSource source, PersonProfile owner, Guid id,
                string title, string markdown, bool archived, bool needsReview = false)
            {
                results.Add(new SearchResult(person, person.Archived || archived ? SearchContext.Archived : SearchContext.Current,
                    source, null, markdown)
                { NoteOwner = owner, NoteId = id, NoteTitle = title, NoteArchived = archived, NoteNeedsReview = needsReview });
        }
    }

    private static IEnumerable<(string Field, string? Value)> SearchableProfile(PersonProfile person)
    {
        yield return ("email", person.Email);
        foreach (var (field, property, _) in TextFields) yield return (property, ReadText(person, field));
        yield return ("managerEmail", person.ManagerEmail);
        yield return ("photoPath", person.PhotoPath);
        yield return ("archived", person.Archived ? "true" : "false");
        yield return ("isFavorite", person.IsFavorite ? "true" : "false");
    }
}
