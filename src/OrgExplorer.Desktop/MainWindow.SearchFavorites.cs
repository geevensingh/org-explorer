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
    private bool? unsavedFavorite;
    private SearchResult? selectedMatch => workflow.PeopleNavigation.SelectedResult;
    private bool queryDirty = true;
    private string? lastQuery;
    private int queryRequest;
    private bool activatingSearch;
    private FrameworkElement? matchedProfileValue;
    private string? matchedProfileHelp;
    private Paragraph? matchedFormerFact;

    private void ConfirmSearchQuery(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (workflow.PeopleNavigation.IsBrowsing) return;
        if (!wideWorkspace) workflow.ShowPeopleOverlay(true);
        UpdatePeopleNavigation();
        FocusManager.SetFocusedElement(this, SearchResults);
        if (IsVisible) SearchResults.Focus();
    }

    private async void ActivateSearchWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (SearchResults.SelectedItem is SearchResult result) await ActivateSearchResult(result);
    }

    private async void ActivateSearchWithMouse(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(SearchResults, source) is not ListBoxItem { DataContext: SearchResult result }) return;
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
        if (SearchResults is null || loading) return;
        var wasBrowsing = workflow.PeopleNavigation.IsBrowsing;
        if (wasBrowsing && !string.IsNullOrWhiteSpace(SearchQuery.Text) && PeoplePanel.Visibility == Visibility.Visible)
            RememberPeopleBrowsing();
        workflow.ChangePeopleQuery(SearchQuery.Text);
        if (!wideWorkspace && !workflow.PeopleNavigation.IsBrowsing) workflow.ShowPeopleOverlay(true);
        UpdatePeopleNavigation();
        if (!wasBrowsing && workflow.PeopleNavigation.IsBrowsing) RestorePeopleBrowsing();
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
        if (SearchResults is null || refreshingQueries || workPending) return;
        var query = SearchQuery.Text;
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
        var snapshot = await Task.Run(() => (Results: workflow.Search(query),
            Favorites: workflow.FavoritePeople, Current: workflow.IsWorkspaceCurrent));
        if (request != queryRequest || workPending || query != SearchQuery.Text) return;
        refreshingQueries = true;
        try
        {
            var results = snapshot.Results;
            if (!results.SequenceEqual(SearchResults.Items.Cast<SearchResult>())) SearchResults.ItemsSource = results;
            SearchCount.Text = string.IsNullOrWhiteSpace(SearchQuery.Text) ? "Enter text to search local information." :
                $"Local matches: {results.Count} — profiles and notes";
            if (selectedMatch is not null)
            {
                var match = results.FirstOrDefault(r => SameSearchDestination(r, selectedMatch));
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

    private async Task ActivateSearchResult(SearchResult result)
    {
        if (refreshingQueries || activatingSearch) return;
        activatingSearch = true;
        try
        {
            var query = SearchQuery.Text;
            var current = await Task.Run(() => workflow.Search(query).FirstOrDefault(r => SameSearchDestination(r, result)));
            if (query != SearchQuery.Text) return;
            if (workPending)
            {
                TryLeaveLocalEdits();
                RestoreSearchSelection();
                return;
            }
            if (current is null)
            {
                RefreshSearchAndFavorites();
                StatusText.Text = "That search match is no longer available in the validated Workspace.";
                return;
            }
            result = current;
            if (!TryLeaveLocalEdits())
            {
                RestoreSearchSelection();
                return;
            }
            if (!workflow.NavigateSearchResult(result))
            {
                RestoreSearchSelection();
                ShowNavigationBlocked(workflow.Status);
                return;
            }
            result = workflow.PeopleNavigation.SelectedResult!;
            if (!NavigateTo(result.Person.Email, searchNavigation: true))
            {
                RestoreSearchSelection();
                return;
            }
            if (result.Source == SearchSource.PersonNote && result.NoteId is { } personNote)
                PersonNotes.OpenNote(personNote);
            else if (result.Source == SearchSource.OwnedTeamNote && result.NoteId is { } teamNote)
                TeamNotes.OpenNote(teamNote);
            ClearSearchContext(clearSelection: false);
            ShowSearchContext(result);
            ShowStatus();
        }
        finally { activatingSearch = false; }
    }

    private void RestoreSearchSelection()
    {
        refreshingQueries = true;
        SearchResults.SelectedItem = SearchResults.Items.Cast<SearchResult>().FirstOrDefault(r =>
            selectedMatch is not null && SameSearchDestination(r, selectedMatch));
        refreshingQueries = false;
    }

    private void ShowSearchContext(SearchResult result)
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
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (ExplorerPanel.Visibility != Visibility.Visible || workflow.PeopleNavigation.OverlayOpen ||
                selectedMatch is null || !SameSearchDestination(result, selectedMatch)) return;
            FocusManager.SetFocusedElement(this, target);
            if (IsVisible) target.Focus();
            if (readingBlock is not null) readingBlock.BringIntoView();
            else target.BringIntoView(new Rect(0, 0, target.ActualWidth, Math.Min(40, target.ActualHeight)));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
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
        if (TryLeaveLocalEdits() && NavigateTo(row.Person.Email))
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
        FavoriteButton.IsEnabled = workflow.IsWorkspaceCurrent;
        FavoriteStatus.Text = (person.IsFavorite ? "Marked favorite" : "Not marked favorite") +
            (unsavedFavorite.HasValue ? " — pending action NOT saved" : "");
        FavoriteStatus.Visibility = unsavedFavorite.HasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChangeFavorite(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null || workPending) return;
        var person = workflow.GetRelationships(selectedEmail)!.Person;
        var favorite = !person.IsFavorite;
        if (await RunWorkflowAsync(() => workflow.EditFavorite(person.Email, favorite), "Saving local favorite…"))
        {
            unsavedFavorite = null;
            RefreshPeople();
        }
        else if (!workflow.CanSafelyClose) unsavedFavorite = favorite;
        ShowFavorite(workflow.GetRelationships(person.Email)!.Person);
        InvalidateRelationshipPreview();
    }
}
