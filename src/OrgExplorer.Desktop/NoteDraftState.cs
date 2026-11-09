using System.Windows.Threading;

namespace OrgExplorer.Desktop;

internal sealed class NoteDraftState
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly string kind;
    private readonly Func<bool> workPending;
    private readonly Action markPending;
    private readonly Action<string> showStatus;
    private readonly Action<string> showNavigationBlocked;
    private bool saveFailed;
    public bool HasUnsavedChanges { get; private set; }

    public NoteDraftState(string kind, EventHandler save, Func<bool> workPending,
        Action markPending, Action<string> showStatus, Action<string> showNavigationBlocked)
    {
        this.kind = kind;
        this.workPending = workPending;
        this.markPending = markPending;
        this.showStatus = showStatus;
        this.showNavigationBlocked = showNavigationBlocked;
        timer.Tick += save;
    }

    public bool TryLeave()
    {
        if (!HasUnsavedChanges) return true;
        var message = saveFailed && !workPending()
            ? $"Navigation blocked: {kind} was not saved. Retry or discard the draft before leaving or closing."
            : $"Navigation blocked: {kind} edits are pending or saving. Please wait before leaving or closing.";
        showStatus(message);
        showNavigationBlocked(message);
        return false;
    }

    public void Edited()
    {
        HasUnsavedChanges = true;
        saveFailed = false;
        timer.Stop();
        timer.Start();
        markPending();
        showStatus($"Pending private {kind} edits — not yet saved.");
    }

    public bool TryBeginSave()
    {
        timer.Stop();
        if (!workPending()) return true;
        timer.Start();
        return false;
    }

    public void CompleteSave(bool saved, string title, string markdown, string currentTitle, string currentMarkdown)
    {
        saveFailed = !saved;
        HasUnsavedChanges = !saved || title != currentTitle || markdown != currentMarkdown;
    }

    public void ResumeNewerEdits(bool saved)
    {
        if (!saved || !HasUnsavedChanges) return;
        timer.Start();
        markPending();
        showStatus($"Pending newer private {kind} edits — not yet saved.");
    }

    public void Stop() => timer.Stop();
    public void Reset() { timer.Stop(); HasUnsavedChanges = saveFailed = false; }

    public async Task DiscardAsync(Func<Task<bool>> refresh, Action discard, Action reload, Func<string> status)
    {
        timer.Stop();
        var refreshed = await refresh();
        discard();
        reload();
        showStatus(refreshed ? "Unsaved draft discarded; last saved local document shown." :
            "Unsaved draft discarded. " + status());
    }
}
