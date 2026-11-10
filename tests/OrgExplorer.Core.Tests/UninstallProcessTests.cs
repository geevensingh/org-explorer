using System.Diagnostics;
using OrgExplorer.Installer.Smoke;
using OrgExplorer.Installer.ProcessFixture;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class UninstallProcessTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public async Task Reinstall_waits_for_deferred_uninstall_cleanup_not_just_missing_application_files()
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "uninstall-" + Guid.NewGuid().ToString("N") + " with spaces"));
        var installation = Path.Combine(root, "Application");
        var app = Path.Combine(installation, "current", "app.txt");
        var childPid = Path.Combine(root, "cleanup.pid");
        var token = Guid.NewGuid().ToString("N");
        var startedName = @"Local\OrgExplorer-cleanup-started-" + token;
        var releaseName = @"Local\OrgExplorer-cleanup-release-" + token;
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, startedName);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var reinstall = new ManualResetEvent(false);
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.WriteAllText(app, "Fictitious application file");
        var runtimeConfig = Path.ChangeExtension(typeof(UninstallProcessTests).Assembly.Location, ".runtimeconfig.json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        foreach (var argument in new[] { "exec", "--runtimeconfig", runtimeConfig, typeof(DeferredUninstall).Assembly.Location,
            "parent", runtimeConfig, installation, startedName, releaseName, childPid })
            start.ArgumentList.Add(argument);
        var run = Task.Factory.StartNew(() =>
        {
            Assert.Equal(0, UninstallProcess.RunToCompletion(start, TimeSpan.FromSeconds(10)));
            reinstall.Set();
        }, TaskCreationOptions.LongRunning);
        Process? cleanup = null;
        try
        {
            Assert.True(started.WaitOne(TimeSpan.FromSeconds(5)), "Deferred cleanup did not start.");
            cleanup = Process.GetProcessById(int.Parse(File.ReadAllText(childPid)));
            _ = cleanup.Handle;
            Assert.False(File.Exists(app));
            Assert.True(Directory.Exists(installation));
            Assert.False(reinstall.WaitOne(TimeSpan.FromMilliseconds(500)),
                "Reinstall was released while deferred uninstall cleanup still owned the installation directory.");
            release.Set();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Directory.Exists(installation));
        }
        finally
        {
            release.Set();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            if (cleanup is not null)
            {
                using (cleanup) Assert.True(cleanup.WaitForExit(5000), "Non-UI cleanup fixture did not exit.");
            }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    [Trait("Category", "Runner")]
    public async Task Deferred_cleanup_consumes_the_existing_budget_and_cannot_release_reinstall_on_timeout()
    {
        var root = Path.GetFullPath(Path.Combine(".test-workspaces", "uninstall-timeout-" + Guid.NewGuid().ToString("N")));
        var installation = Path.Combine(root, "Application");
        var childPid = Path.Combine(root, "cleanup.pid");
        var token = Guid.NewGuid().ToString("N");
        var startedName = @"Local\OrgExplorer-cleanup-started-" + token;
        var releaseName = @"Local\OrgExplorer-cleanup-release-" + token;
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, startedName);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        Directory.CreateDirectory(Path.Combine(installation, "current"));
        File.WriteAllText(Path.Combine(installation, "current", "app.txt"), "Fictitious application file");
        var runtimeConfig = Path.ChangeExtension(typeof(UninstallProcessTests).Assembly.Location, ".runtimeconfig.json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        foreach (var argument in new[] { "exec", "--runtimeconfig", runtimeConfig, typeof(DeferredUninstall).Assembly.Location,
            "parent", runtimeConfig, installation, startedName, releaseName, childPid })
            start.ArgumentList.Add(argument);
        var run = Task.Factory.StartNew(() => UninstallProcess.RunToCompletion(start, TimeSpan.FromSeconds(2)), TaskCreationOptions.LongRunning);
        Process? cleanup = null;
        try
        {
            Assert.True(started.WaitOne(TimeSpan.FromSeconds(5)), "Deferred cleanup did not start.");
            cleanup = Process.GetProcessById(int.Parse(File.ReadAllText(childPid)));
            _ = cleanup.Handle;
            await Assert.ThrowsAsync<TimeoutException>(async () => await run);
            Assert.True(Directory.Exists(installation), "A timed-out cleanup must not claim successful uninstall.");
            Assert.True(cleanup.WaitForExit(5000), "Timed-out uninstall left its owned cleanup process running.");
        }
        finally
        {
            release.Set();
            if (cleanup is not null)
            {
                using (cleanup)
                {
                    if (!cleanup.WaitForExit(5000)) { cleanup.Kill(true); cleanup.WaitForExit(5000); }
                }
            }
            Directory.Delete(root, true);
        }
    }
}
