using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Persistence.Tests;

[Trait("Category", "Performance")]
public sealed class ScaleWorkflowTests(ITestOutputHelper output)
{
    [Fact]
    public void Ten_thousand_People_keep_complete_durable_current_and_frozen_context_through_mutations_and_delivery()
    {
        using var fixture = new ScaleFixture { Log = Log };
        // Timings are evidence, not an agreed application-latency SLA.
        {
            fixture.Build();
            var app = Measure("load", () => fixture.Open());
            AssertPopulation(app);
            Measure("navigation-100", () =>
            {
                for (var i = 0; i < 100; i++) Assert.NotNull(app.GetRelationships(ScaleFixture.Email(1000 + i))!.Manager);
            });
            Measure("profile-100", () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    Assert.Single(app.GetPersonNotes(ScaleFixture.Email(1000 + i)));
                    Assert.NotEmpty(app.GetInheritedTeamNotes(ScaleFixture.Email(1000 + i)));
                    Assert.Single(app.GetHistoricalTeamContexts(ScaleFixture.Email(1000 + i)));
                }
            });
            var frozen = app.GetHistoricalTeamContexts(ScaleFixture.Target)[0];
            Assert.Equal(new[] { ScaleFixture.Email(100), ScaleFixture.Email(10), ScaleFixture.Email(0) },
                frozen.ReportingChain.Select(p => p.Email));
            Assert.Contains(frozen.TeamNotes, n => n.Markdown.Contains("Cedar former context 100."));
            var transition = Assert.Single(frozen.Transitions);
            Assert.Equal(ScaleFixture.Target, transition.PersonEmail);
            Assert.Equal(ProfileChangeSource.Import, transition.Change.Evidence.Source);
            Assert.Equal("15000000-0000-4000-8000-000000000002", transition.Change.Evidence.BatchId.ToString());
            Assert.Equal(new DateTimeOffset(2026, 11, 2, 17, 0, 0, TimeSpan.Zero), transition.Change.Evidence.ObservedAt);
            Assert.Empty(app.GetHistoricalTeamContexts(ScaleFixture.Email(0)));
            Assert.Equal(42, ChainLength(app, ScaleFixture.DeepTarget));
            var current = Measure("search-current", () => app.Search("Pine current context 100."));
            Assert.Contains(current, r => r.Context == SearchContext.Current && r.Source == SearchSource.InheritedTeamNote);
            var history = Measure("search-history", () => app.Search("Cedar former context 100."));
            Assert.Contains(history, r => r.Person.Email == ScaleFixture.Target && r.Source == SearchSource.HistoricalTeamNote &&
                r.Context == SearchContext.Historical && r.HistoricalContextId == frozen.Id &&
                r.NoteOwner!.Email == ScaleFixture.Email(100));
            Assert.Contains(app.Search("person09999@example.com"), r => r.Person.Archived);

            Measure("favorite-save", () => Assert.True(app.EditFavorite(ScaleFixture.Target, false), app.Status));
            var note = app.GetPersonNotes(ScaleFixture.Target)[0];
            Measure("note-save", () => Assert.True(app.EditPersonNote(note.Id, note.Title, "# Revised orchid private writing."), app.Status));
            Assert.Contains(app.Search("Revised orchid"), r => r.NoteId == note.Id);
            Assert.DoesNotContain(app.Search("Orchid private writing 1000."), r => r.NoteId == note.Id);
            Measure("profile-save", () =>
                Assert.True(app.EditPersonText(ScaleFixture.Target, PersonTextField.OfficeNumber, "0009S"), app.Status));
            var import = ScaleFixture.Batch(Enumerable.Range(0, ScaleFixture.Population).Select(i =>
            {
                var patch = new Dictionary<string, object?> { ["email"] = ScaleFixture.Email(i) };
                if (i == 1000) patch["managerEmail"] = ScaleFixture.Email(12);
                if (i == 9999) patch["officeNumber"] = "end-9999B";
                return patch;
            }), 4);
            var preview = Measure("validated-preview", () => app.PreviewImport(import));
            Assert.True(preview.CanApply, app.Status);
            Assert.Contains(preview.Changes, change => change.Contains("end-9999B"));
            Measure("validated-apply", () => Assert.True(app.ApplyImport(preview.Id), app.Status));
            var captures = app.GetHistoricalTeamContexts(ScaleFixture.Target);
            Assert.Equal(2, captures.Count);
            Assert.Equal(frozen.Id, captures[0].Id);
            Assert.Equal("Cedar former context 100.", captures[0].TeamNotes.Single(n => n.OwnerEmail == ScaleFixture.Email(100)).Markdown.Split('\n')[1]);
            var officeChange = app.GetProfileTimeline(ScaleFixture.Target)!.Changes.Last(c => c.Field == "officeNumber");
            Assert.Equal("new-1000B", officeChange.OldValue.GetString());
            Assert.Equal("0009S", officeChange.NewValue.GetString());
            Assert.Equal(ProfileChangeSource.LocalEdit, officeChange.Evidence.Source);
            Assert.Null(officeChange.Evidence.ObservedAt);
            var retained = CaptureRetainedData(app);
            app.Dispose();
            var reopened = Measure("reopen", () => fixture.Open());
            Measure("reopened-projections", () => AssertPopulation(reopened, 201));
            Assert.Equal("0009S", reopened.GetRelationships(ScaleFixture.Target)!.Person.OfficeNumber);
            Assert.Equal(ScaleFixture.Email(12), reopened.GetRelationships(ScaleFixture.Target)!.Manager!.Email);
            Assert.Single(reopened.GetPersonNotes(ScaleFixture.Target));
            Assert.Equal(2, reopened.GetHistoricalTeamContexts(ScaleFixture.Target).Count);
            Assert.Equal(retained, CaptureRetainedData(reopened));
            Assert.Equal("end-9999B", reopened.GetRelationships(ScaleFixture.Email(9999))!.Person.OfficeNumber);
            Assert.True(reopened.GetRelationships(ScaleFixture.Email(9999))!.Person.Archived);
            Measure("latest-replay", () => Assert.True(reopened.ApplyImport(reopened.PreviewImport(import).Id), reopened.Status));
            Assert.Equal(2, reopened.GetHistoricalTeamContexts(ScaleFixture.Target).Count);
            Assert.Equal(retained, CaptureRetainedData(reopened));
            var initialReplay = Measure("initial-replay-preview", () =>
                reopened.PreviewImportFile(Path.Combine(fixture.Producer, "initial.json")));
            Assert.True(initialReplay.CanApply, reopened.Status);
            Assert.Contains("no-op", Assert.Single(initialReplay.Changes));
            Measure("initial-replay-apply", () => Assert.True(reopened.ApplyImport(initialReplay.Id), reopened.Status));
            Assert.Equal("0009S", reopened.GetRelationships(ScaleFixture.Target)!.Person.OfficeNumber);
            Assert.False(reopened.GetRelationships(ScaleFixture.Target)!.Person.IsFavorite);
            Assert.Equal(retained, CaptureRetainedData(reopened));
            Directory.Delete(fixture.Producer, true);
            Assert.Equal(128, reopened.GetPhoto(ScaleFixture.Target)!.Width);
            Measure("unchanged-refresh", () => Assert.True(reopened.RefreshWorkspace(), reopened.Status));
            var deliveryNote = reopened.GetPersonNotes(ScaleFixture.Target)[0];
            var path = Path.Combine(fixture.Workspace, "notes", deliveryNote.Id + ".md");
            File.WriteAllText(path, File.ReadAllText(path).Replace("Revised orchid", "Delivered tulip"));
            Measure("validated-delivery", () => Assert.True(reopened.RefreshWorkspace(), reopened.Status));
            Assert.Contains(reopened.Search("Delivered tulip"), r => r.NoteId == deliveryNote.Id);
            Assert.Empty(reopened.Search("Revised orchid"));
            var delivered = File.ReadAllText(path);
            var deliveredRetained = CaptureRetainedData(reopened);
            File.WriteAllText(path, "incomplete");
            Assert.False(reopened.RefreshWorkspace());
            Assert.False(reopened.IsWorkspaceCurrent);
            Assert.Contains(reopened.Search("Delivered tulip"), r => !r.IsWorkspaceCurrent);
            Assert.False(reopened.EditFavorite(ScaleFixture.Target, true));
            Assert.Equal(10000, reopened.People.Count);
            Assert.Equal(2, reopened.GetHistoricalTeamContexts(ScaleFixture.Target).Count);
            reopened.Dispose();
            File.WriteAllText(path, delivered);
            var recovered = Measure("reopen-after-delivery", () => fixture.Open());
            Assert.True(recovered.IsWorkspaceCurrent, recovered.Status);
            AssertPopulation(recovered, 201);
            Assert.Equal(deliveredRetained, CaptureRetainedData(recovered));
        }
    }

    private void Log(string message)
    {
        output.WriteLine(message);
        if (Environment.GetEnvironmentVariable("SCALE_TIMING_FILE") is { Length: > 0 } path)
            File.AppendAllText(path, message + Environment.NewLine);
    }
    private T Measure<T>(string operation, Func<T> action) => ScaleFixture.Measure(operation, action, Log);
    private void Measure(string operation, Action action) => ScaleFixture.Measure(operation, action, Log);

    private static void AssertPopulation(IApplicationWorkflow app, int historicalCount = 200)
    {
        Assert.Equal(10000, app.People.Count);
        Assert.Equal(100, app.ArchivedPeople.Count);
        Assert.Equal(9900, Count(app.ReportingRoots));
        Assert.Equal(128, app.GetPhoto(ScaleFixture.Target)!.Width);
        Assert.Equal(2500, app.People.Count(p => p.PhotoPath is not null));
        Assert.Equal(1000, app.People.Sum(p => app.GetPersonNotes(p.Email, true).Count));
        Assert.Equal(111, app.People.Sum(p => app.GetTeamNotes(p.Email, true).Count));
        Assert.Equal(historicalCount, app.People.Sum(p => app.GetHistoricalTeamContexts(p.Email).Count));
    }

    private static IReadOnlyList<string> CaptureRetainedData(IApplicationWorkflow app) =>
        app.People.OrderBy(p => p.Email, StringComparer.Ordinal).Select(person => JsonSerializer.Serialize(new
        {
            person,
            notes = app.GetPersonNotes(person.Email, true),
            teamNotes = app.GetTeamNotes(person.Email, true),
            history = app.GetHistoricalTeamContexts(person.Email),
            timeline = app.GetProfileTimeline(person.Email),
            photo = app.GetPhoto(person.Email) is { } photo ? Convert.ToHexString(SHA256.HashData(photo.Bytes.Span)) : null
        })).ToArray();

    private static int Count(IReadOnlyList<ReportingNode> roots)
    {
        var stack = new Stack<ReportingNode>(roots);
        var count = 0;
        while (stack.TryPop(out var node)) { count++; foreach (var report in node.Reports) stack.Push(report); }
        return count;
    }
    private static int ChainLength(IApplicationWorkflow app, string email)
    {
        var count = 0;
        while (app.GetRelationships(email)!.Manager is { } manager) { count++; email = manager.Email; }
        return count;
    }
}
