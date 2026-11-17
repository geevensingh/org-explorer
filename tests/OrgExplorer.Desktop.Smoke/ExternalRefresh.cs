using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static IApplicationWorkflow OpenSmokeWorkspace(string folder)
    {
        var workflow = ApplicationWorkflow.CreateFileBacked();
        if (workflow.OpenWorkspace(folder, WorkspaceOpenMode.ExistingOnly)) return workflow;
        var status = workflow.Status;
        workflow.Dispose();
        throw new InvalidOperationException(status);
    }

    private static PersonNote ReadSmokePersonNote(string folder, Guid id)
    {
        using var workflow = OpenSmokeWorkspace(folder);
        return workflow.People.SelectMany(person => workflow.GetPersonNotes(person.Email, includeArchived: true))
            .Single(note => note.Id == id);
    }

    private static TeamNote ReadSmokeTeamNote(string folder, Guid id)
    {
        using var workflow = OpenSmokeWorkspace(folder);
        return workflow.People.SelectMany(person => workflow.GetTeamNotes(person.Email, includeArchived: true))
            .Single(note => note.Id == id);
    }

    private static Guid SmokeNoteId(string folder, string owner, string title, bool team = false)
    {
        using var workflow = OpenSmokeWorkspace(folder);
        return team ? workflow.GetTeamNotes(owner, includeArchived: true).Single(note => note.Title == title).Id :
            workflow.GetPersonNotes(owner, includeArchived: true).Single(note => note.Title == title).Id;
    }

    private static string SmokeSnapshotFolder(string workspace) =>
        Path.Combine(Path.GetDirectoryName(workspace)!, Path.GetFileName(workspace) + "-snapshots");

    private static void ConfigureSmokeSnapshotFolder(string workspace, Action<string> chooseFolder)
    {
        var folder = SmokeSnapshotFolder(workspace);
        Directory.CreateDirectory(folder);
        chooseFolder(folder);
    }

    private static void ClosePublishedWorkspace(Process process, string workspace)
    {
        var window = Window(process);
        PrepareSmokeNormalClose(workspace, () => PublishSmokeWorkspace(window, workspace));
        Close(process);
    }

    private static void PublishSmokeWorkspace(AutomationElement window, string workspace, string? synchronizationFolder = null)
    {
        PublishSmokeWorkspace(workspace, () =>
        {
            SetSettingsVisible(window, true);
            var transport = synchronizationFolder ?? SmokeSnapshotFolder(workspace);
            if (Find(window, "SynchronizationFolder").Current.Name != transport)
            {
                Directory.CreateDirectory(transport);
                PickWorkspaceFolder(window, transport, "ConfigureSynchronizationFolder");
            }
            Await(() => Find(window, "SynchronizationFolder").Current.Name == transport);
            Click(window, "SyncNow");
            Await(() => Find(window, "SyncNow").Current.IsEnabled &&
                Find(window, "SynchronizationStatus").Current.Name.Contains("Locally saved and published.", StringComparison.Ordinal));
        });
        SetSettingsVisible(window, false);
    }

    private static void PublishSmokeWorkspace(string workspace, Action synchronize)
    {
        Guid? identity;
        using (var before = OpenSmokeWorkspace(workspace))
            identity = before.Synchronization.WorkspaceId;
        synchronize();
        using var published = OpenSmokeWorkspace(workspace);
        if (published.Synchronization.WorkspaceId != identity ||
            published.Synchronization.PublishedRevision != published.Synchronization.LocalRevision ||
            published.Synchronization.HasUnpublishedChanges)
            throw new InvalidOperationException("Synchronization did not publish the exact independently reopened current revision.");
    }

    private static void PrepareSmokeNormalClose(string workspace, Action publish)
    {
        Guid? identity, revision;
        using (var before = OpenSmokeWorkspace(workspace))
        {
            identity = before.Synchronization.WorkspaceId;
            revision = before.Synchronization.LocalRevision;
        }
        publish();
        using (var published = OpenSmokeWorkspace(workspace))
            if (published.Synchronization.WorkspaceId != identity || published.Synchronization.LocalRevision != revision ||
                published.Synchronization.PublishedRevision != revision || published.Synchronization.HasUnpublishedChanges)
                throw new InvalidOperationException("Normal-close preparation changed or failed to publish the exact independently reopened local revision.");
    }

    private static void ReconcileSmokeLocalSaves(AutomationElement window)
    {
        Click(window, "NoticeRetry");
        Await(() => !Activity(window).Contains("Editing is paused", StringComparison.Ordinal) &&
            !Activity(window).Contains("Reconciling", StringComparison.Ordinal));
    }

    private static bool IsSmokeRejectedSave(string feedback, string localState) =>
        localState == "Local save: Failed; outcome: NotCommitted" &&
        feedback.Contains("NOT saved", StringComparison.Ordinal);

    private static bool IsSmokeImportDiscardComplete(bool discardVisible, bool applyEnabled) =>
        !discardVisible && !applyEnabled;

    private static bool SmokeImportDiscardComplete(AutomationElement window)
    {
        var discard = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "DiscardImport"));
        var apply = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ApplyImport"));
        return IsSmokeImportDiscardComplete(discard is not null && !discard.Current.IsOffscreen,
            apply is not null && apply.Current.IsEnabled && !apply.Current.IsOffscreen);
    }

    private sealed class SmokeSnapshotPeer : IDisposable
    {
        internal IApplicationWorkflow Workflow { get; }
        internal string Transport { get; }

        internal SmokeSnapshotPeer(string workspace)
        {
            Transport = SmokeSnapshotFolder(workspace);
            using var source = OpenSmokeWorkspace(workspace);
            Workflow = ApplicationWorkflow.CreateFileBacked();
            var peer = Path.Combine(Path.GetDirectoryName(workspace)!, Path.GetFileName(workspace) + "-peer-" + Guid.NewGuid().ToString("N"));
            if (!Workflow.OpenWorkspace(peer))
                throw new InvalidOperationException(Workflow.Status);
            WorkspaceFixtures.JoinSnapshot(source, Workflow, Transport);
        }

        internal void Publish(Func<IApplicationWorkflow, bool> change)
        {
            if (!change(Workflow) || !Workflow.SyncNow())
                throw new InvalidOperationException(Workflow.Status + "\n" + Workflow.Synchronization.Summary);
        }

        public void Dispose() => Workflow.Dispose();
    }

    private static void RunExternalRefreshFlow(string executable, string root)
    {
        var folder = Path.Combine(root, "external-workspace");
        var photo = Path.Combine(root, "external.png");
        CreatePhotoFixture(photo);
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"80000000-0000-4000-8000-000000000001",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"casey@example.com"},
                 {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null}]}
                """).Id) || !seed.EditPhoto("avery@example.com", photo) ||
                seed.CreatePersonNote("avery@example.com", "Private context", "# Saved context") is null ||
                seed.CreateTeamNote("avery@example.com", "Owned context", "# Saved owned Team context") is null ||
                seed.CreateTeamNote("casey@example.com", "Manager context", "# Saved inherited Team context") is null)
                throw new InvalidOperationException(seed.Status);
        using var peer = new SmokeSnapshotPeer(folder);
        var personId = SmokeNoteId(folder, "avery@example.com", "Private context");
        var ownedId = SmokeNoteId(folder, "avery@example.com", "Owned context", team: true);
        var inheritedId = SmokeNoteId(folder, "casey@example.com", "Manager context", team: true);
        using var process = Launch(executable);
        try
        {
            var window = Window(process);
            SetSettingsVisible(window, true);
            PickWorkspaceFolder(window, folder);
            AwaitRestoredWorkspace(window, folder);
            OpenPerson(window, "casey@example.com");
            AssertPhotoVisible(window);
            SelectNote(window, "Private context");
            peer.Publish(source => source.EditPreferredName("avery@example.com", "External Avery") &&
                source.EditPersonNote(personId, "Private context", "# External context") &&
                source.EditTeamNote(ownedId, "Owned context", "# External owned Team context") &&
                source.EditTeamNote(inheritedId, "Manager context", "# External inherited Team context") &&
                source.EditPersonText("casey@example.com", PersonTextField.JobTitle, "Changed manager role"));
            PublishSmokeWorkspace(window, folder);
            Await(() => Find(window, "ProfileHeading").Current.Name == "External Avery");
            AssertNoteView(window, "Person", editing: false);
            Await(() => ReadingText(window).Contains("External context"));
            SelectTeamNote(window, "Owned context");
            AssertNoteView(window, "Team", editing: false);
            Await(() => TeamReadingText(window).Contains("External owned Team context"));
            ExpandCurrentTeamContext(window);
            Await(() => Find(window, "InheritedTeamSummary").Current.Name.Contains("Needs review"));
            Await(() => CurrentReadingText(window).Contains("External inherited Team context"));

            var timestamp = File.GetLastWriteTimeUtc(photo);
            byte[] greenPixels = [0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255];
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, greenPixels, 8)));
            using (var file = File.Create(photo)) encoder.Save(file);
            File.SetLastWriteTimeUtc(photo, timestamp);
            peer.Publish(source => source.EditPhoto("avery@example.com", photo));
            PublishSmokeWorkspace(window, folder);
            var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
            if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            Await(() =>
            {
                var bounds = Find(window, "PersonPhoto").Current.BoundingRectangle;
                if (bounds.IsEmpty || Find(window, "PersonPhoto").Current.IsOffscreen) return false;
                var dc = GetDC(IntPtr.Zero);
                try
                {
                    var color = GetPixel(dc, (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                    return (color & 0xff) < 20 && ((color >> 8) & 0xff) > 240 && ((color >> 16) & 0xff) < 20;
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
            });

            var invalid = Path.Combine(peer.Transport, "invalid.oesnapshot");
            File.WriteAllText(invalid, "{invalid");
            SetSettingsVisible(window, true);
            Click(window, "SyncNow");
            Await(() => Find(window, "SynchronizationStatus").Current.Name.Contains("failed", StringComparison.OrdinalIgnoreCase));
            SetSettingsVisible(window, false);
            if (Find(window, "ProfileHeading").Current.Name != "External Avery" ||
                !Find(window, "NavigateManager").Current.Name.Contains("Casey Example") ||
                !ReadingText(window).Contains("External context") || !Find(window, "EditPreferredName").Current.IsEnabled ||
                ReadSmokePersonNote(folder, personId).Markdown != "# External context")
                throw new InvalidOperationException("Rejected snapshot delivery changed validated local reading or paused local editing.");
            File.Delete(invalid);
            SetSettingsVisible(window, true);
            Click(window, "SyncNow");
            Await(() => Find(window, "SynchronizationStatus").Current.Name.Contains("Locally saved and published."));
            SetSettingsVisible(window, false);
            if (!Find(window, "InheritedTeamSummary").Current.Name.Contains("Needs review"))
                throw new InvalidOperationException("Snapshot retry silently confirmed externally changed Team context.");
            foreach (var kind in new[] { "Person", "Team" })
            {
                SelectTab(window, $"{kind}NoteEditTab");
                Set(window, $"{kind}NoteTitle", "");
                Set(window, $"{kind}NoteMarkdown", $"Retained {kind} draft during snapshot delivery");
                Await(() => Find(window, $"{kind}NoteStatus").Current.Name.Contains("NOT saved"));
                var marker = "Delivered while retaining " + kind;
                peer.Publish(source => source.EditPreferredName("avery@example.com", marker));
                SetSettingsVisible(window, true);
                Click(window, "SyncNow");
                Await(() => Find(window, "SyncNow").Current.IsEnabled);
                SetSettingsVisible(window, false);
                AssertValue(window, $"{kind}NoteMarkdown", $"Retained {kind} draft during snapshot delivery");
                if (Find(window, "ProfileHeading").Current.Name == marker)
                    throw new InvalidOperationException("Incoming snapshot replaced a retained draft's base.");
                Click(window, $"Discard{kind}NoteDraft");
                SetSettingsVisible(window, true);
                Click(window, "SyncNow");
                Await(() => Find(window, "SyncNow").Current.IsEnabled);
                SetSettingsVisible(window, false);
                Await(() => Find(window, "ProfileHeading").Current.Name == marker);
            }
            if (ReadSmokePersonNote(folder, personId).Markdown != "# External context" ||
                ReadSmokeTeamNote(folder, ownedId).Markdown != "# External owned Team context" ||
                ReadSmokeTeamNote(folder, inheritedId).Markdown != "# External inherited Team context")
                throw new InvalidOperationException("Snapshot refresh/discard changed stable note identities or writing.");
            ClosePublishedWorkspace(process, folder);
            Console.WriteLine("PASS: complete snapshot delivery updates profile/notes/review/photo pixels, rejects invalid transport without losing local reading, and defers adoption until retained writing is deliberately discarded.");
        }
        finally { StopApplication(process); }
    }
}
