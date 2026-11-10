param(
    [Parameter(Mandatory)][ValidateSet('core', 'filesystem', 'desktop')][string] $Lane
)
$ErrorActionPreference = 'Stop'
$started = if ($env:LANE_STARTED_AT) { [DateTimeOffset]::Parse($env:LANE_STARTED_AT) } else { [DateTimeOffset]::UtcNow }
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$results = Join-Path $root ".ci-results\$Lane"
New-Item -ItemType Directory -Path $results -Force | Out-Null
$steps = [System.Collections.Generic.List[object]]::new()
$index = 0
$artifactBuild = Join-Path $results 'build'
$ceilingSeconds = if ($Lane -eq 'core') { 300 } else { 660 }
$env:SCALE_TIMING_FILE = Join-Path $results 'scale-measurements.log'

function Invoke-BudgetedDotnet([string] $Name, [string[]] $Arguments) {
    $Arguments = @($Arguments[0], '--artifacts-path', $artifactBuild) + $Arguments[1..($Arguments.Length - 1)]
    $remaining = [TimeSpan]::FromSeconds($ceilingSeconds) - ([DateTimeOffset]::UtcNow - $started)
    if ($remaining.TotalMilliseconds -le 0) { throw 'The lane budget (including setup) is exhausted.' }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $results "$script:index-$Name.log"
    $errors = Join-Path $results "$script:index-$Name-errors.log"
    $script:index++
    $process = Start-Process dotnet -ArgumentList $Arguments -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errors
    try {
        if (!$process.WaitForExit([int]$remaining.TotalMilliseconds)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Name exceeded the remaining execution budget."
        }
        Get-Content $log
        Get-Content $errors
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
    } finally {
        $steps.Add(@{ name = $Name; seconds = $timer.Elapsed.TotalSeconds })
        $process.Dispose()
    }
}

try {
    switch ($Lane) {
        core {
            Invoke-BudgetedDotnet restore @('restore', 'tests\OrgExplorer.Core.Tests', '--locked-mode')
            Invoke-BudgetedDotnet test @('test', 'tests\OrgExplorer.Core.Tests', '-c', 'Release', '--no-restore', '--logger', 'trx',
                '--results-directory', '.ci-results\core')
            & "$root\tests\CiGate.Tests.ps1"
            & "$root\tests\Performance.Tests.ps1"
            & "$root\tests\DesktopResults.Tests.ps1"
        }
        filesystem {
            Invoke-BudgetedDotnet restore @('restore', 'tests\OrgExplorer.Persistence.Tests', '--locked-mode')
            Invoke-BudgetedDotnet test @('test', 'tests\OrgExplorer.Persistence.Tests', '-c', 'Release', '--no-restore', '--logger', 'trx',
                '--results-directory', '.ci-results\filesystem', '--filter', 'Category!=Performance')
        }
        desktop {
            $env:DESKTOP_RESULTS_DIRECTORY = $results
            Remove-Item (Join-Path $results 'scenarios.json') -Force -ErrorAction SilentlyContinue
            Invoke-BudgetedDotnet restore-app @('restore', 'src\OrgExplorer.Desktop', '--locked-mode')
            Invoke-BudgetedDotnet restore-tests @('restore', 'tests\OrgExplorer.Desktop.Smoke', '--locked-mode')
            Invoke-BudgetedDotnet build-app @('build', 'src\OrgExplorer.Desktop', '-c', 'Release', '--no-restore')
            Invoke-BudgetedDotnet desktop-flows @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release', '--no-restore',
                '--', (Join-Path $artifactBuild 'bin\OrgExplorer.Desktop\release\OrgExplorer.Desktop.exe'))
            . "$root\eng\DesktopResults.ps1"
            Assert-DesktopResults (Join-Path $results 'scenarios.json') @(Get-Content "$root\tests\DesktopScenarios.json" -Raw | ConvertFrom-Json)
            Invoke-BudgetedDotnet release-isolation @('publish', 'src\OrgExplorer.Desktop', '-c', 'Release', '--no-build', '--no-restore',
                '-o', '.ci-results\desktop\product-check')
            $files = Get-ChildItem (Join-Path $results 'product-check') -Recurse -File
            if ($files | Where-Object { $_.FullName -match '(?i)fixtures|test-workspaces|OrgExplorer\.(Core\.Tests|Persistence\.Tests|Desktop\.Smoke)|ScaleFixture' }) {
                throw 'Fictitious fixtures or test assemblies leaked into the product publish output.'
            }
        }
    }
} finally {
    $elapsed = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
    @{ lane = $Lane; startedAt = $started.ToString('O'); executionSeconds = $elapsed; targetSeconds = 300;
        ceilingSeconds = $ceilingSeconds; steps = @($steps.ToArray()); queuesIncluded = $false; cdIncluded = $false } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $results 'timing.json')
    Write-Host "Lane $Lane execution (setup/restore/build/tests/fixtures): $([Math]::Round($elapsed, 2)) seconds; target 300; ceiling $ceilingSeconds."
    if ($env:GITHUB_STEP_SUMMARY) {
        "### $Lane execution`n$([Math]::Round($elapsed, 2)) seconds including setup, restore, build, tests and fixtures; target 300 s; ceiling $ceilingSeconds s. Queues and later CD excluded." |
            Add-Content $env:GITHUB_STEP_SUMMARY
    }
}
