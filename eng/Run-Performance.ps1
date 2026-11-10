param(
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedRevision,
    [string] $ResultsDirectory = '.ci-results\performance'
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\PerformanceSuite.ps1"
. "$PSScriptRoot\ScaleRunner.ps1"

Invoke-ScaleSuite -Suite non-ui-performance -Label 'Non-UI performance' `
    -ResultsDirectory $ResultsDirectory -ExpectedRevision $ExpectedRevision `
    -Selection @{ filter = $PerformanceFilter; requiredTests = $PerformanceTests } -Verify {
        param($run, $build)
        $previousTiming = $env:SCALE_TIMING_FILE
        try {
            $env:SCALE_TIMING_FILE = Join-Path $run 'scale-measurements.log'
            Invoke-ScaleDotnet restore @('restore', 'tests\OrgExplorer.Persistence.Tests', '--locked-mode')
            Invoke-ScaleDotnet test @('test', 'tests\OrgExplorer.Persistence.Tests', '-c', 'Release', '--no-restore',
                '--filter', $PerformanceFilter, '--logger', 'trx', '--results-directory', $run)
            Assert-PerformanceResults $run
        } finally { $env:SCALE_TIMING_FILE = $previousTiming }
    }
