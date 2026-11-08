using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace OrgExplorer.Installer.Smoke;

internal static class Program
{
    private const string PackageInput = @"C:\PackageInput";
    private const string SmokeRunner = @"C:\SmokeInput\OrgExplorer.Desktop.Smoke.exe";
    private const string Root = @"C:\OrgExplorerInstallerTest";
    private const string Results = @"C:\TestResults";

    private static int Main()
    {
        if (Environment.UserName != "WDAGUtilityAccount" ||
            !File.Exists(Path.Combine(PackageInput, "package-metadata.json")) || !File.Exists(SmokeRunner))
        {
            Console.Error.WriteLine("Installer execution requires the disposable Windows Sandbox test account and dedicated mapped inputs. No host installation was attempted.");
            return 1;
        }

        var evidence = new Dictionary<string, object>
        {
            ["isolation"] = "Disposable Windows Sandbox; network disabled; dedicated read-only package/harness/smoke inputs",
            ["user"] = Environment.UserName,
            ["result"] = "failed"
        };
        var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "geevensingh.OrgExplorer");
        var workspace = Path.Combine(Root, "Workspace");
        var exe = Path.Combine(install, "current", "OrgExplorer.Desktop.exe");
        var setup = Path.Combine(PackageInput, "geevensingh.OrgExplorer-win-Setup.exe");
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !Environment.Is64BitOperatingSystem)
                throw new InvalidOperationException("The clean Sandbox must run Windows 11 x64.");
            if (Directory.Exists(install))
                throw new InvalidOperationException("The per-user installation must be initially absent.");
            if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App")))
                throw new InvalidOperationException("Use a clean Sandbox without a separately installed desktop runtime.");
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackageInput, "package-metadata.json")));
            var version = metadata.RootElement.GetProperty("version").GetString()!;
            var setupAsset = metadata.RootElement.GetProperty("artifacts").EnumerateArray()
                .Single(a => a.GetProperty("fileName").GetString() == Path.GetFileName(setup));
            using (var input = File.OpenRead(setup))
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), setupAsset.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Mapped setup bytes do not match the built artifact inventory.");
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
            evidence["installDirectory"] = install;
            evidence["workspace"] = workspace;

            Run(setup, "install", "--log", Path.Combine(Results, "install.log"));
            if (!File.Exists(exe) || !File.Exists(Path.Combine(install, "Update.exe")))
                throw new InvalidOperationException("Default per-user installation did not contain the built app and updater.");
            CloseAutomaticallyLaunchedDesktop(exe, version);
            evidence["automaticRealWpfLaunch"] = true;
            Run(SmokeRunner, "desktop-create", exe, workspace, "create", version);

            Run(setup, "replace", "--silent", "--log", Path.Combine(Results, "replace.log"));
            Run(SmokeRunner, "desktop-after-replace", exe, workspace, "reopen", version);
            evidence["workspaceSurvivedReplacement"] = true;

            Uninstall(install, "uninstall");
            if (File.Exists(exe))
                throw new InvalidOperationException("Uninstall left the installed application behind.");
            if (!File.Exists(Path.Combine(workspace, "workspace.json")))
                throw new InvalidOperationException("Uninstall removed the independent Workspace.");
            Run(setup, "reinstall", "--silent", "--log", Path.Combine(Results, "reinstall.log"));
            Run(SmokeRunner, "desktop-after-uninstall-reinstall", exe, workspace, "reopen", version);
            evidence["workspaceSurvivedUninstall"] = true;
            Uninstall(install, "cleanup");
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
        }
        return (string)evidence["result"] == "passed" ? 0 : 1;
    }

    private static void Uninstall(string install, string label) =>
        Run(Path.Combine(install, "Update.exe"), label, "--silent", "--rootDir", install, "--log", Path.Combine(Results, label + ".log"), "uninstall");

    private static void Run(string file, string label, params string[] args)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(label + " did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            if (!process.WaitForExit(120000))
                throw new TimeoutException(label + " exceeded two minutes.");
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

    private static void CloseAutomaticallyLaunchedDesktop(string exe, string version)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            var found = false;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName != exe || process.MainWindowHandle == IntPtr.Zero) continue;
                        if (!process.MainWindowTitle.Contains($"Org explorer {version} (Windows x64 / win)"))
                            throw new InvalidOperationException("Automatically launched desktop metadata did not match the installed package.");
                        found = true;
                        process.CloseMainWindow();
                        if (!process.WaitForExit(10000))
                            throw new TimeoutException("Automatically launched WPF application did not close normally.");
                    }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
            }
            if (found) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("The per-user installer did not automatically launch the real WPF application.");
    }
}
