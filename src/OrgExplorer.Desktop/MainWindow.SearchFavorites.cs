using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private sealed record FavoriteRow(PersonProfile Person, string Label);
    private bool refreshingQueries;
    private readonly Dictionary<string, bool> unsavedFavorites = new(StringComparer.OrdinalIgnoreCase);
    private bool? unsavedFavorite
    {
        get => selectedEmail is { } owner && unsavedFavorites.TryGetValue(owner, out var value) ? value : null;
        set
        {
            if (selectedEmail is not { } owner) return;
            if (value is { } favorite) unsavedFavorites[owner] = favorite;
            else unsavedFavorites.Remove(owner);
        }
    }
    private SearchResult? selectedMatch => workflow.PeopleNavigation.SelectedResult;
    private bool queryDirty = true;
    private string? lastQuery;
    private int queryRequest;
    private int queryIntent;
    private Task searchRefresh = Task.CompletedTask;
    private bool PeopleSearchWorkBlocked => workPending && !ordinaryLocalWorkPending &&
        !localImportPending && !recoveryWorkPending;
    private bool activatingSearch;
    private FrameworkElement? matchedProfileValue;
    private string? matchedProfileHelp;
    private Paragraph? matchedFormerFact;

    private async void ConfirmSearchQuery(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || workflow.PeopleNavigation.IsBrowsing) return;
        if (e.Key is not (Key.Up or Key.Down or Key.Enter)) return;
        e.Handled = true;
        if (!await AwaitSearchResults()) return;
        if (e.Key is Key.Up or Key.Down)
        {
            workflow.MoveSearchHighlight(e.Key == Key.Down ? 1 : -1);
            ShowSearchHighlight();
        }
        else if (e.Key == Key.Enter)
        {
            if (workflow.PeopleNavigation.HighlightedResult is { } result) await ActivateSearchResult(result);
        }
    }

    private async Task<bool> AwaitSearchResults()
    {
        var query = SearchQuery.Text;
        var request = queryRequest;
        await searchRefresh;
        return request == queryRequest && query == SearchQuery.Text &&
            !PeopleSearchWorkBlocked && !queryDirty;
    }

    private void HighlightSearchResult(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingQueries) workflow.HighlightSearchResult(SearchResults.SelectedItem as PersonSearchResult);
    }

    private void ShowSearchHighlight()
    {
        var wasRefreshing = refreshingQueries;
        refreshingQueries = true;
        try
        {
            SearchResults.SelectedItem = SearchResults.Items.Cast<PersonSearchResult>().FirstOrDefault(r =>
                workflow.PeopleNavigation.HighlightedResult is { } highlighted && SameSearchPerson(r, highlighted));
            if (SearchResults.SelectedItem is { } selected) SearchResults.ScrollIntoView(selected);
        }
        finally { refreshingQueries = wasRefreshing; }
    }

    private async void ActivateSearchWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (SearchResults.SelectedItem is PersonSearchResult selected)
        {
            await ActivateSearchResult(selected);
            return;
        }
        if (!await AwaitSearchResults()) return;
        if (SearchResults.SelectedItem is PersonSearchResult result) await ActivateSearchResult(result);
    }

    private async void ActivateSearchWithMouse(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(SearchResults, source) is not ListBoxItem { DataContext: PersonSearchResult result }) return;
        e.Handled = true;
        await ActivateSearchResult(result);
    }

    private void ClearSearch(object sender, RoutedEventArgs e)
    {
        SearchQuery.Clear();
        SearchQuery.Focus();
    }

    private void ChangeSearchQuery(object sender, TextChangedEventArgs e)
    {
        queryIntent++;
        if (SearchResults is null || loading) return;
        var wasBrowsing = workflow.PeopleNavigation.IsBrowsing;
        if (wasBrowsing && !string.IsNullOrWhiteSpace(SearchQuery.Text) && PeoplePanel.Visibility == Visibility.Visible)
            RememberPeopleBrowsing();
        workflow.BeginPeopleSearch(SearchQuery.Text);
        if (!wideWorkspace && !workflow.PeopleNavigation.IsBrowsing) workflow.ShowPeopleOverlay(true);
        UpdatePeopleNavigation();
        if (!wasBrowsing && workflow.PeopleNavigation.IsBrowsing)
        {
            if (pendingPeopleRevealEmail is not null) RevealSelectedPerson();
            else RestorePeopleBrowsing();
        }
        if (workflow.PeopleNavigation.IsBrowsing)
        {
            refreshingQueries = true;
            SearchResults.SelectedItem = null;
            refreshingQueries = false;
            ClearSearchContext();
        }
        RefreshSearchAndFavorites();
    }

    private async void RefreshSearchAndFavorites()
    {
        if (SearchResults is null || refreshingQueries || PeopleSearchWorkBlocked) return;
        var query = SearchQuery.Text;
        if (query != workflow.PeopleNavigation.Query) return;
        if (!queryDirty && lastQuery == query)
        {
            refreshingQueries = true;
            FavoritesList.SelectedItem = FavoritesList.Items.Cast<FavoriteRow>().FirstOrDefault(r =>
                string.Equals(r.Person.Email, selectedEmail, StringComparison.OrdinalIgnoreCase));
            refreshingQueries = false;
            return;
        }
        queryDirty = false;
        lastQuery = query;
        var request = ++queryRequest;
        var searchRequest = workflow.BeginPeopleSearch(query);
        searchRefresh = RefreshSearchSnapshot(query, request, searchRequest);
        await searchRefresh;
    }

    private async Task RefreshSearchSnapshot(string query, int request, long searchRequest)
    {
        var snapshot = await Task.Run(() => (Results: workflow.SearchPeople(query),
            Favorites: workflow.FavoritePeople, Current: workflow.IsWorkspaceCurrent));
        if (request != queryRequest || PeopleSearchWorkBlocked || query != SearchQuery.Text) return;
        if (!workflow.PublishPeopleSearchResults(searchRequest, snapshot.Results))
        {
            queryDirty = true;
            return;
        }
        refreshingQueries = true;
        try
        {
            var results = snapshot.Results;
            SearchResults.ItemsSource = results;
            ShowSearchHighlight();
            SearchCount.Text = string.IsNullOrWhiteSpace(SearchQuery.Text) ? "Enter text to search local information." :
                $"People found: {results.Count} — local profiles and notes";
            if (selectedMatch is not null)
            {
                var match = results.FirstOrDefault(r => r.Matches.Any(m => SameSearchDestination(m, selectedMatch)));
                if (match is null) ClearSearchContext();
            }
            var favorites = snapshot.Favorites.Select(p => new FavoriteRow(p,
                (snapshot.Current ? "" : "STALE retained — ") +
                $"{p.Heading} ({p.Email})" + (p.Archived ? " — Archived, retained profile" : ""))).ToArray();
            if (!favorites.SequenceEqual(FavoritesList.Items.Cast<FavoriteRow>())) FavoritesList.ItemsSource = favorites;
            FavoritesList.SelectedItem = favorites.FirstOrDefault(p =>
                string.Equals(p.Person.Email, selectedEmail, StringComparison.OrdinalIgnoreCase));
            FavoriteCount.Text = $"Favorites: {favorites.Length}";
        }
        finally { refreshingQueries = false; }
    }

    private static bool SameSearchDestination(SearchResult left, SearchResult right) =>
        WorkspaceViewModel.SameSearchDestination(left, right);

    private static bool SameSearchPerson(PersonSearchResult left, PersonSearchResult right) =>
        string.Equals(left.Person.Email, right.Person.Email, StringComparison.OrdinalIgnoreCase);

    private async Task ActivateSearchResult(PersonSearchResult result)
    {
        if (refreshingQueries || activatingSearch) return;
        activatingSearch = true;
        try
        {
            var query = SearchQuery.Text;
            var intent = queryIntent;
            var browsing = peopleBrowseRequest;
            var workspace = workflow.Compatibility.WorkspacePath;
            var identity = workflow.Synchronization.WorkspaceId;
            var current = await Task.Run(() => workflow.SearchPeople(query).FirstOrDefault(r => SameSearchPerson(r, result)));
            if (query != SearchQuery.Text || intent != queryIntent || browsing != peopleBrowseRequest ||
                workspace != workflow.Compatibility.WorkspacePath || identity != workflow.Synchronization.WorkspaceId) return;
            if (PeopleSearchWorkBlocked)
            {
                TryLeaveLocalEdits();
                return;
            }
            if (current is null)
            {
                RefreshSearchAndFavorites();
                StatusText.Text = "That search match is no longer available in the validated Workspace.";
                return;
            }
            result = current;
            if (!TryLeaveLocalEdits(browsing: true))
            {
                return;
            }
            if (!workflow.NavigateSearchResult(result))
            {
                ShowNavigationBlocked(workflow.Status);
                return;
            }
            if (!NavigateTo(result.Person.Email, searchNavigation: true))
            {
                return;
            }
            PersonNotes.RefreshExternalWorkspace();
            TeamNotes.RefreshExternalWorkspace();
            ClearSearchContext();
            SearchQuery.Clear();
            ShowArchivedPeople.IsChecked = workflow.PeopleNavigation.ArchivedPeopleVisible;
            expandedPeople.UnionWith(workflow.PeopleNavigation.ExpandedPeople);
            pendingPeopleRevealEmail = result.Person.Email;
            peopleBrowseRequest++;
            UpdatePeopleNavigation();
            if (wideWorkspace) RevealSelectedPerson();
            else FocusPeopleEntry();
            ShowStatus();
        }
        finally { activatingSearch = false; }
    }

    private SearchResult? pendingPreparedContext;

    private bool CanRevealPreparedContext => ExplorerPanel.Visibility == Visibility.Visible &&
        !workflow.IsImportOpen && workflow.PeopleNavigation.IsBrowsing && !workflow.PeopleNavigation.OverlayOpen;

    private void RevealPreparedContext()
    {
        if (!CanRevealPreparedContext || pendingPreparedContext is not { } result) return;
        pendingPreparedContext = null;
        if (selectedMatch is not null && SameSearchDestination(result, selectedMatch))
            ShowSearchContext(result, deferUntilBrowsing: true);
    }

    private void ShowSearchContext(SearchResult result, bool deferUntilBrowsing = false)
    {
        var target = SearchDestinationSurface(result);
        Block? readingBlock = null;
        switch (result.Source)
        {
            case SearchSource.Profile:
                matchedProfileValue = target;
                matchedProfileHelp = System.Windows.Automation.AutomationProperties.GetHelpText(target);
                System.Windows.Automation.AutomationProperties.SetHelpText(target, result.Label);
                if (target is TextBlock value) value.Background = (Brush)FindResource("SearchMatchBrush");
                else if (target is Control control) control.Background = (Brush)FindResource("SearchMatchBrush");
                if (result.Field == "archived" && !result.Person.Archived)
                {
                    PersonArchivalStatus.Text = "Not archived";
                    PersonArchivalStatus.ToolTip = "This Person is not archived. This is not employment or presence information.";
                    PersonArchivalStatus.Visibility = Visibility.Visible;
                }
                break;
            case SearchSource.InheritedTeamNote:
                readingBlock = TeamNotes.RevealInheritedNote(result.NoteId!.Value);
                break;
            case SearchSource.ProfileHistory:
                HistoryAccess.IsExpanded = true;
                break;
            case SearchSource.HistoricalTeamNote:
            case SearchSource.FormerReportingChain:
                HistoryAccess.IsExpanded = true;
                var capture = HistoricalTeamReading.Document.Blocks.OfType<Section>()
                    .Single(section => Equals(section.Tag, result.HistoricalContextId));
                readingBlock = result.NoteId is { } id
                    ? capture.Blocks.OfType<Section>().Single(section => Equals(section.Tag, id)) : capture;
                if (result.Source == SearchSource.FormerReportingChain)
                {
                    matchedFormerFact = new Paragraph(new Run(result.SourceLabel + "\n" + result.MatchText))
                        { Background = (Brush)FindResource("SearchMatchBrush") };
                    capture.Blocks.InsertBefore(capture.Blocks.FirstBlock, matchedFormerFact);
                }
                System.Windows.Automation.AutomationProperties.SetHelpText(HistoricalTeamReading,
                    $"{result.Label}; capture {result.HistoricalContextId}; note {result.NoteId}");
                break;
        }
        if (deferUntilBrowsing && !CanRevealPreparedContext)
        {
            pendingPreparedContext = result;
            return;
        }
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (ExplorerPanel.Visibility != Visibility.Visible || workflow.PeopleNavigation.OverlayOpen ||
                selectedMatch is null || !SameSearchDestination(result, selectedMatch)) return;
            if (deferUntilBrowsing && !CanRevealPreparedContext)
            {
                pendingPreparedContext = result;
                return;
            }
            FocusManager.SetFocusedElement(this, target);
            if (IsVisible) target.Focus();
            if (readingBlock is not null) readingBlock.BringIntoView();
            else target.BringIntoView(new Rect(0, 0, target.ActualWidth, Math.Min(40, target.ActualHeight)));
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private FrameworkElement SearchDestinationSurface(SearchResult result) => result.Source switch
    {
        SearchSource.PersonNote => PersonNotes.NoteReading,
        SearchSource.OwnedTeamNote => TeamNotes.NoteReading,
        SearchSource.InheritedTeamNote => TeamNotes.InheritedNoteReading,
        SearchSource.HistoricalTeamNote or SearchSource.FormerReportingChain => HistoricalTeamReading,
        SearchSource.ProfileHistory => ProfileTimelineText,
        _ => result.Field switch
        {
            "displayName" => DirectoryName, "preferredName" => ProfileHeading, "email" => Email,
            "alias" => AliasValue, "jobTitle" => JobTitleValue, "department" => DepartmentValue,
            "officeBuilding" => OfficeBuildingValue, "officeNumber" => OfficeNumberValue,
            "officeState" => OfficeStateValue, "officeCountry" => OfficeCountryValue,
            "linkedInProfileUrl" => LinkedInProfileUrlValue, "managerEmail" => Manager,
            "isFavorite" => FavoriteButton, "archived" => PersonArchivalStatus, "photoPath" => Photo,
            _ => ProfileHeading
        }
    };

    private void ClearSearchContext(bool clearSelection = true)
    {
        pendingPreparedContext = null;
        if (clearSelection) workflow.CloseSearchContext();
        if (matchedProfileValue == PersonArchivalStatus && selectedEmail is { } email &&
            workflow.GetRelationships(email)?.Person is { Archived: false })
        {
            PersonArchivalStatus.Text = "";
            PersonArchivalStatus.Visibility = Visibility.Collapsed;
        }
        if (matchedProfileValue is TextBlock value) value.ClearValue(TextBlock.BackgroundProperty);
        else if (matchedProfileValue is Control control) control.ClearValue(Control.BackgroundProperty);
        if (matchedProfileValue is not null)
            System.Windows.Automation.AutomationProperties.SetHelpText(matchedProfileValue, matchedProfileHelp ?? "");
        matchedProfileValue = null;
        matchedProfileHelp = null;
        if (matchedFormerFact?.Parent is Section section) section.Blocks.Remove(matchedFormerFact);
        matchedFormerFact = null;
        System.Windows.Automation.AutomationProperties.SetHelpText(HistoricalTeamReading, "");
    }

    private void SelectFavoritePerson(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingQueries || FavoritesList.SelectedItem is not FavoriteRow row) return;
        var overlay = workflow.PeopleNavigation.OverlayOpen;
        RememberPeopleBrowsing();
        if (TryLeaveLocalEdits(browsing: true) && NavigateTo(row.Person.Email))
        {
            ClearSearchContext();
            if (overlay) FocusPersonAfterOverlay();
        }
        RefreshSearchAndFavorites();
    }

    private void ShowFavorite(PersonProfile person)
    {
        var action = person.IsFavorite ? "Unmark favorite" : "Mark favorite";
        FavoriteButton.Content = person.IsFavorite ? "\uE735" : "\uE734";
        System.Windows.Automation.AutomationProperties.SetName(FavoriteButton, action);
        FavoriteButton.ToolTip = action;
        FavoriteButton.IsEnabled = CanEditCurrentWorkspace && !workPending;
        FavoriteStatus.Text = (person.IsFavorite ? "Marked favorite" : "Not marked favorite") +
            (unsavedFavorite.HasValue ? " — pending action NOT saved" : "");
        FavoriteStatus.Visibility = unsavedFavorite.HasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChangeFavorite(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null || workPending) return;
        var person = workflow.GetRelationships(selectedEmail)!.Person;
        var favorite = !person.IsFavorite;
        if (await RunWorkflowAsync(() => workflow.EditFavorite(person.Email, favorite), "Saving local favorite…", ordinarySave: true))
        {
            unsavedFavorites.Remove(person.Email);
            RefreshPeople();
        }
        else if (!workflow.CanSafelyClose) unsavedFavorites[person.Email] = favorite;
        if (selectedEmail == person.Email) ShowFavorite(workflow.GetRelationships(person.Email)!.Person);
        InvalidateRelationshipPreview();
    }
}
