using System.Diagnostics;
using System.Text.Json;
using OrgExplorer.Desktop.Smoke;
using OrgExplorer.Installer.ProcessFixture;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class AutomaticHostLifecycleTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(".test-workspaces", "automatic-host-" + Guid.NewGuid().ToString("N")));
    private string MissingOutput => Path.Combine(root, "missing-output-directory", "automatic.json");

    [Fact]
    [Trait("Category", "Runner")]
    public void Successful_body_with_real_output_failure_cannot_pass_the_scenario_after_actual_normal_exit()
    {
        using var child = new OwnedChild(root);
        var reports = new List<string>();
        var result = Path.Combine(root, "results.json");
        var runner = new SmokeScenarioRunner([new("automatic-host", _ =>
        {
            AutomaticHostLifecycle.Run(child.Process, () =>
            {
                child.Release.Set();
                Assert.True(child.Process.WaitForExit(5000));
                Assert.Equal(0, child.Process.ExitCode);
            }, _ => File.WriteAllText(MissingOutput, "actual observation"),
                () => { }, _ => File.AppendAllText(MissingOutput, "actual abnormal cleanup"),
                reports.Add, reports.Add);
            reports.Add("terminal-pass");
        })]);
        Assert.False(runner.Run(Path.Combine(root, "fixtures"), result));
        using var json = JsonDocument.Parse(File.ReadAllText(result));
        var scenario = Assert.Single(json.RootElement.GetProperty("scenarios").EnumerateArray());
        Assert.Equal("Failed", scenario.GetProperty("status").GetString());
        Assert.Contains("DirectoryNotFoundException", scenario.GetProperty("error").GetString());
        Assert.DoesNotContain("terminal-pass", reports);
        Assert.True(child.Process.HasExited);
        Assert.Equal(0, child.Process.ExitCode);
        Assert.Contains(reports, report => report.Contains("observation", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Runner")]
    public void Primary_and_real_output_cleanup_failures_preserve_attribution_and_cannot_prevent_owned_termination(bool brokenReporter)
    {
        using var child = new OwnedChild(root);
        using var released = new ManualResetEventSlim();
        using var fallback = new StringWriter();
        var reports = new List<string>();
        var primary = new InvalidOperationException("Original native owner/frame assertion.");
        Assert.False(child.Process.HasExited);
        var thrown = Assert.Throws<InvalidOperationException>(() => AutomaticHostLifecycle.Run(child.Process,
            () => ThrowPrimary(primary), _ => File.WriteAllText(MissingOutput, "actual observation"),
            released.Set, _ => File.AppendAllText(MissingOutput, "actual abnormal cleanup"),
            message =>
            {
                if (brokenReporter) File.AppendAllText(MissingOutput, message);
                else reports.Add(message);
            }, fallback.WriteLine));
        Assert.Same(primary, thrown);
        Assert.Contains(nameof(ThrowPrimary), thrown.StackTrace);
        Assert.True(child.Process.WaitForExit(0), "Output failure left the actual owned non-UI child alive.");
        Assert.NotEqual(0, child.Process.ExitCode);
        Assert.NotEqual(2, child.Process.ExitCode);
        Assert.True(released.IsSet);
        var failures = Assert.IsType<AutomaticHostFailure[]>(thrown.Data["AutomaticHostSecondaryFailures"]);
        Assert.Contains(failures, failure => failure.Stage == "observation" && failure.Error is DirectoryNotFoundException);
        Assert.Contains(failures, failure => failure.Stage == "cleanup-diagnostic" && failure.Error is DirectoryNotFoundException);
        var output = string.Join("\n", reports) + fallback;
        Assert.Contains("observation", output);
        Assert.Contains("cleanup-diagnostic", output);
        Assert.Contains(primary.Message, output);
        Assert.Contains($"pid={child.Process.Id}", output);
        if (brokenReporter)
        {
            Assert.Contains(failures, failure => failure.Stage == "secondary-report" && failure.Error is DirectoryNotFoundException);
            Assert.Contains("secondary-report", output);
        }
    }

    private static void ThrowPrimary(Exception primary) => throw primary;

    private sealed class OwnedChild : IDisposable
    {
        internal Process Process { get; }
        internal EventWaitHandle Release { get; }
        private readonly EventWaitHandle started;
        internal OwnedChild(string root)
        {
            var installation = Path.Combine(root, "Application");
            Directory.CreateDirectory(Path.Combine(installation, "current"));
            File.WriteAllText(Path.Combine(installation, "current", "app.txt"), "Fictitious non-UI child");
            var token = Guid.NewGuid().ToString("N");
            var startedName = @"Local\OrgExplorer-auto-child-started-" + token;
            var releaseName = @"Local\OrgExplorer-auto-child-release-" + token;
            started = new(false, EventResetMode.ManualReset, startedName);
            Release = new(false, EventResetMode.ManualReset, releaseName);
            var runtime = Path.ChangeExtension(typeof(AutomaticHostLifecycleTests).Assembly.Location, ".runtimeconfig.json");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            foreach (var argument in new[] { "exec", "--runtimeconfig", runtime, typeof(DeferredUninstall).Assembly.Location,
                "child", runtime, installation, startedName, releaseName, Path.Combine(root, "child.pid") })
                start.ArgumentList.Add(argument);
            Process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Owned non-UI child did not start.");
            try
            {
                Assert.True(started.WaitOne(TimeSpan.FromSeconds(5)), "Owned non-UI child did not enter its actual waiting lifetime.");
                Assert.Equal(Process.Id.ToString(), File.ReadAllText(Path.Combine(root, "child.pid")));
                _ = Process.Handle;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            try
            {
                if (!Process.HasExited) { Process.Kill(true); Assert.True(Process.WaitForExit(5000)); }
            }
            finally { Process.Dispose(); Release.Dispose(); started.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
