using System.IO.Compression;
using System.Text;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using OrgExplorer.Workflow.Updates;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class AutomaticUpdateTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "automatic-update-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Authorized_full_package_is_downloaded_verified_and_restarts_the_same_durably_saved_workspace()
    {
        using var fixture = new SignedReleaseFixture { Package = Package() };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var workspace = Path.Combine(root, "workspace");
        WorkspaceFixtures.CopyCurrent(workspace);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        Assert.True(app.EditPreferredName("avery@example.com", "Saved before automatic restart"), app.Status);
        var effect = new RestartEffect();
        var transport = new PackageTransport(fixture.Package);
        using var installer = new UpdateInstaller(transport, effect, Path.Combine(root, "staging"), new Version("0.1.0"));

        await installer.InstallAsync(release, restart => Task.Run(() => app.RestartWhenSafe(restart)));

        Assert.Equal(UpdateInstallStatus.Restarting, installer.Status);
        Assert.Equal(release.PackageUrl, transport.Request);
        Assert.Equal(workspace, effect.Workspace);
        Assert.Equal(fixture.Package, effect.Bytes);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(effect.Workspace!), reopened.Status);
        Assert.Equal("Saved before automatic restart", reopened.GetRelationships("avery@example.com")!.Person.PreferredName);
    }

    internal static byte[] Package(string id = "geevensingh.OrgExplorer", string version = "0.2.0")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(id + ".nuspec").Open(), Encoding.UTF8))
                writer.Write($"""
                    <?xml version="1.0"?>
                    <package><metadata><id>{id}</id><version>{version}</version><authors>Fixture</authors>
                    <description>Controlled bytes, never executed</description></metadata></package>
                    """);
            using var executable = zip.CreateEntry("lib/net45/OrgExplorer.Desktop.exe").Open();
            executable.Write("Fictitious executable, never run."u8);
        }

        return output.ToArray();
    }

    [Fact]
    public async Task Startup_and_manual_settings_discovery_automatically_consume_only_the_exact_authorized_release()
    {
        using var fixture = new SignedReleaseFixture { Package = Package() };
        var effect = new RestartEffect();
        using var installer = new UpdateInstaller(new PackageTransport(fixture.Package), effect,
            Path.Combine(root, "staging"), new Version("0.1.0"));
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Path.Combine(root, "workspace")), app.Status);
        var settings = new UpdateSettings(fixture.Checker(), Path.Combine(root, "settings.json"),
            (release, token) => installer.InstallAsync(release,
                restart => Task.Run(() => app.RestartWhenSafe(restart)), token));

        await settings.StartupAsync();

        Assert.Equal(UpdateInstallStatus.Restarting, installer.Status);
        Assert.Equal(fixture.Package, effect.Bytes);
        Assert.Equal(Path.Combine(root, "workspace"), effect.Workspace);
    }

    [Fact]
    public async Task Actual_import_publication_finishes_before_restart_without_waiting_for_external_sync()
    {
        using var fixture = new SignedReleaseFixture { Package = Package() };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var publisher = new HeldPublisher();
        using var app = ApplicationWorkflow.CreateFileBacked(filePublisher: publisher);
        var workspace = Path.Combine(root, "import");
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        var preview = app.PreviewImport("""
            {"schemaVersion":1,"batchId":"18000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","people":[
             {"email":"saved@example.com","displayName":"Import finished before restart","managerEmail":null}]}
            """);
        Assert.True(preview.CanApply, app.Status);
        publisher.Hold = true;
        var apply = Task.Run(() => app.ApplyImport(preview.Id));
        await publisher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var effect = new RestartEffect();
        using var installer = new UpdateInstaller(new PackageTransport(fixture.Package), effect,
            Path.Combine(root, "staging"), new Version("0.1.0"));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        installer.Changed += (_, _) =>
        {
            if (installer.Status == UpdateInstallStatus.WaitingForLocalWork) waiting.TrySetResult();
        };
        var install = installer.InstallAsync(release, restart => Task.Run(() => app.RestartWhenSafe(restart)));
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(apply.IsCompleted);
            Assert.False(install.IsCompleted);
            Assert.Null(effect.Bytes);
        }
        finally { publisher.Release.Set(); }
        Assert.True(await apply, app.Status);
        await install.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UpdateInstallStatus.Restarting, installer.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(effect.Workspace!), reopened.Status);
        Assert.Equal("Import finished before restart", Assert.Single(reopened.People).DirectoryName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_failed_note_save_blocks_apply_retains_writing_and_explicit_retry_can_safely_update(bool team)
    {
        using var fixture = new SignedReleaseFixture { Package = Package() };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var workspace = Path.Combine(root, "failed-save");
        WorkspaceFixtures.CopyCurrent(workspace);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(workspace), app.Status);
        var note = team ? app.CreateTeamNote("avery@example.com", "Context", "Saved writing")!.Id
            : app.CreatePersonNote("avery@example.com", "Context", "Saved writing")!.Id;
        var notePath = Path.Combine(workspace, team ? "team-notes" : "notes", note + ".md");
        var effect = new RestartEffect();
        using var installer = new UpdateInstaller(new PackageTransport(fixture.Package), effect,
            Path.Combine(root, "staging"), new Version("0.1.0"));
        using (File.Open(notePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(Edit("Retained unsaved writing"));
            await installer.InstallAsync(release, restart => Task.Run(() => app.RestartWhenSafe(restart)));
            Assert.Equal(UpdateInstallStatus.LocalWorkBlocked, installer.Status);
            Assert.Null(effect.Bytes);
            Assert.False(app.CanSafelyClose);
            Assert.Contains("NOT saved", app.Status);
            Assert.Contains("Saved writing", File.ReadAllText(notePath));
        }
        Assert.True(Edit("Retained unsaved writing"), app.Status);
        await installer.InstallAsync(release, restart => Task.Run(() => app.RestartWhenSafe(restart)));
        Assert.Equal(UpdateInstallStatus.Restarting, installer.Status);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(effect.Workspace!), reopened.Status);
        Assert.Equal("Retained unsaved writing", team
            ? reopened.GetTeamNotes("avery@example.com").Single(n => n.Id == note).Markdown
            : reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == note).Markdown);
        bool Edit(string text) => team ? app.EditTeamNote(note, "Context", text) : app.EditPersonNote(note, "Context", text);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("not-zip")]
    public async Task Signed_but_invalid_or_substituted_full_bytes_never_reach_the_safe_point_or_installer(string failure)
    {
        using var fixture = new SignedReleaseFixture
        {
            Package = failure switch
            {
                "identity" => Package("different.Application"),
                "version" => Package(version: "0.3.0"),
                "not-zip" => "Not a Velopack package"u8.ToArray(),
                _ => Package()
            }
        };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var bytes = fixture.Package.ToArray();
        if (failure == "hash") bytes[^1] ^= 1;
        if (failure == "short") bytes = bytes[..^1];
        if (failure == "long") bytes = [.. bytes, 42];
        var effect = new RestartEffect();
        using var installer = new UpdateInstaller(new PackageTransport(bytes), effect,
            Path.Combine(root, "staging"), new Version("0.1.0"));
        await installer.InstallAsync(release, _ => throw new InvalidOperationException("Invalid package reached local work!"));
        Assert.Equal(UpdateInstallStatus.VerificationFailed, installer.Status);
        Assert.Null(effect.Bytes);
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "staging")));
    }

    [Fact]
    public async Task Authorization_expiring_while_real_local_work_waits_is_rejected_at_handoff()
    {
        using var fixture = new SignedReleaseFixture { Package = Package() };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var clock = new ControlledClock();
        var effect = new RestartEffect();
        using var installer = new UpdateInstaller(new PackageTransport(fixture.Package), effect,
            Path.Combine(root, "staging"), new Version("0.1.0"), clock);
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.True(app.OpenWorkspace(Path.Combine(root, "workspace")), app.Status);
        await installer.InstallAsync(release, restart =>
        {
            clock.Now = release.ExpiresAt;
            return Task.FromResult(app.RestartWhenSafe(restart));
        });
        Assert.Equal(UpdateInstallStatus.VerificationFailed, installer.Status);
        Assert.Contains("expired", installer.Message);
        Assert.Null(effect.Bytes);
    }

    [Fact]
    public async Task Already_installed_version_never_downloads_or_restarts()
    {
        using var fixture = new SignedReleaseFixture { Package = Package() };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var transport = new PackageTransport(fixture.Package);
        var effect = new RestartEffect();
        using var installer = new UpdateInstaller(transport, effect, Path.Combine(root, "staging"), new Version("0.2.0"));
        await installer.InstallAsync(release, _ => throw new InvalidOperationException("Already installed version reached safe point."));
        Assert.Null(transport.Request);
        Assert.Null(effect.Bytes);
        Assert.Equal(UpdateInstallStatus.Idle, installer.Status);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(503)]
    public async Task Actual_HTTP_package_transport_uses_the_pinned_URL_and_download_failure_preserves_local_use(int status)
    {
        using var fixture = new SignedReleaseFixture { Package = Package() };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        using var server = new LocalReleaseServer(() => fixture.Package) { StatusCode = status };
        using var route = new ControlledRoute(server.ManifestUrl);
        using var http = new HttpClient(route);
        var effect = new RestartEffect();
        using var installer = new UpdateInstaller(new HttpReleasePackageTransport(http), effect,
            Path.Combine(root, "staging"), new Version("0.1.0"));
        using var app = ApplicationWorkflow.CreateFileBacked();
        var workspace = Path.Combine(root, "http-workspace");
        WorkspaceFixtures.CopyCurrent(workspace);
        Assert.True(app.OpenWorkspace(workspace), app.Status);

        await installer.InstallAsync(release, restart => Task.Run(() => app.RestartWhenSafe(restart)));

        Assert.Equal(release.PackageUrl, route.Request);
        Assert.Equal(status == 200 ? UpdateInstallStatus.Restarting : UpdateInstallStatus.DownloadFailed, installer.Status);
        if (status == 200) Assert.Equal(fixture.Package, effect.Bytes);
        else
        {
            Assert.Null(effect.Bytes);
            Assert.Contains("No update is confirmed installed", installer.Message);
        }
        Assert.True(app.EditPreferredName("avery@example.com", "Locally usable after HTTP"), app.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task Unsupported_Workspace_can_update_and_restart_without_writes_and_recovery_keeps_upgrade_guidance(int version)
    {
        using var fixture = new SignedReleaseFixture { Package = Package() };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var workspace = Path.Combine(root, "unsupported");
        if (version < 3) WorkspaceFixtures.CopyVersion(version, workspace);
        else WorkspaceFixtures.CopyCurrent(workspace);
        var pointer = Path.Combine(workspace, "workspace.json");
        var unsupported = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pointer))!;
        unsupported["schemaVersion"] = version;
        File.WriteAllText(pointer, unsupported.ToJsonString());
        var original = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, File.ReadAllBytes);
        var directories = Directory.GetDirectories(workspace, "*", SearchOption.AllDirectories).Order().ToArray();
        using var app = ApplicationWorkflow.CreateFileBacked();
        Assert.False(app.OpenWorkspace(workspace));
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, app.Compatibility.State);
        var effect = new RestartEffect();
        using var installer = new UpdateInstaller(new PackageTransport(fixture.Package), effect,
            Path.Combine(root, "staging"), new Version("0.1.0"));
        await installer.InstallAsync(release, restart => Task.Run(() => app.RestartWhenSafe(restart)));
        Assert.Equal(UpdateInstallStatus.Restarting, installer.Status);
        Assert.Equal(workspace, effect.Workspace);
        var journal = new UpdateRestartJournal(Path.Combine(root, "receipt.json"));
        journal.Begin(release, workspace);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        journal.Recover(new Version("0.2.0"), reopened.OpenWorkspace, () => reopened.Status);
        Assert.Equal(WorkspaceCompatibilityState.Unsupported, reopened.Compatibility.State);
        Assert.Contains("Settings and updates remain available", journal.Message);
        Assert.Equal(original.Keys.Order(), Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Order());
        foreach (var (file, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(directories, Directory.GetDirectories(workspace, "*", SearchOption.AllDirectories).Order());
    }

    private sealed class ControlledRoute(Uri endpoint) : DelegatingHandler(new HttpClientHandler())
    {
        public Uri? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request.RequestUri;
            request.RequestUri = endpoint;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HeldPublisher : IWorkspaceFilePublisher
    {
        public bool Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Publish(string staged, string destination, string? backup, WorkspaceFilePublicationMode mode = WorkspaceFilePublicationMode.Automatic)
        {
            if (Hold)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Controlled publication was not released.");
            }
            new WorkspaceFilePublisher().Publish(staged, destination, backup, mode);
        }
    }

    internal sealed class PackageTransport(byte[] bytes) : IReleasePackageTransport
    {
        public Uri? Request { get; private set; }
        public Task<Stream> OpenPackageAsync(AuthorizedRelease release, CancellationToken cancellationToken)
        {
            Request = release.PackageUrl;
            return Task.FromResult<Stream>(new MemoryStream(bytes, false));
        }
    }

    [Fact]
    public async Task Restart_receipt_reopens_current_workspace_and_an_unconfirmed_restart_never_retries_in_a_loop()
    {
        using var fixture = new SignedReleaseFixture();
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var workspace = Path.Combine(root, "current");
        WorkspaceFixtures.CopyCurrent(workspace);
        var originalPointer = File.ReadAllBytes(Path.Combine(workspace, "workspace.json"));
        var path = Path.Combine(root, "update-attempt.json");
        new UpdateRestartJournal(path).Begin(release, workspace);
        using var oldApp = ApplicationWorkflow.CreateFileBacked();
        var oldJournal = new UpdateRestartJournal(path);

        oldJournal.Recover(new Version("0.1.0"), oldApp.OpenWorkspace, () => oldApp.Status);

        Assert.False(oldJournal.AllowAutomatic(release));
        Assert.Contains("not confirmed", oldJournal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(oldApp.People);
        Assert.Equal(WorkspaceCompatibilityState.Compatible, oldApp.Compatibility.State);
        Assert.Equal(originalPointer, File.ReadAllBytes(Path.Combine(workspace, "workspace.json")));
        new UpdateRestartJournal(path).Begin(release, workspace);
        using var newApp = ApplicationWorkflow.CreateFileBacked();
        var newJournal = new UpdateRestartJournal(path);
        newJournal.Recover(new Version("0.2.0"), newApp.OpenWorkspace, () => newApp.Status);
        Assert.Contains("confirmed", newJournal.Message);
        Assert.DoesNotContain("not confirmed", newJournal.Message);
        Assert.NotEmpty(newApp.People);
    }

    internal sealed class RestartEffect : IUpdateRestart
    {
        public string? Workspace { get; private set; }
        public byte[]? Bytes { get; private set; }
        public void ApplyAndRestart(AuthorizedRelease release, string packagePath, string? workspace)
        {
            Workspace = workspace;
            Bytes = File.ReadAllBytes(packagePath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
