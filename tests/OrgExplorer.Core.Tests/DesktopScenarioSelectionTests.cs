using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class DesktopScenarioSelectionTests
{
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
