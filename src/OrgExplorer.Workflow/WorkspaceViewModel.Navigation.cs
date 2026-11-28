using System.Collections.Immutable;

namespace OrgExplorer.Workflow;

public enum PeopleTreeAction { CollapseSubtree, ExpandSubtree, CollapseOtherBranches }
public enum PeopleTreeExpansionStatus { Running, Completed, Cancelled, Interrupted }

public sealed record PeopleTreeExpansionView(string TargetEmail, int VisitedPeople, int ExpandedBranches,
    PeopleTreeExpansionStatus Status)
{
    public bool IsRunning => Status == PeopleTreeExpansionStatus.Running;
}

public sealed record PeopleNavigationView(
    string Query, bool OverlayOpen, string? SelectedEmail, SearchResult? SelectedResult,
    IReadOnlyList<string> ExpandedPeople, double BrowseOffset, RememberedView? LastValidView = null,
    double BrowseHorizontalOffset = 0, double BrowseExtentWidth = 0, PersonSearchResult? HighlightedResult = null,
    bool ArchivedPeopleVisible = false)
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
    private readonly object destinationGate = new();
    private long destinationGeneration;
    private bool changingDestination;

    private bool HasValidPeopleSelection => PeopleNavigation.SelectedEmail is { } selected &&
        workflow.GetRelationships(selected) is not null;

    private void EnsurePeopleSelection()
    {
        long generation;
        Guid? revision;
        lock (destinationGate)
        {
            if (!IsWorkspaceCurrent || HasValidPeopleSelection) return;
            generation = ++destinationGeneration;
            revision = workflow.Synchronization.LocalRevision;
        }
        if (Compatibility.WorkspacePath is { } path && settings?.GetLastView(path) is { } remembered &&
            workflow.GetRelationships(remembered.PersonEmail)?.Person is { Archived: false })
        {
            lock (destinationGate)
                if (CanCommitAutomaticSelection(generation, revision)) RestoreRememberedPerson(remembered);
            return;
        }
        var level = workflow.ReportingRoots;
        var fallback = level.OrderBy(node => node.Person.Heading)
            .ThenBy(node => node.Person.Email, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Person;
        PersonProfile? candidate = null;
        while (level.Count > 0)
        {
            candidate = level.Where(node => node.Reports.Count >= 2).OrderBy(node => node.Person.Heading)
                .ThenBy(node => node.Person.Email, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Person;
            if (candidate is not null) break;
            level = level.SelectMany(node => node.Reports).ToArray();
        }
        candidate ??= fallback;
        if (candidate is null) return;
        lock (destinationGate)
            if (CanCommitAutomaticSelection(generation, revision)) NavigateAutomaticPerson(candidate.Email);
    }

    private bool CanCommitAutomaticSelection(long generation, Guid? revision) =>
        generation == destinationGeneration && revision == workflow.Synchronization.LocalRevision &&
        IsWorkspaceCurrent && !HasValidPeopleSelection;

    private bool NavigateAutomaticPerson(string email, SearchResult? destination = null, bool recoverRememberedNote = false)
    {
        lock (destinationGate) return NavigateAutomaticPersonCore(email, destination, recoverRememberedNote);
    }

    private bool NavigateAutomaticPersonCore(string email, SearchResult? destination, bool recoverRememberedNote)
    {
        var overlay = PeopleNavigation.OverlayOpen;
        changingDestination = true;
        try
        {
            if (!NavigatePeopleCore(email, destination, recoverRememberedNote: recoverRememberedNote)) return false;
            ExpandPeoplePath(email);
            PeopleNavigation = PeopleNavigation with { OverlayOpen = overlay };
            return true;
        }
        finally { changingDestination = false; }
    }

    private void ExpandPeoplePath(string email)
    {
        var expanded = PeopleNavigation.ExpandedPeople.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var context = workflow.GetRelationships(email);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (context is not null && visited.Add(context.Person.Email))
        {
            if (context.ImportedReports.Count > 0) expanded.Add(context.Person.Email);
            context = context.Manager is { } manager ? workflow.GetRelationships(manager.Email) : null;
        }
        PeopleNavigation = PeopleNavigation with { ExpandedPeople = expanded.ToArray() };
    }

    private void RememberCurrentView(NoteScope? scope = null, SearchResult? result = null)
    {
        lock (destinationGate) RememberCurrentViewCore(scope, result);
    }

    private void RememberCurrentViewCore(NoteScope? scope, SearchResult? result)
    {
        if (changingDestination || !IsWorkspaceCurrent ||
            Compatibility.WorkspacePath is not { } path || PeopleNavigation.SelectedEmail is not { } email ||
            workflow.GetRelationships(email)?.Person is not { Archived: false }) return;
        if (result?.NoteArchived == true) return;
        foreach (var noteScope in Enum.GetValues<NoteScope>())
            if (NoteViewFor(noteScope) is { RememberedRecovery: true, Selected: null } recovered)
                noteViews[noteScope] = recovered with { RememberedRecovery = false, Exact = false };
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
        if (RestoreRememberedPerson(view) &&
            PeopleNavigation.SelectedResult is { Source: SearchSource.InheritedTeamNote or SearchSource.HistoricalTeamNote } result)
            PeopleNavigation = PeopleNavigation with { Query = result.NoteTitle! };
    }

    private bool RestoreRememberedPerson(RememberedView view)
    {
        var generation = destinationGeneration;
        var person = workflow.GetRelationships(view.PersonEmail)?.Person;
        var destination = person is { Archived: false } && view.Source is not null ? ResolveRememberedNote(person, view) : null;
        if (person is not { Archived: false })
        {
            ViewRecoveryFeedback = "The remembered Person or exact note is unavailable or archived. " +
                "Your preference and writing are retained; no replacement was opened. Find a Person using search, Favorites or People to deliberately select another view.";
            return false;
        }
        var noteUnavailable = view.Source is not null && destination is null;
        ViewRecoveryFeedback = noteUnavailable
            ? "The remembered exact note is unavailable or archived. " +
                "Your preference and writing are retained; no replacement was opened. Find a Person using search, Favorites or People to deliberately select another view."
            : null;
        if (!NavigateAutomaticPerson(person.Email, destination, recoverRememberedNote: noteUnavailable))
        {
            if (generation != destinationGeneration) return false;
            ViewRecoveryFeedback = "The remembered view could not be restored. " +
                (navigationError ?? workflow.Status) + " Your preference and writing are retained; no replacement was opened.";
            return false;
        }
        if (!noteUnavailable)
            PeopleNavigation = PeopleNavigation with
            {
                SelectedResult = destination?.Source is SearchSource.InheritedTeamNote or SearchSource.HistoricalTeamNote
                    ? destination : null,
                LastValidView = view
            };
        return true;
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
    private IReadOnlyList<PersonSearchResult> peopleSearchResults = [];
    private long peopleSearchRequest;

    public void ChangePeopleQuery(string query)
    {
        if (query == PeopleNavigation.Query) return;
        var request = BeginPeopleSearch(query);
        PublishPeopleSearchResults(request, workflow.SearchPeople(query));
    }

    public long BeginPeopleSearch(string query)
    {
        var request = Interlocked.Increment(ref peopleSearchRequest);
        if (query == PeopleNavigation.Query) return request;
        peopleSearchResults = [];
        PeopleNavigation = PeopleNavigation with { Query = query, HighlightedResult = null };
        Notify();
        return request;
    }

    public bool PublishPeopleSearchResults(long request, IReadOnlyList<PersonSearchResult> results)
    {
        if (request != Volatile.Read(ref peopleSearchRequest)) return false;
        peopleSearchResults = results;
        var highlighted = PeopleNavigation.HighlightedResult?.Person.Email;
        PeopleNavigation = PeopleNavigation with
        {
            HighlightedResult = results.FirstOrDefault(r =>
                r.Person.Email.Equals(highlighted, StringComparison.OrdinalIgnoreCase)) ?? results.FirstOrDefault()
        };
        Notify();
        return true;
    }

    private void ResetPeopleSearch()
    {
        Interlocked.Increment(ref peopleSearchRequest);
        peopleSearchResults = [];
    }

    public void MoveSearchHighlight(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var results = peopleSearchResults;
        var index = -1;
        for (var candidate = 0; candidate < results.Count; candidate++)
            if (results[candidate].Person.Email.Equals(PeopleNavigation.HighlightedResult?.Person.Email,
                StringComparison.OrdinalIgnoreCase))
            {
                index = candidate;
                break;
            }
        PeopleNavigation = PeopleNavigation with
        {
            HighlightedResult = results.Count == 0 ? null : results[Math.Clamp(index + direction, 0, results.Count - 1)]
        };
        Notify();
    }

    public void HighlightSearchResult(PersonSearchResult? result)
    {
        PeopleNavigation = PeopleNavigation with { HighlightedResult = result };
        Notify();
    }

    public void ShowPeopleOverlay(bool open)
    {
        PeopleNavigation = PeopleNavigation with { OverlayOpen = open };
        Notify();
    }

    public void ShowArchivedPeople(bool show)
    {
        PeopleNavigation = PeopleNavigation with { ArchivedPeopleVisible = show };
        Notify();
    }

    public void CloseSearchContext()
    {
        PeopleNavigation = PeopleNavigation with { SelectedResult = null };
        Notify();
    }

    public void RememberPeopleBrowsing(IEnumerable<string> expandedPeople, double offset, double horizontalOffset = 0,
        double extentWidth = 0)
    {
        if (!PeopleNavigation.IsBrowsing) return;
        if (!double.IsFinite(offset) || offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (!double.IsFinite(horizontalOffset) || horizontalOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalOffset));
        if (!double.IsFinite(extentWidth) || extentWidth < 0) throw new ArgumentOutOfRangeException(nameof(extentWidth));
        PeopleNavigation = PeopleNavigation with
        {
            ExpandedPeople = expandedPeople.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            BrowseOffset = offset,
            BrowseHorizontalOffset = horizontalOffset,
            BrowseExtentWidth = extentWidth
        };
        Notify();
    }

    public string? PeopleTreeActionFeedback { get; private set; }
    public PeopleTreeExpansionView? PeopleTreeExpansion { get; private set; }
    private ImmutableStack<ReportingNode> pendingTreeExpansion = ImmutableStack<ReportingNode>.Empty;
    private IReadOnlyList<ReportingNode>? treeExpansionRoots;
    private long reportingChangeGeneration;
    private int reportingChanges;

    private void ResetPeopleTreeExpansion()
    {
        pendingTreeExpansion = ImmutableStack<ReportingNode>.Empty;
        treeExpansionRoots = null;
        PeopleTreeExpansion = null;
        PeopleTreeActionFeedback = null;
        PropertyChanged?.Invoke(this, new(nameof(PeopleTreeExpansion)));
        PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
    }

    public IReadOnlyList<PeopleTreeAction> GetPeopleTreeActions(string email)
    {
        if (Volatile.Read(ref reportingChanges) != 0 || PeopleTreeExpansion is { IsRunning: true }) return [];
        var subtree = CurrentReportingSubtree(email);
        var actions = new List<PeopleTreeAction>();
        var expanded = PeopleNavigation.ExpandedPeople.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (CurrentReportingNode(email) is { } root)
        {
            var pending = new Stack<ReportingNode>();
            pending.Push(root);
            while (pending.TryPop(out var node))
            {
                if (node.Reports.Count > 0 && !expanded.Contains(node.Person.Email))
                {
                    actions.Add(PeopleTreeAction.ExpandSubtree);
                    break;
                }
                foreach (var report in node.Reports) pending.Push(report);
            }
        }
        if (subtree.Overlaps(expanded)) actions.Add(PeopleTreeAction.CollapseSubtree);
        if (CurrentReportingOutsideBranches(subtree, email).Overlaps(expanded))
            actions.Add(PeopleTreeAction.CollapseOtherBranches);
        return actions;
    }

    public bool ExecutePeopleTreeAction(string email, PeopleTreeAction action)
    {
        lock (destinationGate) return ExecutePeopleTreeActionCore(email, action);
    }

    private bool ExecutePeopleTreeActionCore(string email, PeopleTreeAction action)
    {
        if (reportingChanges != 0)
        {
            PeopleTreeActionFeedback = "Tree action was not applied: Workspace work is updating the current view. Try again when it finishes.";
            PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
            return false;
        }
        if (PeopleTreeExpansion is { IsRunning: true })
        {
            PeopleTreeActionFeedback = "Tree action was not applied: subtree expansion is already running. Cancel it before another tree action.";
            PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
            return false;
        }
        if (action == PeopleTreeAction.ExpandSubtree)
        {
            if (!GetPeopleTreeActions(email).Contains(action))
            {
                PeopleTreeActionFeedback = "Expand subtree was not applied: no hidden current reporting levels are available, or expansion is already running.";
                PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
                return false;
            }
            treeExpansionRoots = ReportingRoots;
            pendingTreeExpansion = ImmutableStack<ReportingNode>.Empty.Push(CurrentReportingNode(email)!);
            PeopleTreeExpansion = new(email, 0, 0, PeopleTreeExpansionStatus.Running);
            PeopleTreeActionFeedback = null;
            PropertyChanged?.Invoke(this, new(nameof(PeopleTreeExpansion)));
            PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
            return true;
        }
        var subtree = CurrentReportingSubtree(email);
        var (scope, label) = action switch
        {
            PeopleTreeAction.CollapseSubtree => (subtree, "Collapse subtree"),
            PeopleTreeAction.CollapseOtherBranches => (CurrentReportingOutsideBranches(subtree, email), "Collapse other branches"),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (!scope.Overlaps(PeopleNavigation.ExpandedPeople))
        {
            PeopleTreeActionFeedback = $"{label} was not applied: that current reporting scope has no expansion to reset.";
            PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
            return false;
        }
        PeopleNavigation = PeopleNavigation with
        {
            ExpandedPeople = PeopleNavigation.ExpandedPeople.Where(person => !scope.Contains(person)).ToArray()
        };
        PeopleTreeActionFeedback = null;
        // Presentation changes must not enter the mutation/navigation observation path or clear retained errors.
        PropertyChanged?.Invoke(this, new(nameof(PeopleNavigation)));
        PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
        return true;
    }

    public IReadOnlyList<string> ContinuePeopleTreeExpansion()
    {
        PeopleTreeExpansionView progress;
        ImmutableStack<ReportingNode> pending;
        HashSet<string> expanded;
        long generation;
        lock (destinationGate)
        {
            if (reportingChanges != 0 || PeopleTreeExpansion is not { IsRunning: true } current) return [];
            progress = current;
            if (!IsWorkspaceCurrent || !ReferenceEquals(treeExpansionRoots, ReportingRoots))
            {
                InterruptPeopleTreeExpansion(progress);
                return [];
            }
            generation = reportingChangeGeneration;
            pending = pendingTreeExpansion;
            expanded = PeopleNavigation.ExpandedPeople.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var added = new List<string>();
        var visited = progress.VisitedPeople;
        var branches = progress.ExpandedBranches;
        for (var count = 0; count < 64 && !pending.IsEmpty; count++)
        {
            var node = pending.Peek();
            pending = pending.Pop();
            visited++;
            if (node.Reports.Count > 0 && expanded.Add(node.Person.Email))
            {
                branches++;
                added.Add(node.Person.Email);
            }
            // Visit parents before reports so every completed branch remains useful on interruption.
            for (var index = node.Reports.Count - 1; index >= 0; index--) pending = pending.Push(node.Reports[index]);
        }
        lock (destinationGate)
        {
            if (!ReferenceEquals(PeopleTreeExpansion, progress) || reportingChanges != 0) return [];
            if (!IsWorkspaceCurrent || !ReferenceEquals(treeExpansionRoots, ReportingRoots))
            {
                InterruptPeopleTreeExpansion(progress);
                return [];
            }
            if (generation != reportingChangeGeneration) return [];
            pendingTreeExpansion = pending;
            if (branches != progress.ExpandedBranches)
                PeopleNavigation = PeopleNavigation with { ExpandedPeople = expanded.ToArray() };
            PeopleTreeExpansion = progress with
            {
                VisitedPeople = visited, ExpandedBranches = branches,
                Status = pending.IsEmpty ? PeopleTreeExpansionStatus.Completed : PeopleTreeExpansionStatus.Running
            };
            if (!PeopleTreeExpansion.IsRunning) treeExpansionRoots = null;
            if (branches != progress.ExpandedBranches)
                PropertyChanged?.Invoke(this, new(nameof(PeopleNavigation)));
            PropertyChanged?.Invoke(this, new(nameof(PeopleTreeExpansion)));
            return added;
        }
    }

    private void InterruptPeopleTreeExpansion(PeopleTreeExpansionView progress)
    {
        pendingTreeExpansion = ImmutableStack<ReportingNode>.Empty;
        treeExpansionRoots = null;
        PeopleTreeExpansion = progress with { Status = PeopleTreeExpansionStatus.Interrupted };
        PeopleTreeActionFeedback = "Expand subtree interrupted: the current reporting view changed; completed expansion retained. Expand again in the current view.";
        PropertyChanged?.Invoke(this, new(nameof(PeopleTreeExpansion)));
        PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
    }

    public void CancelPeopleTreeExpansion()
    {
        lock (destinationGate)
        {
            if (PeopleTreeExpansion is not { IsRunning: true } progress) return;
            pendingTreeExpansion = ImmutableStack<ReportingNode>.Empty;
            treeExpansionRoots = null;
            PeopleTreeExpansion = progress with { Status = PeopleTreeExpansionStatus.Cancelled };
            PeopleTreeActionFeedback = "Expand subtree cancelled; completed expansion retained. Not all reporting levels were expanded.";
            PropertyChanged?.Invoke(this, new(nameof(PeopleTreeExpansion)));
            PropertyChanged?.Invoke(this, new(nameof(PeopleTreeActionFeedback)));
        }
    }

    private ReportingNode? CurrentReportingNode(string email)
    {
        if (!IsWorkspaceCurrent) return null;
        var pending = new Stack<ReportingNode>(ReportingRoots);
        while (pending.TryPop(out var node))
        {
            if (node.Person.Email.Equals(email, StringComparison.OrdinalIgnoreCase)) return node;
            foreach (var report in node.Reports) pending.Push(report);
        }
        return null;
    }

    private HashSet<string> CurrentReportingSubtree(string email)
    {
        var subtree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (CurrentReportingNode(email) is not { } root) return subtree;
        var pending = new Stack<ReportingNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
            if (subtree.Add(node.Person.Email))
                foreach (var report in node.Reports) pending.Push(report);
        return subtree;
    }

    private HashSet<string> CurrentReportingOutsideBranches(HashSet<string> subtree, string email)
    {
        var outside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (subtree.Count == 0) return outside;
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<ReportingNode>(ReportingRoots);
        while (pending.TryPop(out var node))
            if (outside.Add(node.Person.Email))
                foreach (var report in node.Reports)
                {
                    parents.Add(report.Person.Email, node.Person.Email);
                    pending.Push(report);
                }
        var protectedPeople = new HashSet<string>(subtree, StringComparer.OrdinalIgnoreCase);
        while (parents.TryGetValue(email, out var manager))
        {
            protectedPeople.Add(manager);
            email = manager;
        }
        outside.ExceptWith(protectedPeople);
        return outside;
    }

    private bool ObserveDestination(Func<bool> operation) => Observe(() =>
    {
        lock (destinationGate)
        {
            var previous = changingDestination;
            changingDestination = false;
            try
            {
                var accepted = operation();
                if (accepted) destinationGeneration++;
                return accepted;
            }
            finally { changingDestination = previous; }
        }
    });

    public bool NavigatePerson(string email) => ObserveDestination(() =>
    {
        var accepted = NavigatePeopleCore(email, null);
        if (accepted) RememberCurrentView();
        return accepted;
    });

    public bool NavigateInheritedTeamNote(Guid noteId) => ObserveDestination(() =>
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
        try
        {
            accepted = NavigatePeopleCore(context.Owner.Email, null);
            if (accepted)
            {
                var view = TeamNotesView;
                var editing = view.SelectedNote?.Id == noteId && view.IsEditing &&
                    HasUnsavedNoteDraft(NoteScope.Team, noteId);
                accepted = OpenOwnedNote(NoteScope.Team, noteId, editing);
            }
        }
        finally { changingDestination = false; }
        if (accepted) RememberCurrentView(NoteScope.Team);
        return accepted;
    });

    public bool NavigateSearchResult(PersonSearchResult result) => ObserveDestination(() =>
    {
        var current = workflow.SearchPeople(PeopleNavigation.Query).FirstOrDefault(r =>
            string.Equals(r.Person.Email, result.Person.Email, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            navigationError = "That search match is no longer available in the validated Workspace. Search again; no replacement was opened.";
            return false;
        }
        changingDestination = true;
        bool accepted;
        try { accepted = NavigatePeopleCore(current.Person.Email, null, requireNavigation: true); }
        finally { changingDestination = false; }
        if (accepted)
        {
            ResetPeopleSearch();
            ExpandPeoplePath(current.Person.Email);
            PeopleNavigation = PeopleNavigation with
            {
                Query = "", HighlightedResult = null,
                ArchivedPeopleVisible = current.Person.Archived || PeopleNavigation.ArchivedPeopleVisible
            };
            RememberCurrentView();
        }
        return accepted;
    });

    private bool NavigatePeopleCore(string email, SearchResult? result, bool requireNavigation = false,
        bool recoverRememberedNote = false)
    {
        var generation = destinationGeneration;
        var person = workflow.GetRelationships(email)?.Person;
        if (person is null)
        {
            navigationError = "That Person is unavailable. No replacement was opened.";
            return false;
        }
        if ((requireNavigation || result is not null || !email.Equals(PeopleNavigation.SelectedEmail, StringComparison.OrdinalIgnoreCase)) &&
            !workflow.CanNavigatePeople)
        {
            navigationError = "Navigation blocked: local writing is pending or was NOT saved. Save, retry or discard the draft before leaving. " +
                (workflow.LocalSaveError ?? workflow.Status);
            return false;
        }
        if (recoverRememberedNote)
        {
            foreach (var scope in Enum.GetValues<NoteScope>())
                noteViews[scope] = new(person.Email, Exact: true, RememberedRecovery: true);
        }
        else if (!OpenOwnedNotes(NoteScope.Person, person.Email) || generation != destinationGeneration ||
            !OpenOwnedNotes(NoteScope.Team, person.Email) || generation != destinationGeneration) return false;
        if (requireNavigation)
        {
            foreach (var scope in Enum.GetValues<NoteScope>())
            {
                if (!GetOwnedNotesView(scope).HasUnsavedChanges) OpenOngoingNote(scope);
                if (generation != destinationGeneration) return false;
            }
        }
        if (generation != destinationGeneration) return false;
        if (result?.Source == SearchSource.PersonNote && result.NoteId is { } personNote &&
            !OpenOwnedNote(NoteScope.Person, personNote)) return false;
        if (result?.Source == SearchSource.OwnedTeamNote && result.NoteId is { } teamNote &&
            !OpenOwnedNote(NoteScope.Team, teamNote)) return false;
        if (generation != destinationGeneration) return false;
        workflow.SelectProfilePerson(person.Email);
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
