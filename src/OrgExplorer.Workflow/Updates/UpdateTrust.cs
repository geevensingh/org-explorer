using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrgExplorer.Workflow.Updates;

public sealed class UpdateTrust
{
    private readonly Dictionary<string, byte[]> keys;
    public Uri ManifestUrl { get; }
    public bool HasKeys => keys.Count > 0;

    private UpdateTrust(Uri manifestUrl, Dictionary<string, byte[]> keys) { ManifestUrl = manifestUrl; this.keys = keys; }

    public static UpdateTrust Parse(string configuration)
    {
        using var document = UpdateJson.Parse(Encoding.UTF8.GetBytes(configuration));
        var root = document.RootElement;
        UpdateJson.Fields(root, "schemaVersion", "manifestUrl", "keys");
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new UpdateVerificationException("Unsupported update trust configuration.");
        var url = new Uri(root.GetProperty("manifestUrl").GetString()!, UriKind.Absolute);
        if ((url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) ||
            url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new UpdateVerificationException("Manifest endpoint must use HTTPS (HTTP is limited to loopback).");
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var list = root.GetProperty("keys");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 8)
            throw new UpdateVerificationException("Invalid trusted key set.");
        foreach (var item in list.EnumerateArray())
        {
            UpdateJson.Fields(item, "keyId", "algorithm", "publicKey");
            var id = item.GetProperty("keyId").GetString()!;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || item.GetProperty("algorithm").GetString() != "ES256")
                throw new UpdateVerificationException("Only identified ES256 verification keys are supported.");
            var bytes = Convert.FromBase64String(item.GetProperty("publicKey").GetString()!);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            if (consumed != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" || !keys.TryAdd(id, bytes))
                throw new UpdateVerificationException("Trusted keys must be unique NIST P-256 public SPKI keys.");
        }
        return new(url, keys);
    }

    internal ECDsa OpenKey(string id)
    {
        if (!keys.TryGetValue(id, out var bytes))
            throw new UpdateVerificationException("The manifest signer is not an embedded trusted project key. A trusted app rebootstrap is required.");
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(bytes, out _);
        return key;
    }
}

internal static class UpdateJson
{
    internal const int MaximumBytes = 65536;

    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new UpdateVerificationException("Update metadata exceeds the supported size.");
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { RejectDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new UpdateVerificationException("Duplicate update metadata fields are not supported.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    internal static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(names.Order()))
            throw new UpdateVerificationException("Missing or unsupported update metadata fields.");
    }
}
