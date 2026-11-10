using System.Text.Json;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class DesktopScenarioRunnerTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(".test-workspaces", "runner-" + Guid.NewGuid().ToString("N")));

    [Fact]
    [Trait("Category", "Runner")]
    public void Named_scenarios_execute_sequentially_in_isolated_fixtures_and_report_each_failure()
    {
        var visited = new List<string>();
        var report = Path.Combine(root, "results.json");
        var runner = new SmokeScenarioRunner([
            new("first", fixture =>
            {
                using var pending = JsonDocument.Parse(File.ReadAllText(report));
                Assert.Equal("Running", pending.RootElement.GetProperty("scenarios")[0].GetProperty("status").GetString());
                Assert.Equal("NotRun", pending.RootElement.GetProperty("scenarios")[1].GetProperty("status").GetString());
                visited.Add(fixture);
                File.WriteAllText(Path.Combine(fixture, "private.txt"), "Fictitious writing");
                throw new InvalidOperationException("Representative rendering failed");
            }),
            new("second", fixture =>
            {
                Assert.Single(visited);
                Assert.False(File.Exists(Path.Combine(fixture, "private.txt")));
                using var pending = JsonDocument.Parse(File.ReadAllText(report));
                Assert.Equal("Running", pending.RootElement.GetProperty("scenarios")[1].GetProperty("status").GetString());
                visited.Add(fixture);
            })
        ]);

        Assert.False(runner.Run(Path.Combine(root, "fixtures"), report));
        Assert.Equal(2, visited.Count);
        Assert.NotEqual(visited[0], visited[1]);
        using var result = JsonDocument.Parse(File.ReadAllText(report));
        var scenarios = result.RootElement.GetProperty("scenarios");
        Assert.Equal(new[] { "first", "second" }, scenarios.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("Failed", scenarios[0].GetProperty("status").GetString());
        Assert.Contains("Representative rendering failed", scenarios[0].GetProperty("error").GetString());
        Assert.Equal("Passed", scenarios[1].GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(visited[0], "private.txt")));
        Assert.False(Directory.Exists(visited[1]));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Scale_fixtures_opt_out_of_production_updates_before_any_application_can_launch()
    {
        var observed = new List<string>();
        var names = new[] { "scale-navigation-rendering", "scale-mutation-responsiveness", "update-settings", "installer-update" };
        var runner = new SmokeScenarioRunner(names.Select(name => new SmokeScenario(name, fixture =>
        {
            var preference = Path.Combine(fixture, "app-settings", "update-settings.json");
            if (name.StartsWith("scale-", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(preference));
                Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.False(json.RootElement.GetProperty("checkOnStartup").GetBoolean());
            }
            else Assert.False(File.Exists(preference));
            observed.Add(name);
        })).ToArray());

        Assert.True(runner.Run(Path.Combine(root, "fixtures"), Path.Combine(root, "result.json")));
        Assert.Equal(names, observed);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Only_complete_success_returns_success_and_empty_duplicate_or_reused_selections_cannot_pass()
    {
        var fixtureRoot = Path.Combine(root, "fixtures");
        var report = Path.Combine(root, "results.json");
        var count = 0;
        var passed = new SmokeScenarioRunner([new("only", _ => count++)]);
        Assert.True(passed.Run(fixtureRoot, report));
        Assert.Equal(1, count);
        using (var json = JsonDocument.Parse(File.ReadAllText(report)))
        {
            var scenario = Assert.Single(json.RootElement.GetProperty("scenarios").EnumerateArray());
            Assert.Equal("Passed", scenario.GetProperty("status").GetString());
            Assert.True(scenario.GetProperty("finishedAt").GetDateTimeOffset() >= scenario.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, scenario.GetProperty("error").ValueKind);
        }
        Assert.Throws<ArgumentException>(() => new SmokeScenarioRunner([]).Run(fixtureRoot, report));
        Assert.Throws<ArgumentException>(() => new SmokeScenarioRunner([new("same", _ => { }), new("same", _ => { })]).Run(fixtureRoot, report));
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "only"));
        Assert.False(passed.Run(fixtureRoot, report));
        Assert.Equal(1, count);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
