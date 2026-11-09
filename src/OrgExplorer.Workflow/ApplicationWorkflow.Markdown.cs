using System.Text;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private void WriteMarkdownAtomically(string folder, Guid id, string content, Action validateDraft)
    {
        string? staged = null;
        try
        {
            using var access = CoordinateWorkspaceIo(workspace!);
            EnsureWorkspaceWritable();
            using var lease = new FileStream(Path.Combine(workspace!, ".workspace.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            validateDraft();
            var expected = CaptureWorkspaceBytes();
            if (WorkspaceFingerprint(snapshot: expected) != revision)
                throw new IOException("Workspace changed. Reopen before editing; no note changes saved.");
            var directory = Path.Combine(workspace!, folder);
            Directory.CreateDirectory(directory);
            RejectNoteLink(directory);
            var destination = Path.Combine(directory, id.ToString("D") + ".md");
            if (File.Exists(destination)) RejectNoteLink(destination);
            staged = Path.Combine(directory, id.ToString("D") + ".next");
            var bytes = new UTF8Encoding(false, true).GetBytes(content);
            WriteDurablyBytes(staged, bytes);
            filePublisher.Publish(staged, destination, null);
            staged = null;
            expected[destination] = bytes;
            AcceptWorkspaceRevision(expected);
        }
        finally
        {
            if (staged is not null)
            {
                try { File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
