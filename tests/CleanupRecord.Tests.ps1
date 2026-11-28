$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$root\eng\NonUiCohort.ps1"
$fixture = Join-Path $root ('.ci-results\cleanup-record-' + [guid]::NewGuid().ToString('N'))
$runName = 'run-' + ('b' * 32)
$run = Join-Path $fixture $runName
$revision = 'a' * 40
$requiredTest = 'OrgExplorer.Persistence.Tests.ScaleWorkflowTests.Ten_thousand_People_keep_complete_durable_current_and_frozen_context_through_mutations_and_delivery'
$started = [DateTimeOffset]::UtcNow.AddSeconds(-200)
$report = @{
    schemaVersion = 1; suite = 'non-ui-performance'; revision = $revision; expectedRevision = $revision
    cohortId = "$revision-321-1"; sample = 1; runId = '321'; runAttempt = '1'
    invocationId = 'c' * 32; runDirectory = $runName; filter = 'Category=Performance'; requiredTests = @($requiredTest)
    ceilingSeconds = 660; observationSeconds = 660; executionSeconds = 200
    diagnosticOnly = $false; trackedChanges = $false; untrackedChanges = $false
    startedAt = $started.ToString('O'); completedAt = $started.AddSeconds(200).ToString('O')
    acceptanceDeadlineAt = $started.AddSeconds(660).ToString('O')
    phase = 'finished'; outcome = 'success'; observedOutcome = 'success'; failure = $null
    steps = @(@{ name = 'restore'; seconds = 10; exitCode = 0 }, @{ name = 'test'; seconds = 190; exitCode = 0 })
    configuration = @{
        workload = 'OrgExplorer.Persistence.Tests'; testTree = 'b' * 40; fixtureTree = 'c' * 40
        sdk = '10.0.404'; runtimes = @('Microsoft.NETCore.App 10.0.0'); os = 'Windows'; architecture = 'X64'
        processors = 4; image = 'win25'; imageVersion = '20261126'; powershell = '7.6'
        setup = 'locked-restore;isolated-build;setup-and-cleanup-included'
    }
}
function Read-CleanupRecord([string]$Policy) {
    $text = $report | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText((Join-Path $fixture 'result.json'), $text)
    [IO.File]::WriteAllText((Join-Path $run 'result.json'), $text)
    $sample = @{ number = 1; diagnostics = @(); validation = 'incomplete' }
    Read-NonUiCohortSample $sample $fixture $revision "$revision-321-1" '321' '1' `
        -RequirePairedReport -ExpectedPolicyVersion $Policy
    return $sample
}
try {
    New-Item -ItemType Directory -Path $run -Force | Out-Null
    $trx = @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results><UnitTestResult testName="$requiredTest" outcome="Passed" /></Results>
<ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
"@
    [IO.File]::WriteAllText((Join-Path $run 'result.trx'), $trx)
    $phases = @('fixture', 'fixture-export', 'load', 'navigation-100', 'profile-100', 'search-current',
        'search-history', 'favorite-save', 'note-save', 'profile-save', 'validated-preview', 'validated-apply',
        'reopen', 'reopened-projections', 'latest-replay', 'initial-replay-preview', 'initial-replay-apply',
        'unchanged-refresh', 'validated-delivery', 'reopen-after-delivery', 'resume-unpublished')
    $profile = @{ samplesMs = @(140.0) * 21; p95Ms = 140.0; budgetMs = 1000
        percentile = 'Nearest rank: sorted sample at ceil(0.95 * count), one based.' }
    $lines = @($phases | ForEach-Object { "SCALE END $_; outcome=Passed; ms=1.0;" })
    $lines += 'SCALE ORDINARY-PROFILE ' + ($profile | ConvertTo-Json -Compress)
    [IO.File]::WriteAllLines((Join-Path $run 'scale-measurements.log'), $lines)
    if ((Read-CleanupRecord rich-scale-original-v1).validation -cne 'success') {
        throw 'Complete legacy records without new cleanup fields must remain accepted.'
    }
    $report.acceptancePolicyVersion = 'ci-timing-v4'
    $report.verificationOutcome = 'success'
    $report.cleanup = @{ outcome = 'success'; warning = $false }
    if ((Read-CleanupRecord ci-timing-v4).validation -cne 'success') {
        throw 'Complete current records with successful housekeeping must remain accepted.'
    }
    $report.Remove('cleanup')
    $rejected = $false
    try { $null = Read-CleanupRecord ci-timing-v4 } catch { $rejected = $true }
    if (!$rejected) { throw 'Current acceptance allowed a complete-looking record with missing cleanup metadata.' }
    Write-Host 'PASS: current cleanup metadata is required without rejecting valid current or legacy records.'
    $report.cleanup = @{ outcome = 'failure'; warning = $false; phase = 'build-housekeeping'
        error = 'Access denied'; errorType = 'System.UnauthorizedAccessException'; hresult = -2147024891 }
    foreach ($mode in @('not-a-union', 'isolated-workers')) {
        $report.verificationMode = $mode
        $rejected = $false
        try { $null = Read-CleanupRecord ci-timing-v4 } catch { $rejected = $true }
        if (!$rejected) { throw "A worker verification mode bypassed unsigned failed housekeeping: $mode" }
    }
    Write-Host 'PASS: neither arbitrary nor collection verification modes bypass worker cleanup validation.'
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
