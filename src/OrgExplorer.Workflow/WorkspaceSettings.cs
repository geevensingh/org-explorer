using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Workflow;

public sealed class WorkspaceSettings
{
    private sealed record Preference(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string LastWorkspacePath);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string preferencePath;

    public string? LastWorkspacePath { get; private set; }
    public string? PreferenceError { get; private set; }

    public WorkspaceSettings(string preferencePath)
    {
        this.preferencePath = preferencePath;
        try
        {
            var preference = JsonSerializer.Deserialize<Preference>(File.ReadAllBytes(preferencePath), Json)
                ?? throw new FormatException("Workspace preferences are empty.");
            if (preference.SchemaVersion != 1 || string.IsNullOrWhiteSpace(preference.LastWorkspacePath) ||
                !Path.IsPathFullyQualified(preference.LastWorkspacePath))
                throw new FormatException("Workspace preferences require version 1 and an absolute Workspace folder.");
            LastWorkspacePath = Path.GetFullPath(preference.LastWorkspacePath);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            PreferenceError = "Workspace preferences could not be read; no remembered Workspace was restored. Choose a folder. " + error.Message;
        }
    }

    public bool RestoreWorkspace(IApplicationWorkflow workflow, string? restartWorkspace = null) =>
        (restartWorkspace ?? LastWorkspacePath) is { } path && OpenWorkspaceCore(workflow, path, WorkspaceOpenMode.ExistingOnly);

    public bool OpenWorkspace(IApplicationWorkflow workflow, string path) =>
        OpenWorkspaceCore(workflow, path, WorkspaceOpenMode.OpenOrCreate);

    private bool OpenWorkspaceCore(IApplicationWorkflow workflow, string path, WorkspaceOpenMode mode)
    {
        if (!workflow.OpenWorkspace(path, mode)) return false;
        RememberWorkspace(workflow.Compatibility.WorkspacePath!);
        return true;
    }

    private void RememberWorkspace(string path)
    {
        string? pending = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(preferencePath))!);
            pending = preferencePath + "." + Guid.NewGuid().ToString("N") + ".pending";
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new Preference(1, path), Json);
                file.Flush(true);
            }
            File.Move(pending, preferencePath, true);
            LastWorkspacePath = path;
            PreferenceError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PreferenceError = "Workspace opened, but its startup preference was NOT saved; the previous startup preference is unchanged. Retry opening after resolving local file access. " + error.Message;
        }
        finally
        {
            if (pending is not null)
                try { File.Delete(pending); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    PreferenceError = (PreferenceError is null ? "" : PreferenceError + "\n") +
                        "Workspace preference staging file could not be removed: " + error.Message;
                }
        }
    }
}
