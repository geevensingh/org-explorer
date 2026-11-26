using System;
using System.Formats.Asn1;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

public static class CleanupProvenanceFixture
{
    public static void Sign(string proofPath, string bundlePath, string rootPath, string revision,
        string runId, string attempt, string hosting = "github-hosted", DateTimeOffset? witnessedAt = null,
        string signerWorkflow = "scale-verification.yml")
    {
        var now = witnessedAt ?? DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Controlled cleanup root,O=Cleanup Fixture",
            rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Controlled signing fixture", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var workflow = $"https://github.com/geevensingh/org-explorer/.github/workflows/{signerWorkflow}@refs/heads/main";
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(workflow));
        request.CertificateExtensions.Add(san.Build());
        Add(request, 8, "https://token.actions.githubusercontent.com");
        Add(request, 9, workflow);
        Add(request, 10, revision);
        Add(request, 11, hosting);
        Add(request, 12, "https://github.com/geevensingh/org-explorer");
        Add(request, 13, revision);
        Add(request, 14, "refs/heads/main");
        Add(request, 15, "1448772658");
        Add(request, 16, "https://github.com/geevensingh");
        Add(request, 17, "11344767");
        Add(request, 18, workflow);
        Add(request, 19, revision);
        Add(request, 20, "workflow_dispatch");
        Add(request, 21, $"https://github.com/geevensingh/org-explorer/actions/runs/{runId}/attempts/{attempt}");
        using var certificate = request.Create(root, now.AddMinutes(-1), now.AddMinutes(5), RandomNumberGenerator.GetBytes(16));
        var proof = File.ReadAllBytes(proofPath);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            _type = "https://in-toto.io/Statement/v1",
            subject = new[] { new { name = "cleanup-proof", digest = new { sha256 = Convert.ToHexString(SHA256.HashData(proof)).ToLowerInvariant() } } },
            predicateType = "https://slsa.dev/provenance/v1",
            predicate = new { buildDefinition = new { buildType = "https://actions.github.io/buildtypes/workflow/v1" } }
        });
        const string type = "application/vnd.in-toto+json";
        var prefix = Encoding.UTF8.GetBytes($"DSSEv1 {type.Length} {type} {payload.Length} ");
        var pae = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(pae, 0);
        payload.CopyTo(pae, prefix.Length);
        var signature = key.SignData(pae, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        using var tsaKey = RSA.Create(2048);
        var tsaRequest = new CertificateRequest("CN=Controlled timestamp fixture", tsaKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        tsaRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        tsaRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, true));
        using var tsaPublic = tsaRequest.Create(root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey),
            now.AddMinutes(-1), now.AddMinutes(5), RandomNumberGenerator.GetBytes(16));
        using var tsa = tsaPublic.CopyWithPrivateKey(tsaKey);
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4"), new Oid("2.16.840.1.101.3.4.2.1"),
            SHA256.HashData(signature), new byte[] { 1 }, now, null, false, null, null, null);
        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()), false);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, tsa);
        signer.IncludeOption = X509IncludeOption.EndCertOnly;
        var signingCertificate = new AsnWriter(AsnEncodingRules.DER);
        signingCertificate.PushSequence();
        signingCertificate.PushSequence();
        signingCertificate.PushSequence();
        signingCertificate.WriteOctetString(SHA256.HashData(tsa.RawData));
        signingCertificate.PopSequence();
        signingCertificate.PopSequence();
        signingCertificate.PopSequence();
        var attribute = new Oid("1.2.840.113549.1.9.16.2.47");
        signer.SignedAttributes.Add(new CryptographicAttributeObject(attribute,
            new AsnEncodedDataCollection(new AsnEncodedData(attribute, signingCertificate.Encode()))));
        cms.ComputeSignature(signer);
        var response = new AsnWriter(AsnEncodingRules.DER);
        response.PushSequence();
        response.PushSequence();
        response.WriteInteger(0);
        response.PopSequence();
        response.WriteEncodedValue(cms.Encode());
        response.PopSequence();
        var timestamp = response.Encode();
        File.WriteAllText(bundlePath, JsonSerializer.Serialize(new
        {
            mediaType = "application/vnd.dev.sigstore.bundle.v0.3+json",
            verificationMaterial = new
            {
                certificate = new { rawBytes = Convert.ToBase64String(certificate.RawData) },
                timestampVerificationData = new { rfc3161Timestamps = new[] { new { signedTimestamp = Convert.ToBase64String(timestamp) } } }
            },
            dsseEnvelope = new
            {
                payload = Convert.ToBase64String(payload), payloadType = type,
                signatures = new[] { new { sig = Convert.ToBase64String(signature), keyid = "" } }
            }
        }));
        File.WriteAllText(rootPath, JsonSerializer.Serialize(new
        {
            mediaType = "application/vnd.dev.sigstore.trustedroot+json;version=0.1",
            certificateAuthorities = new[] { new
            {
                uri = "https://fixture.invalid/fulcio",
                certChain = new { certificates = new[] { new { rawBytes = Convert.ToBase64String(root.RawData) } } },
                validFor = new { start = now.AddDays(-1).ToString("O"), end = now.AddDays(1).ToString("O") }
            } },
            timestampAuthorities = new[] { new
            {
                uri = "https://fixture.invalid/tsa",
                certChain = new { certificates = new[] {
                    new { rawBytes = Convert.ToBase64String(tsa.RawData) },
                    new { rawBytes = Convert.ToBase64String(root.RawData) } } },
                validFor = new { start = now.AddDays(-1).ToString("O"), end = now.AddDays(1).ToString("O") }
            } }
        }) + "\n");
    }

    private static void Add(CertificateRequest request, int id, string value)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteCharacterString(UniversalTagNumber.UTF8String, value);
        request.CertificateExtensions.Add(new X509Extension($"1.3.6.1.4.1.57264.1.{id}", writer.Encode(), false));
    }
}
