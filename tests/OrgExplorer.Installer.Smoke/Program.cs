using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace OrgExplorer.Installer.Smoke;

internal static partial class Program
{
    private static string PackageInput = @"C:\PackageInput";
    private static string SmokeRunner = @"C:\SmokeInput\OrgExplorer.Desktop.Smoke.exe";
    private static string Root = @"C:\OrgExplorerInstallerTest";
    private static string Results = @"C:\TestResults";
    private static readonly Stopwatch Execution = Stopwatch.StartNew();
    private static readonly List<object> UninstallCompletions = [];

    private static int Main(string[] args)
    {
        var sandbox = args.Length == 0 && Environment.UserName == "WDAGUtilityAccount";
        var hosted = args.Length == 5 && args[0] == "--github-hosted" &&
            Environment.GetEnvironmentVariable("CI") == "true" &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
            Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows" &&
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "geevensingh/org-explorer" &&
            Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } checkout &&
            args.Skip(1).All(path => Within(Path.Combine(checkout, ".artifacts"), path));
        if (!sandbox && !hosted)
        {
            Console.Error.WriteLine("Installer execution requires disposable Windows Sandbox or ephemeral GitHub-hosted Windows with checkout-contained inputs/results. No host installation was attempted.");
            return 1;
        }
        if (hosted) (PackageInput, SmokeRunner, Root, Results) = (args[1], args[2], args[3], args[4]);

        var evidence = new Dictionary<string, object>
        {
            ["isolation"] = sandbox ? "Disposable Windows Sandbox; network disabled; dedicated read-only inputs" : "Ephemeral GitHub-hosted Windows runner; no self-hosted execution",
            ["user"] = Environment.UserName,
            ["os"] = DescribeOs(),
            ["runnerImage"] = Environment.GetEnvironmentVariable("ImageOS") ?? "Windows Sandbox",
            ["runnerImageVersion"] = Environment.GetEnvironmentVariable("ImageVersion") ?? "not-provided",
            ["sourceCommit"] = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local-sandbox-input",
            ["trustLimitations"] = "Unsigned fixture installers; ephemeral project-key update authorization only when explicitly provisioned for this hosted run. No Windows trust bypass, production publication, production signing key, or Windows publisher certification.",
            ["uninstallCompletions"] = UninstallCompletions,
            ["result"] = "failed"
        };
        var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "geevensingh.OrgExplorer");
        var workspace = Path.Combine(Root, "Workspace");
        var exe = Path.Combine(install, "current", "OrgExplorer.Desktop.exe");
        var setup = Path.Combine(PackageInput, "baseline", "geevensingh.OrgExplorer-win-Setup.exe");
        var upgradedSetup = Path.Combine(PackageInput, "upgrade", "geevensingh.OrgExplorer-win-Setup.exe");
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !Environment.Is64BitOperatingSystem)
                throw new InvalidOperationException("The isolated Windows x64 runner must meet the package minimum build 22000. A Windows Server run does not prove Windows 11 client coverage.");
            if (Directory.Exists(install))
                throw new InvalidOperationException("The per-user installation must be initially absent.");
            if (Directory.Exists(Root))
                throw new InvalidOperationException("The lifecycle scratch root must be initially absent.");
            if (sandbox && Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App")))
                throw new InvalidOperationException("Use a clean Sandbox without a separately installed desktop runtime.");
            using var metadata = ReadFixture("baseline");
            using var upgradedMetadata = ReadFixture("upgrade");
            var version = metadata.RootElement.GetProperty("version").GetString()!;
            var upgradedVersion = upgradedMetadata.RootElement.GetProperty("version").GetString()!;
            if (Version.Parse(upgradedVersion) <= Version.Parse(version))
                throw new InvalidOperationException("The second real installer must contain a strictly newer fixture version.");
            VerifyAsset(metadata, setup);
            VerifyAsset(upgradedMetadata, upgradedSetup);
            var pendingPackage = Path.Combine(PackageInput, "upgrade", $"geevensingh.OrgExplorer-{upgradedVersion}-full.nupkg");
            VerifyAsset(upgradedMetadata, pendingPackage);
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Results);
            var scratch = Path.Combine(Root, "Scratch");
            Directory.CreateDirectory(scratch);
            foreach (var variable in new[] { "TEMP", "TMP", "VELOPACK_TEMP" })
                Environment.SetEnvironmentVariable(variable, scratch);
            Environment.SetEnvironmentVariable("VELOPACK_DEBUG", null);
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.Combine(scratch, "no-global-dotnet"));
            Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", Path.Combine(scratch, "no-global-dotnet"));
            Environment.SetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP", "0");
            evidence["version"] = version;
            evidence["upgradedVersion"] = upgradedVersion;
            evidence["installDirectory"] = install;
            evidence["workspace"] = workspace;

            Run(setup, "install", "--log", Path.Combine(Results, "install.log"));
            if (!File.Exists(exe) || !File.Exists(Path.Combine(install, "Update.exe")))
                throw new InvalidOperationException("Default per-user installation did not contain the built app and updater.");
            evidence["bundledRuntimeModules"] = CloseAutomaticallyLaunchedDesktop(exe, version);
            evidence["automaticRealWpfLaunch"] = true;
            evidence["installedIcons"] = AssertInstalledIcons(install, exe, setup);
            Run(SmokeRunner, "desktop-create", exe, workspace, "create", version);
            var savedWorkspace = Snapshot(workspace);
            evidence["workspaceFiles"] = savedWorkspace.Count;

            // Staging is deliberately not authorization. Bootstrap must keep the original version.
            Directory.CreateDirectory(Path.Combine(install, "packages"));
            File.Copy(pendingPackage, Path.Combine(install, "packages", Path.GetFileName(pendingPackage)));
            Run(SmokeRunner, "desktop-unauthorized-pending", exe, workspace, "reopen", version);
            RequireUnchanged(workspace, savedWorkspace, "unauthorized pending package");
            evidence["unauthorizedPendingPackageNotApplied"] = true;

            if (Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE") is { } updateFixture)
            {
                Run(SmokeRunner, "desktop-authenticated-automatic-update", exe, workspace, "update", version, upgradedVersion);
                using var handoff = JsonDocument.Parse(File.ReadAllText(Path.Combine(updateFixture, "handoff.json")));
                if (handoff.RootElement.GetProperty("version").GetString() != upgradedVersion ||
                    handoff.RootElement.GetProperty("workspace").GetString() != workspace)
                    throw new InvalidOperationException("Authenticated automatic handoff did not retain the exact version and Workspace.");
                var beforeRestart = handoff.RootElement.GetProperty("workspaceFiles").EnumerateObject()
                    .ToDictionary(file => file.Name, file => file.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
                RequireUnchanged(workspace, beforeRestart, "authenticated apply and automatic Workspace reopen");
                savedWorkspace = Snapshot(workspace);
                evidence["authenticatedAutomaticUpdateAndRestart"] = true;
                evidence["safeLocalHandoffWorkspaceFiles"] = beforeRestart.Count;
            }
            else if (hosted)
                throw new InvalidOperationException("Hosted ticket-18 lifecycle requires the ephemeral signed-update fixture; missing automatic update evidence cannot pass.");

            Run(upgradedSetup, "upgrade", "--silent", "--log", Path.Combine(Results, "upgrade.log"));
            evidence["upgradedIcons"] = AssertInstalledIcons(install, exe, upgradedSetup);
            RequireUnchanged(workspace, savedWorkspace, "installation upgrade");
            Run(SmokeRunner, "desktop-after-upgrade", exe, workspace, "reopen", upgradedVersion);
            RequireUnchanged(workspace, savedWorkspace, "upgraded app reopen");
            evidence["workspaceSurvivedUpgrade"] = true;

            Uninstall(install, "uninstall");
            if (File.Exists(exe))
                throw new InvalidOperationException("Uninstall left the installed application behind.");
            RequireUnchanged(workspace, savedWorkspace, "uninstall");
            Run(upgradedSetup, "reinstall", "--silent", "--log", Path.Combine(Results, "reinstall.log"));
            evidence["reinstalledIcons"] = AssertInstalledIcons(install, exe, upgradedSetup);
            Run(SmokeRunner, "desktop-after-uninstall-reinstall", exe, workspace, "reopen", upgradedVersion);
            RequireUnchanged(workspace, savedWorkspace, "reinstall and reopen");
            evidence["workspaceSurvivedUninstall"] = true;
            Uninstall(install, "cleanup");
            if (File.Exists(exe)) throw new InvalidOperationException("Final uninstall left an application executable behind.");
            RequireUnchanged(workspace, savedWorkspace, "final uninstall");
            evidence["finalApplicationRemoved"] = true;
            evidence["executionSeconds"] = Execution.Elapsed.TotalSeconds;
            evidence["result"] = "passed";
        }
        catch (Exception ex)
        {
            evidence["error"] = ex.ToString();
            Console.Error.WriteLine(ex);
        }
        finally
        {
            Directory.CreateDirectory(Results);
            var result = Path.Combine(Results, "result.json");
            File.WriteAllText(result + ".next", JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(result + ".next", result, true);
            if (sandbox)
            {
                using var shutdown = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
                {
                    UseShellExecute = false,
                    ArgumentList = { "/s", "/t", "0" }
                });
            }
        }

        return (string)evidence["result"] == "passed" ? 0 : 1;
    }

    private static bool Within(string parent, string path) =>
        Path.IsPathFullyQualified(path) && Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static object DescribeOs()
    {
        using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        using var options = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        var productType = options?.GetValue("ProductType")?.ToString() ?? "unknown";
        return new
        {
            description = RuntimeInformation.OSDescription,
            productName = version?.GetValue("ProductName")?.ToString(),
            build = Environment.OSVersion.Version.ToString(),
            productType,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            windows11Client = productType == "WinNT" && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
            coverageLimit = "Hosted Windows Server coverage is not Windows 11 client, SmartScreen reputation, or organization-policy certification."
        };
    }

    private static JsonDocument ReadFixture(string name)
    {
        var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackageInput, name, "package-metadata.json")));
        var metadata = document.RootElement;
        if (metadata.GetProperty("appId").GetString() != "geevensingh.OrgExplorer" ||
            metadata.GetProperty("platform").GetString() != "win-x64" || metadata.GetProperty("channel").GetString() != "win" ||
            !metadata.GetProperty("selfContained").GetBoolean())
            throw new InvalidOperationException("Lifecycle input is not the expected runtime-included Windows x64 product.");
        return document;
    }

    private static void VerifyAsset(JsonDocument metadata, string path)
    {
        var asset = metadata.RootElement.GetProperty("artifacts").EnumerateArray()
            .Single(a => a.GetProperty("fileName").GetString() == Path.GetFileName(path));
        using var input = File.OpenRead(path);
        if (input.Length != asset.GetProperty("size").GetInt64() ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(input)), asset.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Setup/package bytes do not match their built fixture inventory.");
    }

    private static Dictionary<string, string> Snapshot(string workspace)
    {
        if (!File.Exists(Path.Combine(workspace, "workspace.json")))
            throw new InvalidOperationException("Real desktop did not create an independent Workspace.");
        return Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(workspace, file), file =>
            {
                using var input = File.OpenRead(file);
                return Convert.ToHexString(SHA256.HashData(input));
            }, StringComparer.OrdinalIgnoreCase);
    }

    private static void RequireUnchanged(string workspace, Dictionary<string, string> expected, string operation)
    {
        var actual = Snapshot(workspace);
        if (actual.Count != expected.Count || expected.Any(file => !actual.TryGetValue(file.Key, out var hash) || hash != file.Value))
            throw new InvalidOperationException(operation + " changed or removed private Workspace bytes.");
    }

    private static void Uninstall(string install, string label)
    {
        Run(Path.Combine(install, "Update.exe"), label, "--silent", "--rootDir", install, "--log", Path.Combine(Results, label + ".log"), "uninstall");
        if (Directory.Exists(install))
            throw new InvalidOperationException("Uninstall process tree completed but the installation directory remains: " + install);
        UninstallCompletions.Add(new { stage = label, completedAt = DateTimeOffset.UtcNow, processTreeCompleted = true, installationDirectoryAbsent = true });
    }

    private static void Run(string file, string label, params string[] args)
    {
        var captureConsole = file == SmokeRunner;
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            WorkingDirectory = Root,
            RedirectStandardOutput = captureConsole,
            RedirectStandardError = captureConsole
        };
        if (captureConsole)
            start.Environment["DESKTOP_RESULTS_DIRECTORY"] = Path.Combine(Results, label + "-desktop");
        foreach (var argument in args) start.ArgumentList.Add(argument);
        if (args.Contains("uninstall"))
        {
            var remaining = Math.Min(150000, 420000 - (int)Execution.ElapsedMilliseconds);
            if (remaining <= 0) throw new TimeoutException("The uninstall lifecycle budget is exhausted.");
            var exitCode = UninstallProcess.RunToCompletion(start, TimeSpan.FromMilliseconds(remaining));
            if (exitCode != 0) throw new InvalidOperationException(label + " failed with exit code " + exitCode);
            return;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException(label + " did not start.");
        var output = captureConsole ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var errors = captureConsole ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        try
        {
            var remaining = Math.Min(150000, 420000 - (int)Execution.ElapsedMilliseconds);
            if (remaining <= 0 || !process.WaitForExit(remaining))
                throw new TimeoutException(label + " exceeded the 150-second process/seven-minute total lifecycle budget.");
            File.WriteAllText(Path.Combine(Results, label + ".stdout.txt"), output.GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(Results, label + ".stderr.txt"), errors.GetAwaiter().GetResult());
            if (process.ExitCode != 0)
                throw new InvalidOperationException(label + " failed with exit code " + process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(); }
        }
    }

    private static string[] CloseAutomaticallyLaunchedDesktop(string exe, string version)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (!string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase) ||
                            process.MainWindowHandle == IntPtr.Zero) continue;
                        if (!process.MainWindowTitle.Contains($"Org explorer {version} (Windows x64 / win)"))
                            throw new InvalidOperationException("Automatically launched desktop metadata did not match the installed package.");
                        var modules = new[] { "coreclr.dll", "wpfgfx_cor3.dll" }.Select(name =>
                        {
                            var module = process.Modules.Cast<ProcessModule>().SingleOrDefault(m => m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
                            if (module is null || !Within(Path.GetDirectoryName(exe)!, module.FileName))
                                throw new InvalidOperationException("Installed WPF did not load its bundled runtime module: " + name);
                            return module.FileName;
                        }).ToArray();
                        process.CloseMainWindow();
                        if (!process.WaitForExit(10000))
                            throw new TimeoutException("Automatically launched WPF application did not close normally.");
                        return modules;
                    }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("The per-user installer did not automatically launch the real WPF application.");
    }
}
