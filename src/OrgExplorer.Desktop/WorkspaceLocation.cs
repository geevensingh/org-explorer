using System.IO;
using Velopack.Locators;

namespace OrgExplorer.Desktop;

internal static class WorkspaceLocation
{
    internal static void RequireIndependent(string path)
    {
        var workspace = ResolveDirectory(path);
        var installation = ResolveDirectory(VelopackLocator.Current.RootAppDir ?? AppContext.BaseDirectory);
        if (Contains(installation, workspace) || Contains(workspace, installation))
            throw new IOException("Choose a Workspace outside the application installation.");
    }

    private static bool Contains(string parent, string child) =>
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string ResolveDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var resolved = root;
        foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            var directory = new DirectoryInfo(resolved);
            if (directory.Exists && directory.LinkTarget is not null)
                resolved = directory.ResolveLinkTarget(true)?.FullName
                    ?? throw new IOException("Workspace folder link cannot be resolved.");
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }
}
