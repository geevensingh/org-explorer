using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class SearchFavoriteTests
{
    [Fact]
    public void Accepted_Archived_person_reveals_retained_browsing_without_reactivation_and_empty_Enter_cannot_reuse_them()
    {
        using var app = (WorkspaceViewModel)Open();
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        Assert.True(app.NavigatePerson("alex@example.com"));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("007B");
        var result = app.PeopleNavigation.HighlightedResult!;

        Assert.True(app.NavigateSearchResult(result), app.Status);

        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.True(app.PeopleNavigation.ArchivedPeopleVisible);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.True(Assert.Single(app.ArchivedPeople).Archived);
        Assert.DoesNotContain(app.ReportingRoots.SelectMany(root => root.Reports), p => p.Person.Email == "avery@example.com");
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("unmatched-query");
        var navigation = app.PeopleNavigation;
        Assert.Null(navigation.HighlightedResult);
        Assert.False(app.NavigateSearchResult(result));
        Assert.Equal(navigation, app.PeopleNavigation);
    }

    [Fact]
    public void Accepted_current_Person_clears_search_and_expands_the_chosen_path_instead_of_restoring_the_old_selection()
    {
        using var app = (WorkspaceViewModel)Open();
        Assert.True(app.EditFavorite("alex@example.com", true));
        Assert.True(app.NavigatePerson("alex@example.com"));
        app.RememberPeopleBrowsing(["alex@example.com"], 12, 39, 576.26);
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("007B");

        Assert.True(app.NavigateSearchResult(app.PeopleNavigation.HighlightedResult!), app.Status);

        Assert.Equal("", app.PeopleNavigation.Query);
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(["alex@example.com", "morgan@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.False(app.PeopleNavigation.OverlayOpen);
        Assert.Contains(app.FavoritePeople, p => p.Email == "alex@example.com");
    }

    [Fact]
    public void Moving_the_highlight_clamps_at_both_ends_and_typing_resets_it_without_leaving_an_unsaved_profile()
    {
        using var app = (WorkspaceViewModel)Open();
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "  Exact unsaved writing\r\n"));
        var edit = app.ProfileEdit;
        app.ChangePeopleQuery("example.com");
        app.MoveSearchHighlight(-1);
        Assert.Equal("alex@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.MoveSearchHighlight(1);
        Assert.Equal("avery@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.MoveSearchHighlight(1);
        app.MoveSearchHighlight(1);
        Assert.Equal("morgan@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.ChangePeopleQuery("example.co");
        Assert.Equal("alex@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.ChangePeopleQuery("unmatched-query");
        app.MoveSearchHighlight(1);
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Same(edit, app.ProfileEdit);
        Assert.Null(app.PeopleNavigation.SelectedResult);
    }

    [Fact]
    public void Query_changes_highlight_the_first_Person_without_replacing_exact_reading_and_empty_results_clear_the_highlight()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Exact secondary", "Retained reading")!;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id));
        Assert.Null(app.PeopleNavigation.HighlightedResult);

        app.ChangePeopleQuery("example.com");
        Assert.Equal("alex@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        app.ChangePeopleQuery("Scientist");
        Assert.Equal("avery@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.ChangePeopleQuery("unmatched-query");
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.False(app.PeopleNavigation.IsBrowsing);
        app.ChangePeopleQuery("");
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
    }

    [Fact]
    public void People_search_groups_matching_fields_by_email_without_merging_equal_names_or_reranking()
    {
        using var app = Open();
        Apply(app, """
            {"email":"same-name@example.com","displayName":"Avery Directory","managerEmail":null}
            """, 2);

        var people = app.SearchPeople("avery");

        Assert.Equal(["avery@example.com", "same-name@example.com"], people.Select(r => r.Person.Email));
        Assert.Equal(["email", "displayName", "linkedInProfileUrl"],
            people[0].Matches.Select(r => r.Field));
        Assert.Equal(people[0].Person.DirectoryName, people[1].Person.DirectoryName);
        Assert.Contains("avery@example.com", people[0].Label);
        Assert.Contains("Current", people[0].SourceLabel);
        Assert.Contains("Directory name", people[0].SourceLabel);
        Assert.Empty(app.SearchPeople(" \t"));
    }

    [Fact]
    public void People_results_retain_every_source_and_its_current_archived_or_frozen_attribution()
    {
        using var app = Open();
        Assert.True(app.EditPreferredName("morgan@example.com", "Cedar manager"));
        var frozenNote = app.CreateTeamNote("morgan@example.com", "Original", "Cedar frozen writing")!;
        var secondary = app.CreatePersonNote("avery@example.com", "Cedar secondary", "Private writing")!;
        var archivedNote = app.CreatePersonNote("avery@example.com", "Retained", "Cedar archived writing")!;
        Assert.True(app.ArchivePersonNote(archivedNote.Id));
        app.CreateTeamNote("avery@example.com", "Owned", "Cedar owned writing");
        app.CreateTeamNote("alex@example.com", "Inherited", "Cedar current inherited writing");
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Cedar old office"));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Willow"));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.Department, "Cedar current department"));
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        Assert.True(app.EditTeamNote(frozenNote.Id, "Later", "Willow later writing"));
        Assert.True(app.EditPersonArchived("morgan@example.com", true));

        var people = app.SearchPeople("cedar");
        Assert.Equal(["alex@example.com", "avery@example.com", "morgan@example.com"], people.Select(r => r.Person.Email));
        var person = people[1];
        Assert.Equal(Enum.GetValues<SearchSource>().Order(), person.Matches.Select(m => m.Source).Distinct().Order());
        Assert.Equal([SearchContext.Current, SearchContext.Archived, SearchContext.Historical],
            person.Matches.Select(m => m.Context).Distinct());
        Assert.Contains(person.Matches, m => m.NoteId == secondary.Id && m.NoteOwner!.Email == "avery@example.com");
        Assert.Contains(person.Matches, m => m.NoteId == frozenNote.Id && m.Context == SearchContext.Historical &&
            m.NoteOwner!.Heading == "Cedar manager" && m.MatchText == "Cedar frozen writing");
        Assert.Contains("Archived note", person.EvidenceDescription);
        Assert.Contains("Current — Inherited Team note", person.EvidenceDescription);
        Assert.Contains("Historical — Frozen Team note", person.EvidenceDescription);
        Assert.Contains("Historical — Former Reporting chain", person.EvidenceDescription);
        Assert.Contains("Historical — Profile history", person.EvidenceDescription);
        Assert.Contains("Archived person", people[2].EvidenceDescription);
        Assert.Contains("Archived note", person.SourceLabel);
        Assert.Contains("Current — Inherited Team note", person.SourceLabel);
        Assert.Contains("Historical — Frozen Team note", person.SourceLabel);
        Assert.Contains("Historical — Former Reporting chain", person.SourceLabel);
    }

    [Theory]
    [InlineData("avery@example.com")]
    [InlineData("morgan@example.com")]
    public void Grouped_secondary_note_activation_opens_the_normal_profile_and_keeps_other_notes_available(string startingPerson)
    {
        using var app = (WorkspaceViewModel)Open();
        var ongoing = app.CreatePersonNote("avery@example.com", "Continuing", "Ordinary writing")!;
        var secondary = app.CreatePersonNote("avery@example.com", "Orchid secondary", "Orchid private writing")!;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, ongoing.Id));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id, editing: true));
        Assert.True(app.NavigatePerson(startingPerson));
        app.ChangePeopleQuery("Orchid");
        var result = Assert.Single(app.SearchPeople("Orchid"));
        Assert.Equal(startingPerson, app.PeopleNavigation.SelectedEmail);

        Assert.True(app.NavigateSearchResult(result), app.Status);

        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.Equal(ongoing.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.False(app.PersonNotesView.OtherNotesOpen);
        app.ShowOtherNotes(NoteScope.Person, true);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id));
        Assert.Equal("Orchid private writing", app.PersonNotesView.SelectedNote!.Markdown);
    }

    [Theory]
    [InlineData("avery@example.com")]
    [InlineData("alex@example.com")]
    public void Search_cannot_reload_even_the_current_Person_over_an_exact_unsaved_profile(string target)
    {
        using var app = (WorkspaceViewModel)Open();
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "  Exact unsaved writing\r\n"));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery(target);
        var navigation = app.PeopleNavigation;
        var edit = app.ProfileEdit;
        var result = Assert.Single(app.SearchPeople(target));

        Assert.Equal(navigation, app.PeopleNavigation);
        Assert.Same(edit, app.ProfileEdit);
        Assert.False(app.NavigateSearchResult(result));
        Assert.Equal(navigation, app.PeopleNavigation);
        Assert.Same(edit, app.ProfileEdit);
        Assert.Contains("Save, retry or discard", app.Status);
    }

    [Fact]
    public void Explicit_highlighting_and_rejected_activation_preserve_the_chosen_result_and_exact_note_draft()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Exact secondary", "Saved")!;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id, editing: true));
        var basis = app.BeginNoteDraft(NoteScope.Person, note.OwnerEmail, note.Id)!;
        app.RetainNoteDraft(basis);
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("example.com");
        var result = app.SearchPeople("example.com").Single(r => r.Person.Email == "morgan@example.com");
        app.HighlightSearchResult(result);
        var navigation = app.PeopleNavigation;
        Assert.False(app.NavigateSearchResult(app.PeopleNavigation.HighlightedResult!));

        Assert.Equal(navigation, app.PeopleNavigation);
        Assert.Equal("avery@example.com", app.PersonNotesView.OwnerEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal("Saved", app.PersonNotesView.SelectedNote.Markdown);
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("retry or discard", app.Status);
        Assert.NotNull(app.SaveNoteDraft(basis, note.Title, "  Exact unsaved writing\r\n"));
        Assert.Equal("  Exact unsaved writing\r\n", Assert.Single(app.GetPersonNotes(note.OwnerEmail)).Markdown);
    }

    [Fact]
    public void Grouped_activation_revalidates_Person_eligibility_and_archived_access_does_not_reactivate()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Orchid", "Orchid writing")!;
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Orchid"));
        app.ChangePeopleQuery("orchid");
        var result = Assert.Single(app.SearchPeople("orchid"));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Willow"));
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        Assert.True(app.NavigateSearchResult(result), app.Status);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Contains(app.ArchivedPeople, p => p.Email == "avery@example.com");
        Assert.DoesNotContain(app.ReportingRoots.SelectMany(root => root.Reports), p => p.Person.Email == "avery@example.com");
        app.ChangePeopleQuery("unmatched-query");
        var navigation = app.PeopleNavigation;
        Assert.False(app.NavigateSearchResult(result));
        Assert.Equal(navigation, app.PeopleNavigation);
        Assert.Contains("no longer available", app.Status);
        Assert.Equal("Orchid writing", Assert.Single(app.GetPersonNotes(note.OwnerEmail)).Markdown);
    }

    [Fact]
    public void Shared_Team_note_excerpts_do_not_allocate_a_normalized_body_for_every_report()
    {
        using var app = Open();
        Apply(app, string.Join(",", Enumerable.Range(0, 64).Select(index =>
            $$"""{"email":"report{{index}}@example.com","displayName":"Report {{index}}","managerEmail":"morgan@example.com"}""")), 2);
        var markdown = "firstneedle\n" + string.Concat(Enumerable.Repeat("Fictitious shared context \t \n", 4096)) + "lastneedle";
        var note = app.CreateTeamNote("morgan@example.com", "Shared context", markdown)!;
        app.Search("lastneedle");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var matches = app.Search("lastneedle");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(66, matches.Count);
        Assert.True(allocated < 2 * 1024 * 1024,
            $"A shared 112-KiB note must not allocate another normalized body per matching report; allocated {allocated:N0} bytes.");
        Assert.All(matches, match =>
        {
            Assert.Equal(note.Id, match.NoteId);
            Assert.Equal("morgan@example.com", match.NoteOwner!.Email);
            Assert.Equal(markdown, match.MatchText);
            Assert.Contains("lastneedle", match.Excerpt);
            Assert.DoesNotContain("firstneedle", match.Excerpt);
            Assert.True(match.Excerpt.Length <= 162);
        });
        var firstMatches = app.Search("firstneedle");
        Assert.Equal(66, firstMatches.Count);
        Assert.All(firstMatches, match =>
        {
            Assert.Contains("firstneedle", match.Excerpt);
            Assert.DoesNotContain("lastneedle", match.Excerpt);
        });
        Assert.True(app.EditTeamNote(note.Id, "Updated context", "New current writing changedneedle"), app.Status);
        Assert.Empty(app.Search("lastneedle"));
        var updated = app.Search("changedneedle");
        Assert.Equal(66, updated.Count);
        Assert.All(updated, match => Assert.Equal("New current writing changedneedle", match.Excerpt));
    }

    [Fact]
    public void Live_search_excerpts_reveal_the_match_without_navigating_or_replacing_exact_reading()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Visit plan",
            new string('a', 240) + "\nNext visit to the Orchid office.\n" + new string('z', 240))!;
        Assert.True(app.NavigatePerson("morgan@example.com"));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("orchid");

        var person = Assert.Single(app.SearchPeople(app.PeopleNavigation.Query));
        var result = Assert.Single(person.Matches);
        Assert.Contains("Next visit to the Orchid office.", result.Excerpt);
        Assert.DoesNotContain('\n', result.Excerpt);
        Assert.True(result.Excerpt.Length < 200);
        Assert.Equal("Current — Person note 'Visit plan' — owned by Ave Preferred (avery@example.com)", result.SourceLabel);
        Assert.Equal("morgan@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.True(app.PeopleNavigation.OverlayOpen);

        Assert.True(app.NavigateSearchResult(person));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id));
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.Equal(new string('a', 240) + "\nNext visit to the Orchid office.\n" + new string('z', 240),
            app.PersonNotesView.SelectedNote.Markdown);
    }

    [Fact]
    public void People_search_replaces_browsing_and_manual_clearing_restores_its_position()
    {
        using var app = (WorkspaceViewModel)Open();
        var ongoing = app.CreatePersonNote("avery@example.com", "Continuing", "Ordinary writing")!;
        var secondary = app.CreatePersonNote("avery@example.com", "Orchid secondary", "")!;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, ongoing.Id));
        app.RememberPeopleBrowsing(["morgan@example.com"], 12, 39, 576.26);
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("Orchid");
        Assert.False(app.PeopleNavigation.IsBrowsing);
        var result = Assert.Single(app.SearchPeople(app.PeopleNavigation.Query));
        Assert.Equal("Empty note", result.Excerpt);
        Assert.Equal("avery@example.com", result.Person.Email);
        Assert.Equal(ongoing.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.True(app.PeopleNavigation.OverlayOpen);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        app.ChangePeopleQuery("");
        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.Equal(["morgan@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal(12, app.PeopleNavigation.BrowseOffset);
        Assert.Equal(39, app.PeopleNavigation.BrowseHorizontalOffset);
        Assert.Equal(576.26, app.PeopleNavigation.BrowseExtentWidth);
        Assert.Equal(ongoing.Id, app.PersonNotesView.SelectedNote.Id);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id));
    }

    [Fact]
    public void Rejected_overlay_search_retains_owner_exact_note_editor_browsing_and_actionable_explanation()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Exact secondary", "Saved")!;
        app.CreatePersonNote("avery@example.com", "Other", "Untouched");
        Assert.True(app.NavigatePerson(note.OwnerEmail));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id, editing: true));
        var basis = app.BeginNoteDraft(NoteScope.Person, note.OwnerEmail, note.Id)!;
        app.RetainNoteDraft(basis);
        app.RememberPeopleBrowsing(["morgan@example.com"], 9, 24, 500);
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("alex@example");
        var result = Assert.Single(app.SearchPeople(app.PeopleNavigation.Query));
        Assert.False(app.NavigateSearchResult(result));
        Assert.False(app.NavigatePerson("morgan@example.com"));
        Assert.True(app.PeopleNavigation.OverlayOpen);
        Assert.Equal(note.OwnerEmail, app.PeopleNavigation.SelectedEmail);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.Contains("retry or discard", app.Status);
        app.ChangePeopleQuery("");
        Assert.Equal(9, app.PeopleNavigation.BrowseOffset);
        Assert.Equal(24, app.PeopleNavigation.BrowseHorizontalOffset);
        Assert.Equal(500, app.PeopleNavigation.BrowseExtentWidth);
        Assert.Equal(["morgan@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Contains("retry or discard", app.Status);
        Assert.NotNull(app.SaveNoteDraft(basis, note.Title, "Saved deliberately"));
        Assert.True(app.NavigatePerson("alex@example.com"));
        Assert.False(app.PeopleNavigation.OverlayOpen);
    }

    [Fact]
    public void Historical_search_opens_the_normal_Person_with_frozen_evidence_still_available()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreateTeamNote("morgan@example.com", "Cedar", "Cedar original")!;
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(app.EditTeamNote(note.Id, "Later", "Willow current"));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("cedar");
        var result = Assert.Single(app.SearchPeople("cedar"));
        Assert.True(app.NavigateSearchResult(result));
        Assert.Null(app.PeopleNavigation.SelectedResult);
        var destination = Assert.Single(result.Matches);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(capture.Id, destination.HistoricalContextId);
        Assert.Equal(note.Id, destination.NoteId);
        Assert.Equal("Cedar original", destination.MatchText);
        Assert.Equal("Cedar original", Assert.Single(destination.HistoricalContext!.TeamNotes).Markdown);
        Assert.False(app.PeopleNavigation.OverlayOpen);
    }

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
