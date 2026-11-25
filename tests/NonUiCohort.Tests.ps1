$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.test-workspaces\cohort-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$revision = 'a' * 40
$run = @{
    id = 42; run_attempt = 1; head_sha = $revision; event = 'push'; head_branch = 'main'
    path = '.github/workflows/ci.yml'; status = 'completed'; conclusion = 'success'
    head_repository = @{ full_name = 'geevensingh/org-explorer' }
}
$names = @('Core and view models', 'Real filesystem persistence', 'Actual Windows desktop smoke (people)',
    'Actual Windows desktop smoke (workspace)', 'High-resolution Windows smoke (reading)',
    'High-resolution Windows smoke (context)', 'CI')
$jobs = @($names | ForEach-Object { @{ name = $_; status = 'completed'; conclusion = 'success' } })
$latest = $run.Clone(); $latest.run_attempt = 2; $latest.conclusion = 'failure'
$global:NonUiCohortFixture = @{ run = $run; latest = $latest; jobs = $jobs; revision = $revision }
$cohortId = "$revision-99-1"
$global:NonUiCohortFixture.collection = @{
    id = 99; run_attempt = 1; head_sha = $revision; event = 'workflow_run'; status = 'in_progress'
    path = '.github/workflows/non-ui-cohorts.yml'; head_repository = @{ full_name = 'geevensingh/org-explorer' }
}
$global:NonUiCohortFixture.sampleJobs = @()
$global:NonUiCohortFixture.artifacts = @()
$global:NonUiCohortFixture.evidence = @{}
$checks = 0
function Assert([bool]$Condition, [string]$Reason) {
    if (!$Condition) { throw $Reason }
    $script:checks++
}
function Rejects([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert $rejected $Reason
}
# Only the external authenticated GitHub boundary is replaced.
function gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -ceq 'run' -and $args[1] -ceq 'download') {
        $name = $args[[Array]::IndexOf($args, '--name') + 1]
        $destination = $args[[Array]::IndexOf($args, '--dir') + 1]
        Copy-Item -LiteralPath $global:NonUiCohortFixture.evidence[$name] -Destination $destination -Recurse
        return
    }
    switch -Regex ($args[1]) {
        '/actions/runs/42/attempts/1$' { $global:NonUiCohortFixture.run | ConvertTo-Json -Depth 10; return }
        '/actions/runs/42$' { $global:NonUiCohortFixture.latest | ConvertTo-Json -Depth 10; return }
        '/actions/runs/42(/attempts/1)?/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:NonUiCohortFixture.jobs }) -Depth 10; return
        }
        '/git/ref/heads/main$' { @{ object = @{ sha = $global:NonUiCohortFixture.revision } } | ConvertTo-Json; return }
        '/actions/runs/99/attempts/1$' { $global:NonUiCohortFixture.collection | ConvertTo-Json -Depth 10; return }
        '/actions/runs/99/attempts/1/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:NonUiCohortFixture.sampleJobs }) -Depth 10; return
        }
        '/actions/runs/99/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = $global:NonUiCohortFixture.artifacts }) -Depth 10; return
        }
        default { throw "Unexpected cohort GitHub operation: $($args -join ' ')" }
    }
}
try {
    $work = Join-Path $fixture 'plan'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $work
    $plan = Get-Content -LiteralPath (Join-Path $work 'plan.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($plan.revision -ceq $revision -and $plan.ciRunId -ceq '42' -and $plan.ciRunAttempt -ceq '1') 'Plan lost its exact authenticated required CI.'
    Assert ($plan.cohortId -ceq "$revision-99-1" -and $plan.samples.Count -eq 3 -and
        $plan.samples[0] -eq 1 -and $plan.samples[2] -eq 3) 'Initial independent cohort is not exactly three executions.'
    Assert ($plan.mode -ceq 'observation' -and $plan.promotion -ceq 'disabled' -and
        $plan.policyVersion -ceq 'non-ui-cohort-observation-v1') 'An uncalibrated cohort acquired promotion authority.'
    foreach ($change in @(
        @{ field = 'event'; value = 'pull_request' }, @{ field = 'head_branch'; value = 'feature' },
        @{ field = 'path'; value = '.github/workflows/other.yml' }, @{ field = 'conclusion'; value = 'failure' },
        @{ field = 'status'; value = 'in_progress' }, @{ field = 'run_attempt'; value = 2 }
    )) {
        $original = $run[$change.field]; $run[$change.field] = $change.value
        Rejects {
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture ([guid]::NewGuid().ToString('N')))
        } 'Ineligible/stale/untrusted source CI triggered collection.'
        $run[$change.field] = $original
    }
    $run.head_repository.full_name = 'attacker/fork'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'fork')
    } 'Fork CI received collection authority.'
    $run.head_repository.full_name = 'geevensingh/org-explorer'
    $jobs[6].conclusion = 'skipped'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'skipped-ci')
    } 'Missing required CI approval triggered collection.'
    $jobs[6].conclusion = 'success'
    for ($sample = 1; $sample -le 3; $sample++) {
        $name = "non-ui-cohort-$cohortId-sample-$sample"
        $directory = Join-Path $fixture "sample-$sample"
        $invocation = [guid]::NewGuid().ToString('N')
        $runDirectory = "run-$invocation"
        New-Item -ItemType Directory -Path (Join-Path $directory $runDirectory) -Force | Out-Null
        @{
            schemaVersion = 1; suite = 'non-ui-performance'; revision = $revision; expectedRevision = $revision
            cohortId = $cohortId; sample = $sample; invocationId = $invocation; runId = '99'; runAttempt = '1'
            filter = 'Category=Performance'
            requiredTests = @('OrgExplorer.Persistence.Tests.ScaleWorkflowTests.Ten_thousand_People_keep_complete_durable_current_and_frozen_context_through_mutations_and_delivery')
            outcome = 'success'; observedOutcome = 'success'; diagnosticOnly = $false; phase = 'finished'
            trackedChanges = $false; untrackedChanges = $false; ceilingSeconds = 660; observationSeconds = 660
            executionSeconds = @(120, 150, 180)[$sample - 1]; runDirectory = $runDirectory
            startedAt = '2026-11-25T10:00:00Z'; completedAt = '2026-11-25T10:03:00Z'
            steps = @(@{ name = 'restore'; seconds = 10.0; exitCode = 0 }, @{ name = 'test'; seconds = 100.0; exitCode = 0 })
            environment = @{ os = 'Windows'; runtime = '.NET 10'; powershell = '7.6'; image = 'win25'; imageVersion = '20261116'; processors = 4 }
            configuration = @{ workload = 'OrgExplorer.Persistence.Tests'; testTree = 'b' * 40; fixtureTree = 'c' * 40
                sdk = '10.0.100'; runtimes = @('Microsoft.NETCore.App 10.0.0'); os = 'Windows'; architecture = 'X64'
                processors = 4; image = 'win25'; imageVersion = '20261116'; powershell = '7.6'
                setup = 'locked-restore;isolated-build;setup-and-cleanup-included' }
        } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $directory 'result.json')
        @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results><UnitTestResult testName="OrgExplorer.Persistence.Tests.ScaleWorkflowTests.Ten_thousand_People_keep_complete_durable_current_and_frozen_context_through_mutations_and_delivery" outcome="Passed" /></Results>
<ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
"@ | Set-Content -LiteralPath (Join-Path $directory "$runDirectory\result.trx")
        $phases = @('fixture', 'fixture-export', 'load', 'navigation-100', 'profile-100', 'search-current',
            'search-history', 'favorite-save', 'note-save', 'profile-save', 'validated-preview', 'validated-apply',
            'reopen', 'reopened-projections', 'latest-replay', 'initial-replay-preview', 'initial-replay-apply',
            'unchanged-refresh', 'validated-delivery', 'reopen-after-delivery', 'resume-unpublished')
        @($phases | ForEach-Object { "SCALE END $_; outcome=Passed; ms=10.0; utc=2026-11-25T10:00:00Z" }) |
            Set-Content -LiteralPath (Join-Path $directory "$runDirectory\scale-measurements.log")
        'SCALE ORDINARY-PROFILE {"samplesMs":[800,800,800,800,800,800,800,800,800,800,800,800,800,800,800,800,800,800,800,800,800],"p95Ms":800,"budgetMs":1000,"boundary":"Public explicit profile Save","percentile":"Nearest rank: sorted sample at ceil(0.95 * count), one based."}' |
            Add-Content -LiteralPath (Join-Path $directory "$runDirectory\scale-measurements.log")
        $global:NonUiCohortFixture.evidence[$name] = $directory
        $global:NonUiCohortFixture.sampleJobs += @{ id = 100 + $sample; name = "Non-UI cohort sample ($sample)"
            status = 'completed'; conclusion = 'success'; runner_id = 200 + $sample }
        $global:NonUiCohortFixture.artifacts += @{ id = 300 + $sample; name = $name; expired = $false
            expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
            workflow_run = @{ id = 99; head_sha = $revision } }
    }
    $summaryWork = Join-Path $fixture 'summary'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $summaryWork
    $record = Get-Content -LiteralPath (Join-Path $summaryWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($record.samples.Count -eq 3 -and $record.reliability.success -eq 3 -and $record.reliability.attempts -eq 3) 'A complete cohort lost independent executions.'
    Assert ($record.metrics.executionSeconds.median -eq 150 -and $record.metrics.executionSeconds.minimum -eq 120 -and
        $record.metrics.executionSeconds.maximum -eq 180 -and $record.metrics.executionSeconds.range -eq 60) 'Cohort median/range do not preserve independent values.'
    Assert ($record.eligibility -ceq 'pending-calibration' -and $record.promotion -ceq 'disabled' -and
        $record.richScaleAcceptance -ceq 'not-executed' -and $record.extraSamples.Count -eq 0) 'Observation-only non-UI evidence claimed approved full-scale acceptance.'
    $testWork = Join-Path $fixture 'test-policy'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $testWork -AllowLocalFixture `
        -TestPolicy @{ version = 'TEST-non-ui-range-v1'; maximumRangeSeconds = 40 }
    $testRecord = Get-Content -LiteralPath (Join-Path $testWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($testRecord.extraSamples.Count -eq 2 -and $testRecord.extraSamples[0] -eq 4 -and
        $testRecord.extraSamples[1] -eq 5 -and $testRecord.eligibility -ceq 'inconsistent') 'Eligible TEST-policy inconsistency did not request exactly two bounded independent executions.'
    $name = "non-ui-cohort-$cohortId-sample-2"
    $directory = $global:NonUiCohortFixture.evidence[$name]
    $failure = Get-Content -LiteralPath (Join-Path $directory 'result.json') -Raw | ConvertFrom-Json -AsHashtable
    $failure.outcome = 'failure'; $failure.observedOutcome = 'failure'
    $failure | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $directory 'result.json')
    $global:NonUiCohortFixture.sampleJobs[1].conclusion = 'failure'
    $failureWork = Join-Path $fixture 'failure'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $failureWork -AllowLocalFixture `
        -TestPolicy @{ version = 'TEST-non-ui-range-v1'; maximumRangeSeconds = 40 }
    $failed = Get-Content -LiteralPath (Join-Path $failureWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($failed.reliability.failure -eq 1 -and $failed.reliability.success -eq 2 -and
        $failed.metrics.executionSeconds.count -eq 3 -and $failed.metrics.executionSeconds.median -eq 150 -and
        $failed.extraSamples.Count -eq 0 -and $failed.eligibility -ceq 'rejected') 'Collection selected greens, dropped a failed measurement or retried a failed cohort.'
    $failure.outcome = 'success'; $failure.observedOutcome = 'success'
    $failure | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $directory 'result.json')
    $global:NonUiCohortFixture.sampleJobs[1].conclusion = 'success'
    foreach ($outcome in @('cancelled', 'skipped', 'timed_out')) {
        $global:NonUiCohortFixture.sampleJobs[1].conclusion = $outcome
        $outcomeWork = Join-Path $fixture "outcome-$outcome"
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $outcomeWork
        $outcomes = Get-Content -LiteralPath (Join-Path $outcomeWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        $expectedOutcome = if ($outcome -ceq 'timed_out') { 'incomplete' } else { $outcome }
        Assert ($outcomes.samples[1].validation -ceq $expectedOutcome -and $outcomes.samples[1].jobOutcome -ceq $outcome -and
            $outcomes.metrics.executionSeconds.count -eq 3 -and $outcomes.extraSamples.Count -eq 0) 'Original cancellation/skip/incomplete attempt or its measurements disappeared.'
    }
    $global:NonUiCohortFixture.sampleJobs[1].conclusion = 'success'
    $originalInvocation = $failure.invocationId
    $firstReport = Get-Content -LiteralPath (Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-1"] 'result.json') -Raw | ConvertFrom-Json -AsHashtable
    $failure.invocationId = $firstReport.invocationId
    $failure | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $directory 'result.json')
    $duplicateWork = Join-Path $fixture 'duplicate'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $duplicateWork
    $duplicate = Get-Content -LiteralPath (Join-Path $duplicateWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($duplicate.reliability.incomplete -eq 2 -and $duplicate.samples.Count -eq 3 -and
        $duplicate.metrics.executionSeconds.count -eq 3) 'Reused execution identity passed or lost all-attempt evidence.'
    $failure.invocationId = $originalInvocation
    $failure | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $directory 'result.json')
    $artifactSnapshot = $global:NonUiCohortFixture.artifacts
    $global:NonUiCohortFixture.artifacts = @($artifactSnapshot | Where-Object { $_.name -cne $name })
    $missingWork = Join-Path $fixture 'missing'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $missingWork
    $missing = Get-Content -LiteralPath (Join-Path $missingWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($missing.samples[1].evidence.state -ceq 'missing' -and $null -eq $missing.samples[1].measurements -and
        $missing.samples.Count -eq 3 -and $missing.metrics.executionSeconds.unavailable -eq 1) 'A missing complete result was filled from another execution or counted as zero.'
    $global:NonUiCohortFixture.artifacts = $artifactSnapshot
    $profileFile = Join-Path $directory "$($failure.runDirectory)\scale-measurements.log"
    $timingSnapshot = Get-Content -LiteralPath $profileFile -Raw
    $timingSnapshot.Replace('"p95Ms":800', '"p95Ms":0') | Set-Content -LiteralPath $profileFile
    $substitutedWork = Join-Path $fixture 'substituted-profile'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $substitutedWork
    $substituted = Get-Content -LiteralPath (Join-Path $substitutedWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($substituted.samples[1].evidence.state -ceq 'rejected' -and $substituted.validation -ceq 'rejected' -and
        $substituted.metrics.executionSeconds.count -eq 3) 'A substituted raw percentile passed or dropped genuine execution timing.'
    $timingSnapshot | Set-Content -LiteralPath $profileFile
    for ($number = 4; $number -le 5; $number++) {
        $sourceDirectory = $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-3"]
        $nextDirectory = Join-Path $fixture "sample-$number"
        Copy-Item -LiteralPath $sourceDirectory -Destination $nextDirectory -Recurse
        $next = Get-Content -LiteralPath (Join-Path $nextDirectory 'result.json') -Raw | ConvertFrom-Json -AsHashtable
        $next.sample = $number; $next.invocationId = [guid]::NewGuid().ToString('N')
        $newRunDirectory = "run-$($next.invocationId)"
        Move-Item -LiteralPath (Join-Path $nextDirectory $next.runDirectory) -Destination (Join-Path $nextDirectory $newRunDirectory)
        $next.runDirectory = $newRunDirectory; $next.executionSeconds = if ($number -eq 4) { 140.0 } else { 160.0 }
        $next | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $nextDirectory 'result.json')
        $nextName = "non-ui-cohort-$cohortId-sample-$number"
        $global:NonUiCohortFixture.evidence[$nextName] = $nextDirectory
        $global:NonUiCohortFixture.sampleJobs += @{ id = 100 + $number; name = "Non-UI cohort sample ($number)"
            status = 'completed'; conclusion = 'success'; runner_id = 200 + $number }
        $global:NonUiCohortFixture.artifacts += @{ id = 300 + $number; name = $nextName; expired = $false
            expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
            workflow_run = @{ id = 99; head_sha = $revision } }
    }
    $fiveWork = Join-Path $fixture 'five'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $fiveWork -ExpectedSamples 5 -AllowLocalFixture `
        -TestPolicy @{ version = 'TEST-non-ui-range-v1'; maximumRangeSeconds = 40 }
    $five = Get-Content -LiteralPath (Join-Path $fiveWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($five.samples.Count -eq 5 -and $five.metrics.executionSeconds.median -eq 150 -and
        $five.eligibility -ceq 'inconclusive' -and $five.extraSamples.Count -eq 0 -and
        $five.promotion -ceq 'disabled') 'Five still-inconsistent executions were retried, normalized or promoted.'
    $runnerFixture = Join-Path $fixture 'runner'
    New-Item -ItemType Directory -Path (Join-Path $runnerFixture 'eng') -Force | Out-Null
    foreach ($file in @('Run-Performance.ps1', 'ScaleRunner.ps1', 'PerformanceSuite.ps1')) {
        Copy-Item -LiteralPath (Join-Path $root "eng\$file") -Destination (Join-Path $runnerFixture "eng\$file")
    }
    '.ci-results/' | Set-Content -LiteralPath (Join-Path $runnerFixture '.gitignore')
    git -C $runnerFixture init --quiet
    git -C $runnerFixture add .
    git -C $runnerFixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Isolated real performance runner'
    $runnerRevision = (git -C $runnerFixture rev-parse HEAD).Trim()
    $previousStarted = $env:SCALE_STARTED_AT
    try {
        $env:SCALE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-700).ToString('O')
        & pwsh -NoProfile -File (Join-Path $runnerFixture 'eng\Run-Performance.ps1') -ExpectedRevision $runnerRevision `
            -CohortId "$runnerRevision-99-1" -Sample 1 -RunId 99 -RunAttempt 1 2>&1 | Out-Null
        Assert ($LASTEXITCODE -ne 0) 'Exhausted real runner unexpectedly passed.'
        $terminal = Get-Content -LiteralPath (Join-Path $runnerFixture '.ci-results\performance\result.json') -Raw |
            ConvertFrom-Json -AsHashtable
        Assert ($terminal.cohortId -ceq "$runnerRevision-99-1" -and $terminal.sample -eq 1 -and
            $terminal.invocationId -cmatch '^[0-9a-f]{32}$' -and $terminal.runId -ceq '99' -and
            $terminal.runAttempt -ceq '1' -and $terminal.outcome -ceq 'timed_out' -and
            $terminal.steps.Count -eq 0 -and $terminal.executionSeconds -ge 700) 'Real runner lost independent failed-attempt identity or setup budget accounting.'
    } finally { $env:SCALE_STARTED_AT = $previousStarted }
    Write-Host "PASS: $checks public non-UI cohort checks."
} finally {
    Remove-Variable -Name NonUiCohortFixture -Scope Global
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
