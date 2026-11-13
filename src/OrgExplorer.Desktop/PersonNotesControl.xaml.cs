using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class PersonNotesControl : UserControl
{
    internal FlowDocumentScrollViewer NoteReading => Reading;
    private WorkspaceViewModel? workflow;
    private Action? changed;
    private string? owner;
    private Guid? selected;
    private bool loading;
    private bool includeArchived;
    private readonly NoteDraftState draft;
    private readonly OwnedNotePresentation presentation;
    private NoteDraft? noteBase;

    public PersonNotesControl()
    {
        InitializeComponent();
        presentation = new(NoteScope.Person)
        {
            Status = OngoingStatus, Kind = NoteKind, Heading = NoteHeading,
            Designate = DesignateButton, Ongoing = OngoingButton,
            New = NewButton, Empty = EmptyNotes, EmptyBody = EmptyBody
        };
        draft = new NoteDraftState("Person note", SaveNoteDraft,
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
        if (string.Equals(owner, email, StringComparison.OrdinalIgnoreCase)) return;
        if (!TryLeave()) return;
        if (workflow?.OpenOwnedNotes(NoteScope.Person, email) != true) return;
        owner = email;
        loading = true;
        IncludeArchived.IsChecked = includeArchived = false;
        loading = false;
        Ownership.Text = email is null ? "Select a Person." : $"Owned by {email}. Not inherited by reports. No sharing.";
        NewButton.IsEnabled = email is not null;
        RefreshNotes();
    }

    private void RefreshNotes()
    {
        if (workflow is null) return;
        var view = workflow.PersonNotesView;
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

    public void OpenNote(Guid id)
    {
        if (!TryLeave() || workflow is null || owner is null) return;
        if (!workflow.OpenOwnedNote(NoteScope.Person, id)) { ShowPresentation(); return; }
        loading = true;
        if (workflow.PersonNotesView.SelectedNote?.Archived == true)
            IncludeArchived.IsChecked = includeArchived = true;
        if (includeArchived) workflow.ShowOtherNotes(NoteScope.Person, true);
        loading = false;
        RefreshNotes();
    }

    private void LoadNote(OwnedNote? note)
    {
        var editing = workflow?.PersonNotesView.IsEditing == true;
        draft.Stop();
        loading = true;
        selected = note?.Id;
        noteBase = note is null ? null : workflow?.BeginNoteDraft(NoteScope.Person, note.OwnerEmail, note.Id);
        NoteTitle.Text = noteBase?.Title ?? "";
        MarkdownEditor.Text = noteBase?.Markdown ?? "";
        NoteHeading.Text = noteBase?.Title ?? "";
        Views.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        Views.SelectedItem = editing ? EditTab : ReadTab;
        NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = noteBase is not null && note is { Archived: false };
        ArchiveButton.IsEnabled = note is not null;
        ArchiveButton.Content = note?.Archived == true ? "Restore note" : "Archive note";
        ArchiveButton.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        draft.Reset();
        RetryButton.IsEnabled = DiscardButton.IsEnabled = false;
        RetryButton.Visibility = DiscardButton.Visibility = Visibility.Collapsed;
        Render();
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
        ((MainWindow)Window.GetWindow(this)).RunWorkflowAsync(operation, "Saving private Person note…", noteDraft: draft ? this : null);

    private async void CreateNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || owner is null) return;
        var email = owner;
        var note = await RunAsync(() => workflow.CreatePersonNote(email, "New Person note", ""));
        if (note is not null)
        {
            workflow.OpenOwnedNote(NoteScope.Person, note.Id, editing: true);
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
        Render();
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
            var notes = workflow.PersonNotesView.Notes.Where(n => includeArchived || !n.Archived).ToArray();
            NotesList.ItemsSource = notes;
            NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == id);
            loading = false;
        }
        Render();
        ShowStatus();
        draft.ResumeNewerEdits(saved && workflow.GetWorkspaceConflicts().Count == 0);
    }

    private async void ArchiveNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        var note = workflow.GetPersonNotes(owner!, true).Single(n => n.Id == id);
        var ongoing = workflow.PersonNotesView.OngoingNoteId == id;
        if (await RunAsync(() => workflow.ArchivePersonNote(id, !note.Archived)))
        {
            if (note.Archived) workflow.OpenOwnedNote(NoteScope.Person, id);
            else if (ongoing) workflow.OpenOngoingNote(NoteScope.Person);
            RefreshNotes();
        }
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
        await draft.DiscardAsync(() => RunAsync(workflow.RefreshPersonNotes),
            () => workflow.DiscardUnsavedChanges(selected), () =>
            {
                if (selected is { } id) workflow.OpenOwnedNote(NoteScope.Person, id);
                RefreshNotes();
            }, () => workflow.Status);
        changed?.Invoke();
    }

    public void RefreshExternalWorkspace()
    {
        if (workflow is null || owner is null) return;
        if (!draft.HasUnsavedChanges)
        {
            var notes = workflow.PersonNotesView.Notes.Where(n => includeArchived || !n.Archived);
            if (!notes.SequenceEqual(NotesList.Items.Cast<OwnedNote>()) ||
                selected != workflow.PersonNotesView.SelectedNote?.Id ||
                workflow.PersonNotesView.IsEditing != (Views.SelectedItem == EditTab)) RefreshNotes();
            else if (workflow.IsWorkspaceCurrent && selected is { } id)
                noteBase = workflow.BeginNoteDraft(NoteScope.Person, owner, id);
        }
        NewButton.IsEnabled = workflow.IsWorkspaceCurrent;
        if (!draft.HasUnsavedChanges)
        {
            var editable = workflow.IsWorkspaceCurrent && workflow.PersonNotesView.SelectedNote is { Archived: false };
            NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = editable;
            ArchiveButton.IsEnabled = workflow.IsWorkspaceCurrent && selected is not null;
        }
        if (!draft.HasUnsavedChanges) NoteStatus.Text = workflow.Status;
        ShowPresentation();
    }

    public void DiscardLocalDraft()
    {
        if (draft.HasUnsavedChanges) RefreshNotes();
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
        presentation.Show(workflow.PersonNotesView, workflow.IsWorkspaceCurrent, draft.HasUnsavedChanges);
        ((MainWindow?)Window.GetWindow(this))?.ShowViewPreferenceStatus();
    }

    private void ChangeOtherNotes(object sender, RoutedEventArgs e)
    {
        if (!loading) workflow?.ShowOtherNotes(NoteScope.Person, OtherNotes.IsExpanded);
    }

    private void ChangeNoteMode(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && selected is { } id && e.Source == Views)
        {
            workflow?.OpenOwnedNote(NoteScope.Person, id, editing: Views.SelectedItem == EditTab);
            ShowPresentation();
        }
    }

    private void OpenOngoing(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null) return;
        workflow.OpenOngoingNote(NoteScope.Person);
        RefreshNotes();
    }

    private async void DesignateOngoing(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        if (await workflow.DesignateOngoingNoteAsync(NoteScope.Person, id, operation => RunAsync(operation)))
            RefreshNotes();
        ShowPresentation();
        changed?.Invoke();
    }
    private void Render() => Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
    private void OpenExternalLink(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            NoteStatus.Text = "External link could not be opened: " + ex.Message;
        }
    }
}
