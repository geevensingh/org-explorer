using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private sealed class RichScaleCommitBoundary(EventWaitHandle hold, EventWaitHandle entered,
        EventWaitHandle release) : IWorkspaceFileObserver
    {
        public void Observe(WorkspaceFileEffect effect) { }
        public void ObservePhase(WorkspaceStoragePhase phase)
        {
            if (phase.Name != "local-sqlite-commit" || phase.Kind != WorkspaceStoragePhaseKind.Begin || !hold.WaitOne(0)) return;
            entered.Set();
            if (!release.WaitOne(TimeSpan.FromSeconds(180)))
                throw new IOException("The owned rich-profile transaction was not released.");
        }
    }

    private static void SelectRichNoteReport(AutomationElement window)
    {
        Set(window, "SearchQuery", "handle01000");
        AwaitScale(() => Find(window, "SearchCount").Current.Name.Contains("People found: 1 "));
        SelectScaleResult(window);
        AwaitScale(() => CurrentPersonEmail(window) == ScaleFixture.Target && Value(window, "SearchQuery") == "");
        EnsurePeopleBrowsing(window);
    }

    private static void RunRichBackgroundExport(string executable, ScaleFixture fixture)
    {
        Guid exportedRevision;
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly) ||
                !seed.EditFavorite(ScaleFixture.Email(11), true))
                throw new InvalidOperationException(seed.Status);
            exportedRevision = seed.Synchronization.LocalRevision!.Value;
            ScaleFixture.RequireRichPopulation(seed, additionalPersonNotes: 1, additionalTeamNotes: 20);
        }
        var token = Guid.NewGuid().ToString("N");
        var names = new[] { @"Local\OrgExplorer-rich-tick-" + token, @"Local\OrgExplorer-rich-hold-" + token,
            @"Local\OrgExplorer-rich-entered-" + token, @"Local\OrgExplorer-rich-release-" + token,
            @"Local\OrgExplorer-rich-ended-" + token, @"Local\OrgExplorer-rich-profile-hold-" + token,
            @"Local\OrgExplorer-rich-profile-entered-" + token, @"Local\OrgExplorer-rich-profile-release-" + token };
        using var advance = new EventWaitHandle(false, EventResetMode.AutoReset, names[0]);
        using var hold = new EventWaitHandle(false, EventResetMode.ManualReset, names[1]);
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, names[2]);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, names[3]);
        using var ended = new EventWaitHandle(false, EventResetMode.ManualReset, names[4]);
        using var profileHold = new EventWaitHandle(false, EventResetMode.ManualReset, names[5]);
        using var profileEntered = new EventWaitHandle(false, EventResetMode.ManualReset, names[6]);
        using var profileRelease = new EventWaitHandle(false, EventResetMode.ManualReset, names[7]);
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--rich-scale-snapshot-fixture",
            Path.GetFullPath(executable), fixture.Workspace }.Concat(names)) start.ArgumentList.Add(argument);
        var productHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
        var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(executable, ".dll"))));
        var remembered = ReadSmokeWorkspacePreferences();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned rich export host did not start.");
        string? hostExecutable = null, hostHash = null;
        Guid? latestRevision = null;
        string? pendingArtifactHash = null;
        string? heldProfileHash = null, heldProfilePhotoHash = null;
        long? admittedAt = null, localWritingVerifiedAt = null, releasedAt = null;
        long? profileAdmittedAt = null, profileBrowsedAt = null;
        void RequireHeld()
        {
            if (!entered.WaitOne(0) || ended.WaitOne(0) || release.WaitOne(0) || !hold.WaitOne(0))
                throw new InvalidOperationException("The same actual complete-snapshot export did not remain held during rich native editing/navigation.");
        }
        void RequireProfileHeld()
        {
            if (!profileEntered.WaitOne(0) || profileRelease.WaitOne(0) || !profileHold.WaitOne(0))
                throw new InvalidOperationException("The same actual rich profile transaction did not remain held before Commit.");
        }
        AutomaticHostLifecycle.Run(process, () =>
        {
            var window = Window(process);
            hostExecutable = process.MainModule!.FileName;
            hostHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hostExecutable)));
            AwaitRememberedWorkspace(window, fixture.Workspace, remembered, AwaitScale);
            SelectRichNoteReport(window);
            ScaleFixture.Measure("UI-profile-mutation", () =>
            {
                PersonProfile originalProfile;
                using (var baseline = ApplicationWorkflow.CreateFileBacked())
                {
                    if (!baseline.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly))
                        throw new InvalidOperationException(baseline.Status);
                    originalProfile = baseline.GetRelationships(ScaleFixture.Target)!.Person;
                    heldProfileHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(originalProfile)));
                    heldProfilePhotoHash = Convert.ToHexString(SHA256.HashData(baseline.GetPhoto(ScaleFixture.Target)!.Bytes.Span));
                }
                BeginProfileEditing(window);
                Set(window, "OfficeNumber", "0009U");
                profileHold.Set();
                Click(window, "SaveProfile");
                if (!profileEntered.WaitOne(TimeSpan.FromSeconds(180)))
                    throw new TimeoutException("The real full-population profile did not reach its actual SQLite Commit.");
                profileAdmittedAt = Stopwatch.GetTimestamp();
                RequireProfileHeld();
                Click(window, "OpenSettings");
                AwaitScale(() => !Find(window, "SettingsTitle").Current.IsOffscreen &&
                    !Find(window, "WorkspacePath").Current.IsOffscreen);
                Click(window, "OpenSettings");
                Click(window, "NavigateManager");
                AwaitScale(() => CurrentPersonEmail(window) == ScaleFixture.Email(11));
                RequireProfileHeld();
                using (var original = ApplicationWorkflow.CreateFileBacked())
                    if (!original.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly) ||
                        original.GetRelationships(ScaleFixture.Target)!.Person != originalProfile ||
                        originalProfile.OfficeNumber != "new-1000B" ||
                        Convert.ToHexString(SHA256.HashData(original.GetPhoto(ScaleFixture.Target)!.Bytes.Span)) != heldProfilePhotoHash)
                        throw new InvalidOperationException("The held profile was prematurely reported committed.");
                RequireProfileHeld();
                profileBrowsedAt = Stopwatch.GetTimestamp();
                profileHold.Reset();
                profileRelease.Set();
                AwaitScale(() => Activity(window) == "Ready");
                using var saved = ApplicationWorkflow.CreateFileBacked();
                if (!saved.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly) ||
                    saved.GetRelationships(ScaleFixture.Target)!.Person != originalProfile with { OfficeNumber = "0009U" } ||
                    saved.GetPhoto(ScaleFixture.Target) is not { Width: 128, Height: 128 } photo ||
                    Convert.ToHexString(SHA256.HashData(photo.Bytes.Span)) != heldProfilePhotoHash)
                    throw new InvalidOperationException("The original profile did not commit exactly after release.");
                exportedRevision = saved.Synchronization.LocalRevision!.Value;
            });
            SelectRichNoteReport(window);
            SetSettingsVisible(window, true);
            hold.Set();
            advance.Set();
            if (!entered.WaitOne(TimeSpan.FromSeconds(180)))
                throw new TimeoutException("The full rich periodic export did not reach actual snapshot publication.");
            admittedAt = Stopwatch.GetTimestamp();
            RequireHeld();
            AwaitScale(() => Find(window, "SynchronizationStatus").Current.Name.Contains("Publishing a complete local version."));
            var pending = Directory.GetFiles(fixture.SynchronizationFolder, "*.pending").Single();
            var pendingBytes = File.ReadAllBytes(pending);
            pendingArtifactHash = Convert.ToHexString(SHA256.HashData(pendingBytes));
            using (var envelope = JsonDocument.Parse(pendingBytes))
            using (var content = JsonDocument.Parse(envelope.RootElement.GetProperty("content").GetBytesFromBase64()))
                if (content.RootElement.GetProperty("revision").GetGuid() != exportedRevision)
                    throw new InvalidOperationException("The held rich export did not contain the actual prior local revision.");
            CaptureVisualEvidence(window, "sqlite-rich-background-export-pending");
            SetSettingsVisible(window, false);
            RunLocalNoteAutosaveJourney(window, fixture.Workspace, fixture, executable, "background-export", RequireHeld);
            SelectRichNoteReport(window);
            RunRichProfileLatency(window, fixture, executable, "background-export", RequireHeld);
            RequireHeld();
            using (var reading = ApplicationWorkflow.CreateFileBacked())
            {
                if (!reading.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly))
                    throw new InvalidOperationException(reading.Status);
                ScaleFixture.RequireRichPopulation(reading, additionalPersonNotes: 2, additionalTeamNotes: 40);
                latestRevision = reading.Synchronization.LocalRevision;
                if (latestRevision == exportedRevision || !reading.Synchronization.HasUnpublishedChanges)
                    throw new InvalidOperationException("Rich native writing was not independently committed during the held older export.");
            }
            localWritingVerifiedAt = Stopwatch.GetTimestamp();
            if (pendingArtifactHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pending))))
                throw new InvalidOperationException("The older full export changed while independent local edits committed.");
            hold.Reset();
            releasedAt = Stopwatch.GetTimestamp();
            release.Set();
            AwaitScale(() => ended.WaitOne(0));
            SetSettingsVisible(window, true);
            AwaitScale(() => Find(window, "SyncNow").Current.IsEnabled);
            using (var reading = ApplicationWorkflow.CreateFileBacked())
                if (!reading.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly) ||
                    reading.Synchronization.PublishedRevision != exportedRevision ||
                    reading.Synchronization.LocalRevision != latestRevision || !reading.Synchronization.HasUnpublishedChanges)
                    throw new InvalidOperationException("An older rich export falsely acknowledged later native writing.");
            Click(window, "SyncNow");
            AwaitScale(() => Find(window, "SyncNow").Current.IsEnabled &&
                Find(window, "SynchronizationStatus").Current.Name.Contains("Locally saved and published."));
            using (var reading = ApplicationWorkflow.CreateFileBacked())
                if (!reading.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly) ||
                    reading.Synchronization.LocalRevision != latestRevision ||
                    reading.Synchronization.PublishedRevision != latestRevision)
                    throw new InvalidOperationException("Sync now did not publish the exact later rich local revision.");
            CaptureVisualEvidence(window, "sqlite-rich-background-export-latest-published");
            SetSettingsVisible(window, false);
            Close(process);
        }, failure =>
        {
            var evidence = Path.Combine(Path.GetDirectoryName(diagnostics)!,
                Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-rich-export-lifecycle.json");
            if (File.Exists(evidence)) throw new IOException("Rich export evidence must not overwrite an earlier observation.");
            File.WriteAllText(evidence, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                productExecutable = Path.GetFullPath(executable), productExeSha256 = productHash,
                productAssemblySha256 = assemblyHash, hostExecutable, hostExecutableSha256 = hostHash,
                processId = process.Id, environment = new { os = Environment.OSVersion.ToString(),
                    processors = Environment.ProcessorCount, runtime = Environment.Version.ToString() },
                outcome = failure is null ? "Passed" : "Failed", error = failure?.Message,
                scope = "Actual compiled product MainWindow in an owned hosted WPF process at full rich scale. Only the public 60-second TimeProvider and external immutable snapshot publication are controlled. Normal-executable latency is separately measured in the same scenario.",
                timestampFrequency = Stopwatch.Frequency, profileAdmittedAt, profileBrowsedAt,
                admittedAt, localWritingVerifiedAt, releasedAt,
                exportedRevision, latestRevision, pendingArtifactHash, heldProfileHash, heldProfilePhotoHash,
                latencyEvidence = Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-note-latency-rich-background-export.json",
                providerDelivery = "Unknown; publication is not delivery elsewhere.",
                completion = "Written after normal process exit or original failure; require both rich latency artifacts and complete terminal scenario/suite results."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, () => { profileHold.Reset(); profileRelease.Set(); hold.Reset(); release.Set(); },
            failure => File.AppendAllText(diagnostics, $"Rich export host abnormal exit; pid={process.Id}; primary={failure}{Environment.NewLine}"),
            Console.Error.WriteLine, Console.Out.WriteLine);
    }

    private static void RunRichProfileLatency(AutomationElement window, ScaleFixture fixture, string executable,
        string phase = "normal-executable", Action? requireHeldExport = null)
    {
        PersonProfile expected;
        string photoHash;
        object workload;
        using (var original = ApplicationWorkflow.CreateFileBacked())
        {
            if (!original.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly))
                throw new InvalidOperationException(original.Status);
            workload = ScaleFixture.RequireRichPopulation(original, additionalPersonNotes: 2, additionalTeamNotes: 40);
            expected = original.GetRelationships(ScaleFixture.Target)!.Person;
            photoHash = Convert.ToHexString(SHA256.HashData(original.GetPhoto(ScaleFixture.Target)!.Bytes.Span));
        }
        var samples = new List<double>();
        var verificationMs = new List<double>();
        var email = Find(window, "Email");
        var value = Find(window, "OfficeNumberValue");
        var edit = Find(window, "EditProfile");
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        for (var sample = 0; sample < 21; sample++)
        {
            requireHeldExport?.Invoke();
            BeginProfileEditing(window);
            var requestedValue = sample == 20 ? "0009U" : $"native-{sample:D2}";
            Set(window, "OfficeNumber", requestedValue);
            var profileInput = Find(window, "DirectoryNameEditor");
            if (profileInput.Current.IsOffscreen || CurrentPersonEmail(window) != ScaleFixture.Target)
                throw new InvalidOperationException("The rich profile sample did not begin with its actual owner/editor.");
            var save = ControlReadiness.AwaitPattern(() => Find(window, "SaveProfile"),
                control => control.Current.IsEnabled,
                control => (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern), AwaitScale);
            var request = Stopwatch.StartNew();
            save.Invoke();
            ObserveNoteLatency(() =>
            {
                var identity = email.GetUpdatedCache(cache).Cached;
                var current = value.GetUpdatedCache(cache).Cached;
                var entry = edit.GetUpdatedCache(cache).Cached;
                return identity.Name == ScaleFixture.Target && !identity.IsOffscreen &&
                    current.Name == requestedValue && !current.IsOffscreen &&
                    entry.IsEnabled && !entry.IsOffscreen && profileInput.Current.IsOffscreen && Activity(window) == "Ready";
            });
            samples.Add(request.Elapsed.TotalMilliseconds);
            requireHeldExport?.Invoke();
            expected = expected with { OfficeNumber = requestedValue };
            var verification = Stopwatch.StartNew();
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            if (!reopened.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly) ||
                reopened.GetRelationships(ScaleFixture.Target)!.Person != expected ||
                reopened.GetPhoto(ScaleFixture.Target) is not { Width: 128, Height: 128 } photo ||
                Convert.ToHexString(SHA256.HashData(photo.Bytes.Span)) != photoHash)
                throw new InvalidOperationException("The rich native profile acknowledgment was not backed by its exact complete owner/profile/photo.");
            verificationMs.Add(verification.Elapsed.TotalMilliseconds);
        }
        var p95 = samples.Order().ElementAt((int)Math.Ceiling(samples.Count * .95) - 1);
        const int profileGoalMs = 1000;
        const int profileCeilingMs = 1500;
        using var process = Process.GetProcessById(window.Current.ProcessId);
        var evidence = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-sqlite-rich-profile-latency-" + phase + ".json");
        if (File.Exists(evidence)) throw new IOException("Rich profile evidence must not overwrite an earlier observation.");
        File.WriteAllText(evidence, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            processId = process.Id, hostExecutable = process.MainModule!.FileName, phase, workload,
            hostExecutableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(process.MainModule!.FileName))),
            productExecutable = Path.GetFullPath(executable),
            productExeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))),
            productAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(executable, ".dll")))),
            environment = new { os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
                processors = Environment.ProcessorCount, window = window.Current.BoundingRectangle.ToString() },
            owner = ScaleFixture.Target, photoSha256 = photoHash, samplesMs = samples, p95Ms = p95,
            policyVersion = "ci-timing-v3", goalMs = profileGoalMs, goalMet = p95 <= profileGoalMs,
            budgetMs = profileCeilingMs, budgetEnforced = true, timingUse = "acceptance",
            percentile = "Nearest rank: sorted sample at ceil(0.95 * count), one based.",
            boundary = (phase == "normal-executable" ? "Normal product EXE." : "Actual compiled product MainWindow in the held-export host.") +
                " Clock starts before actual native Save Invoke, including queue/dispatch, through the same owner's exact changed reading value, closed profile form and enabled Edit/Ready after durable acceptance. Conservative postcommit-ack upper bound, not storage-only timing.",
            causalSource = "SaveProfileSessionAsync awaits RunLocalSaveAsync/SaveProfileEdit; CompleteLocalProfileEdit accepts only after durable Commit. An actual held real Commit in the same rich scenario independently retains the prior profile before release.",
            independentFullReopenVerificationMs = verificationMs,
            independentVerification = "Each sample independently reopens the entire local store and checks the exact whole Person plus decoded 128x128 photo/hash. This verification time does not retroactively date Commit or replace native acknowledgment timing."
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (p95 > profileCeilingMs)
            throw new InvalidOperationException($"Rich native profile Save p95 was {p95:F1} ms; see {evidence}.");
    }
}
