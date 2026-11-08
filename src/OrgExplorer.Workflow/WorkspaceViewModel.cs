using System.ComponentModel;

namespace OrgExplorer.Workflow;

public sealed class WorkspaceViewModel(IApplicationWorkflow workflow) : IApplicationWorkflow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<PersonProfile> People => workflow.People;
    public string Status => workflow.Status;
    public bool OpenWorkspace(string path) => Observe(() => workflow.OpenWorkspace(path));
    public ImportPreview PreviewImport(string json) => Observe(() => workflow.PreviewImport(json));
    public ImportPreview PreviewImportFile(string path) => Observe(() => workflow.PreviewImportFile(path));
    public bool ApplyImport(Guid previewId) => Observe(() => workflow.ApplyImport(previewId));
    public bool EditPreferredName(string email, string? preferredName) => Observe(() => workflow.EditPreferredName(email, preferredName));
    public PersonPhoto? GetPhoto(string email) => workflow.GetPhoto(email);
    public bool EditPhoto(string email, string? localPath) => Observe(() => workflow.EditPhoto(email, localPath));

    private T Observe<T>(Func<T> operation)
    {
        var result = operation();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(People)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        return result;
    }
}
