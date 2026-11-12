using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class WorkspaceStartupTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "startup-" + Guid.NewGuid().ToString("N")));
    private string Preferences => Path.Combine(root, "app-settings", "workspace-settings.json");

    [Fact]
    public void First_run_stays_unopened_and_successful_open_is_restored_after_settings_and_app_reopen()
    {
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.Null(settings.LastWorkspacePath);
            Assert.Null(settings.PreferenceError);
            Assert.False(settings.RestoreWorkspace(app));
            Assert.Equal(WorkspaceCompatibilityState.NotOpened, app.Compatibility.State);
            Assert.Empty(app.People);
            Assert.False(Directory.Exists(root));

            var workspace = Path.Combine(root, "workspace");
            WorkspaceFixtures.CopyCurrent(workspace);
            Assert.True(settings.OpenWorkspace(app, workspace), app.Status);
            Assert.Equal(workspace, settings.LastWorkspacePath);
            Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        }

        var reopenedSettings = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(reopenedSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal(WorkspaceCompatibilityState.Compatible, reopened.Compatibility.State);
        Assert.Equal(Path.Combine(root, "workspace"), reopened.Compatibility.WorkspacePath);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Missing_workspace_is_not_recreated_and_retry_can_reopen_the_original()
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyCurrent(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
            Assert.True(settings.OpenWorkspace(app, workspace), app.Status);
        var moved = Path.Combine(root, "temporarily-moved");
        Directory.Move(workspace, moved);

        var reopenedSettings = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.False(reopenedSettings.RestoreWorkspace(reopened));
        Assert.False(reopened.IsWorkspaceCurrent);
        Assert.Empty(reopened.People);
        Assert.Contains("could not be opened", reopened.Status);
        Assert.False(Directory.Exists(workspace));
        Assert.Equal(workspace, new WorkspaceSettings(Preferences).LastWorkspacePath);

        Directory.Move(moved, workspace);
        Assert.True(reopenedSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_or_inaccessible_restore_keeps_the_app_empty_and_preserves_the_successful_preference(bool locked)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyCurrent(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
            Assert.True(settings.OpenWorkspace(app, workspace), app.Status);
        var pointer = Path.Combine(workspace, "workspace.json");
        var original = File.ReadAllBytes(pointer);
        if (!locked)
        {
            var json = JsonNode.Parse(original)!;
            json["schemaVersion"] = 99;
            File.WriteAllText(pointer, json.ToJsonString());
        }
        var bytes = WorkspaceFixtures.Bytes(workspace);
        using (var held = locked ? File.Open(pointer, FileMode.Open, FileAccess.Read, FileShare.None) : null)
        using (var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.False(settings.RestoreWorkspace(reopened));
            Assert.False(reopened.IsWorkspaceCurrent);
            Assert.Empty(reopened.People);
            Assert.Contains(locked ? "being used" : "Unsupported", reopened.Status);
            Assert.False(settings.OpenWorkspace(reopened, workspace));
            Assert.Equal(workspace, new WorkspaceSettings(Preferences).LastWorkspacePath);
        }
        AssertWorkspaceBytes(workspace, bytes);
        File.WriteAllBytes(pointer, original);
        using var retry = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(settings.RestoreWorkspace(retry), retry.Status);
        Assert.Equal("Ave", retry.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Fact]
    public void Only_successful_alternate_opens_replace_the_default_and_manual_folder_creation_still_works()
    {
        var first = Path.Combine(root, "first");
        var invalid = Path.Combine(root, "not-a-folder");
        var alternate = Path.Combine(root, "alternate");
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.True(settings.OpenWorkspace(app, first), app.Status);
            Assert.True(Directory.Exists(first));
            File.WriteAllText(invalid, "Not a Workspace directory");
            Assert.False(settings.OpenWorkspace(app, invalid));
            Assert.Equal(first, new WorkspaceSettings(Preferences).LastWorkspacePath);
            WorkspaceFixtures.CopyCurrent(alternate);
            Assert.True(settings.OpenWorkspace(app, alternate), app.Status);
            Assert.Equal(alternate, new WorkspaceSettings(Preferences).LastWorkspacePath);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(new WorkspaceSettings(Preferences).RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal(alternate, reopened.Compatibility.WorkspacePath);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"schemaVersion":99,"lastWorkspacePath":"C:\\untrusted"}""")]
    [InlineData("""{"schemaVersion":1,"lastWorkspacePath":"relative-folder"}""")]
    public void Corrupt_or_unwritable_preferences_are_visible_without_claiming_the_new_default_was_saved(string corrupt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Preferences)!);
        File.WriteAllText(Preferences, corrupt);
        var settings = new WorkspaceSettings(Preferences);
        Assert.Null(settings.LastWorkspacePath);
        Assert.Contains("could not be read", settings.PreferenceError);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.False(settings.RestoreWorkspace(app));
        Assert.Equal(WorkspaceCompatibilityState.NotOpened, app.Compatibility.State);
        Assert.Equal(corrupt, File.ReadAllText(Preferences));

        var first = Path.Combine(root, "first");
        Assert.True(settings.OpenWorkspace(app, first), app.Status);
        Assert.Null(settings.PreferenceError);
        var accepted = File.ReadAllBytes(Preferences);
        var alternate = Path.Combine(root, "alternate");
        using (File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(settings.OpenWorkspace(app, alternate), app.Status);
            Assert.True(app.IsWorkspaceCurrent);
            Assert.Equal(alternate, app.Compatibility.WorkspacePath);
            Assert.Equal(first, settings.LastWorkspacePath);
            Assert.Contains("NOT saved", settings.PreferenceError);
            Assert.Equal(accepted, File.ReadAllBytes(Preferences));
            Assert.Equal(first, new WorkspaceSettings(Preferences).LastWorkspacePath);
        }
        Assert.True(settings.OpenWorkspace(app, alternate), app.Status);
        Assert.Null(settings.PreferenceError);
        Assert.Equal(alternate, new WorkspaceSettings(Preferences).LastWorkspacePath);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Preferences)!, "*.pending"));
    }

    [Fact]
    public void An_unusable_settings_location_does_not_prevent_local_open_or_claim_a_saved_preference()
    {
        var settings = new WorkspaceSettings(Path.Combine(root, "invalid\0.json"));
        Assert.Contains("could not be read", settings.PreferenceError);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(settings.OpenWorkspace(app, Path.Combine(root, "workspace")), app.Status);
        Assert.True(app.IsWorkspaceCurrent);
        Assert.Null(settings.LastWorkspacePath);
        Assert.Contains("NOT saved", settings.PreferenceError);
    }

    [Fact]
    public void Explicit_restart_workspace_restore_uses_existing_only_and_updates_the_default_only_on_success()
    {
        var first = Path.Combine(root, "first");
        var restartWorkspace = Path.Combine(root, "restart-workspace");
        var settings = new WorkspaceSettings(Preferences);
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked()))
        {
            Assert.True(settings.OpenWorkspace(app, first), app.Status);
            Assert.False(settings.RestoreWorkspace(app, restartWorkspace));
            Assert.False(Directory.Exists(restartWorkspace));
            Assert.Equal(first, new WorkspaceSettings(Preferences).LastWorkspacePath);
            WorkspaceFixtures.CopyCurrent(restartWorkspace);
            Assert.True(settings.RestoreWorkspace(app, restartWorkspace), app.Status);
        }
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(new WorkspaceSettings(Preferences).RestoreWorkspace(reopened), reopened.Status);
        Assert.Equal(restartWorkspace, reopened.Compatibility.WorkspacePath);
        Assert.Equal("Ave", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manual_and_existing_only_settings_opens_retain_dirty_profiles_and_the_successful_default(bool restore)
    {
        var first = Path.Combine(root, "first");
        var alternate = Path.Combine(root, "alternate");
        WorkspaceFixtures.CopyCurrent(first);
        WorkspaceFixtures.CopyCurrent(alternate);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
        Assert.True(settings.OpenWorkspace(app, first), app.Status);
        Assert.True(app.BeginProfileEdit("avery@example.com"), app.Status);
        Assert.True(app.StageProfileText(PersonTextField.JobTitle, "Retained on open rejection"), app.Status);
        Assert.True(app.EditPreferredName("avery@example.com", "Independent quick name"), app.Status);
        var draft = app.ProfileEdit;
        var preference = File.ReadAllBytes(Preferences);
        var saved = WorkspaceFixtures.Bytes(first);

        bool OpenAlternate() => restore ? settings.RestoreWorkspace(app, alternate) : settings.OpenWorkspace(app, alternate);
        Assert.False(OpenAlternate());
        Assert.Same(draft, app.ProfileEdit);
        Assert.Equal("Retained on open rejection", app.ProfileEdit!.Draft.JobTitle);
        Assert.Equal("Independent quick name", app.GetRelationships("avery@example.com")!.Person.PreferredName);
        Assert.Equal(first, app.Compatibility.WorkspacePath);
        Assert.Contains("Save, Discard or Stay", app.Status);
        Assert.Equal(preference, File.ReadAllBytes(Preferences));
        Assert.Equal(first, new WorkspaceSettings(Preferences).LastWorkspacePath);
        AssertWorkspaceBytes(first, saved);

        Assert.True(app.TryLeaveProfileEdit(ProfileLeaveDecision.Discard), app.Status);
        Assert.True(OpenAlternate(), app.Status);
        Assert.Null(app.ProfileEdit);
        Assert.Equal(alternate, new WorkspaceSettings(Preferences).LastWorkspacePath);
        Assert.Equal("Ave", app.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    private static void AssertWorkspaceBytes(string workspace, Dictionary<string, byte[]> expected)
    {
        var actual = WorkspaceFixtures.Bytes(workspace);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, actual[path]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
