[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Candidate,
    [string]$GatesDirectory = '.artifacts\release-gates'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. "$PSScriptRoot\Release.ps1"
if ((Invoke-ReleaseGit $root @('rev-parse', 'HEAD')).Trim() -cne $Candidate -or
    @(Invoke-ReleaseGit $root @('status', '--porcelain')).Count -ne 0) { throw 'Candidate verification requires an exact clean checkout.' }
$runId = [long]$env:GITHUB_RUN_ID
$run = Invoke-ReleaseApi "repos/$ReleaseRepository/actions/runs/$runId"
Assert-ReleaseVerification -PerformanceDirectory (Join-Path $GatesDirectory 'performance') -UiScaleDirectory (Join-Path $GatesDirectory 'ui-scale') `
    -Candidate $Candidate -StartedAt ([DateTimeOffset]$run.created_at)
$fixture = Join-Path $root ('.test-workspaces\release-signing-' + [guid]::NewGuid().ToString('N'))
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$signer = $null
$version = (Get-ReleasePlan -Root $root -Candidate $Candidate).version
$installer = Join-Path $root ".artifacts\installer\$version"
$result = Join-Path $root '.artifacts\release-verification'
$renewalDirectory = Join-Path $root '.artifacts\release-verification\renewal-fixture'
New-Item $fixture, $result -ItemType Directory -Force | Out-Null
try {
    $trustFile = Join-Path $fixture 'public-trust.json'
    @{ schemaVersion = 1; manifestUrl = 'https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json'
        keys = @(@{ keyId = 'ephemeral-candidate-verification'; algorithm = 'ES256'; publicKey = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) }) } |
        ConvertTo-Json -Depth 5 | Set-Content $trustFile
    $signer = Open-ReleaseSigningKey -TrustFile $trustFile -KeyId ephemeral-candidate-verification `
        -PrivateKeyBase64 ([Convert]::ToBase64String($key.ExportPkcs8PrivateKey()))
    & "$PSScriptRoot\Package-Windows.ps1" -Version $version -UpdateTrustFile $trustFile
    & "$root\tests\Update.Trust.Tests.ps1" -Assembly (Join-Path $installer 'publish\OrgExplorer.Desktop.dll') -Configuration $trustFile
    $release = Join-Path $installer 'releases'
    $manifest = New-SignedReleaseManifest -ReleaseDirectory $release -Version $version -Key $signer -KeyId ephemeral-candidate-verification -Now ([DateTimeOffset]::UtcNow.AddDays(-8))
    $plan = Get-ReleasePlan -Root $root -Candidate $Candidate
    New-ReleaseProvenance -Plan $plan -ReleaseDirectory $release -CiRunId $runId -VerificationRunId $runId
    Assert-ReleaseInventory -ReleaseDirectory $release -Version $version -Key $signer -KeyId ephemeral-candidate-verification
    Assert-OriginalPackageTrust -PackagePath (Join-Path $release "geevensingh.OrgExplorer-$version-full.nupkg") -TrustFile $trustFile
    $originalProvenance = Get-Content (Join-Path $release 'release-provenance.json') -Raw | ConvertFrom-Json
    $originalAssets = @(Get-ReleaseInventory $release | ForEach-Object {
        @{ name = $_.fileName; size = $_.size; digest = 'sha256:' + $_.sha256 }
    })
    $originalRelease = @{ id = 1; tag_name = $plan.tag; assets = $originalAssets }
    $state = @{ product = $originalProvenance; productRelease = $originalRelease; publication = $originalProvenance; publicationRelease = $originalRelease }
    Assert-PublishedReleaseInventory $originalRelease $originalProvenance
    $authorization = Read-ReleaseAuthorization -Text (Get-Content $manifest -Raw) -TrustFile $trustFile -Product $originalProvenance
    $renewalPlan = Get-MetadataRenewalPlan -State $state -Authorization $authorization -ControllerRevision $Candidate -RunId $runId -Attempt ([int]$env:GITHUB_RUN_ATTEMPT)
    if (!$renewalPlan.publish -or $renewalPlan.version -cne $version) { throw 'Controlled due renewal did not retain the original app version.' }
    New-MetadataRenewalArtifacts -OriginalDirectory $release -Destination $renewalDirectory -Plan $renewalPlan -Key $signer -KeyId ephemeral-candidate-verification `
        -VerificationRunId $runId -ControllerCiRunId $runId
    $renewed = Join-Path $renewalDirectory 'org-explorer-update.win-x64.json'
    $renewedAuthorization = Read-ReleaseAuthorization -Text (Get-Content $renewed -Raw) -TrustFile $trustFile -Product $originalProvenance
    if ((Get-MetadataRenewalPlan -State $state -Authorization $renewedAuthorization -ControllerRevision $Candidate -RunId $runId -Attempt ([int]$env:GITHUB_RUN_ATTEMPT)).publish) {
        throw 'Fresh renewed metadata was not an idempotent evaluation no-op.'
    }
    Copy-Item $renewed (Join-Path $result 'ephemeral-renewal-manifest.json')
    Copy-Item $manifest (Join-Path $result 'ephemeral-signed-manifest.json')
    Copy-Item (Join-Path $release 'package-metadata.json') (Join-Path $result 'package-metadata.json')
    dotnet publish src\OrgExplorer.Desktop -c Release -o (Join-Path $result 'product-check')
    if ($LASTEXITCODE -ne 0) { throw 'Production-isolation publish failed.' }
    & "$root\tests\Update.Trust.Tests.ps1" -Assembly (Join-Path $result 'product-check\OrgExplorer.Desktop.dll') `
        -Configuration "$root\src\OrgExplorer.Desktop\update-trust.json"
    if ((Invoke-ReleaseGit $root @('rev-parse', 'HEAD')).Trim() -cne $Candidate -or
        @(Invoke-ReleaseGit $root @('status', '--porcelain')).Count -ne 0) { throw 'Candidate changed during release verification.' }
} finally {
    if ($signer) { $signer.Dispose() }
    $key.Dispose()
    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
    if (Test-Path $installer) { Remove-Item $installer -Recurse -Force }
    if (Test-Path $renewalDirectory) { Remove-Item $renewalDirectory -Recurse -Force }
}
@{ outcome = 'success'; revision = $Candidate; verificationRunId = $runId; productionPublication = $false
    ephemeralSigningOnly = $true; productionTrustIsolation = $true; installationExecuted = $false
    metadataRenewalVerified = $true; originalAppVersion = $version; renewalAppVersion = $version } |
    ConvertTo-Json | Set-Content (Join-Path $result 'result.json')
Write-Host 'PASS: fresh exact-candidate scale evidence, genuine original-package metadata renewal, ephemeral signing, production-trust isolation and completed cleanup. No publication or installation.'
