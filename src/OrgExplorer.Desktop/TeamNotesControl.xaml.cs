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
    private bool loading;
    private bool includeArchived;
    private readonly NoteDraftState draft;
    private readonly OwnedNotePresentation presentation;
    private NoteDraft? noteBase;
    public event Action<Guid>? OwnerRequested;

    public TeamNotesControl()
    {
        InitializeComponent();
        presentation = new(NoteScope.Team)
        {
            Status = OngoingStatus, Kind = NoteKind, Heading = NoteHeading,
            Designate = DesignateButton, Ongoing = OngoingButton
        };
        draft = new NoteDraftState("Team note", SaveNoteDraft,
            () => ((MainWindow)Window.GetWindow(this)).HasPendingWork,
            () => ((MainWindow)Window.GetWindow(this)).MarkDraftPending(),
            message => NoteStatus.Text = message,
            message => ((MainWindow)Window.GetWindow(this)).ShowNavigationBlocked(message));
        Unloaded += (_, _) => draft.Stop();
    }

    public void Attach(WorkspaceViewModel application, Action onChanged)
    {
        workflow = application;
        changed = onChanged;
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window)
                window.Closing += (_, e) => { if (!TryLeave()) e.Cancel = true; };
        };
    }

    public bool TryLeave()
    {
        if (workflow is { } current && current.GetWorkspaceConflicts().Count != 0)
        {
            NoteStatus.Text = current.Status;
            ((MainWindow)Window.GetWindow(this)).ShowNavigationBlocked(current.Status);
            return false;
        }
        return draft.TryLeave();
    }

    public void OpenPerson(string? email)
    {
        if (!string.Equals(owner, email, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryLeave()) return;
            if (workflow?.OpenOwnedNotes(NoteScope.Team, email) != true) return;
            owner = email;
            loading = true;
            IncludeArchived.IsChecked = includeArchived = false;
            OtherNotes.IsExpanded = CurrentTeamContext.IsExpanded = false;
            loading = false;
        }
        Ownership.Text = email is null ? "Select a Person." :
            $"Owned by {email}; applies to current formal descendants. Ownership stays with this Person, not a permanent team. No sharing.";
        NewButton.IsEnabled = email is not null;
        if (!draft.HasUnsavedChanges) RefreshNotes();
        RefreshContext();
    }

    public void OpenNote(Guid id)
    {
        if (!TryLeave() || workflow is null || owner is null) return;
        if (!workflow.OpenOwnedNote(NoteScope.Team, id)) { ShowPresentation(); return; }
        loading = true;
        IncludeArchived.IsChecked = includeArchived = workflow.TeamNotesView.SelectedNote?.Archived == true;
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
        EmptyNotes.Visibility = owner is not null && !view.Notes.Any(n => !n.Archived) ? Visibility.Visible : Visibility.Collapsed;
        EmptyNotes.Text = "No active owned Team notes. Write a new Team note to begin; Other notes retains archived writing.";
        loading = false;
        LoadNote(view.SelectedNote);
        ShowPresentation();
    }

    private void LoadNote(OwnedNote? note)
    {
        var editing = workflow?.TeamNotesView.IsEditing == true;
        draft.Stop();
        loading = true;
        selected = note?.Id;
        noteBase = note is null ? null : workflow?.BeginNoteDraft(NoteScope.Team, note.OwnerEmail, note.Id);
        NoteTitle.Text = noteBase?.Title ?? "";
        MarkdownEditor.Text = noteBase?.Markdown ?? "";
        NoteHeading.Text = noteBase?.Title ?? "";
        Views.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        Views.SelectedItem = editing ? EditTab : ReadTab;
        NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = noteBase is not null && note is { Archived: false };
        ArchiveButton.IsEnabled = note is not null;
        ArchiveButton.Content = note?.Archived == true ? "Restore Team note" : "Archive Team note";
        ArchiveButton.Visibility = ConfirmButton.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        draft.Reset();
        RetryButton.IsEnabled = DiscardButton.IsEnabled = false;
        RetryButton.Visibility = DiscardButton.Visibility = Visibility.Collapsed;
        Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
        loading = false;
    }

    private void SelectNote(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (!TryLeave())
        {
            loading = true;
            NotesList.SelectedItem = NotesList.Items.Cast<OwnedNote>().FirstOrDefault(n => n.Id == selected);
            loading = false;
            return;
        }
        if (NotesList.SelectedItem is OwnedNote note) OpenNote(note.Id);
    }

    private Task<T> RunAsync<T>(Func<T> operation, bool draft = false) =>
        ((MainWindow)Window.GetWindow(this)).RunWorkflowAsync(operation, "Saving private Team note…", noteDraft: draft ? this : null);

    private async void CreateNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || owner is null) return;
        var email = owner;
        var note = await RunAsync(() => workflow.CreateTeamNote(email, "New Team note", ""));
        if (note is not null)
        {
            workflow.OpenOwnedNote(NoteScope.Team, note.Id, editing: true);
            loading = true;
            IncludeArchived.IsChecked = includeArchived = false;
            loading = false;
            RefreshNotes();
        }
        ShowStatus();
    }

    private void EditNote(object sender, TextChangedEventArgs e)
    {
        if (loading || workflow is null || selected is not { } id) return;
        if (!draft.HasUnsavedChanges && noteBase is { } basis) workflow.RetainNoteDraft(basis);
        draft.Edited();
        RetryButton.Visibility = DiscardButton.Visibility = Visibility.Visible;
        NoteHeading.Text = NoteTitle.Text;
        Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
    }

    private async void SaveNoteDraft(object? sender, EventArgs e) => await SaveNoteDraftAsync();

    public Task<bool> FlushPendingDraftAsync() => draft.FlushPendingAsync(SaveNoteDraftAsync);

    private async Task SaveNoteDraftAsync()
    {
        if (workflow is null || selected is not { } id || noteBase is not { } basis) return;
        if (workflow.GetWorkspaceConflicts().Count != 0) { draft.Stop(); ShowStatus(); return; }
        if (!draft.TryBeginSave()) return;
        var title = NoteTitle.Text;
        var markdown = MarkdownEditor.Text;
        var accepted = await RunAsync(() => workflow.SaveNoteDraft(basis, title, markdown), draft: true);
        var saved = accepted is not null;
        if (saved) noteBase = accepted;
        draft.CompleteSave(saved, title, markdown, NoteTitle.Text, MarkdownEditor.Text);
        if (saved && draft.HasUnsavedChanges) workflow.RetainNoteDraft(accepted!);
        RetryButton.IsEnabled = DiscardButton.IsEnabled = draft.HasUnsavedChanges;
        RetryButton.Visibility = DiscardButton.Visibility = draft.HasUnsavedChanges ? Visibility.Visible : Visibility.Collapsed;
        if (!draft.HasUnsavedChanges)
        {
            loading = true;
            var notes = workflow.TeamNotesView.Notes.Where(n => includeArchived || !n.Archived).ToArray();
            NotesList.ItemsSource = notes;
            NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == id);
            loading = false;
        }
        Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
        RefreshContext();
        ShowStatus();
        draft.ResumeNewerEdits(saved && workflow.GetWorkspaceConflicts().Count == 0);
    }

    private async void ConfirmNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        if (await RunAsync(() => workflow.ConfirmTeamNote(id))) RefreshNotes();
        RefreshContext();
        ShowStatus();
    }

    private async void ArchiveNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        var note = workflow.GetTeamNotes(owner!, true).Single(n => n.Id == id);
        var ongoing = workflow.TeamNotesView.OngoingNoteId == id;
        if (await RunAsync(() => workflow.ArchiveTeamNote(id, !note.Archived)))
        {
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
        if (TryLeave())
        {
            includeArchived = IncludeArchived.IsChecked == true;
            RefreshNotes();
        }
        else
        {
            loading = true;
            IncludeArchived.IsChecked = includeArchived;
            loading = false;
        }
    }
    private void RetrySave(object sender, RoutedEventArgs e) => SaveNoteDraft(null, EventArgs.Empty);
    private async void DiscardDraft(object sender, RoutedEventArgs e)
    {
        if (!draft.HasUnsavedChanges || workflow is null) return;
        await draft.DiscardAsync(() => RunAsync(workflow.RefreshTeamNotes),
            () => workflow.DiscardUnsavedChanges(selected), () =>
            {
                if (selected is { } id) workflow.OpenOwnedNote(NoteScope.Team, id);
                RefreshNotes();
                RefreshContext();
            }, () => workflow.Status);
        changed?.Invoke();
    }

    public void RefreshExternalWorkspace()
    {
        if (workflow is null || owner is null) return;
        if (!draft.HasUnsavedChanges)
        {
            var notes = workflow.TeamNotesView.Notes.Where(n => includeArchived || !n.Archived);
            if (!notes.SequenceEqual(NotesList.Items.Cast<OwnedNote>()) ||
                selected != workflow.TeamNotesView.SelectedNote?.Id ||
                workflow.TeamNotesView.IsEditing != (Views.SelectedItem == EditTab)) RefreshNotes();
            else if (workflow.IsWorkspaceCurrent && selected is { } id)
                noteBase = workflow.BeginNoteDraft(NoteScope.Team, owner, id);
        }
        RefreshContext();
        NewButton.IsEnabled = workflow.IsWorkspaceCurrent;
        if (!draft.HasUnsavedChanges)
        {
            NoteTitle.IsEnabled = MarkdownEditor.IsEnabled =
                workflow.IsWorkspaceCurrent && workflow.TeamNotesView.SelectedNote is { Archived: false };
            ArchiveButton.IsEnabled = workflow.IsWorkspaceCurrent && selected is not null;
        }
        if (!draft.HasUnsavedChanges) NoteStatus.Text = workflow.Status;
        ShowPresentation();
    }

    public void DiscardLocalDraft()
    {
        if (draft.HasUnsavedChanges) RefreshNotes();
    }

    private void RefreshContext()
    {
        var inherited = workflow is null || owner is null ? [] : workflow.GetInheritedTeamNotes(owner);
        var warnings = inherited.Where(n => n.Note.NeedsReview).ToArray();
        InheritedReviewWarnings.Text = string.Join("\n", warnings.Select(n =>
            $"Needs review: '{n.Note.Title}' - {n.Owner.Heading} ({n.Owner.Email}). Expand Current inherited Team context to read or review."));
        InheritedReviewWarnings.Visibility = warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ContextHeading.Text = $"Current inherited Team context - {inherited.Count} notes from formal ancestors";
        InheritedSummary.Text = inherited.Count == 0 ? "No current inherited Team notes." :
            string.Join("\n", inherited.Select(n => $"{n.Note.Title} — {n.Owner.Heading} ({n.Owner.Email}): {n.Note.ReviewStatus}"));
        ReviewOwners.Children.Clear();
        var document = PrivateMarkdownReading.Create("", OpenExternalLink);
        foreach (var context in inherited)
        {
            document.Blocks.Add(new Paragraph(new Bold(new Run($"{context.Note.Title} — owned by {context.Owner.Heading} ({context.Owner.Email})"))));
            document.Blocks.Add(new Paragraph(new Run(context.Note.ReviewStatus)));
            var rendered = PrivateMarkdownReading.Create(context.Note.Markdown, OpenExternalLink);
            while (rendered.Blocks.FirstBlock is { } block)
            {
                rendered.Blocks.Remove(block);
                document.Blocks.Add(block);
            }
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = $"Read / review '{context.Note.Title}' on {context.Owner.Heading}'s profile",
                    TextWrapping = TextWrapping.Wrap
                },
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2)
            };
            AutomationProperties.SetAutomationId(button, "ReviewTeamNote-" + context.Note.Id);
            AutomationProperties.SetName(button, $"Read / review '{context.Note.Title}' - owner {context.Owner.Email}");
            button.Click += (_, _) => OwnerRequested?.Invoke(context.Note.Id);
            ReviewOwners.Children.Add(button);
        }
        InheritedReading.Document = document;
    }

    private void ShowStatus()
    {
        NoteStatus.Text = workflow?.Status ?? "";
        ShowPresentation();
        changed?.Invoke();
    }

    private void ShowPresentation()
    {
        if (workflow is null) return;
        presentation.Show(workflow.TeamNotesView, workflow.IsWorkspaceCurrent);
        ((MainWindow?)Window.GetWindow(this))?.ShowViewPreferenceStatus();
        var notes = owner is null ? [] : workflow.GetTeamNotes(owner, true);
        var note = notes.FirstOrDefault(n => n.Id == selected);
        ReviewStatus.Text = note?.ReviewStatus ?? "";
        ConfirmButton.IsEnabled = workflow.IsWorkspaceCurrent && note is { NeedsReview: true, Archived: false };
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
            workflow?.OpenOwnedNote(NoteScope.Team, id, editing: Views.SelectedItem == EditTab);
    }

    private void OpenOngoing(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null) return;
        workflow.OpenOngoingNote(NoteScope.Team);
        RefreshNotes();
    }

    private async void DesignateOngoing(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        if (await workflow.DesignateOngoingNoteAsync(NoteScope.Team, id, operation => RunAsync(operation))) RefreshNotes();
        ShowPresentation();
        changed?.Invoke();
    }
    private void OpenExternalLink(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { NoteStatus.Text = "External link could not be opened: " + ex.Message; }
    }
}
