$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
. "$root\eng\CiGate.ps1"
$fixture = Join-Path $root ('.test-workspaces\fh-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$remote = Join-Path $fixture 'history.git'
$source = Join-Path $fixture 'source'
$evidence = Join-Path $fixture 'evidence'
New-Item -ItemType Directory -Path $source, $evidence -Force | Out-Null
git init --bare --quiet $remote
git -C $source init --quiet
git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet --allow-empty -m 'Candidate'
$candidate = (git -C $source rev-parse HEAD).Trim()
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
    id = 42; run_attempt = 1; head_sha = $candidate; event = 'push'; head_branch = 'main'
    path = '.github/workflows/ci.yml'; status = 'completed'
    head_repository = @{ full_name = 'geevensingh/org-explorer' }
}
$jobs = @(@{
    id = 100; name = 'Real filesystem persistence'; status = 'completed'; conclusion = 'success'
    started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:02:05Z'
})
$artifacts = @(@{
    id = 200; name = 'filesystem-attempt-1'; expired = $false
    expires_at = '2099-01-01T00:00:00Z'; digest = 'sha256:' + ('a' * 64)
    workflow_run = @{ id = 42; head_sha = $candidate }
})
$global:FilesystemHealthFixture = @{ run = $run; jobs = $jobs; artifacts = $artifacts; evidence = $evidence }
function Write-Evidence {
    @{
        schemaVersion = 1; lane = 'filesystem'; revision = $script:candidate; runId = '42'
        runAttempt = [string]$script:run.run_attempt; complete = $true; outcome = 'success'
        startedAt = '2026-11-25T10:00:00Z'; finishedAt = '2026-11-25T10:02:00Z'
        executionSeconds = 120.0; ceilingSeconds = 660; targetSeconds = 300
        policyVersion = 'filesystem-original-v1'; selector = 'Category!=Performance'
        configuration = @{
            workload = 'OrgExplorer.Persistence.Tests'; testTree = 'b' * 40; fixtureTree = 'c' * 40
            sdk = '10.0.100'; runtimes = @('Microsoft.NETCore.App 10.0.0', 'Microsoft.WindowsDesktop.App 10.0.0')
            os = 'Windows 10.0.26100'; architecture = 'X64'; processors = 4
            image = 'win25'; imageVersion = '20261116.315.1'; powershell = '7.6.10'
            setup = 'locked-restore;isolated-build;setup-and-cleanup-included'
        }
        steps = @(
            @{ name = 'restore'; seconds = 15.0; status = 'Passed' },
            @{ name = 'test'; seconds = 95.0; status = 'Passed' }
        )
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'execution.json')
    @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Fixture.durable_save" outcome="Passed" />
    <UnitTestResult testName="Fixture.rejected_delivery" outcome="Passed" />
  </Results>
  <ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="2" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
'@ | Set-Content -LiteralPath (Join-Path $evidence 'result.trx')
}

# Only GitHub transport is replaced; result validation and Git history are real.
function gh {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if ($arguments[0] -ceq 'run' -and $arguments[1] -ceq 'download') {
        $destination = $arguments[[Array]::IndexOf($arguments, '--dir') + 1]
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($file in @(Get-ChildItem -LiteralPath $global:FilesystemHealthFixture.evidence -File)) {
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
        return
    }
    if ($arguments[0] -cne 'api') { throw 'Unexpected GitHub fixture operation.' }
    switch -Regex ($arguments[1]) {
        '/actions/runs/42/attempts/[1-9][0-9]*$' { $global:FilesystemHealthFixture.run | ConvertTo-Json -Depth 10 -Compress; return }
        '/actions/runs/42/attempts/[1-9][0-9]*/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:FilesystemHealthFixture.jobs }) -Depth 10 -Compress; return
        }
        '/actions/runs/42/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = $global:FilesystemHealthFixture.artifacts }) -Depth 10 -Compress; return
        }
        default { throw "Unexpected GitHub fixture endpoint: $($arguments[1])" }
    }
}
function Observe([switch]$Publish) {
    $work = Join-Path $fixture ('o-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt $run.run_attempt `
        -HistoryRemote $remote -AllowLocalFixture -WorkingDirectory $work -Publish:$Publish
    return @{
        record = Get-Content -LiteralPath (Join-Path $work 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        summary = Get-Content -LiteralPath (Join-Path $work 'summary.md') -Raw
    }
}
try {
    Write-Evidence
    $observed = Observe -Publish
    $saved = (git --git-dir=$remote show refs/heads/ci-health-data:filesystem/42/1.json) -join "`n" |
        ConvertFrom-Json -AsHashtable
    Assert ($saved.schemaVersion -eq 1 -and $saved.revision -ceq $candidate -and $saved.runAttempt -ceq '1') 'History lost exact candidate/attempt provenance.'
    Assert ($saved.validation -ceq 'success' -and $saved.jobOutcome -ceq 'success') 'Complete real result did not remain successful.'
    Assert ($saved.measurements.executionSeconds -eq 120 -and $saved.measurements.steps[0].seconds -eq 15 -and
        $saved.measurements.steps[1].seconds -eq 95 -and $saved.measurements.headroomSeconds -eq 540) 'Saved numeric observation inputs are wrong.'
    Assert ($saved.tests.total -eq 2 -and $saved.tests.passed -eq 2) 'Real TRX counters did not survive publication.'
    Assert ($saved.timingHealth -ceq 'unbaselined' -and $null -eq $saved.reference) 'A first observation invented a healthy reference.'
    Assert ($observed.summary.Contains('Validation: **success**') -and $observed.summary.Contains('Timing: **unbaselined**') -and
        $observed.summary.Contains('120') -and $observed.summary.Contains('540') -and
        $observed.summary.Contains('not fresh release acceptance')) 'GitHub summary does not expose the separate observed result.'
    Assert-CiResults @('success', $saved.jobOutcome, 'success', 'success')
    $run.run_attempt = 2
    $artifacts[0].name = 'filesystem-attempt-2'
    $jobs[0].conclusion = 'failure'
    Write-Evidence
    $failed = Get-Content -LiteralPath (Join-Path $evidence 'execution.json') -Raw | ConvertFrom-Json -AsHashtable
    $failed.complete = $false; $failed.outcome = 'failure'; $failed.steps[1].status = 'Failed'
    $failed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'execution.json')
    (Get-Content -LiteralPath (Join-Path $evidence 'result.trx') -Raw).Replace(
        'testName="Fixture.rejected_delivery" outcome="Passed"', 'testName="Fixture.rejected_delivery" outcome="Failed"').Replace(
        'passed="2" failed="0"', 'passed="1" failed="1"') |
        Set-Content -LiteralPath (Join-Path $evidence 'result.trx')
    $observedFailure = Observe -Publish
    $failureHistory = (git --git-dir=$remote show refs/heads/ci-health-data:filesystem/42/2.json) -join "`n" |
        ConvertFrom-Json -AsHashtable
    Assert ($failureHistory.validation -ceq 'failure' -and $failureHistory.jobOutcome -ceq 'failure' -and
        $failureHistory.tests.failed -eq 1 -and $failureHistory.tests.failedNames[0] -ceq 'Fixture.rejected_delivery' -and
        $failureHistory.measurements.executionSeconds -eq 120) 'A failed attempt lost its actual tests or numeric observations.'
    Assert ($observedFailure.summary.Contains('Attempts: 2') -and $observedFailure.summary.Contains('failure: 1')) 'A later attempt hid earlier history.'
    Rejects { Assert-CiResults @('success', $failureHistory.jobOutcome, 'success', 'success') } 'Timing history changed the required failing gate.'
    $prior = (git --git-dir=$remote show refs/heads/ci-health-data:filesystem/42/1.json) -join "`n" | ConvertFrom-Json -AsHashtable
    Assert ($prior.validation -ceq 'success') 'Earlier validation was rewritten.'
    git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet --allow-empty -m 'New candidate'
    $candidate = (git -C $source rev-parse HEAD).Trim()
    $run.head_sha = $candidate; $run.run_attempt = 3; $jobs[0].conclusion = 'success'
    $artifacts[0].name = 'filesystem-attempt-3'; $artifacts[0].workflow_run.head_sha = $candidate
    Write-Evidence
    $changed = Get-Content -LiteralPath (Join-Path $evidence 'execution.json') -Raw | ConvertFrom-Json -AsHashtable
    $changed.executionSeconds = 150.0; $changed.finishedAt = '2026-11-25T10:02:30Z'; $changed.steps[1].seconds = 125.0
    $changed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'execution.json')
    $comparison = Observe -Publish
    Assert ($comparison.record.reference.revision -ceq $prior.revision -and $comparison.record.reference.runAttempt -ceq '1' -and
        $comparison.record.reference.executionSeconds -eq 120 -and $comparison.record.reference.deltaSeconds -eq 30 -and
        $comparison.record.reference.deltaPercent -eq 25 -and $comparison.record.timingHealth -ceq 'observation') 'An existing compatible observation was not pinned before publication.'
    Assert ($comparison.summary.Contains('25') -and $comparison.summary.Contains('prior compatible observation') -and
        $comparison.summary.Contains($prior.revision) -and $comparison.summary.Contains('| test (Passed) | 125 | 95 | 30 |')) 'The reference measurements and revision are not GitHub-visible.'
    $run.run_attempt = 4; $artifacts[0].name = 'filesystem-attempt-4'
    $changed.runAttempt = '4'
    $changed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'execution.json')
    $selfComparison = Observe -Publish
    Assert ($selfComparison.record.reference.revision -ceq $prior.revision -and
        $selfComparison.record.reference.revision -cne $candidate) 'A candidate became its own yardstick instead of resolving its predecessor.'
    $run.run_attempt = 5; $artifacts[0].name = 'filesystem-attempt-5'; $changed.runAttempt = '5'
    $changed.configuration.imageVersion = 'different-image'
    $changed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'execution.json')
    $incompatible = Observe -Publish
    Assert ($null -eq $incompatible.record.reference -and $incompatible.record.timingHealth -ceq 'inconclusive') 'Incompatible configuration invented a comparable reference.'
    Assert-CiResults @('success', $incompatible.record.jobOutcome, 'success', 'success')
    $run.run_attempt = 6; $jobs[0].conclusion = 'cancelled'
    $global:FilesystemHealthFixture.artifacts = @()
    $cancelled = Observe -Publish
    Assert ($cancelled.record.validation -ceq 'cancelled' -and $null -eq $cancelled.record.measurements -and
        $cancelled.record.evidence.state -ceq 'missing' -and $cancelled.record.timingHealth -ceq 'inconclusive') 'Cancellation without artifacts became fabricated timing or lost history.'
    $global:FilesystemHealthFixture.artifacts = $artifacts
    $run.run_attempt = 7; $artifacts[0].name = 'filesystem-attempt-7'; $artifacts[0].expired = $true
    $expired = Observe -Publish
    Assert ($expired.record.evidence.state -ceq 'expired' -and $null -eq $expired.record.measurements -and
        $expired.summary.Contains('expired')) 'Expired evidence was claimed available or used as fresh verification.'
    $run.run_attempt = 8; $jobs[0].conclusion = 'success'; $artifacts[0].expired = $false; $artifacts[0].name = 'filesystem-attempt-8'
    Write-Evidence
    $malformed = Get-Content -LiteralPath (Join-Path $evidence 'execution.json') -Raw | ConvertFrom-Json -AsHashtable
    $malformed.executionSeconds = 'not-a-number'
    $malformed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'execution.json')
    $invalid = Observe -Publish
    Assert ($invalid.record.validation -ceq 'incomplete' -and $invalid.record.jobOutcome -ceq 'success' -and
        $null -eq $invalid.record.measurements -and $invalid.summary.Contains('nonnumeric')) 'Malformed timing became zero or silently green.'
    Assert-CiResults @('success', $invalid.record.jobOutcome, 'success', 'success')
    $overlap = Join-Path $fixture 'overlap'
    git clone --quiet --branch ci-health-data $remote $overlap
    $overlapRecord = $prior.Clone(); $overlapRecord.runId = '43'
    $overlapRecord.evidence = $prior.evidence.Clone()
    $overlapRecord.evidence.url = 'https://github.com/geevensingh/org-explorer/actions/runs/43/attempts/1'
    $overlapRecord.evidence.expiresAt = '2026-11-20T00:00:00Z'
    New-Item -ItemType Directory -Path (Join-Path $overlap 'filesystem\43') -Force | Out-Null
    $overlapRecord | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $overlap 'filesystem\43\1.json')
    git -C $overlap add filesystem
    git -C $overlap -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Concurrent observation'
    $overlapCommit = (git -C $overlap rev-parse HEAD).Trim()
    $beforeOverlap = (git --git-dir=$remote rev-parse refs/heads/ci-health-data).Trim()
    git -C $overlap push --quiet origin HEAD:refs/heads/overlap-fixture
    @"
#!/bin/sh
unset GIT_QUARANTINE_PATH GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES
git --git-dir="`$PWD" update-ref refs/heads/ci-health-data $overlapCommit $beforeOverlap || exit 1
rm -- "`$PWD/hooks/pre-receive"
"@ | Set-Content -LiteralPath (Join-Path $remote 'hooks\pre-receive') -Encoding utf8NoBOM
    $run.run_attempt = 9; $artifacts[0].name = 'filesystem-attempt-9'
    Write-Evidence
    $overlapping = Observe -Publish
    $paths = @(git --git-dir=$remote ls-tree -r --name-only refs/heads/ci-health-data)
    Assert ($paths -ccontains 'filesystem/43/1.json' -and $paths -ccontains 'filesystem/42/9.json' -and
        $paths.Count -eq 10) 'Overlapping data-branch updates lost an attempt.'
    Assert ($overlapping.record.historySnapshot -ceq $beforeOverlap -and
        $overlapping.record.reference.revision -ceq $prior.revision) 'A concurrent append replaced the pinned comparison.'
    $run.run_attempt = 10; $artifacts[0].name = 'filesystem-attempt-10'
    Write-Evidence
    $expiredReference = Observe -Publish
    Assert ($expiredReference.record.reference.evidenceState -ceq 'expired' -and
        $expiredReference.record.reference.executionSeconds -eq 120 -and
        $expiredReference.summary.Contains('Raw reference evidence: [expired]')) 'Compact comparison inputs did not survive raw artifact expiry.'
    $badHistory = Join-Path $fixture 'bad-history'
    git clone --quiet --branch ci-health-data $remote $badHistory
    $badRecord = Get-Content -LiteralPath (Join-Path $badHistory 'filesystem\43\1.json') -Raw | ConvertFrom-Json -AsHashtable
    $badRecord.measurements.executionSeconds = 'untrusted-text'
    $badRecord | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $badHistory 'filesystem\43\1.json')
    git -C $badHistory add filesystem
    git -C $badHistory -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Malformed history fixture'
    git -C $badHistory push --quiet
    $malformedTip = (git --git-dir=$remote rev-parse refs/heads/ci-health-data).Trim()
    $run.run_attempt = 11; $artifacts[0].name = 'filesystem-attempt-11'; Write-Evidence
    $blockedWork = Join-Path $fixture 'blocked'
    Rejects {
        & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt 11 -HistoryRemote $remote `
            -AllowLocalFixture -WorkingDirectory $blockedWork -Publish
    } 'Malformed durable history was silently accepted.'
    Assert ((git --git-dir=$remote rev-parse refs/heads/ci-health-data).Trim() -ceq $malformedTip) 'Malformed history was overwritten instead of failing closed.'
    Assert ((Get-Content -LiteralPath (Join-Path $blockedWork 'summary.md') -Raw).Contains('History read failed')) 'History read failure was not GitHub-visible.'
    $refusedRemote = Join-Path $fixture 'refused.git'
    git init --bare --quiet $refusedRemote
    "#!/bin/sh`nexit 1" | Set-Content -LiteralPath (Join-Path $refusedRemote 'hooks\pre-receive') -Encoding utf8NoBOM
    $publicationWork = Join-Path $fixture 'publication-failure'
    Rejects {
        & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt 11 -HistoryRemote $refusedRemote `
            -AllowLocalFixture -WorkingDirectory $publicationWork -Publish
    } 'A rejected history write was reported successful.'
    Assert ((Get-Content -LiteralPath (Join-Path $publicationWork 'summary.md') -Raw).Contains('History publication: FAILED')) 'Publication refusal was not GitHub-visible.'
    Assert-CiResults @('success', 'success', 'success', 'success')
    $run.event = 'pull_request'; $run.head_repository.full_name = 'attacker/fork'
    $readOnlyWork = Join-Path $fixture 'fork'
    "'Candidate content must never execute' | Set-Content '$fixture\executed.txt'" |
        Set-Content -LiteralPath (Join-Path $evidence 'candidate.ps1')
    & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt 11 -HistoryRemote $refusedRemote `
        -AllowLocalFixture -WorkingDirectory $readOnlyWork
    $readOnly = Get-Content -LiteralPath (Join-Path $readOnlyWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($readOnly.authority -ceq 'untrusted-observation' -and !(Test-Path -LiteralPath (Join-Path $fixture 'executed.txt'))) 'Candidate data acquired execution or write trust.'
    Rejects {
        & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt 11 -HistoryRemote $refusedRemote `
            -AllowLocalFixture -WorkingDirectory (Join-Path $fixture 'fork-write') -Publish
    } 'A fork was authorized to publish.'
    $run.event = 'push'; $run.head_repository.full_name = 'geevensingh/org-explorer'
    (Get-Content -LiteralPath (Join-Path $evidence 'result.trx') -Raw).Replace('total="2"', 'total="bad-counter"') |
        Set-Content -LiteralPath (Join-Path $evidence 'result.trx')
    $badTrxWork = Join-Path $fixture 'bad-trx'
    & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt 11 -HistoryRemote $refusedRemote `
        -AllowLocalFixture -WorkingDirectory $badTrxWork
    $badTrx = Get-Content -LiteralPath (Join-Path $badTrxWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($badTrx.validation -ceq 'incomplete' -and $null -eq $badTrx.tests -and
        $badTrx.measurements.executionSeconds -eq 120) 'Malformed TRX manufactured passing/zero counters or discarded valid lane observations.'
    Write-Evidence
    $stale = Get-Content -LiteralPath (Join-Path $evidence 'execution.json') -Raw | ConvertFrom-Json -AsHashtable
    $stale.runAttempt = '1'
    $stale | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'execution.json')
    $staleWork = Join-Path $fixture 'stale'
    & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt 11 -HistoryRemote $refusedRemote `
        -AllowLocalFixture -WorkingDirectory $staleWork
    $staleRecord = Get-Content -LiteralPath (Join-Path $staleWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($staleRecord.validation -ceq 'incomplete' -and $null -eq $staleRecord.measurements -and
        $staleRecord.evidence.state -ceq 'rejected') 'An earlier attempt was substituted for missing current evidence.'
    New-Item -ItemType Directory -Path (Join-Path $source 'eng'), (Join-Path $source 'tests') -Force | Out-Null
    foreach ($name in @('Run-CiLane.ps1', 'DesktopResults.ps1', 'CoreContracts.ps1', 'TimingPolicy.ps1')) {
        Copy-Item -LiteralPath (Join-Path $root "eng\$name") -Destination (Join-Path $source "eng\$name")
    }
    foreach ($name in @('DesktopScenarios.json', 'DesktopPartitions.json')) {
        Copy-Item -LiteralPath (Join-Path $root "tests\$name") -Destination (Join-Path $source "tests\$name")
    }
    $previousEnvironment = @{}
    foreach ($name in @('LANE_STARTED_AT', 'GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'SCALE_TIMING_FILE')) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $previousDirectory = Get-Location
    try {
        $env:LANE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-661).ToString('O')
        $env:GITHUB_SHA = $candidate; $env:GITHUB_RUN_ID = '42'; $env:GITHUB_RUN_ATTEMPT = '11'
        $rejected = $false
        try { & (Join-Path $source 'eng\Run-CiLane.ps1') filesystem }
        catch {
            if ($_.Exception.Message -cne 'The lane budget (including setup) is exhausted.') { throw }
            $rejected = $true
        }
        $terminal = Get-Content -LiteralPath (Join-Path $source '.ci-results\filesystem\execution.json') -Raw |
            ConvertFrom-Json -AsHashtable
        Assert ($rejected -and !$terminal.complete -and $terminal.outcome -ceq 'timed_out' -and
            $terminal.revision -ceq $candidate -and $terminal.runId -ceq '42' -and $terminal.runAttempt -ceq '11' -and
            $terminal.executionSeconds -gt 660 -and $terminal.steps.Count -eq 0 -and
            $terminal.selector -ceq 'Category!=Performance') 'The public runner lost its original timeout or candidate-bound incomplete record.'
        Copy-Item -LiteralPath (Join-Path $source '.ci-results\filesystem\execution.json') -Destination (Join-Path $evidence 'execution.json')
        Remove-Item -LiteralPath (Join-Path $evidence 'result.trx')
        $jobs[0].conclusion = 'timed_out'
        $timeoutRemote = Join-Path $fixture 'timeout.git'
        git init --bare --quiet $timeoutRemote
        $timeoutWork = Join-Path $fixture 'timeout'
        & "$root\eng\Invoke-FilesystemHealth.ps1" -RunId 42 -RunAttempt 11 -HistoryRemote $timeoutRemote `
            -AllowLocalFixture -WorkingDirectory $timeoutWork -Publish
        $timeoutRecord = Get-Content -LiteralPath (Join-Path $timeoutWork 'record.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert ($timeoutRecord.validation -ceq 'incomplete' -and $timeoutRecord.jobOutcome -ceq 'timed_out' -and
            $timeoutRecord.measurements.executionSeconds -gt 660 -and $null -eq $timeoutRecord.tests) 'A real runner interruption lost its useful timing or acquired passing tests.'
        Rejects { Assert-CiResults @('success', $timeoutRecord.jobOutcome, 'success', 'success') } 'Timeout history changed the original failing gate.'
    } finally {
        Set-Location -LiteralPath $previousDirectory
        foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name]) }
    }
    Write-Host "PASS: $checks filesystem result-to-history-to-summary assertions."
} finally {
    Remove-Variable -Name FilesystemHealthFixture -Scope Global
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
