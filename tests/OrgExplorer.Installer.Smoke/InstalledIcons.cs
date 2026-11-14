using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Installer.Smoke;

internal static partial class Program
{
    private static object AssertInstalledIcons(string installation, string executable, string setup)
    {
        var expected = NativeIconResource.ReadExecutableIcon(setup);
        if (!NativeIconResource.ReadExecutableIcon(executable).SequenceEqual(expected))
            throw new InvalidOperationException("The installed application lost the setup's C1 icon resources.");
        var shortcuts = new List<object>();
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        var shell = Activator.CreateInstance(shellType)!;
        try
        {
            foreach (var location in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
            {
                var path = Path.Combine(Environment.GetFolderPath(location), "Org explorer.lnk");
                if (!File.Exists(path)) throw new InvalidOperationException("The existing installed shortcut is missing: " + path);
                object shortcut = ((dynamic)shell).CreateShortcut(path);
                try
                {
                    var target = Environment.ExpandEnvironmentVariables((string)((dynamic)shortcut).TargetPath);
                    var icon = (string)((dynamic)shortcut).IconLocation;
                    if (!Within(installation, target) || !File.Exists(target))
                        throw new InvalidOperationException("The shortcut no longer targets the real per-user installation.");
                    var resolved = AssertIconLocation(installation, string.IsNullOrWhiteSpace(icon) ? target : icon, expected);
                    shortcuts.Add(new { path, target, icon = resolved });
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        using var uninstall = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\geevensingh.OrgExplorer");
        var displayIcon = uninstall?.GetValue("DisplayIcon") as string;
        if (string.IsNullOrWhiteSpace(displayIcon))
            throw new InvalidOperationException("Installed Apps has no real product DisplayIcon.");
        return new
        {
            iconSha256 = Convert.ToHexString(SHA256.HashData(expected)),
            executable,
            shortcuts,
            installedAppsIcon = AssertIconLocation(installation, displayIcon, expected),
            coverage = "Actual installed bytes and shell registrations; visual taskbar/Start/installer review remains separate."
        };
    }

    private static string AssertIconLocation(string installation, string location, byte[] expected)
    {
        var index = 0;
        var separator = location.LastIndexOf(',');
        if (separator >= 0)
        {
            if (!int.TryParse(location[(separator + 1)..].Trim(), out index))
                throw new InvalidOperationException("Invalid shell icon resource index: " + location);
            location = location[..separator];
        }
        var path = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
        if (!Within(installation, path) || !File.Exists(path))
            throw new InvalidOperationException("The shell icon is not a real installed product resource: " + path);
        byte[] actual;
        if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            if (index != 0) throw new InvalidOperationException("An ICO shortcut cannot select a different native resource.");
            actual = File.ReadAllBytes(path);
        }
        else actual = NativeIconResource.ReadExecutableIcon(path, index);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("The installed shortcut/Installed Apps icon does not contain the tested C1 frames: " + path);
        return path + "," + index;
    }
}
