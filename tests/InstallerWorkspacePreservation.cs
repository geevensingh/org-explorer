using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

internal static class InstallerWorkspacePreservation
{
    internal const string InitialImport = """
        {"schemaVersion":1,"batchId":"16000000-0000-4000-8000-000000000001",
        "observedAt":"2026-11-01T17:00:00Z","people":[
        {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null},
        {"email":"avery@example.com","displayName":"Avery Example","alias":"avery",
         "jobTitle":"Engineer","department":"Research","officeNumber":"007B",
         "managerEmail":"morgan@example.com","photoPath":"avery.png"},
        {"email":"alex@example.com","displayName":"Alex Example","managerEmail":"casey@example.com"},
        {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":"casey@example.com"}]}
        """;
    internal const string TransferImport = """
        {"schemaVersion":1,"batchId":"16000000-0000-4000-8000-000000000002",
        "observedAt":"2026-11-03T17:00:00Z","people":[
        {"email":"morgan@example.com","archived":true},
        {"email":"avery@example.com","managerEmail":"alex@example.com","officeNumber":"0002B","department":null}]}
        """;

    internal static InstallerPreservationEvidence Capture(string workspace, string replayDirectory)
    {
        var full = Path.GetFullPath(workspace);
        var replay = Path.GetFullPath(replayDirectory);
        if (replay.Equals(full, StringComparison.OrdinalIgnoreCase) ||
            replay.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(replay + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(replay))
            throw new IOException("Replay observations require a fresh directory outside the private Workspace.");
        InstallerPreservationEvidence evidence;
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            Require(app.OpenWorkspace(full, WorkspaceOpenMode.ExistingOnly), app.Status);
            var state = app.Synchronization;
            Require(app.IsWorkspaceCurrent && app.CanSafelyClose && app.LocalSaveError is null &&
                app.OperationError is null && state is
                { Supported: true, WorkspaceId: not null, LocalRevision: not null, Folder: not null, Error: null, Diverged: false },
                "The installed-update observation requires healthy, locally committed fixture state, not publication.");
            var publicHash = WorkspacePublicState.Hash(app);
            // This fixture never replaces its initial imported photo. Reconstruct only its removed producer input.
            var photo = app.GetPhoto("avery@example.com") ??
                throw new InvalidOperationException("The controlled fixture's original imported photo is missing.");
            Directory.CreateDirectory(replay);
            File.WriteAllBytes(Path.Combine(replay, "avery.png"), photo.Bytes.ToArray());
            var replays = new[] { InitialImport, TransferImport }.Select((source, index) =>
            {
                var path = Path.Combine(replay, $"import-{index}.json");
                File.WriteAllText(path, source);
                var preview = app.PreviewImportFile(path);
                Require(preview.CanApply && preview.Errors.Count == 0 && preview.Warnings.Count == 0 &&
                    preview.Changes.SequenceEqual(["Already applied: exact replay is a no-op."]),
                    $"The controlled fixture's accepted Import {index + 1} no longer replays exactly: {app.Status}");
                using var json = JsonDocument.Parse(source);
                return new InstallerImportReplay(json.RootElement.GetProperty("batchId").GetGuid(),
                    Hash(Encoding.UTF8.GetBytes(source)), index == 0 ? Hash(photo.Bytes.Span) : null);
            }).ToArray();
            Require(app.Synchronization.WorkspaceId == state.WorkspaceId &&
                app.Synchronization.LocalRevision == state.LocalRevision &&
                WorkspacePublicState.Hash(app) == publicHash,
                "Replay preview changed the observed private fixture state.");
            evidence = new(1, full, state.WorkspaceId!.Value, state.LocalRevision!.Value, state.Folder!,
                state.PublishedRevision, publicHash, replays, []);
        }
        return evidence with { Files = InstallerPreservationEvidence.SnapshotFiles(full) };
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
