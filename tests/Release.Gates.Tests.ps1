param([string]$TimingFixtureMetadata)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\Release.ps1"
. "$PSScriptRoot\..\eng\PerformanceSuite.ps1"
. "$PSScriptRoot\..\eng\TimingPolicy.ps1"
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
        runId = '123'; runAttempt = '2'
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
    $active = New-UiScaleUnionFixture (Join-Path $root 'active') $candidate -StartedAt $started.AddSeconds(1) -TimingPolicyVersion ci-timing-v2
    $report = Get-Content (Join-Path $active.prefix 'result.json') -Raw | ConvertFrom-Json
    $report.executionSeconds = 661
    $report.completedAt = ([DateTimeOffset]$report.startedAt).AddSeconds(661).ToString('O')
    $report | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $active.prefix 'result.json')
    $report | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $active.prefix "$($active.runName)\result.json")
    foreach ($phase in @('normal-executable', 'background-export')) {
        $path = Join-Path $active.prefix "$($active.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
        $note = Get-Content $path -Raw | ConvertFrom-Json
        $note.localDurability.samplesMs = @(400) * 19 + @(1030.138, 1600)
        $note.localDurability.p95Ms = 1030.138; $note.localDurability.goalMet = $false
        $note | ConvertTo-Json -Depth 12 | Set-Content $path
    }
    $activeUnion = Join-Path $root 'active-union'
    $timingRemote = Join-Path $root 'timing-history.git'
    git init --bare --quiet $timingRemote
    & "$PSScriptRoot\..\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $active.prefix -TailDirectory $active.tail `
        -ExpectedRevision $candidate -RunId 123 -RunAttempt 2 -ExpectedPolicyVersion ci-timing-v2 -ResultsDirectory $activeUnion
    Assert-ReleaseVerification -PerformanceDirectory "$root\non-ui-performance" -UiScaleDirectory $activeUnion `
        -Candidate $candidate -StartedAt $started -ExpectedPolicyVersion ci-timing-v2 -TimingHistoryRemote $timingRemote -AllowLocalFixture
    $timing = @(Get-Content (Join-Path $activeUnion 'timing-evaluation.json') -Raw | ConvertFrom-Json)
    if ($timing.Count -ne 2 -or $timing[0].timingEvaluation.health -cne 'yellow' -or
        $timing[0].timingEvaluation.blocking -or $timing[0].timingEvaluation.version -cne 'ci-timing-v2' -or
        $timing[0].measurements.executionSeconds -ne 661) {
        throw 'Fresh661s release failed v2 or a known1030ms goal warning became blocking without a reference.'
    }
    $checks++
    Rejects {
        Assert-ReleaseVerification -PerformanceDirectory "$root\non-ui-performance" -UiScaleDirectory $activeUnion `
            -Candidate $candidate -StartedAt $started -ExpectedPolicyVersion rich-scale-original-v1
    } 'v2 artifacts claiming acceptance under an old source policy'
    if ($TimingFixtureMetadata) {
        $metadata = Get-Content -LiteralPath $TimingFixtureMetadata -Raw | ConvertFrom-Json
        if ($metadata.schemaVersion -ne 1 -or $metadata.hostedMeasurement -or
            $metadata.kind -cne 'isolated-non-production-public-contract-fixture') {
            throw 'Release timing tests require explicitly isolated public publisher fixtures.'
        }
        $beforeRemote = Join-Path $root 'before-collection.git'
        git init --bare --quiet $beforeRemote
        git "--git-dir=$beforeRemote" fetch --quiet $metadata.runningHistoryRemote 'refs/heads/main:refs/heads/main'
        $effective = Get-SourceTimingPolicyVersion $metadata.sourceB -GitDirectory $beforeRemote
        $originalBefore = $null
        foreach ($state in @(
            @{ name = 'before'; remote = $beforeRemote; blocked = $false; reference = $false },
            @{ name = 'during'; remote = $metadata.runningHistoryRemote; blocked = $false; reference = $true },
            @{ name = 'after'; remote = $metadata.rejectedHistoryRemote; blocked = $true; reference = $true }
        )) {
            $case = Join-Path $root "collection-$($state.name)"
            New-Item -ItemType Directory -Path $case | Out-Null
            $sample = $metadata.sampleDirectories[0]
            Copy-Item (Join-Path $sample 'non-ui') (Join-Path $case 'performance') -Recurse
            Copy-Item (Join-Path $sample 'active-union') (Join-Path $case 'ui-scale') -Recurse
            $raw = Get-Content (Join-Path $case 'performance\result.json') -Raw | ConvertFrom-Json
            # This synthetic collector fixture also needs the real release runner's identical owned report.
            Copy-Item (Join-Path $case 'performance\result.json') (Join-Path $case "performance\$($raw.runDirectory)\result.json")
            $failure = $null
            try {
                Assert-ReleaseVerification -PerformanceDirectory (Join-Path $case 'performance') -UiScaleDirectory (Join-Path $case 'ui-scale') `
                    -Candidate $metadata.sourceB -StartedAt ([DateTimeOffset]$raw.startedAt).AddSeconds(-1) `
                    -ExpectedPolicyVersion $effective -TimingHistoryRemote $state.remote -AllowLocalFixture
            } catch { $failure = $_ }
            $comparisonPath = Join-Path $case 'ui-scale\timing-comparison.json'
            if (!(Test-Path -LiteralPath $comparisonPath)) { throw "Fresh fixture rejection before comparison: $failure" }
            $comparison = Get-Content $comparisonPath -Raw | ConvertFrom-Json
            if (($null -ne $failure) -ne $state.blocked -or $comparison.timingEvaluation.blocking -ne $state.blocked -or
                ($null -ne $comparison.timingEvaluation.metrics.'prefix-executionSeconds'.reference) -ne $state.reference -or
                $comparison.samples.Count -ne 1 -or $comparison.samples[0].rich.seriesKey -cne $metadata.seriesKey -or
                $comparison.validation -cne 'success') {
                throw "Release timing failed $($state.name) collection: $failure"
            }
            if ($state.name -ceq 'before') { $originalBefore = Get-Content $comparisonPath -Raw }
            $checks++
        }
        if ((Get-Content (Join-Path $root 'collection-before\ui-scale\timing-comparison.json') -Raw) -cne $originalBefore) {
            throw 'A later collection finding rewrote an earlier completed release gate.'
        }
    }
    Write-Host "PASS: $checks fail-closed fresh exact-candidate release gate checks."
} finally {
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
}
