using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
                $"Local matches: {results.Count} — current, archived and historical labels identify applicability.";
            if (selectedMatch is not null)
            {
                var match = results.FirstOrDefault(r => SameSearchDestination(r, selectedMatch));
                if (match is null) ClearSearchContext();
                else ShowSearchContext(match);
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

    private async void SelectSearchResult(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingQueries || SearchResults.SelectedItem is not SearchResult result) return;
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
        ShowSearchContext(result);
        SearchContextPanel.BringIntoView();
        ShowStatus();
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (ExplorerPanel.Visibility != Visibility.Visible || workflow.PeopleNavigation.OverlayOpen ||
                selectedMatch is null || !SameSearchDestination(result, selectedMatch)) return;
            FocusManager.SetFocusedElement(this, SearchContextText);
            if (IsVisible) SearchContextText.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
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
        SearchContextPanel.Visibility = Visibility.Visible;
        SearchContextText.Text = result.Label;
        var markdown = result.MatchText;
        if (result.HistoricalContext is { } capture)
        {
            SearchContextText.Text += "\n" + FormerChain(capture) + "\n" + capture.DisplayProvenance;
            if (result.NoteId is { } noteId)
            {
                var note = capture.TeamNotes.Single(n => n.Id == noteId);
                SearchContextText.Text += "\n" + FrozenNoteLabel(capture, note);
                markdown = note.Markdown;
            }
        }
        else if (result.ProfileChange is { } change)
            SearchContextText.Text += "\n" + new ProfileTimeline([change], [], []).DisplayChanges;
        if (result.Source is SearchSource.Profile or SearchSource.ProfileHistory or SearchSource.FormerReportingChain)
        {
            var document = PrivateMarkdownReading.Create("", OpenHistoricalLink);
            document.Blocks.Add(new Paragraph(new Run(markdown)));
            SearchResultReading.Document = document;
        }
        else SearchResultReading.Document = PrivateMarkdownReading.Create(markdown, OpenHistoricalLink);
    }

    private void CloseSearchContext(object sender, RoutedEventArgs e) => ClearSearchContext();
    private void ClearSearchContext(bool clearSelection = true)
    {
        if (clearSelection) workflow.CloseSearchContext();
        SearchContextPanel.Visibility = Visibility.Collapsed;
        SearchContextText.Text = "";
        SearchResultReading.Document = new FlowDocument();
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
        FavoriteButton.Content = person.IsFavorite ? "Unmark favorite" : "Mark favorite";
        FavoriteButton.IsEnabled = workflow.IsWorkspaceCurrent;
        FavoriteStatus.Text = (person.IsFavorite ? "Marked favorite" : "Not marked favorite") +
            (unsavedFavorite.HasValue ? " — pending action NOT saved" : "");
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
