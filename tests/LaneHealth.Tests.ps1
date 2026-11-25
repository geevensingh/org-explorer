$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.test-workspaces\lh-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$remote = Join-Path $fixture 'history.git'
$evidence = Join-Path $fixture 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
git init --bare --quiet $remote
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
$run = @{
    id = 51; run_attempt = 1; head_sha = 'a' * 40; event = 'push'; head_branch = 'main'
    path = '.github/workflows/ci.yml'; status = 'completed'
    head_repository = @{ full_name = 'geevensingh/org-explorer' }
}
$jobs = @(
    @{ id = 101; name = 'Core and view models'; status = 'completed'; conclusion = 'success'
        started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:02:05Z'
        steps = @(@{ name = 'Setup .NET'; conclusion = 'success'; started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:00:05Z' }) }
)
$artifacts = @(@{
    id = 201; name = 'core-attempt-1'; expired = $false; expires_at = '2099-01-01T00:00:00Z'
    digest = 'sha256:' + ('b' * 64); workflow_run = @{ id = 51; head_sha = $run.head_sha }
})
$global:LaneHealthFixture = @{ run = $run; jobs = $jobs; artifacts = $artifacts; evidence = $evidence }
function gh {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if ($arguments[0] -ceq 'run' -and $arguments[1] -ceq 'download') {
        $destination = $arguments[[Array]::IndexOf($arguments, '--dir') + 1]
        $name = $arguments[[Array]::IndexOf($arguments, '--name') + 1]
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $global:LaneHealthFixture.evidence "$name\*") -Destination $destination -Recurse
        return
    }
    if ($arguments[0] -cne 'api') { throw 'Unexpected external operation.' }
    switch -Regex ($arguments[1]) {
        '/actions/runs/51/attempts/[1-9][0-9]*$' { $global:LaneHealthFixture.run | ConvertTo-Json -Depth 10 -Compress; return }
        '/actions/runs/51/attempts/[1-9][0-9]*/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:LaneHealthFixture.jobs }) -Depth 10 -Compress; return
        }
        '/actions/runs/51/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = $global:LaneHealthFixture.artifacts }) -Depth 10 -Compress; return
        }
        default { throw "Unexpected endpoint: $($arguments[1])" }
    }
}
function Observe([switch]$Publish) {
    $work = Join-Path $fixture ('o-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    & "$root\eng\Invoke-LaneHealth.ps1" -RunId 51 -RunAttempt $run.run_attempt -HistoryRemote $remote `
        -AllowLocalFixture -WorkingDirectory $work -Publish:$Publish
    return @{
        records = @(Get-Content (Join-Path $work 'records.json') -Raw | ConvertFrom-Json -AsHashtable)
        summary = Get-Content (Join-Path $work 'summary.md') -Raw
    }
}
try {
    $core = Join-Path $evidence 'core-attempt-1'
    New-Item -ItemType Directory -Path $core | Out-Null
    $companions = @('CiGate', 'CiRunner', 'FilesystemHealth', 'LaneHealth', 'MixedHealth', 'ImplementationBaseline',
        'DocumentationHandoff', 'DraftDiscard', 'Performance', 'DesktopResults', 'UiScale', 'Release',
        'Release.Gates', 'Release.Publication', 'Release.Renewal', 'Release.Workflow', 'Release.Payload',
        'Update.TrustLifetime', 'ApplicationIcon')
    $execution = @{
        schemaVersion = 1; lane = 'core'; revision = $run.head_sha; runId = '51'; runAttempt = '1'
        complete = $true; outcome = 'success'; executionSeconds = 120; ceilingSeconds = 300
        targetSeconds = 300; selector = 'all'; policyVersion = 'core-original-v1'
        startedAt = '2026-11-25T10:00:00Z'; finishedAt = '2026-11-25T10:02:00Z'
        configuration = @{
            workload = 'OrgExplorer.Core.Tests'; testTree = 'c' * 40; fixtureTree = 'd' * 40
            sdk = '10.0.100'; runtimes = @('Microsoft.NETCore.App 10.0.0')
            os = 'Windows'; architecture = 'X64'; processors = 4; image = 'win25'; imageVersion = '1'
            powershell = '7.6.10'; setup = 'locked-restore;isolated-build;setup-and-cleanup-included'
        }
        steps = @(@{ name = 'restore'; seconds = 10; status = 'Passed' },
            @{ name = 'test'; seconds = 60; status = 'Passed' }) +
            @($companions | ForEach-Object { @{ name = "$_.Tests.ps1"; seconds = 1; status = 'Passed' } })
    }
    $execution | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $core 'execution.json')
    '<TestRun><Results><UnitTestResult testName="Real.workflow" outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>' |
        Set-Content (Join-Path $core 'result.trx')
    $observed = Observe -Publish
    $publicationCount = [int](git --git-dir=$remote rev-list --count refs/heads/ci-health-data)
    Assert ($publicationCount -eq 1) 'One observed attempt was not published atomically as one immutable history append.'
    $saved = (git --git-dir=$remote show refs/heads/ci-health-data:lanes/51/1/core.json) -join "`n" | ConvertFrom-Json -AsHashtable
    Assert ($saved.validation -ceq 'success' -and $saved.measurements.executionSeconds -eq 120 -and
        $saved.measurements.headroomSeconds -eq 180 -and $saved.tests.total -eq 1 -and
        $saved.measurements.steps[2].name -ceq 'CiGate.Tests.ps1') 'Core lost complete runner/companion evidence.'
    Assert ($saved.timingHealth -ceq 'unbaselined' -and $null -eq $saved.reference -and
        $observed.summary.Contains('Core and view models') -and $observed.summary.Contains('observation only')) 'Core invented comparative health.'
    foreach ($lane in @('desktop', 'high-resolution')) {
        $partitions = Get-Content "$root\tests\$(if ($lane -ceq 'desktop') { 'Desktop' } else { 'HighResolution' })Partitions.json" -Raw |
            ConvertFrom-Json -AsHashtable
        foreach ($partition in $partitions.Keys) {
            $name = "$lane-$partition-attempt-1"
            $directory = Join-Path $evidence $name
            New-Item -ItemType Directory -Path $directory | Out-Null
            $steps = @('restore-app', 'restore-tests', 'build-app', 'display-prerequisite', 'desktop-flows')
            if ($partition -ceq 'workspace') { $steps += @('profile-note-refresh', 'update-yield', 'release-isolation') }
            @{ schemaVersion = 1; lane = $lane; partition = $partition; revision = $run.head_sha
                runId = '51'; runAttempt = '1'; complete = $true; ceilingSeconds = 900; executionSeconds = 100
                steps = @($steps | ForEach-Object { @{ name = $_; status = 'Passed'; seconds = 5 } })
            } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $directory 'execution.json')
            @{ schemaVersion = 1; scenarios = @($partitions[$partition] | ForEach-Object {
                @{ name = $_; status = 'Passed'; error = $null; seconds = 10
                    startedAt = '2026-11-25T10:00:00Z'; finishedAt = '2026-11-25T10:00:10Z' }
            }) } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $directory 'scenarios.json')
            $jobs += @{ id = $jobs.Count + 102; name = "$(if ($lane -ceq 'desktop') { 'Actual Windows desktop smoke' } else { 'High-resolution Windows smoke' }) ($partition)"
                status = 'completed'; conclusion = 'success'; started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:02:00Z'; steps = @() }
            $artifacts += @{ id = $artifacts.Count + 202; name = $name; expired = $false
                digest = 'sha256:' + ('b' * 64); expires_at = '2099-01-01T00:00:00Z'
                workflow_run = @{ id = 51; head_sha = $run.head_sha } }
        }
    }
    $global:LaneHealthFixture.jobs = $jobs
    $global:LaneHealthFixture.artifacts = $artifacts
    $native = Observe
    foreach ($lane in @('desktop-people', 'desktop-workspace', 'high-resolution-reading', 'high-resolution-context')) {
        $record = @($native.records | Where-Object { $_.lane -ceq $lane })[0]
        Assert ($record.validation -ceq 'success' -and $record.measurements.executionSeconds -eq 100 -and
            $record.measurements.headroomSeconds -eq 800) "$lane did not validate the real complete native selection."
    }
    $workspacePath = Join-Path $evidence 'desktop-workspace-attempt-1\execution.json'
    $workspace = Get-Content $workspacePath -Raw | ConvertFrom-Json -AsHashtable
    $workspace.steps = @($workspace.steps | Where-Object { $_.name -cne 'profile-note-refresh' })
    $workspace | ConvertTo-Json -Depth 10 | Set-Content $workspacePath
    $broken = Observe
    $brokenWorkspace = @($broken.records | Where-Object { $_.lane -ceq 'desktop-workspace' })[0]
    Assert ($brokenWorkspace.validation -ceq 'incomplete' -and $broken.summary.Contains('required command')) 'A green native job concealed a missing compiled companion.'
    . "$root\tests\UiScaleUnionFixture.ps1"
    $rich = New-UiScaleUnionFixture (Join-Path $fixture 'rich') $run.head_sha '51' '1'
    $normalPath = Join-Path $rich.prefix "$($rich.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-normal-executable.json"
    $normal = Get-Content $normalPath -Raw | ConvertFrom-Json -AsHashtable
    $normal.localDurability.samplesMs = @(400) * 19 + @(500, 700)
    $normal.localDurability.p95Ms = 500
    $normal | ConvertTo-Json -Depth 12 | Set-Content $normalPath
    $union = Join-Path $fixture 'union'
    & "$root\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $rich.prefix -TailDirectory $rich.tail `
        -ExpectedRevision $run.head_sha -RunId 51 -RunAttempt 1 -ResultsDirectory $union `
        -RequiredJobOutcomes @('success', 'success', 'success')
    $performance = Join-Path $fixture 'performance'
    $ownedRun = 'run-' + ('e' * 32)
    New-Item -ItemType Directory -Path (Join-Path $performance $ownedRun) -Force | Out-Null
    $performanceTest = 'OrgExplorer.Persistence.Tests.ScaleWorkflowTests.Ten_thousand_People_keep_complete_durable_current_and_frozen_context_through_mutations_and_delivery'
    "<TestRun><Results><UnitTestResult testName=`"$performanceTest`" outcome=`"Passed`" /></Results><ResultSummary outcome=`"Completed`"><Counters total=`"1`" executed=`"1`" passed=`"1`" failed=`"0`" notExecuted=`"0`" /></ResultSummary></TestRun>" |
        Set-Content (Join-Path $performance "$ownedRun\result.trx")
    $performanceReport = @{
        suite = 'non-ui-performance'; outcome = 'success'; observedOutcome = 'success'; phase = 'finished'; failure = $null
        revision = $run.head_sha; expectedRevision = $run.head_sha; runId = '51'; runAttempt = '1'
        trackedChanges = $false; untrackedChanges = $false; diagnosticOnly = $false; observationSeconds = 660
        ceilingSeconds = 660; executionSeconds = 120; runDirectory = $ownedRun
        startedAt = '2026-11-25T10:00:00Z'; completedAt = '2026-11-25T10:02:00Z'
        filter = 'Category=Performance'; requiredTests = @($performanceTest)
        steps = @(@{ name = 'restore'; seconds = 10; exitCode = 0 }, @{ name = 'test'; seconds = 100; exitCode = 0 })
    }
    $performanceReport | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $performance 'result.json')
    Copy-Item (Join-Path $performance 'result.json') (Join-Path $performance "$ownedRun\result.json")
    $run.path = '.github/workflows/scale-verification.yml'
    $global:LaneHealthFixture.jobs = @()
    $global:LaneHealthFixture.artifacts = @()
    foreach ($item in @(
        @('non-ui-performance', 'Thorough non-UI performance', $performance),
        @('ui-scale-prefix', 'Rich combined normal and held-export saves', $rich.prefix),
        @('ui-scale-tail', 'Rich logical Join, delivery, reopen and navigation', $rich.tail),
        @('ui-scale', 'Complete scale acceptance union', $union)
    )) {
        $name = "$($item[0])-$($run.head_sha)-51-1"
        Copy-Item -LiteralPath $item[2] -Destination (Join-Path $evidence $name) -Recurse
        $global:LaneHealthFixture.jobs += @{ id = 110 + $global:LaneHealthFixture.jobs.Count; name = $item[1]
            status = 'completed'; conclusion = 'success'; steps = @()
            started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:03:00Z' }
        $global:LaneHealthFixture.artifacts += @{ id = 210 + $global:LaneHealthFixture.artifacts.Count; name = $name
            expired = $false; digest = 'sha256:' + ('b' * 64); expires_at = '2099-01-01T00:00:00Z'
            workflow_run = @{ id = 51; head_sha = $run.head_sha } }
    }
    $scale = Observe
    foreach ($lane in @('non-ui-performance', 'ui-scale-prefix', 'ui-scale-tail', 'ui-scale')) {
        $record = @($scale.records | Where-Object { $_.lane -ceq $lane })[0]
        Assert ($record.validation -ceq 'success') "Complete $lane real validator evidence was lost."
    }
    $prefixRecord = @($scale.records | Where-Object { $_.lane -ceq 'ui-scale-prefix' })[0]
    $metric = @($prefixRecord.measurements.metrics | Where-Object { $_.name -ceq 'note-durability-normal-executable' })[0]
    Assert ($metric.value -eq 500 -and $metric.samples.Count -eq 21 -and $metric.samples[-1] -eq 700) 'Rich original p95/raw sample inputs were changed.'
    $downloadedNote = Join-Path $evidence "ui-scale-prefix-$($run.head_sha)-51-1\$($rich.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-normal-executable.json"
    $wrongAttemptNote = Get-Content $downloadedNote -Raw | ConvertFrom-Json -AsHashtable
    $wrongAttemptNote.runAttempt = '2'
    $wrongAttemptNote | ConvertTo-Json -Depth 12 | Set-Content $downloadedNote
    $staleMetric = Observe
    $stalePrefix = @($staleMetric.records | Where-Object { $_.lane -ceq 'ui-scale-prefix' })[0]
    Assert ($stalePrefix.validation -ceq 'incomplete') 'A rich metric from a different attempt was attributed to the current worker.'
    $wrongAttemptNote.runAttempt = '1'
    $wrongAttemptNote | ConvertTo-Json -Depth 12 | Set-Content $downloadedNote
    $prefixArtifact = Join-Path $evidence "ui-scale-prefix-$($run.head_sha)-51-1"
    $originalPrefixText = Get-Content (Join-Path $prefixArtifact 'result.json') -Raw
    $partialReport = $originalPrefixText | ConvertFrom-Json -AsHashtable
    $partialReport.outcome = 'timed_out'; $partialReport.observedOutcome = 'timed_out'
    $partialReport.failure = 'Shared execution budget exhausted'; $partialReport.executionSeconds = 661
    $partialReport.startedAt = [DateTimeOffset]::UtcNow.AddSeconds(-662).ToString('O')
    $partialReport.completedAt = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O')
    $partialReport | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $prefixArtifact 'result.json')
    Copy-Item (Join-Path $prefixArtifact 'result.json') (Join-Path $prefixArtifact "$($rich.runName)\result.json") -Force
    $partialBinding = Join-Path $prefixArtifact "$($rich.runName)\handoff\handoff.json"
    Remove-Item -LiteralPath $partialBinding
    $prefixJob = @($global:LaneHealthFixture.jobs | Where-Object { $_.name -ceq 'Rich combined normal and held-export saves' })[0]
    $prefixJob.conclusion = 'failure'
    $partial = Observe
    $partialPrefix = @($partial.records | Where-Object { $_.lane -ceq 'ui-scale-prefix' })[0]
    Assert ($partialPrefix.validation -ceq 'failure' -and $partialPrefix.measurements.executionSeconds -eq 661 -and
        @($partialPrefix.measurements.metrics | Where-Object { $_.name -ceq 'note-durability-normal-executable' })[0].value -eq 500 -and
        $partial.summary.Contains('handoff unavailable')) 'An interrupted prefix lost safe prior measurements or fabricated complete handoff proof.'
    $originalPrefixText | Set-Content (Join-Path $prefixArtifact 'result.json')
    Copy-Item (Join-Path $prefixArtifact 'result.json') (Join-Path $prefixArtifact "$($rich.runName)\result.json") -Force
    Copy-Item (Join-Path $rich.prefix "$($rich.runName)\handoff\handoff.json") $partialBinding
    $prefixJob.conclusion = 'success'
    Remove-Item (Join-Path $evidence "ui-scale-$($run.head_sha)-51-1\$((Get-Content (Join-Path $union 'result.json') -Raw | ConvertFrom-Json).runDirectory)\tail\$($rich.runName)\handoff-received.json")
    $missingHandoff = Observe
    $unionRecord = @($missingHandoff.records | Where-Object { $_.lane -ceq 'ui-scale' })[0]
    Assert ($unionRecord.validation -ceq 'incomplete') 'A green union artifact substituted for actual complete prefix-to-tail proof.'
    $run.path = '.github/workflows/installer-verification.yml'
    $run.event = 'workflow_dispatch'
    $installerName = 'isolated-installer-evidence-1'
    $installerDirectory = Join-Path $evidence $installerName
    New-Item -ItemType Directory -Path $installerDirectory | Out-Null
    @{ result = 'passed'; sourceCommit = $run.head_sha; runId = '51'; runAttempt = '1'; executionSeconds = 200
        os = @{ description = 'Windows Server'; windows11Client = $false; coverageLimit = 'Hosted Server is not Windows 11 client evidence.' }
        trustLimitations = 'Unsigned fixture installers; not publisher trust or production signing.'
    } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $installerDirectory 'result.json')
    $global:LaneHealthFixture.jobs = @(@{ id = 120; name = 'Isolated Windows installer lifecycle'; status = 'completed'; conclusion = 'success'
        started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:05:00Z'; steps = @() })
    $global:LaneHealthFixture.artifacts = @(@{ id = 220; name = $installerName; expired = $false
        expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('b' * 64); workflow_run = @{ id = 51; head_sha = $run.head_sha } })
    $installer = Observe
    Assert ($installer.records[0].validation -ceq 'success' -and $installer.records[0].measurements.executionSeconds -eq 200 -and
        $installer.summary.Contains('not Windows 11') -and $installer.summary.Contains('Unsigned fixture')) "Installer lost its actual platform/trust boundary: $($installer.summary)"
    $run.path = '.github/workflows/release.yml'
    $run.event = 'workflow_run'
    $global:LaneHealthFixture.jobs = @(
        @{ id = 121; name = 'plan'; status = 'completed'; conclusion = 'success'
            started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:00:10Z'; steps = @() },
        @{ id = 122; name = 'publish'; status = 'completed'; conclusion = 'skipped'
            started_at = '2026-11-25T10:00:01Z'; completed_at = '2026-11-25T10:00:00Z'; steps = @() }
    )
    $global:LaneHealthFixture.artifacts = @()
    $release = Observe
    $publication = @($release.records | Where-Object { $_.lane -ceq 'release-publish' })[0]
    Assert ($publication.validation -ceq 'skipped' -and $null -eq $publication.measurements -and
        $null -eq $publication.hosted.seconds -and !$publication.hosted.clockValid -and
        $release.summary.Contains('no-ops are not executed scale')) 'Release no-op or invalid skip clocks fabricated verification.'
    $run.path = '.github/workflows/release-verification.yml'; $run.event = 'workflow_dispatch'
    $rehearsalName = "secret-free-release-evidence-$($run.head_sha)-51-1"
    $rehearsalDirectory = Join-Path $evidence $rehearsalName
    New-Item -ItemType Directory -Path $rehearsalDirectory | Out-Null
    $rehearsalResult = @{ outcome = 'success'; revision = $run.head_sha; verificationRunId = 51; runAttempt = '1'
        productionPublication = $false; installationExecuted = $false; ephemeralSigningOnly = $true
        productionTrustIsolation = $true; metadataRenewalVerified = $true }
    $rehearsalResult | ConvertTo-Json | Set-Content (Join-Path $rehearsalDirectory 'result.json')
    $global:LaneHealthFixture.jobs = @(@{ id = 123; name = 'package'; status = 'completed'; conclusion = 'success'
        started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:05:00Z'; steps = @() })
    $global:LaneHealthFixture.artifacts = @(@{ id = 223; name = $rehearsalName; expired = $false
        expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('b' * 64); workflow_run = @{ id = 51; head_sha = $run.head_sha } })
    $rehearsal = Observe
    Assert ($rehearsal.records[0].validation -ceq 'success' -and $rehearsal.summary.Contains('not production publication')) 'A rehearsal lost its explicit non-production scope.'
    $rehearsalResult.runAttempt = '2'
    $rehearsalResult | ConvertTo-Json | Set-Content (Join-Path $rehearsalDirectory 'result.json')
    $staleRehearsal = Observe
    Assert ($staleRehearsal.records[0].validation -ceq 'incomplete') 'A stale rehearsal result became fresh acceptance.'
    $run.path = '.github/workflows/ci.yml'; $run.event = 'push'; $run.run_attempt = 2; $run.head_sha = 'f' * 40
    $secondCore = Join-Path $evidence 'core-attempt-2'
    Copy-Item -LiteralPath $core -Destination $secondCore -Recurse
    $execution.revision = $run.head_sha; $execution.runAttempt = '2'; $execution.executionSeconds = 150
    $execution.finishedAt = '2026-11-25T10:02:30Z'; $execution.complete = $false; $execution.outcome = 'failure'
    $execution | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $secondCore 'execution.json')
    '<TestRun><Results><UnitTestResult testName="Real.workflow" outcome="Failed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="0" failed="1" notExecuted="0" /></ResultSummary></TestRun>' |
        Set-Content (Join-Path $secondCore 'result.trx')
    $coreJob = @{ id = 101; name = 'Core and view models'; status = 'completed'; conclusion = 'failure'; steps = @()
        started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:05:00Z' }
    $coreArtifact = @{ id = 201; name = 'core-attempt-2'; expired = $false; expires_at = '2099-01-01T00:00:00Z'
        digest = 'sha256:' + ('b' * 64); workflow_run = @{ id = 51; head_sha = $run.head_sha } }
    $global:LaneHealthFixture.jobs = @($coreJob); $global:LaneHealthFixture.artifacts = @($coreArtifact)
    $failedAttempt = Observe -Publish
    $failedCore = @($failedAttempt.records | Where-Object { $_.lane -ceq 'core' })[0]
    Assert ($failedCore.validation -ceq 'failure' -and $failedCore.tests.failed -eq 1 -and
        $failedCore.reference.revision -ceq ('a' * 40) -and $failedCore.reference.deltaSeconds -eq 30) 'Failed attempt lost its real tests or descriptive non-self comparison.'
    $run.run_attempt = 3; $coreJob.conclusion = 'success'; $coreArtifact.name = 'core-attempt-3'
    $thirdCore = Join-Path $evidence 'core-attempt-3'
    Copy-Item -LiteralPath $core -Destination $thirdCore -Recurse
    $execution.runAttempt = '3'; $execution.complete = $true; $execution.outcome = 'success'
    $execution | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $thirdCore 'execution.json')
    $recovered = Observe -Publish
    $recoveredCore = @($recovered.records | Where-Object { $_.lane -ceq 'core' })[0]
    Assert ($recoveredCore.validation -ceq 'success' -and $recoveredCore.reference.revision -ceq ('a' * 40) -and
        $recoveredCore.reliability.attempts -eq 3 -and $recoveredCore.reliability.failure -eq 1 -and
        $recoveredCore.recent[0].validation -ceq 'failure') 'A successful rerun erased earlier failure history or became its own reference.'
    $run.run_attempt = 4; $coreJob.conclusion = 'cancelled'; $global:LaneHealthFixture.artifacts = @()
    $cancelled = Observe -Publish
    $cancelledCore = @($cancelled.records | Where-Object { $_.lane -ceq 'core' })[0]
    Assert ($cancelledCore.validation -ceq 'cancelled' -and $null -eq $cancelledCore.measurements -and
        $cancelledCore.reliability.attempts -eq 4 -and $cancelledCore.reliability.failureRatePercent -eq 25) 'Cancellation fabricated measurements or normalized all-attempt reliability.'
    $run.event = 'pull_request'; $run.head_repository.full_name = 'attacker/fork'
    Rejects { Observe -Publish } 'A fork acquired authoritative history access.'
    $run.event = 'push'; $run.head_repository.full_name = 'geevensingh/org-explorer'
    $badHistory = Join-Path $fixture 'bad-history'
    git clone --quiet --branch ci-health-data $remote $badHistory
    $badPath = Join-Path $badHistory 'lanes\51\1\core.json'
    $badRecord = Get-Content $badPath -Raw | ConvertFrom-Json -AsHashtable
    $badRecord.seriesKey = '0' * 64
    $badRecord | ConvertTo-Json -Depth 30 | Set-Content $badPath
    git -C $badHistory add .
    git -C $badHistory -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Invalid series fixture'
    git -C $badHistory push --quiet
    Rejects { Observe } 'Malformed durable comparison configuration was silently accepted.'
    $previousStart = $env:LANE_STARTED_AT
    $previousSummary = $env:GITHUB_STEP_SUMMARY
    $previousTiming = $env:SCALE_TIMING_FILE
    $previousSha = $env:GITHUB_SHA
    try {
        $probe = Join-Path $fixture 'core-probe'
        New-Item -ItemType Directory -Path (Join-Path $probe 'eng'), (Join-Path $probe 'tests') -Force | Out-Null
        foreach ($name in @('Run-CiLane.ps1', 'DesktopResults.ps1')) {
            Copy-Item "$root\eng\$name" (Join-Path $probe "eng\$name")
        }
        foreach ($name in @('DesktopScenarios.json', 'DesktopPartitions.json')) {
            Copy-Item "$root\tests\$name" (Join-Path $probe "tests\$name")
        }
        git -C $probe init --quiet
        git -C $probe add .
        git -C $probe -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Isolated runner'
        $env:LANE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-301).ToString('O')
        $env:GITHUB_STEP_SUMMARY = ''
        $env:GITHUB_SHA = ''
        & pwsh -NoProfile -File (Join-Path $probe 'eng\Run-CiLane.ps1') core *> (Join-Path $fixture 'core-probe.log')
        Assert ($LASTEXITCODE -ne 0) 'An exhausted core budget passed.'
        $terminal = Get-Content (Join-Path $probe '.ci-results\core\execution.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert (!$terminal.complete -and $terminal.outcome -ceq 'timed_out' -and $terminal.steps.Count -eq 0 -and
            $terminal.executionSeconds -gt 300 -and $terminal.selector -ceq 'all') 'Core setup failure lost its original terminal runner result.'
    } finally {
        $env:LANE_STARTED_AT = $previousStart
        $env:GITHUB_STEP_SUMMARY = $previousSummary
        $env:SCALE_TIMING_FILE = $previousTiming
        $env:GITHUB_SHA = $previousSha
    }
    Write-Host "PASS: $checks lane observation contracts."
} finally {
    Remove-Item Function:\gh
    Remove-Variable LaneHealthFixture -Scope Global
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
