using System.Text;
using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow.Updates;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ReleaseUpdateTests
{
    [Fact]
    public async Task Newer_project_authorized_release_pins_the_exact_package_for_the_next_update_slice()
    {
        using var fixture = new SignedReleaseFixture();
        var result = await fixture.Checker().CheckAsync();

        Assert.Equal(ReleaseCheckStatus.Available, result.Status);
        var release = Assert.IsType<AuthorizedRelease>(result.Release);
        Assert.Equal("0.2.0", release.Version.ToString());
        Assert.Equal("win-x64", release.Platform);
        Assert.Equal("win", release.Channel);
        Assert.Equal("v0.2.0", release.ReleaseTag);
        Assert.Equal("geevensingh.OrgExplorer-0.2.0-full.nupkg", release.FileName);
        Assert.Equal(new Uri("https://github.com/geevensingh/org-explorer/releases/download/v0.2.0/geevensingh.OrgExplorer-0.2.0-full.nupkg"), release.PackageUrl);
        await release.VerifyPackageAsync(new MemoryStream(fixture.Package));
    }

    [Fact]
    public async Task Offline_check_reports_unknown_availability_instead_of_up_to_date()
    {
        using var fixture = new SignedReleaseFixture();
        var checker = new UpdateChecker(new OfflineFeed(), UpdateTrust.Parse(fixture.Configuration()), new Version("0.1.0"));

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckStatus.Offline, result.Status);
        Assert.Null(result.Release);
        Assert.Contains("availability is unknown", result.Message);
        Assert.DoesNotContain("up to date", result.Message);
    }

    [Theory]
    [InlineData("unknown-key")]
    [InlineData("different-key")]
    [InlineData("tampered-payload")]
    [InlineData("tampered-signature")]
    [InlineData("algorithm")]
    [InlineData("downloaded-key")]
    [InlineData("duplicate-field")]
    [InlineData("malformed")]
    public async Task Untrusted_or_ambiguous_envelopes_never_authorize_a_release(string attack)
    {
        using var fixture = new SignedReleaseFixture();
        using var attacker = new SignedReleaseFixture();
        var envelope = JsonNode.Parse(fixture.Sign())!.AsObject();
        switch (attack)
        {
            case "unknown-key": envelope["keyId"] = "downloaded-key"; break;
            case "different-key": envelope = JsonNode.Parse(attacker.Sign())!.AsObject(); break;
            case "tampered-payload": envelope["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.Manifest("99.0.0").ToJsonString())); break;
            case "tampered-signature": envelope["signature"] = Convert.ToBase64String(new byte[64]); break;
            case "algorithm": envelope["algorithm"] = "none"; break;
            case "downloaded-key": envelope["keys"] = JsonNode.Parse(attacker.Configuration())!["keys"]!.DeepClone(); break;
        }
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        if (attack == "duplicate-field")
            bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString().Insert(1, "\"keyId\":\"ephemeral-release-key\","));
        if (attack == "malformed") bytes = "not json"u8.ToArray();

        var result = await fixture.Checker(bytes).CheckAsync();

        Assert.Equal(ReleaseCheckStatus.VerificationFailed, result.Status);
        Assert.Null(result.Release);
        Assert.Contains("availability is unknown", result.Message);
    }

    [Theory]
    [InlineData("protocolVersion", "2")]
    [InlineData("appId", "\"other.app\"")]
    [InlineData("platform", "\"win-arm64\"")]
    [InlineData("channel", "\"beta\"")]
    [InlineData("version", "\"0.2.0-beta.1\"")]
    [InlineData("version", "\"00.2.0\"")]
    [InlineData("version", "\"0.2.0+build\"")]
    [InlineData("releaseTag", "\"latest\"")]
    [InlineData("expiresAt", "\"2000-01-01T00:00:00Z\"")]
    [InlineData("issuedAt", "\"2099-01-01T00:00:00Z\"")]
    [InlineData("expiresAt", "\"2099-01-01T00:00:00Z\"")]
    [InlineData("newKeys", "[]")]
    public async Task Even_signed_unsupported_metadata_is_rejected(string field, string value)
    {
        using var fixture = new SignedReleaseFixture();
        var manifest = fixture.Manifest();
        manifest[field] = JsonNode.Parse(value);

        var result = await fixture.Checker(fixture.Sign(manifest)).CheckAsync();

        Assert.Equal(ReleaseCheckStatus.VerificationFailed, result.Status);
        Assert.Null(result.Release);
    }

    [Theory]
    [InlineData("kind", "\"delta\"")]
    [InlineData("fileName", "\"other.nupkg\"")]
    [InlineData("url", "\"https://github.com/geevensingh/org-explorer/releases/latest/download/geevensingh.OrgExplorer-0.2.0-full.nupkg\"")]
    [InlineData("url", "\"https://attacker.example/package.nupkg\"")]
    [InlineData("url", "\"https://github.com/attacker/org-explorer/releases/download/v0.2.0/geevensingh.OrgExplorer-0.2.0-full.nupkg\"")]
    [InlineData("sha256", "\"sha1-is-not-authorization\"")]
    [InlineData("size", "0")]
    [InlineData("size", "2147483649")]
    public async Task Signed_asset_must_be_the_exact_supported_package(string field, string value)
    {
        using var fixture = new SignedReleaseFixture();
        var manifest = fixture.Manifest();
        manifest["assets"]![0]![field] = JsonNode.Parse(value);

        Assert.Equal(ReleaseCheckStatus.VerificationFailed, (await fixture.Checker(fixture.Sign(manifest)).CheckAsync()).Status);
    }

    [Fact]
    public async Task Manifest_cannot_authorize_multiple_or_missing_assets()
    {
        using var fixture = new SignedReleaseFixture();
        var manifest = fixture.Manifest();
        var assets = manifest["assets"]!.AsArray();
        assets.Add(assets[0]!.DeepClone());
        Assert.Equal(ReleaseCheckStatus.VerificationFailed, (await fixture.Checker(fixture.Sign(manifest)).CheckAsync()).Status);
        assets.Clear();
        Assert.Equal(ReleaseCheckStatus.VerificationFailed, (await fixture.Checker(fixture.Sign(manifest)).CheckAsync()).Status);
    }

    [Fact]
    public async Task Authorization_is_independent_of_later_feed_mutation_and_verifies_actual_package_bytes()
    {
        using var fixture = new SignedReleaseFixture();
        var bytes = fixture.Sign();
        var result = await fixture.Checker(bytes).CheckAsync();
        var release = Assert.IsType<AuthorizedRelease>(result.Release);
        Array.Fill(bytes, (byte)0);
        await release.VerifyPackageAsync(new MemoryStream(fixture.Package));
        foreach (var invalid in new[] { fixture.Package[..^1], fixture.Package.Concat(new byte[] { 0 }).ToArray(), new byte[fixture.Package.Length] })
            await Assert.ThrowsAsync<UpdateVerificationException>(() => release.VerifyPackageAsync(new MemoryStream(invalid)));
        Assert.Equal(SignedReleaseFixture.KeyId, release.KeyId);
        Assert.Equal(64, release.ManifestSha256.Length);
    }

    [Fact]
    public async Task Stored_authorization_cannot_verify_package_bytes_after_its_signed_expiry()
    {
        using var fixture = new SignedReleaseFixture();
        var release = Assert.IsType<AuthorizedRelease>((await fixture.Checker().CheckAsync()).Release);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => release.VerifyPackageAsync(
            new MemoryStream(fixture.Package), clock: new FixedClock(release.ExpiresAt.AddSeconds(1))));
    }

    [Fact]
    public async Task Signed_version_with_a_trailing_newline_is_not_a_canonical_release_identity()
    {
        using var fixture = new SignedReleaseFixture();
        var result = await fixture.Checker(fixture.Sign(fixture.Manifest("0.2.0\n"))).CheckAsync();
        Assert.Equal(ReleaseCheckStatus.VerificationFailed, result.Status);
    }

    [Fact]
    public async Task Signed_expiry_without_a_timezone_is_not_an_unambiguous_authorization()
    {
        using var fixture = new SignedReleaseFixture();
        var manifest = fixture.Manifest();
        manifest["expiresAt"] = DateTimeOffset.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ss");
        Assert.Equal(ReleaseCheckStatus.VerificationFailed, (await fixture.Checker(fixture.Sign(manifest)).CheckAsync()).Status);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.0.9")]
    public async Task Only_a_verified_feed_can_report_no_newer_eligible_version(string version)
    {
        using var fixture = new SignedReleaseFixture();
        var result = await fixture.Checker(fixture.Sign(fixture.Manifest(version))).CheckAsync();
        Assert.Equal(ReleaseCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task Key_rotation_requires_trusted_rebootstrap_not_downloaded_trust()
    {
        using var original = new SignedReleaseFixture();
        using var rotated = new SignedReleaseFixture();
        var configuration = JsonNode.Parse(original.Configuration())!;
        var newKey = JsonNode.Parse(rotated.Configuration())!["keys"]![0]!.DeepClone();
        newKey["keyId"] = "rotated";
        configuration["keys"]!.AsArray().Add(newKey);
        var envelope = rotated.Sign(keyId: "rotated");
        Assert.Equal(ReleaseCheckStatus.VerificationFailed, (await original.Checker(envelope).CheckAsync()).Status);
        var rebootstrap = new UpdateChecker(new SignedReleaseFixture.FixtureFeed(envelope),
            UpdateTrust.Parse(configuration.ToJsonString()), new Version("0.1.0"));
        Assert.Equal(ReleaseCheckStatus.Available, (await rebootstrap.CheckAsync()).Status);
    }

    [Fact]
    public async Task Missing_production_trust_is_visible_and_makes_no_feed_request()
    {
        using var fixture = new SignedReleaseFixture();
        var configuration = JsonNode.Parse(fixture.Configuration())!;
        configuration["keys"] = new JsonArray();
        var checker = new UpdateChecker(new MustNotReadFeed(), UpdateTrust.Parse(configuration.ToJsonString()), new Version("0.1.0"));
        var result = await checker.CheckAsync();
        Assert.Equal(ReleaseCheckStatus.NotConfigured, result.Status);
        Assert.Null(result.Release);
        Assert.Contains("no trusted project public keys", result.Message);
    }

    [Fact]
    public async Task Startup_and_manual_checks_coalesce_while_external_io_is_pending()
    {
        using var fixture = new SignedReleaseFixture();
        var feed = new PendingFeed();
        var checker = new UpdateChecker(feed, UpdateTrust.Parse(fixture.Configuration()), new Version("0.1.0"));
        var startup = checker.CheckAsync();
        Assert.False(startup.IsCompleted);
        Assert.Same(startup, checker.CheckAsync());
        feed.Response.SetResult(fixture.Sign());
        Assert.Equal(ReleaseCheckStatus.Available, (await startup).Status);
    }

    private sealed class MustNotReadFeed : IReleaseFeed
    {
        public Task<byte[]> ReadManifestAsync(CancellationToken cancellationToken) =>
            throw new Exception("Missing embedded trust must fail before network I/O.");
    }

    private sealed class PendingFeed : IReleaseFeed
    {
        internal TaskCompletionSource<byte[]> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]> ReadManifestAsync(CancellationToken cancellationToken) => Response.Task.WaitAsync(cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class OfflineFeed : IReleaseFeed
    {
        public Task<byte[]> ReadManifestAsync(CancellationToken cancellationToken) =>
            throw new HttpRequestException("Controlled offline boundary.");
    }
}
