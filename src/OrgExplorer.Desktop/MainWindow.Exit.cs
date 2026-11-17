using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using OrgExplorer.Workflow.Updates;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? exitIntent;
    private bool normalExitReady;
    private readonly DispatcherTimer exitFeedbackTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private bool HasUnsavedWindowChanges => workPending || HasPreferredNameDraft || HasUnsavedRelationship ||
        PersonNotes.HasUnsavedChanges || TeamNotes.HasUnsavedChanges;
    private bool CanFinishNormalExit => !updateHandoffPending && !HasUnsavedWindowChanges && workflow.ExitStatus.CanClose;

    private void InitializeExit()
    {
        exitFeedbackTimer.Tick += (_, _) => ShowExitDecision();
        Closing += RequestWindowExit;
        Closed += (_, _) =>
        {
            CancelExitIntent();
            workflow.Dispose();
        };
    }

    private void RequestWindowExit(object? sender, CancelEventArgs e)
    {
        // The signed updater retains its own safe-point/restart contract.
        if (updateInstaller?.Status == UpdateInstallStatus.Restarting &&
            !updateHandoffPending && !HasUnsavedWindowChanges && workflow.CanSafelyClose) return;
        if (normalExitReady && CanFinishNormalExit) return;
        if (!workflow.Synchronization.Supported && CanFinishNormalExit) return;
        e.Cancel = true;
        normalExitReady = false;
        SettingsPanel.Visibility = Visibility.Collapsed;
        UpdateMainViews();
        if (exitIntent is not null) { ShowExitDecision(); return; }
        var intent = exitIntent = new();
        ShowExitDecision();
        exitFeedbackTimer.Start();
        ContinueExitAsync(intent);
    }

    private async void ContinueExitAsync(CancellationTokenSource intent)
    {
        var token = intent.Token;
        try
        {
            // Never recursively call Close inside WPF's Closing event.
            await Dispatcher.Yield(DispatcherPriority.Background);
            while (ReferenceEquals(exitIntent, intent))
            {
                token.ThrowIfCancellationRequested();
                var preparation = workflow.PrepareForExitAsync(token);
                await WaitForLocalWork().WaitAsync(token);
                await preparation;
                await Dispatcher.Yield(DispatcherPriority.Background);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(exitIntent, intent)) return;
                if (CanFinishNormalExit)
                {
                    normalExitReady = true;
                    Close();
                    return;
                }
                await Task.Delay(50, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or
            Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            if (ReferenceEquals(exitIntent, intent))
            {
                ExitRisk.Text = "Normal exit could not finish: " + error.Message + "\n" + workflow.ExitStatus.Message;
                exitFeedbackTimer.Stop();
            }
        }
        finally
        {
            if (ReferenceEquals(exitIntent, intent))
            {
                exitIntent = null;
                exitFeedbackTimer.Stop();
            }
            intent.Dispose();
        }
    }

    private void ShowExitDecision()
    {
        if (exitIntent is null) return;
        ExitDecision.Visibility = Visibility.Visible;
        var state = workflow.ExitStatus;
        ExitRisk.Text = (updateHandoffPending ? "Update handoff is pending. Exit anyway stops without waiting for it.\n" : "") +
            (HasUnsavedWindowChanges && !state.HasUnsavedChanges ? "Local edits or work are not confirmed saved and may be lost if you exit.\n" : "") +
            state.Message;
    }

    private void CancelExit(object sender, RoutedEventArgs e) => CancelExitIntent();

    private void CancelExitIntent()
    {
        var previous = exitIntent;
        exitIntent = null;
        normalExitReady = false;
        exitFeedbackTimer.Stop();
        ExitDecision.Visibility = Visibility.Collapsed;
        previous?.Cancel();
    }

    private void ExitAnyway(object sender, RoutedEventArgs e)
    {
        // Do not run Closing/owned-window vetoes or wait for workers/Dispose.
        // SQLite rolls back incomplete transactions; committed recovery evidence stays on disk.
        Environment.Exit(0);
    }
}
