using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class DesktopScenarioSelectionTests
{
    [Theory]
    [InlineData("""{"people":["first"],"workspace":["first"]}""")]
    [InlineData("""{"people":["first"],"workspace":[]}""")]
    [InlineData("""{"people":["first"],"workspace":["second","scale"]}""")]
    [InlineData("""{"people":["first"],"unknown":["second"]}""")]
    [Trait("Category", "Runner")]
    public void Incomplete_overlapping_or_unexpected_partition_plans_are_refused_before_execution(string partitions)
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "selection-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "DesktopScenarios.json"), """["first","second"]""");
            File.WriteAllText(Path.Combine(root, "HighResolutionScenarios.json"), """["high-first","high-second"]""");
            File.WriteAllText(Path.Combine(root, "HighResolutionPartitions.json"),
                """{"reading":["high-first"],"context":["high-second"]}""");
            File.WriteAllText(Path.Combine(root, "UiScaleScenarios.json"), """["scale"]""");
            File.WriteAllText(Path.Combine(root, "DesktopPartitions.json"), partitions);
            Assert.Throws<InvalidOperationException>(() => new DesktopScenarioSelection(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Isolated_desktop_partitions_preserve_every_ordinary_scenario_once_without_scale_or_high_resolution()
    {
        var selection = new DesktopScenarioSelection(AppContext.BaseDirectory);
        var people = selection.Select("--desktop-people");
        var workspace = selection.Select("--desktop-workspace");
        Assert.Equal(["profile-journey", "draft-navigation", "person-note-rendering",
            "team-context-history", "search-favorites"], people);
        Assert.Equal(["archival-navigation", "compatibility-settings", "update-settings", "external-refresh"], workspace);
        Assert.Equal(selection.Smoke, people.Concat(workspace));
        Assert.Empty(people.Intersect(workspace));
        Assert.Empty(people.Concat(workspace).Intersect(selection.HighResolution.Concat(selection.Scale)));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Required_high_resolution_is_explicit_complete_and_independent_from_ordinary_smoke_and_ui_scale()
    {
        var selection = new DesktopScenarioSelection(AppContext.BaseDirectory);
        var expected = new[] { "responsive-workspace", "responsive-draft-navigation", "responsive-team-history" };
        Assert.Equal(expected, selection.Select("--high-resolution"));
        Assert.Empty(selection.Select(null).Intersect(expected));
        Assert.Empty(selection.Scale.Intersect(expected));
        Assert.Equal(["responsive-team-history"], selection.Select("responsive-team-history"));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void High_resolution_partitions_preserve_every_required_scenario_once_without_other_lanes()
    {
        var selection = new DesktopScenarioSelection(AppContext.BaseDirectory);
        var reading = selection.Select("--high-resolution-reading");
        var context = selection.Select("--high-resolution-context");

        Assert.Equal(["responsive-workspace"], reading);
        Assert.Equal(["responsive-draft-navigation", "responsive-team-history"], context);
        Assert.Equal(selection.HighResolution, reading.Concat(context));
        Assert.Empty(reading.Intersect(context));
        Assert.Empty(reading.Concat(context).Intersect(selection.Smoke.Concat(selection.Scale)));
    }

    [Theory]
    [InlineData("""{"reading":["high-first"],"context":["high-first"]}""")]
    [InlineData("""{"reading":["high-first"],"context":[]}""")]
    [InlineData("""{"reading":["high-first"],"context":["high-second","scale"]}""")]
    [InlineData("""{"reading":["high-first"],"unknown":["high-second"]}""")]
    [Trait("Category", "Runner")]
    public void Incomplete_overlapping_or_unexpected_high_resolution_partitions_are_refused(string partitions)
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "selection-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "DesktopScenarios.json"), """["first","second"]""");
            File.WriteAllText(Path.Combine(root, "DesktopPartitions.json"), """{"people":["first"],"workspace":["second"]}""");
            File.WriteAllText(Path.Combine(root, "HighResolutionScenarios.json"), """["high-first","high-second"]""");
            File.WriteAllText(Path.Combine(root, "HighResolutionPartitions.json"), partitions);
            File.WriteAllText(Path.Combine(root, "UiScaleScenarios.json"), """["scale"]""");
            Assert.Throws<InvalidOperationException>(() => new DesktopScenarioSelection(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Default_smoke_excludes_scale_and_isolated_workers_preserve_every_scale_scenario_once()
    {
        var selection = new DesktopScenarioSelection(AppContext.BaseDirectory);
        Assert.Equal(new[] { "profile-journey", "draft-navigation", "person-note-rendering",
            "team-context-history", "search-favorites", "archival-navigation",
            "compatibility-settings", "update-settings", "external-refresh" }, selection.Select(null));
        Assert.Equal(new[] { "scale-mutation-save-prefix" }, selection.Select("--ui-scale-prefix"));
        Assert.Equal(new[] { "scale-mutation-delivery-tail", "scale-navigation-rendering" }, selection.Select("--ui-scale-tail"));
        Assert.Equal(selection.Scale, selection.Select("--ui-scale-prefix").Concat(selection.Select("--ui-scale-tail")));
        Assert.Empty(selection.Select("--ui-scale-prefix").Intersect(selection.Select("--ui-scale-tail")));
        Assert.Throws<ArgumentException>(() => selection.Select("--ui-scale"));
        Assert.Equal(new[] { "scale-navigation-rendering" }, selection.Select("scale-navigation-rendering"));
        Assert.Throws<ArgumentException>(() => selection.Select("scale"));
        Assert.Throws<ArgumentException>(() => selection.Select("unknown"));
    }

    [Theory]
    [InlineData("""{"prefix":["prefix"],"tail":["prefix"]}""")]
    [InlineData("""{"prefix":["prefix"],"tail":[]}""")]
    [InlineData("""{"prefix":["prefix"],"tail":["tail","replacement"]}""")]
    [InlineData("""{"prefix":["prefix"],"unknown":["tail"]}""")]
    [Trait("Category", "Runner")]
    public void Invalid_split_worker_union_is_refused_before_execution(string workers)
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "selection-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "DesktopScenarios.json"), """["first","second"]""");
            File.WriteAllText(Path.Combine(root, "DesktopPartitions.json"), """{"people":["first"],"workspace":["second"]}""");
            File.WriteAllText(Path.Combine(root, "HighResolutionScenarios.json"), """["high-first","high-second"]""");
            File.WriteAllText(Path.Combine(root, "HighResolutionPartitions.json"),
                """{"reading":["high-first"],"context":["high-second"]}""");
            File.WriteAllText(Path.Combine(root, "UiScaleScenarios.json"), """["prefix","tail"]""");
            File.WriteAllText(Path.Combine(root, "UiScaleWorkers.json"), workers);
            Assert.Throws<InvalidOperationException>(() => new DesktopScenarioSelection(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
