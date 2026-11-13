using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private bool storageReclamationDeferred;
    private sealed record CreationInventory(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string Path,
        [property: JsonRequired] string StagingPath,
        [property: JsonRequired] long Bytes,
        [property: JsonRequired] string Digest);

    private sealed record CreationInventoryBatch(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] CreationInventory[] Objects);

    private sealed record StorageRetirement(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid Transaction,
        [property: JsonRequired] string Revision,
        [property: JsonRequired] string RecoveryDigest,
        [property: JsonRequired] Dictionary<string, string> Files);

    private sealed record ReclamationPlan(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid Transaction,
        [property: JsonRequired] string Revision,
        [property: JsonRequired] string RecoveryDigest);

    // Called with the local I/O gate and writer lease, only after recovery selection.
    private void ReclaimIncrementalStorage(PreparedRecovery prepared, ValidatedRecovery selected) =>
        ObserveStoragePhase("storage.reclamation", () => ReclaimIncrementalStorageCore(prepared, selected));

    private void ReclaimIncrementalStorageCore(PreparedRecovery prepared, ValidatedRecovery selected)
    {
        LoadPublicationConflicts();
        if (workspaceConflicts.Count != 0)
            throw new IOException("Publication conflict evidence must remain protected until explicit resolution.");
        var current = CaptureWorkspaceBytes();
        using (var validation = new ApplicationWorkflow(false, null, timeProvider, fileObserver: fileObserver) { workspace = workspace })
            validation.ReadIncrementalWorkspaceData(current);
        var accepted = WorkspaceFingerprint(snapshot: current);
        if (accepted != prepared.Metadata.SourceRevision)
            throw new IOException("Current delivery changed; storage reclamation is deferred.");
        var selectedPath = Path.Combine(workspace!, "recovery", "current.json");
        var selectedBytes = ReadRecoverySelectorBytes(prepared.Root);
        if (selected.Metadata.SourceRevision != accepted || !selectedBytes.AsSpan().SequenceEqual(prepared.Selector))
            throw new IOException("Recovery selection changed; storage reclamation is deferred.");

        var maintenance = Path.Combine(workspace!, "maintenance");
        CreateIncrementalDirectory(maintenance);
        var pending = Path.Combine(maintenance, "reclamation.json");
        if (!File.Exists(pending))
        {
            var stage = Path.Combine(maintenance, Guid.NewGuid().ToString("N") + ".plan");
            WriteDurablyBytes(stage, JsonSerializer.SerializeToUtf8Bytes(
                new ReclamationPlan(1, selected.Metadata.Transaction, accepted, ByteDigest(selectedBytes)), IncrementalJsonOptions));
            PublishWorkspaceFile(stage, pending, null);
        }
        RejectStoragePathLinks(pending);
        var pendingBytes = ReadWorkspaceBytesUncached(pending);
        using (var json = JsonDocument.Parse(pendingBytes))
        {
            CheckProperties(json.RootElement, ["schemaVersion", "transaction", "revision", "recoveryDigest"], "Reclamation plan");
            var plan = json.RootElement.Deserialize<ReclamationPlan>(JsonOptions);
            if (plan is null || plan.SchemaVersion != 1 || plan.Transaction == Guid.Empty ||
                !ValidDigest(plan.Revision) || !ValidDigest(plan.RecoveryDigest))
                throw new FormatException("Invalid pending reclamation plan; preserved without deletion.");
        }
        var retirementPath = Path.Combine(prepared.Transaction, "retired.json");
        if (!File.Exists(retirementPath))
        {
            var files = TransactionFiles(prepared.Transaction)
                .Where(path => IsCreationInventory(Path.GetFileName(path)) ||
                    IsPublicationMetadata(Path.GetFileName(path)) ||
                    Path.GetFileName(path) is "intent.json" or "committed.json" or "selector.next" or
                        "recovery.next" or "recovery.previous.json")
                .ToDictionary(path => Path.GetFileName(path), path => ByteDigest(ReadWorkspaceBytesUncached(path)), StringComparer.Ordinal);
            var retirement = new StorageRetirement(1, prepared.Metadata.Transaction, accepted, ByteDigest(selectedBytes), files);
            var stage = Path.Combine(prepared.Transaction, Guid.NewGuid().ToString("N") + ".retirement");
            WriteDurablyBytes(stage, JsonSerializer.SerializeToUtf8Bytes(retirement, IncrementalJsonOptions));
            PublishWorkspaceFile(stage, retirementPath, null);
        }

        var protectedPaths = new HashSet<string>(current.Keys, StringComparer.OrdinalIgnoreCase);
        var guards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Guard(string path, byte[]? expected = null)
        {
            if (expected is null) RejectStoragePathLinks(path);
            var digest = ByteDigest(expected ?? ReadWorkspaceBytesUncached(path));
            if (guards.TryGetValue(path, out var prior) && prior != digest)
                throw new IOException("Protected storage changed during root capture; storage reclamation is deferred.");
            guards[path] = digest;
        }
        void ProtectGraph(IncrementalGraph graph, bool recovery, IReadOnlyDictionary<string, byte[]> bytes)
        {
            foreach (var path in graph.Paths)
            {
                protectedPaths.Add(path);
                Guard(path, bytes[path]);
                if (recovery)
                {
                    protectedPaths.Add(path + ".sha256");
                    Guard(path + ".sha256", System.Text.Encoding.ASCII.GetBytes(ByteDigest(bytes[path])));
                }
            }
        }
        void ProtectRecovery(RecoverySelector metadata, IReadOnlyDictionary<string, byte[]> bytes, byte[]? receipt)
        {
            ProtectGraph(RecoveryDocuments(prepared.Root, metadata, bytes), true, bytes);
            var receiptPath = Path.Combine(workspace!, "transactions", metadata.Transaction.ToString("N"), "committed.json");
            protectedPaths.Add(receiptPath);
            if (receipt is not null) Guard(receiptPath, receipt);
            else if (File.Exists(receiptPath)) Guard(receiptPath);
        }
        void ProtectCurrentCandidate(byte[] selector)
        {
            var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var graph = ResolveIncrementalGraph(workspace!, selector, capturedIndexes: bytes);
            foreach (var path in graph.Paths)
                if (!bytes.ContainsKey(path)) bytes.Add(path, ReadWorkspaceBytesUncached(path));
            ProtectGraph(ResolveIncrementalGraph(workspace!, selector, bytes), false, bytes);
        }
        Guard(pending);
        Guard(selectedPath, selectedBytes);
        ProtectRecovery(selected.Metadata, selected.Bytes, selected.Receipt);
        var previous = Path.Combine(workspace!, "workspace.previous.json");
        var previousDigest = File.Exists(previous) ? ByteDigest(ReadWorkspaceBytesUncached(previous)) : null;
        if (previousDigest is not null)
        {
            Guard(previous);
            ProtectCurrentCandidate(ReadWorkspaceBytesUncached(previous));
        }
        ProtectPublicationRoots(prepared.Transaction);

        void ProtectPublicationRoots(string transaction)
        {
            var baseline = Path.Combine(transaction, "publication.base.json");
            byte[]? baselineBytes = null;
            if (File.Exists(baseline))
            {
                RejectStoragePathLinks(baseline, false);
                baselineBytes = ReadWorkspaceBytesUncached(baseline);
                Guard(baseline, baselineBytes);
                protectedPaths.Add(baseline);
                ProtectCurrentCandidate(baselineBytes);
            }
            var displaced = Path.Combine(transaction, "publication.displaced");
            if (!File.Exists(displaced)) return;
            RejectStoragePathLinks(displaced, false);
            var displacedBytes = ReadWorkspaceBytesUncached(displaced);
            Guard(displaced, displacedBytes);
            protectedPaths.Add(displaced);
            var intent = ReadPublicationRecord<PublicationIntent>(Path.Combine(transaction, "publication.json"));
            if (intent.Target == "workspace.json" &&
                (baselineBytes is null || !baselineBytes.AsSpan().SequenceEqual(displacedBytes)))
                ProtectCurrentCandidate(displacedBytes);
        }

        var transactionsRoot = Path.Combine(workspace!, "transactions");
        RejectStoragePathLinks(transactionsRoot);
        var transactions = Directory.GetDirectories(transactionsRoot);
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retired = new List<(string Directory, StorageRetirement Retirement)>();
        var deferObjects = false;
        foreach (var transaction in transactions)
        {
            RejectStoragePathLinks(transaction);
            var files = TransactionFiles(transaction);
            foreach (var file in files) knownFiles.Add(file);
            var retiredPath = Path.Combine(transaction, "retired.json");
            if (File.Exists(retiredPath))
            {
                var retirement = ReadStorageRetirement(retiredPath);
                Guard(retiredPath);
                foreach (var file in files.Where(path => IsPublicationMetadata(Path.GetFileName(path)) || Path.GetFileName(path) is
                    "intent.json" or "committed.json" or "selector.next" or "recovery.next" or "recovery.previous.json"))
                {
                    if (!retirement.Files.TryGetValue(Path.GetFileName(file), out var digest) ||
                        ByteDigest(ReadWorkspaceBytesUncached(file)) != digest)
                        throw new IOException("Retired transaction metadata changed; storage reclamation is deferred.");
                    Guard(file);
                }
                retired.Add((transaction, retirement));
                continue;
            }
            foreach (var file in files) Guard(file);
            if (File.Exists(Path.Combine(transaction, "publication.resolved.json")))
            {
                // Explicitly acknowledged conflicts retain raw evidence and source objects, not a guessed closure.
                deferObjects = true;
                continue;
            }
            ProtectPublicationRoots(transaction);
            // Missing receipts do not prove abortion. Incomplete intents may still
            // need reused base objects for which no candidate has yet been written.
            deferObjects |= files.Length != 0 && !File.Exists(Path.Combine(transaction, "recovery.next"));
            foreach (var file in files.Where(path => IsCreationInventory(Path.GetFileName(path))))
            {
                foreach (var (_, path, staged) in ReadCreationInventories(ReadWorkspaceBytesUncached(file)))
                {
                    protectedPaths.Add(path);
                    protectedPaths.Add(staged);
                }
            }
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name == "selector.next")
                    ProtectCurrentCandidate(ReadWorkspaceBytesUncached(file));
                if (name is "recovery.next" or "recovery.previous.json")
                {
                    var bytes = ReadWorkspaceBytesUncached(file);
                    var recovery = ValidateRecovery(prepared.Root, bytes, requireReceipt: name == "recovery.previous.json");
                    ProtectRecovery(recovery.Metadata, recovery.Bytes, recovery.Receipt);
                }
            }
        }

        void Recheck() => ObserveStoragePhase("storage.reclamation-recheck", RecheckCore);
        void RecheckCore()
        {
            var actual = CaptureWorkspaceBytes();
            if (WorkspaceFingerprint(snapshot: actual) != accepted ||
                (File.Exists(previous) ? ByteDigest(ReadWorkspaceBytesUncached(previous)) : null) != previousDigest ||
                !Directory.GetDirectories(transactionsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(transactions.Where(Directory.Exists)) ||
                !transactions.Where(Directory.Exists).SelectMany(TransactionFiles)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(knownFiles))
                throw new IOException("Workspace or transaction delivery changed; storage reclamation is deferred.");
            actual[selectedPath] = ReadRecoverySelectorBytes(prepared.Root);
            RunBoundedFileIo(guards, guard =>
            {
                byte[] bytes;
                if (actual.TryGetValue(guard.Key, out var captured)) bytes = captured;
                else
                {
                    RejectStoragePathLinks(guard.Key);
                    bytes = ReadWorkspaceBytesUncached(guard.Key);
                }
                if (ByteDigest(bytes) != guard.Value)
                    throw new IOException("Protected storage changed; storage reclamation is deferred.");
            });
        }
        var batchSize = 0;
        void DeleteRecorded(string path, string digest, bool rootsAlreadyChecked = false)
        {
            if (!File.Exists(path)) return;
            if (batchSize == 0 && !rootsAlreadyChecked) Recheck();
            RejectStoragePathLinks(path);
            if (ByteDigest(ReadWorkspaceBytesUncached(path)) != digest) return;
            DeleteWorkspaceFile(path);
            knownFiles.Remove(path);
            guards.Remove(path);
            if (++batchSize == 32)
            {
                Recheck();
                batchSize = 0;
            }
        }

        foreach (var (transaction, retirement) in retired)
        {
            var inventories = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
            RunBoundedFileIo(retirement.Files.Keys.Where(IsCreationInventory), name =>
            {
                var record = Path.Combine(transaction, name);
                if (File.Exists(record)) inventories[name] = ReadWorkspaceBytesUncached(record);
            });
            foreach (var (name, digest) in retirement.Files.Where(e => IsCreationInventory(e.Key)))
            {
                var record = Path.Combine(transaction, name);
                if (!inventories.TryGetValue(name, out var recordBytes)) continue;
                if (ByteDigest(recordBytes) != digest) continue;
                var retainInventory = false;
                foreach (var (inventory, path, staged) in ReadCreationInventories(recordBytes))
                {
                    if (deferObjects || protectedPaths.Contains(path) || protectedPaths.Contains(staged))
                    {
                        retainInventory = true;
                        continue;
                    }
                    RejectStoragePathLinks(path);
                    RejectStoragePathLinks(staged);
                    if (File.Exists(path) && new FileInfo(path).Length == inventory.Bytes)
                        DeleteRecorded(path, inventory.Digest);
                    if (File.Exists(staged) && new FileInfo(staged).Length == inventory.Bytes)
                        DeleteRecorded(staged, inventory.Digest);
                }
                // Changed bytes become unknown files, never cleanup candidates.
                if (!retainInventory) DeleteRecorded(record, digest);
            }
            if (TransactionFiles(transaction).Any(path => IsCreationInventory(Path.GetFileName(path))) ||
                protectedPaths.Contains(Path.Combine(transaction, "committed.json"))) continue;
            foreach (var (name, digest) in retirement.Files)
                if (!IsCreationInventory(name)) DeleteRecorded(Path.Combine(transaction, name), digest);
            var marker = Path.Combine(transaction, "retired.json");
            if (Directory.EnumerateFileSystemEntries(transaction).All(path => path == marker))
            {
                DeleteRecorded(marker, guards[marker]);
                Directory.Delete(transaction, recursive: false);
            }
        }
        Recheck();
        DeleteRecorded(pending, ByteDigest(pendingBytes), rootsAlreadyChecked: true);
        if (File.Exists(pending))
            throw new IOException("Reclamation plan changed before completion; storage maintenance remains incomplete.");
        storageReclamationDeferred = transactions.Any(transaction => Directory.Exists(transaction) &&
            !File.Exists(Path.Combine(transaction, "retired.json")) && TransactionFiles(transaction).Length != 0);
        failedSaves.Remove("storage-maintenance");
    }

    private string[] TransactionFiles(string directory)
    {
        RejectStoragePathLinks(directory);
        // Unknown subdirectories are neither traversed nor removed.
        return Directory.GetFiles(directory);
    }

    private static bool IsCreationInventory(string name) =>
        Regex.IsMatch(name, @"^created-(recovery-)?[0-9]{8}\.json$");

    private StorageRetirement ReadStorageRetirement(string path)
    {
        using var json = JsonDocument.Parse(ReadWorkspaceBytesUncached(path));
        CheckProperties(json.RootElement, ["schemaVersion", "transaction", "revision", "recoveryDigest", "files"], "Storage retirement");
        var retirement = json.RootElement.Deserialize<StorageRetirement>(JsonOptions)
            ?? throw new FormatException("Invalid storage retirement.");
        if (retirement.SchemaVersion != 1 || retirement.Transaction == Guid.Empty ||
            retirement.Transaction.ToString("N") != Path.GetFileName(Path.GetDirectoryName(path)) ||
            retirement.Files is null || !ValidDigest(retirement.Revision) || !ValidDigest(retirement.RecoveryDigest))
            throw new FormatException("Invalid storage retirement provenance.");
        foreach (var (name, digest) in retirement.Files)
            if ((!IsCreationInventory(name) && !IsPublicationMetadata(name) && name is not ("intent.json" or "committed.json" or
                "selector.next" or "recovery.next" or "recovery.previous.json")) || !ValidDigest(digest))
                throw new FormatException("Invalid retired file identity.");
        return retirement;
    }

    private (CreationInventory Inventory, string Path, string Staged)[] ReadCreationInventories(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Creation inventory: expected an object.");
        CreationInventory[] inventories;
        if (json.RootElement.TryGetProperty("objects", out var objects))
        {
            CheckProperties(json.RootElement, ["schemaVersion", "objects"], "Creation inventory batch");
            var batch = json.RootElement.Deserialize<CreationInventoryBatch>(JsonOptions)
                ?? throw new FormatException("Invalid creation inventory batch.");
            if (batch.SchemaVersion != 2 || batch.Objects is null || batch.Objects.Length is < 1 or > 32)
                throw new FormatException("Invalid creation inventory batch provenance.");
            foreach (var item in objects.EnumerateArray()) CheckInventoryProperties(item);
            inventories = batch.Objects;
        }
        else
        {
            CheckInventoryProperties(json.RootElement);
            inventories = [json.RootElement.Deserialize<CreationInventory>(JsonOptions)
                ?? throw new FormatException("Invalid creation inventory.")];
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return inventories.Select(inventory =>
        {
            if (inventory is null || inventory.SchemaVersion != 1 || inventory.Bytes < 0 || !ValidDigest(inventory.Digest))
                throw new FormatException("Invalid creation inventory provenance.");
            var path = InventoryObjectPath(inventory.Path);
            var staged = InventoryStagePath(inventory.StagingPath);
            if (!paths.Add(path) || !paths.Add(staged))
                throw new FormatException("Duplicate creation inventory location.");
            return (inventory, path, staged);
        }).ToArray();

        static void CheckInventoryProperties(JsonElement item) =>
            CheckProperties(item, ["schemaVersion", "path", "stagingPath", "bytes", "digest"], "Creation inventory");
    }

    private static bool ValidDigest(string value) => value is not null && Regex.IsMatch(value, "^[A-F0-9]{64}$");

    private string InventoryObjectPath(string relative)
    {
        if (relative is null) throw new FormatException("Missing inventoried path.");
        var normalized = relative.Replace('/', '\\');
        var recovery = normalized.StartsWith("recovery\\", StringComparison.Ordinal);
        var root = recovery ? Path.Combine(workspace!, "recovery") : workspace!;
        var local = recovery ? normalized["recovery\\".Length..] : normalized;
        var seal = recovery && local.EndsWith(".sha256", StringComparison.Ordinal);
        if (seal) local = local[..^".sha256".Length];
        string path;
        if (local.StartsWith("index\\", StringComparison.Ordinal)) path = IncrementalReference(root, local.Replace('\\', '/'));
        else
        {
            var parts = local.Split('\\');
            var families = recovery ? IndexedFamilies.Concat(["personNotes", "teamNotes"]) : IndexedFamilies;
            if (parts.Length != 3 || !families.Contains(parts[1]))
                throw new FormatException("Invalid inventoried document family.");
            path = IncrementalReference(root, local.Replace('\\', '/'), parts[1]);
        }
        path += seal ? ".sha256" : "";
        return path;
    }

    private string InventoryStagePath(string relative)
    {
        if (relative is null) throw new FormatException("Missing inventoried staging path.");
        var local = relative.Replace('/', '\\');
        var recovery = local.StartsWith("recovery\\", StringComparison.Ordinal);
        if (recovery) local = local["recovery\\".Length..];
        var families = recovery ? IndexedFamilies.Concat(["personNotes", "teamNotes"]) : IndexedFamilies;
        var locations = @"index\\[a-f0-9]{32}|documents\\(" + string.Join("|", families) + @")\\[a-f0-9]{32}";
        if (recovery) locations += @"|[a-f0-9]{32}";
        if (!Regex.IsMatch(local, "^(" + locations + @")\.next$"))
            throw new FormatException("Invalid inventoried staging path.");
        var path = Path.Combine(workspace!, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        return path;
    }

    private void RejectStoragePathLinks(string path, bool allowMissing = true)
    {
        for (var entry = path; !entry.Equals(workspace, StringComparison.OrdinalIgnoreCase);
             entry = Path.GetDirectoryName(entry)!)
            RejectWorkspaceLink(entry, allowMissing);
        RejectWorkspaceLink(workspace!);
    }
}
