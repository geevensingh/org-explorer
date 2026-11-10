$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\Release.ps1"
. "$PSScriptRoot\..\eng\PerformanceSuite.ps1"
$checks = 0
function Rejects([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw "Release accepted $Reason." }
    $script:checks++
}
$root = Join-Path $PSScriptRoot ("..\.ci-results\release-gates-" + [guid]::NewGuid().ToString('N'))
$candidate = 'a' * 40
$started = [DateTimeOffset]::UtcNow.AddMinutes(-2)
$runName = 'run-' + ('b' * 32)
function Write-FixtureReport([string]$Suite, [string]$Outcome = 'success', [string]$Revision = $candidate, [DateTimeOffset]$Start = $started.AddSeconds(1)) {
    $directory = Join-Path $root $Suite
    $report = @{
        suite = $Suite; outcome = $Outcome; revision = $Revision; expectedRevision = $Revision
        trackedChanges = $false; untrackedChanges = $false; startedAt = $Start.ToString('O'); completedAt = $Start.AddSeconds(1).ToString('O')
        executionSeconds = 1; ceilingSeconds = 660; runDirectory = $runName
        filter = $PerformanceFilter; requiredTests = $PerformanceTests; requiredScenarios = @('scale-navigation-rendering', 'scale-mutation-responsiveness')
    }
    New-Item (Join-Path $directory $runName) -ItemType Directory -Force | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $directory 'result.json')
    $report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $directory "$runName\result.json")
}
function Verify { Assert-ReleaseVerification -PerformanceDirectory "$root\non-ui-performance" -UiScaleDirectory "$root\ui-scale" -Candidate $candidate -StartedAt $started }
try {
    Rejects { Verify } 'missing current-run artifacts'
    Write-FixtureReport non-ui-performance
    Write-FixtureReport ui-scale
    $trx = @"
<TestRun><Results><UnitTestResult testName="$($PerformanceTests[0])" outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
"@
    $trx | Set-Content "$root\non-ui-performance\$runName\results.trx"
    $scenarios = @{ schemaVersion = 1; scenarios = @('scale-navigation-rendering', 'scale-mutation-responsiveness') | ForEach-Object {
        @{ name = $_; status = 'Passed'; startedAt = $started.ToString('O'); finishedAt = $started.ToString('O'); seconds = 0; error = $null }
    } }
    $scenarios | ConvertTo-Json -Depth 6 | Set-Content "$root\ui-scale\$runName\scenarios.json"
    Verify
    foreach ($suite in @('non-ui-performance', 'ui-scale')) {
        foreach ($outcome in @('failure', 'running', 'skipped', 'cancelled', 'timed_out', '')) {
            Write-FixtureReport $suite $outcome
            Rejects { Verify } "$suite $outcome"
        }
        Write-FixtureReport $suite -Revision ('c' * 40)
        Rejects { Verify } "$suite wrong candidate"
        Write-FixtureReport $suite -Start $started.AddDays(-1)
        Rejects { Verify } "$suite past nightly success"
        Write-FixtureReport $suite
    }
    $trx.Replace('outcome="Passed"', 'outcome="NotExecuted"') | Set-Content "$root\non-ui-performance\$runName\results.trx"
    Rejects { Verify } 'unexecuted named performance workload'
    $trx | Set-Content "$root\non-ui-performance\$runName\results.trx"
    $scenarios.scenarios[1].status = 'NotRun'
    $scenarios | ConvertTo-Json -Depth 6 | Set-Content "$root\ui-scale\$runName\scenarios.json"
    Rejects { Verify } 'missing named WPF scale scenario'
    Write-Host "PASS: $checks fail-closed fresh exact-candidate release gate checks."
} finally {
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
}
