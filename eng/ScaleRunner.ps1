. "$PSScriptRoot\ScaleCleanupPolicy.ps1"

function Invoke-ScaleSuite(
    [string] $Suite, [string] $Label, [string] $ResultsDirectory, [string] $ExpectedRevision,
    [hashtable] $Selection, [scriptblock] $Verify,
    [ValidateSet(660, 900)][int] $ObservationSeconds = 660,
    [string] $ExpectedPolicyVersion = 'rich-scale-original-v1',
    [switch] $DiagnosticOnly
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
    if ($Suite -ceq 'ui-scale') {
        $policy = Get-ApprovedTimingPolicy $ExpectedPolicyVersion
        $ceilingSeconds = if ($Selection.worker -ceq 'prefix') { $policy.prefixSafetySeconds } else { $policy.tailSafetySeconds }
    }
    $diagnostic = $DiagnosticOnly -or $ObservationSeconds -gt $ceilingSeconds
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
        configuration = $null; configurationErrors = @()
        sourcePolicy = $ExpectedPolicyVersion; cleanup = $null; ownedCommandsExited = $true
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
            observationSeconds = $ObservationSeconds; diagnosticOnly = [bool]$diagnostic
            acceptanceDeadlineAt = $state.started.AddSeconds($ceilingSeconds).ToString('O')
            observationDeadlineAt = $state.started.AddSeconds($ObservationSeconds).ToString('O')
            observedOutcome = $state.observedOutcome; phase = $state.phase; activeCommand = $state.activeCommand
            lastProgress = $state.lastProgress; heartbeatAt = [DateTimeOffset]::UtcNow.ToString('O')
            activePhase = $state.activePhase; lastActivePhase = $state.lastActivePhase
            environment = $executionEnvironment
            configuration = $state.configuration; configurationErrors = $state.configurationErrors
        }
        if ($Suite -ceq 'ui-scale') { $report.policyVersion = $ExpectedPolicyVersion }
        foreach ($key in $Selection.Keys) { $report[$key] = $Selection[$key] }
        if ($state.sourcePolicy -ceq 'ci-timing-v4') {
            $report.acceptancePolicyVersion = $state.sourcePolicy
            $report.verificationOutcome = $state.observedOutcome
            $report.cleanup = $state.cleanup
        }
        $json = $report | ConvertTo-Json -Depth 12
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

    function Write-ScaleCleanupFailure([Management.Automation.ErrorRecord] $Failure) {
        $target = if ($Failure.TargetObject -is [IO.FileSystemInfo]) {
            $Failure.TargetObject.FullName
        } else { [string] $Failure.TargetObject }
        $path = if ([string]::IsNullOrWhiteSpace($target)) { $null } else { [IO.Path]::GetFullPath($target) }
        $ownerQuery = @{ operation = 'unavailable'; reason = 'No verified owned Windows file path.' }
        if ($IsWindows -and $path -and
            $path.StartsWith($build + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            try {
                if (!('OrgExplorer.Storage.ProcessFixture.LeaseOwners' -as [type])) {
                    $source = [IO.File]::ReadAllText((Join-Path $root 'tests\OrgExplorer.Storage.ProcessFixture\LeaseOwners.cs'))
                    Add-Type -TypeDefinition ("#nullable enable`nusing System;`nusing System.Linq;`n" + $source)
                }
                $ownerQuery = [OrgExplorer.Storage.ProcessFixture.LeaseOwners]::Read(@($path))
            } catch {
                $ownerQuery = @{ operation = 'unavailable'; errorType = $_.Exception.GetType().FullName
                    hresult = $_.Exception.HResult }
            }
        }
        $observation = @{
            outcome = 'failure'; revision = $state.revision; path = $path
            observedAt = [DateTimeOffset]::UtcNow.ToString('O')
            errorType = $Failure.Exception.GetType().FullName; hresult = $Failure.Exception.HResult
            ownerQuery = $ownerQuery
            interpretation = 'Read-only, non-atomic exact-owned-file observation after cleanup failed. Missing owners do not identify an earlier holder. No deletion retry or process shutdown.'
        }
        [IO.File]::WriteAllText((Join-Path $run 'cleanup-failure.json'), ($observation | ConvertTo-Json -Depth 8))
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
            $previousDeadline = [Environment]::GetEnvironmentVariable('SCALE_DEADLINE_AT')
            try {
                [Environment]::SetEnvironmentVariable('SCALE_DEADLINE_AT', $state.started.AddSeconds($ObservationSeconds).ToString('O'))
                $process = Start-Process $FilePath -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errors
                $state.ownedCommandsExited = $false
            } finally {
                if ($null -eq $previousDeadline) {
                    [Environment]::SetEnvironmentVariable('SCALE_DEADLINE_AT', [NullString]::Value)
                } else {
                    [Environment]::SetEnvironmentVariable('SCALE_DEADLINE_AT', $previousDeadline)
                }
            }
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
                    $state.ownedCommandsExited = $process.HasExited
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
        if ($Suite -cin @('non-ui-performance', 'ui-scale')) {
            $state.sourcePolicy = Get-SourceTimingPolicyVersion $state.revision
        }
        Write-ScaleReport
        if ($ExpectedRevision -and ($state.revision -ne $ExpectedRevision -or $state.dirty -or $state.untracked)) {
            throw 'Pre-publication/candidate verification requires the exact expected revision and a clean checkout, including untracked files.'
        }
        $null = Assert-ScaleRemainingBudget -ForObservation
        if ($Suite -ceq 'ui-scale') {
            try {
                $testTree = (& git rev-parse --verify HEAD:tests/OrgExplorer.Desktop.Smoke).Trim()
                if ($LASTEXITCODE -ne 0) { throw 'Rich test-tree identity unavailable.' }
                $tree = @(git ls-tree -r HEAD -- tests eng Directory.Build.props Directory.Build.targets)
                if ($LASTEXITCODE -ne 0 -or !$tree.Count) { throw 'Rich workload/fixture identity unavailable.' }
                $fixtureTree = (($tree -join "`n") | git hash-object --stdin).Trim()
                if ($LASTEXITCODE -ne 0) { throw 'Rich workload hash unavailable.' }
                $sdk = (dotnet --version).Trim()
                if ($LASTEXITCODE -ne 0) { throw 'Resolved SDK unavailable.' }
                $runtimes = @(dotnet --list-runtimes | ForEach-Object { $_ -replace ' \[.*\]$', '' })
                if ($LASTEXITCODE -ne 0 -or !$runtimes.Count) { throw 'Resolved runtimes unavailable.' }
                $state.configuration = @{
                    workload = 'OrgExplorer.Desktop.Smoke'; testTree = $testTree; fixtureTree = $fixtureTree
                    sdk = $sdk; runtimes = $runtimes; os = $executionEnvironment.os
                    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
                    processors = $executionEnvironment.processors; image = $executionEnvironment.image
                    imageVersion = $executionEnvironment.imageVersion; powershell = $executionEnvironment.powershell
                    setup = 'locked-restore;isolated-build;setup-and-cleanup-included'
                    display = $null; native = $null; observationSeconds = $ObservationSeconds
                }
            } catch { $state.configurationErrors += "Rich comparison configuration unavailable: $($_.Exception.Message)" }
        }
        Write-Host "$Label`: revision $($state.revision); tracked changes $($state.dirty); shared ceiling $ceilingSeconds s."
        if ($diagnostic) {
            Write-Host "DIAGNOSTIC ONLY: observation window $ObservationSeconds s; this invocation cannot pass or supply a baseline."
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
        $state.outcome = if ($diagnostic) { 'diagnostic_complete' } else { 'success' }
        if ($state.outcome -eq 'diagnostic_complete') {
            $state.failure = 'Extended observation is diagnostic only, not passing candidate verification.'
        }
    } catch {
        $state.observedOutcome = if ($state.outcome -eq 'timed_out') { 'timed_out' } else { 'failure' }
        if ($state.outcome -ne 'timed_out') { $state.outcome = 'failure' }
        $state.failure = $_.Exception.Message
    } finally {
        if ($Suite -ceq 'ui-scale' -and $null -ne $state.configuration) {
            try {
                $display = Join-Path $run 'display.json'
                if (Test-Path -LiteralPath $display) {
                    $state.configuration.display = Get-Content -LiteralPath $display -Raw | ConvertFrom-Json -AsHashtable
                } else { $state.configurationErrors += 'Actual rich display metadata unavailable.' }
                $handoff = if ($Selection.worker -ceq 'prefix') { Join-Path $run 'handoff\handoff.json' } else { Join-Path $run 'handoff-received.json' }
                if (Test-Path -LiteralPath $handoff) {
                    $binding = (Get-Content -LiteralPath $handoff -Raw | ConvertFrom-Json -AsHashtable).binding
                    $state.configuration.native = @{ version = $binding.nativeVersion; sourceId = $binding.nativeSourceId
                        loadedSha256 = $binding.loadedNativeSha256 }
                } else { $state.configurationErrors += 'Actual loaded native handoff binding unavailable.' }
            } catch { $state.configurationErrors += "Rich configuration evidence rejected: $($_.Exception.Message)" }
        }
        $state.phase = 'cleanup'
        $state.activeCommand = $null
        $state.activePhase = $null
        Write-ScaleReport
        try {
            # Owned commands have exited before their fixture/build tree is removed.
            if (Test-Path $build) { Remove-Item $build -Recurse -Force }
            $state.cleanup = @{ outcome = 'success'; warning = $false; phase = 'build-housekeeping' }
        } catch {
            $cleanupFailure = $_
            try { Write-ScaleCleanupFailure $cleanupFailure }
            catch { [Console]::Error.WriteLine("Cleanup observation failed ({0}); original failure retained.", $_.Exception.GetType().FullName) }
            $state.cleanup = @{
                outcome = 'failure'; warning = $false; phase = 'build-housekeeping'
                error = $cleanupFailure.Exception.Message; errorType = $cleanupFailure.Exception.GetType().FullName
                hresult = $cleanupFailure.Exception.HResult; observedAt = [DateTimeOffset]::UtcNow.ToString('O')
                path = [string]$cleanupFailure.TargetObject; remainingBuild = Get-ScaleBuildInventory $build
                authenticationFailure = $null
            }
            try {
                $native = Get-ScaleNativeCleanupTarget $cleanupFailure $build $Suite
                if ($state.sourcePolicy -cne 'ci-timing-v4' -or !$native -or $state.outcome -cne 'success' -or
                    $state.observedOutcome -cne 'success' -or $state.failure -or $diagnostic -or !$ExpectedRevision -or
                    !$state.ownedCommandsExited -or $env:GITHUB_ACTIONS -cne 'true' -or
                    $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') {
                    throw 'This cleanup refusal is outside the approved signed disposable-worker policy.'
                }
                $null = Assert-ScaleRemainingBudget
                $state.cleanup.path = $native.path
                $state.cleanup.native = $native
                $authenticationStarted = [DateTimeOffset]::UtcNow
                if ($state.configurationErrors.Count -or
                    ($Selection.ContainsKey('configurationErrors') -and @($Selection.configurationErrors).Count)) {
                    throw 'Required source/runtime cleanup scope is unavailable.'
                }
                $state.phase = 'cleanup-authentication'
                Write-ScaleReport
                $proof = Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
                $proof.Remove('cleanup')
                $proof.schemaVersion = 1; $proof.repository = 'geevensingh/org-explorer'
                $proof.nonce = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
                $proof.cleanup = @{} + $state.cleanup
                if ($Suite -ceq 'non-ui-performance') {
                    . "$PSScriptRoot\NonUiCohort.ps1"
                    Assert-PerformanceResults $run
                    $proof.measurements = Read-NonUiCohortMeasurementLog @(Get-Content -LiteralPath (Join-Path $run 'scale-measurements.log'))
                    if ($proof.measurements.phasesMs.Count -ne $NonUiCohortPhases.Count -or
                        @($proof.measurements.phasesMs.Values | Where-Object { $_.outcome -cne 'Passed' }).Count -or
                        !$proof.measurements.profile -or $proof.measurements.profile.p95Ms -gt 1000) {
                        throw 'Required complete raw non-UI verification is missing or invalid.'
                    }
                    $proof.tests = Read-FilesystemTests $run
                    $proof.tests.requiredNames = @($PerformanceTests)
                } else {
                    . "$PSScriptRoot\RichScaleCohort.ps1"
                    $proof.phasesMs = Read-RichCohortPhases @(Get-Content -LiteralPath (Join-Path $run 'ui-scale.log'))
                    $proof.evidence = @{}
                    $worker = $Selection.worker
                    if ($worker -ceq 'prefix') {
                        $handoff = Get-Content (Join-Path $run 'handoff\handoff.json') -Raw | ConvertFrom-Json
                        foreach ($phase in @('normal-executable', 'background-export')) {
                            $proof.evidence["note-$phase"] = Read-UiScaleEvidence $run "scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json" $state.revision $Selection.runId $Selection.runAttempt $handoff.binding
                        }
                        $proof.evidence.lifecycle = Read-UiScaleEvidence $run 'scale-mutation-save-prefix-ui-sqlite-rich-export-lifecycle.json' $state.revision $Selection.runId $Selection.runAttempt $handoff.binding
                    } else { $handoff = Get-Content (Join-Path $run 'handoff-received.json') -Raw | ConvertFrom-Json }
                    $phase = if ($worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }
                    $scenario = if ($worker -ceq 'prefix') { 'scale-mutation-save-prefix' } else { 'scale-mutation-delivery-tail' }
                    $proof.evidence["profile-$phase"] = Read-UiScaleEvidence $run "$scenario-ui-sqlite-rich-profile-latency-$phase.json" $state.revision $Selection.runId $Selection.runAttempt $handoff.binding
                    Assert-UiScaleRichEvidence $proof.evidence $handoff $state.revision $Selection.runId $Selection.runAttempt $state.sourcePolicy -Worker $worker
                    $proof.handoff = $handoff
                }
                $proof.files = @(Get-ScaleProofFiles $run)
                $proofText = $proof | ConvertTo-Json -Depth 40
                $state.cleanup.proof = $proofText
                $attestation = Join-Path $run 'cleanup-attestation.json'
                $watcher = [IO.FileSystemWatcher]::new($run, 'cleanup-attestation.json')
                try {
                    $watcher.EnableRaisingEvents = $true
                    [IO.File]::WriteAllText((Join-Path $run 'cleanup-proof.json'), $proofText)
                    $signal = Join-Path $results 'cleanup-proof.json'
                    [IO.File]::WriteAllText("$signal.writing", $proofText)
                    [IO.File]::Move("$signal.writing", $signal, $true)
                    if (!(Test-Path -LiteralPath $attestation)) {
                        $event = $watcher.WaitForChanged(
                            ([IO.WatcherChangeTypes]::Created -bor [IO.WatcherChangeTypes]::Renamed),
                            [int]((Assert-ScaleRemainingBudget) * 1000))
                        if ($event.TimedOut) { throw 'Signed cleanup proof did not arrive within the original execution budget.' }
                    }
                    $state.cleanup.attestation = Get-Content -LiteralPath $attestation -Raw
                    Write-ScaleReport
                    $report = Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
                    Assert-ScaleCleanupDisposition $report ci-timing-v4 $results -DuringExecution
                    $state.cleanup.warning = $true
                    $state.cleanup.authenticationSeconds = ([DateTimeOffset]::UtcNow - $authenticationStarted).TotalSeconds
                    $null = Assert-ScaleRemainingBudget
                    Write-Warning "Cleanup FAILED at $($native.path): $($state.cleanup.error). Scoped disposable-worker housekeeping warning; holder unknown. VM teardown only."
                    Write-Host "::warning title=Owned build cleanup failed::$($state.cleanup.error -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A')"
                } finally { $watcher.Dispose() }
            } catch {
                $state.cleanup.authenticationFailure = $_.Exception.Message
                $state.outcome = if (([DateTimeOffset]::UtcNow - $state.started).TotalSeconds -ge $ceilingSeconds) { 'timed_out' } else { 'failure' }
                $state.failure = if ($state.failure) { $state.failure + "`n" + $cleanupFailure.Exception.Message } else { $cleanupFailure.Exception.Message }
                Write-Error -Message $state.failure -ErrorAction Continue
            }
        }
        $elapsed = ([DateTimeOffset]::UtcNow - $state.started).TotalSeconds
        if ($elapsed -ge $ceilingSeconds) {
            $state.outcome = 'timed_out'
            $budgetFailure = "$Label exhausted its unchanged whole-execution budget, including cleanup and authentication."
            $state.failure = if ($state.failure) { $state.failure + "`n" + $budgetFailure } else { $budgetFailure }
        }
        $state.phase = 'finished'
        Write-ScaleReport
        $summary = "$Label $($state.outcome) at $($state.revision) in $([Math]::Round($elapsed, 2)) s including setup, restore/build, fixtures, tests and cleanup; ceiling $ceilingSeconds s. No latency SLA."
        Write-Host $summary
        if ($env:GITHUB_STEP_SUMMARY) {
            "### $Label`: $($state.outcome)`n$summary Queues and publication excluded." | Add-Content $env:GITHUB_STEP_SUMMARY
            if ($state.cleanup -and $state.cleanup.outcome -ceq 'failure') {
                "Build housekeeping: **FAILED**; warning: **$($state.cleanup.warning)**; phase ``$($state.cleanup.phase)``; path ``$($state.cleanup.path)``; ``$($state.cleanup.errorType)`` / ``$($state.cleanup.hresult)``. $($state.cleanup.error) Holder unknown; cleanup did not succeed. Remaining build metadata and signed/raw proof retained; compiled build bytes excluded." |
                    Add-Content $env:GITHUB_STEP_SUMMARY
            }
        }
    }
    if ($state.outcome -ne 'success') { throw "$Label did not succeed: $($state.failure)" }
}
