using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class TeamNotesControl : UserControl
{
    private WorkspaceViewModel? workflow;
    private Action? changed;
    private string? owner;
    private Guid? selected;
    private OwnedNote? displayedNote;
    private bool loading;
    private bool includeArchived;
    private readonly OwnedNotePresentation presentation;
    private bool HasRetainedDraft => selected is { } id && workflow?.HasUnsavedNoteDraft(NoteScope.Team, id) == true;
    internal bool HasUnsavedChanges => HasRetainedDraft;
    public event Action<Guid>? OwnerRequested;
    internal FlowDocumentScrollViewer InheritedNoteReading => InheritedReading;
    internal FlowDocumentScrollViewer NoteReading => Reading;

    internal Section RevealInheritedNote(Guid id)
    {
        CurrentTeamContext.IsExpanded = true;
        var note = InheritedReading.Document.Blocks.OfType<Section>().Single(section => Equals(section.Tag, id));
        var context = workflow!.GetInheritedTeamNotes(owner!).Single(item => item.Note.Id == id);
        AutomationProperties.SetHelpText(InheritedReading, $"Current inherited Team note {id}; '{context.Note.Title}'; " +
            $"owned by {context.Owner.Heading} ({context.Owner.Email}); {context.Note.ReviewStatus}");
        return note;
    }

    public TeamNotesControl()
    {
        InitializeComponent();
        presentation = new(NoteScope.Team)
        {
            Status = OngoingStatus, Kind = NoteKind, Heading = NoteHeading,
            Designate = DesignateButton, Ongoing = OngoingButton,
            New = NewButton, Empty = EmptyNotes, EmptyBody = EmptyBody, OperationStatus = NoteStatus, Surface = this
        };
    }

    public void Attach(WorkspaceViewModel application, Action onChanged)
    {
        workflow = application;
        changed = onChanged;
    }

    public void OpenPerson(string? email)
    {
        var changedOwner = !string.Equals(owner, email, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(owner, email, StringComparison.OrdinalIgnoreCase))
        {
            if (workflow?.OpenOwnedNotes(NoteScope.Team, email) != true) return;
            owner = email;
            loading = true;
            IncludeArchived.IsChecked = includeArchived = false;
            OtherNotes.IsExpanded = CurrentTeamContext.IsExpanded = false;
            loading = false;
        }
        Ownership.Text = email is null ? "Select a Person." :
            $"Owned by {email}; applies to current formal descendants.";
        NewButton.IsEnabled = email is not null;
        if (changedOwner) RefreshNotes();
        else if (workflow is not null) presentation.ShowOperationStatus(workflow, HasRetainedDraft);
        RefreshContext();
    }

    public void OpenNote(Guid id)
    {
        if (workflow is null || owner is null) return;
        if (!workflow.OpenOwnedNote(NoteScope.Team, id)) { ShowPresentation(); return; }
        loading = true;
        if (workflow.TeamNotesView.SelectedNote?.Archived == true)
            IncludeArchived.IsChecked = includeArchived = true;
        if (includeArchived) workflow.ShowOtherNotes(NoteScope.Team, true);
        loading = false;
        RefreshNotes();
    }

    private void RefreshNotes()
    {
        if (workflow is null) return;
        var view = workflow.TeamNotesView;
        loading = true;
        var notes = view.Notes.Where(n => includeArchived || !n.Archived).ToArray();
        NotesList.ItemsSource = notes;
        NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == view.SelectedNote?.Id);
        OtherNotes.IsExpanded = view.OtherNotesOpen;
        OtherNotes.Visibility = owner is not null && view.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        loading = false;
        LoadNote(view.SelectedNote);
        ShowPresentation();
    }

    private void LoadNote(OwnedNote? note)
    {
        var editing = workflow?.TeamNotesView.IsEditing == true;
        loading = true;
        selected = note?.Id;
        var retained = note is null ? null : workflow?.GetRetainedNoteDraft(NoteScope.Team, note.Id);
        var noteBase = retained?.Basis ?? (note is null ? null : workflow?.BeginNoteDraft(NoteScope.Team, note.OwnerEmail, note.Id));
        var title = retained?.Title ?? noteBase?.Title ?? "";
        var markdown = retained?.Markdown ?? noteBase?.Markdown ?? "";
        displayedNote = note is null ? null : note with { Title = title, Markdown = markdown };
        if (editing) NoteTitle.Text = title;
        if (editing) MarkdownEditor.Text = markdown;
        NoteHeading.Text = title;
        Views.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        Views.SelectedItem = editing ? EditTab : ReadTab;
        NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = noteBase is not null && note is { Archived: false };
        ArchiveButton.IsEnabled = note is not null;
        ArchiveButton.Content = note?.Archived == true ? "Restore Team note" : "Archive Team note";
        ArchiveButton.Visibility = ConfirmButton.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.IsEnabled = DiscardButton.IsEnabled = false;
        RetryButton.Visibility = DiscardButton.Visibility = Visibility.Collapsed;
        Reading.Document = PrivateMarkdownReading.Create(markdown, OpenExternalLink);
        loading = false;
        RefreshLocalAutosave();
    }

    private void SelectNote(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (NotesList.SelectedItem is OwnedNote note) OpenNote(note.Id);
    }

    private Task<T> RunAsync<T>(Func<T> operation) => workflow!.RunLocalSaveAsync(operation);

    private void CreateNote(object sender, RoutedEventArgs e)
    {
        if (workflow is null || owner is null) return;
        if (workflow.CreateOwnedNoteDraft(NoteScope.Team, owner) is { } created)
        {
            RefreshNotes();
            if (IsVisible) _ = Dispatcher.BeginInvoke(() =>
            {
                if (selected != created.Id || owner != created.OwnerEmail) return;
                NoteTitle.BringIntoView();
                NoteTitle.Focus();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
        ShowStatus();
    }

    private void EditNote(object sender, TextChangedEventArgs e)
    {
        if (loading || workflow is null || selected is not { } id) return;
        workflow.UpdateNoteDraft(NoteScope.Team, id, NoteTitle.Text, MarkdownEditor.Text);
        displayedNote = displayedNote is { } current
            ? current with { Title = NoteTitle.Text, Markdown = MarkdownEditor.Text }
            : throw new InvalidOperationException("The selected Team editor has no displayed note.");
        NoteHeading.Text = NoteTitle.Text;
        Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
        RefreshLocalAutosave();
    }

    public Task<bool> FlushPendingDraftAsync() => workflow!.FlushNoteDraftsAsync();

    private async void ConfirmNote(object sender, RoutedEventArgs e)
    {
        if (workflow is null || selected is not { } id) return;
        var email = owner;
        if (!await workflow.FlushNoteDraftsAsync()) { RefreshLocalAutosave(); return; }
        if (await RunAsync(() => workflow.ConfirmTeamNote(id)) &&
            string.Equals(owner, email, StringComparison.OrdinalIgnoreCase) && selected == id) RefreshNotes();
        RefreshContext();
        ShowStatus();
    }

    private async void ArchiveNote(object sender, RoutedEventArgs e)
    {
        if (workflow is null || selected is not { } id) return;
        var email = owner;
        if (!await workflow.FlushNoteDraftsAsync()) { RefreshLocalAutosave(); return; }
        var note = workflow.GetTeamNotes(email!, true).Single(n => n.Id == id);
        var ongoing = workflow.TeamNotesView.OngoingNoteId == id;
        if (await RunAsync(() => workflow.ArchiveTeamNote(id, !note.Archived)))
        {
            if (!string.Equals(owner, email, StringComparison.OrdinalIgnoreCase) || selected != id) return;
            if (note.Archived) workflow.OpenOwnedNote(NoteScope.Team, id);
            else if (ongoing) workflow.OpenOngoingNote(NoteScope.Team);
            RefreshNotes();
        }
        RefreshContext();
        ShowStatus();
    }

    private void ChangeArchiveFilter(object sender, RoutedEventArgs e)
    {
        if (loading || workflow is null) return;
        includeArchived = IncludeArchived.IsChecked == true;
        RefreshNotes();
    }
    private async void RetrySave(object sender, RoutedEventArgs e)
    {
        if (workflow is not null && selected is { } id)
        {
            await workflow!.RetryNoteDraftAsync(NoteScope.Team, id);
            RefreshLocalAutosave();
        }
    }
    private void DiscardDraft(object sender, RoutedEventArgs e)
    {
        if (!HasRetainedDraft || workflow is null) return;
        workflow.DiscardUnsavedChanges(selected);
        RefreshNotes();
        RefreshContext();
        ShowStatus();
    }

    public void RefreshExternalWorkspace()
    {
        if (workflow is null || owner is null) return;
        var view = workflow.TeamNotesView;
        if (!HasRetainedDraft &&
            (!view.Notes.Where(note => includeArchived || !note.Archived).SequenceEqual(NotesList.Items.Cast<OwnedNote>()) ||
             selected != view.SelectedNote?.Id || view.IsEditing != (Views.SelectedItem == EditTab) ||
             displayedNote?.Title != view.SelectedNote?.Title || displayedNote?.Markdown != view.SelectedNote?.Markdown))
            RefreshNotes();
        else RefreshLocalAutosave();
        RefreshContext();
    }

    public void DiscardLocalDraft()
    {
        if (HasRetainedDraft) RefreshNotes();
    }

    private void RefreshLocalAutosave()
    {
        if (workflow is null) return;
        var retained = selected is { } id ? workflow.GetRetainedNoteDraft(NoteScope.Team, id) : null;
        var view = workflow.TeamNotesView;
        loading = true;
        var notes = view.Notes.Where(n => includeArchived || !n.Archived).ToArray();
        NotesList.ItemsSource = notes;
        NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == selected);
        loading = false;
        RetryButton.Visibility = DiscardButton.Visibility = HasRetainedDraft ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = DiscardButton.IsEnabled = retained is { IsSaving: false };
        NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = view.IsEditing && workflow.CanEditLocalWorkspace &&
            workflow.Compatibility.SupportsNoteEdits && view.SelectedNote is { Archived: false };
        ArchiveButton.IsEnabled = selected is not null && workflow.CanEditLocalWorkspace && workflow.Compatibility.SupportsNoteEdits;
        ShowStatus();
    }

    private void RefreshContext()
    {
        AutomationProperties.SetHelpText(InheritedReading, "");
        var inherited = workflow is null || owner is null ? [] : workflow.GetInheritedTeamNotes(owner);
        var warnings = inherited.Where(n => n.Note.NeedsReview).ToArray();
        InheritedReviewWarnings.Text = string.Join("\n", warnings.Select(n =>
            $"Needs review: '{n.Note.Title}' - {n.Owner.Heading} ({n.Owner.Email}). Expand Current inherited Team context to read or review."));
        InheritedReviewWarnings.Visibility = warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ContextHeading.Text = "Current inherited Team context";
        ContextAttribution.Text = inherited.Count == 0 ? "No current inherited Team notes. Person notes never inherit." :
            "From " + string.Join("; ", inherited.Select(n => $"{n.Owner.Heading} ({n.Owner.Email})").Distinct()) +
            $". All {inherited.Count} active ancestor notes, not just ongoing writing.";
        InheritedSummary.Text = inherited.Count == 0 ? "No current inherited Team notes." :
            string.Join("\n", inherited.Select(n => $"{n.Note.Title} — {n.Owner.Heading} ({n.Owner.Email}): {n.Note.ReviewStatus}"));
        ReviewOwners.Children.Clear();
        var document = PrivateMarkdownReading.Create("", OpenExternalLink);
        foreach (var context in inherited)
        {
            var section = new Section { Tag = context.Note.Id };
            section.Blocks.Add(new Paragraph(new Bold(new Run($"{context.Note.Title} — owned by {context.Owner.Heading} ({context.Owner.Email})"))));
            section.Blocks.Add(new Paragraph(new Run(context.Note.ReviewStatus)));
            var rendered = PrivateMarkdownReading.Create(context.Note.Markdown, OpenExternalLink);
            while (rendered.Blocks.FirstBlock is { } block)
            {
                rendered.Blocks.Remove(block);
                section.Blocks.Add(block);
            }
            document.Blocks.Add(section);
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = $"Read / review '{context.Note.Title}' on {context.Owner.Heading}'s profile",
                    TextWrapping = TextWrapping.Wrap
                },
                MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2)
            };
            button.SetResourceReference(StyleProperty, "QuietButton");
            AutomationProperties.SetAutomationId(button, "ReviewTeamNote-" + context.Note.Id);
            AutomationProperties.SetName(button, $"Read / review '{context.Note.Title}' - owner {context.Owner.Email}");
            button.Click += (_, _) => OwnerRequested?.Invoke(context.Note.Id);
            ReviewOwners.Children.Add(button);
        }
        InheritedReading.Document = document;
    }

    private void ShowStatus()
    {
        if (workflow is not null) presentation.ShowOperationStatus(workflow, HasRetainedDraft);
        ShowPresentation();
        changed?.Invoke();
    }

    private void ShowPresentation()
    {
        if (workflow is null) return;
        presentation.Show(workflow.TeamNotesView, workflow.CanEditLocalWorkspace && workflow.Compatibility.SupportsNoteEdits, HasRetainedDraft);
        ((MainWindow?)Window.GetWindow(this))?.ShowViewPreferenceStatus();
        var notes = owner is null ? [] : workflow.GetTeamNotes(owner, true);
        var note = notes.FirstOrDefault(n => n.Id == selected);
        ReviewStatus.Text = note?.ReviewStatus ?? "";
        ReviewStatus.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.IsEnabled = workflow.CanEditLocalWorkspace && workflow.Compatibility.SupportsNoteEdits &&
            note is { NeedsReview: true, Archived: false };
        ConfirmButton.Visibility = note is { NeedsReview: true, Archived: false } ? Visibility.Visible : Visibility.Collapsed;
        var warnings = notes.Where(n => n.NeedsReview && !n.Archived).ToArray();
        OwnedReviewWarnings.Text = string.Join("\n", warnings.Select(n =>
            $"Needs review: '{n.Title}' - owned by {n.OwnerEmail}. Open the note to confirm, edit or archive."));
        OwnedReviewWarnings.Visibility = warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChangeOtherNotes(object sender, RoutedEventArgs e)
    {
        if (!loading) workflow?.ShowOtherNotes(NoteScope.Team, OtherNotes.IsExpanded);
    }

    private void ChangeNoteMode(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && selected is { } id && e.Source == Views)
        {
            workflow?.OpenOwnedNote(NoteScope.Team, id, editing: Views.SelectedItem == EditTab);
            if (Views.SelectedItem == EditTab) LoadNote(workflow?.TeamNotesView.SelectedNote);
            ShowPresentation();
        }
    }

    private void OpenOngoing(object sender, RoutedEventArgs e)
    {
        if (workflow is null) return;
        workflow.OpenOngoingNote(NoteScope.Team);
        RefreshNotes();
    }

    private async void DesignateOngoing(object sender, RoutedEventArgs e)
    {
        if (workflow is null || selected is not { } id) return;
        if (await workflow.DesignateOngoingNoteAsync(NoteScope.Team, id, operation => RunAsync(operation))) RefreshNotes();
        ShowPresentation();
        changed?.Invoke();
    }
    private void OpenExternalLink(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { presentation.ShowOperationStatus("External link could not be opened: " + ex.Message); }
    }
}
