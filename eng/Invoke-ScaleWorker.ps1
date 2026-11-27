param(
    [Parameter(Mandatory)][ValidateSet('Start', 'Execute', 'Complete')][string]$Stage,
    [Parameter(Mandatory)][ValidateSet('non-ui-performance', 'prefix', 'tail')][string]$Worker,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedRevision,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [string]$PrefixResultsDirectory,
    [string]$CohortId,
    [int]$Sample,
    [switch]$ExtendedObservation,
    [string]$AttestationBundle
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = [IO.Path]::GetFullPath($ResultsDirectory, $root)
if (!$results.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Scale worker results must remain inside this checkout.'
}
$handle = Join-Path $results 'worker-process.json'
$reportPath = Join-Path $results 'result.json'
$receiptPath = Join-Path $results 'worker-exit.json'
$logPath = Join-Path $results 'worker.log'
$errorPath = Join-Path $results 'worker-errors.log'
if ($Worker -ceq 'non-ui-performance' -and $ExtendedObservation) { throw 'Non-UI work has no extended-observation selection.' }

if ($Stage -ceq 'Execute') {
    $entry = if ($Worker -ceq 'non-ui-performance') { 'Run-Performance.ps1' } else { 'Run-UiScale.ps1' }
    $parameters = @{ ExpectedRevision = $ExpectedRevision; ResultsDirectory = $results; PrepareCleanupAuthentication = $true }
    if ($Worker -cne 'non-ui-performance') { $parameters.Worker = $Worker; $parameters.ExtendedObservation = $ExtendedObservation }
    if ($PrefixResultsDirectory) { $parameters.PrefixResultsDirectory = $PrefixResultsDirectory }
    if ($CohortId) { $parameters.CohortId = $CohortId; $parameters.Sample = $Sample }
    $exitCode = 0
    try { & (Join-Path $PSScriptRoot $entry) @parameters }
    catch { $exitCode = 1; [Console]::Error.WriteLine($_.ToString()) }
    $runDirectory = $null
    if (Test-Path -LiteralPath $reportPath) { $runDirectory = (Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json).runDirectory }
    $receipt = @{ processId = $PID; processStartedAt = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('O')
        revision = $ExpectedRevision; worker = $Worker; runDirectory = $runDirectory; exitCode = $exitCode }
    [IO.File]::WriteAllText("$receiptPath.writing", ($receipt | ConvertTo-Json))
    [IO.File]::Move("$receiptPath.writing", $receiptPath, $true)
    exit $exitCode
}

if ($Stage -ceq 'Start') {
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    if (Test-Path -LiteralPath (Join-Path $results 'cleanup-proof.json')) {
        throw 'A previous cleanup proof cannot supply a fresh worker invocation; select a new results directory.'
    }
    . "$PSScriptRoot\TimingPolicy.ps1"
    $policy = Get-ApprovedTimingPolicy (Get-SourceTimingPolicyVersion $ExpectedRevision)
    $ceiling = if ($Worker -ceq 'prefix') { $policy.prefixSafetySeconds } else { $policy.tailSafetySeconds }
    $started = if ($env:SCALE_STARTED_AT) { [DateTimeOffset]::Parse($env:SCALE_STARTED_AT) } else { [DateTimeOffset]::UtcNow }
    $deadline = $started.AddSeconds($ceiling)
    $arguments = @('-NoProfile', '-File', $PSCommandPath, '-Stage', 'Execute', '-Worker', $Worker,
        '-ExpectedRevision', $ExpectedRevision, '-ResultsDirectory', $results)
    if ($PrefixResultsDirectory) { $arguments += @('-PrefixResultsDirectory', $PrefixResultsDirectory) }
    if ($CohortId) { $arguments += @('-CohortId', $CohortId, '-Sample', [string]$Sample) }
    if ($ExtendedObservation) { $arguments += '-ExtendedObservation' }
    $previousStarted = $env:SCALE_STARTED_AT
    try {
        $env:SCALE_STARTED_AT = $started.ToString('O')
        $process = Start-Process (Get-Process -Id $PID).Path -PassThru -NoNewWindow `
            -ArgumentList @($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) `
            -RedirectStandardOutput $logPath -RedirectStandardError $errorPath
    } finally { $env:SCALE_STARTED_AT = $previousStarted }
    try {
        $metadata = @{ processId = $process.Id; processStartedAt = $process.StartTime.ToUniversalTime().ToString('O')
            worker = $Worker; revision = $ExpectedRevision; deadlineAt = $deadline.ToString('O') }
        $metadata | ConvertTo-Json | Set-Content -LiteralPath $handle
        $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds
        if ($remaining -le 0) { throw 'Scale setup exhausted its original worker budget.' }
        if (!$process.WaitForExit([int]$remaining)) { throw 'Scale worker did not exit within its original budget.' }
        $metadata.observedExitCode = $process.ExitCode
        $metadata | ConvertTo-Json | Set-Content -LiteralPath $handle
        if ($process.ExitCode -ne 0) {
            Get-Content -LiteralPath (Join-Path $results 'worker.log')
            Get-Content -LiteralPath (Join-Path $results 'worker-errors.log')
            throw "Scale worker exited with code $($process.ExitCode)."
        }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $needsSignature = $report.phase -ceq 'cleanup-authentication'
        if ($env:GITHUB_OUTPUT) {
            "needs_cleanup_signature=$($needsSignature.ToString().ToLowerInvariant())" | Add-Content $env:GITHUB_OUTPUT
            if ($needsSignature) {
                "cleanup_proof=$(Join-Path $results 'cleanup-proof.json')" | Add-Content $env:GITHUB_OUTPUT
            }
        }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    return
}

$metadata = Get-Content -LiteralPath $handle -Raw | ConvertFrom-Json
if ($metadata.worker -cne $Worker -or $metadata.revision -cne $ExpectedRevision -or $metadata.processId -le 0) {
    throw 'Worker completion belongs to a different source/invocation.'
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($report.revision -cne $ExpectedRevision -or $report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$') {
    throw 'Worker completion lacks its own exact-source run directory.'
}
if (!$report.acceptanceDeadlineAt -or [DateTimeOffset]$report.acceptanceDeadlineAt -ne [DateTimeOffset]$metadata.deadlineAt) {
    throw 'Worker completion differs from its original supervised deadline.'
}
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if (($metadata.observedExitCode -isnot [int] -and $metadata.observedExitCode -isnot [long]) -or $metadata.observedExitCode -ne 0 -or
    $receipt.processId -ne $metadata.processId -or $receipt.processStartedAt -cne $metadata.processStartedAt -or
    $receipt.revision -cne $ExpectedRevision -or $receipt.worker -cne $Worker -or
    $receipt.runDirectory -cne $report.runDirectory -or
    ($receipt.exitCode -isnot [long] -and $receipt.exitCode -isnot [int]) -or $receipt.exitCode -ne 0) {
    throw 'Scale worker completion lacks its actual successful supervised exit.'
}
if ($report.phase -ceq 'cleanup-authentication' -and !$AttestationBundle) {
    throw 'Prepared cleanup requires the original attestation before completion.'
}
if ($AttestationBundle) {
    if ($report.phase -cne 'cleanup-authentication') { throw 'Only an original prepared post-verification refusal may receive its signed proof.' }
    $destination = Join-Path $results "$($report.runDirectory)\cleanup-attestation.json"
    [IO.File]::WriteAllText("$destination.writing", (Get-Content -LiteralPath $AttestationBundle -Raw))
    [IO.File]::Move("$destination.writing", $destination, $true)
    . "$PSScriptRoot\ScaleCleanupPolicy.ps1"
    $prepared = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -AsHashtable -Depth 40 -DateKind String
    Complete-ScalePreparedCleanup $prepared $results
}
$process = Get-Process -Id $metadata.processId -ErrorAction SilentlyContinue
if ($process) {
    try {
        if ($process.StartTime.ToUniversalTime().ToString('O') -cne $metadata.processStartedAt) {
            throw 'The original worker PID has been reused; it is not an owned process.'
        }
        $remaining = ([DateTimeOffset]$metadata.deadlineAt - [DateTimeOffset]::UtcNow).TotalMilliseconds
        if (!$process.HasExited -and ($remaining -le 0 -or !$process.WaitForExit([int]$remaining))) {
            throw 'Known running worker cannot supply completed verification within the original budget.'
        }
    } finally { $process.Dispose() }
}
Get-Content -LiteralPath (Join-Path $results 'worker.log')
Get-Content -LiteralPath (Join-Path $results 'worker-errors.log')
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.processId -ne $metadata.processId -or $receipt.processStartedAt -cne $metadata.processStartedAt -or
    $receipt.revision -cne $ExpectedRevision -or $receipt.worker -cne $Worker -or
    $receipt.runDirectory -cne $report.runDirectory -or ($receipt.exitCode -isnot [long] -and $receipt.exitCode -isnot [int]) -or
    $receipt.exitCode -ne 0 -or $report.outcome -cne 'success' -or $report.phase -cne 'finished') {
    throw "Scale worker did not finish successfully: $($report.failure)"
}
