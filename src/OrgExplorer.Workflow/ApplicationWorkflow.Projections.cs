namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private volatile ApplicationWorkflow? queryView;
    private bool queryOnly;
    private object? populationSource;
    private IReadOnlyList<PersonProfile>? orderedPeople;
    private IReadOnlyList<ReportingNode>? reportingRoots;
    private Dictionary<string, IReadOnlyList<PersonProfile>>? reportsByManager;
    private Dictionary<string, PersonNote[]>? personNotesByOwner;
    private Dictionary<string, TeamNote[]>? teamNotesByOwner;
    private IReadOnlyList<SearchResult>? searchIndex;

    private void InvalidateProjections()
    {
        orderedPeople = null;
        reportingRoots = null;
        reportsByManager = null;
        personNotesByOwner = null;
        teamNotesByOwner = null;
        searchIndex = null;
        if (!queryOnly)
        {
            var previous = queryView;
            var samePopulation = previous is not null && previous.people.Count == people.Count &&
                (ReferenceEquals(previous.populationSource, people) || people.Values.All(person =>
                    previous.people.TryGetValue(person.Email, out var prior) && prior == person));
            queryView = new ApplicationWorkflow(true, null, timeProvider)
            {
                queryOnly = true, workspaceCurrent = workspaceCurrent,
                populationSource = people,
                orderedPeople = samePopulation ? previous!.orderedPeople : null,
                reportingRoots = samePopulation ? previous!.reportingRoots : null,
                reportsByManager = samePopulation ? previous!.reportsByManager : null,
                people = new(people, StringComparer.OrdinalIgnoreCase),
                personNotes = new(personNotes), teamNotes = new(teamNotes),
                teamNoteConfirmations = new(teamNoteConfirmations),
                teamContextRevisions = new(teamContextRevisions, StringComparer.OrdinalIgnoreCase),
                profileHistory = new(profileHistory, StringComparer.OrdinalIgnoreCase),
                historicalTeamContexts = new(historicalTeamContexts, StringComparer.OrdinalIgnoreCase),
                photos = new(photos)
            };
        }
    }

    private IReadOnlyList<PersonProfile> OrderedPeople() =>
        orderedPeople ??= Array.AsReadOnly(people.Values.OrderBy(p => p.Heading).ToArray());

    private IReadOnlyList<PersonProfile> ReportsOf(string email)
    {
        reportsByManager ??= OrderedPeople().Where(p => !p.Archived && p.ManagerEmail is not null)
            .GroupBy(p => p.ManagerEmail!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PersonProfile>)Array.AsReadOnly(g.ToArray()), StringComparer.OrdinalIgnoreCase);
        return reportsByManager.GetValueOrDefault(email) ?? [];
    }

    private PersonNote[] PersonNotesOf(string email)
    {
        personNotesByOwner ??= personNotes.Values.GroupBy(n => n.OwnerEmail, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        return personNotesByOwner.GetValueOrDefault(email) ?? [];
    }

    private TeamNote[] TeamNotesOf(string email)
    {
        teamNotesByOwner ??= teamNotes.Values.GroupBy(n => n.OwnerEmail, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
                .Select(n => n with { NeedsReview = teamNoteConfirmations[n.Id] != teamContextRevisions.GetValueOrDefault(n.OwnerEmail) }).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        return teamNotesByOwner.GetValueOrDefault(email) ?? [];
    }
}
