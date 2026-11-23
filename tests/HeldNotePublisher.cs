using System.IO;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

internal sealed class HeldNotePublisher : IWorkspaceFilePublisher, IWorkspaceFileObserver, IDisposable
{
    public bool HoldNextNote { get; set; }
    public bool HoldNextProfile { get; set; }
    public ManualResetEventSlim Entered { get; } = new();
    public ManualResetEventSlim Release { get; } = new();

    public void Publish(string staged, string destination, string? backup,
        WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
    {
        new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
    }

    public void Observe(WorkspaceFileEffect effect) { }

    public void ObservePhase(WorkspaceStoragePhase phase)
    {
        if (phase is { Name: "local-sqlite-commit", Kind: WorkspaceStoragePhaseKind.Begin } &&
            (HoldNextNote || HoldNextProfile))
        {
            HoldNextNote = false;
            HoldNextProfile = false;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Controlled local commit was not released.");
        }
    }

    public void Dispose() { Entered.Dispose(); Release.Dispose(); }
}
