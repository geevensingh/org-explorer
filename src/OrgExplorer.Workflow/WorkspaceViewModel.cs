using System.ComponentModel;

namespace OrgExplorer.Workflow;

public sealed partial class WorkspaceViewModel(IApplicationWorkflow workflow) : IApplicationWorkflow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<PersonProfile> People => workflow.People;
    public IReadOnlyList<ReportingNode> ReportingRoots => workflow.ReportingRoots;
    public PersonRelationships? GetRelationships(string email) => workflow.GetRelationships(email);
    public string Status => workflow.Status;
    public bool OpenWorkspace(string path) => Observe(() => workflow.OpenWorkspace(path));
    public ImportPreview PreviewImport(string json) => Observe(() => workflow.PreviewImport(json));
    public ImportPreview PreviewImportFile(string path) => Observe(() => workflow.PreviewImportFile(path));
    public bool ApplyImport(Guid previewId) => Observe(() => workflow.ApplyImport(previewId));
    public bool EditPreferredName(string email, string? preferredName) => Observe(() => workflow.EditPreferredName(email, preferredName));
    public bool EditPersonText(string email, PersonTextField field, string? value) => Observe(() => workflow.EditPersonText(email, field, value));
    public bool OpenLinkedInProfile(string email) => Observe(() => workflow.OpenLinkedInProfile(email));

    private T Observe<T>(Func<T> operation)
    {
        var result = operation();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(People)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReportingRoots)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        return result;
    }
}
