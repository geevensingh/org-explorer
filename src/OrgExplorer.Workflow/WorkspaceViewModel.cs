using System.ComponentModel;

namespace OrgExplorer.Workflow;

public sealed partial class WorkspaceViewModel : IApplicationWorkflow, INotifyPropertyChanged
{
    private readonly IApplicationWorkflow workflow;
    private string? navigationError;
    public WorkspaceViewModel(IApplicationWorkflow workflow)
    {
        this.workflow = workflow;
        workflow.WorkspaceChanged += ForwardWorkspaceChange;
    }
    public event EventHandler? WorkspaceChanged;
    public bool IsWorkspaceCurrent => workflow.IsWorkspaceCurrent;
    public WorkspaceCompatibility Compatibility => workflow.Compatibility;
    public bool CanSafelyClose => workflow.CanSafelyClose;
    public bool CanEnterImport => workflow.CanEnterImport;
    public bool HasFailedImport => workflow.HasFailedImport;
    public bool RestartWhenSafe(Action<string?> restart) => Observe(() => workflow.RestartWhenSafe(restart));
    public string? LocalSaveError => workflow.LocalSaveError;
    public void DiscardUnsavedChanges(Guid? noteId = null) => Observe(() => { workflow.DiscardUnsavedChanges(noteId); return true; });
    public void DiscardImportChanges() => Observe(() => { workflow.DiscardImportChanges(); return true; });
    private void ForwardWorkspaceChange(object? sender, EventArgs e)
    {
        Notify();
        WorkspaceChanged?.Invoke(this, e);
    }
    public bool RefreshWorkspace() => Observe(workflow.RefreshWorkspace);
    public void Dispose()
    {
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
    public string Status => navigationError ?? workflow.Status;
    public bool IsImportOpen { get; private set; }
    public bool TryOpenImport() => Observe(() =>
    {
        if (!workflow.TryLeaveProfileEdit(ProfileLeaveDecision.Stay)) return false;
        if (!workflow.CanEnterImport)
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
            var opened = workflow.OpenWorkspace(path, mode);
            if (opened) IsImportOpen = false;
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
    public PersonProfile? SavePersonTextDraft(PersonProfile basis, PersonTextField field, string? value) =>
        Observe(() => workflow.SavePersonTextDraft(basis, field, value));
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWorkspaceCurrent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Compatibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSafelyClose)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEnterImport)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFailedImport)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalSaveError)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PublicationOutcome)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileEdit)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImportOpen)));
    }
}
