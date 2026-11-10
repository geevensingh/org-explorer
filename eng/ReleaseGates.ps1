function Assert-ReleaseVerification(
    [string]$PerformanceDirectory, [string]$UiScaleDirectory, [string]$Candidate, [DateTimeOffset]$StartedAt
) {
    . "$PSScriptRoot\PerformanceSuite.ps1"
    . "$PSScriptRoot\DesktopResults.ps1"
    $requiredScenarios = @(Get-Content "$PSScriptRoot\..\tests\UiScaleScenarios.json" -Raw | ConvertFrom-Json)
    foreach ($suite in @('non-ui-performance', 'ui-scale')) {
        $directory = if ($suite -eq 'non-ui-performance') { $PerformanceDirectory } else { $UiScaleDirectory }
        $rootReport = Join-Path $directory 'result.json'
        $reportText = Get-Content -LiteralPath $rootReport -Raw
        $report = $reportText | ConvertFrom-Json
        if ($report.suite -cne $suite -or $report.outcome -cne 'success' -or
            $report.revision -cne $Candidate -or $report.expectedRevision -cne $Candidate -or
            $report.trackedChanges -ne $false -or $report.untrackedChanges -ne $false -or
            $report.ceilingSeconds -ne 660 -or $report.executionSeconds -lt 0 -or $report.executionSeconds -gt 660 -or
            $report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$' -or
            [DateTimeOffset]::Parse($report.startedAt) -lt $StartedAt -or
            [DateTimeOffset]::Parse($report.completedAt) -lt [DateTimeOffset]::Parse($report.startedAt) -or
            [DateTimeOffset]::Parse($report.completedAt) -gt [DateTimeOffset]::UtcNow -or
            ([DateTimeOffset]::Parse($report.completedAt) - [DateTimeOffset]::Parse($report.startedAt)).TotalSeconds -gt 660) {
            throw "Release requires fresh, complete, clean, exact-candidate $suite success from this execution. Nightly or previous artifacts are not approval."
        }
        $run = Join-Path $directory $report.runDirectory
        if ((Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw) -cne $reportText) { throw 'Suite root and unique-run evidence disagree.' }
        if ($suite -eq 'non-ui-performance') {
            if ($report.filter -cne $PerformanceFilter -or
                (Compare-Object @($PerformanceTests) @($report.requiredTests))) { throw 'Incomplete release performance selection.' }
            Assert-PerformanceResults $run
        } else {
            if (Compare-Object $requiredScenarios @($report.requiredScenarios)) { throw 'Incomplete release UI-scale selection.' }
            Assert-DesktopResults (Join-Path $run 'scenarios.json') $requiredScenarios
        }
    }
}
