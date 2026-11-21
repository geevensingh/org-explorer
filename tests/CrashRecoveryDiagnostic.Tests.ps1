$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$tokens = $null
$errors = $null
$runner = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'eng\Run-CrashRecoveryDiagnostic.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Crash diagnostic runner syntax is invalid: $errors" }
foreach ($name in @('Write-CrashDiagnosticRecord', 'Report-CrashDiagnosticFailure',
    'Write-CrashDiagnosticFinalRecord', 'Get-CrashDiagnosticCaseNames',
    'Read-CrashDiagnosticTrx', 'Invoke-CrashDiagnosticProcess')) {
    $function = $runner.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (!$function) { throw "Missing actual diagnostic contract $name." }
    . ([scriptblock]::Create($function.Extent.Text))
}
$workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\scale-verification.yml') -Raw
if ($workflow -notmatch '(?m)^  workflow_dispatch:\s*$' -or
    $workflow -match '(?m)^  (push|pull_request|workflow_call|schedule):' -or
    @([regex]::Matches($workflow, '(?m)^  [a-z-]+:\s*$') | Where-Object { $_.Index -gt $workflow.IndexOf('jobs:') }).Count -ne 1 -or
    $workflow -notmatch 'timeout-minutes: 11' -or $workflow -notmatch 'runs-on: windows-latest') {
    throw 'Crash diagnostics must remain one manual-only Windows job with the original eleven-minute watchdog.'
}
if ($workflow -match 'Run-CiLane|Run-UiScale|Run-Performance|release|matrix:') {
    throw 'The temporary diagnostic must not select normal/native/performance/release lanes.'
}
if ($workflow -notmatch '(?m)^name: TEMP focused CrashRecovery\r?$' -or
    $workflow -notmatch 'expected_revision:' -or $workflow -notmatch 'CRASH_DIAGNOSTIC_EXPECTED_REVISION:' -or
    (Test-Path -LiteralPath (Join-Path $root '.github\workflows\crash-recovery-diagnostic.yml'))) {
    throw 'The diagnostic must use the registered optional path and exact-SHA dispatch guard, not a new workflow filename.'
}
$source = $runner.Extent.Text
if ($source -notmatch "FullyQualifiedName=OrgExplorer.Persistence.Tests.SqliteWorkspaceTests.Abrupt_process_death" -or
    $source -notmatch 'console;verbosity=detailed' -or $source -notmatch "'--locked-mode'" -or
    $source -notmatch 'acceptanceEligible = \$false' -or $source -notmatch 'ceilingSeconds = 660' -or
    $source -notmatch '\$revision -cne \$env:CRASH_DIAGNOSTIC_EXPECTED_REVISION') {
    throw 'The runner lost exact selection, raw output, normal locked restore, nonacceptance or filesystem budget.'
}
$results = Join-Path $root ('.ci-results\crash-contract-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $results
$trx = Join-Path $results 'fixture.trx'
$record = Join-Path $results 'cases.json'
function Write-TrxFixture([string[]]$Names, [string]$Outcome = 'Passed', [int]$Total = 9) {
    $rows = foreach ($name in $Names) {
        $escaped = [Security.SecurityElement]::Escape($name)
        "<UnitTestResult testName=`"$escaped`" outcome=`"$Outcome`" duration=`"00:00:00.001`" executionId=`"$([guid]::NewGuid())`"><Output><StdOut>SQLITE-CRASH-REOPEN-FAILURE exact retained payload</StdOut><ErrorInfo><Message>Original error</Message><StackTrace>Original stack</StackTrace></ErrorInfo></Output></UnitTestResult>"
    }
    "<TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results>$($rows -join '')</Results><ResultSummary outcome=`"Completed`"><Counters total=`"$Total`" executed=`"9`" passed=`"9`"/></ResultSummary></TestRun>" |
        Set-Content -LiteralPath $trx
}
function Require-Rejection([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw "Expected rejection matching $Pattern."
}
try {
    $guard = $runner.Find({
        param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text.Contains("throw 'This temporary diagnostic requires manual hosted Windows execution")
    }, $true)
    if (!$guard) { throw 'Missing exact manual workflow/checkout/input guard.' }
    $environmentNames = @('GITHUB_ACTIONS', 'GITHUB_EVENT_NAME', 'GITHUB_SHA',
        'CRASH_DIAGNOSTIC_WORKFLOW_SHA', 'CRASH_DIAGNOSTIC_EXPECTED_REVISION')
    $previous = @{}
    foreach ($name in $environmentNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
    try {
        $revision = '1111111111111111111111111111111111111111'
        $env:GITHUB_ACTIONS = 'true'
        $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
        $env:GITHUB_SHA = $revision
        $env:CRASH_DIAGNOSTIC_WORKFLOW_SHA = $revision
        $env:CRASH_DIAGNOSTIC_EXPECTED_REVISION = $revision
        $check = [scriptblock]::Create($guard.Extent.Text)
        & $check
        foreach ($case in @(
            @{ name = 'GITHUB_EVENT_NAME'; value = 'schedule' },
            @{ name = 'GITHUB_SHA'; value = '2222222222222222222222222222222222222222' },
            @{ name = 'CRASH_DIAGNOSTIC_WORKFLOW_SHA'; value = '2222222222222222222222222222222222222222' },
            @{ name = 'CRASH_DIAGNOSTIC_EXPECTED_REVISION'; value = 'main' },
            @{ name = 'CRASH_DIAGNOSTIC_EXPECTED_REVISION'; value = '2222222222222222222222222222222222222222' }
        )) {
            $original = [Environment]::GetEnvironmentVariable($case.name)
            [Environment]::SetEnvironmentVariable($case.name, $case.value)
            Require-Rejection $check 'exact workflow/checkout SHA'
            [Environment]::SetEnvironmentVariable($case.name, $original)
        }
    } finally {
        foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    }
    $names = @(Get-CrashDiagnosticCaseNames)
    Write-TrxFixture $names
    Read-CrashDiagnosticTrx $trx $record
    $saved = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    if (!$saved.diagnosticOnly -or $saved.acceptanceEligible -or $saved.cases.Count -ne 9 -or
        $saved.cases[0].stdout -cne 'SQLITE-CRASH-REOPEN-FAILURE exact retained payload') {
        throw 'The actual parser lost raw diagnostic output or created acceptance-shaped records.'
    }
    Require-Rejection { Read-CrashDiagnosticTrx (Join-Path $results 'missing.trx') $record } 'TRX is missing'
    Write-TrxFixture $names[0..7]
    Require-Rejection { Read-CrashDiagnosticTrx $trx $record } 'exactly the original nine'
    Write-TrxFixture @($names[0..7] + $names[0])
    Require-Rejection { Read-CrashDiagnosticTrx $trx $record } 'exactly the original nine'
    Write-TrxFixture @($names[0..7] + 'Different case')
    Require-Rejection { Read-CrashDiagnosticTrx $trx $record } 'exactly the original nine'
    Write-TrxFixture $names 'Passed' 10
    Require-Rejection { Read-CrashDiagnosticTrx $trx $record } 'account for all nine'
    Write-TrxFixture $names
    (Get-Content $trx -Raw).Replace('passed="9"', 'passed="9" aborted="1"') | Set-Content $trx
    Require-Rejection { Read-CrashDiagnosticTrx $trx $record } 'unaccounted failure'
    Write-TrxFixture $names
    (Get-Content $trx -Raw).Replace('outcome="Completed"', 'outcome="Aborted"') | Set-Content $trx
    Require-Rejection { Read-CrashDiagnosticTrx $trx $record } 'did not complete normally'
    foreach ($outcome in @('Failed', 'NotExecuted')) {
        Write-TrxFixture $names $outcome
        Require-Rejection { Read-CrashDiagnosticTrx $trx $record } 'did not pass'
        $saved = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
        if ($saved.cases[0].outcome -cne $outcome -or $saved.cases[0].error -cne 'Original error') {
            throw 'A rejected original case lost its actual error or outcome.'
        }
    }
    $steps = [Collections.Generic.List[object]]::new()
    $pwsh = (Get-Process -Id $PID).Path
    $expired = [DateTimeOffset]::UtcNow.AddSeconds(-661)
    Require-Rejection {
        Invoke-CrashDiagnosticProcess $pwsh @('-NoProfile', '-Command', 'exit 0') expired $results $expired 660 $steps
    } 'including setup.*exhausted'
    if ($steps.Count) { throw 'An expired filesystem budget launched work.' }
    Invoke-CrashDiagnosticProcess $pwsh @('-NoProfile', '-Command', "'exact stdout'; [Console]::Error.WriteLine('exact stderr')") passed $results ([DateTimeOffset]::UtcNow) 10 $steps
    Require-Rejection {
        Invoke-CrashDiagnosticProcess $pwsh @('-NoProfile', '-Command', "'failed stdout'; exit 7") failed $results ([DateTimeOffset]::UtcNow) 10 $steps
    } 'failed with exit code 7'
    Require-Rejection {
        Invoke-CrashDiagnosticProcess $pwsh @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') timeout $results ([DateTimeOffset]::UtcNow) 1 $steps
    } 'exceeded.*budget'
    if ($steps.Count -ne 3 -or $steps[0].outcome -cne 'Passed' -or $steps[1].outcome -cne 'Failed' -or
        $steps[2].outcome -cne 'TimedOut' -or (Get-Content (Join-Path $results 'passed-errors.log') -Raw).Trim() -cne 'exact stderr' -or
        (Get-Content (Join-Path $results 'failed.log') -Raw).Trim() -cne 'failed stdout') {
        throw 'Real bounded process execution lost stdout/stderr, failure or timeout.'
    }
    if (Get-Process -Id $steps[2].processId -ErrorAction SilentlyContinue) { throw 'The timed-out owned process survived cleanup.' }
    foreach ($exit in @(7, 0)) {
        $name = "blocked-output-$exit"
        $path = Join-Path $results "$name.log"
        $heldLog = [IO.FileStream]::new($path, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stderr = [IO.StringWriter]::new()
        $previousError = [Console]::Error
        [Console]::SetError($stderr)
        try {
            $primary = $null
            try {
                Invoke-CrashDiagnosticProcess $pwsh @('-NoProfile', '-Command', "'retained raw output'; exit $exit") $name $results ([DateTimeOffset]::UtcNow) 10 $steps
            } catch { $primary = $_ }
            $pattern = if ($exit -eq 7) { 'failed with exit code 7' } else { 'being used by another process' }
            if (!$primary -or $primary.Exception.Message -notmatch $pattern -or
                $steps[-1].outcome -cne 'Failed' -or $steps[-1].stdoutSaved -or
                $steps[-1].secondaryFailures[0].phase -cne 'stdout-persistence' -or
                $stderr.ToString() -notmatch 'CRASH-DIAGNOSTIC-SECONDARY-FAILURE.*stdout-persistence') {
                throw "Blocked-log contract lost the primary failure or explicit unsaved output for exit $exit."
            }
            if (Get-Process -Id $steps[-1].processId -ErrorAction SilentlyContinue) {
                throw 'Diagnostic-output failure left its owned process alive.'
            }
            $executionPath = Join-Path $results "blocked-execution-$exit.json"
            $heldRecord = [IO.FileStream]::new($executionPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try {
                $execution = [ordered]@{ complete = $false; outcome = 'Failed'; failure = $primary.Exception.Message }
                $caught = $null
                try { throw $primary }
                finally { Write-CrashDiagnosticFinalRecord $executionPath $execution $primary }
            } catch { $caught = $_ }
            finally { $heldRecord.Dispose() }
            if (!$caught -or $caught.Exception.Message -cne $primary.Exception.Message -or
                $caught.Exception.HResult -ne $primary.Exception.HResult -or
                $caught.Exception.GetType() -ne $primary.Exception.GetType() -or
                $stderr.ToString() -notmatch 'execution-record-persistence' -or
                @($primary.Exception.Data['CrashDiagnosticFailures']).Count -lt 2) {
                throw 'A blocked final execution record replaced or hid the original failure.'
            }
            if ($exit -eq 0) {
                $heldRecord = [IO.FileStream]::new($executionPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                try {
                    Require-Rejection {
                        Write-CrashDiagnosticFinalRecord $executionPath ([ordered]@{ complete = $true; outcome = 'DiagnosticNonreproduction' }) $null
                    } 'being used by another process'
                } finally { $heldRecord.Dispose() }
            }
        } finally {
            [Console]::SetError($previousError)
            Write-Host $stderr.ToString()
            $stderr.Dispose()
            $heldLog.Dispose()
        }
    }
    Write-CrashDiagnosticRecord (Join-Path $results 'execution.json') ([ordered]@{
        diagnosticOnly = $false; acceptanceEligible = $true; complete = $false; steps = $steps.ToArray()
    })
    $saved = Get-Content (Join-Path $results 'execution.json') -Raw | ConvertFrom-Json
    if (!$saved.diagnosticOnly -or $saved.acceptanceEligible) { throw 'Diagnostic records allowed acceptance flags to be changed.' }
    Write-Host 'PASS: actual diagnostic parser requires the exact nine identities/outcomes and preserves payloads; real processes retain failure/output and enforce setup-inclusive budgets. Manual-only Windows/nonacceptance contracts remain.'
} finally {
    Remove-Item -LiteralPath $results -Recurse -Force
}
