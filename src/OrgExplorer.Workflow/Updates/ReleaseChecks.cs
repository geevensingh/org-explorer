using System.Security.Cryptography;

namespace OrgExplorer.Workflow.Updates;

public interface IReleaseFeed
{
    Task<byte[]> ReadManifestAsync(CancellationToken cancellationToken);
}

public enum ReleaseCheckStatus { NotChecked, Checking, Available, UpToDate, Offline, RateLimited, VerificationFailed, NotConfigured, Failed }

public sealed record ReleaseCheckResult(ReleaseCheckStatus Status, string Message, AuthorizedRelease? Release = null);

public sealed class UpdateVerificationException(string message) : Exception(message);

// Only the verifier can create this immutable authorization. A later feed must not reselect its asset.
public sealed class AuthorizedRelease
{
    public Version Version { get; }
    public string Platform => "win-x64";
    public string Channel => "win";
    public string ReleaseTag { get; }
    public string FileName { get; }
    public Uri PackageUrl { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public string KeyId { get; }
    public string ManifestSha256 { get; }
    public DateTimeOffset ExpiresAt { get; }

    internal AuthorizedRelease(Version version, string tag, string fileName, Uri url, long size, string sha256,
        string keyId, string manifestSha256, DateTimeOffset expiresAt)
    {
        Version = version; ReleaseTag = tag; FileName = fileName; PackageUrl = url; Size = size;
        Sha256 = sha256; KeyId = keyId; ManifestSha256 = manifestSha256; ExpiresAt = expiresAt;
    }

    public async Task VerifyPackageAsync(Stream package, CancellationToken cancellationToken = default, TimeProvider? clock = null)
    {
        RequireCurrentAuthorization(clock);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await package.ReadAsync(buffer, cancellationToken)) > 0)
        {
            length += read;
            if (length > Size) throw new UpdateVerificationException("Package exceeds the authorized size.");
            hash.AppendData(buffer, 0, read);
        }
        if (length != Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(Sha256)))
            throw new UpdateVerificationException("Package bytes do not match the project-authorized size and SHA256.");
        RequireCurrentAuthorization(clock);
    }

    internal void RequireCurrentAuthorization(TimeProvider? clock)
    {
        if ((clock ?? TimeProvider.System).GetUtcNow() >= ExpiresAt)
            throw new UpdateVerificationException("The project authorization has expired. Check again before using this package.");
    }
}
