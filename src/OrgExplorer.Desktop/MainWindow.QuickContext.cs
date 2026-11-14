using System.Windows;
using System.Windows.Input;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private string? preferredNameOwner;
    private PersonProfile? displayedPreferredNameBase;
    private PersonProfile? preferredNameBasis;
    private PersonProfile? pendingPreferredNameCancellation;
    private bool preferredNameEditing;
    private bool preferredNameDirty;
    private bool preferredNameSavePending;
    private long preferredNameEntryVersion;
    private string? preferredNameSaveError;
    private bool HasPreferredNameDraft => preferredNameEditing && (preferredNameDirty || preferredNameSaveError is not null);

    private void LoadPreferredName(PersonProfile person)
    {
        if (!string.Equals(preferredNameOwner, person.Email, StringComparison.OrdinalIgnoreCase))
        {
            preferredNameOwner = person.Email;
            FinishPreferredNameEntry();
        }
        var action = person.PreferredName is null ? "Add preferred name" : "Edit preferred name";
        System.Windows.Automation.AutomationProperties.SetName(EditPreferredNameButton, action);
        EditPreferredNameButton.ToolTip = action;
        if (!preferredNameEditing)
        {
            displayedPreferredNameBase = person;
            PreferredName.Text = person.PreferredName ?? "";
        }
        PreferredNameContext.IsEnabled = workflow.IsWorkspaceCurrent;
        UpdatePreferredNameActions();
    }

    private void SetPreferredNameEditing(bool editing)
    {
        preferredNameEditing = editing;
        PreferredNameEditorPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        // Invisible compiled-control checks must not create the Popup's native window.
        PreferredNamePopup.IsOpen = editing && IsVisible;
        UpdatePreferredNameActions();
    }

    private void EditPreferredName(object sender, RoutedEventArgs e)
    {
        if (selectedEmail is null || !workflow.IsWorkspaceCurrent || workPending) return;
        if (preferredNameEditing) return;
        preferredNameBasis = displayedPreferredNameBase;
        if (preferredNameBasis is null) return;
        preferredNameEntryVersion++;
        preferredNameDirty = false;
        preferredNameSaveError = null;
        loading = true;
        PreferredName.Text = preferredNameBasis.PreferredName ?? "";
        loading = false;
        SetPreferredNameEditing(true);
        if (IsVisible)
            Dispatcher.BeginInvoke(() =>
            {
                if (!preferredNameEditing) return;
                PreferredName.Focus();
                PreferredName.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void SavePreferredName(object sender, RoutedEventArgs e) => await SubmitPreferredName();

    private async Task SubmitPreferredName()
    {
        if (!preferredNameEditing || preferredNameBasis is not { } basis || workPending) return;
        var entry = preferredNameEntryVersion;
        var text = PreferredName.Text;
        preferredNameSavePending = true;
        PersonProfile? accepted;
        try
        {
            accepted = await RunWorkflowAsync(
                () => workflow.SavePersonTextDraft(basis, PersonTextField.PreferredName, text.Length == 0 ? null : text),
                "Saving Preferred name…");
        }
        finally { preferredNameSavePending = false; }
        var returnFocus = entry == preferredNameEntryVersion && accepted is not null && PreferredName.Text == text;
        if (entry != preferredNameEntryVersion)
        {
            // Dismissal cannot revoke an admitted write or acknowledge its durable conflict.
            workflow.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        }
        else if (accepted is not null)
        {
            preferredNameBasis = accepted;
            preferredNameSaveError = null;
            if (PreferredName.Text == text) FinishPreferredNameEntry();
            else preferredNameDirty = true;
        }
        else preferredNameSaveError = workflow.Status;
        RefreshPeople();
        ShowStatus();
        if (returnFocus && IsVisible) EditPreferredNameButton.Focus();
    }

    private void FinishPreferredNameEntry()
    {
        preferredNameEntryVersion++;
        preferredNameBasis = null;
        preferredNameDirty = false;
        preferredNameSaveError = null;
        SetPreferredNameEditing(false);
    }

    private void CancelPreferredNameEntry()
    {
        if (!preferredNameEditing) return;
        var basis = preferredNameBasis;
        FinishPreferredNameEntry();
        if (workPending)
        {
            if (!preferredNameSavePending) pendingPreferredNameCancellation = basis;
            StatusText.Text = "Preferred-name entry closed. Admitted local work is still in progress; wait for its outcome.";
            return;
        }
        if (basis is not null) workflow.DiscardPersonTextDraft(basis, PersonTextField.PreferredName);
        RefreshPeople();
        ShowStatus();
    }

    private void CancelPreferredName(object sender, RoutedEventArgs e)
    {
        CancelPreferredNameEntry();
        if (IsVisible && !workPending) EditPreferredNameButton.Focus();
    }

    private void PreferredNameDismissed(object sender, EventArgs e)
    {
        if (!PreferredNamePopup.IsOpen) CancelPreferredNameEntry();
    }

    private async void PreferredNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelPreferredName(sender, e);
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && PreferredName.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            await SubmitPreferredName();
        }
    }

    private void UpdatePreferredNameActions()
    {
        var ownerUnavailable = preferredNameEditing && preferredNameOwner is { } owner &&
            workflow.GetRelationships(owner) is null;
        PreferredName.IsEnabled = preferredNameEditing && workflow.IsWorkspaceCurrent &&
            (!workPending || preferredNameSavePending);
        SavePreferredNameButton.IsEnabled = preferredNameEditing && !workPending && workflow.IsWorkspaceCurrent &&
            !ownerUnavailable;
        CancelPreferredNameButton.IsEnabled = preferredNameEditing;
        PreferredNameError.Text = ownerUnavailable
            ? "This Person is unavailable. Your entry is retained. Cancel to leave this entry."
            : preferredNameSaveError is null ? "" : workflow.PublicationOutcome switch
        {
            WorkspacePublicationOutcome.PublishedConflict =>
                "A saved change has a conflict. Your entry is retained. See Details.",
            WorkspacePublicationOutcome.Indeterminate =>
                "A save outcome is unknown. Your entry is retained. Preserve the evidence in Details.",
            _ => "Could not save. Your entry is retained. See Details before retrying."
        };
        PreferredNameError.Visibility = PreferredNameError.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(PreferredNameError, preferredNameSaveError ?? PreferredNameError.Text);
        PreferredNameErrorDetails.Text = preferredNameSaveError ?? "";
        PreferredNameFailureDetails.Visibility = preferredNameSaveError is null ? Visibility.Collapsed : Visibility.Visible;
        if (preferredNameSaveError is null) PreferredNameFailureDetails.IsExpanded = false;
    }
}
