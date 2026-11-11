using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private static readonly string[] IndexedFamilies =
        ["people", "profileHistory", "acceptedBatches", "photos", "teamContext", "historicalTeamContext"];

    private sealed record IncrementalMetadata(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid Commit,
        [property: JsonRequired] Dictionary<string, string?> Roots,
        [property: JsonRequired] Dictionary<string, int> RecordVersions);

    private sealed record IndexNode(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string Family,
        [property: JsonRequired] Dictionary<string, string>? Entries,
        [property: JsonRequired] Dictionary<string, string>? Children);

    private static IncrementalMetadata ReadIncrementalMetadata(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        RequireVersion(json.RootElement, "Workspace", [3]);
        CheckProperties(json.RootElement, ["schemaVersion", "commit", "roots", "recordVersions"], "Workspace");
        var metadata = json.RootElement.Deserialize<IncrementalMetadata>(JsonOptions)
            ?? throw new FormatException("Invalid incremental Workspace selector.");
        if (metadata.Commit == Guid.Empty || metadata.Roots is null ||
            !metadata.Roots.Keys.Order().SequenceEqual(IndexedFamilies.Order()) || metadata.RecordVersions is null ||
            !metadata.RecordVersions.Keys.Order().SequenceEqual(CurrentRecordVersions.Keys.Order()))
            throw new FormatException("Incomplete incremental Workspace selector.");
        foreach (var (family, version) in metadata.RecordVersions)
            if (version != CurrentRecordVersions[family]) throw new UnsupportedWorkspaceVersion(family, version);
        CheckProperties(json.RootElement.GetProperty("roots"), IndexedFamilies, "Workspace roots");
        CheckProperties(json.RootElement.GetProperty("recordVersions"), CurrentRecordVersions.Keys.ToArray(), "Record versions");
        return metadata;
    }

    private static string IndexKeyHash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string IncrementalPath(string root, string relative, string? family = null)
    {
        var pattern = family is null ? @"^index[/\\][a-f0-9]{32}\.json$" :
            @"^documents[/\\]" + Regex.Escape(family) + @"[/\\][a-f0-9]{32}\." +
            (family == "photos" ? "(png|jpg)$" : family is "personNotes" or "teamNotes" ? "md$" : "json$");
        if (relative is null || !Regex.IsMatch(relative, pattern))
            throw new FormatException("Invalid incremental Workspace file reference.");
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        for (var current = path; !current.Equals(root, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current)!)
            if (File.Exists(current) || Directory.Exists(current)) RejectWorkspaceLink(current);
        return path;
    }

    private static IndexNode ReadIndexNode(string root, string relative, string family, string prefix,
        IReadOnlyDictionary<string, byte[]>? bytes = null)
    {
        var path = IncrementalPath(root, relative);
        using var json = JsonDocument.Parse(bytes is null ? ReadWorkspaceBytesUncached(path) :
            bytes.TryGetValue(path, out var value) ? value : throw new IOException("Workspace index is unavailable."));
        RequireVersion(json.RootElement, "Workspace index", [1]);
        CheckProperties(json.RootElement, ["schemaVersion", "family", "entries", "children"], "Workspace index");
        var node = json.RootElement.Deserialize<IndexNode>(JsonOptions)
            ?? throw new FormatException("Invalid Workspace index.");
        if (node.Family != family || (node.Entries is null) == (node.Children is null) || prefix.Length > 64 ||
            node.Entries is { Count: > 16 } || node.Children is { Count: 0 or > 16 } ||
            node.Children is not null && prefix.Length == 64)
            throw new FormatException("Invalid Workspace index family, depth or fanout.");
        if (node.Entries is { } entries)
        {
            CheckProperties(json.RootElement.GetProperty("entries"), entries.Keys.ToArray(), "Index entries");
            foreach (var (key, document) in entries)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key != key.ToLowerInvariant() ||
                    !IndexKeyHash(key).StartsWith(prefix, StringComparison.Ordinal))
                    throw new FormatException("Invalid Workspace index identity or partition.");
                IncrementalPath(root, document, family);
            }
        }
        if (node.Children is { } children)
        {
            CheckProperties(json.RootElement.GetProperty("children"), children.Keys.ToArray(), "Index children");
            foreach (var (edge, child) in children)
            {
                if (!Regex.IsMatch(edge, "^[a-f0-9]$")) throw new FormatException("Invalid Workspace index edge.");
                IncrementalPath(root, child);
            }
        }
        return node;
    }

    private sealed record IncrementalGraph(IncrementalMetadata Metadata,
        Dictionary<string, Dictionary<string, string>> Documents, HashSet<string> Paths);

    private static IncrementalGraph ResolveIncrementalGraph(string root, byte[] selector,
        IReadOnlyDictionary<string, byte[]>? bytes = null)
    {
        var metadata = ReadIncrementalMetadata(selector);
        var graph = new IncrementalGraph(metadata, [], new(StringComparer.OrdinalIgnoreCase));
        foreach (var (family, reference) in metadata.Roots)
        {
            var documents = new Dictionary<string, string>(StringComparer.Ordinal);
            graph.Documents.Add(family, documents);
            if (reference is not null) Visit(reference, "");

            void Visit(string relative, string prefix)
            {
                var path = IncrementalPath(root, relative);
                if (!graph.Paths.Add(path)) throw new FormatException("Duplicate or cyclic Workspace index reference.");
                var node = ReadIndexNode(root, relative, family, prefix, bytes);
                if (node.Entries is { } entries)
                    foreach (var (key, document) in entries)
                    {
                        if (!documents.TryAdd(key, document)) throw new FormatException("Duplicate Workspace identity.");
                        var documentPath = IncrementalPath(root, document, family);
                        if (!graph.Paths.Add(documentPath)) throw new FormatException("Duplicate Workspace document reference.");
                    }
                else
                    foreach (var (edge, child) in node.Children!) Visit(child, prefix + edge);
            }
        }
        return graph;
    }

    private static IEnumerable<string> EnumerateIncrementalWorkspaceFiles(string root)
    {
        RejectWorkspaceLink(root);
        var pointer = Path.Combine(root, "workspace.json");
        if (File.Exists(pointer))
        {
            RejectWorkspaceLink(pointer);
            var graph = ResolveIncrementalGraph(root, ReadWorkspaceBytesUncached(pointer));
            foreach (var path in graph.Paths) yield return path;
        }
        // Retired objects and transactions are not current data. Unknown ordinary files
        // still participate in the revision, as they do in the generation format.
        foreach (var path in EnumerateOtherFiles(root))
            yield return path;

        IEnumerable<string> EnumerateOtherFiles(string directory)
        {
            RejectWorkspaceLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (directory == root && new[] { "index", "documents", "transactions", "maintenance", "recovery", "generations" }
                    .Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)) continue;
                RejectWorkspaceLink(entry);
                if (IsWorkspacePublicationFile(root, entry)) continue;
                if (Directory.Exists(entry))
                    foreach (var child in EnumerateOtherFiles(entry)) yield return child;
                else yield return entry;
            }
        }
    }

    private string UpdateIndex(string? reference, string family, string key, string document, string prefix,
        IReadOnlyDictionary<string, byte[]> source, Action<string, byte[]> write, string? indexRoot = null) =>
        UpdateIndexBatch(reference, family, new Dictionary<string, string> { [key] = document }, prefix,
            source, write, indexRoot);

    private string UpdateIndexBatch(string? reference, string family, IReadOnlyDictionary<string, string> updates, string prefix,
        IReadOnlyDictionary<string, byte[]> source, Action<string, byte[]> write, string? indexRoot = null)
    {
        var root = indexRoot ?? workspace!;
        var node = reference is null ? new IndexNode(1, family, [], null) :
            ReadIndexNode(root, reference, family, prefix, source);
        if (node.Entries is { } existing)
        {
            var entries = new Dictionary<string, string>(existing);
            foreach (var (key, document) in updates) entries[key] = document;
            return Build(entries, prefix);
        }
        var children = new Dictionary<string, string>(node.Children!);
        foreach (var group in updates.GroupBy(e => IndexKeyHash(e.Key)[prefix.Length].ToString()))
            children[group.Key] = UpdateIndexBatch(children.GetValueOrDefault(group.Key), family,
                group.ToDictionary(e => e.Key, e => e.Value), prefix + group.Key, source, write, root);
        return WriteNode(new(1, family, null, children));

        string Build(Dictionary<string, string> entries, string partition)
        {
            if (entries.Count <= 16) return WriteNode(new(1, family, entries, null));
            if (partition.Length == 64) throw new FormatException("Workspace identity hash collision exceeds index capacity.");
            var branches = new Dictionary<string, string>();
            foreach (var group in entries.GroupBy(e => IndexKeyHash(e.Key)[partition.Length].ToString()))
                branches[group.Key] = Build(group.ToDictionary(e => e.Key, e => e.Value), partition + group.Key);
            return WriteNode(new(1, family, null, branches));
        }

        string WriteNode(IndexNode value)
        {
            var relative = Path.Combine("index", Guid.NewGuid().ToString("N") + ".json");
            write(relative, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
            return relative;
        }
    }
}
