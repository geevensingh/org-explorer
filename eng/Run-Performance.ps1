param(
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedRevision,
    [string] $ResultsDirectory = '.ci-results\performance',
    [ValidatePattern('^[0-9a-f]{40}-[1-9][0-9]*-[1-9][0-9]*$')][string]$CohortId,
    [ValidateRange(1, 5)][int]$Sample,
    [string]$RunId = $env:GITHUB_RUN_ID,
    [string]$RunAttempt = $env:GITHUB_RUN_ATTEMPT,
    [switch]$PrepareCleanupAuthentication
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\PerformanceSuite.ps1"
. "$PSScriptRoot\ScaleRunner.ps1"
$selection = @{
    schemaVersion = 1; filter = $PerformanceFilter; requiredTests = $PerformanceTests
    invocationId = [guid]::NewGuid().ToString('N'); runId = $RunId; runAttempt = $RunAttempt
    policyVersion = 'non-ui-original-v1'; configuration = $null; configurationErrors = @()
}
if ($CohortId) {
    if (!$ExpectedRevision -or $Sample -lt 1 -or $CohortId -cne "$ExpectedRevision-$RunId-$RunAttempt") {
        throw 'Cohort execution requires an exact candidate, sample and current run/attempt binding.'
    }
    $selection.cohortId = $CohortId; $selection.sample = $Sample
} elseif ($PSBoundParameters.ContainsKey('Sample')) { throw 'A cohort sample requires its cohort identity.' }

Invoke-ScaleSuite -Suite non-ui-performance -Label 'Non-UI performance' `
    -ResultsDirectory $ResultsDirectory -ExpectedRevision $ExpectedRevision `
    -PrepareCleanupAuthentication:$PrepareCleanupAuthentication -Selection $selection -Verify {
        param($run, $build)
        $previousTiming = $env:SCALE_TIMING_FILE
        try {
            $env:SCALE_TIMING_FILE = Join-Path $run 'scale-measurements.log'
            try { $selection.configuration = Get-PerformanceConfiguration }
            catch { $selection.configurationErrors += "Comparison configuration unavailable: $($_.Exception.Message)" }
            Invoke-ScaleDotnet restore @('restore', 'tests\OrgExplorer.Persistence.Tests', '--locked-mode')
            Invoke-ScaleDotnet test @('test', 'tests\OrgExplorer.Persistence.Tests', '-c', 'Release', '--no-restore',
                '--filter', $PerformanceFilter, '--logger', 'trx', '--results-directory', $run)
            Assert-PerformanceResults $run
        } finally { $env:SCALE_TIMING_FILE = $previousTiming }
    }
