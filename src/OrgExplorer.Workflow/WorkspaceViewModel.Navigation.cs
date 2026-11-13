namespace OrgExplorer.Workflow;

public sealed record PeopleNavigationView(
    string Query, bool OverlayOpen, string? SelectedEmail, SearchResult? SelectedResult,
    IReadOnlyList<string> ExpandedPeople, double BrowseOffset, RememberedView? LastValidView = null)
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
    public string? ViewPreferenceFeedback { get; private set; }
    public string? ViewRecoveryFeedback { get; private set; }
    private bool changingDestination;

    private void RememberCurrentView(NoteScope? scope = null, SearchResult? result = null)
    {
        if (changingDestination || !IsWorkspaceCurrent ||
            Compatibility.WorkspacePath is not { } path || PeopleNavigation.SelectedEmail is not { } email ||
            workflow.GetRelationships(email)?.Person is not { Archived: false }) return;
        if (result?.NoteArchived == true) return;
        if (scope is null && result?.NoteId is null &&
            PeopleNavigation.LastValidView is { Source: SearchSource.OwnedTeamNote } previous &&
            previous.PersonEmail.Equals(email, StringComparison.OrdinalIgnoreCase) &&
            TeamNotesView.SelectedNote is { Archived: false } team && team.Id == previous.NoteId)
            scope = NoteScope.Team;
        var note = GetOwnedNotesView(scope ?? NoteScope.Person).SelectedNote;
        if (result?.NoteId is null && note is not null &&
            !note.OwnerEmail.Equals(email, StringComparison.OrdinalIgnoreCase)) return;
        var view = result?.NoteId is { } resultId
            ? new RememberedView(email, result.Source, resultId, result.NoteOwner?.Email, result.HistoricalContextId)
            : note is { Archived: false }
                ? new RememberedView(email, scope == NoteScope.Team ? SearchSource.OwnedTeamNote : SearchSource.PersonNote,
                    note.Id, note.OwnerEmail)
                : new RememberedView(email);
        PeopleNavigation = PeopleNavigation with { LastValidView = view };
        ViewPreferenceFeedback = settings is null || settings.SetLastView(path, view) ? null : settings.PreferenceError;
        ViewRecoveryFeedback = null;
    }

    private void RestoreRememberedView()
    {
        ViewPreferenceFeedback = null;
        ViewRecoveryFeedback = null;
        if (settings?.GetLastView(Compatibility.WorkspacePath!) is not { } view) return;
        var person = workflow.GetRelationships(view.PersonEmail)?.Person;
        var destination = person is { Archived: false } && view.Source is not null ? ResolveRememberedNote(person, view) : null;
        if (person is not { Archived: false } || view.Source is not null && destination is null)
        {
            ViewRecoveryFeedback = "The remembered Person or exact note is unavailable or archived. " +
                "Your preference and writing are retained; no replacement was opened. Find a Person using search, Favorites or People to deliberately select another view.";
            return;
        }
        changingDestination = true;
        try
        {
            if (!NavigatePeopleCore(person.Email, destination))
            {
                ViewRecoveryFeedback = "The remembered view could not be restored. " +
                    (navigationError ?? workflow.Status) + " Your preference and writing are retained; no replacement was opened.";
                return;
            }
            PeopleNavigation = PeopleNavigation with
            {
                SelectedResult = destination?.Source is SearchSource.InheritedTeamNote or SearchSource.HistoricalTeamNote
                    ? destination : null,
                Query = destination?.Source is SearchSource.InheritedTeamNote or SearchSource.HistoricalTeamNote
                    ? destination.NoteTitle! : "",
                LastValidView = view
            };
        }
        finally { changingDestination = false; }
    }

    private SearchResult? ResolveRememberedNote(PersonProfile person, RememberedView view)
    {
        var owner = view.NoteOwnerEmail is { } email ? workflow.GetRelationships(email)?.Person : null;
        if (view.Source == SearchSource.HistoricalTeamNote)
        {
            var capture = workflow.GetHistoricalTeamContexts(person.Email).FirstOrDefault(c => c.Id == view.HistoricalContextId);
            var frozen = capture?.TeamNotes.FirstOrDefault(n => n.Id == view.NoteId &&
                n.OwnerEmail.Equals(view.NoteOwnerEmail, StringComparison.OrdinalIgnoreCase));
            if (frozen is null || capture is null) return null;
            return new(person, SearchContext.Historical, SearchSource.HistoricalTeamNote, null, frozen.Markdown)
            {
                NoteId = frozen.Id, NoteTitle = frozen.Title,
                NoteOwner = capture.ReportingChain.Single(p => p.Email.Equals(frozen.OwnerEmail, StringComparison.OrdinalIgnoreCase)),
                HistoricalContextId = capture.Id, HistoricalContext = capture,
                CapturedAt = capture.CapturedAt, NoteNeedsReview = frozen.NeedsReview
            };
        }
        if (owner is not { Archived: false }) return null;
        if (view.Source == SearchSource.PersonNote)
        {
            var note = workflow.GetPersonNotes(person.Email).FirstOrDefault(n => n.Id == view.NoteId);
            return note is null ? null : new(person, SearchContext.Current, SearchSource.PersonNote, null, note.Markdown)
                { NoteId = note.Id, NoteTitle = note.Title, NoteOwner = owner };
        }
        var team = view.Source == SearchSource.InheritedTeamNote
            ? workflow.GetInheritedTeamNotes(person.Email).FirstOrDefault(n => n.Note.Id == view.NoteId &&
                n.Owner.Email.Equals(view.NoteOwnerEmail, StringComparison.OrdinalIgnoreCase))?.Note
            : workflow.GetTeamNotes(person.Email).FirstOrDefault(n => n.Id == view.NoteId);
        return team is null ? null : new(person, SearchContext.Current, view.Source!.Value, null, team.Markdown)
            { NoteId = team.Id, NoteTitle = team.Title, NoteOwner = owner, NoteNeedsReview = team.NeedsReview };
    }

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

    public bool NavigatePerson(string email) => Observe(() =>
    {
        var accepted = NavigatePeopleCore(email, null);
        if (accepted) RememberCurrentView();
        return accepted;
    });

    public bool NavigateInheritedTeamNote(Guid noteId) => Observe(() =>
    {
        var context = PeopleNavigation.SelectedEmail is { } email
            ? workflow.GetInheritedTeamNotes(email).FirstOrDefault(n => n.Note.Id == noteId) : null;
        if (context is null)
        {
            navigationError = "That inherited Team note is unavailable in the current reporting chain. No replacement was opened.";
            return false;
        }
        changingDestination = true;
        bool accepted;
        try { accepted = NavigatePeopleCore(context.Owner.Email, null) && OpenOwnedNote(NoteScope.Team, noteId); }
        finally { changingDestination = false; }
        if (accepted) RememberCurrentView(NoteScope.Team);
        return accepted;
    });

    public bool NavigateSearchResult(SearchResult result) => Observe(() =>
    {
        var current = workflow.Search(PeopleNavigation.Query).FirstOrDefault(r => SameSearchDestination(r, result));
        if (current is null)
        {
            navigationError = "That search match is no longer available in the validated Workspace. Search again; no replacement was opened.";
            return false;
        }
        changingDestination = true;
        bool accepted;
        try { accepted = NavigatePeopleCore(current.Person.Email, current); }
        finally { changingDestination = false; }
        if (accepted) RememberCurrentView(result: current);
        return accepted;
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
            !workflow.CanNavigatePeople)
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
