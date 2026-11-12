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
    public void Pane_percentages_reopen_per_Workspace_and_survive_interleaved_ongoing_and_startup_updates()
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        WorkspaceFixtures.CopyVersionOne(first);
        WorkspaceFixtures.CopyVersionOne(second);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, first));
        var note = app.CreatePersonNote("avery@example.com", "Continuing", "Unchanged writing")!;
        var before = WorkspaceFixtures.Bytes(first);
        Assert.True(app.SavePaneProportions(28, 38));
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, note.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, note.Id));
        Assert.True(settings.OpenWorkspace(app, second));
        Assert.Equal(new PaneProportions(25, 35), app.PaneProportions);
        Assert.True(app.SavePaneProportions(31, 40));
        Assert.True(settings.RestoreWorkspace(app, first));
        Assert.Equal(new PaneProportions(28, 38), app.PaneProportions);
        Assert.True(app.SavePaneProportions(27, 37));
        var reopened = new WorkspaceSettings(Preferences);
        Assert.Equal(new PaneProportions(27, 37), reopened.GetPaneProportions(first.ToUpperInvariant() + "\\"));
        Assert.Equal(new PaneProportions(31, 40), reopened.GetPaneProportions(second));
        Assert.Equal(note.Id, reopened.GetOngoingNote(first, NoteScope.Person, note.OwnerEmail));
        Assert.Equal(Path.GetFullPath(first), reopened.LastWorkspacePath);
        AssertWorkspaceBytes(first, before);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("corrupt")]
    [InlineData("version")]
    [InlineData("percent")]
    [InlineData("missing-percent")]
    public void Rejected_pane_preferences_retain_durable_ratios_and_other_entries_until_explicit_retry(string failure)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        Assert.True(app.SavePaneProportions(28, 38));
        var valid = File.ReadAllBytes(Preferences);
        var before = WorkspaceFixtures.Bytes(workspace);
        if (failure != "locked")
        {
            var json = JsonNode.Parse(valid)!;
            if (failure == "version") json["schemaVersion"] = 99;
            if (failure == "percent") json["workspaces"]![0]!["paneProportions"]!["peoplePercent"] = 100;
            if (failure == "missing-percent")
                json["workspaces"]![0]!["paneProportions"]!.AsObject().Remove("profilePercent");
            File.WriteAllText(Preferences, failure == "corrupt" ? "{broken" : json.ToJsonString());
            var invalid = new WorkspaceSettings(Preferences);
            Assert.Contains("pane proportions", invalid.PreferenceError);
            Assert.Null(invalid.GetPaneProportions(workspace));
        }
        var rejected = File.ReadAllBytes(Preferences);
        using (var held = failure == "locked" ? File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            Assert.False(app.SavePaneProportions(30, 40));
            Assert.Contains("NOT saved", app.LayoutPreferenceFeedback);
            Assert.Contains("retry", app.LayoutPreferenceFeedback);
            Assert.Equal(new PaneProportions(28, 38), app.PaneProportions);
            Assert.Equal(rejected, File.ReadAllBytes(Preferences));
            AssertWorkspaceBytes(workspace, before);
        }
        File.WriteAllBytes(Preferences, valid);
        Assert.True(app.SavePaneProportions(30, 40));
        Assert.Null(app.LayoutPreferenceFeedback);
        Assert.Equal(new PaneProportions(30, 40), new WorkspaceSettings(Preferences).GetPaneProportions(workspace));
        Assert.False(app.SavePaneProportions(double.NaN, 35));
        Assert.Equal(new PaneProportions(30, 40), new WorkspaceSettings(Preferences).GetPaneProportions(workspace));
    }

    [Fact]
    public void Unreadable_preferences_keep_manual_Workspace_access_without_claiming_an_ongoing_choice_was_restored()
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using (var seed = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.OpenWorkspace(seed, workspace));
            var first = seed.CreatePersonNote("avery@example.com", "First", "Saved")!;
            seed.CreatePersonNote("avery@example.com", "Other", "");
            Assert.True(seed.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
            Assert.True(seed.DesignateOngoingNote(NoteScope.Person, first.Id));
        }
        var preference = File.ReadAllBytes(Preferences);
        var notes = WorkspaceFixtures.Bytes(workspace);
        using (File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var unreadable = new WorkspaceSettings(Preferences);
            Assert.Contains("could not be read", unreadable.PreferenceError);
            Assert.Null(unreadable.LastWorkspacePath);
            using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), unreadable);
            Assert.False(unreadable.RestoreWorkspace(app));
            Assert.True(unreadable.OpenWorkspace(app, workspace));
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
            Assert.Null(app.PersonNotesView.OngoingNoteId);
            Assert.Null(app.PersonNotesView.SelectedNote);
            Assert.Contains("NOT saved", app.PersonNotesView.Feedback);
            Assert.True(app.OpenOwnedNote(NoteScope.Person, app.PersonNotesView.Notes[0].Id));
        }
        Assert.Equal(preference, File.ReadAllBytes(Preferences));
        AssertWorkspaceBytes(workspace, notes);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("corrupt")]
    [InlineData("version")]
    [InlineData("invalid-entry")]
    [InlineData("owner")]
    public void Failed_designation_keeps_the_previous_durable_choice_and_note_bytes_with_actionable_feedback(string failure)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        var first = app.CreatePersonNote("avery@example.com", "Continuing", "Saved")!;
        var other = app.CreatePersonNote("avery@example.com", "Other", "")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, first.Id));
        Assert.True(app.OpenOwnedNote(NoteScope.Person, other.Id));
        var valid = File.ReadAllBytes(Preferences);
        var workspaceBytes = WorkspaceFixtures.Bytes(workspace);
        if (failure != "locked")
        {
            var json = JsonNode.Parse(valid)!;
            if (failure == "version") json["schemaVersion"] = 99;
            if (failure == "invalid-entry") json["workspaces"]![0]!["ongoingNotes"]![0]!["noteId"] = Guid.Empty.ToString();
            if (failure == "owner") json["workspaces"]![0]!["ongoingNotes"]![0]!["ownerEmail"] = "Someone <avery@example.com>";
            File.WriteAllText(Preferences, failure == "corrupt" ? "{broken" : json.ToJsonString());
        }
        var rejected = File.ReadAllBytes(Preferences);
        using (var held = failure == "locked" ? File.Open(Preferences, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            Assert.False(app.DesignateOngoingNote(NoteScope.Person, other.Id));
            Assert.Equal(other.Id, app.PersonNotesView.SelectedNote!.Id);
            Assert.Equal(first.Id, app.PersonNotesView.OngoingNoteId);
            Assert.Contains("NOT saved", app.PersonNotesView.Feedback);
            Assert.Equal(rejected, File.ReadAllBytes(Preferences));
            AssertWorkspaceBytes(workspace, workspaceBytes);
        }
        File.WriteAllBytes(Preferences, valid);
        using (var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(Preferences)))
        {
            Assert.True(reopened.OpenWorkspace(workspace));
            Assert.True(reopened.OpenOwnedNotes(NoteScope.Person, first.OwnerEmail));
            Assert.Equal(first.Id, reopened.PersonNotesView.SelectedNote!.Id);
        }
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, other.Id), app.PersonNotesView.Feedback);
        Assert.Equal(other.Id, new WorkspaceSettings(Preferences).GetOngoingNote(workspace, NoteScope.Person, first.OwnerEmail));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unavailable_ongoing_choice_is_retained_without_selecting_or_creating_replacement_writing(bool archived)
    {
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyVersionOne(workspace);
        var settings = new WorkspaceSettings(Preferences);
        using var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings);
        Assert.True(settings.OpenWorkspace(app, workspace));
        var note = app.CreatePersonNote("avery@example.com", "Continuing", "Retained")!;
        var other = app.CreatePersonNote("avery@example.com", "Other", "")!;
        Assert.True(app.OpenOwnedNotes(NoteScope.Person, note.OwnerEmail));
        Assert.True(app.DesignateOngoingNote(NoteScope.Person, note.Id));
        var preference = File.ReadAllBytes(Preferences);
        if (archived) Assert.True(app.ArchivePersonNote(note.Id));
        else
        {
            File.Delete(Path.Combine(workspace, "notes", note.Id + ".md"));
            Assert.True(app.RefreshPersonNotes(), app.Status);
        }
        Assert.Null(app.PersonNotesView.SelectedNote);
        Assert.Contains("unavailable or archived", app.PersonNotesView.Feedback);
        Assert.Equal(note.Id, settings.GetOngoingNote(workspace, NoteScope.Person, note.OwnerEmail));
        Assert.Equal(preference, File.ReadAllBytes(Preferences));
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), new WorkspaceSettings(Preferences));
        Assert.True(reopened.OpenWorkspace(workspace), reopened.Status);
        Assert.True(reopened.OpenOwnedNotes(NoteScope.Person, note.OwnerEmail));
        Assert.Null(reopened.PersonNotesView.SelectedNote);
        Assert.Contains("unavailable or archived", reopened.PersonNotesView.Feedback);
        Assert.True(reopened.OpenOwnedNote(NoteScope.Person, other.Id));
        Assert.False(reopened.PersonNotesView.IsOngoing);
        if (archived)
        {
            Assert.True(reopened.OpenOwnedNote(NoteScope.Person, note.Id));
            Assert.True(reopened.PersonNotesView.SelectedNote!.Archived);
            Assert.True(reopened.ArchivePersonNote(note.Id, false));
            Assert.True(reopened.OpenOngoingNote(NoteScope.Person));
            Assert.Equal(note.Id, reopened.PersonNotesView.SelectedNote!.Id);
        }
        Assert.True(reopened.DesignateOngoingNote(NoteScope.Person, other.Id));
        Assert.Equal(other, Assert.Single(reopened.GetPersonNotes(note.OwnerEmail), n => n.Id == other.Id));
    }

    [Fact]
    public void Ongoing_designation_extends_legacy_preferences_and_reopens_by_identity_without_changing_Workspace_writing()
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        WorkspaceFixtures.CopyVersionOne(first);
        WorkspaceFixtures.CopyVersionOne(second);
        Directory.CreateDirectory(Path.GetDirectoryName(Preferences)!);
        File.WriteAllText(Preferences, new JsonObject { ["schemaVersion"] = 1, ["lastWorkspacePath"] = first }.ToJsonString());
        var settings = new WorkspaceSettings(Preferences);
        Guid ongoing;
        using (var app = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), settings))
        {
            Assert.True(settings.RestoreWorkspace(app), app.Status);
            var note = app.CreatePersonNote("avery@example.com", "Ongoing", "Retained **writing**")!;
            var other = app.CreatePersonNote("avery@example.com", "Other", "")!;
            ongoing = note.Id;
            var before = WorkspaceFixtures.Bytes(first);
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
            Assert.True(app.DesignateOngoingNote(NoteScope.Person, note.Id), app.PersonNotesView.Feedback);
            AssertWorkspaceBytes(first, before);
            Assert.True(settings.OpenWorkspace(app, second), app.Status);
            var secondNote = app.CreatePersonNote("avery@example.com", "Different Workspace", "")!;
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
            Assert.True(app.DesignateOngoingNote(NoteScope.Person, secondNote.Id));
            Assert.True(settings.RestoreWorkspace(app, first), app.Status);
            Assert.True(app.OpenOwnedNotes(NoteScope.Person, "AVERY@example.com"));
            Assert.Equal(note.Id, app.PersonNotesView.SelectedNote!.Id);
            Assert.True(app.EditPersonNote(note.Id, "Renamed without re-designation", note.Markdown));
            Assert.Equal(other, Assert.Single(app.GetPersonNotes("avery@example.com"), n => n.Id == other.Id));
        }
        var reopenedSettings = new WorkspaceSettings(Preferences);
        using var reopened = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked(), reopenedSettings);
        Assert.True(reopenedSettings.RestoreWorkspace(reopened), reopened.Status);
        Assert.True(reopened.OpenOwnedNotes(NoteScope.Person, "avery@example.com"));
        Assert.Equal(ongoing, reopened.PersonNotesView.SelectedNote!.Id);
        Assert.Equal("Renamed without re-designation", reopened.PersonNotesView.SelectedNote.Title);
        Assert.Equal(ongoing, reopenedSettings.GetOngoingNote(first.ToUpperInvariant() + Path.DirectorySeparatorChar,
            NoteScope.Person, "AVERY@example.com"));
        Assert.Null(reopenedSettings.GetOngoingNote(first, NoteScope.Team, "avery@example.com"));
        Assert.Null(reopenedSettings.GetOngoingNote(first, NoteScope.Person, "casey@example.com"));
    }

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
            WorkspaceFixtures.CopyVersionOne(workspace);
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
        WorkspaceFixtures.CopyVersionOne(workspace);
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
        WorkspaceFixtures.CopyVersionOne(workspace);
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
            WorkspaceFixtures.CopyVersionOne(alternate);
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
        Assert.Contains("NOT saved", settings.PreferenceError);
        Assert.Equal(corrupt, File.ReadAllText(Preferences));
        File.Delete(Preferences);
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
            WorkspaceFixtures.CopyVersionOne(restartWorkspace);
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
        WorkspaceFixtures.CopyVersionOne(first);
        WorkspaceFixtures.CopyVersionOne(alternate);
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
