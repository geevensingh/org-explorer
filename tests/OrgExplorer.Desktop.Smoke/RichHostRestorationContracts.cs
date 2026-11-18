using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyRichHostRestoration(Type windowType, string root)
    {
        var path = Path.Combine(root, "workspace");
        var transport = Path.Combine(root, "transport");
        var settings = Path.Combine(root, "remembered-settings");
        var previousSettings = Environment.GetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY");
        Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", settings);
        try
        {
            Guid note;
            using (var seed = ApplicationWorkflow.CreateFileBacked())
            {
                if (!seed.OpenWorkspace(path) || !seed.ApplyImport(seed.PreviewImport(RecoveryPeople).Id))
                    throw new InvalidOperationException(seed.Status);
                note = seed.CreateTeamNote("morgan@example.com", "Remembered context", "Exact saved context")!.Id;
                Directory.CreateDirectory(transport);
                if (!seed.ConfigureSynchronizationFolder(transport) || !seed.SyncNow())
                    throw new InvalidOperationException(seed.Status);
                var preferences = new WorkspaceSettings(Path.Combine(settings, "workspace-settings.json"));
                if (!preferences.OpenWorkspace(seed, path, WorkspaceOpenMode.ExistingOnly) ||
                    !preferences.SetLastView(path, new("morgan@example.com", SearchSource.OwnedTeamNote,
                        note, "morgan@example.com")))
                    throw new InvalidOperationException(preferences.PreferenceError ?? seed.Status);
            }

            void Reopen(string expectedEmail, bool selectReport, bool automatic)
            {
                var expected = ReadSmokeWorkspacePreferences();
                using var adoption = new SnapshotAdoptionCommitControl();
                var observation = new RestorationObserver(adoption);
                using var store = ApplicationWorkflow.CreateFileBacked(fileObserver: observation);
                var clock = new SnapshotClock();
                var window = automatic ? CreateAutomaticSnapshotWindow(windowType, store, path, clock) :
                    CreateHeldSnapshotWindow(windowType, store, path);
                T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
                var closed = false;
                window.Closed += (_, _) => closed = true;
                try
                {
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    PumpUntil(() => model.IsWorkspaceCurrent && AutomationProperties.GetItemStatus(window) == "Ready" &&
                        Control<Button>("OpenWorkspaceButton").IsEnabled);
                    WorkspaceRestoration.Observation Observe() => new(
                        AutomationProperties.GetHelpText(Control<TextBlock>("WorkspaceName")),
                        AutomationProperties.GetItemStatus(window),
                        AutomationProperties.GetItemStatus(Control<TextBlock>("WorkspaceName")),
                        Control<TextBlock>("Email").Text,
                        Control<FrameworkElement>("ViewPreferenceStatus").Visibility != Visibility.Visible &&
                            Control<FrameworkElement>("WorkspaceStartupPanel").Visibility != Visibility.Visible);
                    WorkspaceRestoration.AssertRemembered(Observe, path, expected, PumpUntil);
                    if (Observe().Email != expectedEmail)
                        throw new InvalidOperationException("The actual rich host restored the wrong independently remembered Person.");
                    if (automatic)
                    {
                        var before = observation.LocalReads;
                        clock.Advance(TimeSpan.FromSeconds(60));
                        PumpUntil(() => observation.LocalReads > before && !model.IsSynchronizing &&
                            Control<Button>("SyncNowButton").IsEnabled);
                    }
                    if (selectReport)
                    {
                        if (model.TeamNotesView.SelectedNote?.Id != note ||
                            model.TeamNotesView.SelectedNote.Markdown != "Exact saved context" ||
                            model.TeamNotesView.IsEditing)
                            throw new InvalidOperationException("Rich startup did not restore its exact saved Team-note reading.");
                        var report = model.ReportingRoots.Single().Reports.Single();
                        Control<TreeView>("PeopleList").RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
                            null!, report, TreeView.SelectedItemChangedEvent));
                        PumpUntil(() => Control<TextBlock>("Email").Text == "avery@example.com" &&
                            ReadSmokeWorkspacePreferences().GetLastView(path)?.PersonEmail == "avery@example.com");
                    }
                    else
                    {
                        Control<TextBox>("SearchQuery").Text = "avery@example.com";
                        PumpUntil(() => Control<TextBlock>("SearchCount").Text.Contains("People found: 1 "));
                    }
                    if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                        throw new InvalidOperationException("Rich host restoration must not create a native window.");
                    window.Close();
                    PumpUntil(() => closed);
                }
                finally { if (!closed) ClosePublishedCompiledWindow(window); }
            }

            foreach (var automatic in new[] { true, false })
            {
                var preferences = ReadSmokeWorkspacePreferences();
                if (!preferences.SetLastView(path, new("morgan@example.com", SearchSource.OwnedTeamNote,
                    note, "morgan@example.com")))
                    throw new InvalidOperationException(preferences.PreferenceError);
                Reopen("morgan@example.com", selectReport: true, automatic);
                Reopen("avery@example.com", selectReport: false, automatic);
                Console.WriteLine("PASS: actual " + (automatic ? "automatic rich injected-clock" : "held-adoption") +
                    " factory preserves Loaded/Ready/Open readiness, remembered exact Read note/Person and normal Closed without a native window.");
            }
            using (var adoption = new SnapshotAdoptionCommitControl())
            {
                var host = LaunchHeldSnapshotFixture(Path.ChangeExtension(windowType.Assembly.Location, ".exe"),
                    path, adoption, startupOnly: true);
                if (!host.WaitForExit(20_000)) throw new TimeoutException("The owned no-window WPF host did not finish its existing startup/normal-close observation.");
                var error = host.StandardError.ReadToEnd();
                var result = host.StandardOutput.ReadToEnd();
                if (host.ExitCode != 0) throw new InvalidOperationException("Owned no-window host startup failed: " + error);
                using var observed = System.Text.Json.JsonDocument.Parse(result);
                if (observed.RootElement.GetProperty("processId").GetInt32() != host.Id ||
                    !observed.RootElement.GetProperty("noNativeWindow").GetBoolean() ||
                    observed.RootElement.GetProperty("remembered").GetProperty("PersonEmail").GetString() != "avery@example.com")
                    throw new InvalidOperationException("The actual owned host did not independently verify its process and remembered Person.");
                Console.WriteLine("OWNED-ADOPTION-HOST-STARTUP " + result.Trim());
            }
            using var reading = OpenSmokeWorkspace(path);
            if (reading.BeginNoteDraft(NoteScope.Team, "morgan@example.com", note)?.Markdown != "Exact saved context")
                throw new InvalidOperationException("Remembered startup or selection changed saved Team writing.");
            Console.WriteLine("PASS: actual controlled-host construction and Loaded restore independently remembered Person/note, then the next saved Person; current grouped search and real normal Closed remain intact without a native window.");
        }
        finally { Environment.SetEnvironmentVariable("ORG_EXPLORER_SETTINGS_DIRECTORY", previousSettings); }
    }

    private sealed class RestorationObserver(SnapshotAdoptionCommitControl adoption) : IWorkspaceFileObserver
    {
        internal int LocalReads;
        public void Observe(WorkspaceFileEffect effect) => adoption.Observe(effect);
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name == "snapshot.local-read" && phase.Kind == WorkspaceStoragePhaseKind.Begin)
                Interlocked.Increment(ref LocalReads);
            adoption.ObservePhase(phase);
        }
    }
}
