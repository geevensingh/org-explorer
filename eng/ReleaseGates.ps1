function Assert-ReleaseVerification(
    [string]$PerformanceDirectory, [string]$UiScaleDirectory, [string]$Candidate, [DateTimeOffset]$StartedAt,
    [string]$ExpectedPolicyVersion
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
}
