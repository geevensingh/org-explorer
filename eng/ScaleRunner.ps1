function Invoke-ScaleSuite(
    [string] $Suite, [string] $Label, [string] $ResultsDirectory, [string] $ExpectedRevision,
    [hashtable] $Selection, [scriptblock] $Verify
) {
    $ErrorActionPreference = 'Stop'
    $root = Split-Path $PSScriptRoot -Parent
    Set-Location $root
    $results = [IO.Path]::GetFullPath($ResultsDirectory, $root)
    if (!$results.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label results must be inside this checkout."
    }
    $run = Join-Path $results ("run-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $run -Force | Out-Null
    $build = Join-Path $run 'build'
    $ceilingSeconds = 660
    $state = @{
        started = [DateTimeOffset]::UtcNow; revision = ''; dirty = $false; untracked = $false
        outcome = 'running'; failure = $null; steps = [System.Collections.Generic.List[object]]::new()
    }

    function Write-ScaleReport {
        $report = @{
            suite = $Suite; outcome = $state.outcome; failure = $state.failure; revision = $state.revision
            expectedRevision = $ExpectedRevision; trackedChanges = $state.dirty; untrackedChanges = $state.untracked
            startedAt = $state.started.ToString('O')
            completedAt = if ($state.outcome -eq 'running') { $null } else { [DateTimeOffset]::UtcNow.ToString('O') }
            executionSeconds = ([DateTimeOffset]::UtcNow - $state.started).TotalSeconds
            ceilingSeconds = $ceilingSeconds; steps = @($state.steps.ToArray())
            runDirectory = [IO.Path]::GetRelativePath($results, $run); queuesIncluded = $false; cdIncluded = $false
        }
        foreach ($key in $Selection.Keys) { $report[$key] = $Selection[$key] }
        $json = $report | ConvertTo-Json -Depth 5
        $json | Set-Content (Join-Path $run 'result.json')
        $json | Set-Content (Join-Path $results 'result.json')
    }

    function Assert-ScaleRemainingBudget {
        $remaining = $ceilingSeconds - ([DateTimeOffset]::UtcNow - $state.started).TotalSeconds
        if ($remaining -le 0) { throw "$Label verification exhausted its shared execution budget (including setup and cleanup)." }
        return $remaining
    }

    function Invoke-ScaleProcess([string] $Name, [string] $FilePath, [string[]] $Arguments) {
        $remaining = Assert-ScaleRemainingBudget
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $log = Join-Path $run "$Name.log"
        $errors = Join-Path $run "$Name-errors.log"
        $quoted = $Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
        $process = $null
        Write-ScaleReport
        try {
            $process = Start-Process $FilePath -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errors
            if (!$process.WaitForExit([int]($remaining * 1000))) {
                $state.outcome = 'timed_out'
                throw "$Name exceeded the remaining $Label execution budget."
            }
            if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
        } finally {
            if ($null -ne $process) {
                try {
                    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $state.steps.Add(@{ name = $Name; seconds = $timer.Elapsed.TotalSeconds; exitCode = $process.ExitCode })
                } finally { $process.Dispose() }
            }
            if (Test-Path $log) { Get-Content $log }
            if (Test-Path $errors) { Get-Content $errors }
        }
    }

    function Invoke-ScaleDotnet([string] $Name, [string[]] $Arguments) {
        $arguments = @($Arguments[0], '--artifacts-path', $build) + $Arguments[1..($Arguments.Length - 1)]
        Invoke-ScaleProcess $Name dotnet $arguments
    }

    Write-ScaleReport
    try {
        if ($env:SCALE_STARTED_AT) { $state.started = [DateTimeOffset]::Parse($env:SCALE_STARTED_AT) }
        if ($state.started -gt [DateTimeOffset]::UtcNow) { throw "The $Label execution start cannot be in the future." }
        $state.revision = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $state.revision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the tested commit.' }
        $state.dirty = [bool](& git status --porcelain --untracked-files=no)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot verify checkout state.' }
        $state.untracked = [bool](& git ls-files --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot verify untracked checkout state.' }
        Write-ScaleReport
        if ($ExpectedRevision -and ($state.revision -ne $ExpectedRevision -or $state.dirty -or $state.untracked)) {
            throw 'Pre-publication/candidate verification requires the exact expected revision and a clean checkout, including untracked files.'
        }
        $null = Assert-ScaleRemainingBudget
        Write-Host "$Label`: revision $($state.revision); tracked changes $($state.dirty); shared ceiling $ceilingSeconds s."
        & $Verify $run $build
        $currentRevision = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot recheck the tested commit.' }
        $changed = [bool](& git status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot recheck candidate checkout state.' }
        if ($currentRevision -ne $state.revision -or ($ExpectedRevision -and $changed)) {
            throw "The candidate checkout changed during $Label verification."
        }
        $state.outcome = 'success'
    } catch {
        if ($state.outcome -ne 'timed_out') { $state.outcome = 'failure' }
        $state.failure = $_.Exception.Message
    } finally {
        try {
            # Owned commands have exited before their fixture/build tree is removed.
            if (Test-Path $build) { Remove-Item $build -Recurse -Force }
            $null = Assert-ScaleRemainingBudget
        } catch {
            $state.outcome = if (([DateTimeOffset]::UtcNow - $state.started).TotalSeconds -ge $ceilingSeconds) { 'timed_out' } else { 'failure' }
            $state.failure = $_.Exception.Message
            Write-Error -Message $state.failure -ErrorAction Continue
        }
        $elapsed = ([DateTimeOffset]::UtcNow - $state.started).TotalSeconds
        Write-ScaleReport
        $summary = "$Label $($state.outcome) at $($state.revision) in $([Math]::Round($elapsed, 2)) s including setup, restore/build, fixtures, tests and cleanup; ceiling $ceilingSeconds s. No latency SLA."
        Write-Host $summary
        if ($env:GITHUB_STEP_SUMMARY) {
            "### $Label`: $($state.outcome)`n$summary Queues and publication excluded." | Add-Content $env:GITHUB_STEP_SUMMARY
        }
    }
    if ($state.outcome -ne 'success') { throw "$Label did not succeed: $($state.failure)" }
}
