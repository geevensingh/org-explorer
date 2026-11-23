using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OrgExplorer.TestFixtures;

// Test evidence for the two-import controlled installer fixture, not an arbitrary Workspace export.
internal sealed record InstallerPreservationEvidence(int SchemaVersion, string Workspace, Guid WorkspaceId,
    Guid Head, string Folder, Guid? PublishedRevision, string PublicStateSha256,
    InstallerImportReplay[] ImportReplays, Dictionary<string, string> Files)
{
    internal static Dictionary<string, string> SnapshotFiles(string workspace)
    {
        if (!File.Exists(Path.Combine(workspace, "workspace.sqlite")) ||
            File.ReadAllText(Path.Combine(workspace, "local-workspace.format")) != "OrgExplorer local SQLite Workspace")
            throw new InvalidOperationException("The installer fixture requires an existing local SQLite Workspace.");
        return Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(workspace, file), file =>
            {
                using var input = File.OpenRead(file);
                return Convert.ToHexString(SHA256.HashData(input));
            }, StringComparer.OrdinalIgnoreCase);
    }

    internal void RequirePreserved(InstallerPreservationEvidence actual, string operation)
    {
        Validate();
        actual.Validate();
        if (!string.Equals(Workspace, actual.Workspace, StringComparison.OrdinalIgnoreCase) ||
            WorkspaceId != actual.WorkspaceId || Head != actual.Head ||
            !string.Equals(Folder, actual.Folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(operation + " changed the exact Workspace identity, local head, path or synchronization folder.");
        if (PublicStateSha256 != actual.PublicStateSha256 || !ImportReplays.SequenceEqual(actual.ImportReplays))
            throw new InvalidOperationException(operation + " changed complete public fixture state or accepted-import replay.");
        if (actual.PublishedRevision != Head)
            throw new InvalidOperationException(operation + " did not publish the exact locally committed handoff head on normal close.");
        // Only this apply/reopen/normal-close boundary permits SQLite physical rewrites.
        // The exact committed state, head and both accepted fixture Imports were checked above.
        var changed = ChangedFiles(Files, actual.Files)
            .Where(file => file is not ("workspace.sqlite" or "workspace.sqlite-wal" or "workspace.sqlite-shm")).ToArray();
        if (changed.Length != 0)
            throw new InvalidOperationException(operation + " changed or removed private Workspace bytes: " + string.Join(", ", changed));
    }

    internal static string[] ChangedFiles(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual) =>
        expected.Keys.Union(actual.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
            .Where(file => !expected.TryGetValue(file, out var before) ||
                !actual.TryGetValue(file, out var after) || before != after)
            .ToArray();

    internal static InstallerPreservationEvidence Read(string path) =>
        JsonSerializer.Deserialize<InstallerPreservationEvidence>(File.ReadAllBytes(path)) ??
        throw new FormatException("Missing installed-update preservation evidence.");

    internal void Write(string path) => File.WriteAllText(path,
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    private void Validate()
    {
        if (SchemaVersion != 1 || !Path.IsPathFullyQualified(Workspace) || !Path.IsPathFullyQualified(Folder) ||
            WorkspaceId == Guid.Empty || Head == Guid.Empty || !IsHash(PublicStateSha256) ||
            ImportReplays is not { Length: 2 } || ImportReplays.Select(replay => replay.BatchId).Distinct().Count() != 2 ||
            ImportReplays.Any(replay => replay.BatchId == Guid.Empty || !IsHash(replay.SourceSha256) ||
                replay.PhotoSha256 is { } photo && !IsHash(photo)) ||
            Files is null || !Files.ContainsKey("workspace.sqlite") || !Files.ContainsKey("local-workspace.format") ||
            Files.Any(file => !IsHash(file.Value)))
            throw new FormatException("Incomplete or unsupported controlled-installer preservation evidence.");
    }

    private static bool IsHash(string value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal sealed record InstallerImportReplay(Guid BatchId, string SourceSha256, string? PhotoSha256);
