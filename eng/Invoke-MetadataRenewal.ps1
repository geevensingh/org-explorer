[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Publish')][string]$Mode = 'Plan',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}\z')][string]$ControllerRevision,
    [string]$ExpectedProductRevision,
    [string]$ExpectedPreviousPublicationTag,
    [string]$GatesDirectory = '.artifacts\release-gates'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. "$PSScriptRoot\Release.ps1"
$privateValue = $env:ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8
$env:ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8 = $null
$key = $null
try {
    if ($env:GITHUB_EVENT_NAME -cne 'schedule' -or $env:GITHUB_REPOSITORY -cne $ReleaseRepository -or $env:GITHUB_REF -cne 'refs/heads/main') {
        throw 'Production metadata renewal only runs in the registered main schedule. Use secret-free verification before merge.'
    }
    if ((Invoke-ReleaseGit $root @('rev-parse', 'HEAD')).Trim() -cne $ControllerRevision -or
        @(Invoke-ReleaseGit $root @('status', '--porcelain')).Count -ne 0) { throw 'Renewal needs its exact clean trusted main controller checkout.' }
    $runId = [long]$env:GITHUB_RUN_ID
    $attempt = [int]$env:GITHUB_RUN_ATTEMPT
    $run = Invoke-ReleaseApi "repos/$ReleaseRepository/actions/runs/$runId"
    if ($run.event -cne 'schedule' -or $run.path -cne '.github/workflows/release.yml' -or $run.head_sha -cne $ControllerRevision -or
        $run.head_repository.full_name -cne $ReleaseRepository) { throw 'Renewal execution is not the trusted main scheduled workflow.' }
    $context = Get-MetadataRenewalContext -Root $root -ControllerRevision $ControllerRevision -RunId $runId -Attempt $attempt
    $plan = $context.plan
    if ($Mode -eq 'Plan') {
        if ($env:GITHUB_OUTPUT) {
            "renew=$($plan.publish.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
            if ($plan.publish) {
                "product-revision=$($plan.sourceSha)" >> $env:GITHUB_OUTPUT
                "previous-publication=$($plan.previousPublicationTag)" >> $env:GITHUB_OUTPUT
            }
        }
        Write-Host $plan.reason
        return
    }
    if (!$plan.publish -or $plan.sourceSha -cne $ExpectedProductRevision -or $plan.previousPublicationTag -cne $ExpectedPreviousPublicationTag) {
        throw 'Renewal is no longer due or its original/latest publication changed. Re-evaluate, never replace a newer product/renewal.'
    }
    Assert-ReleaseVerification -PerformanceDirectory (Join-Path $GatesDirectory 'performance') -UiScaleDirectory (Join-Path $GatesDirectory 'ui-scale') `
        -Candidate $plan.sourceSha -StartedAt ([DateTimeOffset]$run.created_at)
    try {
        $key = Open-ReleaseSigningKey -TrustFile $context.trustFile -KeyId $env:ORG_EXPLORER_UPDATE_KEY_ID -PrivateKeyBase64 $privateValue
    } catch {
        throw "Metadata renewal signing is not configured for original app $($plan.tag). Owner: restore secrets.ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8 (base64 DER PKCS#8 P-256) and vars.ORG_EXPLORER_UPDATE_KEY_ID matching a key already embedded in that app, or ship a genuinely trusted bridge product release. Current-main public trust cannot substitute. Nothing was published."
    }
    $privateValue = $null
    Assert-RenewalSnapshot -Plan $plan -ControllerCiRunId $context.controllerCiRunId
    $original = Join-Path $root '.artifacts\metadata-renewal\original'
    $renewal = Join-Path $root '.artifacts\metadata-renewal\renewed'
    Receive-OriginalRelease -State $context.state -Directory $original -TrustFile $context.trustFile
    New-MetadataRenewalArtifacts -OriginalDirectory $original -Destination $renewal -Plan $plan -Key $key -KeyId $env:ORG_EXPLORER_UPDATE_KEY_ID `
        -VerificationRunId $runId -ControllerCiRunId $context.controllerCiRunId
    if ((Invoke-ReleaseGit $root @('rev-parse', 'HEAD')).Trim() -cne $ControllerRevision -or
        @(Invoke-ReleaseGit $root @('status', '--porcelain')).Count -ne 0) { throw 'Renewal controller changed during verification.' }
    Publish-MetadataRenewal -Plan $plan -OriginalDirectory $original -RenewalDirectory $renewal -ControllerCiRunId $context.controllerCiRunId `
        -Key $key -KeyId $env:ORG_EXPLORER_UPDATE_KEY_ID
} finally {
    $privateValue = $null
    $env:ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8 = $null
    if ($key) { $key.Dispose() }
}
