$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\Release.ps1"
. "$PSScriptRoot\..\eng\PerformanceSuite.ps1"
. "$PSScriptRoot\UiScaleUnionFixture.ps1"
$checks = 0
function Rejects([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw "Release accepted $Reason." }
    $script:checks++
}
$root = Join-Path $PSScriptRoot ("..\.ci-results\release-gates-" + [guid]::NewGuid().ToString('N'))
$candidate = 'a' * 40
$month = [DateTimeOffset]::UtcNow.AddMonths(-1)
$started = [DateTimeOffset]::new($month.Year, $month.Month, 23, 17, 0, 0, [TimeSpan]::Zero)
$runName = 'run-' + ('b' * 32)
function Write-FixtureReport([string]$Suite, [string]$Outcome = 'success', [string]$Revision = $candidate, [DateTimeOffset]$Start = $started.AddSeconds(1)) {
    $directory = Join-Path $root $Suite
    if ($Suite -eq 'ui-scale') {
        $fixture = New-UiScaleUnionFixture (Join-Path $root 'split-source') $Revision -StartedAt $Start
        & "$PSScriptRoot\..\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $fixture.prefix -TailDirectory $fixture.tail `
            -ExpectedRevision $Revision -RunId '123' -RunAttempt '2' -ResultsDirectory $directory -ExpectedPolicyVersion rich-scale-original-v1
        $report = Get-Content (Join-Path $directory 'result.json') -Raw | ConvertFrom-Json
        $report.outcome = $Outcome
        $report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directory 'result.json')
        $report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directory "$($report.runDirectory)\result.json")
        return
    }
    $report = @{
        suite = $Suite; outcome = $Outcome; revision = $Revision; expectedRevision = $Revision
        trackedChanges = $false; untrackedChanges = $false; startedAt = $Start.ToString('O'); completedAt = $Start.AddSeconds(1).ToString('O')
        executionSeconds = 1; ceilingSeconds = 660; runDirectory = $runName
        filter = $PerformanceFilter; requiredTests = $PerformanceTests
    }
    New-Item (Join-Path $directory $runName) -ItemType Directory -Force | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $directory 'result.json')
    $report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $directory "$runName\result.json")
}
function Verify { Assert-ReleaseVerification -PerformanceDirectory "$root\non-ui-performance" -UiScaleDirectory "$root\ui-scale" -Candidate $candidate -StartedAt $started -ExpectedPolicyVersion rich-scale-original-v1 }
try {
    Rejects { Verify } 'missing current-run artifacts'
    Write-FixtureReport non-ui-performance
    Write-FixtureReport ui-scale
    $trx = @"
<TestRun><Results><UnitTestResult testName="$($PerformanceTests[0])" outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
"@
    $trx | Set-Content "$root\non-ui-performance\$runName\results.trx"
    Verify
    $culture = [Threading.Thread]::CurrentThread.CurrentCulture
    try {
        [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('en-GB')
        Verify
    } finally { [Threading.Thread]::CurrentThread.CurrentCulture = $culture }
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
    $unionReport = Get-Content "$root\ui-scale\result.json" -Raw | ConvertFrom-Json
    $workerReport = Get-Content "$root\ui-scale\$($unionReport.runDirectory)\tail\result.json" -Raw | ConvertFrom-Json
    $scenarioPath = "$root\ui-scale\$($unionReport.runDirectory)\tail\$($workerReport.runDirectory)\scenarios.json"
    $scenarios = Get-Content $scenarioPath -Raw | ConvertFrom-Json
    $scenarios.scenarios[1].status = 'NotRun'
    $scenarios | ConvertTo-Json -Depth 6 | Set-Content $scenarioPath
    Rejects { Verify } 'missing named WPF scale scenario'
    Write-Host "PASS: $checks fail-closed fresh exact-candidate release gate checks."
} finally {
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
}
