using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class PersonSelectionTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Canceled_archive_retains_current_selection_when_the_archived_list_is_collapsed()
    {
        PersonSelection.AssertMatchesProfile("morgan@example.com", showArchivedPeople: false,
            list => list == "People" ? ["morgan@example.com"] :
                throw new InvalidOperationException("Missing actual UI control: " + list));
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Showing_archived_people_checks_both_lists_against_the_retained_profile()
    {
        PersonSelection.AssertMatchesProfile("avery@example.com", showArchivedPeople: true,
            list => list == "ArchivedPeople" ? ["avery@example.com"] : []);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false, "People")]
    [InlineData(true, "People")]
    [InlineData(true, "ArchivedPeople")]
    public void Wrong_selection_in_any_visible_list_remains_a_failure(bool showArchivedPeople, string wrongList)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PersonSelection.AssertMatchesProfile("avery@example.com", showArchivedPeople,
                list => list == wrongList ? ["morgan@example.com"] : ["avery@example.com"]));

        Assert.Contains("different Person selected", error.Message);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false, "People")]
    [InlineData(true, "People")]
    [InlineData(true, "ArchivedPeople")]
    public void Required_visible_lists_cannot_be_missing(bool showArchivedPeople, string missingList)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PersonSelection.AssertMatchesProfile("avery@example.com", showArchivedPeople,
                list => list == missingList
                    ? throw new InvalidOperationException("Missing actual UI control: " + list) : []));

        Assert.Equal("Missing actual UI control: " + missingList, error.Message);
    }
}
