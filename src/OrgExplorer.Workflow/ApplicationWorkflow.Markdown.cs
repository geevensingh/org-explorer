using System.Text;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private void WriteMarkdownAtomically(string folder, Guid id, string content, Action validateDraft)
    {
        if (workspaceConflicts.Count == 0) publicationOutcome = WorkspacePublicationOutcome.NotPublished;
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
            var transaction = BeginRecoveryTransaction("note", revision);
            var candidate = new Dictionary<string, byte[]>(expected, StringComparer.OrdinalIgnoreCase) { [destination] = bytes };
            var recovery = PrepareRecovery(candidate, transaction,
                new(Path.GetRelativePath(workspace!, destination),
                    expected.TryGetValue(destination, out var before) ? ByteDigest(before) : null, ByteDigest(bytes)));
            if (WorkspaceFingerprint() != revision)
                throw new IOException("Workspace changed during recovery staging; no note changes saved.");
            WriteDurablyBytes(staged, bytes);
            PublishCurrent(staged, destination, expected, candidate, recovery);
            staged = null;
            AcceptWorkspaceRevision(candidate);
            FinishCommittedRecovery(recovery);
        }
        finally
        {
            if (staged is not null && PublicationOutcome != WorkspacePublicationOutcome.Indeterminate)
            {
                try { DeleteWorkspaceFile(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
