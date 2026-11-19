using System.IO;
using System.Text.Json;

namespace OrgExplorer.Desktop.Smoke;

internal sealed class DesktopScenarioSelection
{
    private readonly Dictionary<string, string[]> partitions;
    private readonly Dictionary<string, string[]> highResolutionPartitions;
    internal string[] Smoke { get; }
    internal string[] HighResolution { get; }
    internal string[] Scale { get; }
    internal IEnumerable<string> All => Smoke.Concat(HighResolution).Concat(Scale);

    internal DesktopScenarioSelection(string directory)
    {
        Smoke = Read("DesktopScenarios.json");
        HighResolution = Read("HighResolutionScenarios.json");
        Scale = Read("UiScaleScenarios.json");
        if (All.Distinct().Count() != All.Count())
            throw new InvalidOperationException("Ordinary smoke, required high-resolution smoke and explicit UI scale must not overlap.");
        partitions = ReadPartitions("DesktopPartitions.json", Smoke, ["people", "workspace"]);
        highResolutionPartitions = ReadPartitions("HighResolutionPartitions.json", HighResolution, ["reading", "context"]);

        Dictionary<string, string[]> ReadPartitions(string file, string[] expected, string[] keys)
        {
            var groups = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                File.ReadAllText(Path.Combine(directory, file)));
            if (groups is null || groups.Count != keys.Length || keys.Any(key => !groups.ContainsKey(key)) ||
                groups.Values.Any(names => names is null || names.Length == 0 || names.Any(string.IsNullOrWhiteSpace)) ||
                !groups.Values.SelectMany(names => names).Order().SequenceEqual(expected.Order()))
                throw new InvalidOperationException($"{file} must contain every required scenario exactly once in its two partitions.");
            return groups;
        }

        string[] Read(string file)
        {
            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, file)));
            if (names is null || names.Length == 0 || names.Any(string.IsNullOrWhiteSpace) ||
                names.Distinct().Count() != names.Length)
                throw new InvalidOperationException("Desktop manifests must contain nonempty unique scenario names.");
            return names;
        }
    }

    internal string[] Select(string? requested) => requested switch
    {
        null => Smoke,
        "--high-resolution" => HighResolution,
        "--ui-scale" => Scale,
        "--desktop-people" => partitions["people"],
        "--desktop-workspace" => partitions["workspace"],
        "--high-resolution-reading" => highResolutionPartitions["reading"],
        "--high-resolution-context" => highResolutionPartitions["context"],
        "migration" => ["compatibility-settings"],
        "--search-favorites" => ["search-favorites"],
        "--team-notes" => ["team-context-history"],
        "--person-notes" => ["person-note-rendering"],
        _ when All.Contains(requested) => [requested],
        _ => throw new ArgumentException("Unknown desktop scenario. Available: " +
            string.Join(", ", All) + ", --high-resolution, --ui-scale, --desktop-people, --desktop-workspace, " +
            "--high-resolution-reading, --high-resolution-context")
    };
}
