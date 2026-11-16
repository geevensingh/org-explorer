using System.ComponentModel;

namespace OrgExplorer.Workflow;

public sealed record RetainedPersonTextDraft(PersonProfile Basis, PersonTextField Field, string? Value, string? Error = null);

public sealed partial class WorkspaceViewModel : IApplicationWorkflow, INotifyPropertyChanged
{
    private readonly IApplicationWorkflow workflow;
    private readonly WorkspaceSettings? settings;
    private string? navigationError;
    private readonly object personTextDraftGate = new();
    private readonly Dictionary<(string Email, PersonTextField Field), RetainedPersonTextDraft> personTextDrafts = [];
    private bool HasLocalPersonTextDrafts { get { lock (personTextDraftGate) return personTextDrafts.Count != 0; } }
    public WorkspaceViewModel(IApplicationWorkflow workflow, WorkspaceSettings? settings = null)
    {
        this.workflow = workflow;
        this.settings = settings;
        workflow.WorkspaceChanged += ForwardWorkspaceChange;
    }
    public event EventHandler? WorkspaceChanged;
    public bool IsWorkspaceCurrent => workflow.IsWorkspaceCurrent;
    public WorkspaceCompatibility Compatibility => workflow.Compatibility;
    public bool CanSafelyClose => !HasLocalNoteDrafts && !HasLocalPersonTextDrafts && workflow.CanSafelyClose;
    public bool CanNavigatePeople => workflow.CanNavigatePeople;
    public bool CanEnterImport => !HasLocalPersonTextDrafts && workflow.CanEnterImport;
    public bool HasFailedImport => workflow.HasFailedImport;
    public bool HasPendingStorageMaintenance => workflow.HasPendingStorageMaintenance;
    public bool LastWorkspaceOpenWasRejected { get; private set; }
    public bool RestartWhenSafe(Action<string?> restart) => Observe(() =>
    {
        if (!HasLocalNoteDrafts && !HasLocalPersonTextDrafts) return workflow.RestartWhenSafe(restart);
        navigationError = "Restart blocked: private edits are not confirmed saved locally.";
        return false;
    });
    public string? LocalSaveError => workflow.LocalSaveError;
    public void DiscardUnsavedChanges(Guid? noteId = null) => Observe(() =>
    {
        if (!DiscardLocalNoteDrafts(noteId)) return false;
        workflow.DiscardUnsavedChanges(noteId);
        return true;
    });
    public void DiscardImportChanges() => Observe(() => { workflow.DiscardImportChanges(); return true; });
    private void ForwardWorkspaceChange(object? sender, EventArgs e)
    {
        Notify();
        WorkspaceChanged?.Invoke(this, e);
    }
    public bool RefreshWorkspace() => Observe(workflow.RefreshWorkspace);
    public void Dispose()
    {
        lock (noteDraftGate)
        {
            noteAutosaveStopped = true;
            foreach (var draft in localNoteDrafts.Values) draft.AdoptionDeferral.Dispose();
        }
        workflow.WorkspaceChanged -= ForwardWorkspaceChange;
        workflow.Dispose();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<PersonProfile> People => workflow.People;
    public IReadOnlyList<SearchResult> Search(string query) => workflow.Search(query);
    public IReadOnlyList<PersonProfile> FavoritePeople => workflow.FavoritePeople;
    public bool EditFavorite(string email, bool isFavorite) => Observe(() => workflow.EditFavorite(email, isFavorite));
    public IReadOnlyList<PersonProfile> ArchivedPeople => workflow.ArchivedPeople;
    public IReadOnlyList<ReportingNode> ReportingRoots => workflow.ReportingRoots;
    public PersonRelationships? GetRelationships(string email) => workflow.GetRelationships(email);
    public ProfileTimeline? GetProfileTimeline(string email) => workflow.GetProfileTimeline(email);
    public IReadOnlyList<HistoricalTeamContext> GetHistoricalTeamContexts(string email) => workflow.GetHistoricalTeamContexts(email);
    public string Status => navigationError ?? (HasLocalNoteDrafts || HasLocalPersonTextDrafts
        ? (HasLocalNoteDrafts ? "Pending private note edits - not yet saved." :
            "Pending Preferred-name drafts - not yet saved. Choose explicit Save or Cancel on the owner.") +
            (workflow.LocalSaveError is { } error ? "\n" + error : "") : workflow.Status);
    public string? OperationError => navigationError ?? workflow.OperationError;
    public bool IsImportOpen { get; private set; }
    public bool TryOpenImport() => Observe(() =>
    {
        if (!workflow.TryLeaveProfileEdit(ProfileLeaveDecision.Stay)) return false;
        if (!CanEnterImport)
        {
            navigationError = "Import navigation blocked: local writing is pending or was NOT saved. " +
                "Retry or discard the independent changes before leaving. " + (workflow.LocalSaveError ?? workflow.Status);
            return false;
        }
        IsImportOpen = true;
        return true;
    });
    public void ShowPeople() => Observe(() =>
    {
        IsImportOpen = false;
        return true;
    });
    public bool OpenWorkspace(string path) => OpenWorkspace(path, WorkspaceOpenMode.OpenOrCreate);
    public bool OpenWorkspace(string path, WorkspaceOpenMode mode) =>
        Observe(() =>
        {
            LastWorkspaceOpenWasRejected = false;
            if (HasLocalPersonTextDrafts || !workflow.CanSafelyClose && workflow.ProfileEdit is not { IsDirty: true })
            {
                LastWorkspaceOpenWasRejected = true;
                navigationError = "Workspace navigation blocked: local writing is pending or was NOT saved. " +
                    "Save, retry or discard the independent draft before reopening a Workspace. " +
                    (workflow.LocalSaveError ?? workflow.Status);
                return false;
            }
            var opened = workflow.OpenWorkspace(path, mode);
            if (opened)
            {
                IsImportOpen = false;
                noteViews.Clear();
                retainedNoteViews.Clear();
                ongoingPreferenceError = null;
                PeopleNavigation = new("", false, null, null, [], 0);
                LayoutPreferenceFeedback = null;
                ViewPreferenceFeedback = null;
                ViewRecoveryFeedback = null;
                if (mode == WorkspaceOpenMode.ExistingOnly && IsWorkspaceCurrent) RestoreRememberedView();
            }
            return opened;
        });
    public ImportPreview PreviewImport(string json) => Observe(() => workflow.PreviewImport(json));
    public ImportPreview PreviewImportFile(string path) => Observe(() => workflow.PreviewImportFile(path));
    public bool ApplyImport(Guid previewId) => Observe(() => workflow.ApplyImport(previewId));
    public bool EditPreferredName(string email, string? preferredName) => Observe(() => workflow.EditPreferredName(email, preferredName));
    public bool EditPersonArchived(string email, bool archived, bool confirmed = true,
        ProfileLeaveDecision profileDecision = ProfileLeaveDecision.Stay) =>
        Observe(() => workflow.EditPersonArchived(email, archived, confirmed, profileDecision));
    public bool EditPersonManager(string email, string? managerEmail) => Observe(() => workflow.EditPersonManager(email, managerEmail));
    public PersonPhoto? GetPhoto(string email) => workflow.GetPhoto(email);
    public bool EditPhoto(string email, string? localPath) => Observe(() => workflow.EditPhoto(email, localPath));
    public bool EditPersonText(string email, PersonTextField field, string? value) => Observe(() => workflow.EditPersonText(email, field, value));
    public void RetainPersonTextDraft(PersonProfile basis, PersonTextField field, string? value)
    {
        if (field != PersonTextField.PreferredName)
            throw new ArgumentException("Ordinary fields belong to the complete profile draft.", nameof(field));
        lock (personTextDraftGate)
        {
            var key = (basis.Email.ToLowerInvariant(), field);
            if (value == basis.PreferredName && !personTextDrafts.ContainsKey(key)) return;
            personTextDrafts[key] = new(basis, field, value, personTextDrafts.GetValueOrDefault(key)?.Error);
            workflow.RetainPersonTextDraft(basis, field, value);
        }
        Notify();
    }

    public RetainedPersonTextDraft? GetPersonTextDraft(string email, PersonTextField field)
    {
        lock (personTextDraftGate) return personTextDrafts.GetValueOrDefault((email.ToLowerInvariant(), field));
    }

    public PersonProfile? SavePersonTextDraft(PersonProfile basis, PersonTextField field, string? value) => Observe(() =>
    {
        var accepted = workflow.SavePersonTextDraft(basis, field, value);
        lock (personTextDraftGate)
        {
            var key = (basis.Email.ToLowerInvariant(), field);
            if (personTextDrafts.TryGetValue(key, out var latest))
            {
                if (accepted is not null && latest.Value == value) personTextDrafts.Remove(key);
                else personTextDrafts[key] = latest with
                {
                    Basis = accepted ?? latest.Basis,
                    Error = accepted is null ? workflow.LocalSaveError ?? workflow.Status : null
                };
            }
        }
        return accepted;
    });
    public void DiscardPersonTextDraft(PersonProfile basis, PersonTextField field) =>
        Observe(() =>
        {
            lock (personTextDraftGate) personTextDrafts.Remove((basis.Email.ToLowerInvariant(), field));
            workflow.DiscardPersonTextDraft(basis, field);
            return true;
        });
    public bool OpenLinkedInProfile(string email) => Observe(() => workflow.OpenLinkedInProfile(email));

    private T Observe<T>(Func<T> operation)
    {
        navigationError = null;
        var result = operation();
        Notify();
        return result;
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(People)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArchivedPeople)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoritePeople)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReportingRoots)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OperationError)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWorkspaceCurrent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Compatibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSafelyClose)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanNavigatePeople)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEnterImport)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFailedImport)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPendingStorageMaintenance)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastWorkspaceOpenWasRejected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalSaveError)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Synchronization)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PublicationOutcome)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileEdit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImportOpen)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PersonNotesView)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TeamNotesView)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeopleNavigation)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PaneProportions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LayoutPreferenceFeedback)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewPreferenceFeedback)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewRecoveryFeedback)));
    }
}
