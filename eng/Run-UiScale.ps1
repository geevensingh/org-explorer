param(
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedRevision,
    [ValidateSet('prefix', 'tail')][string] $Worker,
    [string] $PrefixResultsDirectory,
    [string] $RunId = $env:GITHUB_RUN_ID,
    [string] $RunAttempt = $env:GITHUB_RUN_ATTEMPT,
    [string] $ResultsDirectory = '.ci-results\ui-scale',
    [ValidatePattern('^[0-9a-f]{40}-[1-9][0-9]*-[1-9][0-9]*$')][string] $CohortId,
    [ValidateRange(1, 5)][int] $Sample,
    [switch] $ExtendedObservation
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\DesktopResults.ps1"
. "$PSScriptRoot\ScaleRunner.ps1"
. "$PSScriptRoot\UiScaleResults.ps1"
if (!$Worker) { throw 'Select prefix or tail; complete UI-scale acceptance requires both isolated workers and Assert-UiScaleUnion.ps1.' }
$plan = Get-UiScalePlan
$required = @($plan.workers[$Worker])
$observation = if ($ExtendedObservation) { 900 } else { 660 }
$selection = @{ selector = "--ui-scale-$Worker"; worker = $Worker; requiredScenarios = $required
    runId = $RunId; runAttempt = $RunAttempt; completedPhases = @() }
if ($CohortId) {
    if (!$ExpectedRevision -or $Sample -lt 1 -or $CohortId -cne "$ExpectedRevision-$RunId-$RunAttempt") {
        throw 'Rich cohort execution requires exact candidate, sample and run/attempt.'
    }
    $selection.cohortId = $CohortId; $selection.sample = $Sample
} elseif ($PSBoundParameters.ContainsKey('Sample')) { throw 'A rich sample requires its cohort identity.' }

Invoke-ScaleSuite -Suite ui-scale -Label 'UI scale' `
    -ResultsDirectory $ResultsDirectory -ExpectedRevision $ExpectedRevision -ObservationSeconds $observation `
    -Selection $selection -Verify {
        param($run, $build)
        if (![Environment]::UserInteractive) {
            throw 'UI scale requires an interactive, unlocked hosted or dedicated isolated Windows desktop. No rendering checks may be skipped.'
        }
        $previousResults = $env:DESKTOP_RESULTS_DIRECTORY
        $names = @('UI_SCALE_REVISION', 'UI_SCALE_SOURCE_TREE', 'UI_SCALE_TEST_TREE', 'UI_SCALE_ENG_TREE',
            'UI_SCALE_WORKFLOW_BLOB', 'UI_SCALE_HANDOFF_DIRECTORY', 'UI_SCALE_WORKER_RUN_DIRECTORY',
            'GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT')
        $previous = @{}
        foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
        try {
            if (!$ExpectedRevision -or !$RunId -or !$RunAttempt) { throw 'Rich workers require explicit candidate, run and attempt.' }
            $handoff = Join-Path $run 'handoff'
            if ($Worker -eq 'tail') {
                if (!$PrefixResultsDirectory) { throw 'The tail requires the current successful prefix worker artifact.' }
                $prefix = Get-UiScaleWorkerResult $PrefixResultsDirectory prefix $ExpectedRevision $RunId $RunAttempt $CohortId $Sample
                $handoff = [IO.Path]::GetFullPath((Join-Path $prefix.run 'handoff'))
            }
            $env:UI_SCALE_HANDOFF_DIRECTORY = $handoff
            $env:UI_SCALE_WORKER_RUN_DIRECTORY = Split-Path $run -Leaf
            $env:UI_SCALE_REVISION = $ExpectedRevision
            $env:GITHUB_SHA = $ExpectedRevision
            $env:GITHUB_RUN_ID = $RunId
            $env:GITHUB_RUN_ATTEMPT = $RunAttempt
            $env:UI_SCALE_SOURCE_TREE = (& git rev-parse 'HEAD:src').Trim()
            $env:UI_SCALE_TEST_TREE = (& git rev-parse 'HEAD:tests').Trim()
            $env:UI_SCALE_ENG_TREE = (& git rev-parse 'HEAD:eng').Trim()
            $env:UI_SCALE_WORKFLOW_BLOB = (& git rev-parse 'HEAD:.github/workflows/scale-verification.yml').Trim()
            $env:DESKTOP_RESULTS_DIRECTORY = $run
            Invoke-ScaleDotnet restore-app @('restore', 'src\OrgExplorer.Desktop', '--locked-mode')
            Invoke-ScaleDotnet restore-tests @('restore', 'tests\OrgExplorer.Desktop.Smoke', '--locked-mode')
            # Map both unique output roots and checkout roots for reproducible component hashes across isolated workers.
            $map = "-p:PathMap=$build=C:\_ui-scale-build%2C$root=C:\_ui-scale-source"
            Invoke-ScaleDotnet build-app @('build', 'src\OrgExplorer.Desktop', '-c', 'Release', '--no-restore', $map)
            $displayCommand = if ($env:GITHUB_ACTIONS -ceq 'true' -and
                $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and $env:RUNNER_OS -ceq 'Windows') {
                '--prepare-high-resolution'
            } else { '--verify-high-resolution' }
            Invoke-ScaleDotnet display-prerequisite @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release',
                '--no-restore', $map, '--', $displayCommand)
            Invoke-ScaleDotnet ui-scale @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release', '--no-build', '--no-restore',
                '--', (Join-Path $build 'bin\OrgExplorer.Desktop\release\OrgExplorer.Desktop.exe'), "--ui-scale-$Worker")
            Assert-DesktopResults (Join-Path $run 'scenarios.json') $required
            Assert-UiScalePhases (Join-Path $run 'ui-scale.log') @($plan.phases[$Worker])
            $selection.completedPhases = @($plan.phases[$Worker])
            $selection.handoffSha256 = (Get-FileHash (Join-Path $handoff 'handoff.json') -Algorithm SHA256).Hash
        } finally {
            $env:DESKTOP_RESULTS_DIRECTORY = $previousResults
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
        }
    }
