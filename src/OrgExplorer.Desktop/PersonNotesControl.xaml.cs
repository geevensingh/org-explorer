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
    private bool unsaved;

    public PersonNotesControl()
    {
        InitializeComponent();
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

    public bool TryLeave()
    {
        if (!unsaved) return true;
        NoteStatus.Text = "Unsaved Person note. Resolve the local save error and retry before leaving or closing.";
        return false;
    }

    public void OpenPerson(string? email)
    {
        if (owner == email) return;
        owner = email;
        selected = null;
        Ownership.Text = email is null ? "Select a Person." : $"Owned by {email}. Not inherited by reports. No sharing.";
        NewButton.IsEnabled = email is not null;
        RefreshNotes();
    }

    private void RefreshNotes()
    {
        loading = true;
        var notes = workflow is null || owner is null ? [] : workflow.GetPersonNotes(owner, IncludeArchived.IsChecked == true);
        NotesList.ItemsSource = notes;
        NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == selected);
        loading = false;
        LoadNote(NotesList.SelectedItem as PersonNote);
    }

    private void LoadNote(PersonNote? note)
    {
        loading = true;
        selected = note?.Id;
        NoteTitle.Text = note?.Title ?? "";
        MarkdownEditor.Text = note?.Markdown ?? "";
        NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = note is not null && !note.Archived;
        ArchiveButton.IsEnabled = note is not null;
        ArchiveButton.Content = note?.Archived == true ? "Restore note" : "Archive note";
        unsaved = false;
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

    private void CreateNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || owner is null) return;
        var note = workflow.CreatePersonNote(owner, "New Person note", "");
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
        unsaved = !workflow.EditPersonNote(id, NoteTitle.Text, MarkdownEditor.Text);
        RetryButton.IsEnabled = DiscardButton.IsEnabled = unsaved;
        if (!unsaved)
        {
            loading = true;
            var notes = workflow.GetPersonNotes(owner!, IncludeArchived.IsChecked == true);
            NotesList.ItemsSource = notes;
            NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == id);
            loading = false;
        }
        Render();
        ShowStatus();
    }

    private void ArchiveNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        var note = workflow.GetPersonNotes(owner!, true).Single(n => n.Id == id);
        if (workflow.ArchivePersonNote(id, !note.Archived)) RefreshNotes();
        ShowStatus();
    }

    private void ChangeArchiveFilter(object sender, RoutedEventArgs e)
    {
        if (loading || workflow is null) return;
        if (TryLeave()) RefreshNotes();
    }

    private void RetrySave(object sender, RoutedEventArgs e) => EditNote(sender, null!);
    private void DiscardDraft(object sender, RoutedEventArgs e)
    {
        if (!unsaved || workflow is null) return;
        var refreshed = workflow.RefreshPersonNotes();
        workflow.DiscardUnsavedChanges(selected);
        RefreshNotes();
        NoteStatus.Text = refreshed
            ? "Unsaved draft discarded; last saved local document shown."
            : "Unsaved draft discarded. " + workflow.Status;
        changed?.Invoke();
    }

    public void RefreshExternalWorkspace()
    {
        if (workflow is null || owner is null) return;
        if (!unsaved)
        {
            var notes = workflow.GetPersonNotes(owner, IncludeArchived.IsChecked == true);
            if (!notes.SequenceEqual(NotesList.Items.Cast<PersonNote>())) RefreshNotes();
        }
        NewButton.IsEnabled = workflow.IsWorkspaceCurrent;
        if (!unsaved)
        {
            var editable = workflow.IsWorkspaceCurrent && NotesList.SelectedItem is PersonNote { Archived: false };
            NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = editable;
            ArchiveButton.IsEnabled = workflow.IsWorkspaceCurrent && selected is not null;
        }
        NoteStatus.Text = workflow.Status;
    }

    public void DiscardLocalDraft()
    {
        if (unsaved) RefreshNotes();
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
