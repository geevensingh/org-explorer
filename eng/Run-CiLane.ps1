param(
    [Parameter(Mandatory)][ValidateSet('core', 'filesystem', 'desktop', 'high-resolution')][string] $Lane,
    [ValidateSet('people', 'workspace')][string] $DesktopPartition,
    [ValidateSet('reading', 'context')][string] $HighResolutionPartition
)
$ErrorActionPreference = 'Stop'
$started = if ($env:LANE_STARTED_AT) { [DateTimeOffset]::Parse($env:LANE_STARTED_AT) } else { [DateTimeOffset]::UtcNow }
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if ($DesktopPartition -and $Lane -ne 'desktop') { throw 'Only ordinary desktop execution supports partitions.' }
if ($HighResolutionPartition -and $Lane -ne 'high-resolution') {
    throw 'Only high-resolution desktop execution supports high-resolution partitions.'
}
. "$root\eng\DesktopResults.ps1"
$partition = if ($HighResolutionPartition) { $HighResolutionPartition } else { $DesktopPartition }
$partitions = Get-DesktopPartitions -HighResolution:([bool]$HighResolutionPartition)
$runDesktopContracts = $Lane -eq 'desktop' -and $DesktopPartition -ne 'people'
$resultsName = if ($partition) { "$Lane-$partition" } else { $Lane }
$results = Join-Path $root ".ci-results\$resultsName"
New-Item -ItemType Directory -Path $results -Force | Out-Null
$steps = [System.Collections.Generic.List[object]]::new()
$index = 0
$artifactBuild = Join-Path $results 'build'
$ceilingSeconds = if ($Lane -eq 'core') { 300 } `
    elseif ($Lane -in @('desktop', 'high-resolution')) { 900 } else { 660 }
$env:SCALE_TIMING_FILE = Join-Path $results 'scale-measurements.log'
$updateFixture = $null
$previousUpdateFixture = $env:ORG_EXPLORER_UPDATE_FIXTURE
$complete = $false
$executionFailure = $null
if ($partition -or $Lane -eq 'filesystem') {
    $executionRevision = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or ($env:GITHUB_SHA -and $executionRevision -cne $env:GITHUB_SHA)) {
        $executionKind = if ($Lane -eq 'filesystem') { 'Filesystem' } else { 'Desktop' }
        throw "$executionKind execution does not match the dispatched candidate."
    }
}
if ($Lane -eq 'filesystem') {
    foreach ($file in @(Get-ChildItem -LiteralPath $results -File | Where-Object {
        $_.Extension -ceq '.trx' -or $_.Name -cin @('execution.json', 'timing.json')
    })) { Remove-Item -LiteralPath $file.FullName -Force }
}

function Invoke-BudgetedDotnet([string] $Name, [string[]] $Arguments) {
    $Arguments = @($Arguments[0], '--artifacts-path', $artifactBuild) + $Arguments[1..($Arguments.Length - 1)]
    $remaining = [TimeSpan]::FromSeconds($ceilingSeconds) - ([DateTimeOffset]::UtcNow - $started)
    if ($remaining.TotalMilliseconds -le 0) { throw 'The lane budget (including setup) is exhausted.' }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $phaseStarted = [DateTimeOffset]::UtcNow
    $log = Join-Path $results "$script:index-$Name.log"
    $errors = Join-Path $results "$script:index-$Name-errors.log"
    $script:index++
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errorOutput = $process.StandardError.ReadToEndAsync()
    $succeeded = $false
    $timedOut = $false
    try {
        if (!$process.WaitForExit([int]$remaining.TotalMilliseconds)) {
            $timedOut = $true
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Name exceeded the remaining execution budget."
        }
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
        $succeeded = $true
    } finally {
        try {
            if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            [IO.File]::WriteAllText($log, $output.GetAwaiter().GetResult())
            [IO.File]::WriteAllText($errors, $errorOutput.GetAwaiter().GetResult())
            Get-Content -LiteralPath $log
            Get-Content -LiteralPath $errors
        } finally {
            $steps.Add(@{ name = $Name; seconds = $timer.Elapsed.TotalSeconds
                status = if ($succeeded) { 'Passed' } else { 'Failed' }
                outcome = if ($succeeded) { 'success' } elseif ($timedOut) { 'timed_out' } else { 'failure' }
                startedAt = $phaseStarted.ToString('O'); finishedAt = [DateTimeOffset]::UtcNow.ToString('O') })
            $process.Dispose()
        }
    }
}

try {
    switch ($Lane) {
        core {
            Invoke-BudgetedDotnet restore @('restore', 'tests\OrgExplorer.Core.Tests', '--locked-mode')
            Invoke-BudgetedDotnet test @('test', 'tests\OrgExplorer.Core.Tests', '-c', 'Release', '--no-restore', '--logger', 'trx',
                '--results-directory', '.ci-results\core')
            & "$root\tests\CiGate.Tests.ps1"
            & "$root\tests\CiRunner.Tests.ps1"
            & "$root\tests\FilesystemHealth.Tests.ps1"
            & "$root\tests\ImplementationBaseline.Tests.ps1"
            & "$root\tests\DocumentationHandoff.Tests.ps1"
            & "$root\tests\DraftDiscard.Tests.ps1" -Assembly (Join-Path $artifactBuild 'bin\OrgExplorer.Core.Tests\release\OrgExplorer.Core.Tests.dll')
            & "$root\tests\Performance.Tests.ps1"
            & "$root\tests\DesktopResults.Tests.ps1"
            & "$root\tests\UiScale.Tests.ps1"
            & "$root\tests\Release.Tests.ps1"
            & "$root\tests\Release.Gates.Tests.ps1"
            & "$root\tests\Release.Publication.Tests.ps1"
            & "$root\tests\Release.Renewal.Tests.ps1"
            & "$root\tests\Release.Workflow.Tests.ps1"
            & "$root\tests\Release.Payload.Tests.ps1"
            & "$root\tests\Update.TrustLifetime.Tests.ps1"
            & "$root\tests\ApplicationIcon.Tests.ps1"
        }
        filesystem {
            Invoke-BudgetedDotnet restore @('restore', 'tests\OrgExplorer.Persistence.Tests', '--locked-mode')
            Invoke-BudgetedDotnet test @('test', 'tests\OrgExplorer.Persistence.Tests', '-c', 'Release', '--no-restore', '--logger', 'trx',
                '--results-directory', '.ci-results\filesystem', '--filter', 'Category!=Performance')
        }
        { $_ -in @('desktop', 'high-resolution') } {
            $highResolution = $Lane -eq 'high-resolution'
            $env:DESKTOP_RESULTS_DIRECTORY = $results
            Remove-Item (Join-Path $results 'scenarios.json') -Force -ErrorAction SilentlyContinue
            if (!$highResolution) {
                $updateFixture = Join-Path $root ('.test-workspaces\update-trust-' + [guid]::NewGuid().ToString('N'))
                & "$root\eng\New-UpdateTestFixture.ps1" -Directory $updateFixture
                $env:ORG_EXPLORER_UPDATE_FIXTURE = $updateFixture
            }
            Invoke-BudgetedDotnet restore-app @('restore', 'src\OrgExplorer.Desktop', '--locked-mode')
            Invoke-BudgetedDotnet restore-tests @('restore', 'tests\OrgExplorer.Desktop.Smoke', '--locked-mode')
            $buildArguments = @('build', 'src\OrgExplorer.Desktop', '-c', 'Release', '--no-restore')
            if (!$highResolution) {
                $buildArguments += @("-p:UpdateTrustFile=$(Join-Path $updateFixture 'trust.json')",
                    "-p:UpdateEffectsFile=$(Join-Path $root 'tests\ControlledUpdateEffects.cs')")
            }
            Invoke-BudgetedDotnet build-app $buildArguments
            $displayCommand = if ($highResolution) { '--prepare-high-resolution' } else { '--verify-standard-display' }
            Invoke-BudgetedDotnet display-prerequisite @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release',
                '--no-restore', '--', $displayCommand)
            $flowArguments = @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release', '--no-build',
                '--no-restore', '--', (Join-Path $artifactBuild 'bin\OrgExplorer.Desktop\release\OrgExplorer.Desktop.exe'))
            if ($HighResolutionPartition) { $flowArguments += "--high-resolution-$HighResolutionPartition" }
            elseif ($highResolution) { $flowArguments += '--high-resolution' }
            elseif ($DesktopPartition) { $flowArguments += "--desktop-$DesktopPartition" }
            Invoke-BudgetedDotnet desktop-flows $flowArguments
            if ($runDesktopContracts) {
                Invoke-BudgetedDotnet profile-note-refresh @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release',
                    '--no-build', '--no-restore', '--',
                    (Join-Path $artifactBuild 'bin\OrgExplorer.Desktop\release\OrgExplorer.Desktop.exe'), '--verify-profile-refresh')
                Invoke-BudgetedDotnet update-yield @('run', '--project', 'tests\OrgExplorer.Desktop.Smoke', '-c', 'Release',
                    '--no-build', '--no-restore', '--',
                    (Join-Path $artifactBuild 'bin\OrgExplorer.Desktop\release\OrgExplorer.Desktop.exe'), '--verify-update-yield')
            }
            $manifest = if ($highResolution) { 'HighResolutionScenarios.json' } else { 'DesktopScenarios.json' }
            $expected = if ($partition) { @($partitions[$partition]) } `
                else { @(Get-Content "$root\tests\$manifest" -Raw | ConvertFrom-Json) }
            Assert-DesktopResults (Join-Path $results 'scenarios.json') @($expected)
            if ($runDesktopContracts) {
                Invoke-BudgetedDotnet release-isolation @('publish', 'src\OrgExplorer.Desktop', '-c', 'Release', '--no-restore',
                    '-o', (Join-Path $results 'product-check'))
                & "$root\tests\Update.Trust.Tests.ps1" -Assembly (Join-Path $results 'product-check\OrgExplorer.Desktop.dll') `
                    -Configuration "$root\src\OrgExplorer.Desktop\update-trust.json"
                & "$root\tests\ApplicationIcon.Tests.ps1" -Executable (Join-Path $results 'product-check\OrgExplorer.Desktop.exe')
                $files = Get-ChildItem (Join-Path $results 'product-check') -Recurse -File
                if ($files | Where-Object { $_.FullName -match '(?i)fixtures|test-workspaces|OrgExplorer\.(Core\.Tests|Persistence\.Tests|Desktop\.Smoke)|ScaleFixture' }) {
                    throw 'Fictitious fixtures or test assemblies leaked into the product publish output.'
                }
            }
        }
    }
    $complete = $true
} catch {
    $executionFailure = $_.Exception.Message
    throw
} finally {
    $env:ORG_EXPLORER_UPDATE_FIXTURE = $previousUpdateFixture
    if ($updateFixture -and (Test-Path -LiteralPath $updateFixture)) { Remove-Item -LiteralPath $updateFixture -Recurse -Force }
    $configuration = $null
    $configurationErrors = @()
    if ($Lane -eq 'filesystem') {
        $testTree = git rev-parse --verify HEAD:tests/OrgExplorer.Persistence.Tests 2>$null
        if ($LASTEXITCODE -ne 0) { $testTree = $null; $configurationErrors += 'Persistence test-tree identity unavailable.' }
        $fixtureTree = $null
        try {
            $projectDirectory = Join-Path $root 'tests\OrgExplorer.Persistence.Tests'
            [xml]$project = Get-Content -LiteralPath (Join-Path $projectDirectory 'OrgExplorer.Persistence.Tests.csproj') -Raw
            $paths = @('tests/OrgExplorer.Persistence.Tests', 'tests/OrgExplorer.Storage.ProcessFixture', 'tests/Fixtures',
                'Directory.Build.props', 'Directory.Build.targets')
            foreach ($compile in @($project.SelectNodes('/Project/ItemGroup/Compile'))) {
                $include = $compile.GetAttribute('Include')
                $path = [IO.Path]::GetFullPath((Join-Path $projectDirectory $include))
                $paths += [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
            }
            $tree = @(git ls-tree -r HEAD -- @paths)
            if ($LASTEXITCODE -ne 0 -or !$tree.Count) { throw 'Selected workload tree could not be resolved.' }
            $fixtureTree = ($tree -join "`n") | git hash-object --stdin
            if ($LASTEXITCODE -ne 0) { throw 'Selected workload identity could not be hashed.' }
        } catch { $configurationErrors += "Workload/linked-fixture identity unavailable: $($_.Exception.Message)" }
        $sdk = dotnet --version
        if ($LASTEXITCODE -ne 0) { $sdk = $null; $configurationErrors += 'Resolved SDK identity unavailable.' }
        $runtimes = @(dotnet --list-runtimes | ForEach-Object { $_ -replace ' \[.*\]$', '' })
        if ($LASTEXITCODE -ne 0 -or !$runtimes) { $runtimes = @(); $configurationErrors += 'Installed runtime identities unavailable.' }
        $configuration = @{
            workload = 'OrgExplorer.Persistence.Tests'; testTree = $testTree; fixtureTree = $fixtureTree; sdk = $sdk; runtimes = $runtimes
            os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            processors = [Environment]::ProcessorCount; image = $env:ImageOS; imageVersion = $env:ImageVersion
            powershell = $PSVersionTable.PSVersion.ToString()
            setup = 'locked-restore;isolated-build;setup-and-cleanup-included'
        }
    }
    $elapsed = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
    @{ lane = $Lane; startedAt = $started.ToString('O'); executionSeconds = $elapsed; targetSeconds = 300;
        ceilingSeconds = $ceilingSeconds; steps = @($steps.ToArray()); queuesIncluded = $false; cdIncluded = $false } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $results 'timing.json')
    if ($partition) {
        @{ schemaVersion = 1; lane = $Lane; partition = $partition; revision = $executionRevision
            runId = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'local' }
            runAttempt = if ($env:GITHUB_RUN_ATTEMPT) { $env:GITHUB_RUN_ATTEMPT } else { 'local' }
            complete = $complete; ceilingSeconds = $ceilingSeconds; executionSeconds = $elapsed
            steps = @($steps.ToArray())
        } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $results 'execution.json')
    }
    if ($Lane -eq 'filesystem') {
        $outcome = if ($complete) { 'success' } `
            elseif ($executionFailure -ceq 'The lane budget (including setup) is exhausted.' -or
                @($steps | Where-Object { $_.outcome -ceq 'timed_out' }).Count) { 'timed_out' } else { 'failure' }
        @{ schemaVersion = 1; lane = $Lane; revision = $executionRevision
            runId = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'local' }
            runAttempt = if ($env:GITHUB_RUN_ATTEMPT) { $env:GITHUB_RUN_ATTEMPT } else { 'local' }
            startedAt = $started.ToString('O'); finishedAt = [DateTimeOffset]::UtcNow.ToString('O')
            complete = $complete; outcome = $outcome; error = $executionFailure
            policyVersion = 'filesystem-original-v1'; selector = 'Category!=Performance'
            ceilingSeconds = $ceilingSeconds; targetSeconds = 300; executionSeconds = $elapsed
            steps = @($steps.ToArray()); configuration = $configuration; configurationErrors = $configurationErrors
        } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $results 'execution.json')
    }
    Write-Host "Lane $Lane execution (setup/restore/build/tests/fixtures): $([Math]::Round($elapsed, 2)) seconds; target 300; ceiling $ceilingSeconds."
    if ($env:GITHUB_STEP_SUMMARY) {
        "### $Lane execution`n$([Math]::Round($elapsed, 2)) seconds including setup, restore, build, tests and fixtures; target 300 s; ceiling $ceilingSeconds s. Queues and later CD excluded." |
            Add-Content $env:GITHUB_STEP_SUMMARY
    }
}
