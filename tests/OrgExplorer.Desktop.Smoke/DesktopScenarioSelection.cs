using System.IO;
using System.Text.Json;

namespace OrgExplorer.Desktop.Smoke;

internal sealed class DesktopScenarioSelection
{
    private readonly Dictionary<string, string[]> partitions;
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
        partitions = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
            File.ReadAllText(Path.Combine(directory, "DesktopPartitions.json")))
            ?? throw new InvalidOperationException("Desktop partitions are missing.");
        if (partitions.Count != 2 || !partitions.ContainsKey("people") || !partitions.ContainsKey("workspace") ||
            partitions.Values.Any(names => names is null || names.Length == 0 || names.Any(string.IsNullOrWhiteSpace)) ||
            !partitions.Values.SelectMany(names => names).Order().SequenceEqual(Smoke.Order()))
            throw new InvalidOperationException("The two desktop partitions must contain every ordinary scenario exactly once.");

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
        "migration" => ["compatibility-settings"],
        "--search-favorites" => ["search-favorites"],
        "--team-notes" => ["team-context-history"],
        "--person-notes" => ["person-note-rendering"],
        _ when All.Contains(requested) => [requested],
        _ => throw new ArgumentException("Unknown desktop scenario. Available: " +
            string.Join(", ", All) + ", --high-resolution, --ui-scale, --desktop-people, --desktop-workspace")
    };
}
