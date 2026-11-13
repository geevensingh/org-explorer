using System.IO;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

internal sealed class HeldNotePublisher : IWorkspaceFilePublisher, IDisposable
{
    public bool HoldNextNote { get; set; }
    public bool HoldNextProfile { get; set; }
    public ManualResetEventSlim Entered { get; } = new();
    public ManualResetEventSlim Release { get; } = new();

    public void Publish(string staged, string destination, string? backup,
        WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
    {
        if (HoldNextNote && Path.GetExtension(destination) == ".md" ||
            HoldNextProfile && Path.GetFileName(destination) == "workspace.json")
        {
            HoldNextNote = false;
            HoldNextProfile = false;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Controlled publication was not released.");
        }
        new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
    }

    public void Dispose() { Entered.Dispose(); Release.Dispose(); }
}
