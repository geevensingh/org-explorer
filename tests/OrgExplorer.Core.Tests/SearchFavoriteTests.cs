using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class SearchFavoriteTests
{
    private static string Batch(string records, int id) => $$"""
        {"schemaVersion":1,"batchId":"10000000-0013-4000-8000-{{id:000000000000}}",
         "observedAt":"2026-11-06T17:00:00Z","people":[{{records}}]}
        """;

    private static void Apply(IApplicationWorkflow app, string records, int id)
    {
        var preview = app.PreviewImport(Batch(records, id));
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    private static IApplicationWorkflow Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious-search"));
        Apply(app, """
            {"email":"avery@example.com","displayName":"Avery Directory","preferredName":"Ave Preferred",
             "alias":"avhandle","jobTitle":"Scientist","department":"Research","officeBuilding":"Juniper",
             "officeNumber":"007B","officeState":"Ontario","officeCountry":"Canada",
             "linkedInProfileUrl":"https://www.linkedin.com/in/fictitious-avery","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
            """, 1);
        return app;
    }

    [Theory]
    [InlineData(" directory ", "displayName")]
    [InlineData("PREFERRED", "preferredName")]
    [InlineData("AVERY@EXAMPLE", "email")]
    [InlineData("avhandle", "alias")]
    [InlineData("scientist", "jobTitle")]
    [InlineData("research", "department")]
    [InlineData("juniper", "officeBuilding")]
    [InlineData("007b", "officeNumber")]
    [InlineData("ontario", "officeState")]
    [InlineData("canada", "officeCountry")]
    [InlineData("fictitious-avery", "linkedInProfileUrl")]
    [InlineData("morgan@example", "managerEmail")]
    public void Search_finds_each_stored_attribute_without_losing_identity_or_current_attribution(string query, string field)
    {
        using var app = Open();
        var match = Assert.Single(app.Search(query), r => r.Person.Email == "avery@example.com" && r.Field == field);
        Assert.Equal(SearchContext.Current, match.Context);
        Assert.Equal(SearchSource.Profile, match.Source);
        Assert.Null(match.HistoricalContextId);
        Assert.Empty(app.Search(" \t"));
        Assert.Empty(app.Search("not-present-anywhere"));
    }

    [Fact]
    public void Person_and_Team_writing_is_searchable_with_explicit_ownership_inheritance_and_note_archival()
    {
        using var app = Open();
        var direct = app.CreatePersonNote("morgan@example.com", "Cedar direct", "Cedar private Person context")!;
        var team = app.CreateTeamNote("morgan@example.com", "Cedar team", "# Cedar formal subtree")!;
        var own = app.CreateTeamNote("avery@example.com", "Own", "Cedar owned writing")!;
        var results = app.Search("cedar");
        Assert.Contains(results, r => r.Source == SearchSource.PersonNote && r.Person.Email == "morgan@example.com" &&
            r.NoteId == direct.Id && r.NoteOwner!.Email == "morgan@example.com");
        Assert.DoesNotContain(results, r => r.Source == SearchSource.PersonNote && r.Person.Email == "avery@example.com");
        var inherited = Assert.Single(results, r => r.Source == SearchSource.InheritedTeamNote);
        Assert.Equal("avery@example.com", inherited.Person.Email);
        Assert.Equal("morgan@example.com", inherited.NoteOwner!.Email);
        Assert.Equal(team.Id, inherited.NoteId);
        Assert.Equal(SearchContext.Current, inherited.Context);
        Assert.Contains("Inherited Team note", inherited.Label);
        Assert.Contains("morgan@example.com", inherited.Label);
        Assert.Contains(results, r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.OwnedTeamNote && r.NoteId == own.Id);

        Assert.True(app.ArchiveTeamNote(team.Id));
        Assert.True(app.ArchivePersonNote(direct.Id));
        Assert.DoesNotContain(app.Search("cedar"), r => r.Source == SearchSource.InheritedTeamNote);
        var archived = Assert.Single(app.Search("cedar"), r => r.NoteId == team.Id);
        Assert.Equal(SearchContext.Archived, archived.Context);
        Assert.True(archived.NoteArchived);
        Assert.Contains("Archived note", archived.Label);
        Assert.Contains(app.Search("cedar"), r => r.NoteId == direct.Id && r.NoteArchived);
        Assert.True(app.EditTeamNote(own.Id, "Own", "Updated local text"));
        Assert.DoesNotContain(app.Search("cedar"), r => r.NoteId == own.Id);
        Assert.Contains(app.Search("updated local"), r => r.NoteId == own.Id);
    }

    [Fact]
    public void Historical_matches_target_the_frozen_capture_or_profile_change_not_later_current_writing()
    {
        using var app = Open();
        var note = app.CreateTeamNote("morgan@example.com", "Old cedar", "Cedar former team")!;
        app.CreateTeamNote("alex@example.com", "New context", "New pine team");
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(app.EditTeamNote(note.Id, "Later title", "Later willow team"));
        Assert.True(app.EditPreferredName("morgan@example.com", "Renamed manager"));
        var frozen = Assert.Single(app.Search("cedar"));
        Assert.Equal(SearchSource.HistoricalTeamNote, frozen.Source);
        Assert.Equal(SearchContext.Historical, frozen.Context);
        Assert.Equal(capture.Id, frozen.HistoricalContextId);
        Assert.Equal(capture.Id, frozen.HistoricalContext!.Id);
        Assert.Equal(note.Id, frozen.NoteId);
        Assert.Equal("Morgan", frozen.NoteOwner!.Heading);
        Assert.Equal("Cedar former team", frozen.MatchText);
        Assert.Contains("Historical", frozen.Label);
        Assert.DoesNotContain(app.Search("willow"), r => r.Person.Email == "avery@example.com");
        Assert.Contains(app.Search("pine"), r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.InheritedTeamNote);
        Assert.Contains(app.Search("morgan@example"), r => r.Person.Email == "avery@example.com" &&
            r.Source == SearchSource.FormerReportingChain && r.HistoricalContextId == capture.Id);

        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Cypress"));
        var oldOffice = Assert.Single(app.Search("juniper"));
        Assert.Equal(SearchSource.ProfileHistory, oldOffice.Source);
        Assert.Equal(SearchContext.Historical, oldOffice.Context);
        Assert.Equal("officeBuilding", oldOffice.ProfileChange!.Field);
        Assert.Equal("Juniper", oldOffice.ProfileChange.OldValue.GetString());
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        var archived = Assert.Single(app.Search("007B"), r => r.Source == SearchSource.Profile);
        Assert.Equal(SearchContext.Archived, archived.Context);
        Assert.Contains("Archived person", archived.Label);
        Assert.Empty(app.GetInheritedTeamNotes("avery@example.com"));
        Assert.Contains(app.Search("cedar"), r => r.Person.Archived && r.HistoricalContextId == capture.Id &&
            r.Label.Contains("Historical / Archived person"));
        Assert.Contains(app.Search("pine"), r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.HistoricalTeamNote);
    }

    [Fact]
    public void Favorites_are_local_or_explicit_import_state_and_omission_false_replay_and_archival_remain_distinct()
    {
        using var app = Open();
        Assert.Empty(app.FavoritePeople);
        Assert.All(app.People, p => Assert.False(p.IsFavorite));
        var notifications = new List<string?>();
        ((WorkspaceViewModel)app).PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        var preview = app.PreviewImport(Batch("""{"email":"avery@example.com","officeCountry":"France"}""", 2));
        Assert.True(app.EditFavorite("AVERY@example.com", true));
        Assert.Contains(nameof(app.FavoritePeople), notifications);
        Assert.Equal("avery@example.com", Assert.Single(app.FavoritePeople).Email);
        Assert.False(app.ApplyImport(preview.Id));
        var evidence = Assert.Single(app.GetProfileTimeline("avery@example.com")!.Confirmations, e => e.Field == "isFavorite");
        Assert.Equal(ProfileChangeSource.LocalEdit, evidence.Source);
        Assert.True(evidence.Value.GetBoolean());
        Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
        Apply(app, """{"email":"avery@example.com","officeNumber":"0009C"}""", 3);
        Apply(app, """{"email":"alex@example.com","isFavorite":true}""", 4);
        Assert.Equal(2, app.FavoritePeople.Count);
        Assert.Equal(evidence, Assert.Single(app.GetProfileTimeline("avery@example.com")!.Confirmations, e => e.Field == "isFavorite"));
        Apply(app, """{"email":"avery@example.com","isFavorite":false}""", 5);
        Assert.Equal("alex@example.com", Assert.Single(app.FavoritePeople).Email);
        Assert.True(app.EditFavorite("avery@example.com", true));
        Apply(app, """{"email":"avery@example.com","isFavorite":false}""", 5);
        Assert.Equal(2, app.FavoritePeople.Count);
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        Assert.Contains(app.FavoritePeople, p => p.Email == "avery@example.com" && p.Archived);
        Assert.True(app.EditFavorite("avery@example.com", false));
        Assert.Equal("alex@example.com", Assert.Single(app.FavoritePeople).Email);
        var count = app.GetProfileTimeline("avery@example.com")!.Changes.Count;
        Assert.True(app.EditFavorite("avery@example.com", false));
        Assert.Equal(count, app.GetProfileTimeline("avery@example.com")!.Changes.Count);
        var invalid = app.PreviewImport(Batch("""{"email":"alex@example.com","isFavorite":null}""", 6));
        Assert.False(invalid.CanApply);
        Assert.Contains("isFavorite", string.Join("\n", invalid.Errors));
        Assert.False(app.EditFavorite("unknown@example.com", true));
    }

    [Fact]
    public void Search_retains_all_ancestor_attribution_review_warnings_and_independently_frozen_note_versions()
    {
        using var app = Open();
        Apply(app, """
            {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
            {"email":"morgan@example.com","managerEmail":"casey@example.com"}
            """, 2);
        var managerNote = app.CreateTeamNote("morgan@example.com", "Aurora manager", "Aurora version one")!;
        var rootNote = app.CreateTeamNote("casey@example.com", "Aurora root", "Aurora ancestor")!;
        Assert.True(app.EditPersonText("morgan@example.com", PersonTextField.JobTitle, "Director"));
        var inherited = app.Search("aurora").Where(r => r.Person.Email == "avery@example.com" &&
            r.Source == SearchSource.InheritedTeamNote).ToArray();
        Assert.Equal(new[] { "morgan@example.com", "casey@example.com" }, inherited.Select(r => r.NoteOwner!.Email));
        Assert.True(inherited[0].NoteNeedsReview);
        Assert.Contains("Needs review", inherited[0].Label);
        Assert.False(inherited[1].NoteNeedsReview);
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var first = app.GetHistoricalTeamContexts("avery@example.com").Last();
        Assert.True(app.EditPersonManager("avery@example.com", "morgan@example.com"));
        Assert.True(app.EditTeamNote(managerNote.Id, "Aurora edited", "Aurora version two"));
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var second = app.GetHistoricalTeamContexts("avery@example.com").Last();
        var frozen = app.Search("aurora").Where(r => r.Person.Email == "avery@example.com" && r.NoteId == managerNote.Id).ToArray();
        Assert.Equal(2, frozen.Length);
        Assert.Contains(frozen, r => r.HistoricalContextId == first.Id && r.MatchText == "Aurora version one" && r.NoteNeedsReview);
        Assert.Contains(frozen, r => r.HistoricalContextId == second.Id && r.MatchText == "Aurora version two" && !r.NoteNeedsReview);
        Assert.Contains(app.Search("aurora"), r => r.HistoricalContextId == first.Id && r.NoteId == rootNote.Id);
    }
}
