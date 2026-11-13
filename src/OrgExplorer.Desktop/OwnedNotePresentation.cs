using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

internal sealed class OwnedNotePresentation(NoteScope scope)
{
    internal required TextBlock Status { get; init; }
    internal required TextBlock Kind { get; init; }
    internal required TextBlock Heading { get; init; }
    internal required Button Designate { get; init; }
    internal required Button Ongoing { get; init; }
    internal required Button New { get; init; }
    internal required TextBlock Empty { get; init; }
    internal required TextBlock EmptyBody { get; init; }

    internal void Show(OwnedNotesView view, bool workspaceCurrent, bool retainedDraft)
    {
        Status.Text = view.IsEmpty ? "" : view.Feedback;
        Status.Visibility = Status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        New.Content = view.IsEmpty ? $"Write a {scope} note" : $"New {scope} note";
        New.IsEnabled = view.OwnerEmail is not null && workspaceCurrent;
        EmptyBody.Visibility = view.SelectedNote is not null && !view.IsEditing && !retainedDraft &&
            string.IsNullOrWhiteSpace(view.SelectedNote.Markdown) ? Visibility.Visible : Visibility.Collapsed;
        EmptyBody.Text = view.SelectedNote?.Archived == true
            ? "This archived note is empty. Restore it to start writing."
            : "This note is empty. Choose Edit to start writing.";
        Kind.Text = view.SelectedNote is null ? "" :
            (view.IsOngoing ? $"Ongoing {scope} note" : $"Other {scope} note") +
            (view.SelectedNote.Archived ? " - Archived" : "");
        Heading.Visibility = Kind.Visibility = view.SelectedNote is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(Heading, view.SelectedNote?.Id.ToString() ?? "");
        Designate.Visibility = view.SelectedNote is { Archived: false } ? Visibility.Visible : Visibility.Collapsed;
        Designate.IsEnabled = workspaceCurrent;
        Ongoing.Visibility = view.OngoingNoteId is not null && !view.IsOngoing ? Visibility.Visible : Visibility.Collapsed;
        Ongoing.IsEnabled = view.Notes.Any(n => n.Id == view.OngoingNoteId && !n.Archived);
    }
}
