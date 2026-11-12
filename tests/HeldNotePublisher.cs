using System.IO;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

internal sealed class HeldNotePublisher : IWorkspaceFilePublisher, IDisposable
{
    public bool HoldNextNote { get; set; }
    public ManualResetEventSlim Entered { get; } = new();
    public ManualResetEventSlim Release { get; } = new();

    public void Publish(string staged, string destination, string? backup)
    {
        if (HoldNextNote && Path.GetExtension(destination) == ".md")
        {
            HoldNextNote = false;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Controlled publication was not released.");
        }
        if (File.Exists(destination)) File.Replace(staged, destination, backup);
        else File.Move(staged, destination);
    }

    public void Dispose() { Entered.Dispose(); Release.Dispose(); }
}
