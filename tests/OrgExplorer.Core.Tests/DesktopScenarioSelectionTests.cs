using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class DesktopScenarioSelectionTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Default_smoke_excludes_scale_and_explicit_ui_scale_selects_both_independent_scenarios()
    {
        var selection = new DesktopScenarioSelection(AppContext.BaseDirectory);
        Assert.Equal(new[] { "profile-journey", "draft-navigation", "person-note-rendering",
            "team-context-history", "search-favorites", "archival-navigation",
            "compatibility-settings", "external-refresh" }, selection.Select(null));
        Assert.Equal(new[] { "scale-navigation-rendering", "scale-mutation-responsiveness" },
            selection.Select("--ui-scale"));
        Assert.Equal(new[] { "scale-navigation-rendering" }, selection.Select("scale-navigation-rendering"));
        Assert.Throws<ArgumentException>(() => selection.Select("scale"));
        Assert.Throws<ArgumentException>(() => selection.Select("unknown"));
    }
}
