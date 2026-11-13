using System.Collections.Concurrent;
using System.Text.Json;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Persistence.Tests;

public sealed class WorkspaceWriteObservationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Storage_phases_bracket_real_validation_recovery_and_failed_publication()
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        foreach (var name in new[] { "storage.capture-current", "storage.prepare-recovery",
            "storage.validate-recovery", "storage.publish-recovery", "storage.reclamation", "storage.write-objects" })
            Assert.Contains(observer.Phases, phase => phase.Name == name && phase.Kind == WorkspaceStoragePhaseKind.Begin);
        Assert.All(observer.Phases.GroupBy(phase => phase.Id), phases =>
        {
            Assert.Equal(WorkspaceStoragePhaseKind.Begin, phases.First().Kind);
            Assert.Equal(WorkspaceStoragePhaseKind.End, phases.Last().Kind);
            Assert.True(phases.Last().Milliseconds >= 0);
            Assert.True(phases.Last().CpuMilliseconds >= 0);
        });
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000004",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
            """).Id), app.Status);
        observer.Phases.Clear();
        publisher.BeforePublish = (_, destination) =>
        {
            if (destination == Path.Combine(folder, "workspace.json"))
                throw new WorkspaceFilePublicationException("Controlled publication refusal.", true);
        };
        Assert.False(app.EditPreferredName("avery@example.com", "Retained writing"));
        var failed = Assert.Single(observer.Phases, phase => phase.Name == "storage.publish-current" &&
            phase.Kind == WorkspaceStoragePhaseKind.Failed);
        Assert.Contains(observer.Phases, phase => phase.Id == failed.Id && phase.Kind == WorkspaceStoragePhaseKind.Begin);
        Assert.DoesNotContain(observer.Phases, phase => phase.Id == failed.Id && phase.Kind == WorkspaceStoragePhaseKind.End);
        Assert.Null(app.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Timing_marks_the_active_phase_before_failure_and_preserves_the_original_error()
    {
        var lines = new List<string>();
        var active = false;
        var expected = new IOException("Controlled measured failure.");
        var actual = Assert.Throws<IOException>(() => ScaleFixture.Measure("controlled-operation", () =>
        {
            active = lines.Any(line => line.StartsWith("SCALE BEGIN controlled-operation", StringComparison.Ordinal));
            throw expected;
        }, lines.Add));
        Assert.Same(expected, actual);
        Assert.True(active);
        Assert.Contains(lines, line => line.StartsWith("SCALE END controlled-operation", StringComparison.Ordinal) &&
            line.Contains("Failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BEGIN", false)]
    [InlineData("END", false)]
    [InlineData("END", true)]
    public void Scale_phase_logging_failures_do_not_change_real_save_outcomes(string eventKind, bool rejectPublication)
    {
        var workspace = Path.Combine(folder, "workspace");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(seed.OpenWorkspace(workspace), seed.Status);
            Assert.True(seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000005",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """).Id), seed.Status);
        }
        var loggingError = new IOException("Controlled diagnostic write failure.");
        var publicationError = new WorkspaceFilePublicationException("Controlled real publication refusal.", true);
        var publisher = new InspectingPublisher
        {
            BeforePublish = (_, destination) =>
            {
                if (rejectPublication && destination == Path.Combine(workspace, "workspace.json"))
                    throw publicationError;
            }
        };
        var fixture = new ScaleFixture(folder)
        {
            Log = message =>
            {
                if (message.StartsWith($"SCALE {eventKind} storage.publish-current;", StringComparison.Ordinal))
                    throw loggingError;
            }
        };
        Exception? completionError = null;
        try
        {
            var app = fixture.Open(filePublisher: publisher);
            var saved = app.EditPreferredName("avery@example.com", "Ave");
            if (rejectPublication)
            {
                Assert.False(saved);
                Assert.Contains(publicationError.Message, app.LocalSaveError);
                Assert.DoesNotContain(loggingError.Message, app.LocalSaveError);
                Assert.False(app.CanSafelyClose);
            }
            else
            {
                Assert.True(saved, app.Status);
                Assert.Null(app.LocalSaveError);
                Assert.True(app.CanSafelyClose);
            }
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(workspace), reopened.Status);
            Assert.Equal(rejectPublication ? null : "Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        }
        finally { completionError = Record.Exception(fixture.Dispose); }
        var diagnostics = Assert.IsType<AggregateException>(completionError);
        Assert.Same(loggingError, Assert.Single(diagnostics.InnerExceptions));
    }

    [Fact]
    public void Real_workflow_accounts_for_every_staged_payload_before_publication_and_reopens_saved_data()
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var photo = Path.Combine(folder, "source.png");
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
             {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"}]}
            """).Id), app.Status);
        File.WriteAllBytes(photo, image);
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.EditPhoto("morgan@example.com", photo), app.Status);
        var note = app.CreatePersonNote("avery@example.com", "Private", "Keep this writing")!;
        var team = app.CreateTeamNote("morgan@example.com", "Team", "Keep team context")!;
        observer.Effects.Clear();
        publisher.BeforePublish = (staged, destination) =>
        {
            if (destination != Path.Combine(folder, "workspace.json")) return;
            var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
            Assert.NotEmpty(writes);
            foreach (var write in writes)
            {
                Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == write.Path);
            }
            Assert.Equal(new FileInfo(staged).Length, Assert.Single(writes, e => e.Path == staged).Bytes);
            using var pointer = JsonDocument.Parse(File.ReadAllBytes(staged));
            Assert.Equal(3, pointer.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(writes.Sum(e => e.Bytes) < 32768);
            Assert.Contains(writes, e => e.Path.StartsWith(Path.Combine(folder, "recovery")));
            Assert.Contains(writes, e => e.Path.StartsWith(Path.Combine(folder, "transactions")));
        };
        Assert.True(app.EditPreferredName("avery@example.com", "Ave"), app.Status);
        var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish &&
            e.Destination == Path.Combine(folder, "workspace.json"));
        Assert.False(File.Exists(Path.Combine(folder, "workspace.next")));
        output.WriteLine($"Preferred-name save: {writes.Length} payload writes, {writes.Sum(e => e.Bytes)} bytes.");
        foreach (var family in writes.GroupBy(e => Path.GetFileName(Path.GetDirectoryName(e.Path))))
            output.WriteLine($"{family.Key}: {family.Count()} writes, {family.Sum(e => e.Bytes)} bytes");
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal("morgan@example.com", reopened.GetRelationships("avery@example.com")!.Manager!.Email);
        Assert.Equal("Morgan", reopened.GetRelationships("morgan@example.com")!.Person.DirectoryName);
        Assert.Equal(image, reopened.GetPhoto("morgan@example.com")!.Bytes.ToArray());
        Assert.Equal("Keep this writing", reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == note.Id).Markdown);
        Assert.Equal("Keep team context", reopened.GetTeamNotes("morgan@example.com").Single(n => n.Id == team.Id).Markdown);
        Assert.Contains(reopened.GetProfileTimeline("avery@example.com")!.Changes, c =>
            c.Field == "preferredName" && c.NewValue.GetString() == "Ave");
        Assert.Contains("no-op", Assert.Single(reopened.PreviewImport("""
            {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
             {"email":"avery@example.com","displayName":"Avery","managerEmail":"morgan@example.com"}]}
            """).Changes));
    }

    [Fact]
    public void Deleting_transient_objects_cannot_hide_repeated_rejected_commit_write_activity()
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000003",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery","managerEmail":null},
             {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null}]}
            """).Id), app.Status);
        var before = WorkspaceFixtures.Bytes(folder);
        observer.Effects.Clear();
        long transientBytes = 0;
        publisher.BeforePublish = (_, destination) =>
        {
            if (destination != Path.Combine(folder, "workspace.json")) return;
            transientBytes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).Sum(e => e.Bytes);
            foreach (var path in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != ".workspace.lock" &&
                    !before.ContainsKey(Path.GetRelativePath(folder, path))))
                File.Delete(path);
            throw new WorkspaceFilePublicationException("Controlled failure after deleting all staged payloads.", true);
        };
        Assert.False(app.EditPreferredName("avery@example.com", "Not committed"));
        Assert.False(app.EditPreferredName("avery@example.com", "Still not committed"));
        Assert.False(app.CanSafelyClose);
        Assert.Equal(before.Keys.Order(), WorkspaceFixtures.Bytes(folder).Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, path)));
        var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        Assert.Equal(transientBytes, writes.Sum(e => e.Bytes));
        Assert.True(writes.Sum(e => e.Bytes) > 0);
        Assert.All(writes, e => Assert.False(File.Exists(e.Path)));
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish &&
            e.Destination == Path.Combine(folder, "workspace.json"));
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Null(reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal("Morgan", reopened.GetRelationships("morgan@example.com")!.Person.DirectoryName);
    }

    [Fact]
    public void Older_Workspace_rejection_never_writes_copies_or_publishes_any_payload()
    {
        WorkspaceFixtures.CopyVersionOne(folder);
        var originals = WorkspaceFixtures.Bytes(folder);
        var observer = new RecordingObserver();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.False(app.OpenWorkspace(folder));
        Assert.False(app.RefreshWorkspace());
        Assert.Contains("does not migrate", app.Status);
        Assert.DoesNotContain(observer.Effects, e => e.Kind is WorkspaceFileEffectKind.Write or
            WorkspaceFileEffectKind.Flush or WorkspaceFileEffectKind.Publish or WorkspaceFileEffectKind.Delete);
        var after = WorkspaceFixtures.Bytes(folder);
        Assert.Equal(originals.Keys.Order(), after.Keys.Order());
        foreach (var (relative, bytes) in originals) Assert.Equal(bytes, after[relative]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejected_note_publication_counts_repeated_transient_writes_even_after_cleanup(bool team)
    {
        var observer = new RecordingObserver();
        var publisher = new InspectingPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher, fileObserver: observer);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"41000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
            """).Id), app.Status);
        var id = team ? app.CreateTeamNote("avery@example.com", "Title", "Original")!.Id :
            app.CreatePersonNote("avery@example.com", "Title", "Original")!.Id;
        observer.Effects.Clear();
        var stagedPaths = new List<string>();
        var destinationPath = Path.Combine(folder, team ? "team-notes" : "notes", id + ".md");
        long stagedBytes = 0;
        publisher.BeforePublish = (staged, destination) =>
        {
            if (destination != destinationPath) return;
            Assert.StartsWith(Path.Combine(folder, "transactions") + Path.DirectorySeparatorChar, staged);
            Assert.DoesNotContain(staged, stagedPaths);
            stagedPaths.Add(staged);
            stagedBytes += File.ReadAllBytes(staged).LongLength;
            var inventoryPath = Path.Combine(Path.GetDirectoryName(staged)!, "publication.stage.json");
            using var inventory = JsonDocument.Parse(File.ReadAllBytes(inventoryPath));
            var entry = Assert.Single(inventory.RootElement.GetProperty("objects").EnumerateArray());
            Assert.Equal(Path.GetFileName(staged), entry.GetProperty("path").GetString());
            Assert.Equal(new FileInfo(staged).Length, entry.GetProperty("bytes").GetInt64());
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(staged))),
                entry.GetProperty("digest").GetString());
            var effects = observer.Effects.ToArray();
            var inventoryFlush = Array.FindIndex(effects, effect => effect.Kind == WorkspaceFileEffectKind.Flush && effect.Path == inventoryPath);
            var stagedWrite = Array.FindIndex(effects, effect => effect.Kind == WorkspaceFileEffectKind.Write && effect.Path == staged);
            Assert.True(inventoryFlush >= 0 && inventoryFlush < stagedWrite);
            throw new WorkspaceFilePublicationException("Controlled publication failure after the real write and flush.", true);
        };
        Assert.False(Edit());
        Assert.False(File.Exists(Assert.Single(stagedPaths)));
        Assert.False(Edit());
        Assert.False(app.CanSafelyClose);
        Assert.Contains("Controlled publication failure", app.LocalSaveError);
        Assert.Equal(2, stagedPaths.Count);
        Assert.All(stagedPaths, path => Assert.False(File.Exists(path)));
        var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        var noteWrites = writes.Where(e => stagedPaths.Contains(e.Path)).ToArray();
        Assert.Equal(2, noteWrites.Length);
        Assert.True(writes.Length > noteWrites.Length);
        Assert.Equal(stagedBytes, noteWrites.Sum(e => e.Bytes));
        Assert.Equal(writes.Length, observer.Effects.Count(e => e.Kind == WorkspaceFileEffectKind.Flush));
        Assert.Equal(2, observer.Effects.Count(e => e.Kind == WorkspaceFileEffectKind.Delete && stagedPaths.Contains(e.Path)));
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish && stagedPaths.Contains(e.Path));
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Original", team ? Assert.Single(reopened.GetTeamNotes("avery@example.com")).Markdown :
                Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
        }
        publisher.BeforePublish = null;
        Assert.True(Edit(), app.Status);
        Assert.True(app.CanSafelyClose);
        Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish &&
            e.Destination == destinationPath && !stagedPaths.Contains(e.Path));

        bool Edit() => team ? app.EditTeamNote(id, "Title", "Retained draft") :
            app.EditPersonNote(id, "Title", "Retained draft");
    }

    private sealed class RecordingObserver : IWorkspaceFileObserver
    {
        public ConcurrentQueue<WorkspaceFileEffect> Effects { get; } = new();
        public ConcurrentQueue<WorkspaceStoragePhase> Phases { get; } = new();
        public void Observe(WorkspaceFileEffect effect) => Effects.Enqueue(effect);
        public void ObservePhase(WorkspaceStoragePhase phase) => Phases.Enqueue(phase);
    }

    private sealed class InspectingPublisher : IWorkspaceFilePublisher
    {
        public Action<string, string>? BeforePublish { get; set; }
        public void Publish(string staged, string destination, string? backup, WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
        {
            BeforePublish?.Invoke(staged, destination);
            new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
