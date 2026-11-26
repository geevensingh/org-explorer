function Assert-ReleaseVerification(
    [string]$PerformanceDirectory, [string]$UiScaleDirectory, [string]$Candidate, [DateTimeOffset]$StartedAt,
    [string]$ExpectedPolicyVersion,
    [string]$TimingHistoryRemote = 'https://github.com/geevensingh/org-explorer.git',
    [switch]$AllowLocalFixture
) {
    . "$PSScriptRoot\PerformanceSuite.ps1"
    . "$PSScriptRoot\DesktopResults.ps1"
    . "$PSScriptRoot\UiScaleResults.ps1"
    if (!$ExpectedPolicyVersion) { $ExpectedPolicyVersion = Get-SourceTimingPolicyVersion $Candidate }
    $policy = Get-ApprovedTimingPolicy $ExpectedPolicyVersion
    foreach ($suite in @('non-ui-performance', 'ui-scale')) {
        $directory = if ($suite -eq 'non-ui-performance') { $PerformanceDirectory } else { $UiScaleDirectory }
        $rootReport = Join-Path $directory 'result.json'
        $reportText = Get-Content -LiteralPath $rootReport -Raw
        $report = $reportText | ConvertFrom-Json
        Assert-ScaleCleanupDisposition $report $ExpectedPolicyVersion $directory
        if ($suite -eq 'ui-scale') {
            if ($report.suite -cne 'ui-scale' -or $report.outcome -cne 'success' -or
                $report.verificationMode -cne 'isolated-workers' -or $report.revision -cne $Candidate -or
                $report.expectedRevision -cne $Candidate -or $report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$') {
                throw 'Release requires the fresh complete isolated UI-scale union, not a legacy single-worker result.'
            }
            $run = Join-Path $directory $report.runDirectory
            if ((Get-Content (Join-Path $run 'result.json') -Raw) -cne $reportText) {
                throw 'UI-scale union root and unique-run evidence disagree.'
            }
            Assert-UiScaleUnion (Join-Path $run 'prefix') (Join-Path $run 'tail') $Candidate $report.runId $report.runAttempt -ExpectedPolicyVersion $ExpectedPolicyVersion
            foreach ($worker in @('prefix', 'tail')) {
                $execution = (Get-UiScaleWorkerResult (Join-Path $run $worker) $worker $Candidate $report.runId $report.runAttempt -ExpectedPolicyVersion $ExpectedPolicyVersion).report
                $limit = if ($worker -ceq 'prefix') { $policy.prefixSafetySeconds } else { $policy.tailSafetySeconds }
                if ([DateTimeOffset]$execution.startedAt -lt $StartedAt -or
                    [DateTimeOffset]$execution.completedAt -gt [DateTimeOffset]::UtcNow -or
                    ([DateTimeOffset]$execution.completedAt - [DateTimeOffset]$execution.startedAt).TotalSeconds -gt $limit) {
                    throw 'Release requires both fresh current-execution rich workers within their source-bound safety limits.'
                }
            }
            continue
        }
        if ($report.suite -cne $suite -or $report.outcome -cne 'success' -or
            $report.revision -cne $Candidate -or $report.expectedRevision -cne $Candidate -or
            $report.trackedChanges -ne $false -or $report.untrackedChanges -ne $false -or
            $report.ceilingSeconds -ne 660 -or $report.executionSeconds -lt 0 -or $report.executionSeconds -gt 660 -or
            $report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$' -or
            [DateTimeOffset]$report.startedAt -lt $StartedAt -or
            [DateTimeOffset]$report.completedAt -lt [DateTimeOffset]$report.startedAt -or
            [DateTimeOffset]$report.completedAt -gt [DateTimeOffset]::UtcNow -or
            ([DateTimeOffset]$report.completedAt - [DateTimeOffset]$report.startedAt).TotalSeconds -gt 660) {
            throw "Release requires fresh, complete, clean, exact-candidate $suite success from this execution. Nightly or previous artifacts are not approval."
        }
        $run = Join-Path $directory $report.runDirectory
        if ((Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw) -cne $reportText) { throw 'Suite root and unique-run evidence disagree.' }
        if ($suite -eq 'non-ui-performance') {
            if ($report.filter -cne $PerformanceFilter -or
                (Compare-Object @($PerformanceTests) @($report.requiredTests))) { throw 'Incomplete release performance selection.' }
            Assert-PerformanceResults $run
        }
    }
    if ($ExpectedPolicyVersion -cin @('ci-timing-v2', 'ci-timing-v3', 'ci-timing-v4')) {
        $evaluations = @()
        $union = Get-Content (Join-Path $UiScaleDirectory 'result.json') -Raw | ConvertFrom-Json
        $owned = Join-Path $UiScaleDirectory $union.runDirectory
        foreach ($worker in @('prefix', 'tail')) {
            $result = Get-UiScaleWorkerResult (Join-Path $owned $worker) $worker $Candidate $union.runId $union.runAttempt -ExpectedPolicyVersion $ExpectedPolicyVersion
            $metrics = @()
            if ($worker -ceq 'prefix') {
                foreach ($phase in @('normal-executable', 'background-export')) {
                    $note = Get-Content (Join-Path $result.run "scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json") -Raw |
                        ConvertFrom-Json -AsHashtable
                    $metrics += @{ name = "note-durability-$phase"; value = $note.localDurability.p95Ms
                        samples = $note.localDurability.samplesMs; budgetMs = 1500; goalMs = 1000 }
                }
            }
            if ($ExpectedPolicyVersion -cin @('ci-timing-v3', 'ci-timing-v4')) {
                $phase = if ($worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }
                $scenario = if ($worker -ceq 'prefix') { 'scale-mutation-save-prefix' } else { 'scale-mutation-delivery-tail' }
                $profile = Get-Content (Join-Path $result.run "$scenario-ui-sqlite-rich-profile-latency-$phase.json") -Raw |
                    ConvertFrom-Json -AsHashtable
                $metrics += @{ name = "profile-save-$phase"; value = $profile.p95Ms
                    samples = $profile.samplesMs; budgetMs = $policy.richProfileCeilingMs; goalMs = $policy.richProfileGoalMs }
            }
            $record = @{
                recordType = 'release-verification'; lane = "ui-scale-$worker"; validation = 'success'
                revision = $Candidate; runId = $union.runId; runAttempt = $union.runAttempt
                measurements = @{ executionSeconds = $result.report.executionSeconds; metrics = $metrics }
            }
            Set-TimingPolicyEvaluation $record -PolicyVersion $ExpectedPolicyVersion
            $evaluations += $record
            if ($env:GITHUB_STEP_SUMMARY) { Format-TimingPolicyEvaluation $record.timingEvaluation | Add-Content $env:GITHUB_STEP_SUMMARY }
        }
        ConvertTo-Json -InputObject @($evaluations) -Depth 30 | Set-Content (Join-Path $UiScaleDirectory 'timing-evaluation.json')
        . "$PSScriptRoot\LaneHealth.ps1"
        $comparison = New-TimingExecutionRecord $PerformanceDirectory (Join-Path $owned 'prefix') (Join-Path $owned 'tail') $Candidate $union.runId $union.runAttempt -ExpectedPolicyVersion $ExpectedPolicyVersion
        $history = $null
        try {
            $historyDirectory = Join-Path (Split-Path $UiScaleDirectory -Parent) ('th-' + [guid]::NewGuid().ToString('N').Substring(0, 12) + '.git')
            $history = Open-HealthHistory $historyDirectory `
                $TimingHistoryRemote -AllowLocalFixture:$AllowLocalFixture -RecordTypes (Get-LaneHealthRecordTypes)
            Set-TimingExecutionComparison $comparison $history
        } catch {
            $comparison.comparisonDiagnostics += "Available-reference comparison is unavailable: $($_.Exception.Message)"
            Write-Warning $comparison.comparisonDiagnostics[-1]
            Set-TimingPolicyEvaluation $comparison $null $(if ($history) { $history.tip } else { $null }) -PolicyVersion $ExpectedPolicyVersion
        }
        $comparison.verificationScope = 'one fresh exact-candidate execution; never a three-to-five collection cohort'
        if ($comparison.comparisonDiagnostics.Count -and $comparison.timingEvaluation.health -ceq 'green') {
            $comparison.timingEvaluation.health = 'inconclusive'
        }
        if ($comparison.Contains('knownSeriousCohorts') -and $comparison.knownSeriousCohorts.Count) {
            $comparison.timingEvaluation.blocking = $true; $comparison.timingEvaluation.health = 'red'
        }
        $comparison | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $UiScaleDirectory 'timing-comparison.json')
        if ($env:GITHUB_STEP_SUMMARY) { Format-TimingPolicyEvaluation $comparison.timingEvaluation | Add-Content $env:GITHUB_STEP_SUMMARY }
        if ($comparison.Contains('knownSeriousCohorts')) {
            foreach ($cohort in $comparison.knownSeriousCohorts) {
                $message = "Already available approved serious-relative evidence: [$($cohort.cohortId)]($($cohort.evidenceUrl)); pinned reference ``$($cohort.referenceRevision)``."
                if ($env:GITHUB_STEP_SUMMARY) { $message | Add-Content $env:GITHUB_STEP_SUMMARY }
            }
        }
        if ($comparison.timingEvaluation.blocking) { throw 'Fresh release validation passed, but already available approved serious-relative evidence blocks this candidate.' }
    }
}
