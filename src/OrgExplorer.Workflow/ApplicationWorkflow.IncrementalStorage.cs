using System.Text.Json;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private bool incrementalStorage;

    // Internal opt-in until the complete format, recovery and reclamation slices ship.
    internal static IApplicationWorkflow CreateIncrementalFileBacked(TimeProvider? timeProvider = null,
        IWorkspaceFilePublisher? filePublisher = null, IWorkspaceFileObserver? fileObserver = null) =>
        new ApplicationWorkflow(false, null, timeProvider, filePublisher, fileObserver) { incrementalStorage = true };

    private void OpenIncrementalWorkspace()
    {
        var pointer = Path.Combine(workspace!, "workspace.json");
        if (!File.Exists(pointer) && !Directory.EnumerateFileSystemEntries(workspace!).Any())
        {
            workspaceCurrent = true;
            revision = WorkspaceFingerprint();
            if (!SaveIncrementally(new(StringComparer.OrdinalIgnoreCase), [], [], [],
                new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), "create", null))
                throw new IOException(LocalSaveError);
        }
        var bytes = CaptureWorkspaceBytes();
        using var candidate = new ApplicationWorkflow(false, null, timeProvider) { workspace = workspace, incrementalStorage = true };
        candidate.ReadIncrementalWorkspaceData(bytes);
        if (WorkspaceFingerprint() != WorkspaceFingerprint(snapshot: bytes))
            throw new IOException("Workspace delivery changed while reading; waiting for complete local files.");
        people = candidate.people;
        profileHistory = candidate.profileHistory;
        batches = candidate.batches;
        photos = candidate.photos;
        teamContextRevisions = candidate.teamContextRevisions;
        historicalTeamContexts = candidate.historicalTeamContexts;
        personNotes = candidate.personNotes;
        teamNotes = candidate.teamNotes;
        teamNoteConfirmations = candidate.teamNoteConfirmations;
        InvalidateProjections();
        revision = WorkspaceFingerprint(snapshot: bytes);
        workspaceCurrent = true;
        CheckRecoveryOnOpen(bytes);
        compatibility = new(WorkspaceCompatibilityState.Compatible,
            "Development version-3 Workspace. Reclamation and shipping activation remain unavailable.", workspace, 3,
            verifiedRecoveryPath);
    }

    private void ReadIncrementalWorkspaceData(IReadOnlyDictionary<string, byte[]> bytes)
    {
        using var read = new WorkspaceReadScope(bytes);
        if (!bytes.TryGetValue(Path.Combine(workspace!, "workspace.json"), out var selector))
            throw new IOException("Missing Workspace selector; staging files are not committed data.");
        var graph = ResolveIncrementalGraph(workspace!, selector, bytes);
        var loaded = new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, relative) in graph.Documents["people"])
        {
            using var json = JsonDocument.Parse(ReadWorkspaceBytes(IncrementalPath(workspace!, relative, "people")));
            var person = ReadPersonRecord(json.RootElement, 2);
            if (key != person.Email.ToLowerInvariant() || !loaded.TryAdd(person.Email, person))
                throw new FormatException("Person index identity does not match the document.");
        }
        ValidateGraph(loaded);
        var historyFiles = DocumentPaths("profileHistory").ToArray();
        var history = ReadProfileHistory(workspace!, loaded, 2, historyFiles);
        foreach (var (key, relative) in graph.Documents["profileHistory"])
        {
            using var json = JsonDocument.Parse(ReadWorkspaceBytes(IncrementalPath(workspace!, relative, "profileHistory")));
            if (Required(json.RootElement, "email", "Profile history").GetString()!.ToLowerInvariant() != key)
                throw new FormatException("Profile history index identity does not match the document.");
        }
        if (!history.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(loaded.Keys.Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase))
            throw new FormatException("Every indexed Person requires Profile history.");
        var accepted = new Dictionary<string, AcceptedBatch>();
        foreach (var (key, relative) in graph.Documents["acceptedBatches"])
        {
            using var json = JsonDocument.Parse(ReadWorkspaceBytes(IncrementalPath(workspace!, relative, "acceptedBatches")));
            CheckProperties(json.RootElement, [key], "Accepted batch");
            var batch = Required(json.RootElement, key, "Accepted batch").Deserialize<AcceptedBatch>(JsonOptions);
            ValidateAcceptedBatch(key, batch);
            accepted.Add(key, batch!);
        }
        var contexts = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, relative) in graph.Documents["teamContext"])
        {
            using var json = JsonDocument.Parse(ReadWorkspaceBytes(IncrementalPath(workspace!, relative, "teamContext")));
            CheckProperties(json.RootElement, [key], "Team context");
            if (!loaded.ContainsKey(key)) throw new FormatException("Unknown Team context owner.");
            var value = Required(json.RootElement, key, "Team context");
            if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var context))
                throw new FormatException("Invalid Team context revision.");
            contexts.Add(key, context);
        }
        var historical = ReadHistoricalTeamContexts(workspace!, loaded, DocumentPaths("historicalTeamContext").ToArray());
        foreach (var (key, relative) in graph.Documents["historicalTeamContext"])
        {
            using var json = JsonDocument.Parse(ReadWorkspaceBytes(IncrementalPath(workspace!, relative, "historicalTeamContext")));
            if (Required(json.RootElement, "email", "Historical team context").GetString()!.ToLowerInvariant() != key)
                throw new FormatException("Historical team context index identity does not match the document.");
        }
        var loadedPhotos = new Dictionary<string, byte[]>();
        foreach (var (key, relative) in graph.Documents["photos"])
        {
            ValidateHistoryValue("photoPath", JsonSerializer.SerializeToElement(key));
            var photoBytes = ReadWorkspaceBytes(IncrementalPath(workspace!, relative, "photos"));
            var photo = DecodePhoto(photoBytes, "Workspace photo");
            if (!key.EndsWith(photo.ContentType == "image/png" ? ".png" : ".jpg", StringComparison.Ordinal))
                throw new FormatException("Workspace photo extension does not match its decoded type.");
            loadedPhotos.Add(key, photoBytes);
        }
        foreach (var person in loaded.Values)
            if (person.PhotoPath is { } photo && !loadedPhotos.ContainsKey(photo))
                throw new IOException("Required Person photo is unavailable.");
        var notes = LoadPersonNotes(workspace!, loaded);
        var team = LoadTeamNotes(workspace!, loaded);
        people = loaded;
        profileHistory = history;
        batches = accepted;
        photos = loadedPhotos;
        teamContextRevisions = contexts;
        historicalTeamContexts = historical;
        personNotes = notes;
        teamNotes = team.Notes;
        teamNoteConfirmations = team.Confirmations;
        InvalidateProjections();

        IEnumerable<string> DocumentPaths(string family) =>
            graph.Documents[family].Values.Select(relative => IncrementalPath(workspace!, relative, family));
    }

    private bool SaveIncrementally(Dictionary<string, PersonProfile> proposed, Dictionary<string, AcceptedBatch> accepted,
        Dictionary<string, byte[]> proposedPhotos, Dictionary<string, PersonHistory> history,
        Dictionary<string, Guid> nextTeamContexts, Dictionary<string, List<HistoricalTeamContext>> nextHistoricalContexts,
        string operation, IReadOnlyList<PhotoResource>? resources)
    {
        try
        {
            using var access = CoordinateWorkspaceIo(workspace!);
            EnsureWorkspaceWritable();
            using var lease = new FileStream(Path.Combine(workspace!, ".workspace.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var source = CaptureWorkspaceBytes();
            if (WorkspaceFingerprint(snapshot: source) != revision)
                throw new IOException("Workspace changed since opening or preview. Reopen and preview again; no changes saved.");
            var destination = Path.Combine(workspace!, "workspace.json");
            var roots = IndexedFamilies.ToDictionary(f => f, _ => (string?)null);
            var documents = IndexedFamilies.ToDictionary(f => f, _ => new Dictionary<string, string>());
            if (source.TryGetValue(destination, out var selector))
            {
                using var validated = new ApplicationWorkflow(false, null, timeProvider) { workspace = workspace, incrementalStorage = true };
                validated.ReadIncrementalWorkspaceData(source);
                var graph = ResolveIncrementalGraph(workspace!, selector, source);
                roots = new(graph.Metadata.Roots);
                documents = graph.Documents;
            }
            var commit = Guid.NewGuid();
            var transaction = Path.Combine(workspace!, "transactions", commit.ToString("N"));
            CreateIncrementalDirectory(transaction);
            WriteDurablyBytes(Path.Combine(transaction, "intent.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, commit, baseRevision = revision }, JsonOptions));
            var available = new Dictionary<string, byte[]>(source, StringComparer.OrdinalIgnoreCase);
            var updates = IndexedFamilies.ToDictionary(f => f, _ => new Dictionary<string, string>());
            var inventory = 0;
            void WriteObject(string relative, byte[] bytes)
            {
                var path = Path.Combine(workspace!, relative);
                WriteDurablyBytes(Path.Combine(transaction, $"created-{inventory++:D8}.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1, path = relative, bytes = bytes.LongLength,
                    digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                }, IncrementalJsonOptions));
                CreateIncrementalDirectory(Path.GetDirectoryName(path)!);
                WriteDurablyBytes(path, bytes);
                available[path] = bytes;
            }
            void SetDocument(string family, string key, byte[] bytes, string extension = ".json")
            {
                key = key.ToLowerInvariant();
                if (documents[family].TryGetValue(key, out var prior) &&
                    source[IncrementalPath(workspace!, prior, family)].AsSpan().SequenceEqual(bytes)) return;
                var relative = Path.Combine("documents", family, Guid.NewGuid().ToString("N") + extension);
                WriteObject(relative, bytes);
                updates[family][key] = relative;
                documents[family][key] = relative;
            }
            foreach (var person in proposed.Values)
            {
                if (!people.TryGetValue(person.Email, out var before) || before != person)
                    SetDocument("people", person.Email, VersionedBytes(person));
            }
            foreach (var (email, value) in history)
                if (!ReferenceEquals(profileHistory.GetValueOrDefault(email), value))
                    SetDocument("profileHistory", email, VersionedBytes(value));
            foreach (var (id, value) in accepted)
                if (!Equals(batches.GetValueOrDefault(id), value))
                    SetDocument("acceptedBatches", id, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, AcceptedBatch> { [id] = value }, JsonOptions));
            foreach (var (email, value) in nextTeamContexts)
                if (!teamContextRevisions.TryGetValue(email, out var before) || before != value)
                    SetDocument("teamContext", email, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, Guid> { [email.ToLowerInvariant()] = value }, JsonOptions));
            foreach (var (email, value) in nextHistoricalContexts)
                if (!ReferenceEquals(historicalTeamContexts.GetValueOrDefault(email), value))
                    SetDocument("historicalTeamContext", email, JsonSerializer.SerializeToUtf8Bytes(new HistoricalContextDocument(1, email, value), JsonOptions));
            foreach (var (name, value) in proposedPhotos)
                if (!photos.TryGetValue(name, out var before) || !before.AsSpan().SequenceEqual(value))
                    SetDocument("photos", name, value, Path.GetExtension(name));
            foreach (var (family, changes) in updates)
                if (changes.Count > 0)
                    roots[family] = UpdateIndexBatch(roots[family], family, changes, "", available, WriteObject);
            var candidate = JsonSerializer.SerializeToUtf8Bytes(new IncrementalMetadata(3, commit, roots,
                new(CurrentRecordVersions)), JsonOptions);
            var staged = Path.Combine(transaction, "selector.next");
            WriteDurablyBytes(staged, candidate);
            available[destination] = candidate;
            var candidateGraph = ResolveIncrementalGraph(workspace!, candidate, available);
            var expected = available.Where(e => e.Key == destination || candidateGraph.Paths.Contains(e.Key) ||
                    !e.Key.StartsWith(Path.Combine(workspace!, "index") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !e.Key.StartsWith(Path.Combine(workspace!, "documents") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (path, bytes) in expected)
                if (path != destination && !ReadWorkspaceBytesUncached(path).AsSpan().SequenceEqual(bytes))
                    throw new IOException("Workspace candidate changed while validating staged data.");
            using (var validation = new ApplicationWorkflow(false, null, timeProvider) { workspace = workspace, incrementalStorage = true })
                validation.ReadIncrementalWorkspaceData(expected);
            var recovery = PrepareRecovery(expected, transaction,
                new("workspace.json", selector is null ? null : ByteDigest(selector), ByteDigest(candidate)));
            if (WorkspaceFingerprint() != revision || resources is not null && !PhotoResourcesUnchanged(resources))
                throw new IOException("Workspace or supplied resources changed during staging. No changes published.");
            PublishWorkspaceFile(staged, destination, Path.Combine(workspace!, "workspace.previous.json"));
            AcceptWorkspaceRevision(expected);
            teamContextRevisions = nextTeamContexts;
            historicalTeamContexts = nextHistoricalContexts;
            failedSaves.Remove(operation);
            FinishCommittedRecovery(recovery);
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            return SaveFailed(operation, "Save failed; changes are NOT saved locally: " + ex.Message);
        }
    }

    private static byte[] VersionedBytes<T>(T value)
    {
        var record = JsonSerializer.SerializeToNode(value, JsonOptions)!.AsObject();
        record["schemaVersion"] = 2;
        return JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
    }

    private void CreateIncrementalDirectory(string directory)
    {
        for (var current = directory; !current.Equals(workspace, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current)!)
            if (Directory.Exists(current) || File.Exists(current)) RejectWorkspaceLink(current);
        Directory.CreateDirectory(directory);
        RejectWorkspaceLink(directory);
    }
}
