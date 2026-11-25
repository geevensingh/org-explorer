param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$RunId,
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$RunAttempt,
    [string]$HistoryRemote = 'https://github.com/geevensingh/org-explorer.git',
    [Parameter(Mandatory)][string]$WorkingDirectory,
    [switch]$AllowLocalFixture,
    [switch]$Publish,
    [switch]$PublishObservations
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\LaneHealth.ps1"
New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
$run = Invoke-HealthApi "actions/runs/$RunId/attempts/$RunAttempt"
if ($run.id -ne $RunId -or $run.run_attempt -ne $RunAttempt -or $run.status -cne 'completed' -or
    $run.head_sha -cnotmatch '^[0-9a-f]{40}$' -or $run.path -cnotin @('.github/workflows/ci.yml',
        '.github/workflows/scale-verification.yml', '.github/workflows/installer-verification.yml',
        '.github/workflows/release.yml', '.github/workflows/release-verification.yml')) {
    throw 'Completed exact supported workflow run/attempt required.'
}
$sourceRepository = if ($run.head_repository -and $run.head_repository.ContainsKey('full_name')) { $run.head_repository.full_name } else { 'unavailable' }
$sourceBranch = if ($run.head_branch) { $run.head_branch } else { 'unavailable' }
$trusted = Test-QualifiedHealthSource $run.event $sourceBranch $sourceRepository
if ($Publish -and $PublishObservations) { throw 'Choose qualified or baseline-ineligible publication, never both.' }
if ($Publish -and !$trusted) { throw 'Candidate/manual/fork data cannot authorize history publication.' }
if ($PublishObservations) {
    if ($trusted) { throw 'Qualified main sources use qualified history, not the ineligible namespace.' }
    Assert-HealthObservationController -AllowLocalFixture:$AllowLocalFixture
}
$writerRevision = git -C (Split-Path $PSScriptRoot -Parent) rev-parse HEAD 2>$null
$writerRevisionError = $null
if ($LASTEXITCODE -ne 0) { $writerRevision = $null; $writerRevisionError = 'Writer revision unavailable; code provenance is incomplete.' }
$pages = Invoke-HealthApi "actions/runs/$RunId/attempts/$RunAttempt/jobs?per_page=100" @('--paginate', '--slurp')
$jobs = @($pages | ForEach-Object { $_.jobs })
$pages = Invoke-HealthApi "actions/runs/$RunId/artifacts?per_page=100" @('--paginate', '--slurp')
$artifacts = @($pages | ForEach-Object { $_.artifacts })
$descriptors = @(Get-LaneHealthJobs $run $jobs)
$downloads = @{}
foreach ($descriptor in $descriptors) {
    $evidence = @{ state = 'missing'; artifactId = $null; digest = $null; expiresAt = $null
        url = "https://github.com/$FilesystemHealthRepository/actions/runs/$RunId/attempts/$RunAttempt" }
    $diagnostics = @()
    $directory = $null
    $candidate = $run.head_sha
    $selected = @($artifacts | Where-Object { $_.name -cmatch $descriptor.artifactPattern })
    if ($selected.Count -eq 1) {
        $artifact = $selected[0]
        if ($artifact.name -cmatch $descriptor.artifactPattern -and $Matches.ContainsKey('candidate')) { $candidate = $Matches.candidate }
        $evidence.artifactId = $artifact.id; $evidence.digest = $artifact.digest; $evidence.expiresAt = $artifact.expires_at
        if ($artifact.expired) { $evidence.state = 'expired' }
        elseif ($artifact.workflow_run.id -ne $RunId -or $artifact.workflow_run.head_sha -cne $run.head_sha) {
            $evidence.state = 'rejected'
        } else {
            $directory = Join-Path $WorkingDirectory $descriptor.lane
            if (Test-Path -LiteralPath $directory) { throw 'Observation working directory already contains candidate evidence.' }
            & gh run download $RunId --repo $FilesystemHealthRepository --name $artifact.name --dir $directory
            if ($LASTEXITCODE -ne 0) { $evidence.state = 'unavailable'; $diagnostics += 'Artifact download failed.' }
            else { $evidence.state = 'available' }
        }
    } else { $diagnostics += 'Missing or duplicate current-attempt artifact; stale evidence cannot fill the gap.' }
    $downloads[$descriptor.lane] = @{ evidence = $evidence; directory = $directory; diagnostics = $diagnostics; candidate = $candidate }
}
$history = $null
$historyError = $null
try {
    $history = Open-HealthHistory (Join-Path $WorkingDirectory 'history.git') $HistoryRemote `
        -AllowLocalFixture:$AllowLocalFixture -RecordTypes (Get-LaneHealthRecordTypes)
    $history.observationWriterAuthorized = [bool]$PublishObservations
} catch { $historyError = $_ }
$records = @()
foreach ($descriptor in $descriptors) {
    $job = $descriptor.job
    $download = $downloads[$descriptor.lane]
    $evidence = $download.evidence
    $diagnostics = @($download.diagnostics)
    if ($writerRevisionError) { $diagnostics += $writerRevisionError }
    $measurement = $null
    if ($descriptor.kind -cne 'controller' -and $evidence.state -ceq 'available') {
        try {
            $measurement = Get-LaneHealthMeasurement $descriptor $downloads $run
            if ($measurement.ContainsKey('diagnostics')) { $diagnostics += $measurement.diagnostics }
        } catch { $evidence.state = 'rejected'; $diagnostics += "Runner evidence rejected: $($_.Exception.Message)" }
    }
    $validation = switch -CaseSensitive ($job.conclusion) {
        'success' { if ($measurement) { $measurement.validation } elseif ($descriptor.kind -ceq 'controller') { 'success' } else { 'incomplete' } }
        'failure' { 'failure' }
        'cancelled' { 'cancelled' }
        'skipped' { 'skipped' }
        default { $diagnostics += "Unsupported/missing hosted job outcome: $($job.conclusion)"; 'incomplete' }
    }
    if ($job.status -cne 'completed') { $validation = 'incomplete'; $diagnostics += 'Missing/nonterminal current-attempt job.' }
    if ($descriptor.kind -ceq 'controller') {
        $diagnostics += 'Controller/job outcome only: optional skips or planning no-ops are not executed scale, product publication or release acceptance.'
    }
    if ($descriptor.lane -clike 'unmapped-*') { $diagnostics += 'Unmapped hosted job: workload/acceptance scope is unknown, not guessed.' }
    if ($historyError) { $diagnostics += "History read failed: $($historyError.Exception.Message)" }
    $seriesKey = $null
    if ($measurement) {
        try { $seriesKey = Get-LaneHealthSeriesKey $measurement }
        catch { $diagnostics += "Comparison unavailable: $($_.Exception.Message)" }
    }
    $record = [ordered]@{
        schemaVersion = 2; recordType = if ($trusted) { 'lane-health' } else { 'lane-observation' }
        repository = $FilesystemHealthRepository; lane = $descriptor.lane
        revision = if ($measurement -and $measurement.ContainsKey('revision')) { $measurement.revision } else { $download.candidate }
        controllerRevision = $run.head_sha; runId = [string]$RunId; runAttempt = [string]$RunAttempt
        writerRevision = $writerRevision
        writerRunId = if ($env:GITHUB_ACTIONS -ceq 'true') { $env:GITHUB_RUN_ID } else { $null }
        writerScope = if ($AllowLocalFixture) { 'isolated fixture' } elseif ($env:GITHUB_ACTIONS -ceq 'true') { 'workflow controller' } else { 'local CLI' }
        candidateState = if ($measurement) { 'validated-runner-binding' } else { 'source-run-identity-only; runner identity unverified' }
        authority = if ($trusted) { 'trusted-main-push' } elseif ($PublishObservations) { 'trusted-controller-observation' } else { 'untrusted-observation' }
        baselineEligible = $false; comparisonInputEligible = [bool]$trusted
        sourceEvent = $run.event; sourceBranch = $sourceBranch; sourceRepository = $sourceRepository
        workflowPath = $run.path; jobId = $job.id; jobName = $job.name; jobOutcome = $job.conclusion
        observedAt = [DateTimeOffset]::UtcNow.ToString('O'); hosted = Get-HostedHealth $job
        measuredAt = if ($measurement) { $measurement.measuredAt } else { $null }
        policyVersion = 'lane-observation-v2'; originalPolicy = if ($measurement) { $measurement.originalPolicy } else { 'unverified' }
        seriesKey = $seriesKey; configuration = if ($measurement) { $measurement.configuration } else { $null }
        selector = if ($measurement) { $measurement.selector } else { $null }
        validation = $validation
        verificationScope = if ($descriptor.kind -ceq 'controller') { 'controller job only' } elseif ($descriptor.kind -ceq 'rehearsal') { 'reported rehearsal only' } else { 'runner' }
        diagnostics = $diagnostics
        measurements = if ($measurement) { $measurement.measurements } else { $null }
        tests = if ($measurement) { $measurement.tests } else { $null }
        timingHealth = 'inconclusive'; reference = $null; variability = $null; reliability = $null; recent = @()
        historySnapshot = if ($history) { $history.tip } else { $null }; evidence = $evidence
    }
    if ($history) { Set-LaneHealthComparison $record $history }
    $records += $record
}
ConvertTo-Json -InputObject @($records) -Depth 30 | Set-Content (Join-Path $WorkingDirectory 'records.json')
$publication = 'read-only observation'
$errorRecord = $null
if ($historyError) { $publication = 'FAILED: history read'; $errorRecord = $historyError }
elseif ($Publish -or $PublishObservations) {
    try {
        $publication = Publish-HealthRecords $history $records
    } catch { $errorRecord = $_; $publication = "FAILED: $($_.Exception.Message)" }
}
$summary = Format-LaneHealth $records $publication
$summary | Set-Content (Join-Path $WorkingDirectory 'summary.md')
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content $env:GITHUB_STEP_SUMMARY }
if ($errorRecord) { throw $errorRecord }
