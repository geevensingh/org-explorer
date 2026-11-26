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
    if ($ExpectedPolicyVersion -ceq 'ci-timing-v2') {
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
            $record = @{
                recordType = 'release-verification'; lane = "ui-scale-$worker"; validation = 'success'
                revision = $Candidate; runId = $union.runId; runAttempt = $union.runAttempt
                measurements = @{ executionSeconds = $result.report.executionSeconds; metrics = $metrics }
            }
            Set-TimingPolicyEvaluation $record
            $evaluations += $record
            if ($env:GITHUB_STEP_SUMMARY) { Format-TimingPolicyEvaluation $record.timingEvaluation | Add-Content $env:GITHUB_STEP_SUMMARY }
        }
        ConvertTo-Json -InputObject @($evaluations) -Depth 30 | Set-Content (Join-Path $UiScaleDirectory 'timing-evaluation.json')
        . "$PSScriptRoot\LaneHealth.ps1"
        $comparison = @{
            schemaVersion = 2; recordType = 'release-timing'; revision = $Candidate; validation = 'success'
            runId = $union.runId; runAttempt = $union.runAttempt; policyVersion = 'ci-timing-v2'
            observedAt = [DateTimeOffset]::UtcNow.ToString('O'); unexpectedExecutions = $false; measurements = $null
            samples = @(); metrics = @{}; comparisonDiagnostics = @()
        }
        $sample = @{ configuration = $null; rich = @{ seriesKey = $null } }
        foreach ($worker in @('prefix', 'tail')) {
            $sample.rich[$worker] = Get-UiScaleWorkerResult (Join-Path $owned $worker) $worker $Candidate $union.runId $union.runAttempt -ExpectedPolicyVersion $ExpectedPolicyVersion
            $comparison.metrics["$worker-executionSeconds"] = @{
                count = 1; minimum = $sample.rich[$worker].report.executionSeconds; maximum = $sample.rich[$worker].report.executionSeconds
                median = $sample.rich[$worker].report.executionSeconds; range = 0
            }
        }
        $performanceReport = Get-Content (Join-Path $PerformanceDirectory 'result.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($performanceReport.Contains('configuration')) { $sample.configuration = $performanceReport.configuration }
        $comparison.metrics.executionSeconds = @{ count = 1; minimum = $performanceReport.executionSeconds
            maximum = $performanceReport.executionSeconds; median = $performanceReport.executionSeconds; range = 0 }
        $sample.rich.handoff = Get-Content (Join-Path $sample.rich.prefix.run 'handoff\handoff.json') -Raw | ConvertFrom-Json -AsHashtable
        foreach ($phase in @('normal-executable', 'background-export')) {
            $note = Get-Content (Join-Path $sample.rich.prefix.run "scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json") -Raw | ConvertFrom-Json
            $comparison.metrics["prefix-$phase-localDurability-p95-ms"] = @{
                count = 1; minimum = $note.localDurability.p95Ms; maximum = $note.localDurability.p95Ms
                median = $note.localDurability.p95Ms; range = 0
            }
        }
        foreach ($worker in @('prefix', 'tail')) {
            $phase = if ($worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }
            $scenario = if ($worker -ceq 'prefix') { 'scale-mutation-save-prefix' } else { 'scale-mutation-delivery-tail' }
            $profile = Get-Content (Join-Path $sample.rich[$worker].run "$scenario-ui-sqlite-rich-profile-latency-$phase.json") -Raw | ConvertFrom-Json
            $comparison.metrics["$worker-profile-p95-ms"] = @{ count = 1; minimum = $profile.p95Ms
                maximum = $profile.p95Ms; median = $profile.p95Ms; range = 0 }
        }
        $performanceLog = Join-Path $PerformanceDirectory "$($performanceReport.runDirectory)\scale-measurements.log"
        $profileLines = @()
        if (Test-Path -LiteralPath $performanceLog) {
            $profileLines = @(Get-Content $performanceLog | Where-Object { $_.StartsWith('SCALE ORDINARY-PROFILE ') })
        }
        if ($profileLines.Count -eq 1) {
            $profile = $profileLines[0].Substring('SCALE ORDINARY-PROFILE '.Length) | ConvertFrom-Json -AsHashtable
            $metric = Read-HealthLatency $profile 'profile-save' 21
            $comparison.metrics.'profile-p95-ms' = @{ count = 1; minimum = $metric.value
                maximum = $metric.value; median = $metric.value; range = 0 }
        } else { $comparison.comparisonDiagnostics += 'Non-UI profile timing unavailable; no substitute or zero was supplied.' }
        $comparison.samples = @($sample)
        $history = $null; $reference = $null; $knownBlocking = @()
        try {
            if ($null -eq $sample.configuration) { throw 'Resolved non-UI comparison configuration is unavailable.' }
            $sample.rich.seriesKey = Get-RichCohortSeriesKey $sample ci-timing-v2
            $history = Open-HealthHistory (Join-Path $UiScaleDirectory ('timing-history-' + [guid]::NewGuid().ToString('N') + '.git')) `
                $TimingHistoryRemote -AllowLocalFixture:$AllowLocalFixture -RecordTypes (Get-LaneHealthRecordTypes)
            if (@($history.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-reference' }).Count) {
                Set-TimingMainContext $history
                $reference = Get-TimingCanonicalReference $history $sample.rich.seriesKey $Candidate
                foreach ($cohort in @($history.records | Where-Object {
                    $_.Contains('recordType') -and $_.recordType -ceq 'performance-cohort' -and
                    $_.policyVersion -ceq 'ci-timing-v2' -and $_.revision -ceq $Candidate -and
                    $_.samples.Count -eq 5 -and $_.validation -ceq 'success' -and
                    (Get-TimingCohortKey $_) -ceq $sample.rich.seriesKey
                })) {
                    $copy = $cohort | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable -Depth 40
                    $pinned = Get-TimingPinnedReference $copy $history
                    Set-TimingPolicyEvaluation $copy $pinned $history.tip
                    if (@($copy.timingEvaluation.metrics.Values | Where-Object { $_.Contains('seriousRegression') -and $_.seriousRegression }).Count) {
                        $knownBlocking += $copy.cohortId
                    }
                }
            }
        } catch {
            $comparison.comparisonDiagnostics += "Available-reference comparison is unavailable: $($_.Exception.Message)"
            Write-Warning $comparison.comparisonDiagnostics[-1]
            $reference = $null
        }
        Set-TimingPolicyEvaluation $comparison $reference $(if ($history) { $history.tip } else { $null })
        $comparison.knownSeriousCohorts = $knownBlocking
        $comparison.verificationScope = 'one fresh exact-candidate execution; never a three-to-five collection cohort'
        if ($comparison.comparisonDiagnostics.Count -and $comparison.timingEvaluation.health -ceq 'green') {
            $comparison.timingEvaluation.health = 'inconclusive'
        }
        if ($knownBlocking.Count) { $comparison.timingEvaluation.blocking = $true; $comparison.timingEvaluation.health = 'red' }
        $comparison | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $UiScaleDirectory 'timing-comparison.json')
        if ($env:GITHUB_STEP_SUMMARY) { Format-TimingPolicyEvaluation $comparison.timingEvaluation | Add-Content $env:GITHUB_STEP_SUMMARY }
        if ($comparison.timingEvaluation.blocking) { throw 'Fresh release validation passed, but already available approved serious-relative evidence blocks this candidate.' }
    }
}
