param([string]$GateName, [string]$CompanionPath, [string]$ParametersJson, [int]$GateWaitMilliseconds)
if ($GateName) {
    $ErrorActionPreference = 'Stop'
    $gate = [Threading.EventWaitHandle]::OpenExisting($GateName)
    try {
        if (!$gate.WaitOne($GateWaitMilliseconds)) { throw 'Core companion ownership admission exceeded the remaining budget.' }
    } finally { $gate.Dispose() }
    $companionParameters = ConvertFrom-Json -InputObject $ParametersJson -AsHashtable
    & $CompanionPath @companionParameters
    exit 0
}

function Get-CoreContractManifest([string]$ArtifactBuild) {
    foreach ($name in @('CiGate.Tests.ps1', 'CiRunner.Tests.ps1', 'FilesystemHealth.Tests.ps1',
        'LaneHealth.Tests.ps1', 'MixedHealth.Tests.ps1', 'ImplementationBaseline.Tests.ps1',
        'DocumentationHandoff.Tests.ps1', 'DraftDiscard.Tests.ps1', 'Performance.Tests.ps1',
        'HostedCleanup.Tests.ps1', 'NonUiCohort.Tests.ps1', 'DesktopResults.Tests.ps1', 'UiScale.Tests.ps1',
        'Release.Tests.ps1', 'Release.Gates.Tests.ps1', 'Release.Publication.Tests.ps1',
        'Release.Renewal.Tests.ps1', 'Release.Workflow.Tests.ps1', 'Release.Payload.Tests.ps1',
        'Update.TrustLifetime.Tests.ps1', 'ApplicationIcon.Tests.ps1')) {
        $parameters = if ($name -ceq 'DraftDiscard.Tests.ps1') {
            @{ Assembly = Join-Path $ArtifactBuild 'bin\OrgExplorer.Core.Tests\release\OrgExplorer.Core.Tests.dll' }
        } else { @{} }
        @{ name = $name; parameters = $parameters }
    }
}

function Get-CoreContractConfiguration {
    return @{ scheduling = 'bounded-process-queue-v1'; maxParallelism = [Math]::Min(4, [Environment]::ProcessorCount)
        manifest = 'core-contracts-v1'; process = 'pwsh-no-profile'; ownership = 'windows-job-admission-v1'
        reporting = 'per-companion-github-files-v1' }
}

function New-CoreContractProcessScope {
    if (!('OrgExplorer.Ci.CoreContractProcessScope' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace OrgExplorer.Ci {
    public sealed class CoreContractProcessScope : IDisposable {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int kind, IntPtr information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
        IntPtr job;
        public CoreContractProcessScope() {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Core companion job creation failed.");
            var limits = new ExtendedLimits();
            limits.Basic.Flags = 0x2000; // Kill only this job's descendants when its owning handle closes.
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimits>());
            try {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(job, 9, buffer, (uint)Marshal.SizeOf<ExtendedLimits>())) {
                    int error = Marshal.GetLastWin32Error();
                    Dispose();
                    throw new Win32Exception(error, "Core companion job limits failed.");
                }
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Own(Process process) {
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Core companion ownership failed.");
        }
        public void Dispose() {
            if (job == IntPtr.Zero) return;
            if (!CloseHandle(job)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Core companion job closure failed.");
            job = IntPtr.Zero;
        }
    }
}
'@
    }
    return [OrgExplorer.Ci.CoreContractProcessScope]::new()
}

function Invoke-CoreContracts([string]$Root, [string]$Results, [string]$ArtifactBuild,
    [DateTimeOffset]$Started, [double]$CeilingSeconds, $StepResults) {
    $manifest = @(Get-CoreContractManifest $ArtifactBuild)
    $limit = (Get-CoreContractConfiguration).maxParallelism
    $active = [Collections.Generic.List[object]]::new()
    $records = @($manifest | ForEach-Object {
        @{ name = $_.name; status = 'NotStarted'; outcome = 'not_started'; seconds = 0
            startedAt = $null; finishedAt = $null; exitCode = $null; processId = $null
            stdout = $null; stderr = $null; reporting = @{}; error = $null }
    })
    $next = 0
    $expired = $false
    $schedulerFailure = $null
    try {
        while ($next -lt $manifest.Count -or $active.Count) {
            if (([DateTimeOffset]::UtcNow - $Started).TotalSeconds -ge $CeilingSeconds) {
                $expired = $true
                break
            }
            while ($next -lt $manifest.Count -and $active.Count -lt $limit) {
                if (([DateTimeOffset]::UtcNow - $Started).TotalSeconds -ge $CeilingSeconds) {
                    $expired = $true
                    break
                }
                $entry = $manifest[$next]
                $record = $records[$next]
                $record.startedAt = [DateTimeOffset]::UtcNow.ToString('O')
                $timer = [Diagnostics.Stopwatch]::StartNew()
                $record.stdout = "$($next + 2)-$($entry.name).log"
                $record.stderr = "$($next + 2)-$($entry.name)-errors.log"
                $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -CommandType Application).Source)
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                $start.WorkingDirectory = $Root
                foreach ($channel in @('GITHUB_STEP_SUMMARY', 'GITHUB_OUTPUT', 'GITHUB_ENV')) {
                    $path = "$($next + 2)-$($entry.name)-$channel.log"
                    $record.reporting[$channel] = $path
                    $destination = Join-Path $Results $path
                    [IO.File]::WriteAllText($destination, '')
                    $start.Environment[$channel] = $destination
                }
                $gateName = 'Local\OrgExplorer.CoreContract.' + [guid]::NewGuid().ToString('N')
                $gate = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::AutoReset, $gateName)
                $remaining = $CeilingSeconds - ([DateTimeOffset]::UtcNow - $Started).TotalSeconds
                foreach ($argument in @('-NoProfile', '-File', (Join-Path $Root 'eng\CoreContracts.ps1'),
                    '-GateName', $gateName, '-CompanionPath', (Join-Path $Root "tests\$($entry.name)"),
                    '-ParametersJson', (ConvertTo-Json -InputObject $entry.parameters -Compress),
                    '-GateWaitMilliseconds', [string][Math]::Max(1, [int]($remaining * 1000)))) {
                    $start.ArgumentList.Add($argument)
                }
                $scope = $null
                $process = $null
                try {
                    $scope = New-CoreContractProcessScope
                    $process = [Diagnostics.Process]::Start($start)
                    $record.processId = $process.Id
                    $worker = @{ process = $process; scope = $scope; gate = $gate; record = $record; timer = $timer
                        output = $process.StandardOutput.ReadToEndAsync(); errors = $process.StandardError.ReadToEndAsync() }
                    $active.Add($worker)
                    $gate = $null
                    $scope.Own($process)
                    $null = $worker.gate.Set()
                } catch {
                    $record.status = 'Failed'; $record.outcome = 'failure'
                    $record.error = "Companion launch failed: $($_.Exception.Message)"
                    $record.finishedAt = [DateTimeOffset]::UtcNow.ToString('O')
                    $record.seconds = $timer.Elapsed.TotalSeconds
                    if (!$process -and $scope) { $scope.Dispose() }
                    throw
                } finally { if ($gate) { $gate.Dispose() } }
                $next++
            }
            foreach ($worker in @($active.ToArray())) {
                if (!$worker.process.HasExited -or !$worker.output.IsCompleted -or !$worker.errors.IsCompleted) { continue }
                $worker.scope.Dispose()
                $worker.gate.Dispose()
                $worker.record.exitCode = $worker.process.ExitCode
                $worker.record.status = if ($worker.process.ExitCode -eq 0) { 'Passed' } else { 'Failed' }
                $worker.record.outcome = if ($worker.process.ExitCode -eq 0) { 'success' } else { 'failure' }
                $worker.record.finishedAt = [DateTimeOffset]::UtcNow.ToString('O')
                $worker.record.seconds = $worker.timer.Elapsed.TotalSeconds
                if ($worker.process.ExitCode -ne 0) {
                    $worker.record.error = "$($worker.record.name) failed with exit code $($worker.process.ExitCode)."
                }
                [IO.File]::WriteAllText((Join-Path $Results $worker.record.stdout), $worker.output.GetAwaiter().GetResult())
                [IO.File]::WriteAllText((Join-Path $Results $worker.record.stderr), $worker.errors.GetAwaiter().GetResult())
                $worker.process.Dispose()
                $null = $active.Remove($worker)
            }
            if ($expired) { break }
            if ($active.Count) { Start-Sleep -Milliseconds 20 }
        }
    } catch { $schedulerFailure = $_.Exception.Message }
    finally {
        foreach ($worker in @($active.ToArray())) {
            $record = $worker.record
            try {
                $worker.scope.Dispose()
                $worker.gate.Dispose()
                if (!$worker.process.HasExited) {
                    $worker.process.Kill($true)
                    if (!$worker.process.WaitForExit(5000)) { throw 'Owned companion did not exit after process-tree termination.' }
                }
                $record.exitCode = $worker.process.ExitCode
                if (!$worker.output.Wait(5000) -or !$worker.errors.Wait(5000)) {
                    throw 'Owned companion output did not close after termination.'
                }
                [IO.File]::WriteAllText((Join-Path $Results $record.stdout), $worker.output.GetAwaiter().GetResult())
                [IO.File]::WriteAllText((Join-Path $Results $record.stderr), $worker.errors.GetAwaiter().GetResult())
            } catch {
                $cleanupFailure = "Owned companion cleanup/reporting failed: $($_.Exception.Message)"
                $record.error = if ($record.error) { "$($record.error)`n$cleanupFailure" } else { $cleanupFailure }
                $schedulerFailure = if ($schedulerFailure) { "$schedulerFailure`n$cleanupFailure" } else { $cleanupFailure }
            } finally {
                $record.status = 'Failed'
                $record.outcome = if ($expired) { 'timed_out' } else { 'failure' }
                if (!$record.error) {
                    $record.error = if ($expired) { 'The lane budget (including setup) is exhausted.' } else { $schedulerFailure }
                }
                $record.finishedAt = [DateTimeOffset]::UtcNow.ToString('O')
                $record.seconds = $worker.timer.Elapsed.TotalSeconds
                $worker.process.Dispose()
            }
        }
        foreach ($record in $records) {
            if ($record.status -ceq 'NotStarted') {
                $record.error = if ($expired) { 'The lane budget (including setup) is exhausted before launch.' } else { $schedulerFailure }
            }
            $StepResults.Add($record)
        }
    }
    foreach ($record in $records) {
        foreach ($log in @($record.stdout, $record.stderr)) {
            if ($log -and (Test-Path -LiteralPath (Join-Path $Results $log) -PathType Leaf)) {
                Get-Content -LiteralPath (Join-Path $Results $log)
            }
        }
    }
    if ($schedulerFailure) { throw $schedulerFailure }
    if ($expired) { throw 'The lane budget (including setup) is exhausted.' }
    $failed = @($records | Where-Object { $_.status -cne 'Passed' })
    if ($failed.Count) { throw (($failed | ForEach-Object { $_.error }) -join "`n") }
}
