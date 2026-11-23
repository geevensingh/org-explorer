using System.IO;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static int VerifyUiScaleLogicalHandoff(string executable)
    {
        var root = Path.GetFullPath(Path.Combine(".ci-results", "ui-split-validation",
            "logical-handoff-" + Guid.NewGuid().ToString("N")));
        using var source = new ScaleFixture(Path.Combine(root, "source"));
        using var receiver = new ScaleFixture(Path.Combine(root, "receiver"));
        source.Build();
        var preferencesPath = Path.Combine(root, "source-settings", "workspace-settings.json");
        using (var app = ApplicationWorkflow.CreateFileBacked())
        {
            if (!app.OpenWorkspace(source.Workspace, WorkspaceOpenMode.ExistingOnly))
                throw new InvalidOperationException(app.Status);
            for (var i = 0; i < 2; i++)
                ScaleFixture.Check(app.CreatePersonNote(ScaleFixture.Email(11), $"Contract Person {i}", $"# Actual GUID {i}") is not null, app.Status);
            for (var i = 0; i < 40; i++)
                ScaleFixture.Check(app.CreateTeamNote(ScaleFixture.Email(11), $"Contract Team {i}", $"# Actual retained Team {i}") is not null, app.Status);
            ScaleFixture.Check(app.EditPersonText(ScaleFixture.Target, PersonTextField.OfficeNumber, "0009U") && app.SyncNow(), app.Status);
            ScaleFixture.RequireRichPopulation(app, additionalPersonNotes: 2, additionalTeamNotes: 40);
            var selected = app.GetTeamNotes(ScaleFixture.Email(11)).Last();
            var settings = new WorkspaceSettings(preferencesPath);
            ScaleFixture.Check(settings.OpenWorkspace(app, source.Workspace) &&
                settings.SetLastView(source.Workspace, new(ScaleFixture.Target, SearchSource.InheritedTeamNote,
                    selected.Id, ScaleFixture.Email(11))) &&
                settings.SetOngoingNote(source.Workspace, NoteScope.Team, selected.OwnerEmail, selected.Id) &&
                settings.SetPaneProportions(source.Workspace, new(30, 70)), settings.PreferenceError ?? app.Status);
        }
        var binding = ReadScaleBinding(executable);
        var bundle = Path.Combine(root, "handoff");
        UiScaleHandoff.Capture(bundle, source.Workspace, source.SynchronizationFolder, preferencesPath, binding,
            "run-" + Guid.NewGuid().ToString("N"));
        Directory.Delete(source.Root, true);
        UiScaleHandoff.Receive(bundle, receiver.Workspace, receiver.SynchronizationFolder,
            Path.Combine(root, "receiver-settings", "workspace-settings.json"), binding, Path.Combine(root, "handoff-received.json"));
        using (var reopened = ApplicationWorkflow.CreateFileBacked())
        {
            ScaleFixture.Check(reopened.OpenWorkspace(receiver.Workspace, WorkspaceOpenMode.ExistingOnly), reopened.Status);
            ScaleFixture.RequireRichPopulation(reopened, additionalPersonNotes: 2, additionalTeamNotes: 40);
        }
        Directory.Delete(receiver.Root, true);
        Console.WriteLine("PASS: no-window actual full-rich published logical snapshot, original physical store removed, public Join and exact identity/notes/photos/history/preferences independently reopened.");
        Console.WriteLine("Retained contract handoff (not native acceptance): " + bundle);
        return 0;
    }
}
