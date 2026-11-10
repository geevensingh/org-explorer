[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Publish')][string]$Mode = 'Plan',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Candidate,
    [Parameter(Mandatory)][long]$CiRunId,
    [string]$ExpectedVersion,
    [string]$GatesDirectory = '.artifacts\release-gates'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. "$PSScriptRoot\Release.ps1"

if ($env:GITHUB_EVENT_NAME -cne 'workflow_run' -or $env:GITHUB_REPOSITORY -cne $ReleaseRepository -or
    $env:GITHUB_REF -cne 'refs/heads/main') { throw 'Production release runs only in the trusted main workflow_run path; use the secret-free verification bridge before merge.' }
$event = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
if ($event.workflow_run.id -ne $CiRunId -or $event.workflow_run.head_sha -cne $Candidate) { throw 'Release invocation differs from its triggering CI event.' }
$revision = (Invoke-ReleaseGit $root @('rev-parse', 'HEAD')).Trim()
if ($revision -cne $Candidate -or @(Invoke-ReleaseGit $root @('status', '--porcelain')).Count -ne 0) { throw 'Release requires a clean checkout of the exact triggering candidate.' }
Assert-TrustedMainCi -Candidate $Candidate -CiRunId $CiRunId
$last = Get-LastSuccessfulRelease
$reserved = @(Get-ReservedReleaseVersions)
$plan = Get-ReleasePlan -Root $root -Candidate $Candidate -LastRelease $last -ReservedVersions $reserved
if ($Mode -eq 'Plan') {
    New-Item '.artifacts' -ItemType Directory -Force | Out-Null
    $plan | ConvertTo-Json -Depth 6 | Set-Content '.artifacts\release-plan.json'
    if ($env:GITHUB_OUTPUT) {
        "publish=$($plan.publish.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
        "version=$($plan.version)" >> $env:GITHUB_OUTPUT
    }
    Write-Host "$($plan.reason) Candidate $Candidate; next unreserved version $($plan.version)."
    return
}
if (!$plan.publish -or $ExpectedVersion -cne $plan.version) { throw 'The publication plan/version changed; obtain fresh verification for the current plan.' }
$verificationRunId = [long]$env:GITHUB_RUN_ID
$verificationRun = Invoke-ReleaseApi "repos/$ReleaseRepository/actions/runs/$verificationRunId"
if ($verificationRun.event -cne 'workflow_run' -or $verificationRun.path -cne '.github/workflows/release.yml' -or
    $verificationRun.head_sha -cne $Candidate -or $verificationRun.head_repository.full_name -cne $ReleaseRepository) {
    throw 'Scale evidence must belong to this trusted exact-candidate release execution.'
}
Assert-ReleaseVerification -PerformanceDirectory (Join-Path $GatesDirectory 'performance') -UiScaleDirectory (Join-Path $GatesDirectory 'ui-scale') `
    -Candidate $Candidate -StartedAt ([DateTimeOffset]::Parse($verificationRun.created_at))
$key = $null
try {
    $key = Open-ReleaseSigningKey -TrustFile "$root\src\OrgExplorer.Desktop\update-trust.json" `
        -KeyId $env:ORG_EXPLORER_UPDATE_KEY_ID -PrivateKeyBase64 $env:ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8
    $env:ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8 = $null
    & "$PSScriptRoot\Package-Windows.ps1" -Version $plan.version
    $release = Join-Path $root ".artifacts\installer\$($plan.version)\releases"
    & "$root\tests\Update.Trust.Tests.ps1" -Assembly ".artifacts\installer\$($plan.version)\publish\OrgExplorer.Desktop.dll" `
        -Configuration 'src\OrgExplorer.Desktop\update-trust.json'
    $null = New-SignedReleaseManifest -ReleaseDirectory $release -Version $plan.version -Key $key -KeyId $env:ORG_EXPLORER_UPDATE_KEY_ID
    New-ReleaseProvenance -Plan $plan -ReleaseDirectory $release -CiRunId $CiRunId -VerificationRunId $verificationRunId
    if ((Invoke-ReleaseGit $root @('rev-parse', 'HEAD')).Trim() -cne $Candidate -or
        @(Invoke-ReleaseGit $root @('status', '--porcelain')).Count -ne 0) { throw 'Candidate changed while packaging.' }
    Publish-Release -Plan $plan -ReleaseDirectory $release -CiRunId $CiRunId -Key $key -KeyId $env:ORG_EXPLORER_UPDATE_KEY_ID
} finally {
    $env:ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8 = $null
    if ($key) { $key.Dispose() }
}
