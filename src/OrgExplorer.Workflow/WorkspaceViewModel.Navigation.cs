namespace OrgExplorer.Workflow;

public sealed record PeopleNavigationView(
    string Query, bool OverlayOpen, string? SelectedEmail, SearchResult? SelectedResult,
    IReadOnlyList<string> ExpandedPeople, double BrowseOffset)
{
    public bool IsBrowsing => string.IsNullOrWhiteSpace(Query);
}

public sealed partial class WorkspaceViewModel
{
    private readonly Dictionary<string, PaneProportions> sessionProportions = new(StringComparer.OrdinalIgnoreCase);
    public PaneProportions PaneProportions =>
        Compatibility.WorkspacePath is { } path
            ? settings?.GetPaneProportions(path) ?? sessionProportions.GetValueOrDefault(path) ?? new(25, 35)
            : new(25, 35);
    public string? LayoutPreferenceFeedback { get; private set; }

    public bool SavePaneProportions(double peoplePercent, double profilePercent) => Observe(() =>
    {
        var proportions = new PaneProportions(peoplePercent, profilePercent);
        if (Compatibility.WorkspacePath is not { } path || !proportions.IsValid)
        {
            LayoutPreferenceFeedback = "Pane proportions were NOT saved. Open a Workspace and choose finite percentages between zero and 100.";
            return false;
        }
        if (settings is not null && !settings.SetPaneProportions(path, proportions))
        {
            LayoutPreferenceFeedback = settings.PreferenceError;
            return false;
        }
        if (settings is null) sessionProportions[path] = proportions;
        LayoutPreferenceFeedback = null;
        return true;
    });

    public PeopleNavigationView PeopleNavigation { get; private set; } = new("", false, null, null, [], 0);

    public void ChangePeopleQuery(string query)
    {
        PeopleNavigation = PeopleNavigation with { Query = query };
        Notify();
    }

    public void ShowPeopleOverlay(bool open)
    {
        PeopleNavigation = PeopleNavigation with { OverlayOpen = open };
        Notify();
    }

    public void CloseSearchContext()
    {
        PeopleNavigation = PeopleNavigation with { SelectedResult = null };
        Notify();
    }

    public void RememberPeopleBrowsing(IEnumerable<string> expandedPeople, double offset)
    {
        if (!PeopleNavigation.IsBrowsing) return;
        if (!double.IsFinite(offset) || offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        PeopleNavigation = PeopleNavigation with
        {
            ExpandedPeople = expandedPeople.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            BrowseOffset = offset
        };
        Notify();
    }

    public bool NavigatePerson(string email) => Observe(() => NavigatePeopleCore(email, null));

    public bool NavigateInheritedTeamNote(Guid noteId) => Observe(() =>
    {
        var context = PeopleNavigation.SelectedEmail is { } email
            ? workflow.GetInheritedTeamNotes(email).FirstOrDefault(n => n.Note.Id == noteId) : null;
        if (context is null)
        {
            navigationError = "That inherited Team note is unavailable in the current reporting chain. No replacement was opened.";
            return false;
        }
        return NavigatePeopleCore(context.Owner.Email, null) && OpenOwnedNote(NoteScope.Team, noteId);
    });

    public bool NavigateSearchResult(SearchResult result) => Observe(() =>
    {
        var current = workflow.Search(PeopleNavigation.Query).FirstOrDefault(r => SameSearchDestination(r, result));
        if (current is null)
        {
            navigationError = "That search match is no longer available in the validated Workspace. Search again; no replacement was opened.";
            return false;
        }
        return NavigatePeopleCore(current.Person.Email, current);
    });

    private bool NavigatePeopleCore(string email, SearchResult? result)
    {
        var person = workflow.GetRelationships(email)?.Person;
        if (person is null)
        {
            navigationError = "That Person is unavailable. No replacement was opened.";
            return false;
        }
        if ((result is not null || !email.Equals(PeopleNavigation.SelectedEmail, StringComparison.OrdinalIgnoreCase)) &&
            !workflow.CanSafelyClose)
        {
            navigationError = "Navigation blocked: local writing is pending or was NOT saved. Save, retry or discard the draft before leaving. " +
                (workflow.LocalSaveError ?? workflow.Status);
            return false;
        }
        if (!OpenOwnedNotes(NoteScope.Person, person.Email) || !OpenOwnedNotes(NoteScope.Team, person.Email)) return false;
        if (result?.Source == SearchSource.PersonNote && result.NoteId is { } personNote &&
            !OpenOwnedNote(NoteScope.Person, personNote)) return false;
        if (result?.Source == SearchSource.OwnedTeamNote && result.NoteId is { } teamNote &&
            !OpenOwnedNote(NoteScope.Team, teamNote)) return false;
        PeopleNavigation = PeopleNavigation with { SelectedEmail = person.Email, SelectedResult = result, OverlayOpen = false };
        return true;
    }

    public static bool SameSearchDestination(SearchResult left, SearchResult right) =>
        string.Equals(left.Person.Email, right.Person.Email, StringComparison.OrdinalIgnoreCase) &&
        left.Source == right.Source && left.Field == right.Field && left.NoteId == right.NoteId &&
        left.HistoricalContextId == right.HistoricalContextId && left.MatchText == right.MatchText &&
        (left.ProfileChange is null && right.ProfileChange is null ||
         left.ProfileChange is { } leftChange && right.ProfileChange is { } rightChange &&
         leftChange.Evidence.RecordedAt == rightChange.Evidence.RecordedAt &&
         leftChange.Evidence.BatchId == rightChange.Evidence.BatchId &&
         System.Text.Json.JsonElement.DeepEquals(leftChange.OldValue, rightChange.OldValue) &&
         System.Text.Json.JsonElement.DeepEquals(leftChange.NewValue, rightChange.NewValue));
}
