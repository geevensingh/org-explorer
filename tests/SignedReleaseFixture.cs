using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrgExplorer.Workflow.Updates;

namespace OrgExplorer.TestFixtures;

internal sealed class SignedReleaseFixture : IDisposable
{
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public const string KeyId = "ephemeral-release-key";
    public byte[] Package { get; } = "Fictitious full package, never installed."u8.ToArray();

    public string Configuration(string manifestUrl = "https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json") =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1, manifestUrl,
            keys = new[] { new { keyId = KeyId, algorithm = "ES256", publicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()) } }
        });

    public JsonObject Manifest(string version = "0.2.0") => new()
    {
        ["protocolVersion"] = 1,
        ["appId"] = "geevensingh.OrgExplorer",
        ["version"] = version,
        ["platform"] = "win-x64",
        ["channel"] = "win",
        ["releaseTag"] = "v" + version,
        ["issuedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        ["assets"] = new JsonArray(new JsonObject
        {
            ["kind"] = "full",
            ["fileName"] = $"geevensingh.OrgExplorer-{version}-full.nupkg",
            ["size"] = Package.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(Package)),
            ["url"] = $"https://github.com/geevensingh/org-explorer/releases/download/v{version}/geevensingh.OrgExplorer-{version}-full.nupkg"
        })
    };

    public byte[] Sign(JsonObject? manifest = null, string keyId = KeyId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest ?? Manifest());
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            keyId, algorithm = "ES256", payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        });
    }

    public UpdateChecker Checker(byte[]? envelope = null) =>
        new(new FixtureFeed(envelope ?? Sign()), UpdateTrust.Parse(Configuration()), new Version("0.1.0"));

    public void Dispose() => Key.Dispose();

    internal sealed class FixtureFeed(byte[] envelope) : IReleaseFeed
    {
        public Task<byte[]> ReadManifestAsync(CancellationToken cancellationToken) => Task.FromResult(envelope);
    }
}
