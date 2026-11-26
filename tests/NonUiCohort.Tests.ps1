param([switch]$BootstrapPreviewOnly, [switch]$RichPolicyPreviewOnly)
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
$jobId = 1000
$jobs = @(foreach ($name in $names) {
    $jobId++
    @{ id = $jobId; name = $name; status = 'completed'; conclusion = 'success'; steps = @()
        started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:03:00Z' }
})
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
        '/actions/runs/42/attempts/2$' { $global:NonUiCohortFixture.run | ConvertTo-Json -Depth 10; return }
        '/actions/runs/42$' { $global:NonUiCohortFixture.latest | ConvertTo-Json -Depth 10; return }
        '/actions/runs/42(/attempts/[12])?/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:NonUiCohortFixture.jobs }) -Depth 10; return
        }
        '/git/ref/heads/main$' { @{ object = @{ sha = $global:NonUiCohortFixture.revision } } | ConvertTo-Json; return }
        '/actions/runs/99/attempts/[1-9][0-9]*$' { $global:NonUiCohortFixture.collection | ConvertTo-Json -Depth 10; return }
        '/actions/runs/99/attempts/[1-9][0-9]*/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:NonUiCohortFixture.sampleJobs }) -Depth 10; return
        }
        '/actions/runs/99/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = $global:NonUiCohortFixture.artifacts }) -Depth 10; return
        }
        '/actions/runs/42/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = @() }) -Depth 10; return
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
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $summaryWork -TimingPolicyPreview `
        -TimingPolicyVersion ci-timing-preview-v1
    $record = Get-Content -LiteralPath (Join-Path $summaryWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($record.samples.Count -eq 3 -and $record.reliability.success -eq 3 -and $record.reliability.attempts -eq 3) 'A complete cohort lost independent executions.'
    Assert ($record.metrics.executionSeconds.median -eq 150 -and $record.metrics.executionSeconds.minimum -eq 120 -and
        $record.metrics.executionSeconds.maximum -eq 180 -and $record.metrics.executionSeconds.range -eq 60) 'Cohort median/range do not preserve independent values.'
    Assert ($record.eligibility -ceq 'pending-calibration' -and $record.promotion -ceq 'disabled' -and
        $record.richScaleAcceptance -ceq 'not-executed' -and $record.extraSamples.Count -eq 0) 'Observation-only non-UI evidence claimed approved full-scale acceptance.'
    Assert ($record.policyPreview.version -ceq 'ci-timing-preview-v1' -and
        $record.policyPreview.mode -ceq 'observation' -and !$record.policyPreview.blocking -and
        $record.policyPreview.currentValidation -ceq 'success' -and
        $record.policyPreview.metrics.executionSeconds.health -ceq 'unbaselined' -and
        $record.policyPreview.promotion -ceq 'disabled' -and
        (Get-Content (Join-Path $summaryWork 'summary.md') -Raw).Contains('approval pending')) `
        'Public policy preview invented a reference, changed validation or acquired gate/promotion authority.'
    $bootstrapReports = @{}
    $bootstrapTiming = @{}
    $bootstrapJobs = $global:NonUiCohortFixture.sampleJobs
    $bootstrapArtifacts = $global:NonUiCohortFixture.artifacts
    $bootstrapHistory = Join-Path $fixture 'bootstrap-history.git'
    git init --bare --quiet $bootstrapHistory
    try {
        for ($number = 1; $number -le 3; $number++) {
            $path = Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json'
            $bootstrapReports[$number] = Get-Content $path -Raw
            $data = $bootstrapReports[$number] | ConvertFrom-Json -AsHashtable
            $bootstrapTiming[$number] = Get-Content (Join-Path (Split-Path $path -Parent) "$($data.runDirectory)\scale-measurements.log") -Raw
            $data.executionSeconds = @(600, 630, 640)[$number - 1]
            $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($data.executionSeconds).ToString('O')
            $data | ConvertTo-Json -Depth 15 | Set-Content $path
        }
        $bootstrapWork = Join-Path $fixture 'bootstrap-headroom'
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $bootstrapWork -ReadHistory `
            -AllowLocalFixture -HistoryRemote $bootstrapHistory -TimingPolicyPreview
        $bootstrap = Get-Content (Join-Path $bootstrapWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        $bootstrapMetric = $bootstrap.policyPreview.metrics.executionSeconds
        Assert ($bootstrap.validation -ceq 'success' -and $bootstrapMetric.range -eq 40 -and
            $bootstrapMetric.headroom -eq 20 -and $bootstrapMetric.lowHeadroom -and
            $bootstrapMetric.health -ceq 'yellow' -and $bootstrapMetric.relativeHealth -ceq 'unbaselined' -and
            $null -eq $bootstrapMetric.reference -and $null -eq $bootstrap.historySnapshot -and
            $bootstrap.policyPreview.additionalSamples.Count -eq 2 -and
            $bootstrap.policyPreview.additionalSamples[0] -eq 4 -and
            $bootstrap.policyPreview.additionalSamples[1] -eq 5 -and
            $bootstrap.extraSamples.Count -eq 0 -and !$bootstrap.policyPreview.blocking -and
            $bootstrap.promotion -ceq 'disabled') `
            'Bootstrap600/630/640s lost its40s range/20s headroom warning or proposed4/5 without a reference.'
        foreach ($number in @(4, 5)) {
            $directory = Join-Path $fixture "bootstrap-sample-$number"
            Copy-Item -LiteralPath $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-3"] -Destination $directory -Recurse
            $data = Get-Content (Join-Path $directory 'result.json') -Raw | ConvertFrom-Json -AsHashtable
            $data.sample = $number; $data.invocationId = [guid]::NewGuid().ToString('N')
            $newRun = "run-$($data.invocationId)"
            Move-Item -LiteralPath (Join-Path $directory $data.runDirectory) -Destination (Join-Path $directory $newRun)
            $data.runDirectory = $newRun
            $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $directory 'result.json')
            $name = "non-ui-cohort-$cohortId-sample-$number"
            $global:NonUiCohortFixture.evidence[$name] = $directory
            $global:NonUiCohortFixture.sampleJobs += @{ id = 100 + $number; name = "Non-UI cohort sample ($number)"
                status = 'completed'; conclusion = 'success'; runner_id = 200 + $number }
            $global:NonUiCohortFixture.artifacts += @{ id = 300 + $number; name = $name; expired = $false
                expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
                workflow_run = @{ id = 99; head_sha = $revision } }
        }
        for ($number = 1; $number -le 5; $number++) {
            $path = Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json'
            $data = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
            $data.executionSeconds = @(656, 657, 657, 658, 658)[$number - 1]
            $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($data.executionSeconds).ToString('O')
            $data | ConvertTo-Json -Depth 15 | Set-Content $path
        }
        $bootstrapFiveWork = Join-Path $fixture 'bootstrap-five-headroom'
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $bootstrapFiveWork -ExpectedSamples 5 `
            -ReadHistory -AllowLocalFixture -HistoryRemote $bootstrapHistory -TimingPolicyPreview
        $bootstrapFive = Get-Content (Join-Path $bootstrapFiveWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($bootstrapFive.validation -ceq 'success' -and
            $bootstrapFive.policyPreview.metrics.executionSeconds.range -eq 2 -and
            $bootstrapFive.policyPreview.metrics.executionSeconds.headroom -eq 2 -and
            $bootstrapFive.policyPreview.metrics.executionSeconds.lowHeadroom -and
            $bootstrapFive.policyPreview.eligibility -ceq 'inconclusive' -and
            $bootstrapFive.policyPreview.additionalSamples.Count -eq 0 -and
            $null -eq $bootstrapFive.policyPreview.metrics.executionSeconds.reference -and
            !$bootstrapFive.policyPreview.blocking -and $bootstrapFive.promotion -ceq 'disabled') `
            'Five complete stable bootstrap values with2s headroom stayed pending instead of final inconclusive/no reference.'
        $headroomReference = $bootstrap | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
        $headroomReference.Remove('policyPreview')
        $headroomReference.revision = 'c' * 40; $headroomReference.ciRevision = $headroomReference.revision
        $headroomReference.controllerRevision = $headroomReference.revision; $headroomReference.ciRunId = '40'
        $headroomReference.cohortRunId = '88'; $headroomReference.cohortId = "$($headroomReference.revision)-88-1"
        $headroomReference.observedAt = [DateTimeOffset]::UtcNow.AddMinutes(-30).ToString('O')
        for ($index = 0; $index -lt 3; $index++) {
            $headroomReference.samples[$index].jobId = 500 + $index
            $headroomReference.samples[$index].runnerId = 600 + $index
            $headroomReference.samples[$index].invocationId = [guid]::NewGuid().ToString('N')
            $headroomReference.samples[$index].evidence.url = 'https://github.com/geevensingh/org-explorer/actions/runs/88/attempts/1'
        }
        $headroomPeer = Join-Path $fixture 'bootstrap-reference'
        git clone --quiet $bootstrapHistory $headroomPeer
        git -C $headroomPeer checkout --quiet -b ci-health-data
        $headroomReferenceDirectory = Join-Path $headroomPeer "performance-cohorts\$($headroomReference.revision)\88"
        New-Item -ItemType Directory -Path $headroomReferenceDirectory -Force | Out-Null
        $headroomReference | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $headroomReferenceDirectory '1.json')
        git -C $headroomPeer add performance-cohorts
        git -C $headroomPeer -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet `
            -m 'Complete independent headroom reference fixture' `
            -m 'Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>'
        git -C $headroomPeer push --quiet --set-upstream origin ci-health-data
        $headroomTip = (git --git-dir=$bootstrapHistory rev-parse refs/heads/ci-health-data).Trim()
        for ($number = 1; $number -le 5; $number++) {
            $path = Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json'
            $data = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
            $data.executionSeconds = @(628, 629, 630, 631, 632)[$number - 1]
            $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($data.executionSeconds).ToString('O')
            $data | ConvertTo-Json -Depth 15 | Set-Content $path
        }
        $referenceFiveWork = Join-Path $fixture 'reference-five-headroom'
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $referenceFiveWork -ExpectedSamples 5 `
            -ReadHistory -AllowLocalFixture -HistoryRemote $bootstrapHistory -TimingPolicyPreview
        $referenceFive = Get-Content (Join-Path $referenceFiveWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        $referenceFiveMetric = $referenceFive.policyPreview.metrics.executionSeconds
        Assert ($referenceFive.validation -ceq 'success' -and $referenceFiveMetric.range -eq 4 -and
            $referenceFiveMetric.headroom -eq 28 -and $referenceFiveMetric.lowHeadroom -and
            $referenceFiveMetric.reference.range -eq 40 -and !$referenceFiveMetric.seriousRegression -and
            $referenceFive.policyPreview.eligibility -ceq 'inconclusive' -and
            $referenceFive.policyPreview.additionalSamples.Count -eq 0 -and
            (git --git-dir=$bootstrapHistory rev-parse refs/heads/ci-health-data).Trim() -ceq $headroomTip -and
            $referenceFiveMetric.reference.revision -ceq $headroomReference.revision) `
            'Five complete stable values with28s headroom versus40s reference range did not retain the prior reference.'
        $durationReference = $headroomReference | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
        $durationReference.revision = 'd' * 40; $durationReference.ciRevision = $durationReference.revision
        $durationReference.controllerRevision = $durationReference.revision
        $durationReference.cohortRunId = '87'; $durationReference.cohortId = "$($durationReference.revision)-87-1"
        $durationReference.observedAt = [DateTimeOffset]::UtcNow.AddMinutes(-10).ToString('O')
        for ($index = 0; $index -lt 3; $index++) {
            $durationReference.samples[$index].measurements.executionSeconds = @(148, 150, 152)[$index]
            $durationReference.samples[$index].measurements.headroomSeconds = @(512, 510, 508)[$index]
            $durationReference.samples[$index].invocationId = [guid]::NewGuid().ToString('N')
            $durationReference.samples[$index].evidence.url = 'https://github.com/geevensingh/org-explorer/actions/runs/87/attempts/1'
        }
        $durationReference.metrics.executionSeconds = @{
            count = 3; unavailable = 0; median = 150; minimum = 148; maximum = 152; range = 4
        }
        $durationReferenceDirectory = Join-Path $headroomPeer "performance-cohorts\$($durationReference.revision)\87"
        New-Item -ItemType Directory -Path $durationReferenceDirectory -Force | Out-Null
        $durationReference | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $durationReferenceDirectory '1.json')
        git -C $headroomPeer add performance-cohorts
        git -C $headroomPeer -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet `
            -m 'Duration significance reference fixture' `
            -m 'Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>'
        git -C $headroomPeer push --quiet
        foreach ($case in @(
            @{ value = 179.999; health = 'yellow'; serious = $false },
            @{ value = 180; health = 'red'; serious = $true },
            @{ value = 180.001; health = 'red'; serious = $true }
        )) {
            for ($number = 1; $number -le 5; $number++) {
                $path = Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json'
                $data = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
                $data.executionSeconds = $case.value
                $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($case.value).ToString('O')
                $data | ConvertTo-Json -Depth 15 | Set-Content $path
            }
            $durationWork = Join-Path $fixture "duration-floor-$($case.value)"
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $durationWork -ExpectedSamples 5 `
                -ReadHistory -AllowLocalFixture -HistoryRemote $bootstrapHistory -TimingPolicyPreview `
                -TimingPolicyVersion ci-timing-preview-v2
            $duration = Get-Content (Join-Path $durationWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
            Assert ($duration.policyPreview.metrics.executionSeconds.health -ceq $case.health -and
                $duration.policyPreview.metrics.executionSeconds.seriousRegression -eq $case.serious -and
                $duration.validation -ceq 'success' -and !$duration.policyPreview.blocking) `
                "Whole-duration practical20%/30s inclusive floors disagree at$($case.value)s versus150s."
        }
        function Write-PracticalReference([string]$Digit, [string]$RunId, [double[]]$Durations, [double[]]$Profiles) {
            $reference = $headroomReference | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
            $reference.revision = $Digit * 40; $reference.ciRevision = $reference.revision
            $reference.controllerRevision = $reference.revision
            $reference.cohortRunId = $RunId; $reference.cohortId = "$($reference.revision)-$RunId-1"
            $reference.observedAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('O')
            for ($index = 0; $index -lt 3; $index++) {
                $sample = $reference.samples[$index]
                $sample.invocationId = [guid]::NewGuid().ToString('N')
                $sample.measurements.executionSeconds = $Durations[$index]
                $sample.measurements.headroomSeconds = 660 - $Durations[$index]
                $sample.evidence.url = "https://github.com/geevensingh/org-explorer/actions/runs/$RunId/attempts/1"
                if ($Profiles) {
                    $sample.measurements.profile.samplesMs = @($Profiles[$index]) * 21
                    $sample.measurements.profile.p95Ms = $Profiles[$index]
                }
            }
            $reference.metrics.executionSeconds = @{
                count = 3; unavailable = 0; minimum = $Durations[0]; median = $Durations[1]
                maximum = $Durations[2]; range = $Durations[2] - $Durations[0]
            }
            if ($Profiles) {
                $reference.metrics.'profile-p95-ms' = @{
                    count = 3; unavailable = 0; minimum = $Profiles[0]; median = $Profiles[1]
                    maximum = $Profiles[2]; range = $Profiles[2] - $Profiles[0]
                }
            }
            $directory = Join-Path $headroomPeer "performance-cohorts\$($reference.revision)\$RunId"
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $reference | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $directory '1.json')
            git -C $headroomPeer add performance-cohorts
            git -C $headroomPeer -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet `
                -m 'Independent practical significance reference fixture' `
                -m 'Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>'
            git -C $headroomPeer push --quiet
        }
        foreach ($case in @(
            @{ name = 'duration-percent-only'; digit = 'e'; run = '86'; reference = @(98, 100, 102); value = 120; health = 'yellow' },
            @{ name = 'duration-absolute-only'; digit = 'f'; run = '85'; reference = @(198, 200, 202); value = 230; health = 'yellow' },
            @{ name = 'duration-zero-median'; digit = 'b'; run = '84'; reference = @(0, 0, 10); value = 50; health = 'inconclusive' }
        )) {
            Write-PracticalReference $case.digit $case.run $case.reference $null
            for ($number = 1; $number -le 5; $number++) {
                $path = Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json'
                $data = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
                $data.executionSeconds = $case.value
                $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($case.value).ToString('O')
                $data | ConvertTo-Json -Depth 15 | Set-Content $path
            }
            $practicalWork = Join-Path $fixture $case.name
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $practicalWork -ExpectedSamples 5 `
                -ReadHistory -AllowLocalFixture -HistoryRemote $bootstrapHistory -TimingPolicyPreview `
                -TimingPolicyVersion ci-timing-preview-v2
            $result = Get-Content (Join-Path $practicalWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
            $metric = $result.policyPreview.metrics.executionSeconds
            Assert ($metric.health -ceq $case.health -and !$metric.seriousRegression -and
                !$result.policyPreview.blocking -and $result.validation -ceq 'success') `
                'Duration became serious when only one practical floor was met or reference median was zero.'
            if ($case.name -ceq 'duration-zero-median') {
                Assert ($null -eq $metric.deltaPercent -and $null -eq $metric.practicalSignificance) `
                    'Zero reference median fabricated percentage significance.'
            }
        }
        foreach ($series in @(
            @{ digit = '1'; run = '83'; reference = @(495, 500, 505); cases = @(
                @{ value = 599.999; health = 'yellow'; serious = $false },
                @{ value = 600; health = 'red'; serious = $true },
                @{ value = 600.001; health = 'red'; serious = $true }) },
            @{ digit = '2'; run = '82'; reference = @(245, 250, 255); cases = @(
                @{ value = 300; health = 'yellow'; serious = $false }) },
            @{ digit = '3'; run = '81'; reference = @(795, 800, 805); cases = @(
                @{ value = 900; health = 'yellow'; serious = $false }) }
        )) {
            Write-PracticalReference $series.digit $series.run @(148, 150, 152) $series.reference
            foreach ($case in $series.cases) {
                for ($number = 1; $number -le 5; $number++) {
                    $directory = $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"]
                    $path = Join-Path $directory 'result.json'
                    $data = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
                    $data.executionSeconds = 150
                    $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds(150).ToString('O')
                    $data | ConvertTo-Json -Depth 15 | Set-Content $path
                    $timing = Join-Path $directory "$($data.runDirectory)\scale-measurements.log"
                    $lines = @(Get-Content $timing | Where-Object { !$_.StartsWith('SCALE ORDINARY-PROFILE ') })
                    $profile = @{ samplesMs = @($case.value) * 21; p95Ms = $case.value; budgetMs = 1000
                        boundary = 'Public explicit profile Save'
                        percentile = 'Nearest rank: sorted sample at ceil(0.95 * count), one based.' }
                    $lines += 'SCALE ORDINARY-PROFILE ' + ($profile | ConvertTo-Json -Depth 5 -Compress)
                    $lines | Set-Content $timing
                }
                $practicalWork = Join-Path $fixture "product-floor-$($series.run)-$($case.value)"
                & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
                    -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $practicalWork -ExpectedSamples 5 `
                    -ReadHistory -AllowLocalFixture -HistoryRemote $bootstrapHistory -TimingPolicyPreview `
                    -TimingPolicyVersion ci-timing-preview-v2
                $result = Get-Content (Join-Path $practicalWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
                Assert ($result.policyPreview.metrics.'profile-p95-ms'.health -ceq $case.health -and
                    $result.policyPreview.metrics.'profile-p95-ms'.seriousRegression -eq $case.serious -and
                    $result.validation -ceq 'success' -and !$result.policyPreview.blocking) `
                    "Product latency20%/100ms floors disagree at$($case.value)ms or only one floor sufficed."
            }
        }
    } finally {
        for ($number = 1; $number -le 3; $number++) {
            $directory = $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"]
            $bootstrapReports[$number] | Set-Content (Join-Path $directory 'result.json')
            $data = $bootstrapReports[$number] | ConvertFrom-Json -AsHashtable
            $bootstrapTiming[$number] | Set-Content (Join-Path $directory "$($data.runDirectory)\scale-measurements.log")
        }
        $global:NonUiCohortFixture.sampleJobs = $bootstrapJobs
        $global:NonUiCohortFixture.artifacts = $bootstrapArtifacts
    }
    if ($BootstrapPreviewOnly) {
        Write-Host 'PASS: public bootstrap, five-execution headroom and practical-significance boundaries.'
        return
    }
    . "$PSScriptRoot\UiScaleUnionFixture.ps1"
    $nonUiJobs = $global:NonUiCohortFixture.sampleJobs
    $nonUiArtifacts = $global:NonUiCohortFixture.artifacts
    $richFixtures = @{}
    for ($number = 1; $number -le 5; $number++) {
        $rich = New-RichCohortSampleFixture (Join-Path $fixture "rich-$number") $revision $number '99' '1'
        $richFixtures[$number] = $rich
        if ($number -gt 3) { continue }
        foreach ($worker in @('prefix', 'tail', 'union')) {
            $directory = $rich[$worker]
            $artifactName = "rich-cohort-$cohortId-sample-$number-$worker"
            $global:NonUiCohortFixture.evidence[$artifactName] = $directory
            $global:NonUiCohortFixture.sampleJobs += @{ id = 1000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker)
                name = "Rich cohort $worker ($number)"; status = 'completed'; conclusion = 'success'
                runner_id = 2000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker) }
            $global:NonUiCohortFixture.artifacts += @{ id = 3000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker)
                name = $artifactName; expired = $false; expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
                workflow_run = @{ id = 99; head_sha = $revision } }
        }
    }
    $richWork = Join-Path $fixture 'rich-summary'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $richWork -RichScale
    $richRecord = Get-Content (Join-Path $richWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    $richPlanWork = Join-Path $fixture 'rich-plan'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $richPlanWork -RichScale
    Assert ((Get-Content (Join-Path $richWork 'summary.md') -Raw).Contains('Complete performance/scale cohort')) `
        'Full rich cohort summary still claims only NON-UI or unexecuted rich acceptance.'
    Assert ($richRecord.schemaVersion -eq 2 -and $richRecord.richScaleAcceptance -ceq 'complete' -and
        $richRecord.reliability.success -eq 3 -and $richRecord.metrics.'prefix-executionSeconds'.count -eq 3 -and
        $richRecord.samples[0].rich.prefix.notes.'normal-executable'.localDurability.samplesMs.Count -eq 21 -and
        $richRecord.samples[0].rich.tail.profile.samplesMs.Count -eq 21 -and $richRecord.promotion -ceq 'disabled') `
        "Complete independent cohorts lost the real strict union, original rich samples or observation-only policy: $(@($richRecord.samples | ForEach-Object { $_.diagnostics }) -join '; ')"
    Assert ($richRecord.samples[0].rich.tail.phasesMs.ContainsKey('UI-close#2') -and
        $richRecord.metrics.ContainsKey('tail-phase-UI-close#2-ms')) 'Repeated original rich phase overwrote an earlier phase measurement.'
    $meaningfulRemote = Join-Path $fixture 'meaningfulness.git'
    $meaningfulPeer = Join-Path $fixture 'meaningfulness-reference'
    git init --bare --quiet $meaningfulRemote
    git clone --quiet $meaningfulRemote $meaningfulPeer
    git -C $meaningfulPeer checkout --quiet -b ci-health-data
    $meaningfulJson = ($richRecord | ConvertTo-Json -Depth 30).Replace($revision, ('f' * 40)).
        Replace('"99"', '"86"').Replace('-99-1', '-86-1').Replace('/runs/99/', '/runs/86/')
    foreach ($sample in $richRecord.samples) {
        foreach ($part in @($sample, $sample.rich.prefix, $sample.rich.tail, $sample.rich.union)) {
            $meaningfulJson = $meaningfulJson.Replace($part.invocationId, [guid]::NewGuid().ToString('N'))
        }
    }
    $meaningfulReference = $meaningfulJson | ConvertFrom-Json -AsHashtable
    $meaningfulReference.observedAt = [DateTimeOffset]::UtcNow.AddMinutes(-30).ToString('O')
    for ($index = 0; $index -lt 3; $index++) {
        foreach ($phase in @('normal-executable', 'background-export')) {
            $durability = $meaningfulReference.samples[$index].rich.prefix.notes[$phase].localDurability
            $durability.samplesMs = @(@(990, 1000, 1010)[$index]) * 21
            $durability.p95Ms = @(990, 1000, 1010)[$index]
            $durability.goalMet = $durability.p95Ms -le 1000
        }
    }
    foreach ($phase in @('normal-executable', 'background-export')) {
        $meaningfulReference.metrics["prefix-$phase-localDurability-p95-ms"] = @{
            count = 3; unavailable = 0; median = 1000; minimum = 990; maximum = 1010; range = 20
        }
    }
    $meaningfulDirectory = Join-Path $meaningfulPeer "rich-scale-cohorts\$($meaningfulReference.revision)\86"
    New-Item -ItemType Directory -Path $meaningfulDirectory -Force | Out-Null
    $meaningfulReference | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $meaningfulDirectory '1.json')
    git -C $meaningfulPeer add rich-scale-cohorts
    git -C $meaningfulPeer -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet `
        -m 'Independent rich practical significance reference fixture' `
        -m 'Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>'
    git -C $meaningfulPeer push --quiet --set-upstream origin ci-health-data
    $meaningfulJobs = $global:NonUiCohortFixture.sampleJobs
    $meaningfulArtifacts = $global:NonUiCohortFixture.artifacts
    $meaningfulFiles = @{}
    try {
        foreach ($number in @(4, 5)) {
            $name = "non-ui-cohort-$cohortId-sample-$number"
            $global:NonUiCohortFixture.sampleJobs += @{ id = 100 + $number; name = "Non-UI cohort sample ($number)"
                status = 'completed'; conclusion = 'success'; runner_id = 200 + $number }
            $global:NonUiCohortFixture.artifacts += @{ id = 300 + $number; name = $name; expired = $false
                expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
                workflow_run = @{ id = 99; head_sha = $revision } }
            foreach ($worker in @('prefix', 'tail', 'union')) {
                $name = "rich-cohort-$cohortId-sample-$number-$worker"
                $global:NonUiCohortFixture.evidence[$name] = $richFixtures[$number][$worker]
                $global:NonUiCohortFixture.sampleJobs += @{ id = 1000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker)
                    name = "Rich cohort $worker ($number)"; status = 'completed'; conclusion = 'success'
                    runner_id = 2000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker) }
                $global:NonUiCohortFixture.artifacts += @{ id = 3000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker)
                    name = $name; expired = $false; expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
                    workflow_run = @{ id = 99; head_sha = $revision } }
            }
        }
        for ($number = 1; $number -le 5; $number++) {
            $prefixReport = Get-Content (Join-Path $richFixtures[$number].prefix 'result.json') -Raw | ConvertFrom-Json
            $unionReport = Get-Content (Join-Path $richFixtures[$number].union 'result.json') -Raw | ConvertFrom-Json
            foreach ($phase in @('normal-executable', 'background-export')) {
                $name = "scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
                $path = Join-Path $richFixtures[$number].prefix "$($prefixReport.runDirectory)\$name"
                $duplicate = Join-Path $richFixtures[$number].union "$($unionReport.runDirectory)\prefix\$($prefixReport.runDirectory)\$name"
                foreach ($file in @($path, $duplicate)) { $meaningfulFiles[$file] = Get-Content $file -Raw }
                $note = $meaningfulFiles[$path] | ConvertFrom-Json -AsHashtable
                $note.localDurability.samplesMs = @(1050) * 21
                $note.localDurability.p95Ms = 1050; $note.localDurability.goalMet = $false
                $note | ConvertTo-Json -Depth 15 | Set-Content $path
                $note | ConvertTo-Json -Depth 15 | Set-Content $duplicate
            }
        }
        foreach ($version in @('ci-timing-preview-v1', 'ci-timing-preview-v2')) {
            $meaningfulWork = Join-Path $fixture "meaningfulness-$version"
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $meaningfulWork -ExpectedSamples 5 -RichScale `
                -ReadHistory -AllowLocalFixture -HistoryRemote $meaningfulRemote -TimingPolicyPreview `
                -TimingPolicyVersion $version
            $meaningful = Get-Content (Join-Path $meaningfulWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
            foreach ($phase in @('normal-executable', 'background-export')) {
                $metric = $meaningful.policyPreview.metrics["prefix-$phase-localDurability-p95-ms"]
                $expectedHealth = if ($version -ceq 'ci-timing-preview-v1') { 'red' } else { 'yellow' }
                Assert ($metric.health -ceq $expectedHealth -and
                    $metric.seriousRegression -eq ($version -ceq 'ci-timing-preview-v1') -and
                    $metric.deltaPercent -eq 5 -and $metric.reference.median -eq 1000 -and
                    $metric.absoluteRequirement -ceq 'satisfied' -and
                    $meaningful.validation -ceq 'success' -and !$meaningful.policyPreview.blocking) `
                    'The five-run1000-to1050 rich-note example became serious under amended practical floors or rewrote v1.'
            }
        }
    } finally {
        foreach ($file in $meaningfulFiles.Keys) { $meaningfulFiles[$file] | Set-Content $file }
        $global:NonUiCohortFixture.sampleJobs = $meaningfulJobs
        $global:NonUiCohortFixture.artifacts = $meaningfulArtifacts
    }
    if ($RichPolicyPreviewOnly) { Write-Host 'PASS: amended rich practical-significance policy and retained v1 preview.'; return }
    $richJobs = $global:NonUiCohortFixture.sampleJobs
    $richArtifacts = $global:NonUiCohortFixture.artifacts
    $tailName = "rich-cohort-$cohortId-sample-2-tail"
    $originalTail = $global:NonUiCohortFixture.evidence[$tailName]
    $global:NonUiCohortFixture.evidence[$tailName] = $richFixtures[1].tail
    $crossWork = Join-Path $fixture 'cross-sample'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $crossWork -RichScale -AllowLocalFixture `
        -TestPolicy @{ version = 'TEST-rich-range-v2'; maximumRangeSeconds = 40 }
    $cross = Get-Content (Join-Path $crossWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($cross.validation -ceq 'rejected' -and $cross.reliability.success -eq 2 -and
        $cross.samples[1].rich.tail.evidence.state -ceq 'rejected' -and $cross.extraSamples.Count -eq 0 -and
        $cross.metrics.'tail-executionSeconds'.unavailable -eq 1) 'Another sample tail supplied acceptance or hid unavailable evidence.'
    $global:NonUiCohortFixture.evidence[$tailName] = $originalTail
    $richPrefixPath = Join-Path $richFixtures[2].prefix 'result.json'
    $richPrefixOriginal = Get-Content $richPrefixPath -Raw
    foreach ($defect in @('attempt', 'diagnostic', 'budget', 'cancelled', 'skipped', 'missing-tail', 'during-typing', 'invalid-p95', 'no-worker', 'skipped-tail-template')) {
        $changed = $richPrefixOriginal | ConvertFrom-Json -AsHashtable
        $changedJob = @($richJobs | Where-Object { $_.name -ceq 'Rich cohort prefix (2)' })[0]
        $originalRunnerId = $changedJob.runner_id
        $tailJob = @($richJobs | Where-Object { $_.name -ceq 'Rich cohort tail (2)' })[0]
        $notePath = Join-Path $richFixtures[2].prefix "$($changed.runDirectory)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-background-export.json"
        $noteOriginal = Get-Content $notePath -Raw
        switch ($defect) {
            'attempt' { $changed.runAttempt = '2' }
            'diagnostic' { $changed.diagnosticOnly = $true }
            'budget' { $changed.executionSeconds = 660.001 }
            'cancelled' { $changedJob.conclusion = 'cancelled' }
            'skipped' { $changedJob.conclusion = 'skipped' }
            'no-worker' { $changedJob.runner_id = 0 }
            'skipped-tail-template' {
                $tailJob.name = 'non-ui-cohort-pilot / rich-sample-2 / Rich cohort tail (${{ inputs.sample }})'
                $tailJob.conclusion = 'skipped'
            }
            'missing-tail' { $global:NonUiCohortFixture.artifacts = @($richArtifacts | Where-Object { $_.name -cne $tailName }) }
            { $_ -in @('during-typing', 'invalid-p95') } {
                $note = $noteOriginal | ConvertFrom-Json -AsHashtable
                if ($defect -eq 'during-typing') { $note.localDurability.committedDuringTyping[0] = $false }
                else { $note.localDurability.p95Ms = $true }
                $note | ConvertTo-Json -Depth 20 | Set-Content $notePath
            }
        }
        $changed | ConvertTo-Json -Depth 20 | Set-Content $richPrefixPath
        $changed | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $richFixtures[2].prefix "$($changed.runDirectory)\result.json")
        $defectWork = Join-Path $fixture "rich-$defect"
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $defectWork -RichScale -AllowLocalFixture `
            -TestPolicy @{ version = 'TEST-rich-range-v2'; maximumRangeSeconds = 40 }
        $rejectedRich = Get-Content (Join-Path $defectWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($rejectedRich.validation -ceq 'rejected' -and $rejectedRich.reliability.attempts -eq 3 -and
            $rejectedRich.reliability.success -eq 2 -and $rejectedRich.extraSamples.Count -eq 0 -and
            $rejectedRich.metrics.executionSeconds.count -eq 3) "Rich $defect was voted away, retried or lost original non-UI evidence."
        if ($defect -eq 'skipped-tail-template') {
            Assert ($rejectedRich.samples[1].rich.tail.jobOutcome -ceq 'skipped' -and
                $rejectedRich.samples[1].rich.tail.validation -ceq 'skipped' -and
                $rejectedRich.samples[1].rich.tail.jobName -ceq $tailJob.name) 'Original skipped reusable worker was mistaken for a missing job.'
        }
        $richPrefixOriginal | Set-Content $richPrefixPath
        $richPrefixOriginal | Set-Content (Join-Path $richFixtures[2].prefix "$($changed.runDirectory)\result.json")
        $noteOriginal | Set-Content $notePath
        $changedJob.conclusion = 'success'
        $changedJob.runner_id = $originalRunnerId
        $tailJob.name = 'Rich cohort tail (2)'; $tailJob.conclusion = 'success'
        $global:NonUiCohortFixture.artifacts = $richArtifacts
    }
    $richPolicyWork = Join-Path $fixture 'rich-policy'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $richPolicyWork -RichScale -AllowLocalFixture `
        -TestPolicy @{ version = 'TEST-rich-range-v2'; maximumRangeSeconds = 40 }
    $richPolicy = Get-Content (Join-Path $richPolicyWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($richPolicy.eligibility -ceq 'inconsistent' -and $richPolicy.extraSamples.Count -eq 2 -and
        $richPolicy.extraSamples[0] -eq 4 -and $richPolicy.extraSamples[1] -eq 5) 'Complete eligible TEST rich inconsistency did not request precisely four/five.'
    $global:NonUiCohortFixture.sampleJobs = $nonUiJobs
    $global:NonUiCohortFixture.artifacts = $nonUiArtifacts
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
    foreach ($number in $testRecord.extraSamples) {
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
    $fiveNonUiJobs = $global:NonUiCohortFixture.sampleJobs
    $fiveNonUiArtifacts = $global:NonUiCohortFixture.artifacts
    $global:NonUiCohortFixture.sampleJobs += @($richJobs | Where-Object { $_.name.StartsWith('Rich cohort') })
    $global:NonUiCohortFixture.artifacts += @($richArtifacts | Where-Object { $_.name.StartsWith('rich-cohort-') })
    foreach ($number in @(4, 5)) {
        foreach ($worker in @('prefix', 'tail', 'union')) {
            $artifactName = "rich-cohort-$cohortId-sample-$number-$worker"
            $global:NonUiCohortFixture.evidence[$artifactName] = $richFixtures[$number][$worker]
            $global:NonUiCohortFixture.sampleJobs += @{ id = 1000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker)
                name = "Rich cohort $worker ($number)"; status = 'completed'; conclusion = 'success'
                runner_id = 2000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker) }
            $global:NonUiCohortFixture.artifacts += @{ id = 3000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker)
                name = $artifactName; expired = $false; expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
                workflow_run = @{ id = 99; head_sha = $revision } }
        }
    }
    $fiveRichWork = Join-Path $fixture 'five-rich'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $fiveRichWork -RichScale -ExpectedSamples 5 -AllowLocalFixture `
        -TestPolicy @{ version = 'TEST-rich-range-v2'; maximumRangeSeconds = 40 }
    $fiveRich = Get-Content (Join-Path $fiveRichWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($fiveRich.reliability.success -eq 5 -and $fiveRich.samples.Count -eq 5 -and
        $fiveRich.eligibility -ceq 'inconclusive' -and $fiveRich.extraSamples.Count -eq 0 -and
        $fiveRich.metrics.'prefix-executionSeconds'.count -eq 5) 'Complete rich five-execution bound lost original proof or continued collecting.'
    $unexpectedRichWork = Join-Path $fixture 'unexpected-rich'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $unexpectedRichWork -RichScale
    $unexpectedRich = Get-Content (Join-Path $unexpectedRichWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($unexpectedRich.samples.Count -eq 5 -and $unexpectedRich.unexpectedExecutions -and
        $unexpectedRich.validation -ceq 'rejected' -and $unexpectedRich.extraSamples.Count -eq 0) 'Unrequested rich executions were ignored to manufacture three greens.'
    $global:NonUiCohortFixture.sampleJobs = @($fiveNonUiJobs | ForEach-Object {
        $job = $_.Clone(); $job.conclusion = 'success'; $job
    })
    $global:NonUiCohortFixture.artifacts = $fiveNonUiArtifacts
    $global:NonUiCohortFixture.sampleJobs[4].conclusion = 'failure'
    $extraWork = Join-Path $fixture 'unexpected-extra'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $extraWork
    $extra = Get-Content -LiteralPath (Join-Path $extraWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($extra.samples.Count -eq 5 -and $extra.reliability.failure -eq 1 -and $extra.validation -ceq 'rejected' -and
        $extra.metrics.executionSeconds.count -eq 5 -and $extra.extraSamples.Count -eq 0) 'An unexpected failed execution was excluded to select three greens.'
    $global:NonUiCohortFixture.sampleJobs = @($global:NonUiCohortFixture.sampleJobs | Select-Object -First 3)
    $global:NonUiCohortFixture.artifacts = @($global:NonUiCohortFixture.artifacts | Select-Object -First 3)
    $global:NonUiCohortFixture.collection.status = 'completed'
    $global:NonUiCohortFixture.collection.run_attempt = 2
    $missingPlanWork = Join-Path $fixture 'missing-plan'
    Rejects {
        & "$root\eng\Invoke-NonUiCohortObservation.ps1" -RunId 99 -RunAttempt 2 -WorkingDirectory $missingPlanWork
    } 'Completion observation inferred candidate/write authority without its exact attempt plan.'
    Assert ((Get-Content -LiteralPath (Join-Path $missingPlanWork 'summary.md') -Raw).Contains('Exact-attempt plan is unavailable')) 'Unavailable cancellation qualification was hidden.'
    $global:NonUiCohortFixture.collection.status = 'in_progress'
    $global:NonUiCohortFixture.collection.run_attempt = 1
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
    $remote = Join-Path $fixture 'history.git'
    git init --bare --quiet $remote
    $global:NonUiCohortFixture.collection.status = 'completed'
    $global:NonUiCohortFixture.collection.conclusion = 'success'
    $publicationWork = Join-Path $fixture 'publication'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $publicationWork -Publish -AllowLocalFixture -HistoryRemote $remote
    $path = "performance-cohorts/$revision/99/1.json"
    $saved = (git --git-dir=$remote show "refs/heads/ci-health-data:$path") -join "`n" | ConvertFrom-Json -AsHashtable
    $savedBlob = (git --git-dir=$remote rev-parse "refs/heads/ci-health-data:$path").Trim()
    Assert ($saved.metrics.executionSeconds.median -eq 150 -and $saved.samples.Count -eq 3 -and
        $saved.samples[1].measurements.profile.samplesMs.Count -eq 21 -and $saved.promotion -ceq 'disabled') 'Real history lost complete independent numeric inputs or promoted the cohort.'
    $global:NonUiCohortFixture.sampleJobs = $richJobs
    $global:NonUiCohortFixture.artifacts = $richArtifacts
    $richPublicationWork = Join-Path $fixture 'rich-publication'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $richPublicationWork -Publish -RichScale -AllowLocalFixture -HistoryRemote $remote
    $richPath = "rich-scale-cohorts/$revision/99/1.json"
    $richSaved = (git --git-dir=$remote show "refs/heads/ci-health-data:$richPath") -join "`n" | ConvertFrom-Json -AsHashtable
    Assert ($richSaved.schemaVersion -eq 2 -and $richSaved.reliability.success -eq 3 -and
        $richSaved.samples[2].rich.prefix.notes.'background-export'.localDurability.samplesMs.Count -eq 21 -and
        $richSaved.samples[2].rich.receipt.head -ceq $richSaved.samples[2].rich.handoff.head -and $richSaved.promotion -ceq 'disabled') `
        'Shared immutable history lost exact rich proof/raw inputs or mixed legacy non-UI into full acceptance.'
    $prefixPeer = Join-Path $fixture 'prefix-reference'
    git clone --quiet --branch ci-health-data $remote $prefixPeer
    $prefixReferenceJson = ($richSaved | ConvertTo-Json -Depth 30).Replace($revision, ('d' * 40)).
        Replace('"99"', '"89"').Replace('-99-1', '-89-1').Replace('/runs/99/', '/runs/89/')
    foreach ($sample in $richSaved.samples) {
        foreach ($part in @($sample, $sample.rich.prefix, $sample.rich.tail, $sample.rich.union)) {
            $prefixReferenceJson = $prefixReferenceJson.Replace($part.invocationId, [guid]::NewGuid().ToString('N'))
        }
    }
    $prefixReference = $prefixReferenceJson | ConvertFrom-Json -AsHashtable
    $prefixReference.observedAt = [DateTimeOffset]::UtcNow.AddHours(-1).ToString('O')
    for ($index = 0; $index -lt 3; $index++) {
        $sample = $prefixReference.samples[$index]
        foreach ($part in @($sample, $sample.rich.prefix, $sample.rich.tail, $sample.rich.union)) {
            $part.jobId += 4000; $part.runnerId += 4000
        }
        $sample.rich.prefix.report.executionSeconds = @(620, 630, 640)[$index]
        $sample.rich.prefix.report.startedAt = [DateTimeOffset]::UtcNow.AddHours(-2).ToString('O')
        $sample.rich.prefix.report.completedAt = ([DateTimeOffset]$sample.rich.prefix.report.startedAt).
            AddSeconds($sample.rich.prefix.report.executionSeconds).ToString('O')
    }
    $prefixReference.metrics.'prefix-executionSeconds' = @{
        count = 3; unavailable = 0; median = 630; minimum = 620; maximum = 640; range = 20
    }
    $prefixReferenceDirectory = Join-Path $prefixPeer "rich-scale-cohorts\$($prefixReference.revision)\89"
    New-Item -ItemType Directory -Path $prefixReferenceDirectory -Force | Out-Null
    $prefixReference | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $prefixReferenceDirectory '1.json')
    git -C $prefixPeer add rich-scale-cohorts
    git -C $prefixPeer -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Complete compatible prefix reference fixture'
    git -C $prefixPeer push --quiet
    $cutoffReports = @{}
    for ($number = 1; $number -le 3; $number++) {
        $cutoffReports[$number] = Get-Content (Join-Path $richFixtures[$number].prefix 'result.json') -Raw
    }
    try {
        foreach ($seconds in @(659, 661)) {
            for ($number = 1; $number -le 3; $number++) {
                $data = $cutoffReports[$number] | ConvertFrom-Json -AsHashtable
                $data.executionSeconds = $seconds
                $data.startedAt = [DateTimeOffset]::UtcNow.AddHours(-1).ToString('O')
                $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($seconds).ToString('O')
                $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $richFixtures[$number].prefix 'result.json')
                $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $richFixtures[$number].prefix "$($data.runDirectory)\result.json")
            }
            $cutoffWork = Join-Path $fixture "old-cutoff-$seconds"
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $cutoffWork -RichScale `
                -ReadHistory -AllowLocalFixture -HistoryRemote $remote -TimingPolicyPreview
            $cutoff = Get-Content (Join-Path $cutoffWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
            $expectedValidation = if ($seconds -eq 659) { 'success' } else { 'rejected' }
            $expectedSuggestionCount = if ($seconds -eq 659) { 2 } else { 0 }
            Assert ($cutoff.validation -ceq $expectedValidation -and
                $cutoff.policyPreview.metrics.'prefix-executionSeconds'.health -ceq 'yellow' -and
                $cutoff.policyPreview.metrics.'prefix-executionSeconds'.limit -eq 900 -and
                !$cutoff.policyPreview.blocking -and $cutoff.policyPreview.additionalSamples.Count -eq $expectedSuggestionCount -and
                $cutoff.extraSamples.Count -eq 0 -and
                $cutoff.promotion -ceq 'disabled') `
                'A self reference or old cutoff fabricated different relative health, extra runs or changed original acceptance.'
        }
    } finally {
        for ($number = 1; $number -le 3; $number++) {
            $data = $cutoffReports[$number] | ConvertFrom-Json -AsHashtable
            $cutoffReports[$number] | Set-Content (Join-Path $richFixtures[$number].prefix 'result.json')
            $cutoffReports[$number] | Set-Content (Join-Path $richFixtures[$number].prefix "$($data.runDirectory)\result.json")
        }
    }
    $global:NonUiCohortFixture.sampleJobs = $nonUiJobs
    $global:NonUiCohortFixture.artifacts = $nonUiArtifacts
    & "$root\eng\Invoke-LaneHealth.ps1" -RunId 42 -RunAttempt 1 -WorkingDirectory (Join-Path $fixture 'mixed-core') `
        -HistoryRemote $remote -AllowLocalFixture -Publish
    $run.run_attempt = 2; $run.event = 'pull_request'; $run.head_branch = 'feature'
    & "$root\eng\Invoke-LaneHealth.ps1" -RunId 42 -RunAttempt 2 -WorkingDirectory (Join-Path $fixture 'mixed-observation') `
        -HistoryRemote $remote -AllowLocalFixture -PublishObservations
    $run.run_attempt = 1; $run.event = 'push'; $run.head_branch = 'main'
    & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt 1 -WorkingDirectory (Join-Path $fixture 'mixed-filesystem') `
        -HistoryRemote $remote -AllowLocalFixture -Publish
    $paths = @(git --git-dir=$remote ls-tree -r --name-only refs/heads/ci-health-data)
    Assert ($paths -ccontains $path -and $paths -ccontains 'lanes/42/1/core.json' -and
        $paths -ccontains 'filesystem/42/1.json' -and $paths -ccontains $richPath -and
        $paths -ccontains 'observations/42/2/core.json') 'Mixed public readers lost a complete/legacy cohort, qualified lane or ineligible observation namespace.'
    $observerRemote = Join-Path $fixture 'observer.git'
    git init --bare --quiet $observerRemote
    $planName = 'non-ui-cohort-plan-99-1'
    $global:NonUiCohortFixture.evidence[$planName] = $work
    $global:NonUiCohortFixture.artifacts += @{ id = 900; name = $planName; expired = $false
        workflow_run = @{ id = 99; head_sha = $revision } }
    & "$root\eng\Invoke-NonUiCohortObservation.ps1" -RunId 99 -RunAttempt 1 -WorkingDirectory (Join-Path $fixture 'completion-observer') `
        -HistoryRemote $observerRemote -AllowLocalFixture
    $observed = (git --git-dir=$observerRemote show "refs/heads/ci-health-data:$path") -join "`n" | ConvertFrom-Json -AsHashtable
    Assert ($observed.revision -ceq $revision -and $observed.reliability.success -eq 3 -and
        $observed.samples[2].measurements.profile.samplesMs.Count -eq 21) 'Real completion observer failed to authenticate and preserve its plan/results through history.'
    $richObserverRemote = Join-Path $fixture 'rich-observer.git'
    git init --bare --quiet $richObserverRemote
    $observerArtifacts = $global:NonUiCohortFixture.artifacts
    $global:NonUiCohortFixture.evidence[$planName] = $richPlanWork
    $global:NonUiCohortFixture.sampleJobs = $richJobs
    $global:NonUiCohortFixture.artifacts = @($richArtifacts) + @($observerArtifacts | Where-Object { $_.name -ceq $planName })
    & "$root\eng\Invoke-NonUiCohortObservation.ps1" -RunId 99 -RunAttempt 1 -WorkingDirectory (Join-Path $fixture 'rich-completion-observer') `
        -HistoryRemote $richObserverRemote -AllowLocalFixture
    $richObserved = (git --git-dir=$richObserverRemote show "refs/heads/ci-health-data:$richPath") -join "`n" | ConvertFrom-Json -AsHashtable
    Assert ($richObserved.schemaVersion -eq 2 -and $richObserved.richScaleAcceptance -ceq 'complete' -and
        $richObserved.reliability.success -eq 3 -and $richObserved.samples[0].rich.tail.profile.samplesMs.Count -eq 21) `
        'Trusted completion observer lost complete rich qualification, original raw proof or its separate namespace.'
    $global:NonUiCohortFixture.sampleJobs = $nonUiJobs
    $global:NonUiCohortFixture.artifacts = $observerArtifacts
    $global:NonUiCohortFixture.evidence[$planName] = $work
    $peer = Join-Path $fixture 'peer'
    git clone --quiet --branch ci-health-data $remote $peer
    $prior = ($saved | ConvertTo-Json -Depth 30) | ConvertFrom-Json -AsHashtable
    $prior.revision = 'c' * 40; $prior.ciRevision = $prior.revision; $prior.ciRunId = '40'
    $prior.cohortRunId = '88'; $prior.cohortId = "$($prior.revision)-88-1"
    $prior.observedAt = [DateTimeOffset]::UtcNow.AddMinutes(-30).ToString('O')
    for ($index = 0; $index -lt 3; $index++) {
        $prior.samples[$index].measurements.executionSeconds = @(110, 120, 130)[$index]
        $prior.samples[$index].measurements.headroomSeconds = @(550, 540, 530)[$index]
        $prior.samples[$index].invocationId = [guid]::NewGuid().ToString('N')
        $prior.samples[$index].jobId = 500 + $index; $prior.samples[$index].runnerId = 600 + $index
        $prior.samples[$index].evidence.url = 'https://github.com/geevensingh/org-explorer/actions/runs/88/attempts/1'
        $prior.samples[$index].evidence.expiresAt = '2026-11-20T00:00:00Z'
    }
    $prior.metrics.executionSeconds.median = 120; $prior.metrics.executionSeconds.minimum = 110
    $prior.metrics.executionSeconds.maximum = 130; $prior.metrics.executionSeconds.range = 20
    $priorDirectory = Join-Path $peer "performance-cohorts\$($prior.revision)\88"
    New-Item -ItemType Directory -Path $priorDirectory -Force | Out-Null
    $prior | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $priorDirectory '1.json')
    git -C $peer add performance-cohorts
    git -C $peer -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Prior complete observation fixture'
    git -C $peer push --quiet
    $comparisonWork = Join-Path $fixture 'comparison'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $comparisonWork -ReadHistory -AllowLocalFixture -HistoryRemote $remote `
        -TimingPolicyPreview
    $comparison = Get-Content -LiteralPath (Join-Path $comparisonWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($comparison.reference.revision -ceq $prior.revision -and $comparison.reference.medianSeconds -eq 120 -and
        $comparison.reference.deltaSeconds -eq 30 -and $comparison.reference.evidenceState -ceq 'expired-or-unavailable') 'Pinned comparison self-baselined or lost compact values after artifact expiry.'
    Assert ($comparison.policyPreview.metrics.executionSeconds.health -ceq 'yellow' -and
        $comparison.policyPreview.metrics.executionSeconds.reference.revision -ceq $prior.revision -and
        $comparison.policyPreview.metrics.executionSeconds.reference.maximum -eq 130 -and
        $comparison.policyPreview.metrics.executionSeconds.reference.range -eq 20 -and
        $comparison.policyPreview.additionalSamples.Count -eq 2 -and
        $comparison.policyPreview.additionalSamples[0] -eq 4 -and !$comparison.policyPreview.blocking -and
        $comparison.validation -ceq 'success') 'Observed variation became a blocker, self-comparison or unbounded retry.'
    $previewReports = @{}
    for ($number = 1; $number -le 5; $number++) {
        $previewReports[$number] = Get-Content (Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json') -Raw
    }
    try {
        $global:NonUiCohortFixture.sampleJobs = $fiveNonUiJobs
        $global:NonUiCohortFixture.artifacts = $fiveNonUiArtifacts
        for ($number = 1; $number -le 5; $number++) {
            $data = $previewReports[$number] | ConvertFrom-Json -AsHashtable
            $data.executionSeconds = 199 + $number
            $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($data.executionSeconds).ToString('O')
            $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json')
        }
        $slowWork = Join-Path $fixture 'supported-slowdown'
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $slowWork -ExpectedSamples 5 `
            -ReadHistory -AllowLocalFixture -HistoryRemote $remote -TimingPolicyPreview
        $slow = Get-Content (Join-Path $slowWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($slow.policyPreview.metrics.executionSeconds.health -ceq 'red' -and
            $slow.policyPreview.metrics.executionSeconds.seriousRegression -and !$slow.policyPreview.blocking -and
            $slow.policyPreview.additionalSamples.Count -eq 0 -and $slow.validation -ceq 'success' -and
            $slow.promotion -ceq 'disabled') 'Five separated complete measurements did not preview serious deterioration without activating a gate.'
        Assert ($slow.policyPreview.metrics.'profile-p95-ms'.health -ceq 'inconclusive' -and
            !$slow.policyPreview.metrics.'profile-p95-ms'.seriousRegression) `
            'A zero-range reference was treated as a calibrated noise floor or comparative green.'
        for ($number = 1; $number -le 5; $number++) {
            $data = $previewReports[$number] | ConvertFrom-Json -AsHashtable
            $data.executionSeconds = @(111, 120, 129, 160, 100)[$number - 1]
            $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($data.executionSeconds).ToString('O')
            $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json')
        }
        $noisyWork = Join-Path $fixture 'five-remains-variable'
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $noisyWork -ExpectedSamples 5 `
            -ReadHistory -AllowLocalFixture -HistoryRemote $remote -TimingPolicyPreview
        $noisy = Get-Content (Join-Path $noisyWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($noisy.policyPreview.metrics.executionSeconds.health -ceq 'yellow' -and
            !$noisy.policyPreview.metrics.executionSeconds.seriousRegression -and
            $noisy.policyPreview.eligibility -ceq 'inconclusive' -and
            $noisy.policyPreview.additionalSamples.Count -eq 0 -and $noisy.validation -ceq 'success') `
            'Five still-variable observations manufactured a serious regression or another collection extension.'
        $global:NonUiCohortFixture.sampleJobs = $nonUiJobs
        $global:NonUiCohortFixture.artifacts = $nonUiArtifacts
        for ($number = 1; $number -le 3; $number++) {
            $data = $previewReports[$number] | ConvertFrom-Json -AsHashtable
            $data.executionSeconds = @(639, 640, 641)[$number - 1]
            $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($data.executionSeconds).ToString('O')
            $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json')
        }
        $headroomWork = Join-Path $fixture 'low-headroom'
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $headroomWork -ReadHistory `
            -AllowLocalFixture -HistoryRemote $remote -TimingPolicyPreview
        $headroom = Get-Content (Join-Path $headroomWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($headroom.policyPreview.metrics.executionSeconds.lowHeadroom -and
            $headroom.policyPreview.metrics.executionSeconds.headroom -eq 19 -and
            $headroom.policyPreview.metrics.executionSeconds.health -ceq 'yellow' -and
            !$headroom.policyPreview.blocking -and $headroom.validation -ceq 'success') `
            'One observed reference-range of remaining headroom was hidden or changed current acceptance.'
        foreach ($case in @(
            @{ name = 'normal-variation'; seconds = @(111, 120, 129); health = 'green'; suggestions = 0 },
            @{ name = 'single-slight-slowdown'; seconds = @(111, 120, 131); health = 'yellow'; suggestions = 2 }
        )) {
            for ($number = 1; $number -le 3; $number++) {
                $data = $previewReports[$number] | ConvertFrom-Json -AsHashtable
                $data.executionSeconds = $case.seconds[$number - 1]
                $data.completedAt = ([DateTimeOffset]$data.startedAt).AddSeconds($data.executionSeconds).ToString('O')
                $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json')
            }
            $caseWork = Join-Path $fixture $case.name
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $caseWork -ReadHistory `
                -AllowLocalFixture -HistoryRemote $remote -TimingPolicyPreview
            $caseResult = Get-Content (Join-Path $caseWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
            Assert ($caseResult.policyPreview.metrics.executionSeconds.health -ceq $case.health -and
                !$caseResult.policyPreview.metrics.executionSeconds.seriousRegression -and
                $caseResult.policyPreview.additionalSamples.Count -eq $case.suggestions -and
                $caseResult.validation -ceq 'success' -and !$caseResult.policyPreview.blocking) `
                'Ordinary variation or one slightly slow execution acquired a serious blocking regression.'
        }
        foreach ($outcome in @('cancelled', 'skipped', 'timed_out')) {
            $global:NonUiCohortFixture.sampleJobs[1].conclusion = $outcome
            $incompleteWork = Join-Path $fixture "preview-$outcome"
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $incompleteWork -ReadHistory `
                -AllowLocalFixture -HistoryRemote $remote -TimingPolicyPreview
            $incomplete = Get-Content (Join-Path $incompleteWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
            Assert ($incomplete.validation -ceq 'rejected' -and
                $incomplete.policyPreview.metrics.executionSeconds.health -ceq 'inconclusive' -and
                $incomplete.policyPreview.additionalSamples.Count -eq 0 -and
                $incomplete.samples[1].jobOutcome -ceq $outcome -and !$incomplete.policyPreview.blocking) `
                'Unfinished or cancelled verification fabricated comparative green or a replacement attempt.'
        }
        $global:NonUiCohortFixture.sampleJobs[1].conclusion = 'success'
    } finally {
        for ($number = 1; $number -le 5; $number++) {
            $previewReports[$number] | Set-Content (Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-$number"] 'result.json')
        }
        $global:NonUiCohortFixture.sampleJobs = $nonUiJobs
        $global:NonUiCohortFixture.artifacts = $observerArtifacts
    }
    $hardReport = $previewReports[1] | ConvertFrom-Json -AsHashtable
    $hardFile = Join-Path $global:NonUiCohortFixture.evidence["non-ui-cohort-$cohortId-sample-1"] "$($hardReport.runDirectory)\scale-measurements.log"
    $hardOriginal = Get-Content $hardFile -Raw
    try {
        $hardOriginal.Replace('800', '1000.1') | Set-Content $hardFile
        $hardWork = Join-Path $fixture 'hard-policy-preview'
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $hardWork -ReadHistory `
            -AllowLocalFixture -HistoryRemote $remote -TimingPolicyPreview
        $hard = Get-Content (Join-Path $hardWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($hard.validation -ceq 'rejected' -and $hard.reliability.failure -eq 1 -and
            $hard.policyPreview.metrics.'profile-p95-ms'.health -ceq 'red' -and
            !$hard.policyPreview.metrics.'profile-p95-ms'.seriousRegression -and
            $hard.policyPreview.additionalSamples.Count -eq 0 -and !$hard.policyPreview.blocking) `
            'A cohort median erased an individual1000ms product violation or relative preview activated a gate.'
    } finally { $hardOriginal | Set-Content $hardFile }
    $planWork = Join-Path $fixture 'already-observed'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 100 -CohortRunAttempt 1 -WorkingDirectory $planWork -CheckHistory -AllowLocalFixture -HistoryRemote $remote
    $duplicatePlan = Get-Content -LiteralPath (Join-Path $planWork 'plan.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert (!$duplicatePlan.collect) 'Already observed main revision started an unbounded retry cohort.'
    $jobSnapshot = $global:NonUiCohortFixture.sampleJobs
    $global:NonUiCohortFixture.collection.run_attempt = 2
    $global:NonUiCohortFixture.collection.conclusion = 'cancelled'
    $global:NonUiCohortFixture.sampleJobs = @(1..3 | ForEach-Object {
        @{ id = 700 + $_; name = "Non-UI cohort sample ($_)"; status = 'completed'; conclusion = 'cancelled'; runner_id = 0 }
    })
    $cancelledWork = Join-Path $fixture 'cancelled-history'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 2 -WorkingDirectory $cancelledWork -Publish -AllowLocalFixture -HistoryRemote $remote
    $cancelled = (git --git-dir=$remote show "refs/heads/ci-health-data:performance-cohorts/$revision/99/2.json") -join "`n" |
        ConvertFrom-Json -AsHashtable
    Assert ($cancelled.reliability.cancelled -eq 3 -and $cancelled.samples.Count -eq 3 -and
        $cancelled.metrics.Count -eq 0 -and $cancelled.collectionOutcome -ceq 'cancelled') 'Cancelled independent attempts were dropped or fabricated after earlier greens.'
    Assert ((git --git-dir=$remote rev-parse "refs/heads/ci-health-data:$path").Trim() -ceq $savedBlob) 'A later cancellation rewrote the earlier cohort.'
    $cancelledRichWork = Join-Path $fixture 'cancelled-rich-history'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 2 -WorkingDirectory $cancelledRichWork -Publish -RichScale -AllowLocalFixture -HistoryRemote $remote
    $cancelledRich = (git --git-dir=$remote show "refs/heads/ci-health-data:rich-scale-cohorts/$revision/99/2.json") -join "`n" |
        ConvertFrom-Json -AsHashtable
    Assert ($cancelledRich.reliability.cancelled -eq 3 -and $cancelledRich.metrics.Count -eq 0 -and
        $cancelledRich.validation -ceq 'rejected' -and $cancelledRich.samples.Count -eq 3) 'Cancelled full cohort fabricated measurements or lost unavailable workers.'
    $global:NonUiCohortFixture.collection.run_attempt = 1
    $global:NonUiCohortFixture.collection.conclusion = 'success'
    $global:NonUiCohortFixture.sampleJobs = $jobSnapshot
    $refusedRemote = Join-Path $fixture 'refused.git'
    git init --bare --quiet $refusedRemote
    "#!/bin/sh`nexit 1" | Set-Content -LiteralPath (Join-Path $refusedRemote 'hooks\pre-receive') -Encoding utf8NoBOM
    $refusedWork = Join-Path $fixture 'refused'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $refusedWork -Publish -AllowLocalFixture -HistoryRemote $refusedRemote
    } 'Publication failure was reported as durable success.'
    Assert ((Get-Content -LiteralPath (Join-Path $refusedWork 'summary.md') -Raw).Contains('History publication: FAILED')) 'Publication error is not visible at the public summary seam.'
    $badHistory = Join-Path $fixture 'bad-history'
    git clone --quiet --branch ci-health-data $remote $badHistory
    $badRichPath = Join-Path $badHistory "rich-scale-cohorts\$revision\99\1.json"
    $badRich = Get-Content $badRichPath -Raw | ConvertFrom-Json -AsHashtable
    $badRichOriginal = Get-Content $badRichPath -Raw
    $badRich.samples[0].rich.prefix.report.requiredScenarios = @('substituted-rich-workload')
    $badRich | ConvertTo-Json -Depth 30 | Set-Content $badRichPath
    git -C $badHistory add rich-scale-cohorts
    git -C $badHistory -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Substituted rich selection fixture'
    git -C $badHistory push --quiet
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'bad-rich-selection') `
            -ReadHistory -AllowLocalFixture -HistoryRemote $remote
    } 'Compact complete-cohort history accepted substituted original rich selection.'
    $badRichOriginal | Set-Content $badRichPath
    git -C $badHistory add rich-scale-cohorts
    git -C $badHistory -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Restore original rich selection fixture'
    git -C $badHistory push --quiet
    $badCancelledPath = Join-Path $badHistory "rich-scale-cohorts\$revision\99\2.json"
    $badCancelledOriginal = Get-Content $badCancelledPath -Raw
    $badCancelled = $badCancelledOriginal | ConvertFrom-Json -AsHashtable
    $badCancelled.reliability.cancelled = 0
    $badCancelled | ConvertTo-Json -Depth 30 | Set-Content $badCancelledPath
    git -C $badHistory add rich-scale-cohorts
    git -C $badHistory -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Dropped cancelled rich attempts fixture'
    git -C $badHistory push --quiet
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'bad-rich-reliability') `
            -ReadHistory -AllowLocalFixture -HistoryRemote $remote
    } 'Rich all-attempt history voted away cancellation when no metric was available.'
    $badCancelledOriginal | Set-Content $badCancelledPath
    git -C $badHistory add rich-scale-cohorts
    git -C $badHistory -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Restore cancelled rich attempts fixture'
    git -C $badHistory push --quiet
    $badPath = Join-Path $badHistory "performance-cohorts\$revision\99\1.json"
    $bad = Get-Content -LiteralPath $badPath -Raw | ConvertFrom-Json -AsHashtable
    $bad.timingHealth = 'green'
    $bad | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $badPath
    git -C $badHistory add performance-cohorts
    git -C $badHistory -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Malformed uncalibrated health fixture'
    git -C $badHistory push --quiet
    $badWork = Join-Path $fixture 'bad-observation'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $revision -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $badWork -ReadHistory -AllowLocalFixture -HistoryRemote $remote
    } 'Malformed uncalibrated green history was accepted by the real registered validator.'
    Assert ((Get-Content -LiteralPath (Join-Path $badWork 'summary.md') -Raw).Contains('History read failed')) 'Malformed history failure was silently hidden.'
    Write-Host "PASS: $checks public non-UI cohort checks."
} finally {
    Remove-Variable -Name NonUiCohortFixture -Scope Global
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
