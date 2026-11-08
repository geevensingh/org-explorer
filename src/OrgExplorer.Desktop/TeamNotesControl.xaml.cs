using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class TeamNotesControl : UserControl
{
    private IApplicationWorkflow? workflow;
    private Action? changed;
    private string? owner;
    private Guid? selected;
    private bool loading;
    private bool unsaved;
    public event Action<string, Guid>? OwnerRequested;

    public TeamNotesControl()
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
        NoteStatus.Text = "Unsaved Team note. Resolve the local save error and retry before leaving or closing.";
        return false;
    }

    public void OpenPerson(string? email)
    {
        if (!string.Equals(owner, email, StringComparison.OrdinalIgnoreCase))
        {
            owner = email;
            selected = null;
        }
        Ownership.Text = email is null ? "Select a Person." :
            $"Owned by {email}; applies to current formal descendants. Ownership stays with this Person, not a permanent team. No sharing.";
        NewButton.IsEnabled = email is not null;
        if (!unsaved) RefreshNotes();
        RefreshContext();
    }

    public void OpenNote(Guid id)
    {
        if (!TryLeave()) return;
        selected = id;
        IncludeArchived.IsChecked = workflow is not null && owner is not null &&
            workflow.GetTeamNotes(owner, true).Any(n => n.Id == id && n.Archived);
        RefreshNotes();
    }

    private void RefreshNotes()
    {
        loading = true;
        var notes = workflow is null || owner is null ? [] : workflow.GetTeamNotes(owner, IncludeArchived.IsChecked == true);
        NotesList.ItemsSource = notes;
        NotesList.SelectedItem = notes.FirstOrDefault(n => n.Id == selected);
        loading = false;
        LoadNote(NotesList.SelectedItem as TeamNote);
    }

    private void LoadNote(TeamNote? note)
    {
        loading = true;
        selected = note?.Id;
        NoteTitle.Text = note?.Title ?? "";
        MarkdownEditor.Text = note?.Markdown ?? "";
        NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = note is not null && !note.Archived;
        ArchiveButton.IsEnabled = note is not null;
        ArchiveButton.Content = note?.Archived == true ? "Restore Team note" : "Archive Team note";
        ReviewStatus.Text = note?.ReviewStatus ?? "Select an owned Team note to read, edit, or review.";
        ConfirmButton.IsEnabled = note is { NeedsReview: true, Archived: false };
        unsaved = false;
        RetryButton.IsEnabled = DiscardButton.IsEnabled = false;
        Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
        loading = false;
    }

    private void SelectNote(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (!TryLeave())
        {
            loading = true;
            NotesList.SelectedItem = NotesList.Items.Cast<TeamNote>().FirstOrDefault(n => n.Id == selected);
            loading = false;
            return;
        }
        LoadNote(NotesList.SelectedItem as TeamNote);
    }

    private void CreateNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || owner is null) return;
        var note = workflow.CreateTeamNote(owner, "New Team note", "");
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
        unsaved = !workflow.EditTeamNote(id, NoteTitle.Text, MarkdownEditor.Text);
        RetryButton.IsEnabled = DiscardButton.IsEnabled = unsaved;
        if (!unsaved) RefreshNotes();
        Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
        RefreshContext();
        ShowStatus();
    }

    private void ConfirmNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        if (workflow.ConfirmTeamNote(id)) RefreshNotes();
        RefreshContext();
        ShowStatus();
    }

    private void ArchiveNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || selected is not { } id) return;
        var note = workflow.GetTeamNotes(owner!, true).Single(n => n.Id == id);
        if (workflow.ArchiveTeamNote(id, !note.Archived)) RefreshNotes();
        RefreshContext();
        ShowStatus();
    }

    private void ChangeArchiveFilter(object sender, RoutedEventArgs e)
    {
        if (!loading && workflow is not null && TryLeave()) RefreshNotes();
    }
    private void RetrySave(object sender, RoutedEventArgs e) => EditNote(sender, null!);
    private void DiscardDraft(object sender, RoutedEventArgs e)
    {
        if (!unsaved || workflow is null) return;
        var refreshed = workflow.RefreshTeamNotes();
        workflow.DiscardUnsavedChanges(selected);
        RefreshNotes();
        RefreshContext();
        NoteStatus.Text = refreshed ? "Unsaved draft discarded; last saved local document shown." :
            "Unsaved draft discarded. " + workflow.Status;
        changed?.Invoke();
    }

    public void RefreshExternalWorkspace()
    {
        if (workflow is null || owner is null) return;
        if (!unsaved && !workflow.GetTeamNotes(owner, IncludeArchived.IsChecked == true).SequenceEqual(NotesList.Items.Cast<TeamNote>()))
            RefreshNotes();
        RefreshContext();
        NewButton.IsEnabled = workflow.IsWorkspaceCurrent;
        if (!unsaved)
        {
            NoteTitle.IsEnabled = MarkdownEditor.IsEnabled =
                workflow.IsWorkspaceCurrent && NotesList.SelectedItem is TeamNote { Archived: false };
            ArchiveButton.IsEnabled = workflow.IsWorkspaceCurrent && selected is not null;
            ConfirmButton.IsEnabled = workflow.IsWorkspaceCurrent && NotesList.SelectedItem is TeamNote { NeedsReview: true, Archived: false };
        }
        NoteStatus.Text = workflow.Status;
    }

    public void DiscardLocalDraft()
    {
        if (unsaved) RefreshNotes();
    }

    private void RefreshContext()
    {
        var inherited = workflow is null || owner is null ? [] : workflow.GetInheritedTeamNotes(owner);
        InheritedSummary.Text = inherited.Count == 0 ? "No current inherited Team notes." :
            string.Join("\n", inherited.Select(n => $"{n.Note.Title} — {n.Owner.Heading} ({n.Owner.Email}): {n.Note.ReviewStatus}"));
        ReviewOwners.Children.Clear();
        var document = new FlowDocument();
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
                Content = $"Read / review '{context.Note.Title}' on {context.Owner.Heading}'s profile",
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2)
            };
            AutomationProperties.SetAutomationId(button, "ReviewTeamNote-" + context.Owner.Email);
            button.Click += (_, _) => OwnerRequested?.Invoke(context.Owner.Email, context.Note.Id);
            ReviewOwners.Children.Add(button);
        }
        InheritedReading.Document = document;
    }

    private void ShowStatus() { NoteStatus.Text = workflow?.Status ?? ""; changed?.Invoke(); }
    private void OpenExternalLink(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { NoteStatus.Text = "External link could not be opened: " + ex.Message; }
    }
}
