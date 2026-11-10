using System.Diagnostics;

namespace OrgExplorer.Installer.ProcessFixture;

public static class DeferredUninstall
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 3;
        var installation = Path.GetFullPath(args[2]);
        if (!installation.Contains(Path.DirectorySeparatorChar + ".test-workspaces" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.GetFileName(installation) != "Application")
            return 3;
        if (args[0] == "parent")
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "exec", "--runtimeconfig", args[1], typeof(DeferredUninstall).Assembly.Location, "child" }.Concat(args.Skip(1)))
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Cleanup fixture did not start.");
            return 0;
        }
        File.WriteAllText(args[5], Environment.ProcessId.ToString());
        File.Delete(Path.Combine(installation, "current", "app.txt"));
        using var started = EventWaitHandle.OpenExisting(args[3]);
        using var release = EventWaitHandle.OpenExisting(args[4]);
        started.Set();
        if (!release.WaitOne(TimeSpan.FromSeconds(10))) return 2;
        Directory.Delete(installation, true);
        return 0;
    }
}
