namespace OrgExplorer.Workflow;

public enum WorkspaceFilePublicationMode { Automatic, Create, Replace }

public sealed class WorkspaceFilePublicationException(string message, bool definitelyNotPublished, Exception? inner = null)
    : IOException(message, inner)
{
    public bool DefinitelyNotPublished { get; } = definitelyNotPublished;
}

public interface IWorkspaceFilePublisher
{
    // Independent immutable objects may publish concurrently; selectors remain serialized.
    void Publish(string staged, string destination, string? backup,
        WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic);
}

public sealed class WorkspaceFilePublisher : IWorkspaceFilePublisher
{
    public void Publish(string staged, string destination, string? backup,
        WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
    {
        if (mode != WorkspaceFilePublicationMode.Automatic)
            foreach (var path in new[] { staged, destination, backup }.OfType<string>())
                for (var entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
                {
                    var attributes = new FileInfo(entry).Attributes;
                    if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
                        throw new WorkspaceFilePublicationException("Publication paths must not contain links.", true);
                }
        if (mode != WorkspaceFilePublicationMode.Automatic && backup is not null && File.Exists(backup))
            throw new WorkspaceFilePublicationException("Publication backup collision; existing bytes were not replaced.", true);
        try
        {
            if (mode == WorkspaceFilePublicationMode.Replace ||
                mode == WorkspaceFilePublicationMode.Automatic && File.Exists(destination)) File.Replace(staged, destination, backup);
            else File.Move(staged, destination);
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 2 or 3 or 32 or 33 or 80 or 183)
        {
            // These native refusals do not publish; partial ReplaceFile failures are deliberately not included.
            throw new WorkspaceFilePublicationException(error.Message, true, error);
        }
    }
}
