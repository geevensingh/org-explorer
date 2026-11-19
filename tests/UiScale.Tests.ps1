$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root ".ci-results\ui-scale-contract-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $results | Out-Null
function Invoke-RunnerProbe([string[]] $Arguments, [string] $Log) {
    $quoted = @('-NoProfile', '-File', "$root\eng\Run-UiScale.ps1") + $Arguments |
        ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
    $process = Start-Process pwsh -ArgumentList $quoted -PassThru -NoNewWindow -RedirectStandardOutput $Log -RedirectStandardError "$Log-errors"
    try {
        if (!$process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'UI-scale rejection probe did not terminate within its non-UI test deadline.'
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Assert-DiagnosticClassification([string] $Suite, [switch] $OriginalFailure) {
    $probe = Join-Path $results "$Suite-diagnostic-$OriginalFailure"
    $observed = @{ executed = $false }
    $failure = $null
    try {
        Invoke-ScaleSuite -Suite $Suite -Label $Suite -ResultsDirectory $probe -DiagnosticOnly `
            -Selection @{} -Verify {
                param($run, $build)
                $observed.executed = $true
                $status = Get-Content (Join-Path $probe 'result.json') -Raw | ConvertFrom-Json
                $manifestPath = Join-Path $probe 'diagnostic-artifact-manifest.json'
                if (!$status.diagnosticOnly -or $status.acceptanceEligible -ne $false -or
                    $status.observationSeconds -ne 660 -or $status.ceilingSeconds -ne 660 -or
                    !(Test-Path -LiteralPath $manifestPath)) {
                    throw 'Opt-in 660 diagnostic classification is missing before test execution.'
                }
                $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
                if (!$manifest.diagnosticOnly -or $manifest.acceptanceEligible -ne $false -or
                    $manifest.suite -ne $Suite -or $manifest.scope -ne 'all-members-and-records-recursive' -or
                    !$manifest.coversFutureMembers -or !$manifest.includesBinaryRecords -or
                    $manifest.archiveRoot -ne [IO.Path]::GetRelativePath($root, $probe) -or
                    $manifest.runDirectory -ne [IO.Path]::GetRelativePath($probe, $run)) {
                    throw 'Creation-time diagnostic manifest does not cover every archive member and record.'
                }
                $observed.manifestHash = (Get-FileHash $manifestPath -Algorithm SHA256).Hash
                $observed.raw = Join-Path $run 'raw-measurements.json'
                $observed.binary = Join-Path $run 'native-boundary.raw.log'
                [IO.File]::WriteAllText($observed.raw, '{"measurement":123.456,"fixtureOnly":true}')
                [IO.File]::WriteAllBytes($observed.binary, [byte[]](0..63))
                $observed.binaryHash = (Get-FileHash $observed.binary -Algorithm SHA256).Hash
                if ($OriginalFailure) { throw 'PRIMARY-CONTRACT-FAILURE' }
            }
    } catch { $failure = $_.Exception.Message }
    if (!(Test-Path -LiteralPath (Join-Path $probe 'result.json'))) {
        throw "Explicit $Suite diagnostic was not admitted before execution: $failure"
    }
    $status = Get-Content (Join-Path $probe 'result.json') -Raw | ConvertFrom-Json
    $expectedOutcome = if ($OriginalFailure) { 'failure' } else { 'diagnostic_complete' }
    $expectedObserved = if ($OriginalFailure) { 'failure' } else { 'success' }
    if (!$observed.executed -or !$failure -or $status.outcome -ne $expectedOutcome -or
        $status.observedOutcome -ne $expectedObserved -or !$status.diagnosticOnly -or
        $status.acceptanceEligible -ne $false) {
        throw "Explicit $Suite diagnostic did not remain nonaccepting at660: $failure"
    }
    if ($OriginalFailure -and ($status.failure -cne 'PRIMARY-CONTRACT-FAILURE' -or
        !$failure.Contains('PRIMARY-CONTRACT-FAILURE'))) {
        throw 'Diagnostic classification replaced the original failure with a diagnostic-success verdict.'
    }
    if ((Get-FileHash (Join-Path $probe 'diagnostic-artifact-manifest.json') -Algorithm SHA256).Hash -ne
        $observed.manifestHash -or [IO.File]::ReadAllText($observed.raw) -ne
        '{"measurement":123.456,"fixtureOnly":true}' -or
        (Get-FileHash $observed.binary -Algorithm SHA256).Hash -ne $observed.binaryHash) {
        throw 'Diagnostic classification rewrote its creation manifest or individual raw measurements afterward.'
    }
}
function Assert-DefaultScaleVerdict([string] $Suite, [int] $ObservationSeconds) {
    $probe = Join-Path $results "$Suite-default-$ObservationSeconds"
    $diagnostic = $ObservationSeconds -gt 660
    $failure = $null
    try {
        Invoke-ScaleSuite -Suite $Suite -Label $Suite -ResultsDirectory $probe `
            -ObservationSeconds $ObservationSeconds -Selection @{} -Verify {
                param($run, $build)
                $status = Get-Content (Join-Path $probe 'result.json') -Raw | ConvertFrom-Json
                if ($status.diagnosticOnly -ne $diagnostic -or $status.acceptanceEligible -ne !$diagnostic -or
                    $status.observationSeconds -ne $ObservationSeconds -or $status.ceilingSeconds -ne 660) {
                    throw 'Default or existing extended-observation classification changed before execution.'
                }
            }
    } catch { $failure = $_.Exception.Message }
    $status = Get-Content (Join-Path $probe 'result.json') -Raw | ConvertFrom-Json
    $outcome = if ($diagnostic) { 'diagnostic_complete' } else { 'success' }
    if ($status.outcome -ne $outcome -or $status.observedOutcome -ne 'success' -or
        [bool]$failure -ne $diagnostic -or
        (Test-Path -LiteralPath (Join-Path $probe 'diagnostic-artifact-manifest.json'))) {
        throw 'Default660 acceptance or the existing900 non-verifying verdict changed.'
    }
}
try {
    . "$root\eng\TEMP-CPU-WAIT.ps1"
    foreach ($case in @('saved', 'foreign-instance', 'start-failure', 'missing-stacks', 'deadline', 'status-failure', 'lost-events', 'stop-failure')) {
        $directory = Join-Path $results "cpu-wait-$case"
        New-Item -ItemType Directory $directory | Out-Null
        $request = @{
            directory = $directory; instance = 'OrgExplorerCpuWait_' + [guid]::NewGuid().ToString('N')
            profile = 'fixture-profile.wprp'
        }
        $calls = [Collections.Generic.List[object]]::new()
        $observed = @{ ready = $false }
        $failure = $null
        try {
            Invoke-TempCpuWaitSession $request {
                param($label, $arguments, $timeout)
                $calls.Add(@{ label = $label; arguments = $arguments; timeout = $timeout })
                if ($arguments[-2] -cne '-instancename' -or $arguments[-1] -cne $request.instance) {
                    throw 'Capture command escaped its exact owned instance.'
                }
                if ($label -eq 'before-start') {
                    return @{ exitCode = if ($case -eq 'foreign-instance') { 0 } else { 1 }; output = '0xc5583000' }
                }
                if ($case -eq 'start-failure' -and $label -eq 'start') {
                    return @{ exitCode = 5; output = 'fixture kernel capability unavailable' }
                }
                if ($label -eq 'ready-status') {
                    return @{ exitCode = 0; output = if ($case -eq 'missing-stacks') { 'missing stacks' }
                        else { "$($request.instance) Memory System Stacks CSwitch ReadyThread SampledProfile Microsoft-Windows-DotNETRuntime Microsoft-Windows-DotNETRuntimeRundown" } }
                }
                if ($label -eq 'stop') {
                    if ($case -eq 'stop-failure') { return @{ exitCode = 5; output = 'fixture stop failed' } }
                    [IO.File]::WriteAllBytes((Join-Path $directory 'cpu-wait.etl'), [byte[]](0..63))
                }
                if ($case -eq 'status-failure' -and $label -eq 'before-stop-status') {
                    throw 'fixture original status failure'
                }
                if ($case -eq 'lost-events' -and $label -eq 'before-stop-status') {
                    return @{ exitCode = 0; output = "Events Lost : 2`nBuffers Lost : 0" }
                }
                return @{ exitCode = 0; output = 'fixture' }
            } { if ($case -eq 'deadline') { 'deadline' } else { 'phase-finished' } } { $observed.ready = $true }
        } catch { $failure = $_.Exception.Message }
        $session = Get-Content (Join-Path $directory 'cpu-wait-session.json') -Raw | ConvertFrom-Json
        if ([bool]$failure -ne ($case -ne 'saved') -or !$session.diagnosticOnly -or
            $session.acceptanceEligible -ne $false -or $session.resolvedStacks -or
            $null -ne $session.lossCounters -or $null -ne $session.retainedTimeCoverage -or
            ($case -ne 'foreign-instance' -and !($calls | Where-Object { $_.label -eq 'stop' })) -or
            $session.outputGuardIsPolledNotHardCap -ne $true) {
            throw "CPU/wait ownership/default evidence/cleanup contract failed: $case; $failure"
        }
        if ($case -eq 'saved' -and (!$observed.ready -or
            $session.status -cne 'saved-needs-stack-loss-clock-coverage-analysis')) {
            throw 'Keyword/stop success must not claim usable CPU/wait stacks or complete circular retention.'
        }
        if ($case -in @('start-failure', 'missing-stacks') -and $observed.ready) {
            throw 'Unsupported CPU/wait recording admitted the native phase.'
        }
        if ($case -eq 'foreign-instance' -and ($observed.ready -or $calls.Count -ne 1)) {
            throw 'Foreign instance was started, stopped or cancelled.'
        }
        if ($case -eq 'status-failure' -and !$session.failure.Contains('fixture original status failure')) {
            throw 'Failed status must retain its primary error while still attempting stop.'
        }
        if ($case -eq 'lost-events' -and ($session.observedLossStatus.Count -ne 2 -or
            !$session.cleanupFailure.Contains('lost events/buffers'))) {
            throw 'Observed event loss was not recorded and refused as missing evidence.'
        }
        if ($case -eq 'stop-failure' -and
            !($calls | Where-Object { $_.label -eq 'cancel-owned-after-stop-failure' })) {
            throw 'Failed stop did not attempt exact owned cancellation.'
        }
        if ($case -eq 'deadline' -and !$session.failure.Contains('deadline')) {
            throw 'Stop/cleanup replaced the original censored-window failure.'
        }
    }
    [xml]$cpuProfile = Get-Content "$root\eng\TEMP-CPU-WAIT.wprp" -Raw
    $cpuProvider = $cpuProfile.WindowsPerformanceRecorder.Profiles.SystemProvider
    if (@($cpuProvider.Stacks.Stack.Value).Count -ne 3 -or
        @(Compare-Object @('CSwitch', 'ReadyThread', 'SampledProfile') @($cpuProvider.Stacks.Stack.Value)).Count -or
        $cpuProfile.WindowsPerformanceRecorder.Profiles.Profile.LoggingMode -cne 'Memory' -or
        $cpuProfile.WindowsPerformanceRecorder.Profiles.SystemCollector.Buffers.Value -ne '1024') {
        throw 'CPU/wait profile lost exact scheduling stacks or fixed circular memory configuration.'
    }
    $cpuCapture = Get-Content "$root\eng\TEMP-CPU-WAIT.ps1" -Raw
    if ($cpuCapture -match "'-filemode'|'-setprofint'|'-disablepagingexecutive'|'-purgecache'" -or
        !$cpuCapture.Contains("'-skipPdbGen'") -or !$cpuCapture.Contains('WaitForExit(100)')) {
        throw 'CPU/wait capture lost bounded owned command control or attempted global profiler tuning.'
    }
    $noOptIn = Join-Path $results 'cpu-invalid-opt-in.log'
    if ((Invoke-RunnerProbe @('-CpuWaitTrace') $noOptIn) -eq 0 -or
        !(Get-Content "$noOptIn-errors" -Raw).Contains('requires explicit DiagnosticOnly')) {
        throw 'CPU/wait tracing was admitted without its explicit nonaccepting opt-in.'
    }
    Write-Host 'PASS: CPU/wait mocked ownership/readiness/start-stop failures/censored cleanup and honest unresolved-stack/coverage bounds; no real ETW recording.'
    $probe = Join-Path $results 'revision-probe'
    New-Item -ItemType Directory $probe | Out-Null
    '{"outcome":"success","revision":"stale"}' | Set-Content (Join-Path $probe 'result.json')
    $exitCode = Invoke-RunnerProbe @('-ExpectedRevision', ('0' * 40), '-ResultsDirectory', $probe) (Join-Path $results 'revision.log')
    if ($exitCode -eq 0) { throw 'A different candidate revision passed UI-scale verification.' }
    $status = Get-Content (Join-Path $probe 'result.json') -Raw | ConvertFrom-Json
    if ($status.outcome -ne 'failure' -or $status.revision -notmatch '^[0-9a-f]{40}$' -or
        $status.expectedRevision -ne ('0' * 40) -or $status.steps.Count -ne 0 -or $status.suite -ne 'ui-scale' -or
        @($status.requiredScenarios).Count -ne 2) {
        throw 'Candidate mismatch was not reported as a fresh named UI-scale failure before execution.'
    }
    Write-Host 'PASS: UI-scale candidate mismatch replaces stale success before any build or desktop execution.'
    $started = $env:SCALE_STARTED_AT
    try {
        $env:SCALE_STARTED_AT = [DateTimeOffset]::UtcNow.AddMinutes(-12).ToString('O')
        $expired = Join-Path $results 'expired-probe'
        $exitCode = Invoke-RunnerProbe @('-ResultsDirectory', $expired) (Join-Path $results 'expired.log')
        if ($exitCode -eq 0) { throw 'Exhausted setup budget passed UI-scale verification.' }
        $status = Get-Content (Join-Path $expired 'result.json') -Raw | ConvertFrom-Json
        if ($status.outcome -ne 'timed_out' -or $status.steps.Count -ne 0 -or $status.executionSeconds -le 660) {
            throw 'UI-scale setup did not consume the shared bounded execution budget.'
        }
    } finally { $env:SCALE_STARTED_AT = $started }
    Write-Host 'PASS: exhausted UI-scale setup budget fails before any build or desktop execution.'
    . "$root\eng\ScaleRunner.ps1"
    foreach ($suite in @('ui-scale', 'non-ui-performance')) {
        Assert-DiagnosticClassification $suite
        Assert-DiagnosticClassification $suite -OriginalFailure
        Assert-DefaultScaleVerdict $suite 660
        Assert-DefaultScaleVerdict $suite 900
        $prior = Join-Path $results "$suite-prior-artifacts"
        New-Item -ItemType Directory $prior | Out-Null
        [IO.File]::WriteAllText((Join-Path $prior 'old.json'), '{"historical":true}')
        $refusal = $null
        try {
            Invoke-ScaleSuite -Suite $suite -Label $suite -ResultsDirectory $prior -DiagnosticOnly `
                -Selection @{} -Verify { throw 'Historical classification must be refused before execution.' }
        } catch { $refusal = $_.Exception.Message }
        if (!$refusal -or !$refusal.Contains('fresh results directory') -or
            [IO.File]::ReadAllText((Join-Path $prior 'old.json')) -ne '{"historical":true}' -or
            (Test-Path -LiteralPath (Join-Path $prior 'diagnostic-artifact-manifest.json'))) {
            throw 'Diagnostic classification relabelled or overwrote historical artifacts.'
        }
    }
    Write-Host 'PASS: opt-in 660 diagnostics classify both suites and every archive member before execution, never candidate success.'
    Write-Host 'PASS: default660/legacy900 verdicts and real primary failures remain unchanged; prior archives cannot be relabelled.'
    $workflow = Get-Content "$root\.github\workflows\scale-verification.yml" -Raw
    $cpuInputs = [regex]::Matches($workflow, '(?ms)^      cpu_wait_trace:\r?\n(?<body>.*?)(?=^      [\w-]+:|^  [\w-]+:|\z)')
    if ($cpuInputs.Count -ne 2 -or @($cpuInputs | Where-Object {
        $_.Groups['body'].Value -notmatch '(?m)^        default: false\r?$'
    }).Count -or !$workflow.Contains('-CpuWaitTrace:([bool]::Parse($env:CPU_WAIT_TRACE))') -or
        !$workflow.Contains('.ci-results/ui-scale/**/*.etl') -or
        !$workflow.Contains('.ci-results/ui-scale/**/*.wprp')) {
        throw 'CPU/wait opt-in must default off and upload unchanged ETL/profile under the classified root.'
    }
    $diagnosticInputs = [regex]::Matches($workflow, '(?ms)^      diagnostic_only:\r?\n(?<body>.*?)(?=^      [\w-]+:|^  [\w-]+:|\z)')
    if ($diagnosticInputs.Count -ne 2 -or @($diagnosticInputs | Where-Object {
        $_.Groups['body'].Value -notmatch '(?m)^        default: false\r?$' -or
        $_.Groups['body'].Value -notmatch '(?m)^        type: boolean\r?$'
    }).Count) {
        throw 'Both workflow entry points must expose explicit default-false diagnostic_only input.'
    }
    if ($workflow -match '(?m)^\s*concurrency:') {
        throw 'Independent scale verification runs must not serialize or cancel one another on the same ref.'
    }
    foreach ($suite in @(
        @{ job = 'non-ui-performance'; command = 'Run-Performance.ps1'; artifact = 'non-ui-performance-' },
        @{ job = 'ui-scale'; command = 'Run-UiScale.ps1'; artifact = 'ui-scale-' }
    )) {
        $job = [regex]::Match($workflow, "(?ms)^  $($suite.job):\r?`n(?<body>.*?)(?=^  [\w-]+:|\z)").Groups['body'].Value
        $watchdog = if ($suite.job -eq 'ui-scale') {
            'timeout-minutes: ${{ inputs.extended_observation && 17 || 13 }}'
        } else { 'timeout-minutes: 13' }
        foreach ($required in @('runs-on: windows-latest', $watchdog, 'SCALE_STARTED_AT=',
            'TESTED_REVISION=', 'CANDIDATE_REVISION', 'if-no-files-found: error', $suite.artifact,
            ($suite.command + ' -ExpectedRevision $env:TESTED_REVISION'),
            'DIAGNOSTIC_ONLY: ${{ inputs.diagnostic_only || false }}',
            '-DiagnosticOnly:([bool]::Parse($env:DIAGNOSTIC_ONLY))', '$env:GITHUB_SHA -ne $revision',
            'CPU/wait opt-in requires BOTH companions nonaccepting and the unchanged660 window.')) {
            if (!$job.Contains($required)) { throw "$($suite.job) is missing $required" }
        }
        $guard = [regex]::Match($job, '(?ms)        run: \|\r?\n(?<guard>.*?)(?=          \$revision)').Groups['guard'].Value
        $previousDiagnostic = $env:DIAGNOSTIC_ONLY
        try {
            foreach ($case in @(@('false', 'false', 'false', $false), @('true', 'true', 'false', $false),
                @('true', 'false', 'false', $true), @('true', 'true', 'true', $true))) {
                $env:DIAGNOSTIC_ONLY = $case[1]
                $script = $guard.Replace('${{ inputs.cpu_wait_trace || false }}', $case[0]).
                    Replace('${{ inputs.extended_observation || false }}', $case[2])
                $rejected = $false
                try { & ([scriptblock]::Create($script)) } catch { $rejected = $true }
                if (!$guard -or $rejected -ne $case[3]) {
                    throw 'Both actual job guards must reject eligible or extended CPU/wait requests before workload execution.'
                }
            }
        } finally { $env:DIAGNOSTIC_ONLY = $previousDiagnostic }
        $prefix = if ($suite.job -eq 'ui-scale') {
            "(inputs.diagnostic_only || inputs.extended_observation) && 'diagnostic-' || ''"
        } else { "inputs.diagnostic_only && 'diagnostic-' || ''" }
        if (!$job.Contains($prefix) -or $job -notmatch '(?m)^    name: \$\{\{ inputs\.diagnostic_only &&') {
            throw 'Both diagnostic companion job and artifact names must be nonaccepting from dispatch.'
        }
        $upload = [regex]::Match($job, '(?ms)^      - uses: actions/upload-artifact@v4\r?\n(?<body>.*?)(?=^      -|\z)').Groups['body'].Value
        $archivePaths = @([regex]::Match($upload, '(?ms)^          path: \|\r?\n(?<paths>.*)\z').Groups['paths'].Value -split '\r?\n' |
            ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $archiveRoot = if ($suite.job -eq 'ui-scale') { '.ci-results/ui-scale' } else { '.ci-results/performance' }
        if ($archivePaths -notcontains "$archiveRoot/**/*.json" -or
            $archivePaths -notcontains "$archiveRoot/**/*.log" -or
            @($archivePaths | Where-Object { !$_.StartsWith("$archiveRoot/", [StringComparison]::Ordinal) }).Count) {
            throw 'Every uploaded member, including manifest JSON and primitive binary .log records, must remain inside its classified archive root.'
        }
        $entryTokens = $null
        $entryErrors = $null
        $entry = [Management.Automation.Language.Parser]::ParseFile(
            "$root\eng\$($suite.command)", [ref]$entryTokens, [ref]$entryErrors)
        $parameter = $entry.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'DiagnosticOnly' }
        $calls = @($entry.FindAll({
            param($node)
            $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Invoke-ScaleSuite'
        }, $true))
        $forward = @($calls.CommandElements | Where-Object {
            $_ -is [Management.Automation.Language.CommandParameterAst] -and
            $_.ParameterName -ceq 'DiagnosticOnly' -and
            $_.Argument -is [Management.Automation.Language.VariableExpressionAst] -and
            $_.Argument.VariablePath.UserPath -ceq 'DiagnosticOnly'
        })
        if ($entryErrors.Count -or !$parameter -or $parameter.Extent.Text -notmatch '\[switch\]' -or
            $calls.Count -ne 1 -or $forward.Count -ne 1) {
            throw 'Both actual entry points must forward their opt-in switch to the real common runner.'
        }
        if ($job -match '(?m)^\s+(needs|strategy):' -or
            $job -notmatch 'if:\s*\$\{\{\s*always\(\)\s*\}\}' -or
            $job -notmatch 'ref: \$\{\{ inputs.revision \|\| github.sha \}\}') {
            throw 'Separate suites must run independently at the explicit candidate with failure artifacts.'
        }
    }
    $runner = Get-Content "$root\eng\Run-UiScale.ps1" -Raw
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile("$root\eng\Run-UiScale.ps1", [ref]$tokens, [ref]$parseErrors)
    $selection = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$displayCommand'
    }, $true)
    if (!$selection -or $parseErrors.Count) { throw 'UI-scale display selection could not be inspected.' }
    $selectDisplay = [scriptblock]::Create($selection.Extent.Text + '; $displayCommand')
    $previousDisplayEnvironment = @($env:GITHUB_ACTIONS, $env:RUNNER_ENVIRONMENT, $env:RUNNER_OS)
    try {
        foreach ($case in @(
            @('true', 'github-hosted', 'Windows', '--prepare-high-resolution'),
            @('true', 'self-hosted', 'Windows', '--verify-high-resolution'),
            @('true', 'github-hosted', 'Linux', '--verify-high-resolution'),
            @('', '', '', '--verify-high-resolution')
        )) {
            $env:GITHUB_ACTIONS, $env:RUNNER_ENVIRONMENT, $env:RUNNER_OS = $case[0..2]
            if ((& $selectDisplay) -cne $case[3]) {
                throw "UI-scale display selection must prepare only disposable hosted Windows; actual environment: $($case[0..2] -join ', ')."
            }
        }
    } finally { $env:GITHUB_ACTIONS, $env:RUNNER_ENVIRONMENT, $env:RUNNER_OS = $previousDisplayEnvironment }
    Write-Host 'PASS: actual UI-scale selection prepares only GitHub-hosted Windows and verifies other authorized desktops read-only.'
    foreach ($required in @('Assert-DesktopResults', 'UiScaleScenarios.json', "'--ui-scale'", 'Invoke-ScaleSuite')) {
        if (!$runner.Contains($required)) { throw "UI-scale entry point is missing $required" }
    }
    $shared = Get-Content "$root\eng\ScaleRunner.ps1" -Raw
    if (!$shared.Contains('$process.Kill($true)')) { throw 'Shared scale execution lost owned process-tree termination.' }
    if (!$shared.Contains('$ceilingSeconds = 660') -or !$shared.Contains("'diagnostic_complete'") -or
        !$workflow.Contains("(inputs.diagnostic_only || inputs.extended_observation) && 'diagnostic-' || ''") -or
        !$runner.Contains('[switch] $ExtendedObservation')) {
        throw 'Extended observation must stay explicit, non-verifying and separate from the unchanged pass/fail budget.'
    }
    $ciRunner = Get-Content "$root\eng\Run-CiLane.ps1" -Raw
    if (!$ciRunner.Contains('& "$root\tests\UiScale.Tests.ps1"')) {
        throw 'Required core execution must verify the UI-scale command contract without launching UI.'
    }
    $ci = Get-Content "$root\.github\workflows\ci.yml" -Raw
    if ($ci.Contains('Run-UiScale.ps1') -or !$ci.Contains('needs: [core, filesystem, desktop, high-resolution]')) {
        throw 'UI-scale execution must reuse the manual bridge, not become a required PR smoke lane.'
    }
    Write-Host 'PASS: both independent scale suites reuse exact-candidate nightly/manual execution outside required PR smoke.'
} finally { Remove-Item $results -Recurse -Force }
