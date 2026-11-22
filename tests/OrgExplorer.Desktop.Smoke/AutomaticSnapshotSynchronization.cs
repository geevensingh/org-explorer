using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private sealed class AutomaticSnapshotPublicationGate(Func<bool> hold, Action entered, Func<bool> released,
        Action? ended = null)
        : IWorkspaceFilePublisher
    {
        public void Publish(string staged, string destination, string? backup,
            WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
        {
            if (!destination.EndsWith(".oesnapshot", StringComparison.Ordinal) || !hold())
            {
                new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
                return;
            }
            entered();
            try
            {
                if (!released()) throw new IOException("Owned automatic snapshot publication was not released.");
                new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
            }
            finally { ended?.Invoke(); }
        }
    }

    private static int RunAutomaticSnapshotFixture(string[] args)
    {
        if (args.Length != 8 && !(args.Length == 11 && args[0] == "--rich-scale-snapshot-fixture") ||
            !Path.GetFullPath(args[2]).Contains(
            Path.DirectorySeparatorChar + ".test-workspaces" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The automatic snapshot host requires its owned isolated test Workspace.");
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var advance = EventWaitHandle.OpenExisting(args[3]);
        using var hold = EventWaitHandle.OpenExisting(args[4]);
        using var entered = EventWaitHandle.OpenExisting(args[5]);
        using var release = EventWaitHandle.OpenExisting(args[6]);
        using var ended = EventWaitHandle.OpenExisting(args[7]);
        using var profileHold = args.Length == 11 ? EventWaitHandle.OpenExisting(args[8]) : null;
        using var profileEntered = args.Length == 11 ? EventWaitHandle.OpenExisting(args[9]) : null;
        using var profileRelease = args.Length == 11 ? EventWaitHandle.OpenExisting(args[10]) : null;
        var clock = new SnapshotClock();
        var registration = ThreadPool.RegisterWaitForSingleObject(advance,
            (_, _) => clock.Advance(TimeSpan.FromSeconds(60)), null, Timeout.Infinite, false);
        var assemblyPath = Path.ChangeExtension(Path.GetFullPath(args[1]), ".dll");
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
            resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var publisher = new AutomaticSnapshotPublicationGate(() => hold.WaitOne(0), () => entered.Set(),
                () => WaitForAutomaticSnapshotRelease(release, args[0] == "--rich-scale-snapshot-fixture"),
                () => ended.Set());
            var window = CreateAutomaticSnapshotWindow(assembly.GetType("OrgExplorer.Desktop.MainWindow", true)!,
                ApplicationWorkflow.CreateFileBacked(filePublisher: publisher,
                    fileObserver: profileHold is null ? null : new RichScaleCommitBoundary(profileHold, profileEntered!, profileRelease!)),
                args[2], clock);
            application.Run(window);
            return 0;
        }
        finally
        {
            registration.Unregister(null);
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
    }

    private static bool WaitForAutomaticSnapshotRelease(EventWaitHandle release, bool rich)
    {
        if (!rich) return release.WaitOne(TimeSpan.FromSeconds(30));
        if (!DateTimeOffset.TryParseExact(Environment.GetEnvironmentVariable("SCALE_DEADLINE_AT"),
            "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var deadline))
            throw new FormatException("The rich snapshot host requires its original whole-suite deadline.");
        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero && release.WaitOne(remaining);
    }

    private static Window CreateAutomaticSnapshotWindow(Type windowType, IApplicationWorkflow workflow,
        string workspace, TimeProvider clock) =>
        (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [workflow, (Func<Window, string?>)(_ => workspace), (Action<string>)(_ => { }), clock], null)!;

    private static void RunHostedAutomaticSnapshots(string executable, string root)
    {
        var source = Path.Combine(root, "automatic-source");
        var target = Path.Combine(root, "automatic-target");
        var outgoing = Path.Combine(root, "automatic-out");
        var incoming = Path.Combine(root, "automatic-in");
        Directory.CreateDirectory(incoming);
        static void Deliver(string from, string to)
        {
            foreach (var file in Directory.GetFiles(from, "*.oesnapshot"))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        }
        using var first = ApplicationWorkflow.CreateFileBacked();
        if (!first.OpenWorkspace(source, WorkspaceOpenMode.OpenOrCreate) ||
            !first.ApplyImport(first.PreviewImport(RecoveryPeople).Id) ||
            !first.ConfigureSynchronizationFolder(outgoing) || !first.SyncNow())
            throw new InvalidOperationException(first.Status);
        Deliver(outgoing, incoming);
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!seed.OpenWorkspace(target, WorkspaceOpenMode.OpenOrCreate) ||
                !seed.ConfigureSynchronizationFolder(incoming, true) || !seed.SyncNow())
                throw new InvalidOperationException(seed.Status);
        var settings = Path.Combine(root, "automatic-settings");
        var preferences = new WorkspaceSettings(Path.Combine(settings, "workspace-settings.json"));
        using (var seed = ApplicationWorkflow.CreateFileBacked())
            if (!preferences.OpenWorkspace(seed, target, WorkspaceOpenMode.ExistingOnly) ||
                !preferences.SetLastView(target, new("morgan@example.com")))
                throw new InvalidOperationException(seed.Status);
        var token = Guid.NewGuid().ToString("N");
        var names = new[] { @"Local\OrgExplorer-auto-tick-" + token, @"Local\OrgExplorer-auto-hold-" + token,
            @"Local\OrgExplorer-auto-entered-" + token, @"Local\OrgExplorer-auto-release-" + token,
            @"Local\OrgExplorer-auto-ended-" + token };
        using var advance = new EventWaitHandle(false, EventResetMode.AutoReset, names[0]);
        using var hold = new EventWaitHandle(false, EventResetMode.ManualReset, names[1]);
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, names[2]);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, names[3]);
        using var ended = new EventWaitHandle(false, EventResetMode.ManualReset, names[4]);
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.Environment["ORG_EXPLORER_SETTINGS_DIRECTORY"] = settings;
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--automatic-snapshot-fixture",
            Path.GetFullPath(executable), target }.Concat(names)) start.ArgumentList.Add(argument);
        var productHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
        var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(executable, ".dll"))));
        var nativeEvidence = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-automatic-native-observation.json");
        var evidence = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-automatic-snapshots.json");
        using var process = Process.Start(start)!;
        AutomaticSnapshotNativeObservation? native = null;
        string? hostExecutable = null, hostHash = null;
        AutomaticHostLifecycle.Run(process, () =>
        {
            var window = Window(process);
            hostExecutable = process.MainModule!.FileName;
            hostHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hostExecutable)));
            AwaitRestoredWorkspace(window, target, "morgan@example.com");
            EnsurePeopleBrowsing(window);
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Automatic publication owner");
            Set(window, "TeamNoteMarkdown", "Before automatic export");
            var note = Guid.Parse(Find(window, "TeamNoteHeading").Current.HelpText);
            native = new(window, note, () => new(entered.WaitOne(0), ended.WaitOne(0), release.WaitOne(0)));
            bool ReopenedWriting(string text, bool published = false)
            {
                using var reading = ApplicationWorkflow.CreateFileBacked();
                return reading.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly) &&
                    reading.BeginNoteDraft(NoteScope.Team, "morgan@example.com", note)?.Markdown == text &&
                    (!published || !reading.Synchronization.HasUnpublishedChanges);
            }
            Await(() => ReopenedWriting("Before automatic export") && Activity(window) == "Ready");
            Guid? exported;
            using (var reading = ApplicationWorkflow.CreateFileBacked())
            {
                if (!reading.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly)) throw new InvalidOperationException(reading.Status);
                exported = reading.Synchronization.LocalRevision;
            }
            native.Record("preparation-started");
            native.RevealNote();
            SetSettingsVisible(window, true);
            var settingsEntry = Find(window, "OpenWorkspace");
            Await(() => settingsEntry.Current.HasKeyboardFocus);
            native.RevealPublication();
            native.Record("preparation-completed");
            hold.Set();
            var tickRequestedAt = Stopwatch.GetTimestamp();
            native.Record("tick-requested");
            advance.Set();
            if (!entered.WaitOne(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("The real periodic lifecycle did not reach actual snapshot publication.");
            native.Record("publisher-entered-observed");
            native.ObservePublishing();
            var exportingStatus = native.Publishing!.Status.Text;
            native.Capture("publishing", () => CaptureVisualEvidence(window, "sqlite-automatic-publishing"),
                native.ObservePublishing);
            SetSettingsVisible(window, false);
            EnsurePeopleBrowsing(window);
            native.RequireHeld("before-note-reveal");
            native.RevealNote();
            native.EnterWriting("Before automatic export", "Newer writing during automatic export");
            native.Select("avery@example.com", "Avery Example", () => SelectTreePerson(window, "avery@example.com"));
            native.Select("morgan@example.com", "Morgan Example", () => SelectTreePerson(window, "morgan@example.com"));
            Await(() => CurrentPersonEmail(window) == "morgan@example.com" &&
                ReopenedWriting("Newer writing during automatic export"));
            native.RequireHeld("before-returned-note-reveal");
            native.PrepareReturnedEditor();
            native.RevealNote();
            native.ObserveReturned("Newer writing during automatic export");
            if (release.WaitOne(0) || !hold.WaitOne(0))
                throw new InvalidOperationException("The same actual export was not held through native writing and browsing.");
            var continuedEditingAt = Stopwatch.GetTimestamp();
            native.Capture("continued-editing", () => CaptureVisualEvidence(window, "sqlite-automatic-export-continued-editing"),
                () => native.ObserveReturned("Newer writing during automatic export"));
            if (native.InputRequests != 1 || native.SelectionRequests != 2 || native.EditPreparationRequests != 1)
                throw new InvalidOperationException("Held export requires exactly one input, two Person selections and one explicit returned Edit preparation.");
            native.Record("release-requested");
            hold.Reset();
            release.Set();
            SetSettingsVisible(window, true);
            Await(() => Find(window, "SyncNow").Current.IsEnabled &&
                Find(window, "SynchronizationStatus").Current.Name.Contains("latest version is not confirmed published"));
            using (var reading = ApplicationWorkflow.CreateFileBacked())
                if (!reading.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly) ||
                    reading.Synchronization.PublishedRevision != exported || !reading.Synchronization.HasUnpublishedChanges)
                    throw new InvalidOperationException("An older automatic export falsely acknowledged newer writing.");
            if (!ended.WaitOne(0)) throw new InvalidOperationException("The actual publisher callback did not end after publication.");
            native.Record("publisher-ended-observed");
            advance.Set();
            Await(() => ReopenedWriting("Newer writing during automatic export", published: true) &&
                Find(window, "SyncNow").Current.IsEnabled &&
                Find(window, "SynchronizationStatus").Current.Name.Contains("delivery elsewhere is unknown"));
            Deliver(incoming, outgoing);
            if (!first.SyncNow() || !first.EditPreferredName("morgan@example.com", "Automatically refreshed Manager") || !first.SyncNow())
                throw new InvalidOperationException(first.Synchronization.Summary);
            Deliver(outgoing, incoming);
            SetSettingsVisible(window, false);
            advance.Set();
            Await(() => CurrentPersonEmail(window) == "morgan@example.com" &&
                Find(window, "ProfileHeading").Current.Name == "Automatically refreshed Manager");
            AssertValue(window, "TeamNoteMarkdown", "Newer writing during automatic export");
            CaptureVisualEvidence(window, "sqlite-automatic-incoming-refresh");
            SetSettingsVisible(window, true);
            Await(() => Find(window, "SyncNow").Current.IsEnabled &&
                Find(window, "SynchronizationStatus").Current.Name.Contains("Locally saved and published."));
            var providerAway = Path.Combine(root, "automatic-provider-unavailable");
            Directory.Move(incoming, providerAway);
            advance.Set();
            Await(() => Find(window, "SynchronizationStatus").Current.Name.Contains("Synchronization failed:") &&
                Find(window, "SyncNow").Current.IsEnabled);
            var unavailableStatus = Find(window, "SynchronizationStatus").Current.Name;
            CaptureVisualEvidence(window, "sqlite-automatic-folder-unavailable");
            SetSettingsVisible(window, false);
            Set(window, "TeamNoteMarkdown", "Locally durable while automatic synchronization is unavailable");
            Await(() => ReopenedWriting("Locally durable while automatic synchronization is unavailable") &&
                Activity(window) == "Ready");
            Directory.Move(providerAway, incoming);
            advance.Set();
            Await(() => ReopenedWriting("Locally durable while automatic synchronization is unavailable", published: true));
            using var latest = ApplicationWorkflow.CreateFileBacked();
            if (!latest.OpenWorkspace(target, WorkspaceOpenMode.ExistingOnly) ||
                latest.Synchronization.HasUnpublishedChanges || latest.Synchronization.Error is not null)
                throw new InvalidOperationException("The final automatic publication did not confirm the latest actual local revision.");
            using var product = File.OpenRead(executable);
            using var assembly = File.OpenRead(Path.ChangeExtension(executable, ".dll"));
            using var host = File.OpenRead(process.MainModule!.FileName);
            if (File.Exists(evidence)) throw new InvalidOperationException("Automatic snapshot evidence already exists.");
            File.WriteAllText(evidence, SnapshotNativeUsability.SerializeDiagnostic(new
            {
                schemaVersion = 2, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), processId = process.Id,
                productExecutable = Path.GetFullPath(executable),
                productExeSha256 = Convert.ToHexString(SHA256.HashData(product)),
                productAssemblySha256 = Convert.ToHexString(SHA256.HashData(assembly)),
                hostExecutableSha256 = Convert.ToHexString(SHA256.HashData(host)),
                nativeObservationFile = Path.GetFileName(nativeEvidence),
                nativeUsability = native.Diagnostic,
                scope = "Owned hosted process runs actual compiled MainWindow, real SQLite and real snapshot artifacts. Only the public TimeProvider cadence and external publication boundary are controlled. Not normal-EXE latency, provider delivery or rich 10,000-Person/background-export acceptance.",
                workload = new { people = 2, ownerNotesCreated = 1, richScale = false },
                cadence = "Each explicit clock advance is 60 seconds; no Sync now action requests these cycles.",
                timestampFrequency = Stopwatch.Frequency, tickRequestedAt, continuedEditingAt,
                timingBoundary = "Raw harness tick request through actual native writing/navigation and independent durable reopen while the same real export remains held. Not a latency percentile.",
                exportedRevision = exported, incomingRevision = first.Synchronization.LocalRevision,
                latestCommittedRevision = latest.Synchronization.LocalRevision,
                latestPublishedRevision = latest.Synchronization.PublishedRevision,
                workspaceId = first.Synchronization.WorkspaceId, owner = "morgan@example.com", noteId = note,
                exportingStatus, unavailableStatus,
                latestWriting = "Locally durable while automatic synchronization is unavailable",
                selectedPerson = CurrentPersonEmail(window), independentStores = new[] { source, target },
                artifacts = Directory.GetFiles(incoming, "*.oesnapshot").Select(path => new
                { name = Path.GetFileName(path), bytes = new FileInfo(path).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray(),
                providerDelivery = "Unknown; deterministic copying of actual complete snapshots controls only external transport.",
                captures = new[] { "sqlite-automatic-publishing", "sqlite-automatic-export-continued-editing",
                    "sqlite-automatic-incoming-refresh", "sqlite-automatic-folder-unavailable" },
                completion = "Pre-close observations only; require terminal hosted PASS after actual normal close and process exit."
            }));
            Close(process);
        }, nativeFailure =>
        {
            if (File.Exists(nativeEvidence)) throw new IOException("Automatic native observation must not overwrite earlier evidence.");
            File.WriteAllText(nativeEvidence, SnapshotNativeUsability.SerializeDiagnostic(new
            {
                schemaVersion = 1, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), processId = process.Id,
                productExecutable = Path.GetFullPath(executable), productExeSha256 = productHash,
                productAssemblySha256 = assemblyHash, hostExecutable, hostExecutableSha256 = hostHash,
                outcome = nativeFailure is null ? "Passed" : "Failed",
                errorType = nativeFailure?.GetType().Name, error = nativeFailure?.Message,
                nativeUsability = native?.Diagnostic,
                completion = "Written after normal close or original failure, before final process cleanup; require terminal phase/scenario/lane results."
            }));
        }, () => { hold.Reset(); release.Set(); },
            nativeFailure => File.AppendAllText(diagnostics,
                $"Automatic snapshot host abnormal exit; pid={process.Id}; primary={nativeFailure}{Environment.NewLine}"),
            Console.Error.WriteLine, Console.Out.WriteLine);
        Console.WriteLine("PASS: hosted automatic snapshots preserve native continued writing/browsing during real export, truthful latest-publication status, automatic incoming refresh and local editing through folder failure; " + evidence);
    }
}
