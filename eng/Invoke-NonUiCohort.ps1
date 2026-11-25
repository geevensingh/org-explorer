param(
    [Parameter(Mandatory)][ValidateSet('Plan', 'Summarize')][string]$Stage,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Revision,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$CiRunId,
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$CiRunAttempt,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$CohortRunId,
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$CohortRunAttempt,
    [Parameter(Mandatory)][string]$WorkingDirectory,
    [switch]$Pilot,
    [ValidateSet(3, 5)][int]$ExpectedSamples = 3,
    [hashtable]$TestPolicy,
    [switch]$AllowLocalFixture,
    [switch]$CheckHistory,
    [switch]$ReadHistory,
    [switch]$Publish,
    [string]$HistoryRemote = 'https://github.com/geevensingh/org-explorer.git'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\FilesystemHealth.ps1"
. "$PSScriptRoot\ReleaseGitHub.ps1"
. "$PSScriptRoot\NonUiCohort.ps1"
$ReleaseRepository = $FilesystemHealthRepository
New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
if ($Publish -and ($Pilot -or $null -ne $TestPolicy)) { throw 'Manual pilot/TEST policy cannot publish authoritative live history.' }
if ($null -ne $TestPolicy) {
    if (!$AllowLocalFixture -or $TestPolicy.version -cne 'TEST-non-ui-range-v1') {
        throw 'Explicit TEST policy is allowed only at the isolated fixture boundary, never live calibration.'
    }
    Assert-HealthNumber $TestPolicy.maximumRangeSeconds 'TEST maximumRangeSeconds'
} elseif ($ExpectedSamples -eq 5) { throw 'Live extension eligibility/noise policy is pending calibration; five samples cannot be invented.' }
$run = Invoke-HealthApi "actions/runs/$CiRunId/attempts/$CiRunAttempt"
if ($run.id -ne $CiRunId -or $run.run_attempt -ne $CiRunAttempt) {
    throw 'Required CI run/attempt identity differs from the collection request.'
}
Assert-TrustedCi -Candidate $run.head_sha -CiRunId $CiRunId -RunAttempt $CiRunAttempt -RequireCurrentLayout
if (!$Pilot -and $run.head_sha -cne $Revision) { throw 'Collection requires successful required CI at this exact candidate.' }
$history = $null
$historyError = $null
if ($CheckHistory -or $ReadHistory -or $Publish) {
    try {
        . "$PSScriptRoot\LaneHealth.ps1"
        $recordTypes = Get-LaneHealthRecordTypes
        foreach ($type in (Get-NonUiCohortRecordTypes).GetEnumerator()) { $recordTypes[$type.Key] = $type.Value }
        $history = Open-HealthHistory (Join-Path $WorkingDirectory 'history.git') $HistoryRemote `
            -RecordTypes $recordTypes -AllowLocalFixture:$AllowLocalFixture
    } catch { $historyError = $_ }
}
if ($Stage -ceq 'Plan') {
    if ($null -ne $historyError) { throw $historyError }
    $previous = @()
    if ($null -ne $history) {
        $previous = @($history.records | Where-Object { $_.ContainsKey('recordType') -and $_.recordType -ceq 'performance-cohort' })
    }
    $collect = !@($previous | Where-Object { $_.revision -ceq $Revision }).Count
    if (!$Pilot) {
        $main = Invoke-HealthApi 'git/ref/heads/main'
        if (!$previous.Count -and $main.object.sha -cne $Revision) { throw 'Initial collection must use the qualifying latest main revision.' }
        if ($previous.Count -and $main.object.sha -cne $Revision) {
            $comparison = Invoke-HealthApi "compare/$Revision...$($main.object.sha)"
            if ($comparison.status -cnotin @('ahead', 'identical')) { throw 'Queued candidate is no longer in trusted main ancestry.' }
        }
    }
    $plan = [ordered]@{
        schemaVersion = 1; revision = $Revision
        ciRevision = $run.head_sha; ciRunId = [string]$CiRunId; ciRunAttempt = [string]$CiRunAttempt
        cohortId = "$Revision-$CohortRunId-$CohortRunAttempt"
        cohortRunId = [string]$CohortRunId; cohortRunAttempt = [string]$CohortRunAttempt
        samples = @(1, 2, 3); mode = 'observation'; promotion = 'disabled'
        authority = if ($Pilot) { 'pilot-observation' } else { 'trusted-main-push' }
        policyVersion = 'non-ui-cohort-observation-v1'
        collect = $collect; reason = if ($collect) { 'New qualifying observation' } else { 'Already durably observed; no retry-until-green.' }
    }
    $plan | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $WorkingDirectory 'plan.json')
    return
}
$collection = Invoke-HealthApi "actions/runs/$CohortRunId/attempts/$CohortRunAttempt"
if ($collection.id -ne $CohortRunId -or $collection.run_attempt -ne $CohortRunAttempt -or
    $collection.head_repository.full_name -cne $FilesystemHealthRepository -or
    (!$Pilot -and ($collection.event -cne 'workflow_run' -or $collection.path -cne '.github/workflows/non-ui-cohorts.yml')) -or
    ($Pilot -and ($collection.event -cne 'workflow_dispatch' -or $collection.path -cne '.github/workflows/scale-verification.yml'))) {
    throw 'Only the authenticated trusted collection workflow or explicit read-only pilot can collect samples.'
}
if ($Pilot -and $collection.head_sha -cne $Revision) { throw 'A pilot must execute its explicitly selected trusted controller revision, never arbitrary candidate code.' }
$cohortId = "$Revision-$CohortRunId-$CohortRunAttempt"
$pages = Invoke-HealthApi "actions/runs/$CohortRunId/attempts/$CohortRunAttempt/jobs?per_page=100" @('--paginate', '--slurp')
$jobs = @($pages | ForEach-Object { $_.jobs })
$pages = Invoke-HealthApi "actions/runs/$CohortRunId/artifacts?per_page=100" @('--paginate', '--slurp')
$artifacts = @($pages | ForEach-Object { $_.artifacts })
$samples = @()
for ($number = 1; $number -le $ExpectedSamples; $number++) {
    $name = "non-ui-cohort-$cohortId-sample-$number"
    $matches = @($jobs | Where-Object { $_.name -cmatch "(^| / )Non-UI cohort sample \($number\)$" })
    $sample = [ordered]@{
        number = $number; jobId = $null; runnerId = $null; invocationId = $null; jobOutcome = 'missing'
        runnerOutcome = 'unavailable'; validation = 'incomplete'; measurements = $null; tests = $null
        seriesKey = $null; configuration = $null; diagnostics = @()
        evidence = @{ state = 'missing'; artifactId = $null; digest = $null; expiresAt = $null
            url = "https://github.com/$FilesystemHealthRepository/actions/runs/$CohortRunId/attempts/$CohortRunAttempt" }
    }
    if ($matches.Count -eq 1) {
        $sample.jobId = $matches[0].id; $sample.runnerId = $matches[0].runner_id
        $sample.jobOutcome = $matches[0].conclusion
        $sample.validation = switch -CaseSensitive ($matches[0].conclusion) {
            'failure' { 'failure' }; 'cancelled' { 'cancelled' }; 'skipped' { 'skipped' }; default { 'incomplete' }
        }
    } else { $sample.diagnostics += 'Missing or duplicate independent sample job.' }
    $found = @($artifacts | Where-Object { $_.name -ceq $name })
    if ($found.Count -eq 1) {
        $artifact = $found[0]
        $sample.evidence.artifactId = $artifact.id; $sample.evidence.digest = $artifact.digest
        $sample.evidence.expiresAt = $artifact.expires_at
        if ($artifact.expired) { $sample.evidence.state = 'expired' }
        elseif ($artifact.workflow_run.id -ne $CohortRunId -or $artifact.workflow_run.head_sha -cne $collection.head_sha) {
            $sample.evidence.state = 'rejected'; $sample.diagnostics += 'Artifact controller/run identity differs.'
        } else {
            $directory = Join-Path $WorkingDirectory "sample-$number"
            & gh run download $CohortRunId --repo $FilesystemHealthRepository --name $name --dir $directory
            if ($LASTEXITCODE -ne 0) {
                $sample.evidence.state = 'unavailable'; $sample.diagnostics += 'Artifact download failed.'
            } else {
                $sample.evidence.state = 'available'
                try { Read-NonUiCohortSample $sample $directory $Revision $cohortId ([string]$CohortRunId) ([string]$CohortRunAttempt) }
                catch {
                    $sample.evidence.state = 'rejected'
                    if ($sample.validation -ceq 'success') { $sample.validation = 'incomplete' }
                    $sample.diagnostics += "Complete sample rejected: $($_.Exception.Message)"
                }
                if ($matches.Count -ne 1 -or $matches[0].status -cne 'completed') { $sample.validation = 'incomplete' }
                elseif ($matches[0].conclusion -cne 'success') {
                    $sample.validation = switch -CaseSensitive ($matches[0].conclusion) {
                        'failure' { 'failure' }; 'cancelled' { 'cancelled' }; 'skipped' { 'skipped' }; default { 'incomplete' }
                    }
                }
            }
        }
    } else { $sample.diagnostics += 'Missing or duplicate exact-sample artifact; other executions cannot fill the gap.' }
    $samples += $sample
}
$seen = @{}
foreach ($sample in $samples) {
    foreach ($field in @('jobId', 'runnerId', 'invocationId')) {
        if ($null -eq $sample[$field]) { continue }
        $key = "$field-$($sample[$field])"
        if ($seen.ContainsKey($key)) {
            $previous = $seen[$key]
            foreach ($duplicate in @($previous, $sample)) {
                if ($duplicate.validation -ceq 'success') { $duplicate.validation = 'incomplete' }
                $duplicate.diagnostics += "Independent execution identity was reused: $field."
            }
        } else { $seen[$key] = $sample }
    }
}
$reliability = [ordered]@{ attempts = $samples.Count }
foreach ($outcome in @('success', 'failure', 'incomplete', 'cancelled', 'skipped')) {
    $reliability[$outcome] = @($samples | Where-Object { $_.validation -ceq $outcome }).Count
}
$complete = $reliability.success -eq $ExpectedSamples
$compatible = !@($samples | Where-Object { $null -eq $_.seriesKey }).Count -and
    @($samples | ForEach-Object { $_.seriesKey } | Select-Object -Unique).Count -eq 1
$metrics = Get-NonUiCohortMetrics $samples
$eligibility = if (!$complete) { 'rejected' } elseif (!$compatible) { 'inconclusive' } else { 'pending-calibration' }
$extraSamples = @()
if ($complete -and $compatible -and $null -ne $TestPolicy) {
    if ($metrics.executionSeconds.range -gt $TestPolicy.maximumRangeSeconds) {
        $eligibility = if ($ExpectedSamples -eq 3) { 'inconsistent' } else { 'inconclusive' }
        if ($ExpectedSamples -eq 3) { $extraSamples = @(4, 5) }
    } else { $eligibility = 'TEST-eligible' }
}
$record = [ordered]@{
    schemaVersion = 1; recordType = 'performance-cohort'; repository = $FilesystemHealthRepository
    revision = $Revision; cohortId = $cohortId; cohortRunId = [string]$CohortRunId; cohortRunAttempt = [string]$CohortRunAttempt
    ciRevision = $run.head_sha; ciRunId = [string]$CiRunId; ciRunAttempt = [string]$CiRunAttempt
    controllerRevision = $collection.head_sha; observedAt = [DateTimeOffset]::UtcNow.ToString('O')
    authority = if ($Pilot) { 'pilot-observation' } else { 'trusted-main-push' }
    mode = 'observation'; promotion = 'disabled'; richScaleAcceptance = 'not-executed'
    policyVersion = if ($null -eq $TestPolicy) { 'non-ui-cohort-observation-v1' } else { $TestPolicy.version }
    testPolicy = $TestPolicy; samples = $samples; reliability = $reliability
    validation = if ($complete) { 'success' } else { 'rejected' }
    timingHealth = if ($complete) { 'unbaselined' } else { 'inconclusive' }
    eligibility = $eligibility; metrics = $metrics; extraSamples = $extraSamples
    reference = $null; historySnapshot = $null
    historyDiagnostics = if ($null -ne $historyError) { @("History read failed: $($historyError.Exception.Message)") } else { @() }
}
if ($null -ne $history) { Set-NonUiCohortComparison $record $history }
Assert-NonUiCohortRecord $record "performance-cohorts/$Revision/$CohortRunId/$CohortRunAttempt.json"
$record | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $WorkingDirectory 'record.json')
$publication = 'read-only observation'
$publicationError = $null
if ($null -ne $historyError) { $publication = 'blocked: history read failed' }
elseif ($Publish) {
    try { $publication = Publish-HealthRecord $history $record }
    catch { $publicationError = $_; $publication = "FAILED: $($_.Exception.Message)" }
}
$summary = Format-NonUiCohort $record $publication
$summary | Set-Content -LiteralPath (Join-Path $WorkingDirectory 'summary.md')
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
if ($null -ne $historyError) { throw $historyError }
if ($null -ne $publicationError) { throw $publicationError }
