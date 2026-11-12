using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Workflow;

public enum WorkspacePublicationOutcome { None, NotPublished, Published, PublishedConflict, Indeterminate }
public sealed record WorkspaceConflictEvidence(string OriginalPath, string Path, string Digest);
public sealed record WorkspaceConflict(Guid Id, WorkspacePublicationOutcome Outcome, bool CaptureComplete,
    IReadOnlyList<WorkspaceConflictEvidence> Evidence, IReadOnlyList<string> LocalWritingPaths);
public sealed record WorkspaceConflictReview(
    [property: JsonRequired] string Revision,
    [property: JsonRequired] IReadOnlyList<Guid> ConflictIds,
    [property: JsonRequired] string EvidenceRevision);

public partial interface IApplicationWorkflow
{
    WorkspacePublicationOutcome PublicationOutcome { get; }
    IReadOnlyList<WorkspaceConflict> GetWorkspaceConflicts();
    WorkspaceConflictReview? ReviewWorkspaceConflicts();
    bool ResolveWorkspaceConflicts(WorkspaceConflictReview review);
}

public sealed partial class WorkspaceViewModel
{
    public WorkspacePublicationOutcome PublicationOutcome => workflow.PublicationOutcome;
    public IReadOnlyList<WorkspaceConflict> GetWorkspaceConflicts() => workflow.GetWorkspaceConflicts();
    public WorkspaceConflictReview? ReviewWorkspaceConflicts()
    {
        var review = workflow.ReviewWorkspaceConflicts();
        Notify();
        return review;
    }
    public bool ResolveWorkspaceConflicts(WorkspaceConflictReview review) =>
        Observe(() => workflow.ResolveWorkspaceConflicts(review));
}

public sealed partial class ApplicationWorkflow
{
    private sealed record PublicationIntent(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid Id,
        [property: JsonRequired] string Target,
        [property: JsonRequired] string? BeforeDigest,
        [property: JsonRequired] string AfterDigest,
        [property: JsonRequired] string BaseRevision,
        [property: JsonRequired] string[] LocalWritingPaths);
    private sealed record PublicationCheck(
        [property: JsonRequired] bool Conflict,
        [property: JsonRequired] bool CaptureComplete,
        [property: JsonRequired] WorkspaceConflictEvidence[] Evidence);
    private sealed class IndeterminatePublicationException(string message, Exception inner)
        : IOException("Publication outcome unknown; writing and transaction evidence retained. " + message, inner);

    private WorkspacePublicationOutcome publicationOutcome;
    private static bool IsPublicationMetadata(string name) => name is "publication.json" or
        "publication.base.json" or "publication.outcome.json" or "publication.checked.json" or "publication.displaced";
    private IReadOnlyList<WorkspaceConflict> workspaceConflicts = Array.Empty<WorkspaceConflict>();
    public WorkspacePublicationOutcome PublicationOutcome => publicationOutcome;
    public IReadOnlyList<WorkspaceConflict> GetWorkspaceConflicts()
    {
        lock (workspaceGate) return workspaceConflicts;
    }

    private void WritePublicationRecord<T>(string transaction, string name, T value)
    {
        var path = Path.Combine(transaction, name);
        RejectStoragePathLinks(path);
        WriteDurablyBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
    }

    private void PublishCurrent(string staged, string destination, IReadOnlyDictionary<string, byte[]> source,
        IReadOnlyDictionary<string, byte[]> expected, PreparedRecovery recovery,
        IReadOnlyList<PhotoResource>? resources = null)
    {
        var transaction = recovery.Transaction;
        var before = source.GetValueOrDefault(destination);
        var currentGraph = ResolveIncrementalGraph(workspace!, expected[Path.Combine(workspace!, "workspace.json")], expected);
        var recoveryGraph = RecoveryDocuments(recovery.Root, recovery.Metadata, recovery.CapturedBytes);
        var localWriting = currentGraph.Documents.SelectMany(family => family.Value
            .Where(document => !source.ContainsKey(IncrementalReference(workspace!, document.Value, family.Key)))
            .Select(document => Path.Combine("recovery", recoveryGraph.Documents[family.Key][document.Key]))).ToList();
        if (Path.GetExtension(destination) == ".md")
        {
            var family = Path.GetFileName(Path.GetDirectoryName(destination)) == "notes" ? "personNotes" : "teamNotes";
            localWriting.Add(Path.Combine("recovery", recoveryGraph.Documents[family][Path.GetFileNameWithoutExtension(destination)]));
        }
        var intent = new PublicationIntent(1, recovery.Metadata.Transaction,
            Path.GetRelativePath(workspace!, destination), before is null ? null : ByteDigest(before),
            ByteDigest(expected[destination]), WorkspaceFingerprint(snapshot: source), localWriting.ToArray());
        var backup = Path.Combine(transaction, "publication.displaced");
        RejectStoragePathLinks(staged, false);
        RejectStoragePathLinks(destination);
        RejectStoragePathLinks(backup);
        if (File.Exists(backup)) throw new IOException("Publication backup already exists; no changes published.");
        if (Path.GetPathRoot(staged) != Path.GetPathRoot(destination) ||
            Path.GetPathRoot(destination) != Path.GetPathRoot(backup))
            throw new IOException("Publication and backup must use the same local volume.");
        WritePublicationRecord(transaction, "publication.json", intent);
        if (source.TryGetValue(Path.Combine(workspace!, "workspace.json"), out var selector))
            WriteDurablyBytes(Path.Combine(transaction, "publication.base.json"), selector);
        if (WorkspaceFingerprint() != revision || resources is not null && !PhotoResourcesUnchanged(resources))
        {
            WritePublicationRecord(transaction, "publication.outcome.json", WorkspacePublicationOutcome.NotPublished);
            publicationOutcome = WorkspacePublicationOutcome.NotPublished;
            throw new IOException("Workspace changed immediately before publication; no changes published.");
        }
        try
        {
            PublishWorkspaceFile(staged, destination, before is null ? null : backup,
                before is null ? WorkspaceFilePublicationMode.Create : WorkspaceFilePublicationMode.Replace);
        }
        catch (Exception error) when (StorageError(error))
        {
            if (error is WorkspaceFilePublicationException { DefinitelyNotPublished: true })
            {
                if (File.Exists(Path.Combine(transaction, "publication.json")))
                    WritePublicationRecord(transaction, "publication.outcome.json", WorkspacePublicationOutcome.NotPublished);
                publicationOutcome = WorkspacePublicationOutcome.NotPublished;
                throw;
            }
            publicationOutcome = WorkspacePublicationOutcome.Indeterminate;
            try { LoadPublicationConflicts(); }
            catch (Exception journalError) when (StorageError(journalError))
            {
                SaveFailed("publication-conflict", "Publication outcome unknown; publication journal unavailable: " + journalError.Message);
            }
            throw new IndeterminatePublicationException(error.Message, error);
        }

        publicationOutcome = WorkspacePublicationOutcome.Published;
        try
        {
            WritePublicationRecord(transaction, "publication.outcome.json", WorkspacePublicationOutcome.Published);
            var evidence = new List<WorkspaceConflictEvidence>();
            var complete = true;
            void Capture(string original, string physical, byte[]? baseline)
            {
                try
                {
                    RejectStoragePathLinks(physical, false);
                    var bytes = ReadWorkspaceBytesUncached(physical);
                    if (baseline is not null && bytes.AsSpan().SequenceEqual(baseline)) return;
                    var number = evidence.Count;
                    var path = Path.Combine(transaction, $"publication-evidence-{number:D8}");
                    var digest = ByteDigest(bytes);
                    // This bounded inventory precedes both the independent bytes and their individual seal.
                    WritePublicationRecord(transaction, $"publication-inventory-{number:D8}.json",
                        new { schemaVersion = 1, objects = new[] {
                            new { path = Path.GetFileName(path), bytes = bytes.LongLength, digest },
                            new { path = Path.GetFileName(path) + ".sha256", bytes = 64L,
                                digest = ByteDigest(Encoding.ASCII.GetBytes(digest)) } } });
                    WriteDurablyBytes(path, bytes);
                    WriteDurablyBytes(path + ".sha256", Encoding.ASCII.GetBytes(digest));
                    evidence.Add(new(Path.GetRelativePath(workspace!, original), Path.GetFileName(path), digest));
                }
                catch (Exception error) when (StorageError(error))
                {
                    complete = false;
                }
            }
            if (before is not null) Capture(destination, backup, before);
            foreach (var (path, bytes) in source.Where(entry => !expected.ContainsKey(entry.Key)))
                Capture(path, path, bytes);
            Capture(destination, destination, expected[destination]);
            WritePublicationRecord(transaction, "publication.checked.json",
                new PublicationCheck(evidence.Count != 0 || !complete, complete, evidence.ToArray()));
        }
        catch (Exception error) when (StorageError(error))
        {
            SaveFailed("publication-conflict", "Saved locally; publication evidence capture incomplete: " + error.Message);
        }
        try { LoadPublicationConflicts(); }
        catch (Exception error) when (StorageError(error))
        {
            publicationOutcome = WorkspacePublicationOutcome.PublishedConflict;
            SaveFailed("publication-conflict", "Saved locally; publication journal validation failed: " + error.Message);
        }
    }

    private void LoadPublicationConflicts()
    {
        try { ReadPublicationConflicts(); }
        catch (Exception error) when (StorageError(error))
        {
            publicationOutcome = WorkspacePublicationOutcome.Indeterminate;
            if (workspaceConflicts.Count == 0)
                workspaceConflicts = Array.AsReadOnly(new[] { new WorkspaceConflict(Guid.Empty,
                    WorkspacePublicationOutcome.Indeterminate, false, Array.Empty<WorkspaceConflictEvidence>(), Array.Empty<string>()) });
            SaveFailed("publication-conflict", "Publication journal unavailable; outcome unconfirmed and evidence retained: " + error.Message);
            throw;
        }
    }

    private void ReadPublicationConflicts()
    {
        var conflicts = new List<WorkspaceConflict>();
        var transactions = Path.Combine(workspace!, "transactions");
        if (!Directory.Exists(transactions))
        {
            if (workspaceConflicts.Any(value => value.Id != Guid.Empty))
                throw new IOException("Unresolved publication records disappeared; restore the retained evidence before resolving.");
            workspaceConflicts = Array.Empty<WorkspaceConflict>();
            return;
        }
        RejectStoragePathLinks(transactions, false);
        foreach (var transaction in Directory.GetDirectories(transactions))
        {
            RejectStoragePathLinks(transaction, false);
            var path = Path.Combine(transaction, "publication.json");
            if (!File.Exists(path)) continue;
            var intent = ReadPublicationRecord<PublicationIntent>(path);
            if (intent.SchemaVersion != 1 || intent.Id == Guid.Empty || intent.Id.ToString("N") != Path.GetFileName(transaction) ||
                !ValidDigest(intent.AfterDigest) || !ValidDigest(intent.BaseRevision) || intent.LocalWritingPaths is null ||
                intent.BeforeDigest is not null && !ValidDigest(intent.BeforeDigest) ||
                intent.Target != "workspace.json" &&
                !System.Text.RegularExpressions.Regex.IsMatch(intent.Target,
                    @"^(notes|team-notes)\\[a-f0-9-]{36}\.md$"))
                throw new FormatException("Invalid publication provenance; all evidence preserved.");
            var outcomePath = Path.Combine(transaction, "publication.outcome.json");
            var outcome = File.Exists(outcomePath)
                ? ReadPublicationRecord<WorkspacePublicationOutcome>(outcomePath)
                : WorkspacePublicationOutcome.Indeterminate;
            if (outcome == WorkspacePublicationOutcome.NotPublished) continue;
            var checkedPath = Path.Combine(transaction, "publication.checked.json");
            var check = File.Exists(checkedPath) ? ReadPublicationRecord<PublicationCheck>(checkedPath) : null;
            if (check is not null && (check.Evidence is null ||
                !check.Conflict && (!check.CaptureComplete || check.Evidence.Length != 0)))
                throw new FormatException("Invalid publication capture record.");
            if (outcome == WorkspacePublicationOutcome.Published && check is { Conflict: false, CaptureComplete: true })
                continue;
            if (outcome != WorkspacePublicationOutcome.Published && outcome != WorkspacePublicationOutcome.Indeterminate)
                throw new FormatException("Invalid publication outcome; evidence preserved.");
            var resolution = Path.Combine(transaction, "publication.resolved.json");
            if (File.Exists(resolution))
            {
                var accepted = ReadPublicationRecord<WorkspaceConflictReview>(resolution);
                if (!ValidDigest(accepted.Revision) || !ValidDigest(accepted.EvidenceRevision) ||
                    accepted.ConflictIds is null || !accepted.ConflictIds.Contains(intent.Id) ||
                    accepted.ConflictIds.Any(id => id == Guid.Empty) ||
                    accepted.ConflictIds.Distinct().Count() != accepted.ConflictIds.Count)
                    throw new FormatException("Invalid conflict resolution; evidence preserved.");
                continue;
            }
            var evidence = (check?.Evidence ?? []).Select(value =>
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(value.Path, @"^publication-evidence-[0-9]{8}$") ||
                    Path.IsPathRooted(value.OriginalPath) || value.OriginalPath.Split('\\', '/').Contains("..") ||
                    !ValidDigest(value.Digest))
                    throw new FormatException("Invalid conflict evidence provenance.");
                return value with { OriginalPath = Path.Combine(workspace!, value.OriginalPath),
                    Path = Path.Combine(transaction, value.Path) };
            }).ToArray();
            var localWriting = intent.LocalWritingPaths.Select(relative =>
            {
                if (!relative.StartsWith("recovery\\documents\\", StringComparison.Ordinal))
                    throw new FormatException("Invalid retained local writing path.");
                var parts = relative.Split('\\');
                if (parts.Length != 4) throw new FormatException("Invalid retained local writing reference.");
                return IncrementalReference(Path.Combine(workspace!, "recovery"),
                    string.Join("\\", parts.Skip(1)), parts[2]);
            }).ToArray();
            conflicts.Add(new(intent.Id, outcome == WorkspacePublicationOutcome.Published
                ? WorkspacePublicationOutcome.PublishedConflict : WorkspacePublicationOutcome.Indeterminate,
                check?.CaptureComplete ?? false, Array.AsReadOnly(evidence), Array.AsReadOnly(localWriting)));
        }
        foreach (var previous in workspaceConflicts.Where(value => value.Id != Guid.Empty &&
            conflicts.All(current => current.Id != value.Id)))
        {
            var resolution = Path.Combine(transactions, previous.Id.ToString("N"), "publication.resolved.json");
            if (!File.Exists(resolution))
                throw new IOException("An unresolved publication record disappeared or changed without explicit resolution.");
            var acknowledged = ReadPublicationRecord<WorkspaceConflictReview>(resolution);
            if (!ValidDigest(acknowledged.Revision) || !ValidDigest(acknowledged.EvidenceRevision) ||
                !acknowledged.ConflictIds.Contains(previous.Id))
                throw new FormatException("Invalid conflict acknowledgment.");
        }
        workspaceConflicts = Array.AsReadOnly(conflicts.ToArray());
        if (conflicts.Count != 0)
        {
            publicationOutcome = conflicts.Any(value => value.Outcome == WorkspacePublicationOutcome.Indeterminate)
                ? WorkspacePublicationOutcome.Indeterminate : WorkspacePublicationOutcome.PublishedConflict;
            SaveFailed("publication-conflict", publicationOutcome == WorkspacePublicationOutcome.Indeterminate
                ? "Publication outcome unknown; writing and evidence retained. Resolve externally, then explicitly acknowledge the reviewed revision."
                : "Saved locally; external conflict unresolved. Captured evidence is retained; capture may be incomplete. Resolve externally, then explicitly acknowledge the reviewed revision.");
        }
    }

    private T ReadPublicationRecord<T>(string path)
    {
        RejectStoragePathLinks(path, false);
        return JsonSerializer.Deserialize<T>(ReadWorkspaceBytesUncached(path), JsonOptions)
            ?? throw new FormatException("Invalid publication record; evidence preserved.");
    }

    public WorkspaceConflictReview? ReviewWorkspaceConflicts()
    {
        lock (workspaceGate)
        {
            try
            {
                if (workspace is null) throw new IOException("Open a Workspace before reviewing conflicts.");
                using var access = CoordinateWorkspaceIo(workspace);
                LoadPublicationConflicts();
                var bytes = CaptureWorkspaceBytes();
                using var validation = new ApplicationWorkflow(false, null, timeProvider) { workspace = workspace };
                validation.ReadIncrementalWorkspaceData(bytes);
                ValidateConflictEvidence();
                var fingerprint = WorkspaceFingerprint(snapshot: bytes);
                if (WorkspaceFingerprint() != fingerprint) throw new IOException("Delivery changed during conflict review.");
                return new(fingerprint, Array.AsReadOnly(workspaceConflicts.Select(value => value.Id).Order().ToArray()),
                    ConflictEvidenceRevision(workspaceConflicts));
            }
            catch (Exception error) when (StorageError(error))
            {
                SaveFailed("publication-conflict", "Conflict review unavailable; evidence preserved: " + error.Message);
                return null;
            }
        }
    }

    private void ValidateConflictEvidence()
    {
        foreach (var conflict in workspaceConflicts)
        {
            foreach (var local in conflict.LocalWritingPaths)
            {
                RejectStoragePathLinks(local, false);
                RejectStoragePathLinks(local + ".sha256", false);
                if (ByteDigest(ReadWorkspaceBytesUncached(local)) != Encoding.ASCII.GetString(ReadWorkspaceBytesUncached(local + ".sha256")))
                    throw new IOException("Retained local writing changed; resolution is blocked.");
            }
            foreach (var evidence in conflict.Evidence)
            {
                var transaction = Path.Combine(workspace!, "transactions", conflict.Id.ToString("N"));
                if (Path.GetDirectoryName(evidence.Path) != transaction ||
                    !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(evidence.Path), @"^publication-evidence-[0-9]{8}$") ||
                    !ValidDigest(evidence.Digest))
                    throw new FormatException("Invalid publication evidence path.");
                RejectStoragePathLinks(evidence.Path, false);
                RejectStoragePathLinks(evidence.Path + ".sha256", false);
                if (ByteDigest(ReadWorkspaceBytesUncached(evidence.Path)) != evidence.Digest ||
                    Encoding.ASCII.GetString(ReadWorkspaceBytesUncached(evidence.Path + ".sha256")) != evidence.Digest)
                    throw new IOException("Publication evidence changed; resolution is blocked.");
            }
        }
    }

    private string ConflictEvidenceRevision(IReadOnlyList<WorkspaceConflict> conflicts)
    {
        var digests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var conflict in conflicts)
        {
            var transaction = Path.Combine(workspace!, "transactions", conflict.Id.ToString("N"));
            foreach (var path in new[] { "publication.json", "publication.outcome.json", "publication.checked.json" }
                .Select(name => Path.Combine(transaction, name))
                .Concat(conflict.LocalWritingPaths.SelectMany(path => new[] { path, path + ".sha256" }))
                .Concat(conflict.Evidence.SelectMany(value => new[] { value.Path, value.Path + ".sha256" })))
            {
                RejectStoragePathLinks(path);
                digests[Path.GetRelativePath(workspace!, path)] = File.Exists(path)
                    ? ByteDigest(ReadWorkspaceBytesUncached(path)) : "absent";
            }
        }
        return ByteDigest(JsonSerializer.SerializeToUtf8Bytes(digests, JsonOptions));
    }

    public bool ResolveWorkspaceConflicts(WorkspaceConflictReview review)
    {
        lock (workspaceGate)
        {
            try
            {
                using var access = CoordinateWorkspaceIo(workspace!);
                using var lease = new FileStream(Path.Combine(workspace!, ".workspace.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var current = ReviewWorkspaceConflicts();
                if (current is null || current.Revision != review.Revision ||
                    current.EvidenceRevision != review.EvidenceRevision ||
                    !current.ConflictIds.SequenceEqual(review.ConflictIds) || current.ConflictIds.Count == 0)
                    throw new IOException("Conflict set or current delivery changed; review again before acknowledging.");
                ReadAndAcceptWorkspace();
                if (revision != current.Revision)
                    throw new IOException("Delivery changed while accepting the reviewed resolution.");
                var remaining = current.ConflictIds.ToList();
                var reviewedConflicts = workspaceConflicts;
                foreach (var id in current.ConflictIds)
                {
                    LoadPublicationConflicts();
                    if (!workspaceConflicts.Select(value => value.Id).Order().SequenceEqual(remaining))
                        throw new IOException("Conflict set changed during acknowledgement.");
                    ValidateConflictEvidence();
                    if (ConflictEvidenceRevision(reviewedConflicts) != current.EvidenceRevision)
                        throw new IOException("Reviewed conflict evidence changed during acknowledgement.");
                    if (WorkspaceFingerprint() != current.Revision)
                        throw new IOException("Delivery changed during acknowledgement; remaining conflicts stay unresolved.");
                    WritePublicationRecord(Path.Combine(workspace!, "transactions", id.ToString("N")),
                        "publication.resolved.json", current);
                    remaining.Remove(id);
                }
                LoadPublicationConflicts();
                failedSaves.Remove("publication-conflict");
                Status = "External conflict resolution acknowledged. Evidence retained; protect the chosen current revision before safe close.";
                lease.Dispose();
                return MaintainAcceptedStorage();
            }
            catch (Exception error) when (StorageError(error))
            {
                return SaveFailed("publication-conflict", "Conflict resolution not completed; evidence retained: " + error.Message);
            }
        }
    }
}
