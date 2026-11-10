param(
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedRevision,
    [string] $ResultsDirectory = '.ci-results\performance'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. "$PSScriptRoot\PerformanceSuite.ps1"
$results = [IO.Path]::GetFullPath($ResultsDirectory, $root)
if (!$results.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Performance results must be inside this checkout.'
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
$env:SCALE_TIMING_FILE = Join-Path $run 'scale-measurements.log'

function Write-PerformanceReport {
    $report = @{
        suite = 'non-ui-performance'; outcome = $outcome; failure = $failure; revision = $revision
        expectedRevision = $ExpectedRevision; trackedChanges = $dirty; untrackedChanges = $untracked; filter = $PerformanceFilter
        requiredTests = $PerformanceTests; startedAt = $started.ToString('O')
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
    if ($remaining -le 0) { throw 'Performance verification exhausted its shared execution budget (including setup and cleanup).' }
    return $remaining
}

function Invoke-PerformanceDotnet([string] $Name, [string[]] $Arguments) {
    $remaining = Assert-RemainingBudget
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $run "$Name.log"
    $errors = Join-Path $run "$Name-errors.log"
    $arguments = @($Arguments[0], '--artifacts-path', $build) + $Arguments[1..($Arguments.Length - 1)]
    $quoted = $arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
    Write-PerformanceReport
    $process = Start-Process dotnet -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errors
    try {
        if (!$process.WaitForExit([int]($remaining * 1000))) {
            $process.Kill($true)
            $process.WaitForExit()
            $script:outcome = 'timed_out'
            throw "$Name exceeded the remaining performance execution budget."
        }
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
    } finally {
        Get-Content $log
        Get-Content $errors
        $steps.Add(@{ name = $Name; seconds = $timer.Elapsed.TotalSeconds; exitCode = $process.ExitCode })
        $process.Dispose()
    }
}

Write-PerformanceReport
try {
    if ($env:SCALE_STARTED_AT) { $started = [DateTimeOffset]::Parse($env:SCALE_STARTED_AT) }
    if ($started -gt [DateTimeOffset]::UtcNow) { throw 'The performance execution start cannot be in the future.' }
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the tested commit.' }
    $dirty = [bool](& git status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify checkout state.' }
    $untracked = [bool](& git ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify untracked checkout state.' }
    Write-PerformanceReport
    if ($ExpectedRevision -and ($revision -ne $ExpectedRevision -or $dirty -or $untracked)) {
        throw 'Pre-publication/candidate verification requires the exact expected revision and a clean checkout, including untracked files.'
    }
    Write-Host "Non-UI performance: revision $revision; tracked changes $dirty; filter $PerformanceFilter; shared ceiling $ceilingSeconds s."
    Invoke-PerformanceDotnet restore @('restore', 'tests\OrgExplorer.Persistence.Tests', '--locked-mode')
    Invoke-PerformanceDotnet test @('test', 'tests\OrgExplorer.Persistence.Tests', '-c', 'Release', '--no-restore',
        '--filter', $PerformanceFilter, '--logger', 'trx', '--results-directory', $run)
    Assert-PerformanceResults $run
    if ((& git rev-parse HEAD).Trim() -ne $revision -or
        ($ExpectedRevision -and [bool](& git status --porcelain))) {
        throw 'The candidate checkout changed during performance verification.'
    }
    $outcome = 'success'
} catch {
    if ($outcome -ne 'timed_out') { $outcome = 'failure' }
    $failure = $_.Exception.Message
    throw
} finally {
    try {
        # The test process (and its watchers) has exited before fixture/build cleanup.
        if (Test-Path $build) { Remove-Item $build -Recurse -Force }
        $null = Assert-RemainingBudget
    } catch {
        $outcome = if (([DateTimeOffset]::UtcNow - $started).TotalSeconds -ge $ceilingSeconds) { 'timed_out' } else { 'failure' }
        $failure = $_.Exception.Message
        Write-Error -Message $failure -ErrorAction Continue
    }
    $elapsed = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
    Write-PerformanceReport
    Write-Host "Non-UI performance $outcome at $revision in $([Math]::Round($elapsed, 2)) s including setup, restore/build, fixtures, tests and cleanup; ceiling $ceilingSeconds s. No latency SLA."
    if ($env:GITHUB_STEP_SUMMARY) {
        "### Non-UI performance: $outcome`nRevision: ``$revision``; $([Math]::Round($elapsed, 2)) s including setup, restore/build, fixtures, tests and cleanup; ceiling $ceilingSeconds s. Queues and publication excluded. No latency SLA." |
            Add-Content $env:GITHUB_STEP_SUMMARY
    }
}
if ($outcome -ne 'success') { throw "Non-UI performance did not succeed: $failure" }
