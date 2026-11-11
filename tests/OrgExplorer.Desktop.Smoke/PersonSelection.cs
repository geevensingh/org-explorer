namespace OrgExplorer.Desktop.Smoke;

internal static class PersonSelection
{
    internal static void AssertMatchesProfile(string email, bool showArchivedPeople,
        Func<string, IEnumerable<string>> readSelectedEmails)
    {
        var lists = showArchivedPeople ? new[] { "People", "ArchivedPeople" } : ["People"];
        foreach (var list in lists)
            foreach (var selectedEmail in readSelectedEmails(list))
                if (selectedEmail != email)
                    throw new InvalidOperationException("Navigation left a different Person selected while showing the retained profile.");
    }
}
