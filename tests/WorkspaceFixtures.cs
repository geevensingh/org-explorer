using System.IO;
using System.Text.Json.Nodes;

namespace OrgExplorer.TestFixtures;

internal static class WorkspaceFixtures
{
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

    internal static void WriteLegacyPointer(string root, JsonNode pointer)
    {
        pointer["schemaVersion"] = 1;
        pointer.AsObject().Remove("files");
        pointer.AsObject().Remove("recordVersions");
        var generation = Path.Combine(root, "generations", pointer["generation"]!.GetValue<string>());
        foreach (var folder in new[] { "people", "profile-history" })
        {
            var directory = Path.Combine(generation, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                var data = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                data.Remove("schemaVersion");
                File.WriteAllText(file, data.ToJsonString());
            }
        }
        File.WriteAllText(Path.Combine(root, "workspace.json"), pointer.ToJsonString());
    }
}
