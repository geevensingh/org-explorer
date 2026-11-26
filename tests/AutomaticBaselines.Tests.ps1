param([switch]$PlanOnly, [switch]$SmokeOnly, [string]$GateSnapshotDirectory,
    [string]$EventVersionMetadata, [string]$EventVersionCase,
    [ValidateSet('ci-timing-v2', 'ci-timing-v3')][string]$GatePolicyVersion = 'ci-timing-v2')
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
        if ($global:BaselineFixture.ContainsKey('downloadHook')) {
            $hook = $global:BaselineFixture.downloadHook
            $global:BaselineFixture.Remove('downloadHook')
            & $hook
        }
        $name = $args[[Array]::IndexOf($args, '--name') + 1]
        $destination = $args[[Array]::IndexOf($args, '--dir') + 1]
        Copy-Item -LiteralPath $global:BaselineFixture.evidence[$name] -Destination $destination -Recurse
        return
    }
    $endpoint = $args[1]
    $ciId = $global:BaselineFixture.ci.id
    $cohortRun = $global:BaselineFixture.cohortRun
    switch -Regex ($endpoint) {
        '/git/trees/(?<revision>[0-9a-f]{40})\?recursive=1$' {
            $entry = & git "--git-dir=$($global:BaselineFixture.sourceRemote)" ls-tree $Matches.revision -- eng/timing-policy.json
            if ($LASTEXITCODE -ne 0) { throw 'Fixture source tree unavailable.' }
            $tree = @()
            if ($entry -match '^100644 blob (?<blob>[0-9a-f]{40})') {
                $tree = @(@{ path = 'eng/timing-policy.json'; type = 'blob'; sha = $Matches.blob })
            }
            @{ truncated = $false; tree = $tree } | ConvertTo-Json -Depth 10; return
        }
        '/git/blobs/(?<blob>[0-9a-f]{40})$' {
            $text = (& git "--git-dir=$($global:BaselineFixture.sourceRemote)" cat-file blob $Matches.blob) -join "`n"
            if ($LASTEXITCODE -ne 0) { throw 'Fixture policy blob unavailable.' }
            @{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)) } |
                ConvertTo-Json; return
        }
        "/actions/runs/$ciId/attempts/1$" { $global:BaselineFixture.ci | ConvertTo-Json -Depth 10; return }
        "/actions/runs/$ciId(/attempts/1)?/jobs\?per_page=100$" {
            ConvertTo-Json -InputObject @(@{ jobs = $global:BaselineFixture.ciJobs }) -Depth 10; return
        }
        "/actions/runs/$ciId/artifacts\?per_page=100$" {
            ConvertTo-Json -InputObject @(@{ artifacts = @() }) -Depth 10; return
        }
        "/actions/runs/$cohortRun/attempts/1$" {
            @{ id = $cohortRun; run_attempt = 1; head_sha = $global:BaselineFixture.controller
                event = 'workflow_run'; path = '.github/workflows/non-ui-cohorts.yml'; status = $global:BaselineFixture.status
                conclusion = $global:BaselineFixture.outcome
                head_repository = @{ full_name = 'geevensingh/org-explorer' } } | ConvertTo-Json -Depth 10
            return
        }
        "/actions/runs/$cohortRun/attempts/1/jobs\?per_page=100$" {
            ConvertTo-Json -InputObject @(@{ jobs = $global:BaselineFixture.jobs }) -Depth 10; return
        }
        "/actions/runs/$cohortRun/artifacts\?per_page=100$" {
            ConvertTo-Json -InputObject @(@{ artifacts = $global:BaselineFixture.artifacts }) -Depth 10; return
        }
        default { throw "Unexpected external GitHub operation: $($args -join ' ')" }
    }
}
function Add-MainFixtureRevision([string]$Text) {
    $Text | Set-Content (Join-Path $source 'product.txt')
    $null = Invoke-FixtureGit $source @('add', 'product.txt')
    $null = Invoke-FixtureGit $source @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '-m', $Text)
    $null = Invoke-FixtureGit $source @('push', '--quiet', 'origin', 'main')
    return Invoke-FixtureGit $source @('rev-parse', 'HEAD')
}
function Select-BaselineFixture([string]$Revision, [int]$CiId, [int]$CohortRun) {
    $global:BaselineFixture.ci.head_sha = $Revision; $global:BaselineFixture.ci.id = $CiId
    $global:BaselineFixture.cohortRun = $CohortRun; $global:BaselineFixture.controller = $Revision
    $global:BaselineFixture.status = 'in_progress'; $global:BaselineFixture.outcome = $null
    $global:BaselineFixture.jobs = @(); $global:BaselineFixture.artifacts = @()
}
function New-CompleteBaselineSample([int]$Number, [string]$Revision, [double]$Seconds, [double]$Latency,
    [string]$ImageVersion = '20261116', [string]$TimingPolicyVersion = 'ci-timing-v2') {
    . "$PSScriptRoot\UiScaleUnionFixture.ps1"
    . "$root\eng\NonUiCohort.ps1"
    $runId = [string]$global:BaselineFixture.cohortRun
    $directory = Join-Path $fixture "evidence-$Revision-$runId-$Number"
    $nonUi = Join-Path $directory 'non-ui'
    $invocation = [guid]::NewGuid().ToString('N')
    $runName = "run-$invocation"
    $cohortId = "$Revision-$runId-1"
    New-Item -ItemType Directory -Path (Join-Path $nonUi $runName) -Force | Out-Null
    $report = @{
        schemaVersion = 1; suite = 'non-ui-performance'; revision = $Revision; expectedRevision = $Revision
        cohortId = $cohortId; sample = $Number; invocationId = $invocation; runId = $runId; runAttempt = '1'
        filter = 'Category=Performance'; requiredTests = @($PerformanceTests)
        outcome = 'success'; observedOutcome = 'success'; diagnosticOnly = $false; phase = 'finished'
        trackedChanges = $false; untrackedChanges = $false; ceilingSeconds = 660; observationSeconds = 660
        executionSeconds = $Seconds; runDirectory = $runName
        startedAt = '2026-11-25T10:00:00Z'; completedAt = ([DateTimeOffset]'2026-11-25T10:00:00Z').AddSeconds($Seconds).ToString('O')
        steps = @(@{ name = 'restore'; seconds = 10; exitCode = 0 }, @{ name = 'test'; seconds = 100; exitCode = 0 })
        configuration = @{ workload = 'OrgExplorer.Persistence.Tests'; testTree = 'b' * 40; fixtureTree = 'c' * 40
            sdk = '10.0.100'; runtimes = @('Microsoft.NETCore.App 10.0.0'); os = 'Windows'; architecture = 'X64'
            processors = 4; image = 'win25'; imageVersion = $ImageVersion; powershell = '7.6'
            setup = 'locked-restore;isolated-build;setup-and-cleanup-included' }
    }
    $report | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $nonUi 'result.json')
    Copy-Item -LiteralPath (Join-Path $nonUi 'result.json') -Destination (Join-Path $nonUi "$runName\result.json")
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
    $rich = New-RichCohortSampleFixture (Join-Path $directory 'rich') $Revision $Number $runId '1' $TimingPolicyVersion
    foreach ($worker in @('prefix', 'tail')) {
        $path = Join-Path $rich[$worker] 'result.json'
        $data = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        $data.policyVersion = $TimingPolicyVersion
        $data.configuration.imageVersion = $ImageVersion
        if ($worker -ceq 'prefix') { $data.ceilingSeconds = 900; $data.observationSeconds = 900 }
        $data.executionSeconds = $Seconds
        $data.startedAt = $report.startedAt; $data.completedAt = $report.completedAt
        $data | ConvertTo-Json -Depth 15 | Set-Content $path
        $data | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $rich[$worker] "$($data.runDirectory)\result.json")
        $profilePath = Join-Path $rich[$worker] "$($data.runDirectory)\scale-mutation-$(if ($worker -ceq 'prefix') { 'save-prefix' } else { 'delivery-tail' })-ui-sqlite-rich-profile-latency-$(if ($worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }).json"
        $rawProfile = Get-Content $profilePath -Raw | ConvertFrom-Json -AsHashtable
        $rawProfile.samplesMs = @($Latency) * 21; $rawProfile.p95Ms = $Latency
        if ($TimingPolicyVersion -ceq 'ci-timing-v3') { $rawProfile.goalMet = $Latency -le 1000 }
        $rawProfile | ConvertTo-Json -Depth 15 | Set-Content $profilePath
        if ($worker -ceq 'prefix') {
            foreach ($phase in @('normal-executable', 'background-export')) {
                $notePath = Join-Path $rich.prefix "$($data.runDirectory)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
                $note = Get-Content $notePath -Raw | ConvertFrom-Json -AsHashtable
                $note.localDurability.samplesMs = @($Latency) * 21; $note.localDurability.p95Ms = $Latency
                $note.localDurability.budgetMs = 1500; $note.localDurability.policyVersion = $TimingPolicyVersion
                $note.localDurability.temporaryCeiling = $false
                $note.localDurability.goalMet = $Latency -le 1000
                $note | ConvertTo-Json -Depth 15 | Set-Content $notePath
            }
        }
    }
    & "$root\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $rich.prefix -TailDirectory $rich.tail `
        -ExpectedRevision $Revision -RunId $runId -RunAttempt 1 -ResultsDirectory (Join-Path $directory 'active-union') `
        -CohortId $cohortId -Sample $Number -ExpectedPolicyVersion $TimingPolicyVersion
    $rich.union = Join-Path $directory 'active-union'
    foreach ($worker in @('non-ui', 'prefix', 'tail', 'union')) {
        $name = if ($worker -ceq 'non-ui') { "non-ui-cohort-$cohortId-sample-$Number" }
            else { "rich-cohort-$cohortId-sample-$Number-$worker" }
        $global:BaselineFixture.evidence[$name] = if ($worker -ceq 'non-ui') { $nonUi } else { $rich[$worker] }
        $id = [int]$runId * 100 + 10 * $Number + [array]::IndexOf(@('non-ui', 'prefix', 'tail', 'union'), $worker)
        $global:BaselineFixture.jobs += @{ id = $id; runner_id = $id + 1000; status = 'completed'; conclusion = 'success'
            name = if ($worker -ceq 'non-ui') { "Non-UI cohort sample ($Number)" } else { "Rich cohort $worker ($Number)" } }
        $global:BaselineFixture.artifacts += @{ id = $id + 2000; name = $name; expired = $false
            expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('d' * 64)
            workflow_run = @{ id = [long]$runId; head_sha = $global:BaselineFixture.controller } }
    }
}
try {
    if ($EventVersionMetadata) {
        . "$root\eng\LaneHealth.ps1"
        . "$root\eng\TimingPolicy.ps1"
        $metadata = Get-Content -LiteralPath $EventVersionMetadata -Raw | ConvertFrom-Json -AsHashtable
        if ($metadata.kind -cne 'isolated-non-production-public-contract-fixture' -or $metadata.hostedMeasurement) {
            throw 'Version refusal requires an explicitly isolated publisher-produced contract fixture.'
        }
        $types = Get-LaneHealthRecordTypes
        $eventRemote = if ($metadata.ContainsKey('eventHistoryRemote')) { $metadata.eventHistoryRemote }
            else { $metadata.rejectedHistoryRemote }
        $original = Open-HealthHistory (Join-Path $fixture 'original.git') $eventRemote `
            -AllowLocalFixture -RecordTypes $types
        $known = Get-TimingCanonicalReference $original $metadata.seriesKey $metadata.sourceB
        Assert ($known.revision -ceq $metadata.sourceA -and $known.eligibility -ceq 'eligible') `
            'The current producer fixture does not expose its valid non-self canonical predecessor.'
        $bundle = $metadata.sampleDirectories[0]
        $execution = New-TimingExecutionRecord (Join-Path $bundle 'non-ui') (Join-Path $bundle 'rich\prefix') `
            (Join-Path $bundle 'rich\tail') $metadata.sourceB '102' '1' -ExpectedPolicyVersion $metadata.policyVersion
        $eventTypes = @('timing-bootstrap', 'timing-plan', 'timing-decision', 'timing-reference')
        if ($metadata.ContainsKey('eventHistoryRemote')) { $eventTypes += 'cohort-observation' }
        foreach ($type in $eventTypes) {
            $events = @($original.records | Where-Object { $_.recordType -ceq $type })
            $event = if ($type -ceq 'timing-plan') {
                @($events | Where-Object { $_.revision -ceq $metadata.sourceB })[0]
            } else { $events[0] }
            $path = Get-HealthRecordPath $event $types
            foreach ($variant in @(
                @{ name = 'unsupported'; value = 999 },
                @{ name = 'missing' },
                @{ name = 'string'; value = [string]$event.schemaVersion },
                @{ name = 'null'; value = $null },
                @{ name = 'floating'; value = [double]$event.schemaVersion },
                @{ name = 'boolean'; value = $true }
            )) {
                $case = "$type-$($variant.name)"
                if ($EventVersionCase -and $EventVersionCase -cne $case) { continue }
                $remote = Join-Path $fixture "$case.git"
                Copy-Item -LiteralPath $eventRemote -Destination $remote -Recurse
                $tip = Invoke-HealthGit $remote @('rev-parse', 'refs/heads/ci-health-data')
                $record = (Invoke-HealthGit $remote @('show', "${tip}:$path")) |
                    ConvertFrom-Json -AsHashtable -DateKind String -Depth 30
                if ($variant.name -ceq 'missing') { $record.Remove('schemaVersion') }
                else { $record.schemaVersion = $variant.value }
                $inputPath = Join-Path $fixture "$case.json"
                $record | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $inputPath
                $null = Invoke-HealthGit $remote @('read-tree', $tip)
                $blob = Invoke-HealthGit $remote @('hash-object', '-w', $inputPath)
                $null = Invoke-HealthGit $remote @('update-index', '--add', '--cacheinfo', "100644,$blob,$path")
                $tree = Invoke-HealthGit $remote @('write-tree')
                $commit = Invoke-HealthGit $remote @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                    'commit-tree', $tree, '-p', $tip, '-m', "Mutate only $case event schema")
                $null = Invoke-HealthGit $remote @('update-ref', 'refs/heads/ci-health-data', $commit, $tip)
                $reason = $null
                $pointer = $null
                try {
                    $replay = Open-HealthHistory (Join-Path $fixture "$case-reader.git") $remote -AllowLocalFixture -RecordTypes $types
                    $pointer = Get-TimingCanonicalReference $replay $metadata.seriesKey $metadata.sourceB
                } catch { $reason = $_.Exception.Message }
                Assert ($null -eq $pointer -and $reason -match '(?i)schema|version') `
                    "Unknown $case protocol replay obtained a canonical pointer or failed for an unrelated reason: $reason"
                $publication = @{} + $original
                $publication.remote = $remote
                if ($type -ceq 'cohort-observation') { $publication.observationWriterAuthorized = $true }
                $publicationReason = $null
                try { $null = Publish-HealthRecord $publication $record }
                catch { $publicationReason = $_.Exception.Message }
                Assert ($publicationReason -match '(?i)schema|version') `
                    "Unknown $case version reached immutable publication without explicit protocol refusal: $publicationReason"
                $consumer = $execution | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable -DateKind String -Depth 30
                $consumerReason = $null
                try {
                    $consumerHistory = Open-HealthHistory (Join-Path $fixture "$case-consumer.git") $remote -AllowLocalFixture -RecordTypes $types
                    Set-TimingExecutionComparison $consumer $consumerHistory
                } catch { $consumerReason = $_.Exception.Message }
                Assert (!$consumer.Contains('timingEvaluation') -and $consumerReason -match '(?i)schema|version') `
                    "Unknown $case protocol yielded an enforced consumer comparison instead of refusal: $consumerReason"
            }
        }
        Assert ($checks -gt 1) 'Requested event version case did not run any protocol mutation.'
        Write-Host "PASS: $checks public publisher-event version refusal checks."
        $global:LASTEXITCODE = 0
        return
    }
    $remote = Join-Path $fixture 'remote with spaces.git'
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
    $ciJobId = 100
    $ciJobs = @(@('Core and view models', 'Real filesystem persistence', 'Actual Windows desktop smoke (people)',
        'Actual Windows desktop smoke (workspace)', 'High-resolution Windows smoke (reading)',
        'High-resolution Windows smoke (context)', 'CI') | ForEach-Object {
        $ciJobId++
        @{ id = $ciJobId; name = $_; status = 'completed'; conclusion = 'success'; steps = @()
            started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:03:00Z' }
    })
    $global:BaselineFixture = @{ ci = $ci; ciJobs = $ciJobs; controller = $second; cohortRun = 99; status = 'in_progress'
        outcome = $null; jobs = @(); artifacts = @(); evidence = @{}; sourceRemote = $remote }
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
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2 -InitializeBootstrap -PublishPlan
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
    $requestedJobs = $global:BaselineFixture.jobs
    foreach ($number in @(4, 5)) {
        foreach ($worker in @('prefix', 'tail', 'union')) {
            $global:BaselineFixture.jobs += @{ id = 50000 + $number * 10 + [array]::IndexOf(@('prefix', 'tail', 'union'), $worker)
                name = "rich-sample-$number / Rich cohort $worker ($number)"
                status = 'completed'; conclusion = 'skipped'; runner_id = 0 }
        }
    }
    $conditionalWork = Join-Path $fixture 'unrequested-conditional-skips'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $second -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $conditionalWork -RichScale `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $conditional = Get-Content (Join-Path $conditionalWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($conditional.samples.Count -eq 3 -and !$conditional.unexpectedExecutions -and
        $conditional.validation -ceq 'success' -and $conditional.eligibility -ceq 'eligible') `
        'Unrequested conditional fourth/fifth jobs were counted as attempted executions or required skips.'
    $global:BaselineFixture.jobs = $requestedJobs
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
    Assert ((Get-Content (Join-Path $candidateWork 'summary.md') -Raw).Contains('eligible reference event published') -and
        !(Get-Content (Join-Path $candidateWork 'summary.md') -Raw).Contains('Active-reference promotion: **disabled**')) `
        'Successful automatic promotion still reported observation-only disabled policy.'
    if ($GateSnapshotDirectory) {
        if (Test-Path -LiteralPath $GateSnapshotDirectory) { throw 'Fixture export must not overwrite an existing directory.' }
        if ($GatePolicyVersion -ceq 'ci-timing-v3') {
            '{"version":"ci-timing-v3"}' | Set-Content (Join-Path $source 'eng\timing-policy.json')
            $null = Invoke-FixtureGit $source @('add', 'eng/timing-policy.json')
            $second = Add-MainFixtureRevision 'Approved native-profile v3 starts a separate comparison series'
            Select-BaselineFixture $second 42 98
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 98 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'v3-plan') -RichScale -PublishPlan `
                -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v3 -InitializeBootstrap
            for ($number = 1; $number -le 3; $number++) {
                New-CompleteBaselineSample $number $second @(148, 150, 152)[$number - 1] @(495, 500, 505)[$number - 1] -TimingPolicyVersion ci-timing-v3
            }
            $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
            $v3Work = Join-Path $fixture 'v3-first-reference'
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $second -CiRunId 42 -CiRunAttempt 1 `
                -CohortRunId 98 -CohortRunAttempt 1 -WorkingDirectory $v3Work -RichScale -Publish `
                -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v3
            $v3 = Get-Content (Join-Path $v3Work 'record.json') -Raw | ConvertFrom-Json -AsHashtable
            Assert ($v3.eligibility -ceq 'eligible' -and $v3.timingEvaluation.version -ceq 'ci-timing-v3' -and
                $null -eq $v3.reference -and $v3.metrics.'profile-p95-ms'.median -eq 500 -and
                $v3.samples[0].rich.prefix.profile.budgetMs -eq 1500 -and
                $v3.samples[0].measurements.profile.budgetMs -eq 1000) `
                'Public v3 promotion mixed policies or weakened the non-UI profile limit.'
        }
        $gateRevision = Add-MainFixtureRevision 'Serious relative fixture without an absolute violation'
        Select-BaselineFixture $gateRevision 45 102
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $gateRevision -CiRunId 45 -CiRunAttempt 1 `
            -CohortRunId 102 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'gate-plan') -RichScale -PublishPlan `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion $GatePolicyVersion
        for ($number = 1; $number -le 3; $number++) {
            New-CompleteBaselineSample $number $gateRevision @(200, 202, 204)[$number - 1] @(600, 602, 604)[$number - 1] -TimingPolicyVersion $GatePolicyVersion
        }
        $gateDecisionWork = Join-Path $fixture 'gate-decision'
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $gateRevision -CiRunId 45 -CiRunAttempt 1 `
            -CohortRunId 102 -CohortRunAttempt 1 -WorkingDirectory $gateDecisionWork -RichScale -PublishDecision `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion $GatePolicyVersion
        $gateDecision = Get-Content (Join-Path $gateDecisionWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($gateDecision.eligibility -ceq 'extend' -and $gateDecision.reference.revision -ceq $second -and
            ($gateDecision.extraSamples -join ',') -ceq '4,5') 'Serious fixture did not preserve a nonblocking three-run decision before bounded extension.'
        New-Item -ItemType Directory -Path $GateSnapshotDirectory -Force | Out-Null
        $runningRemote = Join-Path $GateSnapshotDirectory 'collecting.git'
        Copy-Item -LiteralPath $remote -Destination $runningRemote -Recurse
        foreach ($number in @(4, 5)) { New-CompleteBaselineSample $number $gateRevision 202 602 -TimingPolicyVersion $GatePolicyVersion }
        $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
        $gateWork = Join-Path $fixture 'gate-rejected'
        Rejects {
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $gateRevision -CiRunId 45 -CiRunAttempt 1 `
                -CohortRunId 102 -CohortRunAttempt 1 -WorkingDirectory $gateWork -RichScale -Publish `
                -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion $GatePolicyVersion
        } 'Five stable practically significant separated values did not block.'
        $gateRecord = Get-Content (Join-Path $gateWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($gateRecord.samples.Count -eq 5 -and $gateRecord.validation -ceq 'success' -and
            $gateRecord.eligibility -ceq 'rejected' -and $gateRecord.timingHealth -ceq 'red' -and
            $gateRecord.timingEvaluation.metrics.executionSeconds.seriousRegression -and
            $gateRecord.timingEvaluation.metrics.'profile-p95-ms'.seriousRegression -and
            $gateRecord.reference.revision -ceq $second -and $gateRecord.extraSamples.Count -eq 0) `
            'Serious five-run fixture changed strict acceptance, lost practical evidence or promoted the rejected candidate.'
        $after = @(& git --git-dir=$remote ls-tree -r --name-only refs/heads/ci-health-data |
            Where-Object { $_.StartsWith('timing-references/') })
        Assert ($after.Count -eq $(if ($GatePolicyVersion -ceq 'ci-timing-v3') { 2 } else { 1 })) 'Serious relative rejection replaced the prior eligible reference.'
        $rejectedRemote = Join-Path $GateSnapshotDirectory 'rejected.git'
        Copy-Item -LiteralPath $remote -Destination $rejectedRemote -Recurse
        $eventRemote = Join-Path $GateSnapshotDirectory 'events.git'
        Copy-Item -LiteralPath $remote -Destination $eventRemote -Recurse
        Rejects {
            & "$root\eng\Invoke-NonUiCohortObservation.ps1" -RunId 102 -RunAttempt 1 `
                -WorkingDirectory (Join-Path $fixture 'event-unavailable-plan') `
                -HistoryRemote $eventRemote -AllowLocalFixture -ApprovedPolicyVersion $GatePolicyVersion
        } 'Unavailable plan fabricated qualified collection evidence instead of retaining an ineligible event.'
        $samplePaths = @()
        for ($number = 1; $number -le 5; $number++) {
            $path = Join-Path $GateSnapshotDirectory "sample-$number"
            Copy-Item -LiteralPath (Join-Path $fixture "evidence-$gateRevision-102-$number") -Destination $path -Recurse
            $samplePaths += $path
        }
        @{
            schemaVersion = 1; kind = 'isolated-non-production-public-contract-fixture'; hostedMeasurement = $false
            sourceA = $second; sourceB = $gateRevision; policyVersion = $GatePolicyVersion
            priorCohortId = "$second-$(if ($GatePolicyVersion -ceq 'ci-timing-v3') { '98' } else { '99' })-1"; currentCohortId = "$gateRevision-102-1"
            seriesKey = $gateRecord.samples[0].rich.seriesKey
            runningHistoryRemote = $runningRemote; rejectedHistoryRemote = $rejectedRemote
            eventHistoryRemote = $eventRemote; sampleDirectories = $samplePaths
        } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $GateSnapshotDirectory 'metadata.json')
        Copy-Item -LiteralPath (Join-Path $gateDecisionWork 'record.json') -Destination (Join-Path $GateSnapshotDirectory 'decision.json')
        Copy-Item -LiteralPath (Join-Path $gateWork 'record.json') -Destination (Join-Path $GateSnapshotDirectory 'rejected.json')
        Write-Host "PASS: public eligible-three/running-extension/serious-five snapshots: $GateSnapshotDirectory"
        $global:LASTEXITCODE = 0
        return
    }
    if ($SmokeOnly) {
        Write-Host "PASS: $checks public automatic baseline smoke checks."
        $global:LASTEXITCODE = 0
        return
    }
    $ci.id = 43; $ci.head_sha = $third
    $global:BaselineFixture.cohortRun = 100; $global:BaselineFixture.controller = $third
    $global:BaselineFixture.jobs = @(); $global:BaselineFixture.artifacts = @()
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $third -CiRunId 43 -CiRunAttempt 1 `
        -CohortRunId 100 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'next-plan') -RichScale -PublishPlan `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    for ($number = 1; $number -le 3; $number++) {
        New-CompleteBaselineSample $number $third @(149, 150, 151)[$number - 1] @(496, 500, 504)[$number - 1]
    }
    $nextWork = Join-Path $fixture 'next-reference'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $third -CiRunId 43 -CiRunAttempt 1 `
        -CohortRunId 100 -CohortRunAttempt 1 -WorkingDirectory $nextWork -RichScale -Publish `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $next = Get-Content (Join-Path $nextWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($next.eligibility -ceq 'eligible' -and $next.reference.revision -ceq $second -and
        $next.extraSamples.Count -eq 0 -and $next.reference.medianSeconds -eq 150 -and $next.reference.deltaSeconds -eq 0) `
        'New application revision required identical code, self-baselined or failed automatic compatible three-run promotion.'
    $fourth = Add-MainFixtureRevision 'Otherwise complete warning candidate'
    Select-BaselineFixture $fourth 44 101
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $fourth -CiRunId 44 -CiRunAttempt 1 `
        -CohortRunId 101 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'extension-plan') -RichScale -PublishPlan `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    for ($number = 1; $number -le 3; $number++) {
        New-CompleteBaselineSample $number $fourth @(151, 151.5, 153)[$number - 1] @(501, 502, 503)[$number - 1]
    }
    $decisionWork = Join-Path $fixture 'extension-decision'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $fourth -CiRunId 44 -CiRunAttempt 1 `
        -CohortRunId 101 -CohortRunAttempt 1 -WorkingDirectory $decisionWork -RichScale -PublishDecision `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $decision = Get-Content (Join-Path $decisionWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($decision.eligibility -ceq 'extend' -and ($decision.extraSamples -join ',') -ceq '4,5' -and
        $decision.reference.revision -ceq $third) 'Otherwise complete warning did not preserve comparison before exactly two additional executions.'
    foreach ($number in @(4, 5)) { New-CompleteBaselineSample $number $fourth 152 502 }
    $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
    $fiveWork = Join-Path $fixture 'extended-reference'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $fourth -CiRunId 44 -CiRunAttempt 1 `
        -CohortRunId 101 -CohortRunAttempt 1 -WorkingDirectory $fiveWork -RichScale -Publish `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $five = Get-Content (Join-Path $fiveWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($five.samples.Count -eq 5 -and $five.eligibility -ceq 'eligible' -and $five.timingHealth -ceq 'yellow' -and
        $five.extraSamples.Count -eq 0 -and $five.historySnapshot -ceq $decision.historySnapshot -and
        $five.reference.revision -ceq $third -and $five.metrics.executionSeconds.median -eq 152) `
        'Stable five-run warning lost its pinned comparison, median, warning history or automatic eligible promotion.'
    $availableArtifacts = $global:BaselineFixture.artifacts
    $global:BaselineFixture.artifacts = @($availableArtifacts | Where-Object {
        $_.name -cne "rich-cohort-$fourth-101-1-sample-5-tail"
    })
    $missingWork = Join-Path $fixture 'missing-extended-tail'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $fourth -CiRunId 44 -CiRunAttempt 1 `
            -CohortRunId 101 -CohortRunAttempt 1 -WorkingDirectory $missingWork -RichScale `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    } 'Missing extended evidence became successful acceptance.'
    Assert (Test-Path (Join-Path $missingWork 'record.json')) `
        'Missing extended evidence aborted comparison instead of preserving all attempts and the pinned predecessor.'
    $missing = Get-Content (Join-Path $missingWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($missing.samples.Count -eq 5 -and $missing.reliability.success -eq 4 -and
        $missing.eligibility -ceq 'rejected' -and $missing.reference.revision -ceq $third -and
        $missing.extraSamples.Count -eq 0 -and $missing.samples[4].rich.tail.evidence.state -ceq 'missing') `
        'A missing fifth tail was removed, replaced, promoted or lost its initial pinned comparison.'
    $global:BaselineFixture.artifacts = @()
    $expiredWork = Join-Path $fixture 'unavailable-extended-evidence'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $fourth -CiRunId 44 -CiRunAttempt 1 `
            -CohortRunId 101 -CohortRunAttempt 1 -WorkingDirectory $expiredWork -RichScale `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    } 'Unavailable raw evidence became fresh complete acceptance.'
    Assert (Test-Path (Join-Path $expiredWork 'record.json')) `
        'Unavailable comparison keys erased the observed cohort instead of retaining its pinned metadata and all five outcomes.'
    $global:BaselineFixture.artifacts = $availableArtifacts
    $olderRevision = Add-MainFixtureRevision 'Older eligible cohort awaiting publication'
    Select-BaselineFixture $olderRevision 45 102
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $olderRevision -CiRunId 45 -CiRunAttempt 1 `
        -CohortRunId 102 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'older-plan') -RichScale -PublishPlan `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    for ($number = 1; $number -le 3; $number++) {
        New-CompleteBaselineSample $number $olderRevision @(151.8, 152, 152.2)[$number - 1] @(501.8, 502, 502.2)[$number - 1]
    }
    $olderContext = $global:BaselineFixture | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable
    $newerRevision = Add-MainFixtureRevision 'Newer eligible main cohort published first'
    Select-BaselineFixture $newerRevision 46 103
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $newerRevision -CiRunId 46 -CiRunAttempt 1 `
        -CohortRunId 103 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'newer-plan') -RichScale -PublishPlan `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    for ($number = 1; $number -le 3; $number++) {
        New-CompleteBaselineSample $number $newerRevision @(151.8, 152, 152.2)[$number - 1] @(501.8, 502, 502.2)[$number - 1]
    }
    $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
    $newerWork = Join-Path $fixture 'newer-published'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $newerRevision -CiRunId 46 -CiRunAttempt 1 `
        -CohortRunId 103 -CohortRunAttempt 1 -WorkingDirectory $newerWork -RichScale -Publish `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $newer = Get-Content (Join-Path $newerWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    $global:BaselineFixture = $olderContext
    $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
    $olderWork = Join-Path $fixture 'older-published-late'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $olderRevision -CiRunId 45 -CiRunAttempt 1 `
        -CohortRunId 102 -CohortRunAttempt 1 -WorkingDirectory $olderWork -RichScale -Publish `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $older = Get-Content (Join-Path $olderWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($older.eligibility -ceq 'eligible' -and $older.reference.revision -ceq $fourth -and
        (Get-Content (Join-Path $olderWork 'summary.md') -Raw).Contains('older or already eligible main revision')) `
        'Out-of-order eligible completion compared against a future candidate or overwrote a newer pointer.'
    . "$root\eng\NonUiCohort.ps1"
    . "$root\eng\TimingPolicy.ps1"
    . "$root\eng\LaneHealth.ps1"
    $orderedHistory = Open-HealthHistory (Join-Path $fixture 'ordered-reader.git') $remote -AllowLocalFixture `
        -RecordTypes (Get-LaneHealthRecordTypes)
    $currentReference = Get-TimingCanonicalReference $orderedHistory $newer.samples[0].rich.seriesKey
    $selfPredecessor = Get-TimingCanonicalReference $orderedHistory $newer.samples[0].rich.seriesKey $newerRevision
    Assert ($currentReference.revision -ceq $newerRevision -and $selfPredecessor.revision -ceq $fourth -and
        @($orderedHistory.records | Where-Object { $_.recordType -ceq 'performance-cohort' -and $_.revision -ceq $olderRevision }).Count -eq 1) `
        'Public reference reader lost the main-order pointer, non-self predecessor or immutable older comparison.'
    $conflictRevision = Add-MainFixtureRevision 'Concurrent immutable attempt publication'
    Select-BaselineFixture $conflictRevision 47 104
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $conflictRevision -CiRunId 47 -CiRunAttempt 1 `
        -CohortRunId 104 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'conflict-plan') -RichScale -PublishPlan `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    for ($number = 1; $number -le 3; $number++) { New-CompleteBaselineSample $number $conflictRevision 152 502 }
    $beforeConflict = (& git --git-dir=$remote rev-parse refs/heads/ci-health-data).Trim()
    $global:BaselineFixture.downloadHook = {
        $outer = $global:BaselineFixture
        $peer = @{} + $outer
        $peer.ci = @{} + $outer.ci; $peer.ci.id = 88
        $global:BaselineFixture = $peer
        try {
            & "$root\eng\Invoke-LaneHealth.ps1" -RunId 88 -RunAttempt 1 -WorkingDirectory (Join-Path $fixture 'concurrent-lane') `
                -HistoryRemote $remote -AllowLocalFixture -Publish
        } finally { $global:BaselineFixture = $outer }
    }
    $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
    $conflictWork = Join-Path $fixture 'conflict-published'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $conflictRevision -CiRunId 47 -CiRunAttempt 1 `
        -CohortRunId 104 -CohortRunAttempt 1 -WorkingDirectory $conflictWork -RichScale -Publish `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $conflict = Get-Content (Join-Path $conflictWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    $conflictHistory = Open-HealthHistory (Join-Path $fixture 'mixed-canonical-reader.git') $remote -AllowLocalFixture `
        -RecordTypes (Get-LaneHealthRecordTypes)
    Assert ($conflict.historySnapshot -ceq $beforeConflict -and $conflict.reference.revision -ceq $newerRevision -and
        @($conflictHistory.records | Where-Object { $_.recordType -ceq 'lane-health' -and $_.runId -ceq '88' }).Count -eq 9 -and
        @($conflictHistory.records | Where-Object { $_.recordType -ceq 'performance-cohort' -and $_.revision -ceq $conflictRevision }).Count -eq 1) `
        'Real nonforce conflict reconciliation discarded a simultaneous namespace or recalculated the pinned comparison.'
    $changedRevision = Add-MainFixtureRevision 'New environment series establishes automatically'
    Select-BaselineFixture $changedRevision 48 105
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $changedRevision -CiRunId 48 -CiRunAttempt 1 `
        -CohortRunId 105 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'changed-series-plan') -RichScale -PublishPlan `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    for ($number = 1; $number -le 3; $number++) {
        New-CompleteBaselineSample $number $changedRevision @(148, 150, 152)[$number - 1] @(495, 500, 505)[$number - 1] '20261117'
    }
    $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
    $changedWork = Join-Path $fixture 'changed-series'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $changedRevision -CiRunId 48 -CiRunAttempt 1 `
        -CohortRunId 105 -CohortRunAttempt 1 -WorkingDirectory $changedWork -RichScale -Publish `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $changed = Get-Content (Join-Path $changedWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($changed.eligibility -ceq 'eligible' -and $changed.timingHealth -ceq 'unbaselined' -and
        $null -eq $changed.reference -and $changed.samples[0].rich.seriesKey -cne $conflict.samples[0].rich.seriesKey) `
        'Changed environment required manual upkeep or compared incompatible series.'
    $failureRevision = Add-MainFixtureRevision 'Individual failures retain the prior reference'
    Select-BaselineFixture $failureRevision 49 106
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $failureRevision -CiRunId 49 -CiRunAttempt 1 `
        -CohortRunId 106 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'failure-plan') -RichScale -PublishPlan `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    for ($number = 1; $number -le 3; $number++) {
        New-CompleteBaselineSample $number $failureRevision @(149, 150, 151)[$number - 1] @(496, 500, 504)[$number - 1] '20261117'
    }
    $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
    $configurationPath = Join-Path $global:BaselineFixture.evidence["non-ui-cohort-$failureRevision-106-1-sample-1"] 'result.json'
    $completeConfiguration = Get-Content $configurationPath -Raw
    $unresolvedConfiguration = $completeConfiguration | ConvertFrom-Json -AsHashtable
    $unresolvedConfiguration.configuration.Remove('sdk')
    $unresolvedConfiguration | ConvertTo-Json -Depth 15 | Set-Content $configurationPath
    $configurationRun = $unresolvedConfiguration.runDirectory
    Copy-Item -LiteralPath $configurationPath -Destination (Join-Path (Split-Path $configurationPath -Parent) "$configurationRun\result.json")
    $unknownConfigurationWork = Join-Path $fixture 'unknown-runtime-configuration'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $failureRevision -CiRunId 49 -CiRunAttempt 1 `
        -CohortRunId 106 -CohortRunAttempt 1 -WorkingDirectory $unknownConfigurationWork -RichScale `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $unknownConfiguration = Get-Content (Join-Path $unknownConfigurationWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($unknownConfiguration.validation -ceq 'success' -and $unknownConfiguration.eligibility -ceq 'inconclusive' -and
        !$unknownConfiguration.timingEvaluation.blocking -and $unknownConfiguration.extraSamples.Count -eq 0) `
        'Unknown runtime configuration either fabricated eligible comparison/promotion or blocked complete strict product validation.'
    $completeConfiguration | Set-Content $configurationPath
    Copy-Item -LiteralPath $configurationPath -Destination (Join-Path (Split-Path $configurationPath -Parent) "$configurationRun\result.json")
    $failedJob = @($global:BaselineFixture.jobs | Where-Object { $_.name -ceq 'Non-UI cohort sample (2)' })[0]
    foreach ($outcome in @('failure', 'cancelled', 'skipped', 'timed_out')) {
        $failedJob.conclusion = $outcome
        $failureWork = Join-Path $fixture "individual-$outcome"
        Rejects {
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $failureRevision -CiRunId 49 -CiRunAttempt 1 `
                -CohortRunId 106 -CohortRunAttempt 1 -WorkingDirectory $failureWork -RichScale `
                -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
        } "An individual $outcome became current acceptance."
        $failed = Get-Content (Join-Path $failureWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($failed.samples.Count -eq 3 -and $failed.reliability.success -eq 2 -and
            $failed.samples[1].jobOutcome -ceq $outcome -and $failed.eligibility -ceq 'rejected' -and
            $failed.extraSamples.Count -eq 0 -and $failed.reference.revision -ceq $changedRevision) `
            "Original $outcome was dropped, replaced, promoted or lost its prior reference."
    }
    $failedJob.conclusion = 'success'
    $profileReport = Get-Content (Join-Path $global:BaselineFixture.evidence["non-ui-cohort-$failureRevision-106-1-sample-1"] 'result.json') -Raw |
        ConvertFrom-Json -AsHashtable
    $profileLog = Join-Path $global:BaselineFixture.evidence["non-ui-cohort-$failureRevision-106-1-sample-1"] "$($profileReport.runDirectory)\scale-measurements.log"
    $validProfileLog = Get-Content $profileLog -Raw
    foreach ($defect in @('hard', 'malformed-p95')) {
        $lines = @($validProfileLog -split "\r?\n" | Where-Object { $_ -and !$_.StartsWith('SCALE ORDINARY-PROFILE ') })
        $profileLine = @($validProfileLog -split "\r?\n" | Where-Object { $_.StartsWith('SCALE ORDINARY-PROFILE ') })[0]
        $rawProfile = $profileLine.Substring('SCALE ORDINARY-PROFILE '.Length) | ConvertFrom-Json -AsHashtable
        if ($defect -ceq 'hard') { $rawProfile.samplesMs = @(1000.1) * 21; $rawProfile.p95Ms = 1000.1 }
        else { $rawProfile.p95Ms = 999 }
        $lines += 'SCALE ORDINARY-PROFILE ' + ($rawProfile | ConvertTo-Json -Depth 10 -Compress)
        $lines | Set-Content $profileLog
        $defectWork = Join-Path $fixture $defect
        Rejects {
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $failureRevision -CiRunId 49 -CiRunAttempt 1 `
                -CohortRunId 106 -CohortRunAttempt 1 -WorkingDirectory $defectWork -RichScale `
                -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
        } 'An individual hard outcome or malformed raw percentile became accepting evidence.'
        $defective = Get-Content (Join-Path $defectWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($defective.eligibility -ceq 'rejected' -and $defective.reliability.success -eq 2 -and
            $defective.extraSamples.Count -eq 0 -and $defective.samples.Count -eq 3) `
            'A hard/malformed attempt was averaged away or replaced by extra samples.'
        if ($defect -ceq 'hard') {
            Assert ($defective.metrics.'profile-p95-ms'.median -eq 504 -and
                $defective.timingEvaluation.metrics.'profile-p95-ms'.health -ceq 'red') `
                'A passing cohort median concealed the original individual hard violation.'
        }
    }
    $validProfileLog | Set-Content $profileLog
    $hookPath = Join-Path $remote 'hooks\pre-receive'
    $beforeRefusal = (& git --git-dir=$remote rev-parse refs/heads/ci-health-data).Trim()
    "#!/bin/sh`nexit 1" | Set-Content -LiteralPath $hookPath -Encoding utf8NoBOM
    $refusedWork = Join-Path $fixture 'write-refused'
    try {
        Rejects {
            & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $failureRevision -CiRunId 49 -CiRunAttempt 1 `
                -CohortRunId 106 -CohortRunAttempt 1 -WorkingDirectory $refusedWork -RichScale -Publish `
                -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
        } 'Rejected Git publication was reported as durable promotion.'
        Assert ((Get-Content (Join-Path $refusedWork 'summary.md') -Raw).Contains('History publication: FAILED') -and
            (& git --git-dir=$remote rev-parse refs/heads/ci-health-data).Trim() -ceq $beforeRefusal) `
            'A failed nonforce write hid its error or changed the prior canonical state.'
    } finally { Remove-Item -LiteralPath $hookPath -Force }
    $unavailableWork = Join-Path $fixture 'missing-plan-observer'
    Rejects {
        & "$root\eng\Invoke-NonUiCohortObservation.ps1" -RunId 106 -RunAttempt 1 -WorkingDirectory $unavailableWork `
            -HistoryRemote $remote -AllowLocalFixture -ApprovedPolicyVersion ci-timing-v2
    } 'Missing plan inferred trusted candidate qualification.'
    $unavailable = (& git --git-dir=$remote show 'refs/heads/ci-health-data:observations/cohorts/106/1.json') -join "`n" |
        ConvertFrom-Json -AsHashtable
    Assert (!$unavailable.baselineEligible -and !$unavailable.comparisonInputEligible -and
        $null -eq $unavailable.revision -and $unavailable.jobs.Count -eq 12 -and
        $unavailable.collectionOutcome -ceq 'success') `
        'Unavailable plan lost original jobs/outcome, invented candidate identity or became a comparison input.'
    foreach ($case in @(
        @{ name = 'persistent-variance'; ci = 50; run = 107; image = '20261117'; durations = @(100, 150, 200); extra = 150; reference = $changedRevision },
        @{ name = 'bootstrap-low-headroom'; ci = 51; run = 108; image = '20261118'; durations = @(600, 630, 640); extra = 635; reference = $null }
    )) {
        $inconclusiveRevision = Add-MainFixtureRevision $case.name
        Select-BaselineFixture $inconclusiveRevision $case.ci $case.run
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $inconclusiveRevision -CiRunId $case.ci -CiRunAttempt 1 `
            -CohortRunId $case.run -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture "$($case.name)-plan") -RichScale -PublishPlan `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
        for ($number = 1; $number -le 3; $number++) {
            New-CompleteBaselineSample $number $inconclusiveRevision $case.durations[$number - 1] @(495, 500, 505)[$number - 1] $case.image
        }
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $inconclusiveRevision -CiRunId $case.ci -CiRunAttempt 1 `
            -CohortRunId $case.run -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture "$($case.name)-decision") -RichScale -PublishDecision `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
        foreach ($number in @(4, 5)) { New-CompleteBaselineSample $number $inconclusiveRevision $case.extra 500 $case.image }
        $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
        $inconclusiveWork = Join-Path $fixture $case.name
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $inconclusiveRevision -CiRunId $case.ci -CiRunAttempt 1 `
            -CohortRunId $case.run -CohortRunAttempt 1 -WorkingDirectory $inconclusiveWork -RichScale -Publish `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
        $inconclusive = Get-Content (Join-Path $inconclusiveWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($inconclusive.validation -ceq 'success' -and $inconclusive.samples.Count -eq 5 -and
            $inconclusive.eligibility -ceq 'inconclusive' -and $inconclusive.extraSamples.Count -eq 0 -and
            $(if ($case.reference) { $inconclusive.reference.revision -ceq $case.reference } else { $null -eq $inconclusive.reference })) `
            'Terminal five-execution uncertainty manufactured a reference or requested further repetitions.'
    }
    Select-BaselineFixture $first 52 109
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $first -CiRunId 52 -CiRunAttempt 1 `
        -CohortRunId 109 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'legacy-source-plan') -RichScale -PublishPlan `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    for ($number = 1; $number -le 3; $number++) { New-CompleteBaselineSample $number $first 150 500 }
    $global:BaselineFixture.status = 'completed'; $global:BaselineFixture.outcome = 'success'
    $legacyWork = Join-Path $fixture 'legacy-source-active-artifacts'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Summarize -Revision $first -CiRunId 52 -CiRunAttempt 1 `
            -CohortRunId 109 -CohortRunAttempt 1 -WorkingDirectory $legacyWork -RichScale -Publish `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    } 'Artifact-declared active policy rewrote an original unactivated source as canonical evidence.'
    $legacySource = Get-Content (Join-Path $legacyWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($legacySource.sourcePolicyVersion -ceq 'rich-scale-original-v1' -and !$legacySource.sourcePolicyCompatible -and
        $legacySource.eligibility -ceq 'rejected' -and $legacySource.samples.Count -eq 3 -and $legacySource.extraSamples.Count -eq 0) `
        'Mixed-source qualification accepted artifact approval or lost original legacy source policy.'
    $badRemote = Join-Path $fixture 'malformed-history.git'
    Copy-Item -LiteralPath $remote -Destination $badRemote -Recurse
    $badWriter = Join-Path $fixture 'malformed-writer'
    & git -c core.longpaths=true clone --quiet --branch ci-health-data $badRemote $badWriter
    if ($LASTEXITCODE -ne 0) { throw 'Malformed-history fixture clone failed before the intended validation case.' }
    $null = Invoke-FixtureGit $badWriter @('config', 'core.longpaths', 'true')
    $badPath = Join-Path $badWriter "rich-scale-cohorts\$changedRevision\105\1.json"
    $bad = Get-Content $badPath -Raw | ConvertFrom-Json -AsHashtable
    $bad.timingEvaluation.metrics.executionSeconds.median = 999
    $bad | ConvertTo-Json -Depth 30 | Set-Content $badPath
    $null = Invoke-FixtureGit $badWriter @('add', '.')
    $null = Invoke-FixtureGit $badWriter @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '-m', 'Malformed stored comparison fixture')
    $null = Invoke-FixtureGit $badWriter @('push', '--quiet', 'origin', 'ci-health-data')
    $malformedReason = $null
    try {
        $null = Open-HealthHistory (Join-Path $fixture 'bad-reader.git') $badRemote -AllowLocalFixture `
            -RecordTypes (Get-LaneHealthRecordTypes)
    } catch { $malformedReason = $_.Exception.Message }
    Assert ($malformedReason -like 'Stored timing health/eligibility does not reproduce*') `
        'Stored grade inputs were not rejected for the actual immutable-comparison mismatch.'
    Write-Host "PASS: $checks public automatic baseline checks."
    $global:LASTEXITCODE = 0
} finally {
    Remove-Variable BaselineFixture -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
