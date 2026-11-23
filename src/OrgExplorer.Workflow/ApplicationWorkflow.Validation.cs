using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private sealed class UnsupportedWorkspaceFormat(string message) : FormatException(message);
    private sealed class UnsupportedWorkspaceVersion(string family, int version)
        : FormatException(family is "Workspace" or "SQLite Workspace" && version < 3
            ? $"Unsupported Workspace data version {version}. This app requires version 3 and does not migrate older Workspaces. Preserve this folder and explicitly create a new empty Workspace. Workspace writes are blocked."
            : $"Unsupported {family} data version {version}. Upgrade the app; Workspace writes are blocked.")
    {
        public int Version { get; } = version;
    }

    private void MarkCompatibilityFailure(Exception error)
    {
        workspaceCurrent = false;
        pending = null;
        compatibility = new(error is UnsupportedWorkspaceVersion or UnsupportedWorkspaceFormat ? WorkspaceCompatibilityState.Unsupported :
                compatibility.State == WorkspaceCompatibilityState.RecoveryRequired
                    ? WorkspaceCompatibilityState.RecoveryRequired : WorkspaceCompatibilityState.Unavailable,
            error.Message + "\nSettings are available. Do not downgrade or repair provider delivery in the app. " +
            "Installer rollback does not restore Workspace data.",
            workspace, error is UnsupportedWorkspaceVersion unsupported ? unsupported.Version :
                compatibility.State == WorkspaceCompatibilityState.RecoveryRequired ? compatibility.DataVersion : null,
            compatibility.RecoveryPath)
        {
            SupportsOrdinaryEdits = false, SupportsNoteEdits = false,
            SupportsNonblockingNoteAutosave = localSqlite, SupportedWorkspaceVersion = 3
        };
    }

    private void EnsureWorkspaceWritable()
    {
        try
        {
            if (workspace is null) throw new IOException("Open a Workspace first.");
            if (localSqlite) LocalSqliteRevision();
            if (!workspaceCurrent)
                throw new IOException("Workspace unavailable; retained information is STALE. No changes saved.");
        }
        catch (Exception error) when (StorageError(error))
        {
            MarkCompatibilityFailure(error);
            throw;
        }
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
