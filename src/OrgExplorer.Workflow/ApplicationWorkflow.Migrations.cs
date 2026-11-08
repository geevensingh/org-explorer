using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private static readonly IReadOnlyDictionary<string, int> CurrentRecordVersions = new Dictionary<string, int>
    {
        ["people"] = 2, ["profileHistory"] = 2, ["acceptedBatches"] = 1, ["personNotes"] = 1,
        ["teamNotes"] = 1, ["photos"] = 1, ["teamContext"] = 1, ["historicalTeamContext"] = 1
    };
    private string? conversionMessage;

    private sealed class UnsupportedWorkspaceVersion(string family, int version)
        : FormatException($"Unsupported {family} data version {version}. Upgrade the app; Workspace writes are blocked.")
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
        var pointer = Path.Combine(workspace!, "workspace.json");
        if (!File.Exists(pointer)) return 2;
        RejectWorkspaceLink(pointer);
        using var json = JsonDocument.Parse(ReadWorkspaceText(pointer));
        var metadata = ReadWorkspaceMetadata(json.RootElement);
        var version = metadata.SchemaVersion;
        var generation = Path.Combine(workspace!, "generations", metadata.Generation);
        if (Directory.Exists(generation)) RejectWorkspaceLink(generation);
        foreach (var folder in new[] { "people", "profile-history", "historical-team-context" })
        {
            var directory = Path.Combine(generation, folder);
            if (!Directory.Exists(directory)) continue;
            RejectWorkspaceLink(directory);
            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                RejectWorkspaceLink(file);
                using var record = JsonDocument.Parse(ReadWorkspaceText(file));
                if (folder == "historical-team-context") RequireVersion(record.RootElement, folder, [1]);
                else CheckRecordVersion(record.RootElement, folder, version);
            }
        }
        foreach (var folder in new[] { "notes", "team-notes" })
        {
            var directory = Path.Combine(workspace!, folder);
            if (!Directory.Exists(directory)) continue;
            RejectWorkspaceLink(directory);
            foreach (var file in Directory.GetFiles(directory, "*.md"))
            {
                RejectWorkspaceLink(file);
                var text = ReadWorkspaceText(file);
                var front = Regex.Match(text, @"\A---\r?\n(?<metadata>[\s\S]*?)\r?\n---\r?\n");
                if (!front.Success) throw new FormatException($"{folder}: expected JSON front matter.");
                using var record = JsonDocument.Parse(front.Groups["metadata"].Value);
                RequireVersion(record.RootElement, folder, [1]);
            }
        }
        var batchesPath = Path.Combine(generation, "batches.json");
        if (File.Exists(batchesPath))
        {
            RejectWorkspaceLink(batchesPath);
            using var batches = JsonDocument.Parse(ReadWorkspaceText(batchesPath));
            if (batches.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Accepted batches must be an object.");
            foreach (var batch in batches.RootElement.EnumerateObject())
                RequireVersion(Required(batch.Value, "source", "Accepted batch"), "accepted batch source", [1]);
        }
        return version;
    }

    private static WorkspaceMetadata ReadWorkspaceMetadata(JsonElement root)
    {
        var version = RequireVersion(root, "Workspace", [1, 2]);
        CheckProperties(root, ["schemaVersion", "generation", "files", "recordVersions"], "Workspace");
        var metadata = root.Deserialize<WorkspaceMetadata>(JsonOptions)
            ?? throw new FormatException("Invalid Workspace metadata.");
        if (metadata.Generation is null || !Regex.IsMatch(metadata.Generation, @"^[a-f0-9]{32}$"))
            throw new FormatException("Invalid Workspace generation.");
        if (version == 2)
        {
            if (metadata.Files is null || metadata.RecordVersions is null)
                throw new FormatException("Version-2 Workspace requires files and recordVersions.");
            var families = Required(root, "recordVersions", "Workspace");
            CheckProperties(families, families.ValueKind == JsonValueKind.Object
                ? families.EnumerateObject().Select(p => p.Name).ToArray() : [], "Workspace.recordVersions");
            foreach (var (family, recordVersion) in metadata.RecordVersions)
                if (!CurrentRecordVersions.TryGetValue(family, out var supported) || recordVersion != supported)
                    throw new UnsupportedWorkspaceVersion(family, recordVersion);
            if (CurrentRecordVersions.Keys.Any(f => !metadata.RecordVersions.ContainsKey(f)))
                throw new FormatException("Workspace recordVersions is incomplete.");
        }
        else if (metadata.RecordVersions is not null)
            throw new FormatException("Version-1 Workspace cannot declare version-2 recordVersions.");
        return metadata;
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

    private static void WriteVersionedRecord<T>(string path, T value)
    {
        var record = JsonSerializer.SerializeToNode(value, JsonOptions)!.AsObject();
        record["schemaVersion"] = 2;
        WriteDurably(path, record);
    }

    private void ConvertVersionOneWorkspace()
    {
        var originalRoot = workspace!;
        using var source = new ApplicationWorkflow(false, profileLinkOpener, timeProvider) { workspace = originalRoot };
        source.ReadAndAcceptWorkspaceData();
        var originalRevision = source.revision;
        compatibility = new(WorkspaceCompatibilityState.RecoveryRequired,
            "Automatic conversion has not been published. Original local files remain authoritative.",
            originalRoot, 1);
        using var lease = new FileStream(Path.Combine(originalRoot, ".workspace.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (InspectWorkspaceVersions() != 1 || WorkspaceFingerprint() != originalRevision)
            throw new IOException("Workspace changed before conversion; wait for complete delivery and retry.");

        var attempt = Path.Combine(originalRoot, "recovery", Guid.NewGuid().ToString("N"));
        var originals = Path.Combine(attempt, "original");
        var candidateRoot = Path.Combine(attempt, "converted");
        try
        {
            CopyWorkspace(originalRoot, originals);
            using var backup = new ApplicationWorkflow(false, profileLinkOpener, timeProvider) { workspace = originals };
            backup.ReadAndAcceptWorkspaceData();
            if (backup.revision != originalRevision || WorkspaceFingerprint() != originalRevision)
                throw new IOException("Workspace changed while preserving originals; no conversion was published.");
            compatibility = new(WorkspaceCompatibilityState.RecoveryRequired,
                "Conversion has not been published. Validated originals are recoverable; resolve the local error and retry.",
                originalRoot, 1, originals);
            WriteDurably(Path.Combine(attempt, "recovery.json"), new
            {
                sourceVersion = 1, targetVersion = 2, originalFingerprint = originalRevision,
                originalWorkspace = originalRoot, original = "original", state = "originals-preserved"
            });

            CopyWorkspace(originals, candidateRoot);
            var oldPointer = JsonSerializer.Deserialize<WorkspaceMetadata>(
                ReadWorkspaceText(Path.Combine(originals, "workspace.json")), JsonOptions)!;
            var generation = Guid.NewGuid().ToString("N");
            var next = Path.Combine(candidateRoot, "generations", generation);
            CopyWorkspace(Path.Combine(originals, "generations", oldPointer.Generation), next, includeRecovery: true);
            foreach (var folder in new[] { "people", "profile-history" })
            {
                var directory = Path.Combine(next, folder);
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.GetFiles(directory, "*.json"))
                {
                    var record = JsonNode.Parse(ReadWorkspaceText(file))!.AsObject();
                    record["schemaVersion"] = 2;
                    WriteDurably(file, record);
                }
            }
            var files = Directory.GetFiles(next, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(next, p)).Order(StringComparer.Ordinal).ToArray();
            var pointer = new WorkspaceMetadata(2, generation, files, CurrentRecordVersions);
            WriteDurably(Path.Combine(candidateRoot, "workspace.json"), pointer);
            using var converted = new ApplicationWorkflow(false, profileLinkOpener, timeProvider) { workspace = candidateRoot };
            converted.InspectWorkspaceVersions();
            converted.ReadAndAcceptWorkspaceData();

            if (InspectWorkspaceVersions() != 1 || WorkspaceFingerprint() != originalRevision)
                throw new IOException("Workspace changed before conversion publication; originals remain authoritative.");
            CopyWorkspace(next, Path.Combine(originalRoot, "generations", generation), includeRecovery: true);
            using var copied = new ApplicationWorkflow(false, profileLinkOpener, timeProvider) { workspace = originalRoot };
            copied.ReadAndAcceptWorkspaceData(pointer);
            if (InspectWorkspaceVersions() != 1 ||
                WorkspaceFingerprint(ignoredGeneration: generation) != originalRevision ||
                copied.WorkspaceFingerprint() != copied.revision)
                throw new IOException("Workspace changed during conversion publication; original pointer was not replaced.");
            var stagedPointer = Path.Combine(attempt, "workspace.converted.next");
            WriteDurably(stagedPointer, pointer);
            File.Replace(stagedPointer, Path.Combine(originalRoot, "workspace.json"),
                Path.Combine(originalRoot, "workspace.previous.json"));
            conversionMessage = "Workspace automatically converted from version 1 to 2. Recoverable originals: " + originals;
        }
        catch (UnsupportedWorkspaceVersion) { throw; }
        catch (Exception error) when (StorageError(error))
        {
            throw new IOException("Automatic Workspace conversion failed; the original Workspace was not replaced. " +
                (compatibility.RecoveryPath is { } recovery ? "Recoverable originals: " + recovery + ". " :
                    "Original local files remain authoritative. ") + "Resolve the local error, then retry opening/refreshing. " + error.Message, error);
        }
    }

    private static void CopyWorkspace(string source, string destination, bool includeRecovery = false)
    {
        Directory.CreateDirectory(destination);
        var directories = new Stack<string>();
        directories.Push(source);
        while (directories.TryPop(out var directory))
        {
            RejectWorkspaceLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!includeRecovery && directory == source && Path.GetFileName(entry).Equals("recovery", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(entry).Equals(".workspace.lock", StringComparison.OrdinalIgnoreCase)) continue;
                RejectWorkspaceLink(entry);
                var target = Path.Combine(destination, Path.GetRelativePath(source, entry));
                if (Directory.Exists(entry))
                {
                    Directory.CreateDirectory(target);
                    directories.Push(entry);
                }
                else
                {
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(ReadWorkspaceBytes(entry));
                    output.Flush(true);
                }
            }
        }
    }
}
