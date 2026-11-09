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
    private readonly NoteDraftState draft;
    public event Action<string, Guid>? OwnerRequested;

    public TeamNotesControl()
    {
        InitializeComponent();
        draft = new NoteDraftState("Team note", SaveNoteDraft,
            () => ((MainWindow)Window.GetWindow(this)).HasPendingWork,
            () => ((MainWindow)Window.GetWindow(this)).MarkDraftPending(),
            message => NoteStatus.Text = message);
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
        if (!string.Equals(owner, email, StringComparison.OrdinalIgnoreCase))
        {
            owner = email;
            selected = null;
        }
        Ownership.Text = email is null ? "Select a Person." :
            $"Owned by {email}; applies to current formal descendants. Ownership stays with this Person, not a permanent team. No sharing.";
        NewButton.IsEnabled = email is not null;
        if (!draft.HasUnsavedChanges) RefreshNotes();
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
        draft.Stop();
        loading = true;
        selected = note?.Id;
        NoteTitle.Text = note?.Title ?? "";
        MarkdownEditor.Text = note?.Markdown ?? "";
        NoteTitle.IsEnabled = MarkdownEditor.IsEnabled = note is not null && !note.Archived;
        ArchiveButton.IsEnabled = note is not null;
        ArchiveButton.Content = note?.Archived == true ? "Restore Team note" : "Archive Team note";
        ReviewStatus.Text = note?.ReviewStatus ?? "Select an owned Team note to read, edit, or review.";
        ConfirmButton.IsEnabled = note is { NeedsReview: true, Archived: false };
        draft.Reset();
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

    private Task<T> RunAsync<T>(Func<T> operation, bool draft = false) =>
        ((MainWindow)Window.GetWindow(this)).RunWorkflowAsync(operation, "Saving private Team note…", noteDraft: draft ? this : null);

    private async void CreateNote(object sender, RoutedEventArgs e)
    {
        if (!TryLeave() || workflow is null || owner is null) return;
        var email = owner;
        var note = await RunAsync(() => workflow.CreateTeamNote(email, "New Team note", ""));
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
        draft.Edited();
        Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
    }

    private async void SaveNoteDraft(object? sender, EventArgs e)
    {
        if (workflow is null || selected is not { } id) return;
        if (!draft.TryBeginSave()) return;
        var title = NoteTitle.Text;
        var markdown = MarkdownEditor.Text;
        var saved = await RunAsync(() => workflow.EditTeamNote(id, title, markdown), draft: true);
        draft.CompleteSave(saved, title, markdown, NoteTitle.Text, MarkdownEditor.Text);
        RetryButton.IsEnabled = DiscardButton.IsEnabled = draft.HasUnsavedChanges;
        if (!draft.HasUnsavedChanges) RefreshNotes();
        Reading.Document = PrivateMarkdownReading.Create(MarkdownEditor.Text, OpenExternalLink);
        RefreshContext();
        ShowStatus();
        draft.ResumeNewerEdits(saved);
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
        if (await RunAsync(() => workflow.ArchiveTeamNote(id, !note.Archived))) RefreshNotes();
        RefreshContext();
        ShowStatus();
    }

    private void ChangeArchiveFilter(object sender, RoutedEventArgs e)
    {
        if (!loading && workflow is not null && TryLeave()) RefreshNotes();
    }
    private void RetrySave(object sender, RoutedEventArgs e) => SaveNoteDraft(null, EventArgs.Empty);
    private async void DiscardDraft(object sender, RoutedEventArgs e)
    {
        if (!draft.HasUnsavedChanges || workflow is null) return;
        await draft.DiscardAsync(() => RunAsync(workflow.RefreshTeamNotes),
            () => workflow.DiscardUnsavedChanges(selected), () => { RefreshNotes(); RefreshContext(); }, () => workflow.Status);
        changed?.Invoke();
    }

    public void RefreshExternalWorkspace()
    {
        if (workflow is null || owner is null) return;
        if (!draft.HasUnsavedChanges && !workflow.GetTeamNotes(owner, IncludeArchived.IsChecked == true).SequenceEqual(NotesList.Items.Cast<TeamNote>()))
            RefreshNotes();
        RefreshContext();
        NewButton.IsEnabled = workflow.IsWorkspaceCurrent;
        if (!draft.HasUnsavedChanges)
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
        if (draft.HasUnsavedChanges) RefreshNotes();
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
