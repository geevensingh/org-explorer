param([string] $RequestPath, [string] $CleanupRoot)
$ErrorActionPreference = 'Stop'

function Invoke-TempCpuWaitSession(
    [hashtable] $Request, [scriptblock] $Tool, [scriptblock] $AwaitStop, [scriptblock] $Ready
) {
    $directory = $Request.directory
    $instance = $Request.instance
    if ($instance -cnotmatch '^OrgExplorerCpuWait_[0-9a-f]{32}$') { throw 'Invalid owned CPU/wait instance.' }
    $etl = Join-Path $directory 'cpu-wait.etl'
    $state = [ordered]@{
        diagnosticOnly = $true; acceptanceEligible = $false; instance = $instance
        request = $Request; status = 'preflight'; startAttempted = $false; stopped = $false
        failure = $null; cleanupFailure = $null; startedQpc = $null; stoppedQpc = $null
        frequency = [Diagnostics.Stopwatch]::Frequency; highResolution = [Diagnostics.Stopwatch]::IsHighResolution
        qpcSource = 'Windows Stopwatch/QPC; ETL ReservedFlags/PerfFreq and timestamps still require analysis, never wall-clock offset guessing.'
        configuredKernelBufferBytes = 134217728; configuredClrBufferBytes = 33554432
        outputGuardBytes = 536870912; outputGuardIsPolledNotHardCap = $true; possibleOvershoot = $true
        retention = 'Circular; no whole-phase/all42 retention guarantee. Verify exact request timestamps remain in ETL.'
        stackAndClockEvidence = 'UNRESOLVED: require actual CPU/CSwitch/ReadyThread stacks for BOTH PIDs, CLR rundown, ETL QPC frequency/clock, loss counters and retained-time coverage. Keyword enablement/stop success is not usable wait evidence.'
        lossCounters = $null; retainedTimeCoverage = $null; resolvedStacks = $false
        observedLossStatus = [Collections.Generic.List[object]]::new()
        symbols = 'No symbol download or generation; public Microsoft/.NET resolution is a separate analysis prerequisite.'
        commands = [Collections.Generic.List[object]]::new()
    }
    function Save-CpuWaitState {
        [IO.File]::WriteAllText((Join-Path $directory 'cpu-wait-session.json'), ($state | ConvertTo-Json -Depth 10))
    }
    function Invoke-OwnedWpr([string] $Label, [string[]] $Arguments, [int] $Timeout = 15) {
        $arguments = @($Arguments) + @('-instancename', $instance)
        $command = @{ label = $Label; arguments = $arguments; exitCode = $null; failure = $null }
        $state.commands.Add($command)
        Save-CpuWaitState
        try {
            $result = & $Tool $Label $arguments $Timeout
            $command.exitCode = $result.exitCode
            $command.elapsedSeconds = $result.elapsedSeconds
            $command.maximumObservedOutputBytes = $result.maximumObservedOutputBytes
            if ($Label -in @('ready-status', 'before-stop-status')) {
                foreach ($match in [regex]::Matches($result.output,
                    '(?im)^\s*(?<name>Events Lost|Dropped Events?|Buffers Lost)\s*:\s*(?<value>\d+)')) {
                    $state.observedLossStatus.Add(@{ source = $Label; name = $match.Groups['name'].Value
                        value = [long]$match.Groups['value'].Value })
                }
            }
            return $result
        } catch { $command.failure = $_.Exception.Message; throw }
        finally { Save-CpuWaitState }
    }
    Save-CpuWaitState
    try {
        $before = Invoke-OwnedWpr 'before-start' @('-status')
        if ($before.exitCode -eq 0 -or $before.output -notmatch '0xc5583000') {
            throw 'Cannot prove the unique instance is absent; refusing to touch a possibly foreign recording.'
        }
        $state.startAttempted = $true
        Save-CpuWaitState
        $start = Invoke-OwnedWpr 'start' @('-start', ($Request.profile + '!OrgExplorerCpuWait.Verbose'),
            '-recordtempto', $directory)
        if ($start.exitCode -ne 0) { throw "Owned CPU/wait start failed: $($start.exitCode)." }
        $status = Invoke-OwnedWpr 'ready-status' @('-status', 'profiles', 'collectors', '-details')
        if ($status.exitCode -ne 0 -or !$status.output.Contains($instance) -or
            $status.output -notmatch '(?i)Memory') { throw 'Owned CPU/wait readiness/instance/memory mode not established.' }
        foreach ($stack in @('SampledProfile', 'CSwitch', 'ReadyThread')) {
            if ($status.output -notmatch "(?s)System Stacks.*\b$stack\b") {
                throw "Required $stack stacks absent from owned collector status."
            }
        }
        foreach ($provider in @('Microsoft-Windows-DotNETRuntime', 'Microsoft-Windows-DotNETRuntimeRundown')) {
            if (!$status.output.Contains($provider)) { throw "Required CLR loader/JIT/rundown support absent: $provider." }
        }
        $state.status = 'recording'
        $state.startedQpc = [Diagnostics.Stopwatch]::GetTimestamp()
        Save-CpuWaitState
        & $Ready
        $reason = & $AwaitStop
        $state.status = $reason
        if ($reason -cne 'phase-finished') { throw "CPU/wait window censored: $reason." }
    } catch { $state.failure = $_.Exception.Message }
    finally {
        if ($state.startAttempted) {
            try {
                try {
                    $status = Invoke-OwnedWpr 'before-stop-status' @('-status', 'profiles', 'collectors', '-details')
                    if ($status.exitCode -ne 0) { throw 'Pre-stop collector status unavailable.' }
                } catch {
                    if (!$state.failure) { $state.failure = $_.Exception.Message }
                }
                $stop = Invoke-OwnedWpr 'stop' @('-stop', $etl, 'TEMP nonaccepting normal-note phase', '-skipPdbGen') 45
                $state.stoppedQpc = [Diagnostics.Stopwatch]::GetTimestamp()
                if ($stop.exitCode -ne 0 -or !(Test-Path -LiteralPath $etl -PathType Leaf)) {
                    throw "Owned CPU/wait stop failed or ETL missing: $($stop.exitCode)."
                }
                $state.stopped = $true
                $state.etlBytes = (Get-Item -LiteralPath $etl).Length
                $state.etlSha256 = (Get-FileHash -LiteralPath $etl -Algorithm SHA256).Hash
                if ($state.etlBytes -gt $state.outputGuardBytes) { throw 'CPU/wait ETL exceeded the polled output guard.' }
                if (@($state.observedLossStatus | Where-Object { $_.value -gt 0 }).Count) {
                    throw 'CPU/wait status reports lost events/buffers; raw trace retained as missing evidence.'
                }
            } catch {
                $state.cleanupFailure = $_.Exception.Message
                try {
                    $cancel = Invoke-OwnedWpr 'cancel-owned-after-stop-failure' @('-cancel') 10
                    if ($cancel.exitCode -ne 0) { $state.cleanupFailure += "; named cancel exit=$($cancel.exitCode)" }
                } catch { $state.cleanupFailure += "; named cancel: $($_.Exception.Message)" }
            }
        }
        $state.status = if ($state.failure -or $state.cleanupFailure) { 'failed-or-incomplete' } else { 'saved-needs-stack-loss-clock-coverage-analysis' }
        Save-CpuWaitState
    }
    if ($state.failure -or $state.cleanupFailure) { throw "CPU/wait diagnostic incomplete: $($state.failure); $($state.cleanupFailure)" }
}

function Invoke-TempCpuWaitTool([string] $Label, [string[]] $Arguments, [int] $Timeout,
    [string] $Executable = $script:CpuWaitTool) {
    $remaining = ($script:CpuWaitDeadline - [DateTimeOffset]::UtcNow).TotalSeconds
    if ($remaining -le 0) { throw 'No remaining original660 budget for owned CPU/wait command.' }
    $timeout = [Math]::Min($Timeout, [Math]::Max(1, [int]$remaining))
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $stderr = $null
    $peakBytes = 0L
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (!$process.WaitForExit(100)) {
            $bytes = (Get-ChildItem -LiteralPath $script:CpuWaitDirectory -File -Recurse |
                Measure-Object -Property Length -Sum).Sum
            $peakBytes = [Math]::Max($peakBytes, [long]$bytes)
            if ($watch.Elapsed.TotalSeconds -ge $timeout -or [DateTimeOffset]::UtcNow -ge $script:CpuWaitDeadline -or
                $bytes -gt 536870912) {
                $process.Kill($true)
                if (!$process.WaitForExit(2000)) { throw 'Exact owned WPR child failed to exit after termination.' }
                throw 'Owned WPR command timed out or exceeded polled512MiB output guard (overshoot possible).'
            }
        }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        return @{ exitCode = $process.ExitCode; output = $text
            elapsedSeconds = $watch.Elapsed.TotalSeconds; maximumObservedOutputBytes = $peakBytes }
    } finally {
        try {
            if ($stdout -and $stderr -and $process.HasExited) {
                if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 2000)) {
                    throw 'Owned tool output did not close within its bounded drain window.'
                }
                [IO.File]::WriteAllText((Join-Path $script:CpuWaitDirectory "$Label.log"),
                    ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()))
            }
        } finally { $process.Dispose() }
    }
}

if ($RequestPath) {
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json -AsHashtable
    $script:CpuWaitDirectory = $request.directory
    $script:CpuWaitDeadline = [DateTimeOffset]::Parse($request.budgetDeadline)
    $script:CpuWaitTool = Join-Path $env:SystemRoot 'System32\wpr.exe'
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
        $env:RUNNER_OS -cne 'Windows' -or ![Environment]::Is64BitProcess -or
        ![Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator) -or !(Test-Path -LiteralPath $script:CpuWaitTool)) {
        throw 'CPU/wait recording requires elevated64-bit disposable GitHub-hosted Windows with inbox WPR; no fallback/install.'
    }
    $tool = Get-Item -LiteralPath $script:CpuWaitTool
    $existing = Invoke-TempCpuWaitTool 'existing-session-names' @('query', '-ets') 10 `
        (Join-Path $env:SystemRoot 'System32\logman.exe')
    $foreign = Select-String -LiteralPath (Join-Path $request.directory 'existing-session-names.log') `
        -Pattern 'WPR_initiated_|NT Kernel Logger|Circular Kernel Context Logger' -Quiet
    if ($existing.exitCode -ne 0 -or $foreign) {
        throw 'Foreign or uninspectable kernel/WPR recording present; no start, stop or cancel attempted.'
    }
    [IO.File]::WriteAllText((Join-Path $request.directory 'cpu-wait-tool.json'), (@{
        diagnosticOnly = $true; acceptanceEligible = $false; path = $tool.FullName
        version = $tool.VersionInfo.FileVersion; sha256 = (Get-FileHash $tool.FullName -Algorithm SHA256).Hash
        profileSha256 = (Get-FileHash $request.profile -Algorithm SHA256).Hash
    } | ConvertTo-Json))
    $advanced = Invoke-TempCpuWaitTool 'help-advanced' @('-help', 'advanced') 10
    $details = Invoke-TempCpuWaitTool 'profile-details' @('-profiledetails', ($request.profile + '!OrgExplorerCpuWait.Verbose')) 10
    if ($advanced.exitCode -ne 0 -or $advanced.output -notmatch '(?i)instancename' -or
        $details.exitCode -ne 0 -or $details.output -notmatch '1024' -or $details.output -notmatch '256') {
        throw 'Required instance/profile/buffer support not available; no recording started.'
    }
    $readySignal = [Threading.EventWaitHandle]::OpenExisting($request.readyEvent)
    $stopSignal = [Threading.EventWaitHandle]::OpenExisting($request.stopEvent)
    try {
        Invoke-TempCpuWaitSession $request ${function:Invoke-TempCpuWaitTool} {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $owner = [Diagnostics.Process]::GetProcessById($request.harnessPid)
            try {
                while (!$stopSignal.WaitOne(100)) {
                    if ($owner.HasExited) { return 'owner-exited' }
                    if ($watch.Elapsed.TotalSeconds -ge $request.maximumCaptureSeconds -or
                        [DateTimeOffset]::UtcNow -ge $script:CpuWaitDeadline.AddSeconds(-60)) { return 'deadline' }
                }
                return 'phase-finished'
            } finally { $owner.Dispose() }
        } { $null = $readySignal.Set() }
    } finally { $readySignal.Dispose(); $stopSignal.Dispose() }
}
if ($CleanupRoot) {
    $requests = @(Get-ChildItem -LiteralPath $CleanupRoot -Filter cpu-wait-request.json -File -Recurse -ErrorAction SilentlyContinue)
    if ($requests.Count -gt 1) { throw 'More than one CPU/wait recording request; refusing ambiguous cleanup.' }
    foreach ($file in $requests) {
        $request = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $statePath = Join-Path $request.directory 'cpu-wait-session.json'
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if (!$state.stopped -and $state.startAttempted -and
            $request.instance -cmatch '^OrgExplorerCpuWait_[0-9a-f]{32}$' -and
            $state.instance -ceq $request.instance) {
            $script:CpuWaitDirectory = $request.directory
            $script:CpuWaitDeadline = [DateTimeOffset]::Parse($request.budgetDeadline)
            $script:CpuWaitTool = Join-Path $env:SystemRoot 'System32\wpr.exe'
            [IO.File]::WriteAllText((Join-Path $request.directory 'cpu-wait-emergency.json'), (@{
                diagnosticOnly = $true; acceptanceEligible = $false; instance = $request.instance
                reason = 'Owner interrupted or stop incomplete; this is censored evidence, not a second recording.'
                qpc = [Diagnostics.Stopwatch]::GetTimestamp(); frequency = [Diagnostics.Stopwatch]::Frequency
            } | ConvertTo-Json))
            $result = Invoke-TempCpuWaitTool 'outer-finally-stop-owned' @('-stop',
                (Join-Path $request.directory 'cpu-wait-emergency.etl'), 'TEMP interrupted owner', '-skipPdbGen',
                '-instancename', $request.instance) 30
            if ($result.exitCode -ne 0) {
                $cancel = Invoke-TempCpuWaitTool 'outer-finally-cancel-owned' @('-cancel', '-instancename', $request.instance) 10
                throw "Interrupted owned recording incomplete; stop=$($result.exitCode); cancel=$($cancel.exitCode)."
            }
        }
    }
}
