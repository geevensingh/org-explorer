using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Workflow;

public sealed record PaneProportions(
    [property: JsonRequired] double PeoplePercent,
    [property: JsonRequired] double ProfilePercent)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(PeoplePercent) && PeoplePercent is > 0 and < 100 &&
        double.IsFinite(ProfilePercent) && ProfilePercent is > 0 and < 100;
}

public sealed record RememberedView(
    [property: JsonRequired] string PersonEmail,
    SearchSource? Source = null, Guid? NoteId = null, string? NoteOwnerEmail = null,
    Guid? HistoricalContextId = null);

public sealed class WorkspaceSettings
{
    private sealed record Preference(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string LastWorkspacePath,
        WorkspacePreference[]? Workspaces = null);
    private sealed record WorkspacePreference(
        [property: JsonRequired] string Path,
        [property: JsonRequired] OngoingDesignation[] OngoingNotes,
        PaneProportions? PaneProportions = null,
        RememberedView? LastView = null);
    private sealed record OngoingDesignation(
        [property: JsonRequired] NoteScope Scope,
        [property: JsonRequired] string OwnerEmail,
        [property: JsonRequired] Guid NoteId);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter<NoteScope>(allowIntegerValues: false),
            new JsonStringEnumConverter<SearchSource>(allowIntegerValues: false) }
    };
    private readonly string preferencePath;
    private Preference? preference;

    public string? LastWorkspacePath { get; private set; }
    public string? PreferenceError { get; private set; }
    internal bool HasValidPreferenceSnapshot => preference is not null || PreferenceError is null;

    public WorkspaceSettings(string preferencePath)
    {
        this.preferencePath = preferencePath;
        try
        {
            preference = ReadPreference();
            LastWorkspacePath = preference?.LastWorkspacePath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            PreferenceError = "Workspace preferences could not be read; no remembered Workspace, exact view, ongoing choice or pane proportions were restored. " +
                "Resolve local preference data/file access. Choosing a folder can still open the Workspace without replacing invalid preferences. " + error.Message;
        }
    }

    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SamePath(string left, string right) =>
        string.Equals(CanonicalPath(left), CanonicalPath(right), StringComparison.OrdinalIgnoreCase);

    private Preference? ReadPreference()
    {
        Preference value;
        try
        {
            value = JsonSerializer.Deserialize<Preference>(File.ReadAllBytes(preferencePath), Json)
                ?? throw new FormatException("Workspace preferences are empty.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        if (value.SchemaVersion != 1 || string.IsNullOrWhiteSpace(value.LastWorkspacePath) ||
            !Path.IsPathFullyQualified(value.LastWorkspacePath))
            throw new FormatException("Workspace preferences require version 1 and an absolute Workspace folder.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in value.Workspaces ?? [])
        {
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path) ||
                !Path.IsPathFullyQualified(workspace.Path) || !paths.Add(CanonicalPath(workspace.Path)) ||
                workspace.OngoingNotes is null)
                throw new FormatException("Workspace presentation preferences require unique absolute folders and ongoing-note entries.");
            if (workspace.PaneProportions is { IsValid: false })
                throw new FormatException("Pane proportions require finite percentages greater than zero and less than 100.");
            if (workspace.LastView is { } view) ValidateView(view);
            var owners = new HashSet<(NoteScope, string)>();
            foreach (var note in workspace.OngoingNotes)
                if (note is null || !Enum.IsDefined(note.Scope) || string.IsNullOrWhiteSpace(note.OwnerEmail) ||
                    !ApplicationWorkflow.IsPlainEmail(note.OwnerEmail) || note.NoteId == Guid.Empty ||
                    !owners.Add((note.Scope, note.OwnerEmail.ToUpperInvariant())))
                    throw new FormatException("Ongoing-note preferences require unique scope/owner identities and existing note GUIDs.");
        }
        return value with { LastWorkspacePath = CanonicalPath(value.LastWorkspacePath) };
    }

    public Guid? GetOngoingNote(string workspacePath, NoteScope scope, string ownerEmail) =>
        preference?.Workspaces?.FirstOrDefault(w => SamePath(w.Path, workspacePath))?.OngoingNotes
            .FirstOrDefault(n => n.Scope == scope && n.OwnerEmail.Equals(ownerEmail, StringComparison.OrdinalIgnoreCase))?.NoteId;

    public bool SetOngoingNote(string workspacePath, NoteScope scope, string ownerEmail, Guid noteId) =>
        SavePreference(current =>
        {
            if (!Enum.IsDefined(scope) || string.IsNullOrWhiteSpace(ownerEmail) ||
                !ApplicationWorkflow.IsPlainEmail(ownerEmail) || noteId == Guid.Empty ||
                !Path.IsPathFullyQualified(workspacePath))
                throw new ArgumentException("An ongoing note requires a scope, owner email and existing note GUID.");
            return UpdateWorkspace(current, workspacePath, workspace =>
            {
                var notes = workspace.OngoingNotes
                    .Where(n => n.Scope != scope || !n.OwnerEmail.Equals(ownerEmail, StringComparison.OrdinalIgnoreCase)).ToList();
                notes.Add(new(scope, ownerEmail.Trim().ToLowerInvariant(), noteId));
                return workspace with { OngoingNotes = notes.ToArray() };
            });
        }, "Ongoing-note preference was NOT saved; the previous ongoing choice is unchanged.");

    public PaneProportions? GetPaneProportions(string workspacePath) =>
        preference?.Workspaces?.FirstOrDefault(w => SamePath(w.Path, workspacePath))?.PaneProportions;

    public bool SetPaneProportions(string workspacePath, PaneProportions proportions) =>
        SavePreference(current =>
        {
            if (!proportions.IsValid || !Path.IsPathFullyQualified(workspacePath))
                throw new ArgumentException("Pane proportions require an absolute Workspace folder and finite percentages between zero and 100.");
            return UpdateWorkspace(current, workspacePath, workspace => workspace with { PaneProportions = proportions });
        }, "Pane proportions were NOT saved; the previous durable proportions are unchanged.");

    public RememberedView? GetLastView(string workspacePath) =>
        preference?.Workspaces?.FirstOrDefault(w => SamePath(w.Path, workspacePath))?.LastView;

    public bool SetLastView(string workspacePath, RememberedView view) =>
        SavePreference(current =>
        {
            ValidateView(view);
            if (!Path.IsPathFullyQualified(workspacePath))
                throw new ArgumentException("A remembered view requires an absolute Workspace folder.");
            return UpdateWorkspace(current, workspacePath, workspace => workspace with { LastView = view });
        }, "The current view was NOT remembered; the previous durable view is unchanged.");

    private static Preference UpdateWorkspace(Preference? current, string workspacePath,
        Func<WorkspacePreference, WorkspacePreference> update)
    {
        var path = CanonicalPath(workspacePath);
        var workspaces = (current?.Workspaces ?? []).ToList();
        var index = workspaces.FindIndex(w => SamePath(w.Path, path));
        var updated = update(index < 0 ? new(path, []) : workspaces[index]);
        if (index < 0) workspaces.Add(updated);
        else workspaces[index] = updated;
        return new(1, current?.LastWorkspacePath ?? path, workspaces.ToArray());
    }

    private static void ValidateView(RememberedView view)
    {
        if (string.IsNullOrWhiteSpace(view.PersonEmail) || !ApplicationWorkflow.IsPlainEmail(view.PersonEmail) ||
            (view.Source is null
                ? view.NoteId is not null || view.NoteOwnerEmail is not null || view.HistoricalContextId is not null
                : view.Source is not (SearchSource.PersonNote or SearchSource.OwnedTeamNote or
                      SearchSource.InheritedTeamNote or SearchSource.HistoricalTeamNote) ||
                  view.NoteId is null || view.NoteId == Guid.Empty ||
                  string.IsNullOrWhiteSpace(view.NoteOwnerEmail) || !ApplicationWorkflow.IsPlainEmail(view.NoteOwnerEmail) ||
                  (view.Source == SearchSource.HistoricalTeamNote
                      ? view.HistoricalContextId is null || view.HistoricalContextId == Guid.Empty
                      : view.HistoricalContextId is not null) ||
                  (view.Source is SearchSource.PersonNote or SearchSource.OwnedTeamNote &&
                      !view.PersonEmail.Equals(view.NoteOwnerEmail, StringComparison.OrdinalIgnoreCase))))
            throw new FormatException("Exact-view preferences require a Person email and a stable note identity with its actual owner and context.");
    }

    public bool RestoreWorkspace(IApplicationWorkflow workflow, string? restartWorkspace = null) =>
        (restartWorkspace ?? LastWorkspacePath) is { } path && OpenWorkspaceCore(workflow, path, WorkspaceOpenMode.ExistingOnly);

    public bool OpenWorkspace(IApplicationWorkflow workflow, string path) =>
        OpenWorkspaceCore(workflow, path, WorkspaceOpenMode.OpenOrCreate);

    private bool OpenWorkspaceCore(IApplicationWorkflow workflow, string path, WorkspaceOpenMode mode)
    {
        if (!workflow.OpenWorkspace(path, mode)) return false;
        var canonical = CanonicalPath(workflow.Compatibility.WorkspacePath!);
        SavePreference(current => new(1, canonical, current?.Workspaces),
            "Workspace opened, but its startup preference was NOT saved; the previous startup preference is unchanged.");
        return true;
    }

    private bool SavePreference(Func<Preference?, Preference> update, string failure)
    {
        string? pending = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(preferencePath))!);
            using var guard = new FileStream(preferencePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var next = update(ReadPreference());
            pending = preferencePath + "." + Guid.NewGuid().ToString("N") + ".pending";
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, next, Json);
                file.Flush(true);
            }
            File.Move(pending, preferencePath, true);
            preference = next;
            LastWorkspacePath = next.LastWorkspacePath;
            PreferenceError = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or FormatException)
        {
            PreferenceError = failure + " Resolve local preference data/file access and retry. " + error.Message;
            return false;
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
