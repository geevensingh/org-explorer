$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$root\eng\PerformanceSuite.ps1"
. "$root\eng\ScaleRunner.ps1"

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
    $lifecycle = Join-Path $results 'lifecycle'
    Invoke-ScaleSuite -Suite contract -Label 'Scale contract' -ResultsDirectory $lifecycle -Selection @{} -Verify {
        param($run, $build)
        $running = Get-Content (Join-Path $run 'result.json') -Raw | ConvertFrom-Json
        if ($running.outcome -ne 'running' -or $running.revision -notmatch '^[0-9a-f]{40}$') {
            throw 'Owned commands did not begin with a fresh identifiable running report.'
        }
        New-Item -ItemType Directory $build | Out-Null
        'owned fixture' | Set-Content (Join-Path $build 'fixture.txt')
        Invoke-ScaleProcess success pwsh @('-NoProfile', '-Command', 'Write-Output owned-success; exit 0')
    }
    $status = Get-Content (Join-Path $lifecycle 'result.json') -Raw | ConvertFrom-Json
    if ($status.outcome -ne 'success' -or $status.steps.Count -ne 1 -or $status.steps[0].exitCode -ne 0 -or
        (Test-Path (Join-Path $lifecycle "$($status.runDirectory)\build"))) {
        throw 'Successful shared execution lost command evidence or fixture/build cleanup.'
    }
    $failed = Join-Path $results 'failed-command'
    Assert-Rejected {
        Invoke-ScaleSuite -Suite contract -Label 'Scale contract' -ResultsDirectory $failed -Selection @{} -Verify {
            param($run, $build)
            New-Item -ItemType Directory $build | Out-Null
            Invoke-ScaleProcess failure pwsh @('-NoProfile', '-Command', 'exit 7')
        }
    } 'an unsuccessful owned command'
    $status = Get-Content (Join-Path $failed 'result.json') -Raw | ConvertFrom-Json
    if ($status.outcome -ne 'failure' -or $status.steps.Count -ne 1 -or $status.steps[0].exitCode -ne 7 -or
        (Test-Path (Join-Path $failed "$($status.runDirectory)\build"))) {
        throw 'Failed shared execution lost the failing exit code or skipped cleanup.'
    }
    Write-Host 'PASS: shared scale execution records fresh running/success/failure evidence and cleans owned build trees.'
    $heartbeatProbe = Join-Path $results 'heartbeat-probe.ps1'
    @'
param([string] $Report, [string] $Transport)
$ErrorActionPreference = 'Stop'
function Write-Progress([string] $Message) {
    if ($Transport -eq 'timing-file') {
        Add-Content -LiteralPath $env:SCALE_TIMING_FILE -Value $Message
    } else { Write-Output $Message }
}
Write-Progress 'SCALE BEGIN heartbeat-probe'
$first = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
Start-Sleep -Seconds 6
$current = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
if ($current.activeCommand -ne 'heartbeat-probe' -or $current.activePhase -ne 'heartbeat-probe' -or
    $current.executionSeconds -le ($first.executionSeconds + 2) -or
    $current.lastProgress -ne 'SCALE BEGIN heartbeat-probe') {
    throw 'The running command report did not retain fresh active-phase evidence.'
}
Write-Progress 'SCALE END heartbeat-probe; outcome=Passed'
'@ | Set-Content -LiteralPath $heartbeatProbe
    $previousTiming = $env:SCALE_TIMING_FILE
    try {
        foreach ($transport in @('stdout', 'timing-file')) {
            $heartbeatResults = Join-Path $results "heartbeat-$transport"
            Invoke-ScaleSuite -Suite contract -Label "Scale heartbeat $transport" -ResultsDirectory $heartbeatResults -Selection @{} -Verify {
                param($run, $build)
                $env:SCALE_TIMING_FILE = Join-Path $run 'scale-measurements.log'
                Invoke-ScaleProcess heartbeat-probe pwsh @('-NoProfile', '-File', $heartbeatProbe,
                    (Join-Path $run 'result.json'), $transport)
            }
            $status = Get-Content -LiteralPath (Join-Path $heartbeatResults 'result.json') -Raw | ConvertFrom-Json
            if ($status.lastProgress -ne 'SCALE END heartbeat-probe; outcome=Passed' -or
                $status.lastActivePhase -ne 'heartbeat-probe' -or $status.activePhase) {
                throw "Final progress did not retain the completed $transport phase."
            }
        }
    } finally {
        $env:SCALE_TIMING_FILE = $previousTiming
    }
    Write-Host 'PASS: stdout and dedicated timing files retain live and final phase evidence.'
    $diagnostic = Join-Path $results 'diagnostic-only'
    Assert-Rejected {
        Invoke-ScaleSuite -Suite contract -Label 'Diagnostic observation' -ResultsDirectory $diagnostic `
            -ObservationSeconds 900 -Selection @{} -Verify { param($run, $build) }
    } 'diagnostic observation as successful candidate verification'
    $status = Get-Content (Join-Path $diagnostic 'result.json') -Raw | ConvertFrom-Json
    if ($status.outcome -ne 'diagnostic_complete' -or !$status.diagnosticOnly -or
        $status.observedOutcome -ne 'success' -or $status.ceilingSeconds -ne 660 -or $status.observationSeconds -ne 900) {
        throw 'Extended observation did not retain the unchanged budget and non-verifying outcome.'
    }
    $priorStarted = $env:SCALE_STARTED_AT
    try {
        $env:SCALE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-659).ToString('O')
        $overBudget = Join-Path $results 'diagnostic-over-budget'
        Assert-Rejected {
            Invoke-ScaleSuite -Suite contract -Label 'Over-budget observation' -ResultsDirectory $overBudget `
                -ObservationSeconds 900 -Selection @{} -Verify {
                    param($run, $build)
                    Invoke-ScaleProcess observed pwsh @('-NoProfile', '-Command', 'Start-Sleep -Seconds 2; Write-Output observed-completion')
                }
        } 'observed completion beyond the original verification budget'
        $status = Get-Content (Join-Path $overBudget 'result.json') -Raw | ConvertFrom-Json
        if ($status.outcome -ne 'timed_out' -or $status.observedOutcome -ne 'success' -or
            !$status.diagnosticOnly -or $status.executionSeconds -le 660 -or $status.steps[0].exitCode -ne 0) {
            throw 'Completing within the observation window erased the original 660-second failure.'
        }
    } finally { $env:SCALE_STARTED_AT = $priorStarted }
    Write-Host 'PASS: extended observation never passes candidate verification or erases an exceeded original budget.'
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
        $env:SCALE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-657).ToString('O')
        $held = Join-Path $results 'held-command'
        Assert-Rejected {
            Invoke-ScaleSuite -Suite contract -Label 'Scale contract' -ResultsDirectory $held -Selection @{} -Verify {
                param($run, $build)
                New-Item -ItemType Directory $build | Out-Null
                Invoke-ScaleProcess held pwsh @('-NoProfile', '-Command', '[Console]::WriteLine($PID); Start-Sleep -Seconds 30')
            }
        } 'a held owned command'
        $status = Get-Content (Join-Path $held 'result.json') -Raw | ConvertFrom-Json
        $run = Join-Path $held $status.runDirectory
        $childId = [int](Get-Content (Join-Path $run 'held.log') -TotalCount 1)
        $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
        if ($null -ne $child) {
            Stop-Process -Id $childId -Force
            throw 'Timed-out shared execution left its owned command running.'
        }
        if ($status.outcome -ne 'timed_out' -or $status.steps.Count -ne 1 -or
            (Test-Path (Join-Path $run 'build'))) {
            throw 'Held execution did not fail closed, report the command, and clean after termination.'
        }
    } finally {
        $env:SCALE_STARTED_AT = $started
    }
    Write-Host 'PASS: exhausted setup starts no commands; held owned execution is terminated before cleanup and remains timed out.'
    $runner = Get-Content "$root\eng\Run-CiLane.ps1" -Raw
    if ($runner -notmatch "'--filter', 'Category!=Performance'") {
        throw 'Ordinary filesystem execution must exclude only the complete performance category.'
    }
    $workflow = Get-Content "$root\.github\workflows\scale-verification.yml" -Raw
    foreach ($required in @('schedule:', 'workflow_dispatch:', 'workflow_call:', 'timeout-minutes: 13', 'SCALE_STARTED_AT=',
        'TESTED_REVISION=', 'Run-Performance.ps1 -ExpectedRevision $env:TESTED_REVISION', 'if-no-files-found: error')) {
        if (!$workflow.Contains($required)) { throw "Separate scale workflow is missing $required" }
    }
    if ($workflow -notmatch '(?s)  workflow_call:\s+inputs:\s+revision:\s+description:[^\r\n]+\s+required: true\s+type: string') {
        throw 'Reusable scale verification must require an explicit candidate revision.'
    }
    if ($workflow -match '(?m)^  (pull_request|push):' -or
        $workflow -notmatch 'if:\s*\$\{\{\s*always\(\)\s*\}\}') {
        throw 'Performance must be nightly/manual, with diagnostic artifacts even after failure.'
    }
    $ci = Get-Content "$root\.github\workflows\ci.yml" -Raw
    if ($ci -notmatch '(?s)  workflow_dispatch:\s+inputs:\s+run_scale_verification:\s+description:[^\r\n]+\s+required: false\s+default: false\s+type: boolean') {
        throw 'Registered CI must expose an opt-in Boolean scale verification input, defaulting to false.'
    }
    $bridge = [regex]::Match($ci, '(?ms)^  scale-verification:\r?\n(?<body>.*?)(?=^  [\w-]+:|\z)').Groups['body'].Value
    foreach ($required in @(
        'if: ${{ github.event_name == ''workflow_dispatch'' && inputs.run_scale_verification && !inputs.run_release_verification }}',
        'uses: ./.github/workflows/scale-verification.yml',
        'revision: ${{ github.sha }}'
    )) {
        if (!$bridge.Contains($required)) { throw "Manual scale bridge is missing $required" }
    }
    if ($bridge -match '(?m)^\s+(steps|runs-on|needs|run):' -or $ci.Contains('Run-Performance.ps1')) {
        throw 'The manual bridge must call the shared workflow, not duplicate or serialize its execution.'
    }
    $aggregate = [regex]::Match($ci, '(?ms)^  ci:\r?\n(?<body>.*?)(?=^  [\w-]+:|\z)').Groups['body'].Value
    if (!$aggregate.Contains('needs: [core, filesystem, desktop]')) {
        throw 'The stable required CI aggregate must depend on exactly its three ordinary lanes.'
    }
    Write-Host 'PASS: registered CI offers only manual opt-in reuse at its exact dispatch commit, outside the three required lanes.'
    Write-Host 'PASS: ordinary selection and separate nightly/manual candidate-bound execution preserve their contracts.'
} finally {
    Remove-Item $results -Recurse -Force
}
