using System.Text.Json;

namespace OrgExplorer.Workflow;

public partial interface IApplicationWorkflow
{
    IReadOnlyList<PersonProfile> ArchivedPeople { get; }
    bool EditPersonArchived(string email, bool archived);
    bool EditPersonManager(string email, string? managerEmail);
}

public sealed partial class ApplicationWorkflow
{
    public IReadOnlyList<PersonProfile> ArchivedPeople
    { get { lock (workspaceGate) return People.Where(p => p.Archived).ToArray(); } }

    public bool EditPersonArchived(string email, bool archived)
    {
        lock (workspaceGate)
        {
            if (workspace is null || !people.TryGetValue(email, out var person)) return Fail("Open a Person first.");
            return SavePersonRelationship(person, person with { Archived = archived }, "archived",
                archived ? "Person archived locally; profile, photos, notes and history retained." :
                    "Person reactivated in the validated current org.");
        }
    }

    public bool EditPersonManager(string email, string? managerEmail)
    {
        lock (workspaceGate)
        {
            if (workspace is null || !people.TryGetValue(email, out var person)) return Fail("Open a Person first.");
            try
            {
                if (managerEmail is not null) Email(JsonSerializer.SerializeToElement(managerEmail), $"{person.Email}.managerEmail");
                return SavePersonRelationship(person, person with { ManagerEmail = managerEmail }, "managerEmail",
                    "Manager change saved locally; no other reporting relationships were inferred.");
            }
            catch (FormatException ex) { return Fail("Manager change rejected: " + ex.Message); }
        }
    }

    private bool SavePersonRelationship(PersonProfile before, PersonProfile after, string field, string savedStatus)
    {
        if (!workspaceCurrent) return Fail("Workspace unavailable; retained information is STALE. No changes saved.");
        var proposed = new Dictionary<string, PersonProfile>(people, StringComparer.OrdinalIgnoreCase) { [before.Email] = after };
        try { ValidateGraph(proposed); }
        catch (FormatException ex) { return Fail("Change rejected; no people changed: " + ex.Message); }
        var history = CaptureLocalHistory(before, after, field);
        if (!Save(proposed, batches, history: history, operation: $"profile:{before.Email.ToLowerInvariant()}:{field}")) return false;
        people = proposed;
        profileHistory = history;
        pending = null;
        Status = savedStatus;
        return true;
    }
}
