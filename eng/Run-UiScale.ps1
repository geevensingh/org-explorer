param(
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedRevision,
    [string] $ResultsDirectory = '.ci-results\ui-scale',
    [switch] $ExtendedObservation,
    [switch] $DiagnosticOnly,
    [switch] $CpuWaitTrace
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\DesktopResults.ps1"
. "$PSScriptRoot\ScaleRunner.ps1"
$required = @(Get-Content "$root\tests\UiScaleScenarios.json" -Raw | ConvertFrom-Json)
$observation = if ($ExtendedObservation) { 900 } else { 660 }
if ($CpuWaitTrace -and (!$DiagnosticOnly -or $ExtendedObservation)) {
    throw 'CPU/wait tracing requires explicit DiagnosticOnly at the unchanged660 window.'
}

Invoke-ScaleSuite -Suite ui-scale -Label 'UI scale' `
    -ResultsDirectory $ResultsDirectory -ExpectedRevision $ExpectedRevision -ObservationSeconds $observation -DiagnosticOnly:$DiagnosticOnly `
    -Selection @{ selector = '--ui-scale'; requiredScenarios = $required } -Verify {
        param($run, $build)
        if (![Environment]::UserInteractive) {
            throw 'UI scale requires an interactive, unlocked hosted or dedicated isolated Windows desktop. No rendering checks may be skipped.'
        }
        $previousResults = $env:DESKTOP_RESULTS_DIRECTORY
        $previousCpuWait = $env:TEMP_CPU_WAIT_SCRIPT
        $primary = $null
        try {
            $env:DESKTOP_RESULTS_DIRECTORY = $run
            if ($CpuWaitTrace) {
                if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
                    $env:RUNNER_OS -cne 'Windows' -or ![Environment]::Is64BitProcess -or
                    ![Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                        [Security.Principal.WindowsBuiltInRole]::Administrator) -or
                    !(Test-Path -LiteralPath "$env:SystemRoot\System32\wpr.exe")) {
                    throw 'CPU/wait requires elevated64-bit disposable hosted Windows and inbox WPR; no install or fallback.'
                }
                $env:TEMP_CPU_WAIT_SCRIPT = "$PSScriptRoot\TEMP-CPU-WAIT.ps1"
            } else { $env:TEMP_CPU_WAIT_SCRIPT = $null }
            Invoke-ScaleDotnet restore-app @('restore', 'src\OrgExplorer.Desktop', '--locked-mode')
            Invoke-ScaleDotnet restore-tests @('restore', 'tests\OrgExplorer.Desktop.Smoke', '--locked-mode')
            Invoke-ScaleDotnet build-app @('build', 'src\OrgExplorer.Desktop', '-c', 'Release', '--no-restore')
            $displayCommand = if ($env:GITHUB_ACTIONS -ceq 'true' -and
                $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and $env:RUNNER_OS -ceq 'Windows') {
                '--prepare-high-resolution'
            } else { '--verify-high-resolution' }
            Invoke-ScaleDotnet display-prerequisite @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release',
                '--no-restore', '--', $displayCommand)
            Invoke-ScaleDotnet ui-scale @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release', '--no-build', '--no-restore',
                '--', (Join-Path $build 'bin\OrgExplorer.Desktop\release\OrgExplorer.Desktop.exe'), '--ui-scale')
            Assert-DesktopResults (Join-Path $run 'scenarios.json') $required
        } catch { $primary = $_; throw }
        finally {
            try {
                if ($CpuWaitTrace) { & "$PSScriptRoot\TEMP-CPU-WAIT.ps1" -CleanupRoot $run }
            } catch {
                if (!$primary) { throw }
                Write-Host "TEMP CPU/wait secondary owned cleanup failure: $($_.Exception.Message)"
            } finally {
                $env:TEMP_CPU_WAIT_SCRIPT = $previousCpuWait
                $env:DESKTOP_RESULTS_DIRECTORY = $previousResults
            }
        }
    }
