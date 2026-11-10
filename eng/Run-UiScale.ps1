param(
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedRevision,
    [string] $ResultsDirectory = '.ci-results\ui-scale'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. "$PSScriptRoot\DesktopResults.ps1"
$required = @(Get-Content "$root\tests\UiScaleScenarios.json" -Raw | ConvertFrom-Json)
$results = [IO.Path]::GetFullPath($ResultsDirectory, $root)
if (!$results.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'UI-scale results must be inside this checkout.'
}
$run = Join-Path $results ("run-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $run -Force | Out-Null
$build = Join-Path $run 'build'
$started = [DateTimeOffset]::UtcNow
$ceilingSeconds = 660
$revision = ''
$dirty = $false
$untracked = $false
$outcome = 'running'
$failure = $null
$steps = [System.Collections.Generic.List[object]]::new()
$env:DESKTOP_RESULTS_DIRECTORY = $run

function Write-UiScaleReport {
    $report = @{
        suite = 'ui-scale'; outcome = $outcome; failure = $failure; revision = $revision
        expectedRevision = $ExpectedRevision; trackedChanges = $dirty; untrackedChanges = $untracked
        selector = '--ui-scale'; requiredScenarios = $required; startedAt = $started.ToString('O')
        completedAt = if ($outcome -eq 'running') { $null } else { [DateTimeOffset]::UtcNow.ToString('O') }
        executionSeconds = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
        ceilingSeconds = $ceilingSeconds; steps = @($steps.ToArray())
        runDirectory = [IO.Path]::GetRelativePath($results, $run); queuesIncluded = $false; cdIncluded = $false
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $run 'result.json')
    $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $results 'result.json')
}

function Assert-RemainingBudget {
    $remaining = $ceilingSeconds - ([DateTimeOffset]::UtcNow - $started).TotalSeconds
    if ($remaining -le 0) { throw 'UI-scale verification exhausted its shared execution budget (including setup and cleanup).' }
    return $remaining
}

function Invoke-UiScaleDotnet([string] $Name, [string[]] $Arguments) {
    $remaining = Assert-RemainingBudget
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $run "$Name.log"
    $errors = Join-Path $run "$Name-errors.log"
    $arguments = @($Arguments[0], '--artifacts-path', $build) + $Arguments[1..($Arguments.Length - 1)]
    $quoted = $arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
    Write-UiScaleReport
    $process = Start-Process dotnet -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errors
    try {
        if (!$process.WaitForExit([int]($remaining * 1000))) {
            $process.Kill($true)
            $process.WaitForExit()
            $script:outcome = 'timed_out'
            throw "$Name exceeded the remaining UI-scale execution budget."
        }
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
    } finally {
        Get-Content $log
        Get-Content $errors
        $steps.Add(@{ name = $Name; seconds = $timer.Elapsed.TotalSeconds; exitCode = $process.ExitCode })
        $process.Dispose()
    }
}

Write-UiScaleReport
try {
    if ($env:SCALE_STARTED_AT) { $started = [DateTimeOffset]::Parse($env:SCALE_STARTED_AT) }
    if ($started -gt [DateTimeOffset]::UtcNow) { throw 'The UI-scale execution start cannot be in the future.' }
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the tested commit.' }
    $dirty = [bool](& git status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify checkout state.' }
    $untracked = [bool](& git ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify untracked checkout state.' }
    Write-UiScaleReport
    if ($ExpectedRevision -and ($revision -ne $ExpectedRevision -or $dirty -or $untracked)) {
        throw 'Pre-publication/candidate verification requires the exact expected revision and a clean checkout, including untracked files.'
    }
    $null = Assert-RemainingBudget
    if (![Environment]::UserInteractive) {
        throw 'UI scale requires an interactive, unlocked hosted or dedicated isolated Windows desktop. No rendering checks may be skipped.'
    }
    Write-Host "UI scale: revision $revision; tracked changes $dirty; selector --ui-scale; shared ceiling $ceilingSeconds s."
    Invoke-UiScaleDotnet restore-app @('restore', 'src\OrgExplorer.Desktop', '--locked-mode')
    Invoke-UiScaleDotnet restore-tests @('restore', 'tests\OrgExplorer.Desktop.Smoke', '--locked-mode')
    Invoke-UiScaleDotnet build-app @('build', 'src\OrgExplorer.Desktop', '-c', 'Release', '--no-restore')
    Invoke-UiScaleDotnet ui-scale @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release', '--no-restore',
        '--', (Join-Path $build 'bin\OrgExplorer.Desktop\release\OrgExplorer.Desktop.exe'), '--ui-scale')
    Assert-DesktopResults (Join-Path $run 'scenarios.json') $required
    if ((& git rev-parse HEAD).Trim() -ne $revision -or
        ($ExpectedRevision -and [bool](& git status --porcelain))) {
        throw 'The candidate checkout changed during UI-scale verification.'
    }
    $outcome = 'success'
} catch {
    if ($outcome -ne 'timed_out') { $outcome = 'failure' }
    $failure = $_.Exception.Message
    throw
} finally {
    try {
        # The harness has stopped its apps and workflows before build cleanup.
        if (Test-Path $build) { Remove-Item $build -Recurse -Force }
        $null = Assert-RemainingBudget
    } catch {
        $outcome = if (([DateTimeOffset]::UtcNow - $started).TotalSeconds -ge $ceilingSeconds) { 'timed_out' } else { 'failure' }
        $failure = $_.Exception.Message
        Write-Error -Message $failure -ErrorAction Continue
    }
    $elapsed = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
    Write-UiScaleReport
    Write-Host "UI scale $outcome at $revision in $([Math]::Round($elapsed, 2)) s including setup, restore/build, fixtures, tests and cleanup; ceiling $ceilingSeconds s. No latency SLA."
    if ($env:GITHUB_STEP_SUMMARY) {
        "### UI scale: $outcome`nRevision: ``$revision``; $([Math]::Round($elapsed, 2)) s including setup, restore/build, fixtures, tests and cleanup; ceiling $ceilingSeconds s. Queues and publication excluded. No latency SLA." |
            Add-Content $env:GITHUB_STEP_SUMMARY
    }
}
if ($outcome -ne 'success') { throw "UI scale did not succeed: $failure" }
