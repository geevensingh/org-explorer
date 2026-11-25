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
if ($run.id -ne $RunId -or $run.run_attempt -ne $RunAttempt -or
    $run.head_sha -cnotmatch '^[0-9a-f]{40}$' -or $run.path -cne '.github/workflows/ci.yml' -or
    $run.status -cne 'completed') { throw 'A completed exact CI run/attempt is required for filesystem observation.' }
$trusted = $run.event -ceq 'push' -and $run.head_branch -ceq 'main' -and
    $run.head_repository.full_name -ceq $FilesystemHealthRepository
if ($Publish -and !$trusted) { throw 'Only authenticated main-push CI observations can write durable history.' }
$pages = Invoke-HealthApi "actions/runs/$RunId/attempts/$RunAttempt/jobs?per_page=100" @('--paginate', '--slurp')
$jobs = @($pages | ForEach-Object { $_.jobs } | Where-Object { $_.name -ceq 'Real filesystem persistence' })
if ($jobs.Count -ne 1) { throw 'Filesystem observation requires exactly one current-attempt filesystem job.' }
$pages = Invoke-HealthApi "actions/runs/$RunId/artifacts?per_page=100" @('--paginate', '--slurp')
$artifacts = @($pages | ForEach-Object { $_.artifacts } | Where-Object { $_.name -ceq "filesystem-attempt-$RunAttempt" })
$diagnostics = @()
$measurement = $null
$seriesKey = $null
$evidence = @{
    state = 'missing'; artifactId = $null; digest = $null; expiresAt = $null
    url = "https://github.com/$FilesystemHealthRepository/actions/runs/$RunId/attempts/$RunAttempt"
}
if ($artifacts.Count -eq 1) {
    $artifact = $artifacts[0]
    $evidence.artifactId = $artifact.id; $evidence.digest = $artifact.digest; $evidence.expiresAt = $artifact.expires_at
    if ($artifact.expired) {
        $evidence.state = 'expired'; $diagnostics += 'Raw evidence expired; no fresh measurements can be recovered.'
    } elseif ($artifact.workflow_run.id -ne $RunId -or $artifact.workflow_run.head_sha -cne $run.head_sha) {
        $evidence.state = 'rejected'; $diagnostics += 'Artifact run/candidate provenance does not match.'
    } else {
        $directory = Join-Path $WorkingDirectory 'evidence'
        & gh run download $RunId --repo $FilesystemHealthRepository --name "filesystem-attempt-$RunAttempt" --dir $directory
        if ($LASTEXITCODE -ne 0) {
            $evidence.state = 'unavailable'; $diagnostics += 'Filesystem artifact download failed.'
        } else {
            $evidence.state = 'available'
            try {
                $measurement = Get-FilesystemMeasurement $directory $run
                $diagnostics += $measurement.diagnostics
            } catch {
                $evidence.state = 'rejected'
                $diagnostics += "Execution evidence rejected: $($_.Exception.Message)"
            }
            if ($null -ne $measurement) {
                try { $seriesKey = Get-HealthSeriesKey $measurement.execution }
                catch { $diagnostics += $_.Exception.Message }
            }
        }
    }
} else { $diagnostics += 'Missing or duplicate exact-attempt artifact; earlier attempts cannot fill the gap.' }
$history = $null
$historyError = $null
try {
    $history = Open-HealthHistory (Join-Path $WorkingDirectory 'history.git') $HistoryRemote `
        -AllowLocalFixture:$AllowLocalFixture -RecordTypes (Get-LaneHealthRecordTypes)
} catch { $historyError = $_; $diagnostics += "History read failed: $($_.Exception.Message)" }
$validation = switch -CaseSensitive ($jobs[0].conclusion) {
    'success' { if ($null -eq $measurement) { 'incomplete' } else { $measurement.validation } }
    'failure' { 'failure' }
    'cancelled' { 'cancelled' }
    'skipped' { 'skipped' }
    'timed_out' { 'incomplete' }
    default { $diagnostics += 'Missing or unsupported hosted job conclusion.'; 'incomplete' }
}
if ($jobs[0].status -cne 'completed') {
    $validation = 'incomplete'
    $diagnostics += 'Hosted filesystem job is not terminal; no successful verification is established.'
}
$record = [ordered]@{
    schemaVersion = 1; repository = $FilesystemHealthRepository; lane = 'filesystem'
    revision = $run.head_sha; runId = [string]$RunId; runAttempt = [string]$RunAttempt
    authority = if ($trusted) { 'trusted-main-push' } else { 'untrusted-observation' }
    sourceEvent = $run.event; sourceBranch = $run.head_branch; sourceRepository = $run.head_repository.full_name
    workflowPath = $run.path; jobId = $jobs[0].id; jobStatus = $jobs[0].status
    observedAt = [DateTimeOffset]::UtcNow.ToString('O')
    measuredAt = if ($null -ne $measurement) { $measurement.execution.finishedAt } else { $null }
    policyVersion = if ($null -ne $measurement) { $measurement.execution.policyVersion } else { 'unverified' }
    seriesKey = $seriesKey
    configuration = if ($null -ne $measurement) { $measurement.execution.configuration } else { $null }
    validation = $validation; jobOutcome = $jobs[0].conclusion; diagnostics = $diagnostics
    measurements = if ($null -ne $measurement) { $measurement.measurements } else { $null }
    tests = if ($null -ne $measurement) { $measurement.tests } else { $null }
    runnerOutcome = if ($null -ne $measurement) { $measurement.execution.outcome } else { $null }
    runnerComplete = if ($null -ne $measurement) { $measurement.execution.complete } else { $null }
    timingHealth = 'inconclusive'; reference = $null; variability = $null
    historySnapshot = if ($null -ne $history) { $history.tip } else { $null }
    evidence = $evidence
}
if ($null -ne $history) { Set-FilesystemComparison $record $history }
$record.reliability = $null
if ($null -ne $history) {
    $attempts = @($history.records | Where-Object { !$_.Contains('recordType') -and $_.lane -ceq 'filesystem' }) + @($record)
    $record.reliability = [ordered]@{ attempts = $attempts.Count }
    foreach ($outcome in @('success', 'failure', 'incomplete', 'cancelled', 'skipped')) {
        $record.reliability[$outcome] = @($attempts | Where-Object { $_.validation -ceq $outcome }).Count
    }
    $record.reliability.failureRatePercent = 100 * $record.reliability.failure / $attempts.Count
}
$record | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $WorkingDirectory 'record.json')
$publicationError = $null
$publication = 'read-only observation; no authoritative write'
if ($null -ne $historyError) { $publication = 'blocked: history read failed, no write attempted' }
elseif ($Publish) {
    try { $publication = Publish-HealthRecord $history $record }
    catch { $publicationError = $_; $publication = "FAILED: $($_.Exception.Message)" }
}
$summary = Format-FilesystemHealth $record $publication
$summary | Set-Content -LiteralPath (Join-Path $WorkingDirectory 'summary.md')
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
if ($null -ne $historyError) { throw $historyError }
if ($null -ne $publicationError) { throw $publicationError }
