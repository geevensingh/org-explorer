using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class PersonNotesControl : UserControl
{
    private IApplicationWorkflow? workflow;
    private Action? changed;
    private string? owner;
    private Guid? selected;
    private bool loading;
    private readonly NoteDraftState draft;
    private NoteDraft? noteBase;

    public PersonNotesControl()
    {
        InitializeComponent();
        draft = new NoteDraftState("Person note", SaveNoteDraft,
            () => ((MainWindow)Window.GetWindow(this)).HasPendingWork,
            () => ((MainWindow)Window.GetWindow(this)).MarkDraftPending(),
            message => NoteStatus.Text = message,
            message => ((MainWindow)Window.GetWindow(this)).ShowNavigationBlocked(message));
        Unloaded += (_, _) => draft.Stop();
    }
    public void Attach(IApplicationWorkflow application, Action onChanged)
    {
        workflow = application;
        changed = onChanged;
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window)
                window.Closing += (_, e) => { if (!TryLeave()) e.Cancel = true; };
        };
    }

    public bool TryLeave() => draft.TryLeave();

    public void OpenPerson(string? email)
    {
        if (owner == email) return;
        if (!TryLeave()) return;
        owner = email;
        selected = null;
        Ownership.Text = email is null ? "Select a Person." : $"Owned by {email}. Not inherited by reports. No sharing.";
        NewButton.IsEnabled = email is not null;
        RefreshNotes();
    }

    private void RefreshNotes(bool preserveEditing = true)
    {
        loading = true;
        var notes = workflow is null || owner is null ? [] : workflow.GetPersonNotes(owner, IncludeArchived.IsChecked == true);
        NotesList.ItemsSource = notes;
        NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == selected);
        EmptyNotes.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyNotes.Text = IncludeArchived.IsChecked == true
            ? "No Person notes, including archived notes. New Person note opens directly for writing."
            : "No active Person notes. New Person note opens directly for writing; Show archived notes to find retained writing.";
        loading = false;
        LoadNote(NotesList.SelectedItem as PersonNote, preserveEditing);
    }

    public void OpenNote(Guid id)
    {
        if (!TryLeave() || workflow is null || owner is null) return;
        selected = id;
        IncludeArchived.IsChecked = workflow.GetPersonNotes(owner, true).Any(n => n.Id == id && n.Archived);
        RefreshNotes(preserveEditing: false);
    }

    private void LoadNote(PersonNote? note, bool preserveEditing = false)
    {
        var editing = preserveEditing && selected == note?.Id && Views.SelectedItem == EditTab;
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
        draft.Reset();
        RetryButton.IsEnabled = DiscardButton.IsEnabled = false;
        Render();
        loading = false;
    }

    private void SelectNote(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (!TryLeave())
        {
            loading = true;
            NotesList.SelectedItem = NotesList.Items.Cast<PersonNote>().FirstOrDefault(n => n.Id == selected);
            loading = false;
            return;
        }
        LoadNote(NotesList.SelectedItem as PersonNote);
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
            selected = note.Id;
            IncludeArchived.IsChecked = false;
            RefreshNotes();
            Views.SelectedItem = EditTab;
        }
        ShowStatus();
    }

    private void EditNote(object sender, TextChangedEventArgs e)
    {
        if (loading || workflow is null || selected is not { } id) return;
        if (!draft.HasUnsavedChanges && noteBase is { } basis) workflow.RetainNoteDraft(basis);
        draft.Edited();
        NoteHeading.Text = NoteTitle.Text;
        Render();
    }

    private async void SaveNoteDraft(object? sender, EventArgs e) => await SaveNoteDraftAsync();

    public Task<bool> FlushPendingDraftAsync() => draft.FlushPendingAsync(SaveNoteDraftAsync);

    private async Task SaveNoteDraftAsync()
    {
        if (workflow is null || selected is not { } id || noteBase is not { } basis) return;
        if (!draft.TryBeginSave()) return;
        var title = NoteTitle.Text;
        var markdown = MarkdownEditor.Text;
        var accepted = await RunAsync(() => workflow.SaveNoteDraft(basis, title, markdown), draft: true);
        var saved = accepted is not null;
        if (saved) noteBase = accepted;
        draft.CompleteSave(saved, title, markdown, NoteTitle.Text, MarkdownEditor.Text);
        if (saved && draft.HasUnsavedChanges) workflow.RetainNoteDraft(accepted!);
        RetryButton.IsEnabled = DiscardButton.IsEnabled = draft.HasUnsavedChanges;
        if (!draft.HasUnsavedChanges)
        {
            loading = true;
            var notes = workflow.GetPersonNotes(owner!, IncludeArchived.IsChecked == true);
            NotesList.ItemsSource = notes;
            NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == id);
            loading = false;
        }
        Render();
        ShowStatus();
        draft.ResumeNewerEdits(saved);
    }

    private async void ArchiveNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        var note = workflow.GetPersonNotes(owner!, true).Single(n => n.Id == id);
        if (await RunAsync(() => workflow.ArchivePersonNote(id, !note.Archived))) RefreshNotes(preserveEditing: false);
        ShowStatus();
    }

    private void ChangeArchiveFilter(object sender, RoutedEventArgs e)
    {
        if (loading || workflow is null) return;
        if (TryLeave()) RefreshNotes();
    }

    private void RetrySave(object sender, RoutedEventArgs e) => SaveNoteDraft(null, EventArgs.Empty);
    private async void DiscardDraft(object sender, RoutedEventArgs e)
    {
        if (!draft.HasUnsavedChanges || workflow is null) return;
        await draft.DiscardAsync(() => RunAsync(workflow.RefreshPersonNotes),
            () => workflow.DiscardUnsavedChanges(selected), () => RefreshNotes(preserveEditing: false), () => workflow.Status);
        changed?.Invoke();
    }

    public void RefreshExternalWorkspace()
    {
        if (workflow is null || owner is null) return;
        if (!draft.HasUnsavedChanges)
        {
            var notes = workflow.GetPersonNotes(owner, IncludeArchived.IsChecked == true);
            if (!notes.SequenceEqual(NotesList.Items.Cast<PersonNote>())) RefreshNotes();
        }
        NewButton.IsEnabled = workflow.IsWorkspaceCurrent;
        if (!draft.HasUnsavedChanges)
        {
            var editable = workflow.IsWorkspaceCurrent && NotesList.SelectedItem is PersonNote { Archived: false };
            NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = editable;
            ArchiveButton.IsEnabled = workflow.IsWorkspaceCurrent && selected is not null;
        }
        NoteStatus.Text = workflow.Status;
    }

    public void DiscardLocalDraft()
    {
        if (draft.HasUnsavedChanges) RefreshNotes();
    }

    private void ShowStatus()
    {
        NoteStatus.Text = workflow?.Status ?? "";
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
