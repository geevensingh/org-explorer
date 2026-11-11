using System.Text.Json.Nodes;
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
    [InlineData("jobTitle", false)]
    [InlineData("department", false)]
    [InlineData("preferredName", false)]
    [InlineData("department", true)]
    public void Same_profile_delivery_blocks_save_and_retry_even_without_view_refresh(string field, bool refresh)
    {
        using var app = Open();
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Local draft"));
        using (var provider = Reopen())
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
        using var reopened = Reopen();
        Assert.Equal("007", reopened.GetRelationships(Email)!.Person.OfficeNumber);
    }

    [Fact]
    public void Incorporated_delivery_and_unrelated_person_delivery_do_not_block_or_get_reverted()
    {
        using var app = Open();
        Deliver(Email, "department", "Already incorporated");
        Assert.True(app.RefreshWorkspace(), app.Status);
        Assert.True(app.BeginProfileEdit(Email));
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Local draft"));
        Deliver("riley@example.com", "officeNumber", "Delivered elsewhere");
        Assert.True(app.SaveProfileEdit(), app.Status);
        using var reopened = Reopen();
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

    [Fact]
    public void Local_quick_change_does_not_authorize_an_existing_external_conflict_and_cancel_keeps_both()
    {
        using var app = Open();
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
        using var reopened = Reopen();
        var saved = reopened.GetRelationships(Email)!.Person;
        Assert.Equal("My Avery", saved.PreferredName);
        Assert.True(saved.IsFavorite);
        Assert.Equal("Delivered department", saved.Department);
        Assert.Equal("Engineer", saved.JobTitle);
    }

    private WorkspaceViewModel Open()
    {
        var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImport(Batch).Id), app.Status);
        return app;
    }

    private IApplicationWorkflow Reopen()
    {
        var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Folder), app.Status);
        return app;
    }

    private string CurrentPersonFile(string email)
    {
        var generation = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "workspace.json")))!["generation"]!.GetValue<string>();
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
