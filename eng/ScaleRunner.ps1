function Invoke-ScaleSuite(
    [string] $Suite, [string] $Label, [string] $ResultsDirectory, [string] $ExpectedRevision,
    [hashtable] $Selection, [scriptblock] $Verify,
    [ValidateSet(660, 900)][int] $ObservationSeconds = 660
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
    $executionEnvironment = @{
        processors = [Environment]::ProcessorCount
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        powershell = $PSVersionTable.PSVersion.ToString()
        image = $env:ImageOS; imageVersion = $env:ImageVersion
    }
    $state = @{
        started = [DateTimeOffset]::UtcNow; revision = ''; dirty = $false; untracked = $false
        outcome = 'running'; failure = $null; steps = [System.Collections.Generic.List[object]]::new()
        observedOutcome = 'running'; phase = 'setup'; activeCommand = $null; lastProgress = $null
        activePhase = $null; lastActivePhase = $null
    }

    function Write-ScaleReport {
        $report = @{
            suite = $Suite; outcome = if ($state.phase -eq 'finished') { $state.outcome } else { 'running' }
            failure = $state.failure; revision = $state.revision
            expectedRevision = $ExpectedRevision; trackedChanges = $state.dirty; untrackedChanges = $state.untracked
            startedAt = $state.started.ToString('O')
            completedAt = if ($state.phase -eq 'finished') { [DateTimeOffset]::UtcNow.ToString('O') } else { $null }
            executionSeconds = ([DateTimeOffset]::UtcNow - $state.started).TotalSeconds
            ceilingSeconds = $ceilingSeconds; steps = @($state.steps.ToArray())
            runDirectory = [IO.Path]::GetRelativePath($results, $run); queuesIncluded = $false; cdIncluded = $false
            observationSeconds = $ObservationSeconds; diagnosticOnly = $ObservationSeconds -gt $ceilingSeconds
            observedOutcome = $state.observedOutcome; phase = $state.phase; activeCommand = $state.activeCommand
            lastProgress = $state.lastProgress; heartbeatAt = [DateTimeOffset]::UtcNow.ToString('O')
            activePhase = $state.activePhase; lastActivePhase = $state.lastActivePhase
            environment = $executionEnvironment
        }
        foreach ($key in $Selection.Keys) { $report[$key] = $Selection[$key] }
        $json = $report | ConvertTo-Json -Depth 5
        foreach ($path in @((Join-Path $run 'result.json'), (Join-Path $results 'result.json'))) {
            [IO.File]::WriteAllText("$path.writing", $json)
            [IO.File]::Move("$path.writing", $path, $true)
        }
    }

    function Assert-ScaleRemainingBudget([switch] $ForObservation) {
        $limit = if ($ForObservation) { $ObservationSeconds } else { $ceilingSeconds }
        $remaining = $limit - ([DateTimeOffset]::UtcNow - $state.started).TotalSeconds
        if ($remaining -le 0) { throw "$Label verification exhausted its shared execution budget (including setup and cleanup)." }
        return $remaining
    }

    function Read-ScaleProgress([string] $Log) {
        $timingLog = Join-Path $run 'scale-measurements.log'
        $progressLog = if (Test-Path -LiteralPath $timingLog -PathType Leaf) {
            $timingLog
        } else { $Log }
        $active = [Collections.Generic.List[string]]::new()
        foreach ($line in Get-Content -LiteralPath $progressLog) {
            if ($line -match '^(SCALE (BEGIN|END)|START:)') { $state.lastProgress = $line }
            if ($line -match '^SCALE (?<event>BEGIN|END) (?<name>[^;]+)(;|$)') {
                if ($Matches.event -eq 'BEGIN') { $active.Add($Matches.name) }
                else {
                    $index = $active.LastIndexOf($Matches.name)
                    if ($index -ge 0) { $active.RemoveAt($index) }
                }
            }
        }
        $state.activePhase = if ($active.Count) { $active[$active.Count - 1] } else { $null }
        if ($state.activePhase) { $state.lastActivePhase = $state.activePhase }
    }

    function Invoke-ScaleProcess([string] $Name, [string] $FilePath, [string[]] $Arguments) {
        $remaining = Assert-ScaleRemainingBudget -ForObservation
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $log = Join-Path $run "$Name.log"
        $errors = Join-Path $run "$Name-errors.log"
        $quoted = $Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
        $process = $null
        $state.phase = 'command'
        $state.activeCommand = $Name
        Write-ScaleReport
        try {
            $process = Start-Process $FilePath -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errors
            $heartbeat = [Diagnostics.Stopwatch]::StartNew()
            while (!$process.WaitForExit([int][Math]::Max(1, [Math]::Min(1000, $remaining * 1000)))) {
                $remaining = $ObservationSeconds - ([DateTimeOffset]::UtcNow - $state.started).TotalSeconds
                if ($remaining -le 0) {
                    $state.outcome = 'timed_out'
                    throw "$Name exceeded the remaining $Label observation window."
                }
                if ($heartbeat.Elapsed.TotalSeconds -ge 5) {
                    Read-ScaleProgress $log
                    Write-ScaleReport
                    $heartbeat.Restart()
                }
            }
            if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
        } finally {
            if ($null -ne $process) {
                try {
                    if (!$process.HasExited) {
                        $state.phase = 'terminating'
                        try { Write-ScaleReport }
                        finally { $process.Kill($true); $process.WaitForExit() }
                    }
                    $state.steps.Add(@{ name = $Name; seconds = $timer.Elapsed.TotalSeconds; exitCode = $process.ExitCode })
                } finally { $process.Dispose() }
            }
            if (Test-Path $log) { Read-ScaleProgress $log; Get-Content $log }
            if (Test-Path $errors) { Get-Content $errors }
            $state.activeCommand = $null
            $state.activePhase = $null
            $state.phase = 'between-commands'
            Write-ScaleReport
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
        $null = Assert-ScaleRemainingBudget -ForObservation
        Write-Host "$Label`: revision $($state.revision); tracked changes $($state.dirty); shared ceiling $ceilingSeconds s."
        if ($ObservationSeconds -gt $ceilingSeconds) {
            Write-Host "DIAGNOSTIC ONLY: observation window $ObservationSeconds s; the 660-second verification verdict is unchanged and this invocation cannot pass."
        }
        & $Verify $run $build
        $currentRevision = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot recheck the tested commit.' }
        $changed = [bool](& git status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot recheck candidate checkout state.' }
        if ($currentRevision -ne $state.revision -or ($ExpectedRevision -and $changed)) {
            throw "The candidate checkout changed during $Label verification."
        }
        $state.observedOutcome = 'success'
        $state.outcome = if ($ObservationSeconds -gt $ceilingSeconds) { 'diagnostic_complete' } else { 'success' }
        if ($state.outcome -eq 'diagnostic_complete') {
            $state.failure = 'Extended observation is diagnostic only, not passing candidate verification.'
        }
    } catch {
        $state.observedOutcome = if ($state.outcome -eq 'timed_out') { 'timed_out' } else { 'failure' }
        if ($state.outcome -ne 'timed_out') { $state.outcome = 'failure' }
        $state.failure = $_.Exception.Message
    } finally {
        $state.phase = 'cleanup'
        $state.activeCommand = $null
        $state.activePhase = $null
        Write-ScaleReport
        try {
            # Owned commands have exited before their fixture/build tree is removed.
            if (Test-Path $build) { Remove-Item $build -Recurse -Force }
            $null = Assert-ScaleRemainingBudget
        } catch {
            $state.outcome = if (([DateTimeOffset]::UtcNow - $state.started).TotalSeconds -ge $ceilingSeconds) { 'timed_out' } else { 'failure' }
            $state.failure = if ($state.failure) { $state.failure + "`n" + $_.Exception.Message } else { $_.Exception.Message }
            Write-Error -Message $state.failure -ErrorAction Continue
        }
        $elapsed = ([DateTimeOffset]::UtcNow - $state.started).TotalSeconds
        $state.phase = 'finished'
        Write-ScaleReport
        $summary = "$Label $($state.outcome) at $($state.revision) in $([Math]::Round($elapsed, 2)) s including setup, restore/build, fixtures, tests and cleanup; ceiling $ceilingSeconds s. No latency SLA."
        Write-Host $summary
        if ($env:GITHUB_STEP_SUMMARY) {
            "### $Label`: $($state.outcome)`n$summary Queues and publication excluded." | Add-Content $env:GITHUB_STEP_SUMMARY
        }
    }
    if ($state.outcome -ne 'success') { throw "$Label did not succeed: $($state.failure)" }
}
