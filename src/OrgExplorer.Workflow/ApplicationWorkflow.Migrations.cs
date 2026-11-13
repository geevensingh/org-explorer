using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private static readonly IReadOnlyDictionary<string, int> CurrentRecordVersions = new Dictionary<string, int>
    {
        ["people"] = 2, ["profileHistory"] = 2, ["acceptedBatches"] = 1, ["personNotes"] = 1,
        ["teamNotes"] = 1, ["photos"] = 1, ["teamContext"] = 1, ["historicalTeamContext"] = 1
    };

    private sealed class UnsupportedWorkspaceVersion(string family, int version)
        : FormatException(family == "Workspace" && version < 3
            ? $"Unsupported Workspace data version {version}. This app requires version 3 and does not migrate older Workspaces. Preserve this folder and explicitly create a new empty Workspace. Workspace writes are blocked."
            : $"Unsupported {family} data version {version}. Upgrade the app; Workspace writes are blocked.")
    {
        public int Version { get; } = version;
    }

    private void MarkCompatibilityFailure(Exception error)
    {
        workspaceCurrent = false;
        pending = null;
        compatibility = new(error is UnsupportedWorkspaceVersion ? WorkspaceCompatibilityState.Unsupported :
                compatibility.State == WorkspaceCompatibilityState.RecoveryRequired
                    ? WorkspaceCompatibilityState.RecoveryRequired : WorkspaceCompatibilityState.Unavailable,
            error.Message + "\nSettings are available. Do not downgrade or repair provider delivery in the app. " +
            "Installer rollback does not restore Workspace data.",
            workspace, error is UnsupportedWorkspaceVersion unsupported ? unsupported.Version :
                compatibility.State == WorkspaceCompatibilityState.RecoveryRequired ? compatibility.DataVersion : null,
            compatibility.RecoveryPath);
    }

    private void EnsureWorkspaceWritable()
    {
        if (workspaceConflicts.Count != 0)
            throw new IOException("Resolve the retained publication conflict before any further mutation.");
        try
        {
            if (workspace is null) throw new IOException("Open a Workspace first.");
            // Check versions again before even creating a local writer lease: the
            // provider can deliver incompatible bytes before its watcher event.
            if (!memory) InspectWorkspaceVersions();
            if (!workspaceCurrent)
                throw new IOException("Workspace unavailable; retained information is STALE. No changes saved.");
        }
        catch (Exception error) when (StorageError(error))
        {
            MarkCompatibilityFailure(error);
            throw;
        }
    }

    private int InspectWorkspaceVersions()
    {
        using var access = CoordinateWorkspaceIo(workspace!);
        var path = Path.Combine(workspace!, "workspace.json");
        if (File.Exists(path))
        {
            RejectWorkspaceLink(path);
            ReadIncrementalMetadata(ReadWorkspaceBytesUncached(path));
        }
        return 3;
    }

    private static void CheckRecordVersion(JsonElement root, string family, int workspaceVersion)
    {
        if (workspaceVersion == 2) RequireVersion(root, family, [2]);
        else if (root.TryGetProperty("schemaVersion", out _)) RequireVersion(root, family, [1]);
    }

    private static int RequireVersion(JsonElement root, string family, int[] supported)
    {
        var value = Required(root, "schemaVersion", family);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version))
            throw new FormatException($"{family}.schemaVersion: expected an integer.");
        if (!supported.Contains(version)) throw new UnsupportedWorkspaceVersion(family, version);
        return version;
    }

    private static T? DeserializeVersionedRecord<T>(JsonElement root)
    {
        var record = JsonNode.Parse(root.GetRawText())!.AsObject();
        record.Remove("schemaVersion");
        return record.Deserialize<T>(JsonOptions);
    }
}
