using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class ProfileSessionPersistenceTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));
    private string Folder => Path.Combine(root, "workspace");
    private const string Email = "avery@example.com";
    private const string Batch = """
        {"schemaVersion":1,"batchId":"29000000-0000-4000-8000-000000000001",
         "observedAt":"2026-11-01T17:00:00Z","people":[
         {"email":"avery@example.com","displayName":"Avery","jobTitle":"Engineer","managerEmail":"morgan@example.com"},
         {"email":"morgan@example.com","displayName":"Morgan","managerEmail":null},
         {"email":"riley@example.com","displayName":"Riley","managerEmail":null}]}
        """;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC");

    [Fact]
    public void Complete_profile_save_is_one_durable_publication_and_cancel_does_not_publish()
    {
        using var app = Open();
        var image = Path.Combine(root, "local.png");
        File.WriteAllBytes(image, Png);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.True(app.StageProfileManager("riley@example.com"));
        Assert.True(app.StageProfilePhoto(image));
        var pointer = File.ReadAllBytes(Path.Combine(Folder, "workspace.json"));
        using (var before = Reopen())
        {
            Assert.Equal("Engineer", before.GetRelationships(Email)!.Person.JobTitle);
            Assert.Null(before.GetPhoto(Email));
            Assert.Empty(before.GetHistoricalTeamContexts(Email));
        }
        app.CancelProfileEdit();
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Folder, "workspace.json")));
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.True(app.StageProfileManager("riley@example.com"));
        Assert.True(app.StageProfilePhoto(image));
        using (File.Open(Path.Combine(Folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.SaveProfileEdit());
            Assert.Equal("Principal", app.ProfileEdit!.Draft.JobTitle);
            Assert.Equal(Email, app.ProfileEdit.Basis.Email);
            Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Folder, "workspace.json")));
        }
        Assert.True(app.EditPreferredName(Email, "My Avery"), app.Status);
        Assert.True(app.EditFavorite(Email, true), app.Status);
        Assert.NotNull(app.CreatePersonNote(Email, "Private context", "# Retained"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        File.Delete(image);
        using var reopened = Reopen();
        var person = reopened.GetRelationships(Email)!.Person;
        Assert.Equal("Principal", person.JobTitle);
        Assert.Equal("riley@example.com", person.ManagerEmail);
        Assert.Equal("My Avery", person.PreferredName);
        Assert.True(person.IsFavorite);
        var photo = reopened.GetPhoto(Email)!;
        Assert.Equal((1, 1, "image/png"), (photo.Width, photo.Height, photo.ContentType));
        Assert.Equal(Png, photo.Bytes.ToArray());
        Assert.Equal("# Retained", Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
        Assert.Equal(new[] { "isFavorite", "jobTitle", "managerEmail", "photoPath", "preferredName" },
            reopened.GetProfileTimeline(Email)!.Changes.Select(c => c.Field).Order().ToArray());
        Assert.Equal("morgan@example.com",
            Assert.Single(Assert.Single(reopened.GetHistoricalTeamContexts(Email)).ReportingChain).Email);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfilePhoto(null));
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var cleared = Reopen();
        Assert.Null(cleared.GetPhoto(Email));
    }

    [Fact]
    public void Import_transition_retains_a_failed_leave_save_then_publishes_only_the_explicitly_reviewed_batch()
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "009"));
        var pointer = File.ReadAllBytes(Path.Combine(Folder, "workspace.json"));
        using (File.Open(Path.Combine(Folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.TryLeaveProfileEdit(ProfileLeaveDecision.Save));
            Assert.Contains("NOT saved", app.Status);
            Assert.False(app.TryOpenImport());
            Assert.False(app.IsImportOpen);
            Assert.Equal(Email, app.ProfileEdit!.Basis.Email);
            Assert.Equal("Principal", app.ProfileEdit.Draft.JobTitle);
            Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Folder, "workspace.json")));
        }
        Assert.True(app.TryLeaveProfileEdit(ProfileLeaveDecision.Save), app.Status);
        Assert.True(app.TryOpenImport(), app.Status);
        var import = Path.Combine(root, "review.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"32000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"avery@example.com","jobTitle":"Imported title"}]}
            """);
        var review = app.PreviewImportFile(import);
        Assert.True(review.CanApply, app.Status);
        using (var beforeApply = Reopen())
            Assert.Equal("Principal", beforeApply.GetRelationships(Email)!.Person.JobTitle);
        Assert.True(app.ApplyImport(review.Id), app.Status);
        app.ShowPeople();
        Assert.False(app.IsImportOpen);
        Assert.Null(app.ProfileEdit);
        using var reopened = Reopen();
        var person = reopened.GetRelationships(Email)!.Person;
        Assert.Equal("Imported title", person.JobTitle);
        Assert.Equal("009", person.OfficeNumber);
        Assert.Equal("morgan@example.com", person.ManagerEmail);
        Assert.Contains(reopened.GetProfileTimeline(Email)!.Changes, change =>
            change.Field == "jobTitle" && change.NewValue.GetString() == "Imported title");
    }

    [Fact]
    public void Failed_import_can_return_to_people_then_reenter_review_and_apply_without_discarding_its_error()
    {
        using var app = Open();
        Assert.True(app.TryOpenImport(), app.Status);
        var import = Path.Combine(root, "retry-import.json");
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"32000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"avery@example.com","jobTitle":"Reviewed retry title"}]}
            """);
        var review = app.PreviewImportFile(import);
        Assert.True(review.CanApply, app.Status);
        var pointer = File.ReadAllBytes(Path.Combine(Folder, "workspace.json"));
        using (File.Open(Path.Combine(Folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.ApplyImport(review.Id));
            Assert.Contains("NOT saved", app.LocalSaveError);
            Assert.False(app.CanSafelyClose);
            Assert.Equal("Engineer", app.GetRelationships(Email)!.Person.JobTitle);
            Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Folder, "workspace.json")));
            app.ShowPeople();
            Assert.False(app.IsImportOpen);
        }
        var failure = app.LocalSaveError;
        Assert.True(app.TryOpenImport(), app.Status);
        Assert.True(app.IsImportOpen);
        Assert.Equal(failure, app.LocalSaveError);
        Assert.False(app.CanSafelyClose);
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Folder, "workspace.json")));
        review = app.PreviewImportFile(import);
        Assert.True(review.CanApply, app.Status);
        using (var beforeApply = Reopen())
            Assert.Equal("Engineer", beforeApply.GetRelationships(Email)!.Person.JobTitle);
        Assert.True(app.ApplyImport(review.Id), app.Status);
        app.ShowPeople();
        Assert.False(app.IsImportOpen);
        Assert.Null(app.LocalSaveError);
        Assert.True(app.CanSafelyClose);
        using var reopened = Reopen();
        Assert.Equal("Reviewed retry title", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("morgan@example.com", reopened.GetRelationships(Email)!.Person.ManagerEmail);
        Assert.Equal("Reviewed retry title",
            Assert.Single(reopened.GetProfileTimeline(Email)!.Changes).NewValue.GetString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("Person")]
    [InlineData("Team")]
    [InlineData("preferred")]
    [InlineData("profile")]
    public void Discarding_failed_import_releases_only_that_batch_and_preserves_independent_writing(string independent)
    {
        using var app = Open();
        var personNote = app.CreatePersonNote(Email, "Person context", "# Saved Person")!;
        var teamNote = app.CreateTeamNote(Email, "Team context", "# Saved Team")!;
        Assert.True(app.TryOpenImport(), app.Status);
        var review = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"32000000-0000-4000-8000-000000000003",
             "observedAt":"2026-11-02T17:00:00Z","people":[
             {"email":"avery@example.com","jobTitle":"Abandoned import"}]}
            """);
        Assert.True(review.CanApply, app.Status);
        var pointer = File.ReadAllBytes(Path.Combine(Folder, "workspace.json"));
        NoteDraft? noteDraft = null;
        using (File.Open(Path.Combine(Folder, ".workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(app.ApplyImport(review.Id));
            Assert.True(app.HasFailedImport);
            Assert.False(app.CanSafelyClose);
            app.ShowPeople();
            if (independent is "Person" or "Team")
            {
                var scope = independent == "Person" ? NoteScope.Person : NoteScope.Team;
                noteDraft = app.BeginNoteDraft(scope, Email, scope == NoteScope.Person ? personNote.Id : teamNote.Id)!;
                Assert.Null(app.SaveNoteDraft(noteDraft, "", "# Retained writing"));
            }
            else if (independent == "preferred")
                Assert.False(app.EditPreferredName(Email, " "));
            else if (independent == "profile")
            {
                Assert.True(app.BeginProfileEdit(Email));
                Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained profile"));
            }
            app.DiscardImportChanges();
            Assert.False(app.HasFailedImport);
            Assert.Contains("Import discarded", app.Status);
            Assert.Equal(independent == "none", app.CanSafelyClose);
            var restarted = false;
            Assert.Equal(independent == "none", app.RestartWhenSafe(_ => restarted = true));
            Assert.Equal(independent == "none", restarted);
            Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(Folder, "workspace.json")));
            if (independent == "profile")
                Assert.Equal("Retained profile", app.ProfileEdit!.Draft.JobTitle);
            if (independent == "preferred") Assert.Contains("preferredName", app.LocalSaveError);
            if (noteDraft is not null) Assert.Contains(independent + " note", app.LocalSaveError);
        }
        Assert.False(app.ApplyImport(review.Id));
        Assert.Contains("missing or stale", app.Status);
        if (noteDraft is not null)
        {
            Assert.False(app.CanSafelyClose);
            Assert.NotNull(app.SaveNoteDraft(noteDraft, "Retained title", "# Retained writing"));
        }
        else if (independent == "preferred") app.DiscardPersonChanges(Email);
        else if (independent == "profile") app.CancelProfileEdit();
        Assert.True(app.CanSafelyClose);
        using var reopened = Reopen();
        Assert.Equal("Engineer", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Empty(reopened.GetProfileTimeline(Email)!.Changes);
        Assert.Equal(independent == "Person" ? "# Retained writing" : "# Saved Person",
            Assert.Single(reopened.GetPersonNotes(Email)).Markdown);
        Assert.Equal(independent == "Team" ? "# Retained writing" : "# Saved Team",
            Assert.Single(reopened.GetTeamNotes(Email)).Markdown);
    }

    [Theory]
    [InlineData("jobTitle", false, false)]
    [InlineData("department", false, false)]
    [InlineData("preferredName", false, false)]
    [InlineData("department", true, false)]
    [InlineData("jobTitle", false, true)]
    [InlineData("department", false, true)]
    [InlineData("preferredName", false, true)]
    [InlineData("department", true, true)]
    public void Same_profile_delivery_blocks_save_and_retry_even_without_view_refresh(string field, bool refresh, bool incremental)
    {
        using var app = Open(incremental);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Local draft"));
        using (var provider = Reopen(incremental))
            Assert.True(provider.EditPersonText(Email, field switch
            {
                "jobTitle" => PersonTextField.JobTitle,
                "department" => PersonTextField.Department,
                _ => PersonTextField.PreferredName
            }, "Delivered"), provider.Status);
        var personFile = CurrentPersonFile(Email);
        var delivered = File.ReadAllBytes(personFile);
        if (refresh) Assert.True(app.RefreshWorkspace(), app.Status);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.False(app.SaveProfileEdit());
            Assert.Contains("changed externally", app.Status);
            Assert.Equal("Local draft", app.ProfileEdit!.Draft.JobTitle);
            Assert.Equal(delivered, File.ReadAllBytes(personFile));
            Assert.False(app.CanSafelyClose);
            Assert.False(app.TryOpenImport());
            Assert.False(app.IsImportOpen);
        }
        app.CancelProfileEdit();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.OfficeNumber, "007"));
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var reopened = Reopen(incremental);
        Assert.Equal("007", reopened.GetRelationships(Email)!.Person.OfficeNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incorporated_delivery_and_unrelated_person_delivery_do_not_block_or_get_reverted(bool incremental)
    {
        using var app = Open(incremental);
        Deliver(Email, "department", "Already incorporated");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Local draft"));
        Deliver("riley@example.com", "officeNumber", "Delivered elsewhere");
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var reopened = Reopen(incremental);
        Assert.Equal("Already incorporated", reopened.GetRelationships(Email)!.Person.Department);
        Assert.Equal("Local draft", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("Delivered elsewhere", reopened.GetRelationships("riley@example.com")!.Person.OfficeNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unavailable_photo_and_invalid_field_retain_the_whole_draft_without_partial_history(bool lockedPhoto)
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Principal"));
        Assert.True(app.StageProfileManager("riley@example.com"));
        var image = Path.Combine(root, "photo.png");
        if (lockedPhoto) File.WriteAllBytes(image, Png);
        Assert.True(app.StageProfilePhoto(image));
        using (lockedPhoto ? File.Open(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
            Assert.False(app.SaveProfileEdit());
        Assert.Contains("photo", app.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(app.StageProfilePhoto(null));
        Assert.True(app.StageProfileText(PersonTextField.LinkedInProfileUrl, "http://invalid.example"));
        Assert.False(app.SaveProfileEdit());
        Assert.Equal("Principal", app.ProfileEdit!.Draft.JobTitle);
        using var reopened = Reopen();
        Assert.Equal("Engineer", reopened.GetRelationships(Email)!.Person.JobTitle);
        Assert.Equal("morgan@example.com", reopened.GetRelationships(Email)!.Person.ManagerEmail);
        Assert.Empty(reopened.GetProfileTimeline(Email)!.Changes);
        Assert.Empty(reopened.GetHistoricalTeamContexts(Email));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Local_quick_change_does_not_authorize_an_existing_external_conflict_and_cancel_keeps_both(bool incremental)
    {
        using var app = Open(incremental);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Local draft"));
        Deliver(Email, "department", "Delivered department");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.EditPreferredName(Email, "My Avery"), app.Status);
        Assert.True(app.EditFavorite(Email, true), app.Status);
        Assert.False(app.SaveProfileEdit());
        Assert.Contains("changed externally", app.Status);
        Assert.Equal("Local draft", app.ProfileEdit!.Draft.JobTitle);
        app.CancelProfileEdit();
        using var reopened = Reopen(incremental);
        var saved = reopened.GetRelationships(Email)!.Person;
        Assert.Equal("My Avery", saved.PreferredName);
        Assert.True(saved.IsFavorite);
        Assert.Equal("Delivered department", saved.Department);
        Assert.Equal("Engineer", saved.JobTitle);
    }

    private WorkspaceViewModel Open(bool incremental = false)
    {
        var app = new WorkspaceViewModel(incremental ? ApplicationWorkflow.CreateIncrementalFileBacked() :
            ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        return app;
    }

    private IApplicationWorkflow Reopen(bool incremental = false)
    {
        var app = incremental ? ApplicationWorkflow.CreateIncrementalFileBacked() : ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        return app;
    }

    private string CurrentPersonFile(string email)
    {
        var pointer = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "workspace.json")))!;
        if (pointer["schemaVersion"]!.GetValue<int>() == 3)
            return WorkspaceFixtures.IndexedDocuments(Folder)["people:" + email];
        var generation = pointer["generation"]!.GetValue<string>();
        return Directory.GetFiles(Path.Combine(Folder, "generations", generation, "people"), "*.json")
            .Single(path => JsonNode.Parse(File.ReadAllText(path))!["email"]!.GetValue<string>() == email);
    }

    private void Deliver(string email, string field, string value)
    {
        var path = CurrentPersonFile(email);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json[field] = value;
        File.WriteAllText(path, json.ToJsonString());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
