using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

// Linked only into test projects. The interchange is the product's complete published snapshot, never SQLite files.
internal static class UiScaleHandoff
{
    internal sealed record Binding(string Revision, string RunId, string RunAttempt, string SourceTree,
        string TestTree, string EngTree, string WorkflowBlob, Dictionary<string, string> ProductFiles,
        Dictionary<string, string> HarnessFiles, string NativeVersion, string NativeSourceId, string LoadedNativeSha256);
    internal sealed record Manifest(int SchemaVersion, Binding Binding, string SourceRunDirectory, string SourceWorkspace,
        Guid WorkspaceId, Guid Head, string SnapshotFile, string SnapshotSha256, string PayloadSha256,
        string PreferencesSha256, RememberedView Selection, string PublicStateSha256);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static Binding Identify(string revision, string runId, string runAttempt, string sourceTree,
        string testTree, string engTree, string workflowBlob, string executable, string harness)
    {
        SQLitePCL.Batteries_V2.Init();
        var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Single(module => Path.GetFileName(module.FileName).Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase));
        var product = Files(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        var actualNative = Hash(File.ReadAllBytes(native.FileName));
        if (!product.Any(file => file.Key.EndsWith("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase) &&
            file.Value == actualNative))
            throw new InvalidOperationException("The handoff host did not load the product's exact bundled native SQLite.");
        var result = new Binding(revision, runId, runAttempt, sourceTree, testTree, engTree, workflowBlob,
            product, Files(Path.GetDirectoryName(Path.GetFullPath(harness))!),
            SQLitePCL.raw.sqlite3_libversion().utf8_to_string(),
            SQLitePCL.raw.sqlite3_sourceid().utf8_to_string(), actualNative);
        ValidateBinding(result);
        return result;

        static Dictionary<string, string> Files(string directory) =>
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file) is ".dll" or ".exe" ||
                    file.EndsWith(".deps.json", StringComparison.Ordinal) ||
                    file.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToDictionary(file => Path.GetRelativePath(directory, file),
                    file => Hash(File.ReadAllBytes(file)), StringComparer.Ordinal);
    }

    internal static void Capture(string directory, string workspace, string snapshots, string preferencesPath,
        Binding binding, string sourceRunDirectory)
    {
        ValidateBinding(binding);
        Require(Regex.IsMatch(sourceRunDirectory, "^run-[0-9a-f]{32}$"), "The handoff requires the exact first-worker invocation.");
        if (Directory.Exists(directory)) throw new IOException("A handoff must not reuse an earlier bundle.");
        using var app = ApplicationWorkflow.CreateFileBacked();
        Require(app.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly), app.Status);
        RequirePublished(app);
        var state = app.Synchronization;
        var name = $"{state.WorkspaceId:D}-{state.LocalRevision:D}.oesnapshot";
        var snapshot = File.ReadAllBytes(Path.Combine(snapshots, name));
        var payload = ValidateEnvelope(snapshot, state.WorkspaceId!.Value, state.LocalRevision!.Value);
        var preferencesBytes = File.ReadAllBytes(preferencesPath);
        var preferences = new WorkspaceSettings(preferencesPath);
        Require(preferences.PreferenceError is null && preferences.LastWorkspacePath == Path.GetFullPath(workspace),
            preferences.PreferenceError ?? "The captured preferences name a different Workspace.");
        var selected = preferences.GetLastView(workspace) ??
            throw new InvalidOperationException("The first worker has no actual remembered selection.");
        ValidateSelection(app, selected);
        var manifest = new Manifest(1, binding, sourceRunDirectory, Path.GetFullPath(workspace), state.WorkspaceId.Value,
            state.LocalRevision.Value, name, Hash(snapshot), payload, Hash(preferencesBytes), selected, PublicStateHash(app));
        Directory.CreateDirectory(directory);
        WriteNew(Path.Combine(directory, name), snapshot);
        WriteNew(Path.Combine(directory, "preferences.json"), preferencesBytes);
        WriteNew(Path.Combine(directory, "handoff.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        // Recheck the original head without mutating or publishing it. A later commit cannot hide behind this handoff.
        using var check = ApplicationWorkflow.CreateFileBacked();
        Require(check.OpenWorkspace(workspace, WorkspaceOpenMode.ExistingOnly), check.Status);
        RequirePublished(check);
        Require(check.Synchronization.WorkspaceId == manifest.WorkspaceId &&
            check.Synchronization.LocalRevision == manifest.Head, "The source advanced while capturing its handoff.");
    }

    internal static Manifest Receive(string directory, string workspace, string snapshots, string preferencesPath,
        Binding binding, string receiptPath)
    {
        ValidateBinding(binding);
        var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "handoff.json"));
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes, Json) ??
            throw new FormatException("Missing logical handoff metadata.");
        Require(manifest.SchemaVersion == 1 && Regex.IsMatch(manifest.SourceRunDirectory, "^run-[0-9a-f]{32}$") &&
            SameBinding(manifest.Binding, binding),
            "The logical handoff belongs to a different candidate/run/attempt/source/component/native build.");
        Require(manifest.WorkspaceId != Guid.Empty && manifest.Head != Guid.Empty &&
            manifest.SnapshotFile == $"{manifest.WorkspaceId:D}-{manifest.Head:D}.oesnapshot" &&
            Path.IsPathFullyQualified(manifest.SourceWorkspace) &&
            !string.Equals(Path.GetFullPath(workspace), manifest.SourceWorkspace, StringComparison.OrdinalIgnoreCase),
            "The logical handoff has an invalid identity or reused physical Workspace.");
        var expectedFiles = new[] { "handoff.json", "preferences.json", manifest.SnapshotFile }.Order(StringComparer.Ordinal);
        Require(Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(expectedFiles) &&
            !Directory.EnumerateDirectories(directory).Any(), "The handoff must contain only its complete logical snapshot and metadata/preferences.");
        var snapshotBytes = File.ReadAllBytes(Path.Combine(directory, manifest.SnapshotFile));
        Require(Hash(snapshotBytes) == manifest.SnapshotSha256 &&
            ValidateEnvelope(snapshotBytes, manifest.WorkspaceId, manifest.Head) == manifest.PayloadSha256,
            "The complete published snapshot does not match the bound payload.");
        var preferencesBytes = File.ReadAllBytes(Path.Combine(directory, "preferences.json"));
        Require(Hash(preferencesBytes) == manifest.PreferencesSha256, "The recorded preferences changed.");
        var originalPreferences = new WorkspaceSettings(Path.Combine(directory, "preferences.json"));
        Require(originalPreferences.PreferenceError is null &&
            originalPreferences.LastWorkspacePath == manifest.SourceWorkspace &&
            originalPreferences.GetLastView(manifest.SourceWorkspace) == manifest.Selection,
            "The recorded selection is missing, invalid or different from the original preferences.");
        Require(!Directory.Exists(workspace) && !Directory.Exists(snapshots) && !File.Exists(preferencesPath) &&
            !File.Exists(receiptPath), "A receiver must use fresh physical folders, preferences and receipt.");
        Directory.CreateDirectory(snapshots);
        WriteNew(Path.Combine(snapshots, manifest.SnapshotFile), snapshotBytes);
        using (var joined = ApplicationWorkflow.CreateFileBacked())
        {
            Require(joined.OpenWorkspace(workspace) &&
                joined.ConfigureSynchronizationFolder(snapshots, joinExistingWorkspace: true) && joined.SyncNow(), joined.Status);
            RequirePublished(joined);
            Require(joined.Synchronization.WorkspaceId == manifest.WorkspaceId &&
                joined.Synchronization.LocalRevision == manifest.Head &&
                PublicStateHash(joined) == manifest.PublicStateSha256,
                "Public FileBacked Join did not preserve the exact complete owner/note/photo/history state and logical identity.");
            ValidateSelection(joined, manifest.Selection);
            var relocated = JsonNode.Parse(preferencesBytes)!.AsObject();
            relocated["lastWorkspacePath"] = Path.GetFullPath(workspace);
            foreach (var item in relocated["workspaces"]!.AsArray())
            {
                Require(item!["path"]!.GetValue<string>() == manifest.SourceWorkspace,
                    "Unexpected unrelated Workspace preferences cannot be manufactured or silently remapped.");
                item["path"] = Path.GetFullPath(workspace);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
            WriteNew(preferencesPath, JsonSerializer.SerializeToUtf8Bytes(relocated, Json));
        }
        // A new independent public reopen must see the same committed state, not only the joining instance.
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
        {
            var restored = new WorkspaceSettings(preferencesPath);
            Require(restored.PreferenceError is null && restored.RestoreWorkspace(reopened) &&
                restored.GetLastView(workspace) == manifest.Selection &&
                restored.GetPaneProportions(workspace) == originalPreferences.GetPaneProportions(manifest.SourceWorkspace),
                restored.PreferenceError ?? "Relocated preferences did not durably restore.");
            RequirePublished(reopened);
            Require(reopened.Synchronization.WorkspaceId == manifest.WorkspaceId &&
                reopened.Synchronization.LocalRevision == manifest.Head &&
                PublicStateHash(reopened) == manifest.PublicStateSha256, "Independent durable Join reopen differs.");
        }
        WriteNew(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, outcome = "Passed", binding, handoffSha256 = Hash(manifestBytes),
            manifest.WorkspaceId, manifest.Head, manifest.SnapshotSha256, manifest.PayloadSha256,
            manifest.PreferencesSha256, manifest.Selection, manifest.PublicStateSha256,
            manifest.SourceRunDirectory, sourceWorkspace = manifest.SourceWorkspace, receiverWorkspace = Path.GetFullPath(workspace),
            scope = "Separate Windows worker; public FileBacked Join of the actual published complete logical snapshot, followed by independent durable reopen. Not continuation of the first physical process/store."
        }, Json));
        return manifest;
    }

    private static void ValidateBinding(Binding binding)
    {
        Require(new[] { binding.Revision, binding.SourceTree, binding.TestTree, binding.EngTree, binding.WorkflowBlob }
            .All(value => value is not null && Regex.IsMatch(value, "^[0-9a-f]{40}$")) &&
            !string.IsNullOrWhiteSpace(binding.RunId) && !string.IsNullOrWhiteSpace(binding.RunAttempt) &&
            !string.IsNullOrWhiteSpace(binding.NativeVersion) && !string.IsNullOrWhiteSpace(binding.NativeSourceId) &&
            IsHash(binding.LoadedNativeSha256), "Incomplete candidate/run/source/native identity.");
        foreach (var files in new[] { binding.ProductFiles, binding.HarnessFiles })
            Require(files is { Count: > 0 } && files.All(file => !string.IsNullOrWhiteSpace(file.Key) &&
                !Path.IsPathFullyQualified(file.Key) && IsHash(file.Value)), "Incomplete component binary inventory.");
    }

    private static bool SameBinding(Binding source, Binding target)
    {
        ValidateBinding(source);
        return source with { ProductFiles = target.ProductFiles, HarnessFiles = target.HarnessFiles } == target &&
            source.ProductFiles.Count == target.ProductFiles.Count &&
            source.HarnessFiles.Count == target.HarnessFiles.Count &&
            source.ProductFiles.All(file => target.ProductFiles.TryGetValue(file.Key, out var hash) && hash == file.Value) &&
            source.HarnessFiles.All(file => target.HarnessFiles.TryGetValue(file.Key, out var hash) && hash == file.Value);
    }

    private static string ValidateEnvelope(byte[] bytes, Guid identity, Guid head)
    {
        using var envelope = JsonDocument.Parse(bytes);
        var root = envelope.RootElement;
        var payload = root.GetProperty("content").GetBytesFromBase64();
        var hash = Hash(payload);
        Require(root.GetProperty("format").GetString() == "OrgExplorer complete logical Workspace" &&
            root.GetProperty("version").GetInt32() == 1 && root.GetProperty("sha256").GetString() == hash,
            "Invalid complete-snapshot envelope/integrity.");
        using var content = JsonDocument.Parse(payload);
        Require(content.RootElement.GetProperty("workspaceId").GetGuid() == identity &&
            content.RootElement.GetProperty("revision").GetGuid() == head, "Snapshot logical identity/head mismatch.");
        return hash;
    }

    internal static string PublicStateHash(IApplicationWorkflow app)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var person in app.People.OrderBy(person => person.Email, StringComparer.Ordinal))
        {
            var photo = app.GetPhoto(person.Email);
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                person, personNotes = app.GetPersonNotes(person.Email, true), teamNotes = app.GetTeamNotes(person.Email, true),
                inherited = app.GetInheritedTeamNotes(person.Email), history = app.GetProfileTimeline(person.Email),
                frozen = app.GetHistoricalTeamContexts(person.Email),
                photo = photo is null ? null : new { photo.Width, photo.Height, sha256 = Hash(photo.Bytes.Span) }
            }, Json));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidateSelection(IApplicationWorkflow app, RememberedView view)
    {
        Require(app.GetRelationships(view.PersonEmail) is not null, "Remembered Person missing from snapshot.");
        if (view.Source is null) return;
        Require(view.NoteId is { } && view.NoteOwnerEmail is not null, "Remembered note identity is missing.");
        var exists = view.Source switch
        {
            SearchSource.PersonNote => app.GetPersonNotes(view.NoteOwnerEmail!, true).Any(note => note.Id == view.NoteId),
            SearchSource.OwnedTeamNote => app.GetTeamNotes(view.NoteOwnerEmail!, true).Any(note => note.Id == view.NoteId),
            SearchSource.InheritedTeamNote => app.GetInheritedTeamNotes(view.PersonEmail)
                .Any(note => note.Note.Id == view.NoteId && note.Owner.Email == view.NoteOwnerEmail),
            SearchSource.HistoricalTeamNote => app.GetHistoricalTeamContexts(view.PersonEmail)
                .Any(context => context.Id == view.HistoricalContextId &&
                    context.TeamNotes.Any(note => note.Id == view.NoteId && note.OwnerEmail == view.NoteOwnerEmail)),
            _ => false
        };
        Require(exists, "The exact remembered owner/note/context does not exist in the actual snapshot.");
    }

    private static void RequirePublished(IApplicationWorkflow app) =>
        Require(app.IsWorkspaceCurrent && app.CanSafelyClose && app.LocalSaveError is null && app.OperationError is null &&
            app.Synchronization is { WorkspaceId: not null, LocalRevision: not null, Error: null, Diverged: false,
                Phase: WorkspaceSynchronizationPhase.Idle, HasUnpublishedChanges: false },
            "The logical handoff requires an idle, healthy, current and completely published durable Workspace.");
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, "^[0-9A-F]{64}$");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
