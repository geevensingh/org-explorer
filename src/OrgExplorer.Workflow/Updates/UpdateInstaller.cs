using Velopack;
using Velopack.NuGet;

namespace OrgExplorer.Workflow.Updates;

public interface IReleasePackageTransport
{
    Task<Stream> OpenPackageAsync(AuthorizedRelease release, CancellationToken cancellationToken);
}

public interface IUpdateRestart
{
    void ApplyAndRestart(AuthorizedRelease release, string packagePath, string? workspace);
}

public enum UpdateInstallStatus { Idle, Downloading, Verifying, WaitingForLocalWork, Restarting, DownloadFailed, VerificationFailed, LocalWorkBlocked, InstallFailed }

// Only transport and process effects are replaceable; authorization, staging and safe-point coordination are shared.
public sealed class UpdateInstaller(IReleasePackageTransport transport, IUpdateRestart restart, string stagingDirectory,
    Version currentVersion, TimeProvider? clock = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? retainedPackage;
    private string? retainedDirectory;
    public UpdateInstallStatus Status { get; private set; }
    public string Message { get; private set; } = "";
    public event EventHandler? Changed;

    public async Task InstallAsync(AuthorizedRelease release, Func<Action<string?>, Task<bool>> atSafeLocalPoint,
        CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        string? directory = null;
        FileStream? package = null;
        try
        {
            if (Status == UpdateInstallStatus.Restarting || release.Version <= currentVersion) return;
            release.RequireCurrentAuthorization(clock);
            Set(UpdateInstallStatus.Downloading, $"Downloading project-authorized update {release.Version}… Local use remains available.");
            PruneAbandonedDownloads(Path.GetFullPath(stagingDirectory));
            directory = Path.Combine(Path.GetFullPath(stagingDirectory), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, release.FileName);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                await using var input = await transport.OpenPackageAsync(release, timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long length = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    length += read;
                    if (length > release.Size) throw new UpdateVerificationException("Package exceeds the authorized size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                output.Flush(true);
            }
            Set(UpdateInstallStatus.Verifying, $"Verifying signed bytes and Velopack integrity for {release.Version}…");
            // Deny mutation/replacement from verification until the updater has been handed this exact file.
            package = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await release.VerifyPackageAsync(package, cancellationToken, clock).ConfigureAwait(false);
            var zip = new ZipPackage(path);
            var asset = await VelopackAsset.FromZipPackageGenerateChecksumAsync(zip).ConfigureAwait(false);
            if (asset.PackageId != "geevensingh.OrgExplorer" || asset.Version.ToString() != release.Version.ToString() ||
                asset.Type != VelopackAssetType.Full || asset.Size != release.Size ||
                !string.Equals(asset.SHA256, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateVerificationException("Velopack package identity or integrity does not match the authorized full package.");
            release.RequireCurrentAuthorization(clock);
            Set(UpdateInstallStatus.WaitingForLocalWork, $"Update {release.Version} verified. Waiting for pending local saves and Import application; external sync is not required.");
            var handedOff = false;
            var safe = await atSafeLocalPoint(workspace =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                release.RequireCurrentAuthorization(clock);
                restart.ApplyAndRestart(release, path, workspace);
                handedOff = true;
            }).ConfigureAwait(false);
            if (!safe || !handedOff)
            {
                Set(UpdateInstallStatus.LocalWorkBlocked, "Automatic update paused: local writing was NOT safely saved. Resolve the visible save/draft error, then check again. No installation or restart was requested.");
                return;
            }
            retainedPackage = package;
            retainedDirectory = directory;
            package = null;
            directory = null;
            Set(UpdateInstallStatus.Restarting, $"Update {release.Version} handed to the installer; restarting the same Workspace. Installation is not confirmed until the restarted app verifies its version.");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var status = error is UpdateVerificationException or InvalidDataException or System.Xml.XmlException
                ? UpdateInstallStatus.VerificationFailed
                : Status == UpdateInstallStatus.Downloading ? UpdateInstallStatus.DownloadFailed
                : Status == UpdateInstallStatus.Verifying ? UpdateInstallStatus.VerificationFailed : UpdateInstallStatus.InstallFailed;
            Set(status, $"Update {status}: {error.Message} No update is confirmed installed. Local app/data remain available; check again after resolving the error.");
        }
        finally
        {
            package?.Dispose();
            Cleanup(directory);
            gate.Release();
        }
    }

    private void Set(UpdateInstallStatus status, string message)
    {
        Status = status;
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        retainedPackage?.Dispose();
        retainedPackage = null;
        Cleanup(retainedDirectory);
        retainedDirectory = null;
    }

    private static void Cleanup(string? directory)
    {
        if (directory is null) return;
        try { Directory.Delete(directory, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void PruneAbandonedDownloads(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.GetDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            Cleanup(directory);
        }
    }
}
