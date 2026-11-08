namespace OrgExplorer.Workflow;

public partial interface IApplicationWorkflow
{
    IReadOnlyList<PersonProfile> FavoritePeople { get; }
    bool EditFavorite(string email, bool isFavorite);
}

public sealed partial class ApplicationWorkflow
{
    public IReadOnlyList<PersonProfile> FavoritePeople
    { get { lock (workspaceGate) return People.Where(p => p.IsFavorite).ToArray(); } }

    public bool EditFavorite(string email, bool isFavorite)
    {
        lock (workspaceGate)
        {
            if (workspace is null || !people.TryGetValue(email, out var person)) return Fail("Open a Person first.");
            return SavePersonRelationship(person, person with { IsFavorite = isFavorite }, "isFavorite",
                isFavorite ? "Favorite marked and saved locally." : "Favorite unmarked and saved locally.");
        }
    }
}
