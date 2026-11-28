using System.Text.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class SearchFavoriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_batch_cannot_publish_old_reporting_expansion_after_concurrent_graph_replacement(bool replaceWorkspace)
    {
        var core = ApplicationWorkflow.CreateInMemory();
        var proxy = DispatchProxy.Create<IApplicationWorkflow, ReportingChangeBoundary>();
        var boundary = (ReportingChangeBoundary)(object)proxy;
        boundary.Inner = core;
        using var app = new WorkspaceViewModel(proxy);
        Assert.True(app.OpenWorkspace("original"));
        Apply(app, """
            {"email":"old-root@example.com","displayName":"Old root","managerEmail":null},
            {"email":"old-leaf@example.com","displayName":"Old leaf","managerEmail":"old-root@example.com"}
            """, 1);
        app.RememberPeopleBrowsing([], 0);
        var incoming = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"88000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"new-root@example.com","displayName":"New root","managerEmail":null}]}
            """);
        Assert.True(incoming.CanApply);
        Assert.True(app.ExecutePeopleTreeAction("old-root@example.com", PeopleTreeAction.ExpandSubtree));
        boundary.DuringReports = () =>
        {
            boundary.DuringReports = null;
            Assert.True(Task.Run(() => replaceWorkspace
                ? app.OpenWorkspace("replacement")
                : app.ApplyImport(incoming.Id)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        };

        Assert.Empty(app.ContinuePeopleTreeExpansion());

        Assert.DoesNotContain("old-root@example.com", app.PeopleNavigation.ExpandedPeople);
        if (replaceWorkspace)
        {
            Assert.Empty(app.People);
            Assert.Null(app.PeopleTreeExpansion);
            Assert.Null(app.PeopleTreeActionFeedback);
        }
        else
        {
            Assert.Contains(app.People, person => person.Email == "new-root@example.com");
            Assert.Equal(PeopleTreeExpansionStatus.Interrupted, app.PeopleTreeExpansion!.Status);
            Assert.Equal(0, app.PeopleTreeExpansion.VisitedPeople);
            Assert.Equal(0, app.PeopleTreeExpansion.ExpandedBranches);
        }
    }

    public class ReportingChangeBoundary : DispatchProxy
    {
        internal IApplicationWorkflow Inner = null!;
        internal Action? DuringReports;
        private IReadOnlyList<ReportingNode>? source, wrapped;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            try
            {
                var result = method!.Invoke(Inner, arguments);
                if (method.Name != "get_ReportingRoots") return result;
                if (!ReferenceEquals(source, result))
                {
                    source = (IReadOnlyList<ReportingNode>)result!;
                    wrapped = source.Select(node => node with
                    {
                        Reports = new ReportBoundary(node.Reports, () => DuringReports?.Invoke())
                    }).ToArray();
                }
                return wrapped;
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    [Fact]
    public void A_refused_concurrent_open_does_not_consume_the_unpublished_expansion_batch()
    {
        var proxy = DispatchProxy.Create<IApplicationWorkflow, ReportingChangeBoundary>();
        var boundary = (ReportingChangeBoundary)(object)proxy;
        boundary.Inner = ApplicationWorkflow.CreateInMemory();
        using var app = new WorkspaceViewModel(proxy);
        Assert.True(app.OpenWorkspace("original"));
        Apply(app, """
            {"email":"root@example.com","displayName":"Root","managerEmail":null},
            {"email":"leaf@example.com","displayName":"Leaf","managerEmail":"root@example.com"}
            """, 1);
        Assert.True(app.BeginProfileEdit("root@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Keep writing"));
        app.RememberPeopleBrowsing([], 0);
        Assert.True(app.ExecutePeopleTreeAction("root@example.com", PeopleTreeAction.ExpandSubtree));
        var progress = app.PeopleTreeExpansion;
        boundary.DuringReports = () =>
        {
            boundary.DuringReports = null;
            Assert.False(Task.Run(() => app.OpenWorkspace("refused")).WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter().GetResult());
        };
        Assert.Empty(app.ContinuePeopleTreeExpansion());
        Assert.Equal(progress, app.PeopleTreeExpansion);
        Assert.Empty(app.PeopleNavigation.ExpandedPeople);
        Assert.Equal(["root@example.com"], app.ContinuePeopleTreeExpansion());
        Assert.Equal(PeopleTreeExpansionStatus.Completed, app.PeopleTreeExpansion!.Status);
        Assert.Equal(2, app.PeopleTreeExpansion.VisitedPeople);
        Assert.Equal(["root@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal("Keep writing", app.ProfileEdit!.Draft.OfficeNumber);
    }

    private sealed class ReportBoundary(IReadOnlyList<ReportingNode> reports, Action observe) : IReadOnlyList<ReportingNode>
    {
        public int Count { get { var count = reports.Count; observe(); return count; } }
        public ReportingNode this[int index] => reports[index];
        public IEnumerator<ReportingNode> GetEnumerator() => reports.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_Workspace_replacement_clears_tree_operation_but_refused_open_retains_it(bool cancel)
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, string.Join(",", Enumerable.Range(0, 100).Select(index => JsonSerializer.Serialize(new
        {
            email = $"replacement{index:000}@example.com", displayName = $"Replacement {index}",
            managerEmail = "morgan@example.com"
        }))), 2);
        app.RememberPeopleBrowsing([], 0);
        Assert.True(app.ExecutePeopleTreeAction("morgan@example.com", PeopleTreeAction.ExpandSubtree));
        app.ContinuePeopleTreeExpansion();
        Assert.True(app.PeopleTreeExpansion!.IsRunning);
        if (cancel) app.CancelPeopleTreeExpansion();
        var operation = app.PeopleTreeExpansion;
        var feedback = app.PeopleTreeActionFeedback;
        var navigation = app.PeopleNavigation;
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "Retain rejected replacement writing"));
        Assert.False(app.OpenWorkspace("replacement"));
        Assert.Equal(operation, app.PeopleTreeExpansion);
        Assert.Equal(feedback, app.PeopleTreeActionFeedback);
        Assert.Equal(navigation, app.PeopleNavigation);
        app.CancelProfileEdit();

        Assert.True(app.OpenWorkspace("replacement"), app.Status);
        Assert.Null(app.PeopleTreeExpansion);
        Assert.Null(app.PeopleTreeActionFeedback);
        Assert.Empty(app.PeopleNavigation.ExpandedPeople);
        Assert.Empty(app.ContinuePeopleTreeExpansion());
        Assert.Null(app.PeopleTreeExpansion);
    }

    [Fact]
    public void Collapse_other_branches_preserves_the_target_subtree_and_ancestor_path_and_resets_hidden_outside_expansion()
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, """
            {"email":"nested@example.com","displayName":"Nested","managerEmail":"avery@example.com"},
            {"email":"leaf@example.com","displayName":"Leaf","managerEmail":"nested@example.com"},
            {"email":"sibling@example.com","displayName":"Sibling","managerEmail":"morgan@example.com"},
            {"email":"hidden@example.com","displayName":"Hidden","managerEmail":"sibling@example.com"},
            {"email":"hidden-leaf@example.com","displayName":"Hidden leaf","managerEmail":"hidden@example.com"},
            {"email":"other@example.com","displayName":"Other","managerEmail":"alex@example.com"},
            {"email":"other-leaf@example.com","displayName":"Other leaf","managerEmail":"other@example.com"}
            """, 2);
        Assert.True(app.NavigatePerson("hidden-leaf@example.com"));
        app.ShowPeopleOverlay(true);
        app.RememberPeopleBrowsing(["MORGAN@example.com", "nested@example.com", "hidden@example.com",
            "alex@example.com", "other@example.com"], 12, 39, 576.26);
        var before = app.PeopleNavigation;
        var actions = app.GetPeopleTreeActions("AVERY@example.com");

        Assert.Equal([PeopleTreeAction.ExpandSubtree, PeopleTreeAction.CollapseSubtree, PeopleTreeAction.CollapseOtherBranches], actions);
        Assert.True(app.ExecutePeopleTreeAction("AVERY@example.com", PeopleTreeAction.CollapseOtherBranches));

        Assert.Equal(["MORGAN@example.com", "nested@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal(before with { ExpandedPeople = app.PeopleNavigation.ExpandedPeople }, app.PeopleNavigation);
        Assert.Equal([PeopleTreeAction.ExpandSubtree, PeopleTreeAction.CollapseSubtree], app.GetPeopleTreeActions("avery@example.com"));
        app.ChangePeopleQuery("hidden-leaf@example.com");
        app.ChangePeopleQuery("");
        app.ShowPeopleOverlay(false);
        app.ShowPeopleOverlay(true);
        Assert.Equal(["MORGAN@example.com", "nested@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal("hidden-leaf@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.DoesNotContain("avery@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.Equal(["alex@example.com", "morgan@example.com"],
            app.ReportingRoots.Select(node => node.Person.Email));
    }

    [Fact]
    public void Expand_subtree_reveals_all_current_levels_without_expanding_ancestors_siblings_or_archived_people()
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, """
            {"email":"nested@example.com","displayName":"Nested","managerEmail":"avery@example.com"},
            {"email":"leaf@example.com","displayName":"Leaf","managerEmail":"nested@example.com"},
            {"email":"archived@example.com","displayName":"Archived","managerEmail":"avery@example.com","archived":true},
            {"email":"archived-report@example.com","displayName":"Archived report","managerEmail":"archived@example.com","archived":true},
            {"email":"sibling@example.com","displayName":"Sibling","managerEmail":"morgan@example.com"},
            {"email":"sibling-leaf@example.com","displayName":"Sibling leaf","managerEmail":"sibling@example.com"}
            """, 2);
        Assert.True(app.NavigatePerson("alex@example.com"));
        app.ShowPeopleOverlay(true);
        app.RememberPeopleBrowsing(["nested@example.com", "sibling@example.com"], 12, 39, 576.26);
        var before = app.PeopleNavigation;

        Assert.Contains(PeopleTreeAction.ExpandSubtree, app.GetPeopleTreeActions("AVERY@example.com"));
        Assert.True(app.ExecutePeopleTreeAction("avery@example.com", PeopleTreeAction.ExpandSubtree));
        while (app.PeopleTreeExpansion!.IsRunning) app.ContinuePeopleTreeExpansion();

        Assert.Equal(["avery@example.com", "nested@example.com", "sibling@example.com"],
            app.PeopleNavigation.ExpandedPeople.Order(StringComparer.Ordinal));
        Assert.Equal(before with { ExpandedPeople = app.PeopleNavigation.ExpandedPeople }, app.PeopleNavigation);
        Assert.Equal(PeopleTreeExpansionStatus.Completed, app.PeopleTreeExpansion.Status);
        Assert.DoesNotContain(PeopleTreeAction.ExpandSubtree, app.GetPeopleTreeActions("avery@example.com"));
        Assert.Equal([PeopleTreeAction.CollapseOtherBranches], app.GetPeopleTreeActions("leaf@example.com"));
        app.ChangePeopleQuery("leaf");
        app.ChangePeopleQuery("");
        app.ShowPeopleOverlay(false);
        app.ShowPeopleOverlay(true);
        Assert.Equal(["avery@example.com", "nested@example.com", "sibling@example.com"],
            app.PeopleNavigation.ExpandedPeople.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Cancelled_expansion_retains_completed_branches_and_resumes_to_every_deep_and_wide_imported_level()
    {
        using var app = (WorkspaceViewModel)Open();
        var deep = Enumerable.Range(0, 1024).Select(index => JsonSerializer.Serialize(new
        {
            email = $"depth{index:0000}@example.com", displayName = $"Depth {index}",
            managerEmail = index == 0 ? "morgan@example.com" : $"depth{index - 1:0000}@example.com"
        }));
        var wide = Enumerable.Range(0, 2048).Select(index => JsonSerializer.Serialize(new
        {
            email = $"wide{index:0000}@example.com", displayName = $"Wide {index}",
            managerEmail = "depth1023@example.com"
        }));
        Apply(app, string.Join(",", deep.Concat(wide)), 2);
        Assert.True(app.NavigatePerson("alex@example.com"));
        app.RememberPeopleBrowsing(["alex@example.com"], 0);

        Assert.True(app.ExecutePeopleTreeAction("depth0000@example.com", PeopleTreeAction.ExpandSubtree));
        app.ContinuePeopleTreeExpansion();
        Assert.True(app.PeopleTreeExpansion!.IsRunning);
        Assert.InRange(app.PeopleTreeExpansion.VisitedPeople, 1, 1023);
        Assert.Contains("depth0000@example.com", app.PeopleNavigation.ExpandedPeople);
        Assert.DoesNotContain("depth1023@example.com", app.PeopleNavigation.ExpandedPeople);
        var completedBranches = app.PeopleTreeExpansion.ExpandedBranches;
        var partial = app.PeopleNavigation;
        app.CancelPeopleTreeExpansion();

        Assert.Equal(PeopleTreeExpansionStatus.Cancelled, app.PeopleTreeExpansion.Status);
        Assert.Contains("completed expansion retained", app.PeopleTreeActionFeedback);
        Assert.Equal(partial, app.PeopleNavigation);
        app.ContinuePeopleTreeExpansion();
        Assert.Equal(partial, app.PeopleNavigation);
        Assert.Equal(partial.ExpandedPeople, app.PeopleNavigation.ExpandedPeople);
        Assert.Contains(PeopleTreeAction.ExpandSubtree, app.GetPeopleTreeActions("depth0000@example.com"));
        Assert.True(app.ExecutePeopleTreeAction("depth0000@example.com", PeopleTreeAction.ExpandSubtree));
        while (app.PeopleTreeExpansion!.IsRunning) app.ContinuePeopleTreeExpansion();

        Assert.Equal(PeopleTreeExpansionStatus.Completed, app.PeopleTreeExpansion.Status);
        Assert.Equal(3072, app.PeopleTreeExpansion.VisitedPeople);
        Assert.Equal(1024, app.PeopleTreeExpansion.ExpandedBranches + completedBranches);
        Assert.Equal(["alex@example.com", .. Enumerable.Range(0, 1024).Select(index => $"depth{index:0000}@example.com")],
            app.PeopleNavigation.ExpandedPeople);
        Assert.DoesNotContain(PeopleTreeAction.ExpandSubtree, app.GetPeopleTreeActions("depth0000@example.com"));
        Assert.Equal("alex@example.com", app.PeopleNavigation.SelectedEmail);
    }

    [Fact]
    public void Collapse_subtree_resets_hidden_descendant_expansion_without_changing_ancestors_siblings_or_other_roots()
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, """
            {"email":"nested@example.com","displayName":"Nested","managerEmail":"avery@example.com"},
            {"email":"leaf@example.com","displayName":"Leaf","managerEmail":"nested@example.com"},
            {"email":"sibling@example.com","displayName":"Sibling","managerEmail":"morgan@example.com"},
            {"email":"sibling-leaf@example.com","displayName":"Sibling leaf","managerEmail":"sibling@example.com"},
            {"email":"other@example.com","displayName":"Other","managerEmail":"alex@example.com"}
            """, 2);
        Assert.True(app.NavigatePerson("leaf@example.com"));
        app.ShowPeopleOverlay(true);
        app.RememberPeopleBrowsing(["morgan@example.com", "nested@example.com", "sibling@example.com", "alex@example.com"],
            12, 39, 576.26);
        var before = app.PeopleNavigation;

        Assert.Equal([PeopleTreeAction.ExpandSubtree, PeopleTreeAction.CollapseSubtree, PeopleTreeAction.CollapseOtherBranches],
            app.GetPeopleTreeActions("AVERY@example.com"));
        Assert.Equal([PeopleTreeAction.CollapseOtherBranches], app.GetPeopleTreeActions("leaf@example.com"));
        Assert.True(app.ExecutePeopleTreeAction("avery@example.com", PeopleTreeAction.CollapseSubtree));

        Assert.Equal(["morgan@example.com", "sibling@example.com", "alex@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal(before with { ExpandedPeople = app.PeopleNavigation.ExpandedPeople }, app.PeopleNavigation);
        Assert.Equal([PeopleTreeAction.ExpandSubtree, PeopleTreeAction.CollapseOtherBranches], app.GetPeopleTreeActions("avery@example.com"));
        app.ChangePeopleQuery("leaf@example.com");
        app.ChangePeopleQuery("");
        app.ShowPeopleOverlay(false);
        app.ShowPeopleOverlay(true);
        Assert.Equal(["morgan@example.com", "sibling@example.com", "alex@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal("leaf@example.com", app.PeopleNavigation.SelectedEmail);
    }

    [Theory]
    [InlineData(NoteScope.Person, PeopleTreeAction.CollapseSubtree)]
    [InlineData(NoteScope.Team, PeopleTreeAction.CollapseSubtree)]
    [InlineData(NoteScope.Person, PeopleTreeAction.CollapseOtherBranches)]
    [InlineData(NoteScope.Team, PeopleTreeAction.CollapseOtherBranches)]
    [InlineData(NoteScope.Person, PeopleTreeAction.ExpandSubtree)]
    [InlineData(NoteScope.Team, PeopleTreeAction.ExpandSubtree)]
    public async Task Tree_actions_retain_the_open_profile_independent_writing_and_existing_errors(
        NoteScope scope, PeopleTreeAction action)
    {
        using var app = (WorkspaceViewModel)Open();
        var owner = scope == NoteScope.Person ? "avery@example.com" : "morgan@example.com";
        var noteId = scope == NoteScope.Person
            ? app.CreatePersonNote(owner, "Saved title", "Saved writing")!.Id
            : app.CreateTeamNote(owner, "Saved title", "Saved writing")!.Id;
        Assert.True(app.NavigatePerson(owner));
        Assert.True(app.OpenOwnedNote(scope, noteId, editing: true));
        app.UpdateNoteDraft(scope, noteId, "", "  Exact failed owner writing\r\n");
        Assert.False(await app.FlushNoteDraftsAsync());
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "  Independent profile writing\r\n"));
        Assert.False(app.NavigatePerson("unavailable@example.com"));
        var error = app.OperationError;
        var status = app.Status;
        var profile = app.ProfileEdit;
        var retained = app.GetRetainedNoteDraft(scope, noteId);
        var savedPerson = app.GetRelationships("avery@example.com")!.Person;
        app.RememberPeopleBrowsing(action == PeopleTreeAction.ExpandSubtree
            ? ["alex@example.com"] : ["morgan@example.com", "alex@example.com"], 9);
        app.ShowPeopleOverlay(true);

        Assert.True(app.ExecutePeopleTreeAction(action == PeopleTreeAction.CollapseOtherBranches
            ? "alex@example.com" : "morgan@example.com", action));
        while (app.PeopleTreeExpansion is { IsRunning: true }) app.ContinuePeopleTreeExpansion();

        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.True(app.PeopleNavigation.OverlayOpen);
        Assert.Equal(action == PeopleTreeAction.ExpandSubtree ? ["alex@example.com", "morgan@example.com"] :
            ["alex@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Same(profile, app.ProfileEdit);
        Assert.Equal(retained, app.GetRetainedNoteDraft(scope, noteId));
        Assert.Same(retained!.Basis, app.GetRetainedNoteDraft(scope, noteId)!.Basis);
        Assert.Equal(savedPerson, app.GetRelationships("avery@example.com")!.Person);
        Assert.Equal("Saved writing", app.BeginNoteDraft(scope, owner, noteId)!.Markdown);
        Assert.Equal(error, app.OperationError);
        Assert.Equal(status, app.Status);
        Assert.False(app.CanSafelyClose);
    }

    [Theory]
    [InlineData("avery@example.com")]
    [InlineData("missing@example.com")]
    [InlineData("archived@example.com")]
    [InlineData("morgan@example.com")]
    public void Collapse_subtree_omits_no_work_and_unavailable_targets_and_rejects_a_stale_action(string target)
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, """{"email":"archived@example.com","displayName":"Archived","managerEmail":null}""", 2);
        Assert.True(app.EditPersonArchived("archived@example.com", true));
        app.RememberPeopleBrowsing(["alex@example.com", "archived@example.com"], 12);
        var before = app.PeopleNavigation;

        Assert.DoesNotContain(PeopleTreeAction.CollapseSubtree, app.GetPeopleTreeActions(target));
        Assert.False(app.ExecutePeopleTreeAction(target, PeopleTreeAction.CollapseSubtree));

        Assert.Equal(before, app.PeopleNavigation);
        Assert.Contains("not applied", app.PeopleTreeActionFeedback);
    }

    [Fact]
    public void Collapse_subtree_resets_a_deep_imported_graph_without_a_depth_limit()
    {
        using var app = (WorkspaceViewModel)Open();
        var records = Enumerable.Range(0, 1024).Select(index => JsonSerializer.Serialize(new
        {
            email = $"depth{index:0000}@example.com",
            displayName = $"Depth {index}",
            managerEmail = index == 0 ? "morgan@example.com" : $"depth{index - 1:0000}@example.com"
        }));
        Apply(app, string.Join(",", records), 2);
        Assert.True(app.NavigatePerson("depth1023@example.com"));
        app.RememberPeopleBrowsing(["morgan@example.com", "alex@example.com",
            .. Enumerable.Range(0, 1023).Select(index => $"depth{index:0000}@example.com")], 0);

        Assert.True(app.ExecutePeopleTreeAction("depth0256@example.com", PeopleTreeAction.CollapseSubtree));

        Assert.Equal(["morgan@example.com", "alex@example.com",
            .. Enumerable.Range(0, 256).Select(index => $"depth{index:0000}@example.com")], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal("depth1023@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.DoesNotContain(PeopleTreeAction.CollapseSubtree, app.GetPeopleTreeActions("depth0256@example.com"));
    }

    [Fact]
    public void Collapse_other_branches_applies_to_a_leaf_without_expanding_it_and_omits_the_resulting_no_op()
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, """
            {"email":"nested@example.com","displayName":"Nested","managerEmail":"avery@example.com"},
            {"email":"leaf@example.com","displayName":"Leaf","managerEmail":"nested@example.com"},
            {"email":"outside@example.com","displayName":"Outside","managerEmail":"alex@example.com"},
            {"email":"outside-leaf@example.com","displayName":"Outside leaf","managerEmail":"outside@example.com"}
            """, 2);
        app.RememberPeopleBrowsing(["morgan@example.com", "avery@example.com", "nested@example.com",
            "outside@example.com"], 9);
        Assert.Equal([PeopleTreeAction.CollapseOtherBranches], app.GetPeopleTreeActions("LEAF@example.com"));

        Assert.True(app.ExecutePeopleTreeAction("LEAF@example.com", PeopleTreeAction.CollapseOtherBranches));

        Assert.Equal(["morgan@example.com", "avery@example.com", "nested@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Empty(app.GetPeopleTreeActions("leaf@example.com"));
        var after = app.PeopleNavigation;
        Assert.False(app.ExecutePeopleTreeAction("leaf@example.com", PeopleTreeAction.CollapseOtherBranches));
        Assert.Equal(after, app.PeopleNavigation);
        Assert.Contains("not applied", app.PeopleTreeActionFeedback);
        app.RememberPeopleBrowsing([.. after.ExpandedPeople, "alex@example.com"], 9);
        Assert.DoesNotContain("outside@example.com", app.PeopleNavigation.ExpandedPeople);
    }

    [Theory]
    [InlineData("missing@example.com")]
    [InlineData("archived@example.com")]
    [InlineData("avery@example.com")]
    [InlineData("morgan@example.com")]
    public void Collapse_other_branches_omits_unavailable_targets_and_noncurrent_or_protected_expansion(string target)
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, """{"email":"archived@example.com","displayName":"Archived","managerEmail":null}""", 2);
        Assert.True(app.EditPersonArchived("archived@example.com", true));
        app.RememberPeopleBrowsing(["MORGAN@example.com", "avery@example.com", "archived@example.com", "missing@example.com"], 12);
        var before = app.PeopleNavigation;

        Assert.DoesNotContain(PeopleTreeAction.CollapseOtherBranches, app.GetPeopleTreeActions(target));
        Assert.False(app.ExecutePeopleTreeAction(target, PeopleTreeAction.CollapseOtherBranches));

        Assert.Equal(before, app.PeopleNavigation);
        Assert.Contains("not applied", app.PeopleTreeActionFeedback);
    }

    [Theory]
    [InlineData("avery@example.com")]
    [InlineData("missing@example.com")]
    [InlineData("archived@example.com")]
    public void Expand_subtree_rejects_leaf_unavailable_and_already_completed_targets(string target)
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, """{"email":"archived@example.com","displayName":"Archived","managerEmail":null,"archived":true}""", 2);
        app.RememberPeopleBrowsing(["morgan@example.com", "alex@example.com"], 12);
        var before = app.PeopleNavigation;

        Assert.DoesNotContain(PeopleTreeAction.ExpandSubtree, app.GetPeopleTreeActions(target));
        Assert.False(app.ExecutePeopleTreeAction(target, PeopleTreeAction.ExpandSubtree));
        Assert.False(app.ExecutePeopleTreeAction("morgan@example.com", PeopleTreeAction.ExpandSubtree));
        Assert.Equal(before, app.PeopleNavigation);
        Assert.Contains("not applied", app.PeopleTreeActionFeedback);
    }

    [Fact]
    public void Collapse_other_branches_resets_deep_hidden_outside_expansion_without_truncating_the_protected_path()
    {
        using var app = (WorkspaceViewModel)Open();
        var records = Enumerable.Range(0, 1024).SelectMany(index => new[]
        {
            JsonSerializer.Serialize(new
            {
                email = $"depth{index:0000}@example.com", displayName = $"Depth {index}",
                managerEmail = index == 0 ? "morgan@example.com" : $"depth{index - 1:0000}@example.com"
            }),
            JsonSerializer.Serialize(new
            {
                email = $"outside{index:0000}@example.com", displayName = $"Outside {index}",
                managerEmail = index == 0 ? "alex@example.com" : $"outside{index - 1:0000}@example.com"
            })
        });
        Apply(app, string.Join(",", records), 2);
        string[] protectedPeople = ["morgan@example.com",
            .. Enumerable.Range(0, 1023).Select(index => $"depth{index:0000}@example.com")];
        app.RememberPeopleBrowsing([.. protectedPeople,
            .. Enumerable.Range(0, 1023).Select(index => $"outside{index:0000}@example.com")], 0);

        Assert.True(app.ExecutePeopleTreeAction("depth1023@example.com", PeopleTreeAction.CollapseOtherBranches));

        Assert.Equal(protectedPeople, app.PeopleNavigation.ExpandedPeople);
        Assert.Empty(app.GetPeopleTreeActions("depth1023@example.com"));
    }

    [Fact]
    public void Expansion_refuses_conflicting_tree_actions_and_stops_when_the_current_imported_graph_changes()
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, string.Join(",", Enumerable.Range(0, 200).Select(index => JsonSerializer.Serialize(new
        {
            email = $"depth{index:0000}@example.com", displayName = $"Depth {index}",
            managerEmail = index == 0 ? "morgan@example.com" : $"depth{index - 1:0000}@example.com"
        }))), 2);
        Assert.True(app.ExecutePeopleTreeAction("morgan@example.com", PeopleTreeAction.ExpandSubtree));
        app.ContinuePeopleTreeExpansion();
        var partial = app.PeopleNavigation;
        Assert.Empty(app.GetPeopleTreeActions("morgan@example.com"));
        Assert.False(app.ExecutePeopleTreeAction("morgan@example.com", PeopleTreeAction.CollapseSubtree));
        Assert.Empty(app.GetPeopleTreeActions("alex@example.com"));
        Assert.False(app.ExecutePeopleTreeAction("alex@example.com", PeopleTreeAction.CollapseOtherBranches));
        Assert.True(app.PeopleTreeExpansion!.IsRunning);
        Assert.Equal(partial, app.PeopleNavigation);
        Apply(app, """{"email":"depth0199@example.com","archived":true}""", 3);

        app.ContinuePeopleTreeExpansion();

        Assert.Equal(PeopleTreeExpansionStatus.Interrupted, app.PeopleTreeExpansion!.Status);
        Assert.Equal(partial, app.PeopleNavigation);
        Assert.Contains("reporting view changed", app.PeopleTreeActionFeedback);
        Assert.Contains(PeopleTreeAction.ExpandSubtree, app.GetPeopleTreeActions("morgan@example.com"));
    }

    [Theory]
    [InlineData(NoteScope.Person, "avery@example.com")]
    [InlineData(NoteScope.Person, "morgan@example.com")]
    [InlineData(NoteScope.Team, "avery@example.com")]
    [InlineData(NoteScope.Team, "morgan@example.com")]
    public void Grouped_search_retains_the_exact_unsaved_secondary_owner_note_instead_of_opening_another_document(
        NoteScope scope, string startingPerson)
    {
        using var app = (WorkspaceViewModel)Open();
        var ongoing = scope == NoteScope.Person
            ? app.CreatePersonNote("avery@example.com", "Continuing", "Ordinary saved writing")!.Id
            : app.CreateTeamNote("avery@example.com", "Continuing", "Ordinary saved writing")!.Id;
        var secondary = scope == NoteScope.Person
            ? app.CreatePersonNote("avery@example.com", "Secondary", "Original secondary writing")!.Id
            : app.CreateTeamNote("avery@example.com", "Secondary", "Original secondary writing")!.Id;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.DesignateOngoingNote(scope, ongoing));
        Assert.True(app.OpenOwnedNote(scope, secondary, editing: true));
        app.UpdateNoteDraft(scope, secondary, "", "  Exact retained owner writing\r\n");
        var retained = app.GetRetainedNoteDraft(scope, secondary)!;
        Assert.True(app.NavigatePerson(startingPerson));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("avery@example.com");
        var result = Assert.Single(app.SearchPeople("avery@example.com"), row => row.Person.Email == "avery@example.com");

        Assert.True(app.NavigateSearchResult(result), app.Status);

        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.Equal(secondary, app.GetOwnedNotesView(scope).SelectedNote!.Id);
        Assert.True(app.GetOwnedNotesView(scope).IsEditing);
        Assert.Equal("", app.GetOwnedNotesView(scope).SelectedNote!.Title);
        Assert.Equal("  Exact retained owner writing\r\n", app.GetOwnedNotesView(scope).SelectedNote!.Markdown);
        Assert.Same(retained.Basis, app.GetRetainedNoteDraft(scope, secondary)!.Basis);
        Assert.False(app.CanSafelyClose);
        Assert.Equal("Original secondary writing",
            app.BeginNoteDraft(scope, "avery@example.com", secondary)!.Markdown);
    }

    [Fact]
    public void Complete_relevance_keeps_exact_historical_identity_reporting_and_frozen_notes_below_current_context()
    {
        using var app = Open();
        Apply(app, """
            {"email":"exact@example.com","displayName":"Z exact","alias":"Cedar","managerEmail":null},
            {"email":"word@example.com","displayName":"Y Cedar name","managerEmail":null},
            {"email":"prefix@example.com","displayName":"X Cedargrove","managerEmail":null},
            {"email":"substring@example.com","displayName":"W JayCedar","managerEmail":null},
            {"email":"owned@example.com","displayName":"V owned","department":"Cedar current department","managerEmail":"exact@example.com"},
            {"email":"inherited@example.com","displayName":"U inherited","managerEmail":"exact@example.com"},
            {"email":"promoted@example.com","displayName":"T inherited","preferredName":"Cedar","managerEmail":"exact@example.com"},
            {"email":"history@example.com","displayName":"A history","preferredName":"Cedar","alias":"Cedar","managerEmail":"exact@example.com"}
            """, 2);
        var note = app.CreateTeamNote("exact@example.com", "Cedar", "Cedar")!;
        Assert.True(app.EditPreferredName("promoted@example.com", "T inherited"));
        Assert.True(app.EditPreferredName("history@example.com", "A history"));
        Assert.True(app.EditPersonText("history@example.com", PersonTextField.Alias, "Past"));
        Assert.True(app.EditPersonManager("history@example.com", "morgan@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("history@example.com"));
        Assert.True(app.EditTeamNote(note.Id, "Current", "Cedar current inherited writing"));
        Assert.True(app.EditPersonArchived("history@example.com", true));

        var people = app.SearchPeople("cedar");

        Assert.Equal(["exact@example.com", "word@example.com", "prefix@example.com", "substring@example.com",
            "owned@example.com", "promoted@example.com", "inherited@example.com", "history@example.com"],
            people.Select(result => result.Person.Email));
        var inherited = people[5];
        Assert.Equal(SearchSource.InheritedTeamNote, inherited.Matches[0].Source);
        Assert.Equal("Cedar current inherited writing", inherited.Excerpt);
        Assert.Contains(inherited.Matches, match => match.Source == SearchSource.ProfileHistory && match.MatchText == "Cedar");
        var history = people[7];
        Assert.True(history.Person.Archived);
        Assert.All(history.Matches, match => Assert.Equal(SearchContext.Historical, match.Context));
        Assert.Contains(history.Matches, match => match.Source == SearchSource.ProfileHistory &&
            match.Field == "preferredName" && match.MatchText == "Cedar");
        Assert.Contains(history.Matches, match => match.Source == SearchSource.ProfileHistory &&
            match.Field == "alias" && match.MatchText == "Cedar");
        Assert.Contains(history.Matches, match => match.Source == SearchSource.FormerReportingChain &&
            match.Field == "alias" && match.MatchText == "Cedar" && match.NoteOwner!.Email == "exact@example.com");
        Assert.Contains(history.Matches, match => match.Source == SearchSource.HistoricalTeamNote &&
            match.HistoricalContextId == capture.Id && match.NoteId == note.Id && match.MatchText == "Cedar" &&
            match.NoteOwner!.Email == "exact@example.com");
        Assert.Contains("Historical / Archived person", history.EvidenceDescription);
        var evidence = app.Search("cedar");
        Assert.All(people, result => Assert.Equal(evidence.Where(match => match.Person.Email == result.Person.Email).ToHashSet(),
            result.Matches.ToHashSet()));
        Assert.Equal(evidence.Where(match => match.Person.Email == "history@example.com"), history.Matches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owned_current_context_outranks_Manager_and_inherited_mentions_with_stable_primary_evidence(bool archived)
    {
        using var app = Open();
        Apply(app, """
            {"email":"cedar-manager@example.com","displayName":"Z manager","managerEmail":null},
            {"email":"report@example.com","displayName":"A report","managerEmail":"cedar-manager@example.com","isFavorite":true},
            {"email":"b-owned@example.com","displayName":"Z owner","managerEmail":"cedar-manager@example.com"},
            {"email":"z-owned@example.com","displayName":"Z owner","department":"Cedar direct department","managerEmail":"cedar-manager@example.com"}
            """, 2);
        for (var index = 0; index < 3; index++)
            app.CreateTeamNote("cedar-manager@example.com", $"Cedar shared {index}", "Cedar repeated context");
        var personNote = app.CreatePersonNote("b-owned@example.com", "Cedar", "Private title-only match")!;
        var teamNote = app.CreateTeamNote("b-owned@example.com", "Owned", "Cedar")!;
        if (archived) Assert.True(app.EditPersonArchived("b-owned@example.com", true));

        var people = app.SearchPeople("  CeDaR \t");

        Assert.Equal(["cedar-manager@example.com", "b-owned@example.com", "z-owned@example.com", "report@example.com"],
            people.Select(result => result.Person.Email));
        var owner = people[1];
        Assert.Equal(archived, owner.Person.Archived);
        Assert.Equal([personNote.Id, teamNote.Id], owner.Matches.Take(2).Select(match => match.NoteId));
        Assert.Equal([SearchSource.PersonNote, SearchSource.OwnedTeamNote], owner.Matches.Take(2).Select(match => match.Source));
        Assert.Equal("Private title-only match", owner.Excerpt);
        Assert.StartsWith(archived ? "Archived person — Person note" : "Current — Person note", owner.SourceLabel);
        Assert.Contains(owner.Matches, match => match.Source == SearchSource.Profile && match.Field == "managerEmail");
        Assert.All(owner.Matches.Where(match => match.NoteId == personNote.Id || match.NoteId == teamNote.Id),
            match => Assert.Equal("b-owned@example.com", match.NoteOwner!.Email));
        Assert.Equal("department", people[2].Matches[0].Field);
        Assert.Equal("Cedar direct department", people[2].Excerpt);
        Assert.Equal("managerEmail", people[3].Matches[0].Field);
        Assert.Equal(3, people[3].Matches.Count(match => match.Source == SearchSource.InheritedTeamNote));
        Assert.Contains("owned by Z manager (cedar-manager@example.com)", people[3].EvidenceDescription);
        var evidence = app.Search("  CeDaR \t");
        Assert.All(people, result => Assert.Equal(evidence.Where(match => match.Person.Email == result.Person.Email).ToHashSet(),
            result.Matches.ToHashSet()));
    }

    [Theory]
    [InlineData("sachin", "sachin-target@example.com", "\"displayName\":\"Sachin\"", "displayName", false)]
    [InlineData("sachin", "sachin-target@example.com", "\"displayName\":\"Z Sachin context\",\"preferredName\":\"Sachin\"", "preferredName", true)]
    [InlineData("sachin", "sachin-target@example.com", "\"displayName\":\"Z Sachin context\",\"alias\":\"Sachin\"", "alias", false)]
    [InlineData("sachin@example.com", "sachin@example.com", "\"displayName\":\"Z target\"", "email", true)]
    public void Complete_own_identity_leads_partial_identity_and_repeated_context_with_its_strongest_evidence(
        string query, string email, string identity, string primaryField, bool archived)
    {
        using var app = Open();
        Apply(app, $$"""
            {"email":"context@example.com","displayName":"A context","department":"{{query}}","isFavorite":true,"managerEmail":null},
            {"email":"partial@example.com","displayName":"B {{query}} completion","managerEmail":null},
            {"email":"{{email}}",{{identity}},"managerEmail":null}
            """, 2);
        app.CreatePersonNote("context@example.com", "First", query);
        app.CreatePersonNote("context@example.com", "Second", query);
        if (archived) Assert.True(app.EditPersonArchived(email, true));

        var people = app.SearchPeople($"  {query.ToUpperInvariant()} \t");

        Assert.Equal([email, "partial@example.com", "context@example.com"], people.Select(r => r.Person.Email));
        Assert.Equal(primaryField, people[0].Matches[0].Field);
        Assert.Equal(SearchSource.Profile, people[0].Matches[0].Source);
        Assert.Equal(archived ? SearchContext.Archived : SearchContext.Current, people[0].Matches[0].Context);
        Assert.Equal(query, people[0].Excerpt, ignoreCase: true);
        Assert.Equal(app.Search(query).Where(m => m.Person.Email == email).ToHashSet(), people[0].Matches.ToHashSet());
        Assert.Contains("context@example.com", people.Select(r => r.Person.Email));
    }

    [Theory]
    [InlineData("sachin", "Sachin Singh", "Sachindra Singh", "JaySachin")]
    [InlineData("sachin", "Sachin-Singh", "Sachindra-Singh", "JaySachin")]
    [InlineData("sachin", "(Sachin)", "(Sachindra)", "JaySachin")]
    [InlineData("sachin", "Before\tSachin\nAfter", "Before\tSachindra", "JaySachin")]
    [InlineData("sachin", "JaySachin Sachindra Sachin", "JaySachin Sachindra", "JaySachin")]
    [InlineData("sachin sri", "Before Sachin Sri After", "Before Sachin Srivastava", "JaySachin Sri")]
    public void Literal_name_words_and_phrases_outrank_prefixes_then_incidental_identity(
        string query, string wholeName, string prefixName, string incidentalName)
    {
        using var app = Open();
        Apply(app, $$"""
            {"email":"whole@example.com","displayName":{{JsonSerializer.Serialize("Z " + wholeName)}},"managerEmail":null},
            {"email":"prefix@example.com","displayName":{{JsonSerializer.Serialize("Y " + prefixName)}},"managerEmail":null},
            {"email":"substring@example.com","displayName":"B {{incidentalName}}","managerEmail":null},
            {"email":"identifier@example.com","displayName":"A identifier","alias":"{{query}}.handle","managerEmail":null},
            {"email":"context@example.com","displayName":"A context","department":"{{query}}","managerEmail":null}
            """, 2);

        var people = app.SearchPeople(query);

        Assert.Equal(["whole@example.com", "prefix@example.com", "identifier@example.com",
            "substring@example.com", "context@example.com"], people.Select(r => r.Person.Email));
        Assert.All(people.Take(2), result => Assert.Equal("displayName", result.Matches[0].Field));
        Assert.Equal(app.Search(query).Select(m => m.Person.Email).ToHashSet(),
            people.Select(r => r.Person.Email).ToHashSet());
    }

    [Fact]
    public void Sachin_lookup_publishes_names_before_Kiran_email_and_retains_Manager_and_historical_matches()
    {
        using var app = (WorkspaceViewModel)Open();
        Apply(app, """
            {"email":"report@example.com","displayName":"Aaron report","managerEmail":"sachin-manager@example.com","isFavorite":true},
            {"email":"history@example.com","displayName":"Aaron history","preferredName":"Sachin","managerEmail":null},
            {"email":"kiran.sachintala@example.com","displayName":"Kiran","managerEmail":null},
            {"email":"sachin-manager@example.com","displayName":"Sachin Jones","managerEmail":null},
            {"email":"sam@example.com","displayName":"Sachin Kumar Srivastava","preferredName":"Sam","managerEmail":null},
            {"email":"prefix@example.com","displayName":"Sachindra Singh","managerEmail":null}
            """, 2);
        Assert.True(app.EditPreferredName("history@example.com", "Aaron history"));
        Assert.True(app.EditPersonArchived("sam@example.com", true));
        var secondary = app.CreatePersonNote("kiran.sachintala@example.com", "Sachin secondary", "Retained writing")!;
        app.CreatePersonNote("kiran.sachintala@example.com", "Other", "Sachin repeated context");
        Assert.True(app.NavigatePerson("alex@example.com"));
        app.ShowPeopleOverlay(true);

        var request = app.BeginPeopleSearch("  SaChIn \t");
        var results = app.SearchPeople(app.PeopleNavigation.Query);
        Assert.Equal(["sachin-manager@example.com", "sam@example.com", "prefix@example.com",
            "kiran.sachintala@example.com", "report@example.com", "history@example.com"],
            results.Select(r => r.Person.Email));
        Assert.True(app.PublishPeopleSearchResults(request, results));
        Assert.Equal("sachin-manager@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        Assert.Equal("alex@example.com", app.PeopleNavigation.SelectedEmail);
        var kiran = results[3];
        Assert.Equal("email", kiran.Matches[0].Field);
        Assert.Contains(kiran.Matches, match => match.NoteId == secondary.Id &&
            match.NoteOwner!.Email == "kiran.sachintala@example.com" && match.Excerpt == "Retained writing");
        Assert.Equal("managerEmail", Assert.Single(results[4].Matches).Field);
        Assert.All(results[5].Matches, match => Assert.Equal(SearchContext.Historical, match.Context));
        Assert.Empty(app.SearchPeople("Sachin Sri"));

        var saved = app.GetRelationships("alex@example.com")!.Person;
        Assert.True(app.BeginProfileEdit("alex@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "  Exact unsaved writing\r\n"));
        app.MoveSearchHighlight(1);
        Assert.Equal("sam@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        var edit = app.ProfileEdit;
        Assert.True(app.NavigateSearchResult(app.PeopleNavigation.HighlightedResult!), app.Status);
        Assert.Equal("sam@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.True(app.PeopleNavigation.ArchivedPeopleVisible);
        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.False(app.CanSafelyClose);
        Assert.True(app.NavigatePerson("alex@example.com"), app.Status);
        Assert.Same(edit, app.ProfileEdit);
        Assert.Equal("  Exact unsaved writing\r\n", app.ProfileEdit!.Draft.OfficeNumber);
        Assert.Same(edit!.Basis, app.ProfileEdit.Basis);
        Assert.Equal(saved, app.GetRelationships("alex@example.com")!.Person);
        app.CancelProfileEdit();

        app.ChangePeopleQuery("sachin");
        Assert.Equal("sachin-manager@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        Assert.True(app.NavigateSearchResult(app.PeopleNavigation.HighlightedResult), app.Status);
        Assert.Equal("sachin-manager@example.com", app.PeopleNavigation.SelectedEmail);
    }

    [Fact]
    public void Beginning_query_input_defers_matching_and_highlights_only_the_published_results()
    {
        using var app = (WorkspaceViewModel)Open();
        var markdown = string.Concat(Enumerable.Repeat("Fictitious shared context \t \n", 4096)) + "lastneedle";
        app.CreateTeamNote("morgan@example.com", "Shared context", markdown);
        app.SearchPeople("lastneedle");
        Assert.Equal("alex@example.com", app.PeopleNavigation.SelectedEmail);

        var before = GC.GetAllocatedBytesForCurrentThread();
        long request = 0;
        for (var index = 0; index < 20; index++)
            request = app.BeginPeopleSearch(index % 2 == 0 ? "lastneedl" : "lastneedle");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 2 * 1024 * 1024,
            $"Query input must defer matching and excerpts to the background search; allocated {allocated:N0} bytes.");
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.Equal("alex@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.True(app.PublishPeopleSearchResults(request, app.SearchPeople("lastneedle")));
        Assert.Equal("morgan@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        Assert.Equal(SearchSource.OwnedTeamNote, app.PeopleNavigation.HighlightedResult.Matches[0].Source);
        Assert.Equal("alex@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.True(app.NavigateSearchResult(app.PeopleNavigation.HighlightedResult), app.Status);
        Assert.Equal("morgan@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.Null(app.PeopleNavigation.SelectedResult);
    }

    [Fact]
    public void Search_publication_rejects_an_old_query_and_preserves_a_newer_highlight()
    {
        using var app = (WorkspaceViewModel)Open();
        var oldRequest = app.BeginPeopleSearch("example.com");
        var oldResults = app.SearchPeople("example.com");
        var request = app.BeginPeopleSearch("Scientist");

        Assert.False(app.PublishPeopleSearchResults(oldRequest, oldResults));
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.True(app.PublishPeopleSearchResults(request, app.SearchPeople("Scientist")));
        Assert.Equal("avery@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        Assert.False(app.PublishPeopleSearchResults(oldRequest, oldResults));
        Assert.Equal("avery@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
    }

    [Fact]
    public void Same_query_refresh_and_workspace_reopen_invalidate_older_search_publication()
    {
        using var app = (WorkspaceViewModel)Open();
        var oldRequest = app.BeginPeopleSearch("example.com");
        var oldResults = app.SearchPeople("example.com");
        Apply(app, """{"email":"aa-new@example.com","displayName":"New Person","managerEmail":null}""", 2);
        var request = app.BeginPeopleSearch("example.com");

        Assert.False(app.PublishPeopleSearchResults(oldRequest, oldResults));
        var results = app.SearchPeople("example.com");
        Assert.Equal(4, results.Count);
        Assert.True(app.PublishPeopleSearchResults(request, results));
        for (var index = 0; index < results.Count; index++) app.MoveSearchHighlight(1);
        Assert.Equal("aa-new@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        Assert.True(app.OpenWorkspace("another-fictitious-search"));
        Assert.False(app.PublishPeopleSearchResults(request, results));
        Assert.Null(app.PeopleNavigation.HighlightedResult);
    }

    [Fact]
    public void Moving_search_highlight_reuses_matches_instead_of_rebuilding_long_note_excerpts()
    {
        using var app = (WorkspaceViewModel)Open();
        var markdown = string.Concat(Enumerable.Repeat("Fictitious shared context \t \n", 4096)) + "lastneedle";
        app.CreateTeamNote("morgan@example.com", "Shared context", markdown);
        app.ChangePeopleQuery("lastneedle");
        Assert.Equal("morgan@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.MoveSearchHighlight(1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 20; index++) app.MoveSearchHighlight(index % 2 == 0 ? -1 : 1);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 2 * 1024 * 1024,
            $"Highlighting existing matches must not rebuild excerpts; allocated {allocated:N0} bytes.");
        Assert.Equal("avery@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
    }

    [Fact]
    public void Accepted_Archived_person_reveals_retained_browsing_without_reactivation_and_empty_Enter_cannot_reuse_them()
    {
        using var app = (WorkspaceViewModel)Open();
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        Assert.True(app.NavigatePerson("alex@example.com"));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("007B");
        var result = app.PeopleNavigation.HighlightedResult!;

        Assert.True(app.NavigateSearchResult(result), app.Status);

        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.True(app.PeopleNavigation.ArchivedPeopleVisible);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.True(Assert.Single(app.ArchivedPeople).Archived);
        Assert.DoesNotContain(app.ReportingRoots.SelectMany(root => root.Reports), p => p.Person.Email == "avery@example.com");
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("unmatched-query");
        var navigation = app.PeopleNavigation;
        Assert.Null(navigation.HighlightedResult);
        Assert.False(app.NavigateSearchResult(result));
        Assert.Equal(navigation, app.PeopleNavigation);
    }

    [Fact]
    public void Accepted_current_Person_clears_search_and_expands_the_chosen_path_instead_of_restoring_the_old_selection()
    {
        using var app = (WorkspaceViewModel)Open();
        Assert.True(app.EditFavorite("alex@example.com", true));
        Assert.True(app.NavigatePerson("alex@example.com"));
        app.RememberPeopleBrowsing(["alex@example.com"], 12, 39, 576.26);
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("007B");

        Assert.True(app.NavigateSearchResult(app.PeopleNavigation.HighlightedResult!), app.Status);

        Assert.Equal("", app.PeopleNavigation.Query);
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(["alex@example.com", "morgan@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.False(app.PeopleNavigation.OverlayOpen);
        Assert.Contains(app.FavoritePeople, p => p.Email == "alex@example.com");
    }

    [Fact]
    public void Moving_the_highlight_clamps_at_both_ends_and_typing_resets_it_without_leaving_an_unsaved_profile()
    {
        using var app = (WorkspaceViewModel)Open();
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "  Exact unsaved writing\r\n"));
        var edit = app.ProfileEdit;
        app.ChangePeopleQuery("example.com");
        app.MoveSearchHighlight(-1);
        Assert.Equal("alex@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.MoveSearchHighlight(1);
        Assert.Equal("avery@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.MoveSearchHighlight(1);
        app.MoveSearchHighlight(1);
        Assert.Equal("morgan@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.ChangePeopleQuery("example.co");
        Assert.Equal("alex@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.ChangePeopleQuery("unmatched-query");
        app.MoveSearchHighlight(1);
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Same(edit, app.ProfileEdit);
        Assert.Null(app.PeopleNavigation.SelectedResult);
    }

    [Fact]
    public void Query_changes_highlight_the_first_Person_without_replacing_exact_reading_and_empty_results_clear_the_highlight()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Exact secondary", "Retained reading")!;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id));
        Assert.Null(app.PeopleNavigation.HighlightedResult);

        app.ChangePeopleQuery("example.com");
        Assert.Equal("alex@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        app.ChangePeopleQuery("Scientist");
        Assert.Equal("avery@example.com", app.PeopleNavigation.HighlightedResult!.Person.Email);
        app.ChangePeopleQuery("unmatched-query");
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.False(app.PeopleNavigation.IsBrowsing);
        app.ChangePeopleQuery("");
        Assert.Null(app.PeopleNavigation.HighlightedResult);
        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
    }

    [Fact]
    public void People_search_keeps_equal_names_separate_with_heading_then_email_ties_and_stable_matching_evidence()
    {
        using var app = Open();
        Apply(app, """
            {"email":"z-same@example.com","displayName":"Avery Directory","preferredName":"Avery Other","alias":"avery-handle","managerEmail":null},
            {"email":"b-same@example.com","displayName":"Avery Directory","preferredName":"Avery Other","alias":"avery-handle","managerEmail":null}
            """, 2);
        Assert.True(app.EditPersonArchived("b-same@example.com", true));

        var people = app.SearchPeople("avery");

        Assert.Equal(["avery@example.com", "b-same@example.com", "z-same@example.com"], people.Select(r => r.Person.Email));
        Assert.Equal(["displayName", "email", "linkedInProfileUrl"],
            people[0].Matches.Select(r => r.Field));
        Assert.All(people.Skip(1), result =>
            Assert.Equal(["displayName", "preferredName", "alias"], result.Matches.Select(r => r.Field)));
        Assert.Equal(people[0].Person.DirectoryName, people[1].Person.DirectoryName);
        Assert.Contains("avery@example.com", people[0].Label);
        Assert.Contains("Current", people[0].SourceLabel);
        Assert.Contains("Directory name", people[0].SourceLabel);
        Assert.Empty(app.SearchPeople(" \t"));
    }

    [Fact]
    public void People_results_retain_every_source_and_its_current_archived_or_frozen_attribution()
    {
        using var app = Open();
        Assert.True(app.EditPreferredName("morgan@example.com", "Cedar manager"));
        var frozenNote = app.CreateTeamNote("morgan@example.com", "Original", "Cedar frozen writing")!;
        var secondary = app.CreatePersonNote("avery@example.com", "Cedar secondary", "Private writing")!;
        var archivedNote = app.CreatePersonNote("avery@example.com", "Retained", "Cedar archived writing")!;
        Assert.True(app.ArchivePersonNote(archivedNote.Id));
        app.CreateTeamNote("avery@example.com", "Owned", "Cedar owned writing");
        app.CreateTeamNote("alex@example.com", "Inherited", "Cedar current inherited writing");
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Cedar old office"));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Willow"));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.Department, "Cedar current department"));
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        Assert.True(app.EditTeamNote(frozenNote.Id, "Later", "Willow later writing"));
        Assert.True(app.EditPersonArchived("morgan@example.com", true));

        var people = app.SearchPeople("cedar");
        Assert.Equal(["morgan@example.com", "alex@example.com", "avery@example.com"], people.Select(r => r.Person.Email));
        var person = people[2];
        Assert.Equal(Enum.GetValues<SearchSource>().Order(), person.Matches.Select(m => m.Source).Distinct().Order());
        Assert.Equal([SearchContext.Current, SearchContext.Archived, SearchContext.Historical],
            person.Matches.Select(m => m.Context).Distinct());
        Assert.Contains(person.Matches, m => m.NoteId == secondary.Id && m.NoteOwner!.Email == "avery@example.com");
        Assert.Contains(person.Matches, m => m.NoteId == frozenNote.Id && m.Context == SearchContext.Historical &&
            m.NoteOwner!.Heading == "Cedar manager" && m.MatchText == "Cedar frozen writing");
        Assert.Contains("Archived note", person.EvidenceDescription);
        Assert.Contains("Current — Inherited Team note", person.EvidenceDescription);
        Assert.Contains("Historical — Frozen Team note", person.EvidenceDescription);
        Assert.Contains("Historical — Former Reporting chain", person.EvidenceDescription);
        Assert.Contains("Historical — Profile history", person.EvidenceDescription);
        Assert.Contains("Archived person", people[0].EvidenceDescription);
        Assert.Contains("Archived note", person.SourceLabel);
        Assert.Contains("Current — Inherited Team note", person.SourceLabel);
        Assert.Contains("Historical — Frozen Team note", person.SourceLabel);
        Assert.Contains("Historical — Former Reporting chain", person.SourceLabel);
    }

    [Theory]
    [InlineData("avery@example.com")]
    [InlineData("morgan@example.com")]
    public void Grouped_secondary_note_activation_opens_the_normal_profile_and_keeps_other_notes_available(string startingPerson)
    {
        using var app = (WorkspaceViewModel)Open();
        var ongoing = app.CreatePersonNote("avery@example.com", "Continuing", "Ordinary writing")!;
        var secondary = app.CreatePersonNote("avery@example.com", "Orchid secondary", "Orchid private writing")!;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, ongoing.Id));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id, editing: true));
        Assert.True(app.NavigatePerson(startingPerson));
        app.ChangePeopleQuery("Orchid");
        var result = Assert.Single(app.SearchPeople("Orchid"));
        Assert.Equal(startingPerson, app.PeopleNavigation.SelectedEmail);

        Assert.True(app.NavigateSearchResult(result), app.Status);

        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.Equal(ongoing.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.False(app.PersonNotesView.OtherNotesOpen);
        app.ShowOtherNotes(NoteScope.Person, true);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id));
        Assert.Equal("Orchid private writing", app.PersonNotesView.SelectedNote!.Markdown);
    }

    [Theory]
    [InlineData("avery@example.com")]
    [InlineData("alex@example.com")]
    public void Grouped_search_browsing_retains_the_exact_unsaved_profile_and_base_on_its_owner(string target)
    {
        using var app = (WorkspaceViewModel)Open();
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.BeginProfileEdit("avery@example.com"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "  Exact unsaved writing\r\n"));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery(target);
        var edit = app.ProfileEdit;
        var saved = app.GetRelationships("avery@example.com")!.Person;
        var result = Assert.Single(app.SearchPeople(target));

        Assert.Same(edit, app.ProfileEdit);
        Assert.True(app.NavigateSearchResult(result), app.Status);
        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.Equal(target, app.PeopleNavigation.SelectedEmail);
        Assert.False(app.PeopleNavigation.OverlayOpen);
        Assert.True(app.NavigatePerson("avery@example.com"), app.Status);
        Assert.Same(edit, app.ProfileEdit);
        Assert.Equal("  Exact unsaved writing\r\n", app.ProfileEdit!.Draft.OfficeNumber);
        Assert.Same(edit!.Basis, app.ProfileEdit.Basis);
        Assert.Equal(saved, app.GetRelationships("avery@example.com")!.Person);
        Assert.False(app.CanSafelyClose);
        app.CancelProfileEdit();
    }

    [Fact]
    public void Explicit_highlighting_and_stale_result_rejection_preserve_the_exact_note_draft()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Exact secondary", "Saved")!;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id, editing: true));
        var basis = app.BeginNoteDraft(NoteScope.Person, note.OwnerEmail, note.Id)!;
        app.RetainNoteDraft(basis);
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("example.com");
        var result = app.SearchPeople("example.com").Single(r => r.Person.Email == "morgan@example.com");
        app.HighlightSearchResult(result);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(result, app.PeopleNavigation.HighlightedResult);
        app.ChangePeopleQuery("unmatched-query");
        var navigation = app.PeopleNavigation;
        Assert.False(app.NavigateSearchResult(result));

        Assert.Equal(navigation, app.PeopleNavigation);
        Assert.Equal("avery@example.com", app.PersonNotesView.OwnerEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.Equal("Saved", app.PersonNotesView.SelectedNote.Markdown);
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.False(app.CanSafelyClose);
        Assert.Contains("no longer available", app.Status);
        Assert.NotNull(app.SaveNoteDraft(basis, note.Title, "  Exact unsaved writing\r\n"));
        Assert.Equal("  Exact unsaved writing\r\n", Assert.Single(app.GetPersonNotes(note.OwnerEmail)).Markdown);
    }

    [Fact]
    public void Grouped_activation_revalidates_Person_eligibility_and_archived_access_does_not_reactivate()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Orchid", "Orchid writing")!;
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Orchid"));
        app.ChangePeopleQuery("orchid");
        var result = Assert.Single(app.SearchPeople("orchid"));
        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Willow"));
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        Assert.True(app.NavigateSearchResult(result), app.Status);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Contains(app.ArchivedPeople, p => p.Email == "avery@example.com");
        Assert.DoesNotContain(app.ReportingRoots.SelectMany(root => root.Reports), p => p.Person.Email == "avery@example.com");
        app.ChangePeopleQuery("unmatched-query");
        var navigation = app.PeopleNavigation;
        Assert.False(app.NavigateSearchResult(result));
        Assert.Equal(navigation, app.PeopleNavigation);
        Assert.Contains("no longer available", app.Status);
        Assert.Equal("Orchid writing", Assert.Single(app.GetPersonNotes(note.OwnerEmail)).Markdown);
    }

    [Fact]
    public void Shared_Team_note_excerpts_do_not_allocate_a_normalized_body_for_every_report()
    {
        using var app = Open();
        Apply(app, string.Join(",", Enumerable.Range(0, 64).Select(index =>
            $$"""{"email":"report{{index}}@example.com","displayName":"Report {{index}}","managerEmail":"morgan@example.com"}""")), 2);
        var markdown = "firstneedle\n" + string.Concat(Enumerable.Repeat("Fictitious shared context \t \n", 4096)) + "lastneedle";
        var note = app.CreateTeamNote("morgan@example.com", "Shared context", markdown)!;
        app.Search("lastneedle");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var matches = app.Search("lastneedle");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(66, matches.Count);
        Assert.True(allocated < 2 * 1024 * 1024,
            $"A shared 112-KiB note must not allocate another normalized body per matching report; allocated {allocated:N0} bytes.");
        Assert.All(matches, match =>
        {
            Assert.Equal(note.Id, match.NoteId);
            Assert.Equal("morgan@example.com", match.NoteOwner!.Email);
            Assert.Equal(markdown, match.MatchText);
            Assert.Contains("lastneedle", match.Excerpt);
            Assert.DoesNotContain("firstneedle", match.Excerpt);
            Assert.True(match.Excerpt.Length <= 162);
        });
        var firstMatches = app.Search("firstneedle");
        Assert.Equal(66, firstMatches.Count);
        Assert.All(firstMatches, match =>
        {
            Assert.Contains("firstneedle", match.Excerpt);
            Assert.DoesNotContain("lastneedle", match.Excerpt);
        });
        Assert.True(app.EditTeamNote(note.Id, "Updated context", "New current writing changedneedle"), app.Status);
        Assert.Empty(app.Search("lastneedle"));
        var updated = app.Search("changedneedle");
        Assert.Equal(66, updated.Count);
        Assert.All(updated, match => Assert.Equal("New current writing changedneedle", match.Excerpt));
    }

    [Fact]
    public void Live_search_excerpts_reveal_the_match_without_navigating_or_replacing_exact_reading()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Visit plan",
            new string('a', 240) + "\nNext visit to the Orchid office.\n" + new string('z', 240))!;
        Assert.True(app.NavigatePerson("morgan@example.com"));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("orchid");

        var person = Assert.Single(app.SearchPeople(app.PeopleNavigation.Query));
        var result = Assert.Single(person.Matches);
        Assert.Contains("Next visit to the Orchid office.", result.Excerpt);
        Assert.DoesNotContain('\n', result.Excerpt);
        Assert.True(result.Excerpt.Length < 200);
        Assert.Equal("Current — Person note 'Visit plan' — owned by Ave Preferred (avery@example.com)", result.SourceLabel);
        Assert.Equal("morgan@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Null(app.PeopleNavigation.SelectedResult);
        Assert.True(app.PeopleNavigation.OverlayOpen);

        Assert.True(app.NavigateSearchResult(person));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id));
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.Equal(new string('a', 240) + "\nNext visit to the Orchid office.\n" + new string('z', 240),
            app.PersonNotesView.SelectedNote.Markdown);
    }

    [Fact]
    public void People_search_replaces_browsing_and_manual_clearing_restores_its_position()
    {
        using var app = (WorkspaceViewModel)Open();
        var ongoing = app.CreatePersonNote("avery@example.com", "Continuing", "Ordinary writing")!;
        var secondary = app.CreatePersonNote("avery@example.com", "Orchid secondary", "")!;
        Assert.True(app.NavigatePerson("avery@example.com"));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, ongoing.Id));
        app.RememberPeopleBrowsing(["morgan@example.com"], 12, 39, 576.26);
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("Orchid");
        Assert.False(app.PeopleNavigation.IsBrowsing);
        var result = Assert.Single(app.SearchPeople(app.PeopleNavigation.Query));
        Assert.Equal("Empty note", result.Excerpt);
        Assert.Equal("avery@example.com", result.Person.Email);
        Assert.Equal(ongoing.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.False(app.PersonNotesView.IsEditing);
        Assert.True(app.PeopleNavigation.OverlayOpen);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        app.ChangePeopleQuery("");
        Assert.True(app.PeopleNavigation.IsBrowsing);
        Assert.Equal(["morgan@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal(12, app.PeopleNavigation.BrowseOffset);
        Assert.Equal(39, app.PeopleNavigation.BrowseHorizontalOffset);
        Assert.Equal(576.26, app.PeopleNavigation.BrowseExtentWidth);
        Assert.Equal(ongoing.Id, app.PersonNotesView.SelectedNote.Id);
        Assert.True(app.OpenOwnedNote(NoteScope.Person, secondary.Id));
    }

    [Fact]
    public async Task Overlay_search_and_browsing_retain_the_exact_owner_note_draft_without_a_navigation_veto()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreatePersonNote("avery@example.com", "Exact secondary", "Saved")!;
        app.CreatePersonNote("avery@example.com", "Other", "Untouched");
        Assert.True(app.NavigatePerson(note.OwnerEmail));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, note.Id, editing: true));
        app.UpdateNoteDraft(NoteScope.Person, note.Id, note.Title, "Retained owner writing");
        app.RememberPeopleBrowsing(["morgan@example.com"], 9, 24, 500);
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("alex@example");
        var result = Assert.Single(app.SearchPeople(app.PeopleNavigation.Query));
        Assert.True(app.NavigateSearchResult(result));
        Assert.Equal("alex@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.False(app.PeopleNavigation.OverlayOpen);
        Assert.True(app.NavigatePerson("morgan@example.com"));
        Assert.True(app.NavigatePerson(note.OwnerEmail));
        Assert.Equal(note.OwnerEmail, app.PeopleNavigation.SelectedEmail);
        Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
        Assert.True(app.PersonNotesView.IsEditing);
        Assert.Equal("Retained owner writing", app.PersonNotesView.SelectedNote.Markdown);
        Assert.Equal("Saved", app.GetPersonNotes(note.OwnerEmail).Single(saved => saved.Id == note.Id).Markdown);
        Assert.False(app.CanSafelyClose);
        app.ChangePeopleQuery("");
        Assert.Equal(9, app.PeopleNavigation.BrowseOffset);
        Assert.Equal(24, app.PeopleNavigation.BrowseHorizontalOffset);
        Assert.Equal(500, app.PeopleNavigation.BrowseExtentWidth);
        Assert.Equal(["morgan@example.com"], app.PeopleNavigation.ExpandedPeople);
        Assert.Equal("Retained owner writing", app.GetRetainedNoteDraft(NoteScope.Person, note.Id)!.Markdown);
        Assert.True(await app.FlushNoteDraftsAsync(), app.Status);
        Assert.Equal("Retained owner writing", app.GetPersonNotes(note.OwnerEmail).Single(saved => saved.Id == note.Id).Markdown);
        Assert.True(app.NavigatePerson("alex@example.com"));
        Assert.False(app.PeopleNavigation.OverlayOpen);
    }

    [Fact]
    public void Historical_search_opens_the_normal_Person_with_frozen_evidence_still_available()
    {
        using var app = (WorkspaceViewModel)Open();
        var note = app.CreateTeamNote("morgan@example.com", "Cedar", "Cedar original")!;
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(app.EditTeamNote(note.Id, "Later", "Willow current"));
        app.ShowPeopleOverlay(true);
        app.ChangePeopleQuery("cedar");
        var result = Assert.Single(app.SearchPeople("cedar"));
        Assert.True(app.NavigateSearchResult(result));
        Assert.Null(app.PeopleNavigation.SelectedResult);
        var destination = Assert.Single(result.Matches);
        Assert.Equal("avery@example.com", app.PeopleNavigation.SelectedEmail);
        Assert.Equal(capture.Id, destination.HistoricalContextId);
        Assert.Equal(note.Id, destination.NoteId);
        Assert.Equal("Cedar original", destination.MatchText);
        Assert.Equal("Cedar original", Assert.Single(destination.HistoricalContext!.TeamNotes).Markdown);
        Assert.False(app.PeopleNavigation.OverlayOpen);
    }

    private static string Batch(string records, int id) => $$"""
        {"schemaVersion":1,"batchId":"10000000-0013-4000-8000-{{id:000000000000}}",
         "observedAt":"2026-11-06T17:00:00Z","people":[{{records}}]}
        """;

    private static void Apply(IApplicationWorkflow app, string records, int id)
    {
        var preview = app.PreviewImport(Batch(records, id));
        Assert.True(preview.CanApply, app.Status);
        Assert.True(app.ApplyImport(preview.Id), app.Status);
    }

    private static IApplicationWorkflow Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateInMemory());
        Assert.True(app.OpenWorkspace("fictitious-search"));
        Apply(app, """
            {"email":"avery@example.com","displayName":"Avery Directory","preferredName":"Ave Preferred",
             "alias":"avhandle","jobTitle":"Scientist","department":"Research","officeBuilding":"Juniper",
             "officeNumber":"007B","officeState":"Ontario","officeCountry":"Canada",
             "linkedInProfileUrl":"https://www.linkedin.com/in/fictitious-avery","managerEmail":"morgan@example.com"},
            {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
            {"email":"alex@example.com","displayName":"Alex","managerEmail":null}
            """, 1);
        return app;
    }

    [Theory]
    [InlineData(" directory ", "displayName")]
    [InlineData("PREFERRED", "preferredName")]
    [InlineData("AVERY@EXAMPLE", "email")]
    [InlineData("avhandle", "alias")]
    [InlineData("scientist", "jobTitle")]
    [InlineData("research", "department")]
    [InlineData("juniper", "officeBuilding")]
    [InlineData("007b", "officeNumber")]
    [InlineData("ontario", "officeState")]
    [InlineData("canada", "officeCountry")]
    [InlineData("fictitious-avery", "linkedInProfileUrl")]
    [InlineData("morgan@example", "managerEmail")]
    public void Search_finds_each_stored_attribute_without_losing_identity_or_current_attribution(string query, string field)
    {
        using var app = Open();
        var match = Assert.Single(app.Search(query), r => r.Person.Email == "avery@example.com" && r.Field == field);
        Assert.Equal(SearchContext.Current, match.Context);
        Assert.Equal(SearchSource.Profile, match.Source);
        Assert.Null(match.HistoricalContextId);
        Assert.Empty(app.Search(" \t"));
        Assert.Empty(app.Search("not-present-anywhere"));
    }

    [Fact]
    public void Person_and_Team_writing_is_searchable_with_explicit_ownership_inheritance_and_note_archival()
    {
        using var app = Open();
        var direct = app.CreatePersonNote("morgan@example.com", "Cedar direct", "Cedar private Person context")!;
        var team = app.CreateTeamNote("morgan@example.com", "Cedar team", "# Cedar formal subtree")!;
        var own = app.CreateTeamNote("avery@example.com", "Own", "Cedar owned writing")!;
        var results = app.Search("cedar");
        Assert.Contains(results, r => r.Source == SearchSource.PersonNote && r.Person.Email == "morgan@example.com" &&
            r.NoteId == direct.Id && r.NoteOwner!.Email == "morgan@example.com");
        Assert.DoesNotContain(results, r => r.Source == SearchSource.PersonNote && r.Person.Email == "avery@example.com");
        var inherited = Assert.Single(results, r => r.Source == SearchSource.InheritedTeamNote);
        Assert.Equal("avery@example.com", inherited.Person.Email);
        Assert.Equal("morgan@example.com", inherited.NoteOwner!.Email);
        Assert.Equal(team.Id, inherited.NoteId);
        Assert.Equal(SearchContext.Current, inherited.Context);
        Assert.Contains("Inherited Team note", inherited.Label);
        Assert.Contains("morgan@example.com", inherited.Label);
        Assert.Contains(results, r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.OwnedTeamNote && r.NoteId == own.Id);

        Assert.True(app.ArchiveTeamNote(team.Id));
        Assert.True(app.ArchivePersonNote(direct.Id));
        Assert.DoesNotContain(app.Search("cedar"), r => r.Source == SearchSource.InheritedTeamNote);
        var archived = Assert.Single(app.Search("cedar"), r => r.NoteId == team.Id);
        Assert.Equal(SearchContext.Archived, archived.Context);
        Assert.True(archived.NoteArchived);
        Assert.Contains("Archived note", archived.Label);
        Assert.Contains(app.Search("cedar"), r => r.NoteId == direct.Id && r.NoteArchived);
        Assert.True(app.EditTeamNote(own.Id, "Own", "Updated local text"));
        Assert.DoesNotContain(app.Search("cedar"), r => r.NoteId == own.Id);
        Assert.Contains(app.Search("updated local"), r => r.NoteId == own.Id);
    }

    [Fact]
    public void Historical_matches_target_the_frozen_capture_or_profile_change_not_later_current_writing()
    {
        using var app = Open();
        var note = app.CreateTeamNote("morgan@example.com", "Old cedar", "Cedar former team")!;
        app.CreateTeamNote("alex@example.com", "New context", "New pine team");
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var capture = Assert.Single(app.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(app.EditTeamNote(note.Id, "Later title", "Later willow team"));
        Assert.True(app.EditPreferredName("morgan@example.com", "Renamed manager"));
        var frozen = Assert.Single(app.Search("cedar"));
        Assert.Equal(SearchSource.HistoricalTeamNote, frozen.Source);
        Assert.Equal(SearchContext.Historical, frozen.Context);
        Assert.Equal(capture.Id, frozen.HistoricalContextId);
        Assert.Equal(capture.Id, frozen.HistoricalContext!.Id);
        Assert.Equal(note.Id, frozen.NoteId);
        Assert.Equal("Morgan", frozen.NoteOwner!.Heading);
        Assert.Equal("Cedar former team", frozen.MatchText);
        Assert.Contains("Historical", frozen.Label);
        Assert.DoesNotContain(app.Search("willow"), r => r.Person.Email == "avery@example.com");
        Assert.Contains(app.Search("pine"), r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.InheritedTeamNote);
        Assert.Contains(app.Search("morgan@example"), r => r.Person.Email == "avery@example.com" &&
            r.Source == SearchSource.FormerReportingChain && r.HistoricalContextId == capture.Id);

        Assert.True(app.EditPersonText("avery@example.com", PersonTextField.OfficeBuilding, "Cypress"));
        var oldOffice = Assert.Single(app.Search("juniper"));
        Assert.Equal(SearchSource.ProfileHistory, oldOffice.Source);
        Assert.Equal(SearchContext.Historical, oldOffice.Context);
        Assert.Equal("officeBuilding", oldOffice.ProfileChange!.Field);
        Assert.Equal("Juniper", oldOffice.ProfileChange.OldValue.GetString());
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        var archived = Assert.Single(app.Search("007B"), r => r.Source == SearchSource.Profile);
        Assert.Equal(SearchContext.Archived, archived.Context);
        Assert.Contains("Archived person", archived.Label);
        Assert.Empty(app.GetInheritedTeamNotes("avery@example.com"));
        Assert.Contains(app.Search("cedar"), r => r.Person.Archived && r.HistoricalContextId == capture.Id &&
            r.Label.Contains("Historical / Archived person"));
        Assert.Contains(app.Search("pine"), r => r.Person.Email == "avery@example.com" && r.Source == SearchSource.HistoricalTeamNote);
    }

    [Fact]
    public void Favorites_are_local_or_explicit_import_state_and_omission_false_replay_and_archival_remain_distinct()
    {
        using var app = Open();
        Assert.Empty(app.FavoritePeople);
        Assert.All(app.People, p => Assert.False(p.IsFavorite));
        var notifications = new List<string?>();
        ((WorkspaceViewModel)app).PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        var preview = app.PreviewImport(Batch("""{"email":"avery@example.com","officeCountry":"France"}""", 2));
        Assert.True(app.EditFavorite("AVERY@example.com", true));
        Assert.Contains(nameof(app.FavoritePeople), notifications);
        Assert.Equal("avery@example.com", Assert.Single(app.FavoritePeople).Email);
        Assert.False(app.ApplyImport(preview.Id));
        var evidence = Assert.Single(app.GetProfileTimeline("avery@example.com")!.Confirmations, e => e.Field == "isFavorite");
        Assert.Equal(ProfileChangeSource.LocalEdit, evidence.Source);
        Assert.True(evidence.Value.GetBoolean());
        Assert.Empty(app.GetHistoricalTeamContexts("avery@example.com"));
        Apply(app, """{"email":"avery@example.com","officeNumber":"0009C"}""", 3);
        Apply(app, """{"email":"alex@example.com","isFavorite":true}""", 4);
        Assert.Equal(2, app.FavoritePeople.Count);
        Assert.Equal(evidence, Assert.Single(app.GetProfileTimeline("avery@example.com")!.Confirmations, e => e.Field == "isFavorite"));
        Apply(app, """{"email":"avery@example.com","isFavorite":false}""", 5);
        Assert.Equal("alex@example.com", Assert.Single(app.FavoritePeople).Email);
        Assert.True(app.EditFavorite("avery@example.com", true));
        Apply(app, """{"email":"avery@example.com","isFavorite":false}""", 5);
        Assert.Equal(2, app.FavoritePeople.Count);
        Assert.True(app.EditPersonArchived("avery@example.com", true));
        Assert.Contains(app.FavoritePeople, p => p.Email == "avery@example.com" && p.Archived);
        Assert.True(app.EditFavorite("avery@example.com", false));
        Assert.Equal("alex@example.com", Assert.Single(app.FavoritePeople).Email);
        var count = app.GetProfileTimeline("avery@example.com")!.Changes.Count;
        Assert.True(app.EditFavorite("avery@example.com", false));
        Assert.Equal(count, app.GetProfileTimeline("avery@example.com")!.Changes.Count);
        var invalid = app.PreviewImport(Batch("""{"email":"alex@example.com","isFavorite":null}""", 6));
        Assert.False(invalid.CanApply);
        Assert.Contains("isFavorite", string.Join("\n", invalid.Errors));
        Assert.False(app.EditFavorite("unknown@example.com", true));
    }

    [Fact]
    public void Search_retains_all_ancestor_attribution_review_warnings_and_independently_frozen_note_versions()
    {
        using var app = Open();
        Apply(app, """
            {"email":"casey@example.com","displayName":"Casey","managerEmail":null},
            {"email":"morgan@example.com","managerEmail":"casey@example.com"}
            """, 2);
        var managerNote = app.CreateTeamNote("morgan@example.com", "Aurora manager", "Aurora version one")!;
        var rootNote = app.CreateTeamNote("casey@example.com", "Aurora root", "Aurora ancestor")!;
        Assert.True(app.EditPersonText("morgan@example.com", PersonTextField.JobTitle, "Director"));
        var inherited = app.Search("aurora").Where(r => r.Person.Email == "avery@example.com" &&
            r.Source == SearchSource.InheritedTeamNote).ToArray();
        Assert.Equal(new[] { "morgan@example.com", "casey@example.com" }, inherited.Select(r => r.NoteOwner!.Email));
        Assert.True(inherited[0].NoteNeedsReview);
        Assert.Contains("Needs review", inherited[0].Label);
        Assert.False(inherited[1].NoteNeedsReview);
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var first = app.GetHistoricalTeamContexts("avery@example.com").Last();
        Assert.True(app.EditPersonManager("avery@example.com", "morgan@example.com"));
        Assert.True(app.EditTeamNote(managerNote.Id, "Aurora edited", "Aurora version two"));
        Assert.True(app.EditPersonManager("avery@example.com", "alex@example.com"));
        var second = app.GetHistoricalTeamContexts("avery@example.com").Last();
        var frozen = app.Search("aurora").Where(r => r.Person.Email == "avery@example.com" && r.NoteId == managerNote.Id).ToArray();
        Assert.Equal(2, frozen.Length);
        Assert.Contains(frozen, r => r.HistoricalContextId == first.Id && r.MatchText == "Aurora version one" && r.NoteNeedsReview);
        Assert.Contains(frozen, r => r.HistoricalContextId == second.Id && r.MatchText == "Aurora version two" && !r.NoteNeedsReview);
        Assert.Contains(app.Search("aurora"), r => r.HistoricalContextId == first.Id && r.NoteId == rootNote.Id);
    }
}
