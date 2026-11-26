param([switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.test-workspaces\baselines-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
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
function Invoke-FixtureGit([string]$Directory, [string[]]$Arguments) {
    $output = & git -C $Directory @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $($output -join ' ')" }
    return ($output -join "`n")
}
function gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -ceq 'run' -and $args[1] -ceq 'download') {
        $name = $args[[Array]::IndexOf($args, '--name') + 1]
        $destination = $args[[Array]::IndexOf($args, '--dir') + 1]
        Copy-Item -LiteralPath $global:BaselineFixture.evidence[$name] -Destination $destination -Recurse
        return
    }
    switch -Regex ($args[1]) {
        '/actions/runs/42/attempts/1$' { $global:BaselineFixture.ci | ConvertTo-Json -Depth 10; return }
        '/actions/runs/42(/attempts/1)?/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:BaselineFixture.ciJobs }) -Depth 10; return
        }
        '/actions/runs/42/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = @() }) -Depth 10; return
        }
        '/actions/runs/99/attempts/1$' {
            @{ id = 99; run_attempt = 1; head_sha = $global:BaselineFixture.controller
                event = 'workflow_run'; path = '.github/workflows/non-ui-cohorts.yml'; status = $global:BaselineFixture.status
                conclusion = $global:BaselineFixture.outcome
                head_repository = @{ full_name = 'geevensingh/org-explorer' } } | ConvertTo-Json -Depth 10
            return
        }
        '/actions/runs/99/attempts/1/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:BaselineFixture.jobs }) -Depth 10; return
        }
        '/actions/runs/99/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = $global:BaselineFixture.artifacts }) -Depth 10; return
        }
        default { throw "Unexpected external GitHub operation: $($args -join ' ')" }
    }
}
function New-CompleteBaselineSample([int]$Number, [string]$Revision, [double]$Seconds, [double]$Latency) {
    . "$PSScriptRoot\UiScaleUnionFixture.ps1"
    . "$root\eng\NonUiCohort.ps1"
    $directory = Join-Path $fixture "evidence-$Revision-$Number"
    $nonUi = Join-Path $directory 'non-ui'
    $invocation = [guid]::NewGuid().ToString('N')
    $runName = "run-$invocation"
    New-Item -ItemType Directory -Path (Join-Path $nonUi $runName) -Force | Out-Null
    $report = @{
        schemaVersion = 1; suite = 'non-ui-performance'; revision = $Revision; expectedRevision = $Revision
        cohortId = "$Revision-99-1"; sample = $Number; invocationId = $invocation; runId = '99'; runAttempt = '1'
        filter = 'Category=Performance'; requiredTests = @($PerformanceTests)
        outcome = 'success'; observedOutcome = 'success'; diagnosticOnly = $false; phase = 'finished'
        trackedChanges = $false; untrackedChanges = $false; ceilingSeconds = 660; observationSeconds = 660
        executionSeconds = $Seconds; runDirectory = $runName
        startedAt = '2026-11-25T10:00:00Z'; completedAt = ([DateTimeOffset]'2026-11-25T10:00:00Z').AddSeconds($Seconds).ToString('O')
        steps = @(@{ name = 'restore'; seconds = 10; exitCode = 0 }, @{ name = 'test'; seconds = 100; exitCode = 0 })
        configuration = @{ workload = 'OrgExplorer.Persistence.Tests'; testTree = 'b' * 40; fixtureTree = 'c' * 40
            sdk = '10.0.100'; runtimes = @('Microsoft.NETCore.App 10.0.0'); os = 'Windows'; architecture = 'X64'
            processors = 4; image = 'win25'; imageVersion = '20261116'; powershell = '7.6'
            setup = 'locked-restore;isolated-build;setup-and-cleanup-included' }
    }
    $report | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $nonUi 'result.json')
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results><UnitTestResult testName="$($PerformanceTests[0])" outcome="Passed" /></Results>
<ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
"@ | Set-Content (Join-Path $nonUi "$runName\result.trx")
    $log = @($NonUiCohortPhases | ForEach-Object { "SCALE END $_; outcome=Passed; ms=10.0;" })
    $profile = @{ samplesMs = @($Latency) * 21; p95Ms = $Latency; budgetMs = 1000
        percentile = 'Nearest rank: sorted sample at ceil(0.95 * count), one based.' }
    $log += 'SCALE ORDINARY-PROFILE ' + ($profile | ConvertTo-Json -Compress)
    $log | Set-Content (Join-Path $nonUi "$runName\scale-measurements.log")
    $rich = New-RichCohortSampleFixture (Join-Path $directory 'rich') $Revision $Number '99' '1'
    foreach ($worker in @('prefix', 'tail')) {
        $path = Join-Path $rich[$worker] 'result.json'
        $data = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        $data.policyVersion = 'ci-timing-v2'
        if ($worker -ceq 'prefix') { $data.ceilingSeconds = 900; $data.observationSeconds = 900 }
        $data.executionSeconds = $Seconds
        $data.startedAt = $report.startedAt; $data.completedAt = $report.completedAt
        $data | ConvertTo-Json -Depth 15 | Set-Content $path
        $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $rich[$worker] "$($data.runDirectory)\result.json")
        $profilePath = Join-Path $rich[$worker] "$($data.runDirectory)\scale-mutation-$(if ($worker -ceq 'prefix') { 'save-prefix' } else { 'delivery-tail' })-ui-sqlite-rich-profile-latency-$(if ($worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }).json"
        $rawProfile = Get-Content $profilePath -Raw | ConvertFrom-Json -AsHashtable
        $rawProfile.samplesMs = @($Latency) * 21; $rawProfile.p95Ms = $Latency
        $rawProfile | ConvertTo-Json -Depth 15 | Set-Content $profilePath
        if ($worker -ceq 'prefix') {
            foreach ($phase in @('normal-executable', 'background-export')) {
                $notePath = Join-Path $rich.prefix "$($data.runDirectory)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
                $note = Get-Content $notePath -Raw | ConvertFrom-Json -AsHashtable
                $note.localDurability.samplesMs = @($Latency) * 21; $note.localDurability.p95Ms = $Latency
                $note.localDurability.budgetMs = 1500; $note.localDurability.policyVersion = 'ci-timing-v2'
                $note | ConvertTo-Json -Depth 15 | Set-Content $notePath
            }
        }
    }
    & "$root\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $rich.prefix -TailDirectory $rich.tail `
        -ExpectedRevision $Revision -RunId 99 -RunAttempt 1 -ResultsDirectory (Join-Path $directory 'active-union') `
        -CohortId "$Revision-99-1" -Sample $Number -ExpectedPolicyVersion ci-timing-v2
    $rich.union = Join-Path $directory 'active-union'
    foreach ($worker in @('non-ui', 'prefix', 'tail', 'union')) {
        $name = if ($worker -ceq 'non-ui') { "non-ui-cohort-$Revision-99-1-sample-$Number" }
            else { "rich-cohort-$Revision-99-1-sample-$Number-$worker" }
        $global:BaselineFixture.evidence[$name] = if ($worker -ceq 'non-ui') { $nonUi } else { $rich[$worker] }
        $id = 1000 + 10 * $Number + [array]::IndexOf(@('non-ui', 'prefix', 'tail', 'union'), $worker)
        $global:BaselineFixture.jobs += @{ id = $id; runner_id = $id + 1000; status = 'completed'; conclusion = 'success'
            name = if ($worker -ceq 'non-ui') { "Non-UI cohort sample ($Number)" } else { "Rich cohort $worker ($Number)" } }
        $global:BaselineFixture.artifacts += @{ id = $id + 2000; name = $name; expired = $false
            expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
            workflow_run = @{ id = 99; head_sha = $global:BaselineFixture.controller } }
    }
}
try {
    $remote = Join-Path $fixture 'remote.git'
    $source = Join-Path $fixture 'source'
    & git init --bare --quiet $remote
    & git clone --quiet $remote $source
    $null = Invoke-FixtureGit $source @('checkout', '--quiet', '-b', 'main')
    'First product revision' | Set-Content (Join-Path $source 'product.txt')
    $null = Invoke-FixtureGit $source @('add', 'product.txt')
    $null = Invoke-FixtureGit $source @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '-m', 'First main fixture')
    $first = Invoke-FixtureGit $source @('rev-parse', 'HEAD')
    'Second product revision' | Set-Content (Join-Path $source 'product.txt')
    New-Item -ItemType Directory -Path (Join-Path $source 'eng') -Force | Out-Null
    '{"version":"ci-timing-v2"}' | Set-Content (Join-Path $source 'eng\timing-policy.json')
    $null = Invoke-FixtureGit $source @('add', 'product.txt', 'eng\timing-policy.json')
    $null = Invoke-FixtureGit $source @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '-m', 'Second main fixture')
    $second = Invoke-FixtureGit $source @('rev-parse', 'HEAD')
    $null = Invoke-FixtureGit $source @('push', '--quiet', 'origin', 'main')
    $ci = @{
        id = 42; run_attempt = 1; head_sha = $second; event = 'push'; head_branch = 'main'
        path = '.github/workflows/ci.yml'; status = 'completed'; conclusion = 'success'
        head_repository = @{ full_name = 'geevensingh/org-explorer' }
    }
    $ciJobs = @(@('Core and view models', 'Real filesystem persistence', 'Actual Windows desktop smoke (people)',
        'Actual Windows desktop smoke (workspace)', 'High-resolution Windows smoke (reading)',
        'High-resolution Windows smoke (context)', 'CI') | ForEach-Object {
        @{ name = $_; status = 'completed'; conclusion = 'success' }
    })
    $global:BaselineFixture = @{ ci = $ci; ciJobs = $ciJobs; controller = $second; status = 'in_progress'
        outcome = $null; jobs = @(); artifacts = @(); evidence = @{} }
    $work = Join-Path $fixture 'plan'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $work -RichScale -CheckHistory `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2 -BootstrapRevision $second
    $plan = Get-Content (Join-Path $work 'plan.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($plan.mode -ceq 'automatic-baseline' -and $plan.policyVersion -ceq 'ci-timing-v2' -and
        ($plan.samples -join ',') -ceq '1,2,3' -and $plan.bootstrapRevision -ceq $second -and
        $plan.mainRevision -ceq $second -and $null -eq $plan.historySnapshot -and $plan.collect) `
        'Trusted automatic plan did not retain explicit bootstrap, approved policy, actual main and exactly three executions.'
    if ($PlanOnly) { Write-Host "PASS: $checks automatic baseline plan checks."; return }
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'durable-plan') -RichScale `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2 -BootstrapRevision $second -PublishPlan
    $bootstrap = (& git --git-dir=$remote show 'refs/heads/ci-health-data:timing-bootstrap/ci-timing-v2.json') -join "`n" |
        ConvertFrom-Json -AsHashtable
    Assert ($bootstrap.revision -ceq $second -and $bootstrap.policyVersion -ceq 'ci-timing-v2') `
        'Explicit first bootstrap was not durable before collection/promotion.'
    'Third product revision' | Set-Content (Join-Path $source 'product.txt')
    $null = Invoke-FixtureGit $source @('add', 'product.txt')
    $null = Invoke-FixtureGit $source @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '-m', 'Main advances during collection')
    $third = Invoke-FixtureGit $source @('rev-parse', 'HEAD')
    $null = Invoke-FixtureGit $source @('push', '--quiet', 'origin', 'main')
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 100 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'advanced-main-plan') -RichScale `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $advanced = Get-Content (Join-Path $fixture 'advanced-main-plan\plan.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($advanced.bootstrapRevision -ceq $second -and $advanced.mainRevision -ceq $third -and $advanced.collect) `
        'Advancing main moved the once-pinned bootstrap or starved the previously planned candidate.'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 101 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'moving-bootstrap') -RichScale `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2 -BootstrapRevision $third
    } 'A new main tip replaced the durable bootstrap.'
    $ci.head_sha = 'f' * 40
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $ci.head_sha -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 102 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'unknown-main') -RichScale `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    } 'Advertised CI provenance replaced actual main membership.'
    $ci.head_sha = $second
    $emptyRemote = Join-Path $fixture 'empty.git'
    & git init --bare --quiet $emptyRemote
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 103 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'missing-source') -RichScale `
            -AllowLocalFixture -HistoryRemote $emptyRemote -ApprovedPolicyVersion ci-timing-v2 -BootstrapRevision $second
    } 'A failed actual-main fetch became empty-history bootstrap success.'
    for ($number = 1; $number -le 3; $number++) {
        New-CompleteBaselineSample $number $second @(148, 150, 152)[$number - 1] @(495, 500, 505)[$number - 1]
    }
    $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
    $candidateWork = Join-Path $fixture 'first-reference'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $second -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $candidateWork -RichScale -Publish `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $candidate = Get-Content (Join-Path $candidateWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($candidate.validation -ceq 'success' -and $candidate.eligibility -ceq 'eligible' -and
        $candidate.timingHealth -ceq 'unbaselined' -and $null -eq $candidate.reference -and
        $candidate.samples.Count -eq 3 -and $candidate.samples[0].rich.prefix.notes.'normal-executable'.localDurability.samplesMs.Count -eq 21) `
        'First complete approved series did not bootstrap unbaselined with full raw evidence.'
    $referencePaths = @(& git --git-dir=$remote ls-tree -r --name-only refs/heads/ci-health-data |
        Where-Object { $_.StartsWith('timing-references/') })
    Assert ($referencePaths.Count -eq 1) 'An eligible complete first series did not publish an immutable canonical reference.'
    Write-Host "PASS: $checks public automatic baseline checks."
    $global:LASTEXITCODE = 0
} finally {
    Remove-Variable BaselineFixture -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
