param(
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedRevision,
    [string] $ResultsDirectory = '.ci-results\ui-scale'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\DesktopResults.ps1"
. "$PSScriptRoot\ScaleRunner.ps1"
$required = @(Get-Content "$root\tests\UiScaleScenarios.json" -Raw | ConvertFrom-Json)

Invoke-ScaleSuite -Suite ui-scale -Label 'UI scale' `
    -ResultsDirectory $ResultsDirectory -ExpectedRevision $ExpectedRevision `
    -Selection @{ selector = '--ui-scale'; requiredScenarios = $required } -Verify {
        param($run, $build)
        if (![Environment]::UserInteractive) {
            throw 'UI scale requires an interactive, unlocked hosted or dedicated isolated Windows desktop. No rendering checks may be skipped.'
        }
        $previousResults = $env:DESKTOP_RESULTS_DIRECTORY
        try {
            $env:DESKTOP_RESULTS_DIRECTORY = $run
            Invoke-ScaleDotnet restore-app @('restore', 'src\OrgExplorer.Desktop', '--locked-mode')
            Invoke-ScaleDotnet restore-tests @('restore', 'tests\OrgExplorer.Desktop.Smoke', '--locked-mode')
            Invoke-ScaleDotnet build-app @('build', 'src\OrgExplorer.Desktop', '-c', 'Release', '--no-restore')
            $displayCommand = if ($env:GITHUB_ACTIONS -ceq 'true') { '--prepare-high-resolution' } else { '--verify-high-resolution' }
            Invoke-ScaleDotnet display-prerequisite @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release',
                '--no-restore', '--', $displayCommand)
            Invoke-ScaleDotnet ui-scale @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release', '--no-build', '--no-restore',
                '--', (Join-Path $build 'bin\OrgExplorer.Desktop\release\OrgExplorer.Desktop.exe'), '--ui-scale')
            Assert-DesktopResults (Join-Path $run 'scenarios.json') $required
        } finally { $env:DESKTOP_RESULTS_DIRECTORY = $previousResults }
    }
