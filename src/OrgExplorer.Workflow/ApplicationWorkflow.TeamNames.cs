namespace OrgExplorer.Workflow;

public sealed record TeamNameSegment(string Name, PersonProfile Owner);

public partial interface IApplicationWorkflow
{
    bool CanEditTeamName(string email);
    IReadOnlyList<TeamNameSegment> GetTeamNames(string email);
    bool StageProfileTeamName(string? value);
}

public sealed partial class ApplicationWorkflow
{
    public bool CanEditTeamName(string email)
    {
        if (queryView is { } view) return view.CanEditTeamName(email);
        lock (workspaceGate)
            return people.TryGetValue(email, out var person) && !person.Archived && ReportsOf(email).Count > 0;
    }

    public IReadOnlyList<TeamNameSegment> GetTeamNames(string email)
    {
        if (queryView is { } view) return view.GetTeamNames(email);
        lock (workspaceGate)
        {
            if (!people.TryGetValue(email, out var person) || person.Archived) return [];
            return Array.AsReadOnly(ReportingChain(person, people).Prepend(person.Email)
                .Where(CanEditTeamName)
                .Select(owner => people[owner])
                .Where(owner => owner.TeamName is not null)
                .Select(owner => new TeamNameSegment(owner.TeamName!, owner)).ToArray());
        }
    }

    public bool StageProfileTeamName(string? value)
    {
        lock (workspaceGate)
        {
            if (profileEdit is not { } edit) return Fail("Choose Edit profile first.");
            if (!CanEditTeamName(edit.Basis.Email))
                return Fail("Team name requires an active owner with a current direct report. The profile draft is retained.");
            profileEdit = edit with { Draft = edit.Draft with { TeamName = value } };
            Status = "Team name staged in the unsaved profile draft. Save or Cancel.";
            return true;
        }
    }
}

public sealed partial class WorkspaceViewModel
{
    public bool CanEditTeamName(string email) => workflow.CanEditTeamName(email);
    public IReadOnlyList<TeamNameSegment> GetTeamNames(string email) => workflow.GetTeamNames(email);
    public bool StageProfileTeamName(string? value) => Observe(() => workflow.StageProfileTeamName(value));
}
