using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrgExplorer.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace OrgExplorer.Persistence.Tests;

public sealed class SearchFavoritePersistenceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private readonly List<IApplicationWorkflow> workflows = [];

    private IApplicationWorkflow Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        workflows.Add(app);
        Assert.True(app.OpenWorkspace(folder), app.Status);
        return app;
    }

    private static void Apply(IApplicationWorkflow app, string records, int id)
    {
        var preview = app.PreviewImport($$"""
            {"schemaVersion":1,"batchId":"20000000-0013-4000-8000-{{id:000000000000}}",
             "observedAt":"2026-11-06T17:00:00Z","people":[{{records}}]}
            """);
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    private IApplicationWorkflow Seed()
    {
        var app = Open();
        Apply(app, """
            {"email":"avery@example.com","displayName":"Avery Directory","preferredName":"Ave Preferred","alias":"avhandle",
             "jobTitle":"Scientist","department":"Research","officeBuilding":"Juniper","officeNumber":"007B",
             "officeState":"Ontario","officeCountry":"Canada","linkedInProfileUrl":"https://www.linkedin.com/in/fictitious-avery",
             "managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null,"isFavorite":true}
            """, 1);
        return app;
    }

    [Fact]
    public void Reopening_rebuilds_local_queries_favorites_and_attributed_current_archived_and_frozen_context()
    {
        var app = Seed();
        var direct = app.CreatePersonNote("avery@example.com", "Meeting",
            new string('a', 240) + "\nOrchid private Person writing\n" + new string('z', 240))!;
        var team = app.CreateTeamNote("morgan@example.com", "Former team", "Cedar former Team writing")!;
        app.CreateTeamNote("alex@example.com", "Current team", "Pine current Team writing");
        Assert.True(app.EditFavorite("avery@example.com", true));
        Apply(app, """{"email":"avery@example.com","officeCountry":"Canada"}""", 2);
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(app.EditTeamNote(team.Id, "Later title", "Willow later writing"));
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        app.Dispose();

        var reopened = Open();
        Assert.Equal(new[] { "alex@example.com", "avery@example.com" }, reopened.FavoritePeople.Select(p => p.Email).Order());
        foreach (var query in new[] { "Directory", "Preferred", "avery@example", "avhandle", "Scientist", "Research",
            "Juniper", "007B", "Ontario", "Canada", "fictitious-avery", "alex@example" })
            Assert.Contains(reopened.Search(query), r => r.Person.Email == "avery@example.com" &&
                r.Source == SearchSource.Profile && r.Context == SearchContext.Archived);
        var directMatch = Assert.Single(reopened.Search("orchid"));
        Assert.Equal(direct.Id, directMatch.NoteId);
        Assert.True(directMatch.Person.Archived);
        Assert.Equal(SearchSource.PersonNote, directMatch.Source);
        Assert.Contains("Orchid private Person writing", directMatch.Excerpt);
        Assert.True(directMatch.Excerpt.Length < 200);
        Assert.Equal(new string('a', 240) + "\nOrchid private Person writing\n" + new string('z', 240),
            reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == direct.Id).Markdown);
        var frozen = Assert.Single(reopened.Search("cedar"));
        Assert.Equal(capture.Id, frozen.HistoricalContextId);
        Assert.Equal(team.Id, frozen.NoteId);
        Assert.Equal("morgan@example.com", frozen.NoteOwner!.Email);
        Assert.Equal("Cedar former Team writing", frozen.MatchText);
        Assert.Contains(reopened.Search("pine"), r => r.Person.Email == "avery@example.com" && r.Context == SearchContext.Historical);
        Assert.DoesNotContain(reopened.Search("willow"), r => r.Person.Email == "avery@example.com");
        Assert.Empty(reopened.GetInheritedTeamNotes("avery@example.com"));
        Assert.True(reopened.EditPersonArchived("avery@example.com", false));
        Assert.Contains(reopened.Search("pine"), r => r.Source == SearchSource.InheritedTeamNote && r.Context == SearchContext.Current);
        Apply(reopened, """{"email":"avery@example.com","isFavorite":false}""", 3);
        Assert.Equal("alex@example.com", Assert.Single(Open().FavoritePeople).Email);
        Assert.True(reopened.EditFavorite("alex@example.com", false));
        Assert.Empty(Open().FavoritePeople);
    }

    [Fact]
    public void Validated_external_delivery_rebuilds_queries_and_favorites_without_rewriting_provider_files()
    {
        var writer = Seed();
        var personNote = writer.CreatePersonNote("avery@example.com", "Individual", "Original orchid")!;
        var teamNote = writer.CreateTeamNote("morgan@example.com", "Team", "Original cedar")!;
        var reader = Open();
        var notifications = new ConcurrentQueue<string>();
        IReadOnlyList<SearchResult> lastTeamMatches = [];
        DateTimeOffset? lastTeamSearchAt = null;
        void RecordRefresh(object? sender, EventArgs args)
        {
            notifications.Enqueue($"{DateTimeOffset.UtcNow:O}: current={reader.IsWorkspaceCurrent}; {reader.Status}");
            while (notifications.Count > 64) notifications.TryDequeue(out _);
        }
        IReadOnlyList<SearchResult> ObserveTeamSearch()
        {
            lastTeamMatches = reader.Search("revised cedar");
            lastTeamSearchAt = DateTimeOffset.UtcNow;
            return lastTeamMatches;
        }
        void RequireAutomaticDelivery(string phase, Func<bool> condition)
        {
            var elapsed = Stopwatch.StartNew();
            var delivered = SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10));
            var verdictAt = DateTimeOffset.UtcNow;
            var duration = elapsed.Elapsed;
            var status = reader.Status;
            if (!delivered)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Phase = phase, VerdictAt = verdictAt, Elapsed = duration, StatusAtVerdict = status,
                    Runtime = Environment.Version.ToString(), LastTeamSearchAt = lastTeamSearchAt,
                    LastTeamMatches = lastTeamMatches, ReaderNotifications = notifications.ToArray()
                }));
                WriteDeliveryFailure(writer, reader);
            }
            Assert.True(delivered, status);
        }
        reader.WorkspaceChanged += RecordRefresh;
        try
        {
            Assert.Contains(reader.Search("cedar"), r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.InheritedTeamNote);
            Assert.True(writer.EditFavorite("avery@example.com", true), writer.Status);
            Apply(writer, """{"email":"alex@example.com","isFavorite":false}""", 2);
            Assert.True(writer.EditPersonText("avery@example.com", PersonTextField.OfficeNumber, "0009C"), writer.Status);
            Assert.True(writer.EditPersonNote(personNote.Id, "Revised", "Revised orchid"), writer.Status);
            Assert.True(writer.EditTeamNote(teamNote.Id, "Revised Team", "Revised cedar"), writer.Status);
            RequireAutomaticDelivery("revised current writing", () => reader.FavoritePeople.Count == 1 && reader.FavoritePeople[0].Email == "avery@example.com" &&
                reader.Search("0009C").Count > 0 && reader.Search("revised orchid").Count == 1 &&
                ObserveTeamSearch().Count == 2);
            Assert.Empty(reader.Search("original orchid"));
            Assert.DoesNotContain(reader.Search("007B"), r => r.Context == SearchContext.Current);
            Assert.Contains(reader.Search("007B"), r => r.Source == SearchSource.ProfileHistory);
            Assert.True(writer.EditPersonManager("avery@example.com", "alex@example.com"));
            RequireAutomaticDelivery("Historical team context", () => ObserveTeamSearch().Any(r => r.Source == SearchSource.HistoricalTeamNote));
            Assert.DoesNotContain(reader.Search("cedar"), r => r.Source == SearchSource.InheritedTeamNote);
            var bytes = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".workspace.lock"))
                .ToDictionary(p => p, File.ReadAllBytes);
            Assert.True(reader.RefreshWorkspace());
            for (var i = 0; i < 3; i++) Assert.Contains(reader.Search("cedar"), r => r.Source == SearchSource.HistoricalTeamNote);
            Assert.Single(reader.GetHistoricalTeamContexts("avery@example.com"));
            foreach (var (path, original) in bytes) Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { reader.WorkspaceChanged -= RecordRefresh; }
    }

    private void WriteDeliveryFailure(IApplicationWorkflow writer, IApplicationWorkflow reader)
    {
        // These observations follow the failed verdict; neither reads nor file copies form an atomic snapshot.
        foreach (var (name, app) in new[] { ("writer", writer), ("reader", reader) })
        {
            try
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Observation = name, StartedAt = DateTimeOffset.UtcNow, app.Status, app.IsWorkspaceCurrent,
                    Relationships = app.GetRelationships("avery@example.com"),
                    History = app.GetHistoricalTeamContexts("avery@example.com"),
                    Matches = app.Search("revised cedar")
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                output.WriteLine($"{name} post-verdict observation failed: {ex}");
            }
        }
        try
        {
            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var path in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(p => Path.GetFileName(p) != ".workspace.lock").Order())
                {
                    var relative = Path.GetRelativePath(folder, path);
                    try
                    {
                        var startedAt = DateTimeOffset.UtcNow;
                        var content = File.ReadAllBytes(path);
                        using var entry = archive.CreateEntry(relative.Replace('\\', '/')).Open();
                        entry.Write(content);
                        output.WriteLine($"Captured {relative}: read started {startedAt:O}; {content.Length} bytes; " +
                            $"SHA256={Convert.ToHexString(SHA256.HashData(content))}");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        output.WriteLine($"Post-verdict file capture failed for {relative}: {ex}");
                    }
                }
            }
            output.WriteLine("Non-atomic post-verdict Workspace ZIP (base64; excludes coordination lock): " +
                Convert.ToBase64String(bytes.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            output.WriteLine($"Post-verdict Workspace capture failed: {ex}");
        }
    }

    [Fact]
    public void Failed_favorite_save_retains_saved_queries_and_safe_close_until_retry_and_legacy_omission_defaults_to_false()
    {
        var app = Seed();
        using (new FileStream(Path.Combine(folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.EditFavorite("avery@example.com", true));
            Assert.False(app.CanSafelyClose);
            Assert.Contains("NOT saved", app.LocalSaveError);
            Assert.Equal("alex@example.com", Assert.Single(app.FavoritePeople).Email);
            Assert.Equal("alex@example.com", Assert.Single(Open().FavoritePeople).Email);
            Assert.Empty(app.GetProfileTimeline("avery@example.com")!.Changes);
        }
        Assert.True(app.EditFavorite("avery@example.com", true), app.Status);
        Assert.True(app.CanSafelyClose, app.Status);
        Assert.Equal(2, Open().FavoritePeople.Count);
        Assert.True(app.EditFavorite("avery@example.com", false), app.Status);
        app.Dispose();
        var personFile = CurrentPersonFile("morgan@example.com");
        var legacy = JsonNode.Parse(File.ReadAllText(personFile))!;
        legacy.AsObject().Remove("isFavorite");
        File.WriteAllText(personFile, legacy.ToJsonString());
        Assert.False(Open().GetRelationships("morgan@example.com")!.Person.IsFavorite);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"false\"")]
    [InlineData("false")]
    public void Invalid_external_favorite_or_inconsistent_evidence_retains_last_valid_search_and_favorites(string invalid)
    {
        var app = Seed();
        var file = CurrentPersonFile("alex@example.com");
        var original = File.ReadAllText(file);
        var document = JsonNode.Parse(original)!;
        document["isFavorite"] = JsonNode.Parse(invalid);
        File.WriteAllText(file, document.ToJsonString());
        Assert.False(app.RefreshWorkspace());
        Assert.False(app.IsWorkspaceCurrent);
        Assert.Contains("STALE", app.Status);
        Assert.Equal("alex@example.com", Assert.Single(app.FavoritePeople).Email);
        Assert.Contains(app.Search("alex"), r => r.Person.IsFavorite);
        Assert.All(app.Search("alex"), r => Assert.StartsWith("STALE retained", r.Label));
        Assert.False(app.EditFavorite("alex@example.com", false));
        using var blocked = ApplicationWorkflow.CreateFileBacked();
        Assert.False(blocked.OpenWorkspace(folder));
        File.WriteAllText(file, original);
        Assert.True(app.RefreshWorkspace(), app.Status);
        app.DiscardUnsavedChanges();
        Assert.True(app.CanSafelyClose);
        Assert.Equal("alex@example.com", Assert.Single(Open().FavoritePeople).Email);
    }

    private string CurrentPersonFile(string email)
    {
        return OrgExplorer.TestFixtures.WorkspaceFixtures.IndexedDocuments(folder)["people:" + email];
    }

    public void Dispose()
    {
        foreach (var app in workflows) app.Dispose();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
