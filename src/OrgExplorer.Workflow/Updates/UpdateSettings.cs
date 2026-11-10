using System.Text.Json;

namespace OrgExplorer.Workflow.Updates;

public sealed class UpdateSettings
{
    private readonly UpdateChecker checker;
    private readonly string preferencePath;
    private readonly Func<AuthorizedRelease, CancellationToken, Task>? consumeRelease;
    private readonly object gate = new();
    private Task? inFlight;
    public bool CheckOnStartup { get; private set; } = true;
    public string StartupPreferenceGuidance => CheckOnStartup
        ? "Startup update checks are enabled." : "Startup update checks are disabled.";
    public string? PreferenceError { get; private set; }
    public ReleaseCheckResult Result { get; private set; } = new(ReleaseCheckStatus.NotChecked, "No update availability has been checked.");

    public UpdateSettings(UpdateChecker checker, string preferencePath,
        Func<AuthorizedRelease, CancellationToken, Task>? consumeRelease = null)
    {
        this.checker = checker;
        this.preferencePath = preferencePath;
        this.consumeRelease = consumeRelease;
        try
        {
            if (!File.Exists(preferencePath)) return;
            using var json = UpdateJson.Parse(File.ReadAllBytes(preferencePath));
            UpdateJson.Fields(json.RootElement, "schemaVersion", "checkOnStartup");
            if (json.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
                throw new UpdateVerificationException("Unsupported preferences version.");
            CheckOnStartup = json.RootElement.GetProperty("checkOnStartup").GetBoolean();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or UpdateVerificationException or InvalidOperationException or FormatException)
        {
            PreferenceError = "Update preferences could not be read; startup checks default to enabled. " + error.Message;
        }
    }

    public Task StartupAsync(CancellationToken cancellationToken = default) =>
        CheckOnStartup ? CheckAsync(cancellationToken) : Task.CompletedTask;

    public Task CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
            return inFlight is { IsCompleted: false } ? inFlight : inFlight = CheckCoreAsync(cancellationToken);
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        Result = new(ReleaseCheckStatus.Checking, "Checking project-authorized updates… Local browsing and editing remain available.");
        Result = await checker.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (Result.Release is { } release && consumeRelease is not null)
            await consumeRelease(release, cancellationToken).ConfigureAwait(false);
    }

    public bool SetCheckOnStartup(bool enabled)
    {
        string? pending = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(preferencePath))!);
            pending = preferencePath + "." + Guid.NewGuid().ToString("N") + ".pending";
            File.WriteAllBytes(pending, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, checkOnStartup = enabled }));
            File.Move(pending, preferencePath, true);
            CheckOnStartup = enabled;
            PreferenceError = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PreferenceError = "Update preference was not saved; the previous setting remains active. Retry after resolving local file access. " + error.Message;
            return false;
        }
        finally
        {
            if (pending is not null)
                try { File.Delete(pending); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
