$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$root\eng\PerformanceSuite.ps1"

$results = Join-Path $root ".ci-results\performance-contract-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $results | Out-Null
try {
    function Write-Result([string] $Outcome = 'Passed', [int] $Total = 1, [string] $Name = $PerformanceTests[0]) {
        @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testName="$Name" outcome="$Outcome" /></Results>
  <ResultSummary outcome="Completed"><Counters total="$Total" executed="$Total" passed="$Total" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
"@ | Set-Content (Join-Path $results 'results.trx')
    }
    function Assert-Rejected([scriptblock] $Action, [string] $Reason) {
        $rejected = $false
        try { & $Action } catch { $rejected = $true }
        if (!$rejected) { throw "Performance results accepted $Reason." }
    }
    function Invoke-RunnerProbe([string[]] $Arguments, [string] $Log) {
        $quoted = @('-NoProfile', '-File', "$root\eng\Run-Performance.ps1") + $Arguments |
            ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
        $process = Start-Process pwsh -ArgumentList $quoted -PassThru -NoNewWindow -RedirectStandardOutput $Log -RedirectStandardError "$Log-errors"
        try {
            if (!$process.WaitForExit(30000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw 'Runner rejection probe did not terminate within its bounded test deadline.'
            }
            return $process.ExitCode
        } finally {
            $process.Dispose()
        }
    }
    Assert-Rejected { Assert-PerformanceResults $results } 'missing results'
    Write-Result
    Assert-PerformanceResults $results
    foreach ($outcome in @('Failed', 'NotExecuted', 'Skipped', 'Timeout', 'Aborted', '')) {
        Write-Result -Outcome $outcome
        Assert-Rejected { Assert-PerformanceResults $results } $outcome
    }
    Write-Result -Total 0
    Assert-Rejected { Assert-PerformanceResults $results } 'zero discovered tests'
    Write-Result -Name 'Unrelated.passing.test'
    Assert-Rejected { Assert-PerformanceResults $results } 'a different passing test'
    Write-Result
    Copy-Item (Join-Path $results 'results.trx') (Join-Path $results 'stale.trx')
    Assert-Rejected { Assert-PerformanceResults $results } 'duplicate or stale results'
    Remove-Item (Join-Path $results 'stale.trx')
    (Get-Content (Join-Path $results 'results.trx') -Raw).Replace('outcome="Completed"', 'outcome="Failed"') |
        Set-Content (Join-Path $results 'results.trx')
    Assert-Rejected { Assert-PerformanceResults $results } 'an unsuccessful run summary'
    Write-Host 'PASS: performance results require the complete named workload, no skips, and one fresh successful run.'
    $probe = Join-Path $results 'revision-probe'
    New-Item -ItemType Directory $probe | Out-Null
    '{"outcome":"success","revision":"stale"}' | Set-Content (Join-Path $probe 'result.json')
    $exitCode = Invoke-RunnerProbe @('-ExpectedRevision', ('0' * 40), '-ResultsDirectory', $probe) (Join-Path $results 'probe.log')
    if ($exitCode -eq 0) { throw 'A different candidate revision passed performance verification.' }
    $status = Get-Content (Join-Path $probe 'result.json') -Raw | ConvertFrom-Json
    if ($status.outcome -ne 'failure' -or $status.revision -notmatch '^[0-9a-f]{40}$' -or
        $status.expectedRevision -ne ('0' * 40) -or $status.steps.Count -ne 0) {
        throw 'Candidate mismatch was not reported as a fresh failure before test execution.'
    }
    Write-Host 'PASS: candidate mismatch fails before execution with identifiable revision and fresh failure results.'
    $started = $env:SCALE_STARTED_AT
    try {
        $env:SCALE_STARTED_AT = [DateTimeOffset]::UtcNow.AddMinutes(-12).ToString('O')
        $expired = Join-Path $results 'expired-probe'
        $exitCode = Invoke-RunnerProbe @('-ResultsDirectory', $expired) (Join-Path $results 'expired.log')
        if ($exitCode -eq 0) { throw 'Exhausted setup budget passed performance verification.' }
        $status = Get-Content (Join-Path $expired 'result.json') -Raw | ConvertFrom-Json
        if ($status.outcome -ne 'timed_out' -or $status.steps.Count -ne 0 -or $status.executionSeconds -le 660) {
            throw 'Performance setup did not consume the shared execution budget.'
        }
    } finally {
        $env:SCALE_STARTED_AT = $started
    }
    Write-Host 'PASS: an exhausted setup budget fails closed without starting restore or tests.'
    $runner = Get-Content "$root\eng\Run-CiLane.ps1" -Raw
    if ($runner -notmatch "'--filter', 'Category!=Performance'") {
        throw 'Ordinary filesystem execution must exclude only the complete performance category.'
    }
    $workflow = Get-Content "$root\.github\workflows\scale-verification.yml" -Raw
    foreach ($required in @('schedule:', 'workflow_dispatch:', 'timeout-minutes: 11', 'SCALE_STARTED_AT=',
        'TESTED_REVISION=', 'Run-Performance.ps1 -ExpectedRevision $env:TESTED_REVISION', 'if-no-files-found: error')) {
        if (!$workflow.Contains($required)) { throw "Separate scale workflow is missing $required" }
    }
    if ($workflow -match '(?m)^  (pull_request|push):' -or
        $workflow -notmatch 'if:\s*\$\{\{\s*always\(\)\s*\}\}') {
        throw 'Performance must be nightly/manual, with diagnostic artifacts even after failure.'
    }
    Write-Host 'PASS: ordinary selection and separate nightly/manual candidate-bound execution preserve their contracts.'
} finally {
    Remove-Item $results -Recurse -Force
}
