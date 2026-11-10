using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.NuGet;
using Velopack.Sources;

namespace OrgExplorer.Workflow.Updates;

public sealed class VelopackUpdateRestart(UpdateRestartJournal journal, IVelopackLocator? locator = null) : IUpdateRestart
{
    public void ApplyAndRestart(AuthorizedRelease release, string packagePath, string? workspace)
    {
        var installed = locator ?? VelopackLocator.Current;
        if (installed.AppId != "geevensingh.OrgExplorer" || installed.CurrentlyInstalledVersion is null ||
            installed.Channel != release.Channel || string.IsNullOrEmpty(installed.PackagesDir))
            throw new InvalidOperationException("Automatic installation requires the installed Windows x64 / win app. Local use remains available.");
        var version = SemanticVersion.Parse(release.Version.ToString());
        if (version <= installed.CurrentlyInstalledVersion)
            throw new InvalidOperationException("This update version is already installed; no restart was requested.");
        if (!Path.IsPathFullyQualified(packagePath) || Path.GetFileName(packagePath) != release.FileName || !File.Exists(packagePath))
            throw new UpdateVerificationException("The exact authorized staged full package is missing.");
        // A rooted internal path forces Update.exe's explicit --package argument. Never use null/latest/cache selection.
        var asset = new VelopackAsset
        {
            PackageId = installed.AppId, Version = version, Type = VelopackAssetType.Full,
            FileName = packagePath, Size = release.Size, SHA256 = release.Sha256
        };
        var manager = new PinnedManager(installed);
        manager.Verify(asset, packagePath);
        release.RequireCurrentAuthorization(null);
        journal.Begin(release, workspace);
        try
        {
            release.RequireCurrentAuthorization(null);
            manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true);
        }
        catch (Exception error)
        {
            journal.Fail("Installer/restart could not start: " + error.Message + " No update is confirmed installed. Local work is retained.");
            throw;
        }
    }

    private sealed class PinnedManager(IVelopackLocator installed) : UpdateManager(new NoFeed(), new UpdateOptions { ExplicitChannel = "win" }, installed)
    {
        public void Verify(VelopackAsset asset, string path) => VerifyPackageChecksumAsync(asset, path).GetAwaiter().GetResult();
    }

    private sealed class NoFeed : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
            throw new UpdateVerificationException("A second release feed is forbidden.");
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken = default) =>
            throw new UpdateVerificationException("Framework downloads, deltas and cached substitutions are forbidden.");
    }
}
