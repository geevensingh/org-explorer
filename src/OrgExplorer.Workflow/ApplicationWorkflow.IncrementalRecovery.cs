using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Workflow;

public partial interface IApplicationWorkflow
{
    bool RetryStorageMaintenance();
    bool RecoverWorkspace(string destinationPath);
}

public sealed partial class WorkspaceViewModel
{
    public bool RetryStorageMaintenance() => Observe(workflow.RetryStorageMaintenance);
    public bool RecoverWorkspace(string destinationPath) => Observe(() => workflow.RecoverWorkspace(destinationPath));
}

public sealed partial class ApplicationWorkflow
{
    private sealed record RecoveryPublication(string Path, string? BeforeDigest, string AfterDigest);

    private sealed record RecoverySelector(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid Transaction,
        [property: JsonRequired] string SourceRevision,
        [property: JsonRequired] IncrementalMetadata Workspace,
        [property: JsonRequired] Dictionary<string, string?> NoteRoots,
        [property: JsonRequired] RecoveryPublication? Publication);

    private sealed record RecoveryReceipt(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid Commit,
        [property: JsonRequired] string Revision,
        [property: JsonRequired] string RecoveryDigest,
        [property: JsonRequired] RecoveryPublication? Publication);

    private sealed record PreparedRecovery(string Root, string Transaction, byte[] Selector, RecoverySelector Metadata);
    private PreparedRecovery? pendingRecovery;
    private string? verifiedRecoveryPath;
    private static readonly JsonSerializerOptions RecoveryJsonOptions = new(JsonOptions) { WriteIndented = false };

    private static string ByteDigest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private PreparedRecovery PrepareRecovery(IReadOnlyDictionary<string, byte[]> current, string transaction,
        RecoveryPublication? publication = null)
    {
        var root = Path.Combine(workspace!, "recovery");
        CreateIncrementalDirectory(root);
        RecoverySelector? previous = null;
        var selected = Path.Combine(root, "current.json");
        if (File.Exists(selected))
        {
            var old = ReadWorkspaceBytesUncached(selected);
            previous = ValidateRecovery(root, old, requireReceipt: true).Metadata;
        }
        var sourceGraph = ResolveIncrementalGraph(workspace!, current[Path.Combine(workspace!, "workspace.json")], current);
        var roots = previous is null ? IndexedFamilies.ToDictionary(f => f, _ => (string?)null) :
            new Dictionary<string, string?>(previous.Workspace.Roots);
        var noteRoots = previous is null ? new Dictionary<string, string?> { ["personNotes"] = null, ["teamNotes"] = null } :
            new Dictionary<string, string?>(previous.NoteRoots);
        var oldDocuments = previous is null ? new Dictionary<string, Dictionary<string, string>>() :
            RecoveryDocuments(root, previous).Documents;
        var available = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var inventory = 0;
        if (previous is not null)
            foreach (var path in RecoveryDocuments(root, previous).Paths)
                available[path] = ReadWorkspaceBytesUncached(path);

        void Write(string relative, byte[] bytes)
        {
            if (relative.StartsWith("index" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                using var node = JsonDocument.Parse(bytes);
                bytes = JsonSerializer.SerializeToUtf8Bytes(node.RootElement, RecoveryJsonOptions);
            }
            WriteRecoveryObject(transaction, ref inventory, Path.Combine("recovery", relative), bytes);
            WriteRecoveryObject(transaction, ref inventory, Path.Combine("recovery", relative + ".sha256"),
                Encoding.ASCII.GetBytes(ByteDigest(bytes)));
            available[Path.Combine(root, relative)] = bytes;
        }
        void Set(string family, string key, byte[] bytes, string extension, Dictionary<string, string?> familyRoots)
        {
            if (oldDocuments.TryGetValue(family, out var documents) && documents.TryGetValue(key, out var prior) &&
                available[IncrementalPath(root, prior, family)].AsSpan().SequenceEqual(bytes)) return;
            var relative = Path.Combine("documents", family, Guid.NewGuid().ToString("N") + extension);
            Write(relative, bytes);
            familyRoots[family] = UpdateIndex(familyRoots[family], family, key, relative, "", available, Write, root);
        }
        var sourceDocuments = sourceGraph.Documents.ToDictionary(e => e.Key, e => e.Value.ToDictionary(
            document => document.Key, document => current[IncrementalPath(workspace!, document.Value, e.Key)]));
        foreach (var (family, directory) in new[] { ("personNotes", "notes"), ("teamNotes", "team-notes") })
            sourceDocuments[family] = current.Where(e =>
                Path.GetDirectoryName(e.Key)!.Equals(Path.Combine(workspace!, directory), StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(e.Key).Equals(".md", StringComparison.OrdinalIgnoreCase)).ToDictionary(
                    e => Path.GetFileNameWithoutExtension(e.Key).ToLowerInvariant(), e => e.Value);
        foreach (var (family, documents) in sourceDocuments)
        {
            var familyRoots = family is "personNotes" or "teamNotes" ? noteRoots : roots;
            if (oldDocuments.TryGetValue(family, out var old))
                foreach (var removed in old.Keys.Except(documents.Keys))
                    familyRoots[family] = RemoveRecoveryIndex(root, familyRoots[family]!, family, removed, "", available, Write);
            foreach (var (key, bytes) in documents)
                Set(family, key, bytes, family == "photos" ? Path.GetExtension(key) :
                    family is "personNotes" or "teamNotes" ? ".md" : ".json", familyRoots);
        }
        var metadata = new RecoverySelector(1, Guid.ParseExact(Path.GetFileName(transaction), "N"),
            WorkspaceFingerprint(snapshot: current), sourceGraph.Metadata with { Roots = roots }, noteRoots, publication);
        var selector = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        var staged = Path.Combine(transaction, "recovery.stage");
        WriteDurablyBytes(staged, selector);
        PublishWorkspaceFile(staged, Path.Combine(transaction, "recovery.next"), null);
        ValidateRecovery(root, selector, requireReceipt: false);
        return new(root, transaction, selector, metadata);
    }

    private static string? RemoveRecoveryIndex(string root, string reference, string family, string key, string prefix,
        IReadOnlyDictionary<string, byte[]> source, Action<string, byte[]> write)
    {
        var node = ReadIndexNode(root, reference, family, prefix, source);
        IndexNode replacement;
        if (node.Entries is { } entries)
        {
            var remaining = new Dictionary<string, string>(entries);
            remaining.Remove(key);
            if (remaining.Count == 0) return null;
            replacement = new(1, family, remaining, null);
        }
        else
        {
            var children = new Dictionary<string, string>(node.Children!);
            var edge = IndexKeyHash(key)[prefix.Length].ToString();
            var child = RemoveRecoveryIndex(root, children[edge], family, key, prefix + edge, source, write);
            if (child is null) children.Remove(edge);
            else children[edge] = child;
            if (children.Count == 0) return null;
            replacement = new(1, family, null, children);
        }
        var relative = Path.Combine("index", Guid.NewGuid().ToString("N") + ".json");
        write(relative, JsonSerializer.SerializeToUtf8Bytes(replacement, JsonOptions));
        return relative;
    }

    private static IncrementalGraph RecoveryDocuments(string root, RecoverySelector metadata,
        IReadOnlyDictionary<string, byte[]>? bytes = null)
    {
        var graph = ResolveIncrementalGraph(root, JsonSerializer.SerializeToUtf8Bytes(metadata.Workspace, JsonOptions), bytes);
        if (!metadata.NoteRoots.Keys.Order().SequenceEqual(new[] { "personNotes", "teamNotes" }))
            throw new FormatException("Incomplete recovery note roots.");
        foreach (var (family, reference) in metadata.NoteRoots)
        {
            var documents = new Dictionary<string, string>();
            graph.Documents.Add(family, documents);
            if (reference is not null) Visit(reference, "");
            void Visit(string relative, string prefix)
            {
                if (!graph.Paths.Add(IncrementalPath(root, relative)))
                    throw new FormatException("Duplicate recovery index reference.");
                var node = ReadIndexNode(root, relative, family, prefix, bytes);
                if (node.Entries is { } entries)
                    foreach (var (key, value) in entries)
                    {
                        if (!Guid.TryParseExact(key, "D", out var id) || id == Guid.Empty ||
                            !documents.TryAdd(key, value) || !graph.Paths.Add(IncrementalPath(root, value, family)))
                            throw new FormatException("Invalid or duplicate recovery note identity.");
                    }
                else
                    foreach (var (edge, child) in node.Children!) Visit(child, prefix + edge);
            }
        }
        return graph;
    }

    private (RecoverySelector Metadata, Dictionary<string, byte[]> Bytes) ValidateRecovery(string root, byte[] selector,
        bool requireReceipt)
    {
        RejectWorkspaceLink(root);
        using var json = JsonDocument.Parse(selector);
        CheckProperties(json.RootElement, ["schemaVersion", "transaction", "sourceRevision", "workspace", "noteRoots", "publication"], "Recovery");
        var metadata = JsonSerializer.Deserialize<RecoverySelector>(selector, JsonOptions)
            ?? throw new FormatException("Invalid recovery selector.");
        if (metadata.SchemaVersion != 1 || metadata.Transaction == Guid.Empty || metadata.Workspace is null ||
            metadata.NoteRoots is null || string.IsNullOrWhiteSpace(metadata.SourceRevision))
            throw new FormatException("Invalid recovery provenance.");
        ReadIncrementalMetadata(Encoding.UTF8.GetBytes(json.RootElement.GetProperty("workspace").GetRawText()));
        CheckProperties(json.RootElement.GetProperty("noteRoots"), ["personNotes", "teamNotes"], "Recovery notes");
        if (requireReceipt)
        {
            var receiptPath = Path.Combine(workspace!, "transactions", metadata.Transaction.ToString("N"), "committed.json");
            for (var entry = receiptPath; !entry.Equals(workspace, StringComparison.OrdinalIgnoreCase);
                 entry = Path.GetDirectoryName(entry)!)
                if (File.Exists(entry) || Directory.Exists(entry)) RejectWorkspaceLink(entry);
            using var receiptJson = JsonDocument.Parse(ReadWorkspaceBytesUncached(receiptPath));
            CheckProperties(receiptJson.RootElement, ["schemaVersion", "commit", "revision", "recoveryDigest", "publication"], "Recovery receipt");
            var receipt = receiptJson.RootElement.Deserialize<RecoveryReceipt>(JsonOptions)
                ?? throw new FormatException("Missing committed recovery receipt.");
            if (receipt.SchemaVersion != 1 || receipt.Commit != metadata.Transaction ||
                receipt.Revision != metadata.SourceRevision || receipt.RecoveryDigest != ByteDigest(selector) ||
                receipt.Publication != metadata.Publication)
                throw new FormatException("Recovery does not match its committed receipt.");
        }
        var graph = RecoveryDocuments(root, metadata);
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in graph.Paths)
        {
            var data = ReadWorkspaceBytesUncached(path);
            var seal = path + ".sha256";
            RejectWorkspaceLink(seal);
            if (Encoding.ASCII.GetString(ReadWorkspaceBytesUncached(seal)) != ByteDigest(data))
                throw new IOException("Recovery seal does not match its protected bytes.");
            bytes[path] = data;
        }
        graph = RecoveryDocuments(root, metadata, bytes);
        foreach (var (family, directory) in new[] { ("personNotes", "notes"), ("teamNotes", "team-notes") })
            foreach (var (key, relative) in graph.Documents[family])
                bytes[Path.Combine(root, directory, key + ".md")] = bytes[IncrementalPath(root, relative, family)];
        bytes[Path.Combine(root, "workspace.json")] = JsonSerializer.SerializeToUtf8Bytes(metadata.Workspace, JsonOptions);
        using var validation = new ApplicationWorkflow(false, null, timeProvider) { workspace = root, incrementalStorage = true };
        validation.ReadIncrementalWorkspaceData(bytes);
        return (metadata, bytes);
    }

    private void CompleteRecovery(PreparedRecovery prepared)
    {
        ValidateRecovery(prepared.Root, prepared.Selector, requireReceipt: false);
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new RecoveryReceipt(1, prepared.Metadata.Transaction,
            prepared.Metadata.SourceRevision, ByteDigest(prepared.Selector), prepared.Metadata.Publication), JsonOptions);
        var receiptPath = Path.Combine(prepared.Transaction, "committed.json");
        if (File.Exists(receiptPath))
        {
            if (!ReadWorkspaceBytesUncached(receiptPath).AsSpan().SequenceEqual(receipt))
                throw new IOException("Committed recovery receipt changed; maintenance cannot overwrite it.");
        }
        else
        {
            var receiptStage = Path.Combine(prepared.Transaction, Guid.NewGuid().ToString("N") + ".receipt");
            WriteDurablyBytes(receiptStage, receipt);
            PublishWorkspaceFile(receiptStage, receiptPath, null);
        }
        ValidateRecovery(prepared.Root, prepared.Selector, requireReceipt: true);
        // A failed replacement consumes neither the selected root nor its prior transition root.
        var staged = Path.Combine(prepared.Transaction, Guid.NewGuid().ToString("N") + ".recovery");
        WriteDurablyBytes(staged, prepared.Selector);
        PublishWorkspaceFile(staged, Path.Combine(prepared.Root, "current.json"),
            Path.Combine(prepared.Transaction, "recovery.previous.json"));
        if (!ReadWorkspaceBytesUncached(Path.Combine(prepared.Root, "current.json")).AsSpan().SequenceEqual(prepared.Selector))
            throw new IOException("Recovery selection changed during publication.");
        ValidateRecovery(prepared.Root, prepared.Selector, requireReceipt: true);
        verifiedRecoveryPath = prepared.Root;
        compatibility = compatibility with { RecoveryPath = prepared.Root };
        pendingRecovery = null;
        failedSaves.Remove("storage-maintenance");
    }

    private void FinishCommittedRecovery(PreparedRecovery prepared)
    {
        pendingRecovery = prepared;
        try { CompleteRecovery(prepared); }
        catch (Exception ex) when (StorageError(ex))
        {
            SaveFailed("storage-maintenance", "Saved locally; recovery maintenance incomplete: " + ex.Message);
        }
    }

    private void CheckRecoveryOnOpen(IReadOnlyDictionary<string, byte[]> current)
    {
        pendingRecovery = null;
        verifiedRecoveryPath = null;
        try
        {
            var root = Path.Combine(workspace!, "recovery");
            var selected = ValidateRecovery(root, ReadWorkspaceBytesUncached(Path.Combine(root, "current.json")), true);
            verifiedRecoveryPath = root;
            if (selected.Metadata.SourceRevision != WorkspaceFingerprint(snapshot: current))
                throw new IOException("The current validated revision is not yet independently protected.");
            failedSaves.Remove("storage-maintenance");
        }
        catch (Exception ex) when (StorageError(ex))
        {
            SaveFailed("storage-maintenance", "Saved locally; recovery maintenance incomplete: " + ex.Message);
        }
    }

    public bool RetryStorageMaintenance()
    {
        lock (workspaceGate)
        {
            if (!incrementalStorage || workspace is null) return Fail("No incremental Workspace maintenance is available.");
            try
            {
                using var access = CoordinateWorkspaceIo(workspace);
                EnsureWorkspaceWritable();
                using var lease = new FileStream(Path.Combine(workspace, ".workspace.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (pendingRecovery is { } prepared) CompleteRecovery(prepared);
                else
                {
                    var current = CaptureWorkspaceBytes();
                    using var validation = new ApplicationWorkflow(false, null, timeProvider) { workspace = workspace, incrementalStorage = true };
                    validation.ReadIncrementalWorkspaceData(current);
                    var transaction = BeginRecoveryTransaction("accepted-current", WorkspaceFingerprint(snapshot: current));
                    var candidate = PrepareRecovery(current, transaction);
                    if (WorkspaceFingerprint() != candidate.Metadata.SourceRevision)
                        throw new IOException("Current delivery changed during recovery capture; no recovery selected.");
                    CompleteRecovery(candidate);
                }
                Status = "Recovery maintenance completed; no current mutation was replayed.";
                return true;
            }
            catch (Exception ex) when (StorageError(ex))
            {
                return SaveFailed("storage-maintenance", "Saved locally; recovery maintenance incomplete: " + ex.Message);
            }
        }
    }

    private string BeginRecoveryTransaction(string operation, string? baseRevision)
    {
        var id = Guid.NewGuid();
        var transaction = Path.Combine(workspace!, "transactions", id.ToString("N"));
        CreateIncrementalDirectory(transaction);
        WriteDurablyBytes(Path.Combine(transaction, "intent.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, commit = id, baseRevision, operation }, JsonOptions));
        return transaction;
    }

    private void WriteRecoveryObject(string transaction, ref int inventory, string relative, byte[] bytes)
    {
        var staged = relative + ".next";
        WriteDurablyBytes(Path.Combine(transaction, $"created-recovery-{inventory++:D8}.json"),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, path = relative, stagingPath = staged, bytes = bytes.LongLength, digest = ByteDigest(bytes)
            }, RecoveryJsonOptions));
        var path = Path.Combine(workspace!, relative);
        CreateIncrementalDirectory(Path.GetDirectoryName(path)!);
        WriteDurablyBytes(Path.Combine(workspace!, staged), bytes);
        PublishWorkspaceFile(Path.Combine(workspace!, staged), path, null);
    }

    public bool RecoverWorkspace(string destinationPath)
    {
        lock (workspaceGate)
        {
            if (!incrementalStorage || workspace is null) return Fail("No incremental recovery source is open.");
            if (!CanSafelyClose) return Fail("Resolve retained writing and recovery maintenance before switching to a recovered Workspace.");
            try
            {
                using var access = CoordinateWorkspaceIo(workspace);
                var source = workspace;
                var root = Path.Combine(source, "recovery");
                var path = Path.Combine(root, "current.json");
                RejectWorkspaceLink(path);
                var selector = ReadWorkspaceBytesUncached(path);
                var recovery = ValidateRecovery(root, selector, true);
                var destination = Path.GetFullPath(destinationPath);
                if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                    throw new IOException("Choose an empty isolated recovery destination outside the source Workspace.");
                for (var directory = destination; directory is not null; directory = Path.GetDirectoryName(directory))
                    if (Directory.Exists(directory)) RejectWorkspaceLink(directory);
                foreach (var (file, bytes) in recovery.Bytes)
                {
                    var target = Path.Combine(destination, Path.GetRelativePath(root, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    for (var entry = target; !entry.Equals(destination, StringComparison.OrdinalIgnoreCase);
                         entry = Path.GetDirectoryName(entry)!)
                        if (File.Exists(entry) || Directory.Exists(entry)) RejectWorkspaceLink(entry);
                    WriteDurablyBytes(target, bytes);
                }
                var after = ValidateRecovery(root, selector, true);
                if (recovery.Bytes.Count != after.Bytes.Count || recovery.Bytes.Any(e =>
                    !after.Bytes.TryGetValue(e.Key, out var value) || !value.AsSpan().SequenceEqual(e.Value)))
                    throw new IOException("Recovery bytes changed while copying; destination has not been opened.");
                if (!ReadWorkspaceBytesUncached(path).AsSpan().SequenceEqual(selector))
                    throw new IOException("Recovery selection changed while copying; destination has not been opened.");
                using (var validation = new ApplicationWorkflow(false, null, timeProvider) { workspace = destination, incrementalStorage = true })
                    validation.ReadIncrementalWorkspaceData(recovery.Bytes.ToDictionary(e =>
                        Path.Combine(destination, Path.GetRelativePath(root, e.Key)), e => ReadWorkspaceBytesUncached(
                            Path.Combine(destination, Path.GetRelativePath(root, e.Key))), StringComparer.OrdinalIgnoreCase));
                return OpenWorkspaceCore(destination, WorkspaceOpenMode.ExistingOnly);
            }
            catch (Exception ex) when (StorageError(ex))
            {
                return Fail("Recovery was not opened; source bytes are unchanged: " + ex.Message);
            }
        }
    }
}
