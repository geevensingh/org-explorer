# Project-authorized release checks (protocol 1)

Settings offers **Check for updates** independently of Workspace compatibility.
Startup checks default to enabled. The checkbox persists in
`%LocalAppData%\OrgExplorer\update-settings.json`, outside every Workspace.
`ORG_EXPLORER_SETTINGS_DIRECTORY` may relocate this preferences directory; it
does **not** configure trust or the feed. Settings reports the accepted enabled/
disabled preference, not just the checkbox's visual state. A failed preference
save retains the previous setting and reports the failure.

Checks are asynchronous, coalesced while in flight, bounded to 15 seconds and
64 KiB of metadata, and never acquire the Workspace work gate. Offline, HTTP,
rate-limit, malformed/untrusted/expired metadata and configuration failures remain
explicit; they do not mean "up to date." There is no client token or Workspace
data in these requests. A check downloads only the small signed envelope, not
packages. Download/install/restart and save-safe application belong to #18.

## Owner provisioning is still required

The committed `src\OrgExplorer.Desktop\update-trust.json` deliberately has an
empty `keys` array. The app visibly reports **not configured / availability
unknown**, without making a network request. This is not a production signing
key, a fake available update, or publication readiness. The owner must provision
and securely retain the production signing key outside this repository/client.
No production private key has been generated, acquired, installed or committed
by this implementation. #19 must arrange owner-approved secret storage and
signing permissions; an empty GitHub secret list is not a usable signer.

Embed the corresponding **public** configuration in a trusted app build:

```json
{
  "schemaVersion": 1,
  "manifestUrl": "https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json",
  "keys": [
    {
      "keyId": "owner-chosen-stable-key-id",
      "algorithm": "ES256",
      "publicKey": "<base64 DER SubjectPublicKeyInfo for the owner's NIST P-256 PUBLIC key>"
    }
  ]
}
```

The placeholder is not a valid key. `publicKey` is standard SPKI DER, not a
certificate, PEM text, raw curve coordinates or a private PKCS#8 key.
With an owner-supplied .NET `ECDsa` public key, its value is
`Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())`. Key IDs are
case-sensitive, nonblank, at most 80 characters, and unique; at most eight
keys are accepted. Only the NIST P-256 curve and ES256 are supported.

The configuration is an assembly **embedded resource**, not a file the app
loads beside an unverified feed. The normal build uses the committed file.
A trusted distribution/bootstrap may supply a public-only file with the
build-time `-p:UpdateTrustFile=C:\absolute\public-trust.json` property.
This changes that build's trust root; it is not an end-user runtime switch.
Manifest endpoints must be HTTPS, without user information or fragments.
HTTP is allowed only on loopback for controlled feeds. There is no runtime
feed/public-key environment override.

## Envelope and signer contract for #19

Publish `org-explorer-update.win-x64.json` as a stable GitHub Release asset.
The installed app fetches the latest-download link **once**. It does not read
an unverified Velopack index to select a package. A changing `latest` redirect
cannot substitute assets: the signed payload pins a version-specific URL.

The envelope contains exactly these fields (order is irrelevant):

```json
{
  "keyId": "owner-chosen-stable-key-id",
  "algorithm": "ES256",
  "payload": "<base64 of the exact UTF-8 manifest bytes>",
  "signature": "<base64 of the 64-byte ECDSA P-256/SHA-256 P1363 signature>"
}
```

Sign the **decoded payload bytes**, not the base64 string, envelope, hash text or
a later reserialization. In .NET the operation is
`key.SignData(payloadBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)`.
This is the standard 32-byte `r` followed by 32-byte `s` representation, not
ASN.1 DER. Verification uses the matching standard .NET cryptography API.
There is no custom cryptographic algorithm or JSON canonicalization scheme.

Example decoded payload (hash/size must come from the actual packaged bytes):

```json
{
  "protocolVersion": 1,
  "appId": "geevensingh.OrgExplorer",
  "version": "0.2.0",
  "platform": "win-x64",
  "channel": "win",
  "releaseTag": "v0.2.0",
  "issuedAt": "2026-11-10T00:00:00Z",
  "expiresAt": "2026-11-17T00:00:00Z",
  "assets": [
    {
      "kind": "full",
      "fileName": "geevensingh.OrgExplorer-0.2.0-full.nupkg",
      "size": 123456,
      "sha256": "<64 hexadecimal SHA256 characters of the full nupkg>",
      "url": "https://github.com/geevensingh/org-explorer/releases/download/v0.2.0/geevensingh.OrgExplorer-0.2.0-full.nupkg"
    }
  ]
}
```

Protocol 1 is deliberately narrow:

- UTF-8 JSON, no BOM/comments, duplicate properties, missing or additional fields.
  Envelope and decoded payload are each limited to 65,536 bytes and depth 16.
  Unknown keys and algorithms are rejected before metadata is trusted.
- Exactly the app/platform/channel above, a canonical three-component stable
  `major.minor.patch` version (0–999999999 per component, no leading zeroes,
  prerelease, metadata, whitespace or fourth component), and tag `v{version}`.
- Exactly **one full package**. No delta, portable/setup selection, paths,
  mutable latest package links, other repositories or alternate download URLs.
  Filename and URL must match the templates above exactly, including case.
  Size is positive and at most 2 GiB; SHA256 is exactly 64 hexadecimal digits.
- ISO-8601 timestamps supported by .NET's JSON reader, with an explicit timezone.
  Expiry must be later than issuance and the current time; validity is at most
  31 days; issuance allows at most five minutes of future clock skew.
  The signer must refresh expiring metadata even when no new product version
  ships. Expired metadata reports verification failure, not "up to date."
  This bounds freeze/replay exposure; it does not prove that a host never
  withheld a newer release during the signed validity interval.
- Only a strictly newer authorized version is offered. A successfully verified,
  unexpired same/older stable feed can report no newer eligible release. A
  failed check clears any previously displayed authorization/availability.

The ordinary `releases.win.json`, `assets.win.json`, `RELEASES`, setup and
unsigned `package-metadata.json` inventory remain packaging artifacts, not
substitutes for this authorization. #19 must publish a mutually consistent,
immutable versioned package and its signed envelope only after release gates.

## Rotation and the #18 boundary

Downloaded metadata cannot add trust. `keys`, `newKeys`, unsupported metadata
and unknown signer IDs are rejected, even if an adjacent file supplies a key.
Rotation uses **trusted rebootstrap**: ship a package authorized by an existing
trusted key whose embedded configuration includes the next public key. Keep
signing bridge releases with an old trusted key until the supported installed
population trusts the new one; subsequent app builds may remove the old key.
If no existing key can safely authorize a bridge, the owner must distribute a
new installer through an independently trusted bootstrap. Compromised/lost keys
cannot be repaired by asking the unverified feed whom to trust.

`UpdateChecker` returns an immutable `AuthorizedRelease` with an inaccessible
public constructor: version, platform/channel, tag, exact filename/URL, length,
SHA256, signer, payload SHA256 and expiry. `VerifyPackageAsync` checks the actual
stream length and SHA256 and rejects expired authorization before and after
reading. Hash/size mismatch never produces authorization for different bytes.
Do not reconstruct an authorization from a mutable framework feed or cache.

#18 must consume that exact authorization, verify freshly downloaded/staged
bytes and current expiry before apply, constrain any Velopack source to the
authorized full asset (including fallback/delta/cache paths), and keep staging
immutable through application. It must also finish local saves/imports, reject
failed saves, reopen the same Workspace, and handle download/install/restart
failures. A check result alone does not establish those properties.

`VelopackApp.SetAutoApplyOnStartup(false)` remains in the early entry point.
There is no `UpdateManager`, apply-on-exit/restart registration, pending-cache
consumer or package download in this slice. Merely staging a newer package
still cannot authorize implicit startup apply; the existing installed lifecycle
test remains required.

This authenticates updates only to an **already trusted app**. It does not
establish a Windows public-trust legal publisher or eliminate installer warnings.
No OS trust store or protection is changed.

## Controlled verification

Core tests exercise the real checker/cryptography with ephemeral P-256 keys and
signed metadata/byte mutations. Filesystem integration uses actual loopback HTTP,
HTTP failures, disconnected/restarted feeds and durable preference files while
local Workspace work proceeds. No production package or credential is used.

The hosted-only required desktop command remains:

```powershell
.\eng\Run-CiLane.ps1 desktop
```

It creates an ephemeral private key under ignored `.test-workspaces`, embeds
**only its public key and controlled loopback endpoint** in the normal app build,
and runs the actual `update-settings` WPF scenario alongside the other required
scenarios. The harness signs fixtures with that ephemeral key; the product takes
the same production verification path, with no bypass or success flag. The flow
observes pending startup checks, enabled default, editable/saved local data,
authorized availability, persisted disable/reopen/manual check, unknown signer,
tampered signature, unsupported channel, rate-limit and offline failures, then
re-enabled startup reporting a signature failure even with Settings closed.

Finally the lane rebuilds/publishes with the production configuration and compares
the compiled trust resource byte-for-byte against it, rejecting leaked test
trust. Ephemeral private material is removed in `finally` and is outside uploaded
results/product inputs. Missing fixture/bootstrap or unexecuted named scenarios
fail closed. Local builds and non-UI tests do not claim a hosted WPF pass.
