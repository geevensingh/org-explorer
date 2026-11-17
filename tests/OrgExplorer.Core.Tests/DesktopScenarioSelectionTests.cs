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
            File.WriteAllText(Path.Combine(root, "HighResolutionScenarios.json"), """["high-resolution"]""");
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
        Assert.Empty(selection.Select("--ui-scale").Intersect(expected));
        Assert.Equal(["responsive-team-history"], selection.Select("responsive-team-history"));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Default_smoke_excludes_scale_and_explicit_ui_scale_selects_both_independent_scenarios()
    {
        var selection = new DesktopScenarioSelection(AppContext.BaseDirectory);
        Assert.Equal(new[] { "profile-journey", "draft-navigation", "person-note-rendering",
            "team-context-history", "search-favorites", "archival-navigation",
            "compatibility-settings", "update-settings", "external-refresh" }, selection.Select(null));
        Assert.Equal(new[] { "scale-navigation-rendering", "scale-mutation-responsiveness" },
            selection.Select("--ui-scale"));
        Assert.Equal(new[] { "scale-navigation-rendering" }, selection.Select("scale-navigation-rendering"));
        Assert.Throws<ArgumentException>(() => selection.Select("scale"));
        Assert.Throws<ArgumentException>(() => selection.Select("unknown"));
    }
}
