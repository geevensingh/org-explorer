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

    internal void Show(OwnedNotesView view, bool workspaceCurrent)
    {
        Status.Text = view.Feedback;
        Kind.Text = view.SelectedNote is null ? "" :
            (view.IsOngoing ? $"Ongoing {scope} note" : $"Other {scope} note") +
            (view.SelectedNote.Archived ? " - Archived" : "");
        AutomationProperties.SetHelpText(Heading, view.SelectedNote?.Id.ToString() ?? "");
        Designate.Visibility = view.SelectedNote is { Archived: false } ? Visibility.Visible : Visibility.Collapsed;
        Designate.IsEnabled = workspaceCurrent;
        Ongoing.Visibility = view.OngoingNoteId is not null && !view.IsOngoing ? Visibility.Visible : Visibility.Collapsed;
        Ongoing.IsEnabled = view.Notes.Any(n => n.Id == view.OngoingNoteId && !n.Archived);
    }
}
