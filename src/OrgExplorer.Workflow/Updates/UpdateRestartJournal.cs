using System.Text.Json;

namespace OrgExplorer.Workflow.Updates;

public sealed class UpdateRestartJournal
{
    private sealed record Attempt(string Version, string? Workspace, bool Pending, bool Failed, string Message);
    private readonly string path;
    private Attempt? attempt;
    public string Message => attempt?.Message ?? "";
    public string? PendingWorkspace => attempt is { Pending: true } ? attempt.Workspace : null;

    public UpdateRestartJournal(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        attempt = JsonSerializer.Deserialize<Attempt>(File.ReadAllText(path))
            ?? throw new IOException("The update restart receipt is empty.");
        if (!Version.TryParse(attempt.Version, out _) ||
            attempt.Workspace is { } workspace && !Path.IsPathFullyQualified(workspace))
            throw new IOException("The update restart receipt is invalid.");
    }

    public bool AllowAutomatic(AuthorizedRelease release) =>
        attempt is null || !(attempt.Failed || attempt.Pending) || attempt.Version != release.Version.ToString();

    public void Begin(AuthorizedRelease release, string? workspace) =>
        Save(new(release.Version.ToString(), workspace, true, false,
            $"Installation/restart of {release.Version} is not confirmed. If restart fails, reopen the app; the retained local Workspace is independent of installation."));

    public void Fail(string message)
    {
        if (attempt is { } previous) Save(previous with { Pending = false, Failed = true, Message = message });
    }

    public void Recover(Version currentVersion, Func<string, bool> openWorkspace, Func<string> workspaceStatus)
    {
        if (attempt is not { Pending: true } previous) return;
        var reopened = previous.Workspace is null || openWorkspace(previous.Workspace);
        var installed = currentVersion >= Version.Parse(previous.Version);
        var message = installed
            ? $"Update {previous.Version} installation confirmed by running version {currentVersion}."
            : $"Update {previous.Version} installation/restart was not confirmed; running version {currentVersion}. Automatic retry is paused to prevent a restart loop; Settings can check and retry explicitly.";
        if (previous.Workspace is not null)
            message += reopened ? " The same Workspace was reopened. " + workspaceStatus()
                : " The same Workspace could not be reopened. " + workspaceStatus() + " Settings and updates remain available.";
        Save(previous with { Pending = false, Failed = !installed, Message = message });
    }

    private void Save(Attempt next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, next);
                file.Flush(true);
            }
            File.Move(pending, path, true);
            attempt = next;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
