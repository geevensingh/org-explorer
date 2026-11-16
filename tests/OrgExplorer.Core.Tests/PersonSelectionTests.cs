using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class PersonSelectionTests
{
    [Theory]
    [Trait("Category", "Runner")]
    [InlineData("Local save failed. Editing is paused.", "Failed")]
    [InlineData("Local save stalled after 3 seconds.", "Stalled")]
    [InlineData("Local transaction outcome is unknown.", "Indeterminate")]
    public void Recovery_selection_reaches_the_actual_destination_while_local_failure_remains_busy(string activity, string phase)
    {
        var selected = "morgan@example.com";
        var observed = false;
        bool IsIdle() => LocalWorkObservation.Observe(() => activity, () => phase,
            () => throw new InvalidOperationException("Busy work must not require idle control discovery."));
        Assert.False(IsIdle());

        PersonSelection.Request(PersonSelection.Mode.Recovery,
            () => { if (!IsIdle()) throw new TimeoutException("The real local failure is deliberately not idle."); },
            () => selected = "avery@example.com",
            () => { Assert.Equal("avery@example.com", selected); Assert.False(IsIdle()); observed = true; });

        Assert.True(observed);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Recovery_selection_cannot_skip_observing_an_unsuccessful_destination()
    {
        var selected = "morgan@example.com";
        Assert.Throws<InvalidOperationException>(() => PersonSelection.Request(PersonSelection.Mode.Recovery,
            () => throw new TimeoutException("Unresolved work is not idle."), () => { },
            () =>
            {
                if (selected != "avery@example.com") throw new InvalidOperationException("The actual destination did not change.");
            }));
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false, "idle,select,observe")]
    [InlineData(true, "select")]
    public void Ordinary_and_expected_rejection_selection_keep_their_existing_preconditions(
        bool expectedRejection, string expected)
    {
        var calls = new List<string>();
        var mode = expectedRejection ? PersonSelection.Mode.ExpectedRejection : PersonSelection.Mode.Ordinary;
        PersonSelection.Request(mode, () => calls.Add("idle"), () => calls.Add("select"), () => calls.Add("observe"));
        Assert.Equal(expected, string.Join(",", calls));
    }

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
