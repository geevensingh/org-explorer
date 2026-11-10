using System.IO;
using System.Text.Json;

namespace OrgExplorer.Desktop.Smoke;

internal sealed class DesktopScenarioSelection
{
    internal string[] Smoke { get; }
    internal string[] Scale { get; }

    internal DesktopScenarioSelection(string directory)
    {
        Smoke = Read("DesktopScenarios.json");
        Scale = Read("UiScaleScenarios.json");
        if (Smoke.Intersect(Scale).Any())
            throw new InvalidOperationException("Required desktop smoke and explicit UI scale must not overlap.");

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
        "--ui-scale" => Scale,
        "migration" => ["compatibility-settings"],
        "--search-favorites" => ["search-favorites"],
        "--team-notes" => ["team-context-history"],
        "--person-notes" => ["person-note-rendering"],
        _ when Smoke.Concat(Scale).Contains(requested) => [requested],
        _ => throw new ArgumentException("Unknown desktop scenario. Available: " +
            string.Join(", ", Smoke.Concat(Scale)) + ", --ui-scale")
    };
}
