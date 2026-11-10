using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow.Updates;

public sealed class UpdateChecker(IReleaseFeed feed, UpdateTrust trust, Version currentVersion, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private Task<ReleaseCheckResult>? inFlight;

    public Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
            return inFlight is { IsCompleted: false } ? inFlight : inFlight = CheckCoreAsync(cancellationToken);
    }

    private async Task<ReleaseCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (!trust.HasKeys)
            return new(ReleaseCheckStatus.NotConfigured, "Update verification is not configured: no trusted project public keys are embedded; availability is unknown. Local use remains available; obtain a trusted app rebootstrap.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var envelope = await feed.ReadManifestAsync(timeout.Token).ConfigureAwait(false);
            var release = Verify(envelope);
            return release.Version > currentVersion
                ? new(ReleaseCheckStatus.Available, $"Project-authorized update {release.Version} is available (Windows x64 / win). Download, installation and restart are not connected yet.", release)
                : new(ReleaseCheckStatus.UpToDate, $"No newer eligible project-authorized release was found; installed version {currentVersion} is up to date with the verified feed.");
        }
        catch (Exception error) when (error is UpdateVerificationException or JsonException or CryptographicException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return new(ReleaseCheckStatus.VerificationFailed, "Update verification failed; availability is unknown. " + error.Message + " Local use remains available.");
        }
        catch (ReleaseFeedException error)
        {
            return new(error.Status, error.Message + " Availability is unknown. Local use remains available.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return new(ReleaseCheckStatus.Offline, "Update check could not reach the feed (offline, cancelled or timed out); availability is unknown. Local use remains available.");
        }
    }

    private AuthorizedRelease Verify(byte[] bytes)
    {
        using var envelope = UpdateJson.Parse(bytes);
        var root = envelope.RootElement;
        UpdateJson.Fields(root, "keyId", "algorithm", "payload", "signature");
        if (root.GetProperty("algorithm").GetString() != "ES256")
            throw new UpdateVerificationException("Unsupported signature algorithm.");
        var keyId = root.GetProperty("keyId").GetString()!;
        using var key = trust.OpenKey(keyId);
        var payload = Convert.FromBase64String(root.GetProperty("payload").GetString()!);
        var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()!);
        if (signature.Length != 64 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new UpdateVerificationException("The project signature is invalid.");
        using var manifest = UpdateJson.Parse(payload);
        var release = manifest.RootElement;
        UpdateJson.Fields(release, "protocolVersion", "appId", "version", "platform", "channel", "releaseTag", "issuedAt", "expiresAt", "assets");
        if (release.GetProperty("protocolVersion").GetInt32() != 1 ||
            release.GetProperty("appId").GetString() != "geevensingh.OrgExplorer" ||
            release.GetProperty("platform").GetString() != "win-x64" || release.GetProperty("channel").GetString() != "win")
            throw new UpdateVerificationException("Unsupported update protocol, app identity, platform or channel.");
        var versionText = release.GetProperty("version").GetString()!;
        if (!Regex.IsMatch(versionText, @"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z"))
            throw new UpdateVerificationException("Only canonical stable major.minor.patch versions are eligible.");
        var version = Version.Parse(versionText);
        var tag = release.GetProperty("releaseTag").GetString()!;
        if (tag != "v" + versionText) throw new UpdateVerificationException("The signed release tag does not match the version.");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var issued = Timestamp(release.GetProperty("issuedAt"));
        var expires = Timestamp(release.GetProperty("expiresAt"));
        if (issued > now.AddMinutes(5) || expires <= now || expires <= issued || expires - issued > TimeSpan.FromDays(31))
            throw new UpdateVerificationException("The signed manifest is expired or has an unsupported validity interval.");
        var assets = release.GetProperty("assets");
        if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() != 1)
            throw new UpdateVerificationException("Protocol 1 authorizes exactly one full Windows package, never delta or substitute assets.");
        var asset = assets[0];
        UpdateJson.Fields(asset, "kind", "fileName", "size", "sha256", "url");
        var fileName = $"geevensingh.OrgExplorer-{versionText}-full.nupkg";
        var urlText = $"https://github.com/geevensingh/org-explorer/releases/download/{tag}/{fileName}";
        var hash = asset.GetProperty("sha256").GetString()!;
        var size = asset.GetProperty("size").GetInt64();
        if (asset.GetProperty("kind").GetString() != "full" || asset.GetProperty("fileName").GetString() != fileName ||
            asset.GetProperty("url").GetString() != urlText || size <= 0 || size > 2L * 1024 * 1024 * 1024 ||
            !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z"))
            throw new UpdateVerificationException("The package identity, immutable release URL, size or SHA256 is not authorized.");
        return new(version, tag, fileName, new Uri(urlText), size, hash.ToUpperInvariant(), keyId,
            Convert.ToHexString(SHA256.HashData(payload)), expires);
    }

    private static DateTimeOffset Timestamp(JsonElement value)
    {
        if (!Regex.IsMatch(value.GetString()!, @"(?:Z|[+-][0-9]{2}:[0-9]{2})\z"))
            throw new UpdateVerificationException("Signed update times require an explicit timezone.");
        return value.GetDateTimeOffset();
    }
}
