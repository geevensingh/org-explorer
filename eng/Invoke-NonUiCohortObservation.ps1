param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$RunId,
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$RunAttempt,
    [Parameter(Mandatory)][string]$WorkingDirectory,
    [ValidateSet('ci-timing-v2')][string]$ApprovedPolicyVersion,
    [string]$HistoryRemote = 'https://github.com/geevensingh/org-explorer.git',
    [switch]$AllowLocalFixture
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\FilesystemHealth.ps1"
. "$PSScriptRoot\NonUiCohort.ps1"
New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
try {
    $run = Invoke-HealthApi "actions/runs/$RunId/attempts/$RunAttempt"
    if ($run.id -ne $RunId -or $run.run_attempt -ne $RunAttempt -or $run.status -cne 'completed' -or
        $run.path -cne '.github/workflows/non-ui-cohorts.yml' -or $run.event -cne 'workflow_run' -or
        $run.head_repository.full_name -cne $FilesystemHealthRepository) { throw 'Only a completed trusted automatic cohort can append history.' }
    $name = "non-ui-cohort-plan-$RunId-$RunAttempt"
    $pages = Invoke-HealthApi "actions/runs/$RunId/artifacts?per_page=100" @('--paginate', '--slurp')
    $artifacts = @($pages | ForEach-Object { $_.artifacts } | Where-Object { $_.name -ceq $name })
    if ($artifacts.Count -ne 1 -or $artifacts[0].expired -or
        $artifacts[0].workflow_run.id -ne $RunId -or $artifacts[0].workflow_run.head_sha -cne $run.head_sha) {
        throw 'Exact-attempt plan is unavailable; no execution/candidate qualification can be inferred. Original hosted cancellation/failure is unchanged.'
    }
    $directory = Join-Path $WorkingDirectory 'plan'
    & gh run download $RunId --repo $FilesystemHealthRepository --name $name --dir $directory
    if ($LASTEXITCODE -ne 0) { throw 'Exact-attempt collection plan download failed.' }
    $plan = Get-Content -LiteralPath (Join-Path $directory 'plan.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String -Depth 10
    $automatic = $plan.mode -ceq 'automatic-baseline'
    if ($plan.schemaVersion -notin @(1, 2) -or $plan.revision -cnotmatch '^[0-9a-f]{40}$' -or
        $plan.cohortRunId -cne [string]$RunId -or $plan.cohortRunAttempt -cne [string]$RunAttempt -or
        $plan.cohortId -cne "$($plan.revision)-$RunId-$RunAttempt" -or $plan.authority -cne 'trusted-main-push' -or
        $plan.collect -isnot [bool] -or $plan.mode -cnotin @('observation', 'automatic-baseline') -or $plan.promotion -cne 'disabled' -or
        ($plan.samples -join ',') -cne '1,2,3' -or
        $plan.policyVersion -cne $(if ($automatic) { 'ci-timing-v2' } elseif ($plan.schemaVersion -eq 2) { 'rich-cohort-observation-v2' } else { 'non-ui-cohort-observation-v1' })) {
        throw 'Collection plan is malformed/substituted; it is data, never execution or write authority.'
    }
    $policy = @{}
    if ($automatic) {
        if ($ApprovedPolicyVersion -cne 'ci-timing-v2' -or $plan.schemaVersion -ne 2) {
            throw 'A retrieved plan cannot approve or activate canonical policy; the trusted controller must select the approved version.'
        }
        Assert-TimingControl $plan (Get-TimingControlPath $plan)
        $policy.ApprovedPolicyVersion = $ApprovedPolicyVersion
    }
    if (!$plan.collect) {
        'No new cohort was executed: candidate already durably observed. No prior verdict was rewritten.' |
            Set-Content -LiteralPath (Join-Path $WorkingDirectory 'summary.md')
        return
    }
    & "$PSScriptRoot\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $plan.revision `
        -CiRunId ([long]$plan.ciRunId) -CiRunAttempt ([int]$plan.ciRunAttempt) `
        -CohortRunId $RunId -CohortRunAttempt $RunAttempt -WorkingDirectory (Join-Path $WorkingDirectory 'observation') `
        -HistoryRemote $HistoryRemote -AllowLocalFixture:$AllowLocalFixture -Publish -RichScale:($plan.schemaVersion -eq 2) @policy
} catch {
    $savedSummary = Join-Path $WorkingDirectory 'observation\summary.md'
    $summary = if (Test-Path -LiteralPath $savedSummary -PathType Leaf) {
        (Get-Content -LiteralPath $savedSummary -Raw) + "`n`nObserver error: $([Net.WebUtility]::HtmlEncode($_.Exception.Message)). Original collection/release verdicts remain unchanged."
    } else {
        "### Non-UI cohort history unavailable`n`n$([Net.WebUtility]::HtmlEncode($_.Exception.Message))`n`nOriginal collection/release verdicts are unchanged; unavailable execution evidence is not successful verification."
    }
    $summary | Set-Content -LiteralPath (Join-Path $WorkingDirectory 'summary.md')
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
    throw
}
