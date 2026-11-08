using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private sealed record FavoriteRow(PersonProfile Person, string Label);
    private bool refreshingQueries;
    private bool? unsavedFavorite;
    private SearchResult? selectedMatch;

    private void ChangeSearchQuery(object sender, TextChangedEventArgs e) => RefreshSearchAndFavorites();

    private void RefreshSearchAndFavorites()
    {
        if (SearchResults is null || refreshingQueries) return;
        refreshingQueries = true;
        try
        {
            var results = workflow.Search(SearchQuery.Text);
            if (!results.SequenceEqual(SearchResults.Items.Cast<SearchResult>())) SearchResults.ItemsSource = results;
            SearchResults.Visibility = results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            SearchCount.Text = string.IsNullOrWhiteSpace(SearchQuery.Text) ? "Enter text to search local information." :
                $"Local matches: {results.Count} — current, archived and historical labels identify applicability.";
            if (selectedMatch is not null)
            {
                selectedMatch = results.FirstOrDefault(r => SameSearchDestination(r, selectedMatch));
                if (selectedMatch is null) ClearSearchContext();
                else ShowSearchContext(selectedMatch);
            }
            var favorites = workflow.FavoritePeople.Select(p => new FavoriteRow(p,
                (workflow.IsWorkspaceCurrent ? "" : "STALE retained — ") +
                $"{p.Heading} ({p.Email})" + (p.Archived ? " — Archived, retained profile" : ""))).ToArray();
            if (!favorites.SequenceEqual(FavoritesList.Items.Cast<FavoriteRow>())) FavoritesList.ItemsSource = favorites;
            FavoritesList.SelectedItem = favorites.FirstOrDefault(p =>
                string.Equals(p.Person.Email, selectedEmail, StringComparison.OrdinalIgnoreCase));
            FavoriteCount.Text = $"Favorites: {favorites.Length}";
        }
        finally { refreshingQueries = false; }
    }

    private static bool SameSearchDestination(SearchResult left, SearchResult right) =>
        string.Equals(left.Person.Email, right.Person.Email, StringComparison.OrdinalIgnoreCase) &&
        left.Source == right.Source && left.Field == right.Field && left.NoteId == right.NoteId &&
        left.HistoricalContextId == right.HistoricalContextId && left.MatchText == right.MatchText &&
        (left.ProfileChange is null && right.ProfileChange is null ||
         left.ProfileChange is { } leftChange && right.ProfileChange is { } rightChange &&
         leftChange.Evidence.RecordedAt == rightChange.Evidence.RecordedAt &&
         leftChange.Evidence.BatchId == rightChange.Evidence.BatchId &&
         System.Text.Json.JsonElement.DeepEquals(leftChange.OldValue, rightChange.OldValue) &&
         System.Text.Json.JsonElement.DeepEquals(leftChange.NewValue, rightChange.NewValue));

    private void SelectSearchResult(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingQueries || SearchResults.SelectedItem is not SearchResult result) return;
        var current = workflow.Search(SearchQuery.Text).FirstOrDefault(r => SameSearchDestination(r, result));
        if (current is null)
        {
            RefreshSearchAndFavorites();
            StatusText.Text = "That search match is no longer available in the validated Workspace.";
            return;
        }
        result = current;
        if (!TryLeaveLocalEdits() || !NavigateTo(result.Person.Email))
        {
            refreshingQueries = true;
            SearchResults.SelectedItem = SearchResults.Items.Cast<SearchResult>().FirstOrDefault(r =>
                selectedMatch is not null && SameSearchDestination(r, selectedMatch));
            refreshingQueries = false;
            return;
        }
        selectedMatch = result;
        if (result.Source == SearchSource.PersonNote && result.NoteId is { } personNote)
            PersonNotes.OpenNote(personNote);
        else if (result.Source == SearchSource.OwnedTeamNote && result.NoteId is { } teamNote)
            TeamNotes.OpenNote(teamNote);
        ShowSearchContext(result);
        SearchContextPanel.BringIntoView();
        ShowStatus();
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
            SearchResultReading.Document = new FlowDocument(new Paragraph(new Run(markdown)));
        else SearchResultReading.Document = PrivateMarkdownReading.Create(markdown, OpenHistoricalLink);
    }

    private void CloseSearchContext(object sender, RoutedEventArgs e) => ClearSearchContext();
    private void ClearSearchContext()
    {
        selectedMatch = null;
        SearchContextPanel.Visibility = Visibility.Collapsed;
        SearchContextText.Text = "";
        SearchResultReading.Document = new FlowDocument();
    }

    private void SelectFavoritePerson(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingQueries || FavoritesList.SelectedItem is not FavoriteRow row) return;
        if (TryLeaveLocalEdits() && NavigateTo(row.Person.Email)) ClearSearchContext();
        RefreshSearchAndFavorites();
    }

    private void ShowFavorite(PersonProfile person)
    {
        FavoriteButton.Content = person.IsFavorite ? "Unmark favorite" : "Mark favorite";
        FavoriteButton.IsEnabled = workflow.IsWorkspaceCurrent;
        FavoriteStatus.Text = (person.IsFavorite ? "Marked favorite" : "Not marked favorite") +
            (unsavedFavorite.HasValue ? " — pending action NOT saved" : "");
    }

    private void ChangeFavorite(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null || !TryLeaveLocalEdits()) return;
        var person = workflow.GetRelationships(selectedEmail)!.Person;
        var favorite = !person.IsFavorite;
        if (workflow.EditFavorite(person.Email, favorite))
        {
            unsavedFavorite = null;
            RefreshPeople();
        }
        else if (!workflow.CanSafelyClose) unsavedFavorite = favorite;
        ShowFavorite(workflow.GetRelationships(person.Email)!.Person);
        InvalidateRelationshipPreview();
    }
}
