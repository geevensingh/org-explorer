$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.ci-results\core contracts ' + [guid]::NewGuid().ToString('N'))
$names = @('CiGate.Tests.ps1', 'CiRunner.Tests.ps1', 'FilesystemHealth.Tests.ps1',
    'LaneHealth.Tests.ps1', 'MixedHealth.Tests.ps1', 'ImplementationBaseline.Tests.ps1',
    'DocumentationHandoff.Tests.ps1', 'DraftDiscard.Tests.ps1', 'Performance.Tests.ps1',
    'HostedCleanup.Tests.ps1', 'NonUiCohort.Tests.ps1', 'DesktopResults.Tests.ps1', 'UiScale.Tests.ps1',
    'Release.Tests.ps1', 'Release.Gates.Tests.ps1', 'Release.Publication.Tests.ps1',
    'Release.Renewal.Tests.ps1', 'Release.Workflow.Tests.ps1', 'Release.Payload.Tests.ps1',
    'Update.TrustLifetime.Tests.ps1', 'ApplicationIcon.Tests.ps1')
function Assert([bool]$Condition, [string]$Reason) {
    if (!$Condition) { throw $Reason }
}
function Invoke-ControlledCore([string]$Mode = 'overlap', [int]$BudgetUsed = 0,
    [int]$WaitMilliseconds = 60000, [int]$ProcessorCount = 4) {
    foreach ($name in @('first.started', 'second.started')) {
        $path = Join-Path $fixture $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $root
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $fixture 'eng\Run-CiLane.ps1'), 'core')) {
        $start.ArgumentList.Add($argument)
    }
    foreach ($key in @('LANE_STARTED_AT', 'GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT',
        'GITHUB_STEP_SUMMARY', 'CORE_CONTRACT_ISOLATION')) { $null = $start.Environment.Remove($key) }
    $start.Environment['CORE_CONTRACT_MODE'] = $Mode
    $start.Environment['DOTNET_PROCESSOR_COUNT'] = [string]$ProcessorCount
    if ($BudgetUsed) { $start.Environment['LANE_STARTED_AT'] = [DateTimeOffset]::UtcNow.AddSeconds(-$BudgetUsed).ToString('O') }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit($WaitMilliseconds)) { throw 'Controlled runner exceeded its test deadline.' }
        return @{ exitCode = $process.ExitCode; output = $stdout.GetAwaiter().GetResult(); errors = $stderr.GetAwaiter().GetResult()
            execution = Get-Content -LiteralPath (Join-Path $fixture '.ci-results\core\execution.json') -Raw | ConvertFrom-Json -AsHashtable }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'eng'),
        (Join-Path $fixture 'tests\OrgExplorer.Core.Tests') -Force | Out-Null
    foreach ($name in @('Run-CiLane.ps1', 'DesktopResults.ps1', 'CoreContracts.ps1')) {
        $path = Join-Path $root "eng\$name"
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path (Join-Path $fixture "eng\$name") }
    }
    foreach ($name in @('DesktopScenarios.json', 'DesktopPartitions.json')) {
        Copy-Item -LiteralPath (Join-Path $root "tests\$name") (Join-Path $fixture "tests\$name")
    }
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fixture 'tests\OrgExplorer.Core.Tests\OrgExplorer.Core.Tests.csproj')
    'public class ControlledWorkload { [Xunit.Fact] public void CompilationCompleted() { if (System.Environment.GetEnvironmentVariable("CORE_CONTRACT_MODE") == "compiled-failure") throw new System.InvalidOperationException("Controlled compiled failure."); } }' |
        Set-Content -LiteralPath (Join-Path $fixture 'tests\OrgExplorer.Core.Tests\ControlledWorkload.cs')
    @'
$PID | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-holder.pid')
Write-Output 'OWNED-HOLDER-STARTED'
Start-Sleep -Seconds 60
'@ | Set-Content -LiteralPath (Join-Path $fixture 'held-child.ps1')
    foreach ($name in $names) {
        @'
param([string]$Assembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$name = Split-Path $PSCommandPath -Leaf
if (!(Get-ChildItem -LiteralPath (Join-Path $root '.ci-results\core') -Filter *.trx)) {
    throw 'A companion started before compiled test completion.'
}
if ($name -ceq 'DraftDiscard.Tests.ps1' -and
    ($Assembly -cne (Join-Path $root '.ci-results\core\build\bin\OrgExplorer.Core.Tests\release\OrgExplorer.Core.Tests.dll') -or
        !(Test-Path -LiteralPath $Assembly))) {
    throw 'The already-built draft-discard assembly argument was lost.'
}
if ((Get-Location).Path -cne $root) { throw 'A companion lost its runner-owned working directory.' }
if ($env:CORE_CONTRACT_MODE -ceq 'timeout') {
    if ($name -ceq 'CiGate.Tests.ps1') {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        foreach ($argument in @('-NoProfile', '-File', (Join-Path $root 'held-child.ps1'))) { $start.ArgumentList.Add($argument) }
        $child = [Diagnostics.Process]::Start($start)
        Write-Output "OWNED-DESCENDANT $($child.Id)"
        $child.Dispose()
        exit 0
    }
    Start-Sleep -Seconds 60
}
if ($name -ceq 'CiGate.Tests.ps1') {
    $env:CORE_CONTRACT_ISOLATION = 'child-only'
    function global:gh { throw 'A fake external binding leaked between companions.' }
    New-Item -ItemType File -Path (Join-Path $root 'first.started') -Force | Out-Null
    Set-Location -LiteralPath (Join-Path $root 'tests')
    if ($env:CORE_CONTRACT_MODE -cne 'serial') {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(3)
        while (!(Test-Path -LiteralPath (Join-Path $root 'second.started'))) {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Required companions did not overlap.' }
            Start-Sleep -Milliseconds 20
        }
    }
    Start-Sleep -Milliseconds 150
}
if ($name -ceq 'CiRunner.Tests.ps1') {
    if ($env:CORE_CONTRACT_ISOLATION) { throw 'A companion inherited another companion environment mutation.' }
    if ((Get-Command gh).CommandType -ne 'Application') { throw 'A companion inherited another companion function binding.' }
    New-Item -ItemType File -Path (Join-Path $root 'second.started') -Force | Out-Null
}
Write-Output "CONTROLLED-COMPANION $name"
if ($env:CORE_CONTRACT_MODE -ceq 'failure' -and $name -ceq 'FilesystemHealth.Tests.ps1') {
    throw 'Controlled required assertion failed.'
}
'@ | Set-Content -LiteralPath (Join-Path $fixture "tests\$name")
    }
    git -C $fixture init --quiet
    git -C $fixture add .
    git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Controlled runner workload'
    Assert ($LASTEXITCODE -eq 0) 'Controlled source could not be committed.'
    . "$root\eng\LaneHealth.ps1"
    $serial = Invoke-ControlledCore -Mode serial -ProcessorCount 1
    Assert ($serial.exitCode -eq 0 -and $serial.execution.complete -and $serial.execution.outcome -ceq 'success') "One-CPU serial execution failed: $($serial.errors)`n$($serial.output)"
    Assert ($serial.execution.configuration.processors -eq 1 -and
        $serial.execution.configuration.coreContracts.maxParallelism -eq 1) 'One-CPU execution did not retain its resolved serial scheduling bound.'
    Assert (($serial.execution.steps.name -join "`n") -ceq ((@('restore', 'test') + $names) -join "`n") -and
        @([regex]::Matches($serial.output, 'CONTROLLED-COMPANION ')).Count -eq 21) 'Serial execution lost complete ordered receipts or repeated a companion.'
    for ($index = 2; $index -lt $serial.execution.steps.Count; $index++) {
        $record = $serial.execution.steps[$index]
        Assert ($record.status -ceq 'Passed' -and $record.exitCode -eq 0) 'A serial companion did not complete successfully.'
        if ($index -gt 2) {
            Assert ([DateTimeOffset]$record.startedAt -ge [DateTimeOffset]$serial.execution.steps[$index - 1].finishedAt) 'The one-worker queue overlapped companion executions.'
        }
    }
    $projection = Export-LocalCoreHealth (Join-Path $fixture '.ci-results\core') (Join-Path $fixture 'serial projection') $serial.exitCode
    Assert ($projection.validation -ceq 'success' -and $projection.tests.total -eq 1 -and
        $projection.configuration.coreContracts.maxParallelism -eq 1 -and !$projection.baselineEligible) 'Serial execution lost actual TRX validation or truthful local scheduling configuration.'
    Write-Host 'PASS: one-CPU execution serializes every required companion with complete ordered receipts and successful local validation.'
    $success = Invoke-ControlledCore
    $execution = $success.execution
    Assert ($success.exitCode -eq 0) "Controlled runner failed: $($success.errors)`n$($success.output)"
    Assert ($execution.complete -and $execution.outcome -ceq 'success') 'Overlapping execution did not complete.'
    Assert ($execution.configuration.processors -eq 4 -and $execution.configuration.coreContracts.maxParallelism -eq 4) 'The overlap fixture did not resolve its controlled four-CPU child configuration.'
    Assert (($execution.steps.name -join "`n") -ceq ((@('restore', 'test') + $names) -join "`n")) 'Required receipts lost manifest order.'
    $first = $execution.steps[2]; $second = $execution.steps[3]
    Assert ([DateTimeOffset]$second.startedAt -lt [DateTimeOffset]$first.finishedAt) 'Required companion clocks do not overlap.'
    Assert (@([regex]::Matches($success.output, 'CONTROLLED-COMPANION ')).Count -eq 21) 'A companion was omitted or repeated.'
    foreach ($record in @($execution.steps | Select-Object -Skip 2)) {
        $overlapping = @($execution.steps | Select-Object -Skip 2 | Where-Object {
            [DateTimeOffset]$_.startedAt -le [DateTimeOffset]$record.startedAt -and
            [DateTimeOffset]$_.finishedAt -gt [DateTimeOffset]$record.startedAt
        })
        Assert ($overlapping.Count -le $execution.configuration.coreContracts.maxParallelism) 'Companion ownership exceeded the resolved scheduling bound.'
        Assert ($record.exitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $fixture ".ci-results\core\$($record.stdout)")) -and
            (Test-Path -LiteralPath (Join-Path $fixture ".ci-results\core\$($record.stderr)"))) 'A completed receipt lost actual exit or output diagnostics.'
    }
    $projection = Export-LocalCoreHealth (Join-Path $fixture '.ci-results\core') (Join-Path $fixture 'success projection') $success.exitCode
    Assert ($projection.validation -ceq 'success' -and $projection.tests.total -eq 1 -and !$projection.baselineEligible) 'Real runner/TRX evidence did not pass the local public validator.'
    $omission = Join-Path $fixture 'omitted cleanup'
    Copy-Item -LiteralPath (Join-Path $fixture '.ci-results\core') -Destination $omission -Recurse
    $omitted = $execution | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable
    $omitted.steps = @($omitted.steps | Where-Object { $_.name -cne 'HostedCleanup.Tests.ps1' })
    $omitted.configuration.coreContracts.manifest = 'legacy-without-cleanup'
    $omitted | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $omission 'execution.json')
    $projection = Export-LocalCoreHealth $omission (Join-Path $fixture 'omission projection') 0
    Assert ($projection.validation -cne 'success') 'Artifact-advertised legacy selection omitted required native cleanup conformance.'
    Write-Host 'PASS: public core execution overlaps isolated companions after compiled success with ordered complete receipts.'
    $compiledFailure = Invoke-ControlledCore compiled-failure
    Assert ($compiledFailure.exitCode -ne 0 -and !$compiledFailure.execution.complete -and
        $compiledFailure.execution.steps.Count -eq 2 -and $compiledFailure.execution.steps[1].status -ceq 'Failed' -and
        !$compiledFailure.output.Contains('CONTROLLED-COMPANION ')) 'A failed compiled core selection launched companions or acquired success.'
    $projection = Export-LocalCoreHealth (Join-Path $fixture '.ci-results\core') (Join-Path $fixture 'compiled failure projection') $compiledFailure.exitCode
    Assert ($projection.validation -ceq 'failure' -and $projection.tests.failed -eq 1) 'Real failed compiled test evidence lost its failure.'
    Write-Host 'PASS: compiled failure starts no companions and remains failed through actual TRX validation.'
    $failure = Invoke-ControlledCore failure
    Assert ($failure.exitCode -ne 0 -and !$failure.execution.complete -and $failure.execution.outcome -ceq 'failure') 'A real required assertion failure became successful execution.'
    Assert (($failure.execution.steps.name -join "`n") -ceq ((@('restore', 'test') + $names) -join "`n")) 'Failure lost complete ordered receipts.'
    $failed = $failure.execution.steps[4]
    Assert ($failed.status -ceq 'Failed' -and $failed.exitCode -ne 0 -and
        (Get-Content -LiteralPath (Join-Path $fixture ".ci-results\core\$($failed.stderr)") -Raw).Contains('Controlled required assertion failed.')) 'An actual child failure lost its error stream or exit.'
    $projection = Export-LocalCoreHealth (Join-Path $fixture '.ci-results\core') (Join-Path $fixture 'failure projection') $failure.exitCode
    Assert ($projection.validation -ceq 'failure' -and $projection.observedCommandExitCode -ne 0) 'Local validation hid a real companion failure.'
    Write-Host 'PASS: a real failing child retains its failure, diagnostics, every ordered receipt and failed local projection.'
    $blockedLog = Join-Path $fixture '.ci-results\core\2-CiGate.Tests.ps1.log'
    Remove-Item -LiteralPath $blockedLog
    New-Item -ItemType Directory -Path $blockedLog | Out-Null
    try {
        $reportFailure = Invoke-ControlledCore report-failure
        Assert ($reportFailure.exitCode -ne 0 -and !$reportFailure.execution.complete -and
            $reportFailure.execution.outcome -ceq 'failure' -and $reportFailure.execution.error.Contains('WriteAllText')) 'Reporting failure hid its initiating write refusal or became successful execution.'
        Assert (($reportFailure.execution.steps.name -join "`n") -ceq ((@('restore', 'test') + $names) -join "`n") -and
            @($reportFailure.execution.steps | Where-Object { $_.status -cne 'Passed' }).Count -gt 0) 'Reporting failure lost ordered nonaccepting receipts.'
        Write-Host 'PASS: reporting refusal preserves its initiating error and nonaccepting ordered receipts.'
    } finally { Remove-Item -LiteralPath $blockedLog }
    $unrelatedStart = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $unrelatedStart.UseShellExecute = $false
    $unrelatedStart.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60')) { $unrelatedStart.ArgumentList.Add($argument) }
    $unrelated = [Diagnostics.Process]::Start($unrelatedStart)
    try {
        $timeout = Invoke-ControlledCore timeout 292 20000
        Assert ($timeout.exitCode -ne 0 -and !$timeout.execution.complete -and $timeout.execution.outcome -ceq 'timed_out') 'An exhausted core budget did not retain its failed incomplete execution.'
        Assert (($timeout.execution.steps.name -join "`n") -ceq ((@('restore', 'test') + $names) -join "`n")) 'Cancellation omitted ordered unfinished receipts.'
        Assert (@($timeout.execution.steps | Where-Object { $_.outcome -ceq 'not_started' }).Count -gt 0) 'A bounded queue launched work after deadline.'
        Assert (!$unrelated.HasExited) 'Cancellation terminated an unrelated process.'
        $ownedId = [int](Get-Content -LiteralPath (Join-Path $fixture 'owned-holder.pid'))
        Assert ($null -eq (Get-Process -Id $ownedId -ErrorAction SilentlyContinue)) 'Cancellation left an owned descendant alive after its parent exited.'
        $projection = Export-LocalCoreHealth (Join-Path $fixture '.ci-results\core') (Join-Path $fixture 'timeout projection') $timeout.exitCode
        Assert ($projection.validation -cne 'success' -and !$projection.runnerComplete -and
            $projection.runnerOutcome -ceq 'timed_out') 'Incomplete deadline evidence acquired successful local validation.'
        Write-Host 'PASS: deadline cancellation bounds owned descendants and pending receipts without touching unrelated processes.'
    } finally {
        if (!$unrelated.HasExited) { $unrelated.Kill($true); $unrelated.WaitForExit() }
        $unrelated.Dispose()
        $holder = Join-Path $fixture 'owned-holder.pid'
        if (Test-Path -LiteralPath $holder) {
            $ownedId = [int](Get-Content -LiteralPath $holder)
            if (Get-Process -Id $ownedId -ErrorAction SilentlyContinue) { Stop-Process -Id $ownedId -Force }
        }
    }
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
