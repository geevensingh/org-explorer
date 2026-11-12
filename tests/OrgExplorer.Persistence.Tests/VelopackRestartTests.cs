using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using OrgExplorer.Workflow.Updates;
using Velopack;
using Velopack.Locators;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class VelopackRestartTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", "velopack-restart-" + Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_Velopack_handoff_uses_only_the_locked_authorized_full_path_and_reports_process_start_failure(bool failStart)
    {
        Directory.CreateDirectory(root);
        var updater = Path.Combine(root, "Update.exe");
        File.WriteAllText(updater, "Never executed; the external process boundary is controlled.");
        var packages = Path.Combine(root, "packages");
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(packages, "geevensingh.OrgExplorer-9999.0.0-full.nupkg"), "Untrusted cached pending package.");
        using var fixture = new SignedReleaseFixture { Package = AutomaticUpdateTests.Package() };
        var release = (await fixture.Checker().CheckAsync()).Release!;
        var process = new ControlledProcess { FailStart = failStart };
        var locator = new ControlledLocator(root, packages, updater, process);
        var journal = new UpdateRestartJournal(Path.Combine(root, "attempt.json"));
        using var installer = new UpdateInstaller(new AutomaticUpdateTests.PackageTransport(fixture.Package),
            new VelopackUpdateRestart(journal, locator), Path.Combine(root, "staging"), new Version("0.1.0"));
        using var workflow = ApplicationWorkflow.CreateFileBacked();
        var workspace = Path.Combine(root, "workspace with spaces");
        WorkspaceFixtures.CopyCurrent(workspace);
        Assert.True(workflow.OpenWorkspace(workspace), workflow.Status);

        await installer.InstallAsync(release, restart => Task.Run(() => workflow.RestartWhenSafe(restart)));

        Assert.Equal(failStart ? UpdateInstallStatus.InstallFailed : UpdateInstallStatus.Restarting, installer.Status);
        var args = Assert.Single(process.Commands);
        Assert.Equal(updater, process.Executable);
        Assert.Contains("apply", args);
        var package = args[Array.IndexOf(args, "--package") + 1];
        Assert.Equal(release.FileName, Path.GetFileName(package));
        Assert.StartsWith(Path.Combine(root, "staging") + Path.DirectorySeparatorChar, package);
        Assert.DoesNotContain("9999.0.0", string.Join(" ", args));
        Assert.Equal("4321", args[Array.IndexOf(args, "--waitPid") + 1]);
        Assert.DoesNotContain("--silent", args);
        Assert.DoesNotContain("--norestart", args);
        Assert.False(process.Exited);
        Assert.False(journal.AllowAutomatic(release));
        if (failStart)
        {
            Assert.Contains("could not start", journal.Message);
            Assert.True(workflow.EditPreferredName("avery@example.com", "Still usable after installer failure"), workflow.Status);
            Assert.True(workflow.CanSafelyClose);
        }
        else
        {
            Assert.Throws<IOException>(() => File.WriteAllText(package, "Cannot replace verified bytes."));
            Assert.Throws<IOException>(() => File.Delete(package));
            Assert.Equal(fixture.Package, File.ReadAllBytes(package));
        }
    }

    private sealed class ControlledLocator(string root, string packages, string updater, IProcessImpl process)
        : TestVelopackLocator("geevensingh.OrgExplorer", "0.1.0", packages, Path.Combine(root, "current"), root, updater, "win")
    {
        public override IProcessImpl Process => process;
    }

    private sealed class ControlledProcess : IProcessImpl
    {
        public bool FailStart { get; set; }
        public bool Exited { get; private set; }
        public string? Executable { get; private set; }
        public List<string[]> Commands { get; } = [];
        public uint GetCurrentProcessId() => 4321;
        public string GetCurrentProcessPath() => throw new InvalidOperationException("Not needed for a pinned handoff.");
        public void Exit(int exitCode) => Exited = true;
        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        {
            Executable = exePath;
            Commands.Add(args.ToArray());
            if (FailStart) throw new IOException("Controlled process launch failure.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
