using System.ComponentModel;

namespace OrgExplorer.Workflow;

public sealed partial class WorkspaceViewModel : IApplicationWorkflow, INotifyPropertyChanged
{
    private readonly IApplicationWorkflow workflow;
    public WorkspaceViewModel(IApplicationWorkflow workflow)
    {
        this.workflow = workflow;
        workflow.WorkspaceChanged += ForwardWorkspaceChange;
    }
    public event EventHandler? WorkspaceChanged;
    public bool IsWorkspaceCurrent => workflow.IsWorkspaceCurrent;
    public bool CanSafelyClose => workflow.CanSafelyClose;
    public string? LocalSaveError => workflow.LocalSaveError;
    public void DiscardUnsavedChanges(Guid? noteId = null) => Observe(() => { workflow.DiscardUnsavedChanges(noteId); return true; });
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
    public string Status => workflow.Status;
    public bool OpenWorkspace(string path) => Observe(() => workflow.OpenWorkspace(path));
    public ImportPreview PreviewImport(string json) => Observe(() => workflow.PreviewImport(json));
    public ImportPreview PreviewImportFile(string path) => Observe(() => workflow.PreviewImportFile(path));
    public bool ApplyImport(Guid previewId) => Observe(() => workflow.ApplyImport(previewId));
    public bool EditPreferredName(string email, string? preferredName) => Observe(() => workflow.EditPreferredName(email, preferredName));
    public bool EditPersonArchived(string email, bool archived) => Observe(() => workflow.EditPersonArchived(email, archived));
    public bool EditPersonManager(string email, string? managerEmail) => Observe(() => workflow.EditPersonManager(email, managerEmail));
    public PersonPhoto? GetPhoto(string email) => workflow.GetPhoto(email);
    public bool EditPhoto(string email, string? localPath) => Observe(() => workflow.EditPhoto(email, localPath));
    public bool EditPersonText(string email, PersonTextField field, string? value) => Observe(() => workflow.EditPersonText(email, field, value));
    public bool OpenLinkedInProfile(string email) => Observe(() => workflow.OpenLinkedInProfile(email));

    private T Observe<T>(Func<T> operation)
    {
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSafelyClose)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalSaveError)));
    }
}
