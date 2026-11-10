$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\Release.ps1"
$checks = 0
function Assert([bool]$Condition, [string]$Reason) { if (!$Condition) { throw $Reason }; $script:checks++ }
function Rejects([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert $rejected "Renewal accepted $Reason."
}
$now = [DateTimeOffset]::Parse('2026-11-10T00:00:00Z')
$product = @{ sourceSha = 'a' * 40; version = '0.1.0'; tag = 'v0.1.0'; ciRunId = 42 }
$state = @{
    product = $product
    productRelease = @{ id = 1; tag_name = 'v0.1.0'; assets = @(@{ name = 'release-provenance.json'; digest = 'sha256:' + ('b' * 64) }) }
    publicationRelease = @{ id = 1; tag_name = 'v0.1.0'; assets = @(@{ name = 'org-explorer-update.win-x64.json'; digest = 'sha256:' + ('c' * 64) }) }
}
$authorization = @{ expiresAt = $now.AddDays(8).ToString('O') }
Assert (!(Get-MetadataRenewalPlan -State $null -Authorization $null -ControllerRevision ('d' * 40) -RunId 123 -Attempt 1 -Now $now).publish) 'No public product must be an explicit no-op, not a first app release.'
Assert (!(Get-MetadataRenewalPlan -State $state -Authorization $authorization -ControllerRevision ('d' * 40) -RunId 123 -Attempt 1 -Now $now).publish) 'Fresh metadata must not publish.'
$authorization.expiresAt = $now.AddDays(7).ToString('O')
$plan = Get-MetadataRenewalPlan -State $state -Authorization $authorization -ControllerRevision ('d' * 40) -RunId 123 -Attempt 1 -Now $now
Assert ($plan.publish -and $plan.version -ceq '0.1.0' -and $plan.tag -ceq 'v0.1.0' -and
    $plan.publicationTag -ceq 'metadata-v0.1.0-123-1' -and $plan.sourceSha -ceq ('a' * 40)) 'Renewal must preserve the exact app identity/source and allocate only a metadata tag.'
$authorization.expiresAt = $now.AddDays(-1).ToString('O')
Assert ((Get-MetadataRenewalPlan -State $state -Authorization $authorization -ControllerRevision ('d' * 40) -RunId 123 -Attempt 2 -Now $now).publicationTag -ceq 'metadata-v0.1.0-123-2') 'Overdue/failed attempts recover with a new immutable metadata tag, not a new app version.'
$authorization.expiresAt = $now.AddDays(14).ToString('O')
Assert (!(Get-MetadataRenewalPlan -State $state -Authorization $authorization -ControllerRevision ('d' * 40) -RunId 124 -Attempt 1 -Now $now).publish) 'Repeated evaluation after a successful renewal must be idempotent.'
$originalProvenance = @{ schemaVersion = 1; sourceSha = 'a' * 40; version = '0.1.0'; tag = 'v0.1.0'; ciRunId = 42; verificationRunId = 43 }
$metadataProvenance = @{
    schemaVersion = 1; kind = 'metadata-renewal'; sourceSha = 'a' * 40; version = '0.1.0'; tag = 'v0.1.0'
    publicationTag = 'metadata-v0.1.0-123-1'; originalReleaseId = 1; originalProvenanceSha256 = 'b' * 64
    controllerSha = 'd' * 40; controllerCiRunId = 42; ciRunId = 42; verificationRunId = 123
}
$originalRelease = @{ id = 1; draft = $false; prerelease = $false; tag_name = 'v0.1.0'; published_at = '2026-11-01T00:00:00Z'
    assets = @(@{ id = 10; name = 'release-provenance.json'; digest = 'sha256:' + ('b' * 64) }) }
$metadataRelease = @{ id = 2; draft = $false; prerelease = $false; tag_name = 'metadata-v0.1.0-123-1'; published_at = '2026-11-09T00:00:00Z'
    assets = @(@{ id = 20; name = 'release-provenance.json'; digest = 'sha256:' + ('e' * 64) }) }
$published = @($originalRelease, $metadataRelease)
function gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -cne 'api') { throw 'Unexpected external effect in renewal read fixture.' }
    switch -Regex ($args[1]) {
        '/releases$' { ConvertTo-Json -InputObject @(@($script:published)) -Depth 15 -Compress; return }
        '/releases/assets/10$' { $script:originalProvenance | ConvertTo-Json -Depth 10 -Compress; return }
        '/releases/assets/20$' { $script:metadataProvenance | ConvertTo-Json -Depth 10 -Compress; return }
        '/git/ref/tags/' { @{ object = @{ sha = 'a' * 40; type = 'commit' } } | ConvertTo-Json -Compress; return }
        default { throw "Unexpected external renewal request: $($args[1])" }
    }
}
try {
    $last = Get-LastSuccessfulRelease
    Assert ($last.tag -ceq 'v0.1.0' -and $last.version -ceq '0.1.0') 'Metadata renewal must never become the product-effect/version comparison baseline.'
    $remoteState = Get-ReleasePublicationState
    Assert ($remoteState.publicationRelease.tag_name -ceq 'metadata-v0.1.0-123-1' -and $remoteState.productRelease.id -eq 1) 'Renewal discovery must retain both immutable original app and latest metadata identities.'
    $metadataRelease.published_at = $originalRelease.published_at
    Assert ((Get-ReleasePublicationState).publicationRelease.id -eq 2) 'Same-second publications must select the newer immutable release ID, not array order.'
    $metadataRelease.published_at = '2026-11-09T00:00:00Z'
    foreach ($field in @('sourceSha', 'tag', 'version', 'originalReleaseId', 'originalProvenanceSha256', 'ciRunId')) {
        $old = $metadataProvenance[$field]; $metadataProvenance[$field] = 'wrong'
        Rejects { Get-LastSuccessfulRelease } "metadata with wrong original $field"
        $metadataProvenance[$field] = $old
    }
    $published = @($metadataRelease)
    Rejects { Get-ReleasePublicationState } 'orphan metadata with no actual app publication'
    $published = @()
    Assert ($null -eq (Get-ReleasePublicationState)) 'No public releases must remain a no-op.'
} finally { Remove-Item Function:\gh }
$fixture = Join-Path $PSScriptRoot ('..\.test-workspaces\metadata-renewal-' + [guid]::NewGuid().ToString('N'))
New-Item $fixture -ItemType Directory -Force | Out-Null
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $trustFile = Join-Path $fixture 'public-trust.json'
    @{ schemaVersion = 1; manifestUrl = 'https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json'
        keys = @(@{ keyId = 'original-app-key'; algorithm = 'ES256'; publicKey = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) }) } |
        ConvertTo-Json -Depth 5 | Set-Content $trustFile
    [IO.File]::WriteAllBytes((Join-Path $fixture 'geevensingh.OrgExplorer-0.1.0-full.nupkg'), [Text.Encoding]::UTF8.GetBytes('abc'))
    $manifestPath = New-SignedReleaseManifest -ReleaseDirectory $fixture -Version 0.1.0 -Key $key -KeyId original-app-key -Now ([DateTimeOffset]::UtcNow.AddDays(-20))
    $product.artifacts = @(@{ fileName = 'geevensingh.OrgExplorer-0.1.0-full.nupkg'; size = 3; sha256 = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad' })
    $text = Get-Content $manifestPath -Raw
    $authorization = Read-ReleaseAuthorization -Text $text -TrustFile $trustFile -Product $product -AllowExpired
    Assert ($authorization.releaseTag -ceq 'v0.1.0' -and [DateTimeOffset]$authorization.expiresAt -lt [DateTimeOffset]::UtcNow) 'Authentic expired original metadata can authorize renewal, not client availability.'
    Rejects { Read-ReleaseAuthorization -Text $text -TrustFile $trustFile -Product $product } 'expired client authorization'
    Rejects { Read-ReleaseAuthorization -Text $text.Replace('"ES256"', '"none"') -TrustFile $trustFile -Product $product -AllowExpired } 'untrusted algorithm'
    Rejects { Read-ReleaseAuthorization -Text $text.Insert(1, '"keyId":"original-app-key",') -TrustFile $trustFile -Product $product -AllowExpired } 'ambiguous duplicate envelope fields'
    $wrongType = $text | ConvertFrom-Json
    $wrongType.algorithm = @('ES256')
    Rejects { Read-ReleaseAuthorization -Text ($wrongType | ConvertTo-Json -Compress) -TrustFile $trustFile -Product $product -AllowExpired } 'array-valued envelope algorithm that the actual client rejects'
    $wrongType = $text | ConvertFrom-Json
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($wrongType.payload)) | ConvertFrom-Json
    $payload.version = @('0.1.0')
    $bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 8 -Compress))
    $wrongType.payload = [Convert]::ToBase64String($bytes)
    $wrongType.signature = [Convert]::ToBase64String($key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation))
    Rejects { Read-ReleaseAuthorization -Text ($wrongType | ConvertTo-Json -Compress) -TrustFile $trustFile -Product $product -AllowExpired } 'even owner-signed unsupported array-valued app identity'
    $product.artifacts[0].size = 4
    Rejects { Read-ReleaseAuthorization -Text $text -TrustFile $trustFile -Product $product -AllowExpired } 'original provenance/package binding mismatch'
    $product.artifacts[0].size = 3
    $changedTrust = (Get-Content $trustFile -Raw).Replace('original-app-key', 'current-but-not-original')
    $changedTrust | Set-Content "$fixture\wrong-trust.json"
    Rejects { Read-ReleaseAuthorization -Text $text -TrustFile "$fixture\wrong-trust.json" -Product $product -AllowExpired } 'a current key not embedded in the original app'
} finally { $key.Dispose(); Remove-Item $fixture -Recurse -Force }
Write-Host "PASS: $checks metadata renewal checks."
