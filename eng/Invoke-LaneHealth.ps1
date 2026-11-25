param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$RunId,
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$RunAttempt,
    [string]$HistoryRemote = 'https://github.com/geevensingh/org-explorer.git',
    [Parameter(Mandatory)][string]$WorkingDirectory,
    [switch]$AllowLocalFixture,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\LaneHealth.ps1"
New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
$run = Invoke-HealthApi "actions/runs/$RunId/attempts/$RunAttempt"
if ($run.id -ne $RunId -or $run.run_attempt -ne $RunAttempt -or $run.status -cne 'completed' -or
    $run.head_sha -cnotmatch '^[0-9a-f]{40}$' -or $run.path -cne '.github/workflows/ci.yml') {
    throw 'Completed exact supported workflow run/attempt required.'
}
$trusted = $run.head_branch -ceq 'main' -and $run.head_repository.full_name -ceq $FilesystemHealthRepository -and
    $run.event -cin @('push', 'schedule', 'workflow_run')
if ($Publish -and !$trusted) { throw 'Candidate/manual/fork data cannot authorize history publication.' }
$pages = Invoke-HealthApi "actions/runs/$RunId/attempts/$RunAttempt/jobs?per_page=100" @('--paginate', '--slurp')
$jobs = @($pages | ForEach-Object { $_.jobs })
$pages = Invoke-HealthApi "actions/runs/$RunId/artifacts?per_page=100" @('--paginate', '--slurp')
$artifacts = @($pages | ForEach-Object { $_.artifacts })
$history = Open-HealthHistory (Join-Path $WorkingDirectory 'history.git') $HistoryRemote `
    -AllowLocalFixture:$AllowLocalFixture -RecordTypes (Get-LaneHealthRecordTypes)
$records = @()
foreach ($job in $jobs) {
    if ($job.name -cne 'Core and view models') { continue }
    $evidence = @{ state = 'missing'; artifactId = $null; digest = $null; expiresAt = $null
        url = "https://github.com/$FilesystemHealthRepository/actions/runs/$RunId/attempts/$RunAttempt" }
    $measurement = $null
    $diagnostics = @()
    $selected = @($artifacts | Where-Object { $_.name -ceq "core-attempt-$RunAttempt" })
    if ($selected.Count -eq 1) {
        $artifact = $selected[0]
        $evidence.artifactId = $artifact.id; $evidence.digest = $artifact.digest; $evidence.expiresAt = $artifact.expires_at
        if ($artifact.expired) { $evidence.state = 'expired' }
        elseif ($artifact.workflow_run.id -ne $RunId -or $artifact.workflow_run.head_sha -cne $run.head_sha) {
            $evidence.state = 'rejected'
        } else {
            $directory = Join-Path $WorkingDirectory 'core'
            & gh run download $RunId --repo $FilesystemHealthRepository --name $artifact.name --dir $directory
            if ($LASTEXITCODE -ne 0) { $evidence.state = 'unavailable'; $diagnostics += 'Artifact download failed.' }
            else {
                try { $measurement = Get-CoreHealthMeasurement $directory $run; $evidence.state = 'available' }
                catch { $evidence.state = 'rejected'; $diagnostics += "Runner evidence rejected: $($_.Exception.Message)" }
            }
        }
    } else { $diagnostics += 'Missing or duplicate current-attempt artifact; stale evidence cannot fill the gap.' }
    $validation = switch -CaseSensitive ($job.conclusion) {
        'success' { if ($measurement) { $measurement.validation } else { 'incomplete' } }
        'failure' { 'failure' }
        'cancelled' { 'cancelled' }
        'skipped' { 'skipped' }
        default { 'incomplete' }
    }
    $seriesKey = $null
    if ($measurement) {
        try { $seriesKey = Get-LaneHealthSeriesKey $measurement }
        catch { $diagnostics += "Comparison unavailable: $($_.Exception.Message)" }
    }
    $record = [ordered]@{
        schemaVersion = 2; recordType = 'lane-health'; repository = $FilesystemHealthRepository; lane = 'core'
        revision = $run.head_sha; controllerRevision = $run.head_sha; runId = [string]$RunId; runAttempt = [string]$RunAttempt
        authority = if ($trusted) { 'trusted-main-push' } else { 'untrusted-observation' }
        sourceEvent = $run.event; sourceBranch = $run.head_branch; sourceRepository = $run.head_repository.full_name
        workflowPath = $run.path; jobId = $job.id; jobName = $job.name; jobOutcome = $job.conclusion
        observedAt = [DateTimeOffset]::UtcNow.ToString('O'); hosted = Get-HostedHealth $job
        measuredAt = if ($measurement) { $measurement.measuredAt } else { $null }
        policyVersion = 'lane-observation-v2'; originalPolicy = if ($measurement) { $measurement.originalPolicy } else { 'unverified' }
        seriesKey = $seriesKey; configuration = if ($measurement) { $measurement.configuration } else { $null }
        selector = if ($measurement) { $measurement.selector } else { $null }
        validation = $validation; verificationScope = 'runner'; diagnostics = $diagnostics
        measurements = if ($measurement) { $measurement.measurements } else { $null }
        tests = if ($measurement) { $measurement.tests } else { $null }
        timingHealth = 'inconclusive'; reference = $null; variability = $null; reliability = $null; recent = @()
        historySnapshot = $history.tip; evidence = $evidence
    }
    Set-LaneHealthComparison $record $history
    $records += $record
}
ConvertTo-Json -InputObject @($records) -Depth 30 | Set-Content (Join-Path $WorkingDirectory 'records.json')
$publication = 'read-only observation'
$errorRecord = $null
if ($Publish) {
    try {
        foreach ($record in $records) {
            $publication = Publish-HealthRecord $history $record
            $history = Open-HealthHistory (Join-Path $WorkingDirectory ('h-' + [guid]::NewGuid().ToString('N'))) `
                $HistoryRemote -AllowLocalFixture:$AllowLocalFixture -RecordTypes (Get-LaneHealthRecordTypes)
        }
    } catch { $errorRecord = $_; $publication = "FAILED: $($_.Exception.Message)" }
}
$summary = Format-LaneHealth $records $publication
$summary | Set-Content (Join-Path $WorkingDirectory 'summary.md')
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content $env:GITHUB_STEP_SUMMARY }
if ($errorRecord) { throw $errorRecord }
