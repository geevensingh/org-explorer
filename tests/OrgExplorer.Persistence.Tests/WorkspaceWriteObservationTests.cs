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
        publisher.BeforePublish = (staged, _) =>
        {
            var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
            Assert.NotEmpty(writes);
            foreach (var write in writes)
            {
                Assert.Equal(new FileInfo(write.Path).Length, write.Bytes);
                Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Flush && e.Path == write.Path);
            }
            Assert.Contains(writes, e => e.Path == staged);
            using var pointer = JsonDocument.Parse(File.ReadAllBytes(staged));
            var generationFiles = Directory.GetFiles(Path.Combine(folder, "generations",
                pointer.RootElement.GetProperty("generation").GetString()!), "*", SearchOption.AllDirectories);
            Assert.Equal(generationFiles.Append(staged).Order(), writes.Select(e => e.Path).Order());
            Assert.Equal(generationFiles.Sum(path => new FileInfo(path).Length) + new FileInfo(staged).Length,
                writes.Sum(e => e.Bytes));
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
    public void Full_copy_then_delete_cannot_hide_rejected_generation_write_activity()
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
        var copied = new List<(string Path, long Bytes)>();
        publisher.BeforePublish = (staged, _) =>
        {
            using var pointer = JsonDocument.Parse(File.ReadAllBytes(staged));
            var generation = Path.Combine(folder, "generations", pointer.RootElement.GetProperty("generation").GetString()!);
            copied.AddRange(Directory.GetFiles(generation, "*", SearchOption.AllDirectories)
                .Append(staged).Select(path => (path, new FileInfo(path).Length)));
            Directory.Delete(generation, true);
            File.Delete(staged);
            throw new IOException("Controlled failure after deleting all staged payloads.");
        };
        Assert.False(app.EditPreferredName("avery@example.com", "Not committed"));
        Assert.False(app.EditPreferredName("avery@example.com", "Still not committed"));
        Assert.False(app.CanSafelyClose);
        Assert.Equal(before.Keys.Order(), WorkspaceFixtures.Bytes(folder).Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, path)));
        var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        Assert.Equal(copied.OrderBy(e => e.Path).ToArray(),
            writes.Select(e => (e.Path, e.Bytes)).OrderBy(e => e.Path).ToArray());
        Assert.True(writes.Sum(e => e.Bytes) > 0);
        Assert.All(writes, e => Assert.False(File.Exists(e.Path)));
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish);
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Null(reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal("Morgan", reopened.GetRelationships("morgan@example.com")!.Person.DirectoryName);
    }

    [Fact]
    public void Existing_conversion_observes_independent_recovery_copies_and_repeated_overwrites()
    {
        WorkspaceFixtures.CopyVersionOne(folder);
        var originals = WorkspaceFixtures.Bytes(folder);
        var observer = new RecordingObserver();
        using var app = ApplicationWorkflow.CreateFileBacked(fileObserver: observer);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        var recovery = app.Compatibility.RecoveryPath!;
        Assert.NotNull(recovery);
        var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        foreach (var (relative, bytes) in originals)
        {
            var path = Path.Combine(recovery, relative);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(bytes.LongLength, Assert.Single(writes, e => e.Path == path).Bytes);
        }
        Assert.Contains(writes.GroupBy(e => e.Path), group => group.Count() > 1);
        Assert.True(writes.Sum(e => e.Bytes) >
            writes.Select(e => e.Path).Distinct().Where(File.Exists).Sum(path => new FileInfo(path).Length));
        Assert.Equal(writes.Length, observer.Effects.Count(e => e.Kind == WorkspaceFileEffectKind.Flush));
        Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish &&
            e.Destination == Path.Combine(folder, "workspace.json"));
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
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
        var stagedPath = Path.Combine(folder, team ? "team-notes" : "notes", id + ".next");
        long stagedBytes = 0;
        publisher.BeforePublish = (staged, _) =>
        {
            Assert.Equal(stagedPath, staged);
            stagedBytes += File.ReadAllBytes(staged).LongLength;
            throw new IOException("Controlled publication failure after the real write and flush.");
        };
        Assert.False(Edit());
        Assert.False(File.Exists(stagedPath));
        Assert.False(Edit());
        Assert.False(app.CanSafelyClose);
        Assert.Contains("Controlled publication failure", app.LocalSaveError);
        Assert.False(File.Exists(stagedPath));
        var writes = observer.Effects.Where(e => e.Kind == WorkspaceFileEffectKind.Write).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.All(writes, e => Assert.Equal(stagedPath, e.Path));
        Assert.Equal(stagedBytes, writes.Sum(e => e.Bytes));
        Assert.Equal(2, observer.Effects.Count(e => e.Kind == WorkspaceFileEffectKind.Flush));
        Assert.Equal(2, observer.Effects.Count(e => e.Kind == WorkspaceFileEffectKind.Delete && e.Path == stagedPath));
        Assert.DoesNotContain(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish);
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
        {
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Original", team ? Assert.Single(reopened.GetTeamNotes("avery@example.com")).Markdown :
                Assert.Single(reopened.GetPersonNotes("avery@example.com")).Markdown);
        }
        publisher.BeforePublish = null;
        Assert.True(Edit(), app.Status);
        Assert.True(app.CanSafelyClose);
        Assert.Contains(observer.Effects, e => e.Kind == WorkspaceFileEffectKind.Publish && e.Path == stagedPath);

        bool Edit() => team ? app.EditTeamNote(id, "Title", "Retained draft") :
            app.EditPersonNote(id, "Title", "Retained draft");
    }

    private sealed class RecordingObserver : IWorkspaceFileObserver
    {
        public ConcurrentQueue<WorkspaceFileEffect> Effects { get; } = new();
        public void Observe(WorkspaceFileEffect effect) => Effects.Enqueue(effect);
    }

    private sealed class InspectingPublisher : IWorkspaceFilePublisher
    {
        public Action<string, string>? BeforePublish { get; set; }
        public void Publish(string staged, string destination, string? backup)
        {
            BeforePublish?.Invoke(staged, destination);
            if (File.Exists(destination)) File.Replace(staged, destination, backup);
            else File.Move(staged, destination);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
