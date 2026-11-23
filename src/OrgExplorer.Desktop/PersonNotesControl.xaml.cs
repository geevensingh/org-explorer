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
    private OwnedNote? displayedNote;
    private bool loading;
    private bool includeArchived;
    private readonly OwnedNotePresentation presentation;
    private bool HasRetainedDraft => selected is { } id && workflow?.HasUnsavedNoteDraft(NoteScope.Person, id) == true;
    internal bool HasUnsavedChanges => HasRetainedDraft;

    public PersonNotesControl()
    {
        InitializeComponent();
        presentation = new(NoteScope.Person)
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
        if (string.Equals(owner, email, StringComparison.OrdinalIgnoreCase))
        {
            if (workflow is not null) presentation.ShowOperationStatus(workflow, HasRetainedDraft);
            return;
        }
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
        if (workflow is null || owner is null) return;
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
        loading = true;
        selected = note?.Id;
        var retained = note is null ? null : workflow?.GetRetainedNoteDraft(NoteScope.Person, note.Id);
        var noteBase = retained?.Basis ?? (note is null ? null : workflow?.BeginNoteDraft(NoteScope.Person, note.OwnerEmail, note.Id));
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
        ArchiveButton.Content = note?.Archived == true ? "Restore note" : "Archive note";
        ArchiveButton.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.IsEnabled = DiscardButton.IsEnabled = false;
        RetryButton.Visibility = DiscardButton.Visibility = Visibility.Collapsed;
        Render();
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
        if (workflow.CreateOwnedNoteDraft(NoteScope.Person, owner) is { } created)
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
        workflow.UpdateNoteDraft(NoteScope.Person, id, NoteTitle.Text, MarkdownEditor.Text);
        displayedNote = displayedNote is { } current
            ? current with { Title = NoteTitle.Text, Markdown = MarkdownEditor.Text }
            : throw new InvalidOperationException("The selected Person editor has no displayed note.");
        NoteHeading.Text = NoteTitle.Text;
        Render();
        RefreshLocalAutosave();
    }

    public Task<bool> FlushPendingDraftAsync() => workflow!.FlushNoteDraftsAsync();

    private async void ArchiveNote(object sender, RoutedEventArgs e)
    {
        if (workflow is null || selected is not { } id) return;
        var email = owner;
        if (!await workflow.FlushNoteDraftsAsync()) { RefreshLocalAutosave(); return; }
        var note = workflow.GetPersonNotes(email!, true).Single(n => n.Id == id);
        var ongoing = workflow.PersonNotesView.OngoingNoteId == id;
        if (await RunAsync(() => workflow.ArchivePersonNote(id, !note.Archived)))
        {
            if (!string.Equals(owner, email, StringComparison.OrdinalIgnoreCase) || selected != id) return;
            if (note.Archived) workflow.OpenOwnedNote(NoteScope.Person, id);
            else if (ongoing) workflow.OpenOngoingNote(NoteScope.Person);
            RefreshNotes();
        }
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
            await workflow!.RetryNoteDraftAsync(NoteScope.Person, id);
            RefreshLocalAutosave();
        }
    }
    private void DiscardDraft(object sender, RoutedEventArgs e)
    {
        if (!HasRetainedDraft || workflow is null) return;
        workflow.DiscardUnsavedChanges(selected);
        RefreshNotes();
        ShowStatus();
    }

    public void RefreshExternalWorkspace()
    {
        if (workflow is null || owner is null) return;
        var view = workflow.PersonNotesView;
        if (!HasRetainedDraft &&
            (!view.Notes.Where(note => includeArchived || !note.Archived).SequenceEqual(NotesList.Items.Cast<OwnedNote>()) ||
             selected != view.SelectedNote?.Id || view.IsEditing != (Views.SelectedItem == EditTab) ||
             displayedNote?.Title != view.SelectedNote?.Title || displayedNote?.Markdown != view.SelectedNote?.Markdown))
            RefreshNotes();
        else RefreshLocalAutosave();
    }

    public void DiscardLocalDraft()
    {
        if (HasRetainedDraft) RefreshNotes();
    }

    private void RefreshLocalAutosave()
    {
        if (workflow is null) return;
        var retained = selected is { } id ? workflow.GetRetainedNoteDraft(NoteScope.Person, id) : null;
        var view = workflow.PersonNotesView;
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

    private void ShowStatus()
    {
        if (workflow is not null) presentation.ShowOperationStatus(workflow, HasRetainedDraft);
        ShowPresentation();
        changed?.Invoke();
    }

    private void ShowPresentation()
    {
        if (workflow is null) return;
        presentation.Show(workflow.PersonNotesView, workflow.CanEditLocalWorkspace && workflow.Compatibility.SupportsNoteEdits, HasRetainedDraft);
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
            if (Views.SelectedItem == EditTab) LoadNote(workflow?.PersonNotesView.SelectedNote);
            ShowPresentation();
        }
    }

    private void OpenOngoing(object sender, RoutedEventArgs e)
    {
        if (workflow is null) return;
        workflow.OpenOngoingNote(NoteScope.Person);
        RefreshNotes();
    }

    private async void DesignateOngoing(object sender, RoutedEventArgs e)
    {
        if (workflow is null || selected is not { } id) return;
        if (await workflow.DesignateOngoingNoteAsync(NoteScope.Person, id, operation => RunAsync(operation)))
            RefreshNotes();
        ShowPresentation();
        changed?.Invoke();
    }
    private void Render() => Reading.Document = PrivateMarkdownReading.Create(displayedNote?.Markdown ?? "", OpenExternalLink);
    private void OpenExternalLink(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            presentation.ShowOperationStatus("External link could not be opened: " + ex.Message);
        }
    }
}
