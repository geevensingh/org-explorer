using System.Diagnostics;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal sealed record FinalNoteExpectation(NoteScope Scope, Guid Id, string Owner, string WrongOwner,
    string Title, string Prefix, string Markdown);

internal static class FinalNoteVerification
{
    internal static double Verify(string workspace, FinalNoteExpectation expected,
        Func<IApplicationWorkflow>? createReader = null)
    {
        var elapsed = Stopwatch.StartNew();
        using (var final = (createReader ?? (() => ApplicationWorkflow.CreateFileBacked()))())
        {
            if (!final.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly))
                throw new InvalidOperationException("The final independent note verification could not reopen the real Workspace: " + final.Status);
            var note = final.BeginNoteDraft(expected.Scope, expected.Owner, expected.Id);
            if (note?.Markdown.StartsWith(expected.Prefix, StringComparison.Ordinal) != true ||
                final.BeginNoteDraft(expected.Scope, expected.WrongOwner, expected.Id) is not null)
                throw new InvalidOperationException("The actual WPF saved acknowledgment was not backed by exact committed owner writing.");
            if (note?.Title != expected.Title || note.Markdown != expected.Markdown)
                throw new InvalidOperationException("The final independent note verification did not retain exact title and writing.");
        }
        return elapsed.Elapsed.TotalMilliseconds;
    }
}
