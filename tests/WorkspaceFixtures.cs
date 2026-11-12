using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

internal static class WorkspaceFixtures
{
    private static readonly Lazy<Dictionary<string, byte[]>> Current = new(() =>
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", "current-fixture-" + Guid.NewGuid().ToString("N")));
        try
        {
            ExportCurrent(root);
            return Bytes(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    internal static void CopyCurrent(string destination)
    {
        foreach (var (relative, bytes) in Current.Value)
        {
            var path = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }

    private static void ExportCurrent(string destination)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "workspace-v2.zip"));
        var selector = archive.Entries.Single(entry => entry.FullName == "workspace.json");
        using var selectorStream = selector.Open();
        var generation = JsonNode.Parse(selectorStream)!["generation"]!.GetValue<string>();
        var documents = new Dictionary<string, Dictionary<string, byte[]>>();
        foreach (var entry in archive.Entries.Where(e => e.Length > 0))
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var parts = entry.FullName.Split(['/', '\\']);
            if (parts[0] is "notes" or "team-notes")
            {
                var path = Path.Combine(destination, parts[0], parts[^1]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                continue;
            }
            if (parts[0] != "generations" || parts[1] != generation) continue;
            var family = parts[2] switch
            {
                "profile-history" => "profileHistory",
                "historical-team-context" => "historicalTeamContext",
                "batches.json" => "acceptedBatches",
                "team-context.json" => "teamContext",
                _ => parts[2]
            };
            if (!documents.TryGetValue(family, out var records)) documents[family] = records = [];
            if (family == "photos") records[parts[^1]] = bytes;
            else
            {
                var data = JsonNode.Parse(bytes)!;
                if (family is "acceptedBatches" or "teamContext")
                    foreach (var pair in data.AsObject())
                        records[pair.Key] = JsonSerializer.SerializeToUtf8Bytes(
                            new JsonObject { [pair.Key] = pair.Value!.DeepClone() });
                else records[data["email"]!.GetValue<string>().ToLowerInvariant()] = bytes;
            }
        }
        WriteCurrent(destination, documents);
    }

    internal static void WriteCurrent(string destination, Dictionary<string, Dictionary<string, byte[]>> documents)
    {
        Directory.CreateDirectory(destination);
        var roots = new JsonObject();
        foreach (var family in new[] { "people", "profileHistory", "acceptedBatches", "photos", "teamContext", "historicalTeamContext" })
        {
            var entries = new Dictionary<string, string>();
            foreach (var (key, bytes) in documents.GetValueOrDefault(family) ?? [])
            {
                var extension = family == "photos" ? Path.GetExtension(key) : ".json";
                var relative = Path.Combine("documents", family, Guid.NewGuid().ToString("N") + extension);
                var path = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                entries.Add(key, relative);
            }
            roots[family] = entries.Count == 0 ? null : WriteIndex(family, entries, 0);
        }
        File.WriteAllText(Path.Combine(destination, "workspace.json"), new JsonObject
        {
            ["schemaVersion"] = 3, ["storageProtocol"] = 1, ["commit"] = Guid.NewGuid(), ["roots"] = roots,
            ["recordVersions"] = JsonSerializer.SerializeToNode(new { people = 2, profileHistory = 2, acceptedBatches = 1,
                personNotes = 1, teamNotes = 1, photos = 1, teamContext = 1, historicalTeamContext = 1 })
        }.ToJsonString());
        using var workflow = ApplicationWorkflow.CreateFileBacked();
        if (!workflow.OpenWorkspace(destination) || !workflow.RetryStorageMaintenance())
            throw new InvalidOperationException(workflow.Status);

        string WriteIndex(string family, Dictionary<string, string> entries, int depth)
        {
            Dictionary<string, string>? children = null;
            if (entries.Count > 16)
                children = entries.GroupBy(pair => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Key)))
                    .ToLowerInvariant()[depth].ToString()).ToDictionary(group => group.Key,
                        group => WriteIndex(family, group.ToDictionary(), depth + 1));
            var relative = Path.Combine("index", Guid.NewGuid().ToString("N") + ".json");
            Directory.CreateDirectory(Path.Combine(destination, "index"));
            File.WriteAllBytes(Path.Combine(destination, relative), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, family, entries = children is null ? entries : null, children
            }));
            return relative;
        }
    }

    internal static void CopyVersionOne(string destination)
    {
        System.IO.Compression.ZipFile.ExtractToDirectory(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "workspace-v1.zip"), destination);
    }

    internal static void CopyVersion(int version, string destination) =>
        System.IO.Compression.ZipFile.ExtractToDirectory(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", $"workspace-v{version}.zip"), destination);

    internal static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    internal static Dictionary<string, byte[]> Bytes(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".workspace.lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllBytes);

    internal static Dictionary<string, string> IndexedDocuments(string root)
    {
        var pointer = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "workspace.json")))!;
        return IndexedDocuments(root, pointer["roots"]!.AsObject());
    }

    internal static Dictionary<string, string> RecoveryDocuments(string root)
    {
        var recovery = Path.Combine(root, "recovery");
        var pointer = JsonNode.Parse(File.ReadAllText(Path.Combine(recovery, "current.json")))!;
        var roots = pointer["workspace"]!["roots"]!.AsObject().DeepClone().AsObject();
        foreach (var family in pointer["noteRoots"]!.AsObject()) roots.Add(family.Key, family.Value?.DeepClone());
        return IndexedDocuments(recovery, roots);
    }

    private static Dictionary<string, string> IndexedDocuments(string root, JsonObject roots)
    {
        var result = new Dictionary<string, string>();
        foreach (var family in roots)
            if (family.Value is not null) Visit(family.Key, family.Value.GetValue<string>());
        return result;

        void Visit(string family, string relative)
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(root, relative)))!;
            if (node["entries"] is JsonObject entries)
                foreach (var entry in entries) result.Add(family + ":" + entry.Key, Path.Combine(root, entry.Value!.GetValue<string>()));
            else
                foreach (var child in node["children"]!.AsObject()) Visit(family, child.Value!.GetValue<string>());
        }
    }

}
