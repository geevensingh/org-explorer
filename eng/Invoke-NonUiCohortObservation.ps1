param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$RunId,
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$RunAttempt,
    [Parameter(Mandatory)][string]$WorkingDirectory,
    [string]$HistoryRemote = 'https://github.com/geevensingh/org-explorer.git',
    [switch]$AllowLocalFixture
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\FilesystemHealth.ps1"
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
    $plan = Get-Content -LiteralPath (Join-Path $directory 'plan.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 10
    if ($plan.schemaVersion -ne 1 -or $plan.revision -cnotmatch '^[0-9a-f]{40}$' -or
        $plan.cohortRunId -cne [string]$RunId -or $plan.cohortRunAttempt -cne [string]$RunAttempt -or
        $plan.cohortId -cne "$($plan.revision)-$RunId-$RunAttempt" -or $plan.authority -cne 'trusted-main-push' -or
        $plan.collect -isnot [bool] -or $plan.mode -cne 'observation' -or $plan.promotion -cne 'disabled') {
        throw 'Collection plan is malformed/substituted; it is data, never execution or write authority.'
    }
    if (!$plan.collect) {
        'No new cohort was executed: candidate already durably observed. No prior verdict was rewritten.' |
            Set-Content -LiteralPath (Join-Path $WorkingDirectory 'summary.md')
        return
    }
    & "$PSScriptRoot\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $plan.revision `
        -CiRunId ([long]$plan.ciRunId) -CiRunAttempt ([int]$plan.ciRunAttempt) `
        -CohortRunId $RunId -CohortRunAttempt $RunAttempt -WorkingDirectory (Join-Path $WorkingDirectory 'observation') `
        -HistoryRemote $HistoryRemote -AllowLocalFixture:$AllowLocalFixture -Publish
} catch {
    $summary = "### Non-UI cohort history unavailable`n`n$([Net.WebUtility]::HtmlEncode($_.Exception.Message))`n`nOriginal collection/release verdicts are unchanged; unavailable execution evidence is not successful verification."
    $summary | Set-Content -LiteralPath (Join-Path $WorkingDirectory 'summary.md')
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
    throw
}
