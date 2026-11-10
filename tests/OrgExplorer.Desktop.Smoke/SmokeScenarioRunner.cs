using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrgExplorer.Desktop.Smoke;

internal sealed record SmokeScenario(string Name, Action<string> Execute);

internal sealed class SmokeScenarioRunner(IReadOnlyList<SmokeScenario> scenarios)
{
    internal bool Run(string fixtureRoot, string resultsPath)
    {
        if (scenarios.Count == 0 || scenarios.Select(s => s.Name).Distinct().Count() != scenarios.Count ||
            scenarios.Any(s => !Regex.IsMatch(s.Name, "^[a-z0-9]+(?:-[a-z0-9]+)*$")))
            throw new ArgumentException("Supply nonempty, unique scenario names safe for fixture paths.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultsPath))!);
        var results = scenarios.Select(s => new ScenarioResult(s.Name, Path.GetFullPath(Path.Combine(fixtureRoot, s.Name)))).ToArray();
        Save();
        for (var index = 0; index < scenarios.Count; index++)
        {
            var result = results[index];
            result.Status = "Running";
            result.StartedAt = DateTimeOffset.UtcNow;
            Save();
            Console.WriteLine($"START: {result.Name}; fixture: {result.Fixture}");
            var timer = Stopwatch.StartNew();
            try
            {
                if (Directory.Exists(result.Fixture))
                    throw new InvalidOperationException("Scenario fixture already exists; refusing to reuse state.");
                Directory.CreateDirectory(result.Fixture);
                scenarios[index].Execute(result.Fixture);
                Directory.Delete(result.Fixture, true);
                result.Status = "Passed";
            }
            catch (Exception error)
            {
                result.Status = "Failed";
                result.Error = error.ToString();
                Console.Error.WriteLine($"{result.Name}: {error}");
            }
            result.Seconds = timer.Elapsed.TotalSeconds;
            result.FinishedAt = DateTimeOffset.UtcNow;
            Save();
            Console.WriteLine($"{result.Status}: {result.Name} ({result.Seconds:F2}s)");
        }
        return results.All(r => r.Status == "Passed");

        void Save()
        {
            var staging = resultsPath + ".writing";
            File.WriteAllText(staging, JsonSerializer.Serialize(new { schemaVersion = 1, scenarios = results }, Json));
            File.Move(staging, resultsPath, true);
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
    };

    private sealed class ScenarioResult(string name, string fixture)
    {
        public string Name { get; } = name;
        public string Fixture { get; } = fixture;
        public string Status { get; set; } = "NotRun";
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public double Seconds { get; set; }
        public string? Error { get; set; }
    }
}
