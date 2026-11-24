using OrgExplorer.Desktop.Smoke;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class FinalNoteVerificationTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "final-note-" + Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void One_fresh_final_reopen_verifies_prefix_final_generation_and_exact_owner(NoteScope scope)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(root), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"92000000-0000-4000-8000-000000000099",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null},
             {"email":"other@example.com","displayName":"Other","managerEmail":"owner@example.com"}]}
            """).Id), app.Status);
        var basis = app.SaveNoteDraft(app.BeginNoteDraft(scope, "owner@example.com")!, "Exact title", "# Original request")!;
        using var early = ApplicationWorkflow.CreateFileBacked();
        Assert.True(early.OpenWorkspace(root, WorkspaceOpenMode.ExistingOnly), early.Status);
        Assert.NotNull(app.SaveNoteDraft(basis, basis.Title, "# Original request 0 1 2 3 4 5"));
        Assert.Equal("# Original request", early.BeginNoteDraft(scope, "owner@example.com", basis.Id)!.Markdown);
        var readers = 0;
        FinalNoteVerification.Verify(root, new(scope, basis.Id, "owner@example.com", "other@example.com",
            basis.Title, "# Original request", "# Original request 0 1 2 3 4 5"), () =>
        {
            readers++;
            return ApplicationWorkflow.CreateFileBacked();
        });
        Assert.Equal(1, readers);
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public async Task A_held_late_final_generation_is_not_verified_from_its_earlier_committed_prefix(NoteScope scope)
    {
        using var gate = new FinalCommitGate();
        var (store, note) = Seed(scope, gate);
        using var app = store;
        var expected = new FinalNoteExpectation(scope, note.Id, "owner@example.com", "other@example.com",
            note.Title, "# Original request", "# Original request final generation");
        gate.Armed = true;
        var saving = Task.Run(() => app.SaveNoteDraft(note, note.Title, expected.Markdown));
        try
        {
            Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(5)), "The actual final note Commit was not held.");
            Assert.Throws<InvalidOperationException>(() => FinalNoteVerification.Verify(root, expected));
            Assert.False(saving.IsCompleted);
        }
        finally { gate.Release.Set(); await saving.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.NotNull(await saving);
        FinalNoteVerification.Verify(root, expected);
    }

    [Theory]
    [InlineData(NoteScope.Person, "owner")]
    [InlineData(NoteScope.Team, "owner")]
    [InlineData(NoteScope.Person, "prefix")]
    [InlineData(NoteScope.Team, "prefix")]
    [InlineData(NoteScope.Person, "title")]
    [InlineData(NoteScope.Team, "title")]
    public void Incorrect_final_owner_request_or_title_remains_a_refusal(NoteScope scope, string damage)
    {
        var (store, note) = Seed(scope);
        using var app = store;
        Assert.NotNull(app.SaveNoteDraft(note, note.Title, "# Original request final generation"));
        var expected = new FinalNoteExpectation(scope, note.Id, "owner@example.com", "other@example.com",
            note.Title, "# Original request", "# Original request final generation");
        expected = damage switch
        {
            "owner" => expected with { Owner = "other@example.com" },
            "prefix" => expected with { Prefix = "# Different request" },
            "title" => expected with { Title = "Different title" },
            _ => throw new ArgumentException(nameof(damage))
        };
        Assert.Throws<InvalidOperationException>(() => FinalNoteVerification.Verify(root, expected));
    }

    [Theory]
    [InlineData(NoteScope.Person)]
    [InlineData(NoteScope.Team)]
    public void Corrupt_final_store_cannot_be_replaced_by_earlier_valid_data(NoteScope scope)
    {
        var (store, note) = Seed(scope);
        store.Dispose();
        File.WriteAllText(Path.Combine(root, "workspace.sqlite"), "Not a valid SQLite database");
        Assert.Throws<InvalidOperationException>(() => FinalNoteVerification.Verify(root,
            new(scope, note.Id, "owner@example.com", "other@example.com", note.Title, "# Original request", note.Markdown)));
    }

    private (IApplicationWorkflow Store, NoteDraft Note) Seed(NoteScope scope, IWorkspaceFileObserver? observer = null)
    {
        var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.True(app.OpenWorkspace(root), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"92000000-0000-4000-8000-000000000099",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"owner@example.com","displayName":"Owner","managerEmail":null},
             {"email":"other@example.com","displayName":"Other","managerEmail":"owner@example.com"}]}
            """).Id), app.Status);
        var note = app.SaveNoteDraft(app.BeginNoteDraft(scope, "owner@example.com")!, "Exact title", "# Original request")!;
        return (app, note);
    }

    private sealed class FinalCommitGate : IWorkspaceFileObserver, IDisposable
    {
        internal bool Armed;
        internal ManualResetEventSlim Entered = new();
        internal ManualResetEventSlim Release = new();
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (!Armed || phase.Name != "local-sqlite-commit" || phase.Kind != WorkspaceStoragePhaseKind.Begin) return;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The final verification fixture did not release Commit.");
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
