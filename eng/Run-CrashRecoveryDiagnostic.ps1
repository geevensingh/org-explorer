param([switch]$InitializeOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-CrashDiagnosticRecord([string]$Path, [System.Collections.IDictionary]$Record) {
    $Record['diagnosticOnly'] = $true
    $Record['acceptanceEligible'] = $false
    $Record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-CrashDiagnosticCaseNames {
    $method = 'OrgExplorer.Persistence.Tests.SqliteWorkspaceTests.Abrupt_process_death_at_actual_SQLite_commit_boundaries_recovers_only_committed_writing'
    foreach ($variant in @(
        @('before', 'Person', 'False', 'False'), @('after', 'Person', 'False', 'False'),
        @('before', 'Team', 'False', 'False'), @('after', 'Team', 'False', 'False'),
        @('before', 'profile', 'False', 'False'), @('after', 'profile', 'False', 'False'),
        @('before', 'profile', 'True', 'False'), @('before', 'profile', 'False', 'True'),
        @('after', 'profile', 'False', 'True')
    )) {
        '{0}(boundary: "{1}", mutation: "{2}", unresolvedEvidence: {3}, receiptHeld: {4})' -f $method, $variant[0], $variant[1], $variant[2], $variant[3]
    }
}

function Read-CrashDiagnosticTrx([string]$Path, [string]$RecordPath) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'The original-nine diagnostic TRX is missing.' }
    $xml = [xml]::new()
    $xml.XmlResolver = $null
    $xml.Load($Path)
    $rows = @($xml.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']") | ForEach-Object {
        [ordered]@{
            testName = $_.GetAttribute('testName'); outcome = $_.GetAttribute('outcome')
            duration = $_.GetAttribute('duration'); executionId = $_.GetAttribute('executionId')
            stdout = $_.SelectSingleNode("./*[local-name()='Output']/*[local-name()='StdOut']")?.InnerText
            error = $_.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")?.InnerText
            stack = $_.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='StackTrace']")?.InnerText
        }
    })
    Write-CrashDiagnosticRecord $RecordPath ([ordered]@{
        cases = $rows; trxSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        stdoutLimit = 'Null per-case TRX stdout is not proof of no output. Full detailed test stdout/stderr and the original TRX are retained without filtering.'
    })
    $expected = @(Get-CrashDiagnosticCaseNames)
    if ($rows.Count -ne 9 -or @($rows.testName | Select-Object -Unique).Count -ne 9 -or
        @(Compare-Object $expected @($rows.testName) -CaseSensitive).Count) {
        throw 'Diagnostic TRX must contain exactly the original nine case identities, without duplicates or replacements.'
    }
    $counters = $xml.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if (!$counters -or $counters.GetAttribute('total') -cne '9' -or $counters.GetAttribute('executed') -cne '9') {
        throw 'Diagnostic TRX must account for all nine executed cases.'
    }
    if (@($rows | Where-Object outcome -cne 'Passed').Count -or $counters.GetAttribute('passed') -cne '9') {
        throw 'At least one original crash-recovery case did not pass. Diagnostic evidence is retained; recovery remains unresolved.'
    }
    foreach ($attribute in $counters.Attributes) {
        if ($attribute.Name -notin @('total', 'executed', 'passed') -and $attribute.Value -cne '0') {
            throw 'Diagnostic TRX counters contain an unaccounted failure or nonexecuted outcome.'
        }
    }
    $summary = $xml.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']")
    if ($summary.GetAttribute('outcome') -cne 'Completed') { throw 'Diagnostic TRX did not complete normally.' }
}

function Invoke-CrashDiagnosticProcess {
    param(
        [string]$FilePath, [string[]]$Arguments, [string]$Name, [string]$Results,
        [DateTimeOffset]$Started, [double]$CeilingSeconds,
        [System.Collections.Generic.List[object]]$Steps
    )
    $remaining = $CeilingSeconds - ([DateTimeOffset]::UtcNow - $Started).TotalSeconds
    if ($remaining -le 0) { throw 'Crash diagnostic filesystem budget (including setup) is exhausted.' }
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $outcome = 'Failed'
    try {
        if (!$process.WaitForExit([int][Math]::Min([int]::MaxValue, $remaining * 1000))) {
            $outcome = 'TimedOut'
            throw "$Name exceeded the remaining filesystem diagnostic budget."
        }
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
        $outcome = 'Passed'
    } finally {
        try {
            if (!$process.HasExited) {
                $process.Kill($true)
                if (!$process.WaitForExit(5000)) { throw 'The owned diagnostic process tree did not terminate.' }
            }
            if (!$output.Wait(5000) -or !$errors.Wait(5000)) { throw 'Owned diagnostic output capture did not finish.' }
            [IO.File]::WriteAllText((Join-Path $Results "$Name.log"), $output.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $Results "$Name-errors.log"), $errors.GetAwaiter().GetResult())
            Get-Content -LiteralPath (Join-Path $Results "$Name.log")
            Get-Content -LiteralPath (Join-Path $Results "$Name-errors.log")
        } finally {
            $Steps.Add([ordered]@{
                name = $Name; outcome = $outcome; seconds = $timer.Elapsed.TotalSeconds
                processId = $process.Id; arguments = $Arguments
            })
            $process.Dispose()
        }
    }
}

$root = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
$results = Join-Path $root '.ci-results\crash-recovery-diagnostic'
$null = New-Item -ItemType Directory -Path $results -Force
$started = [DateTimeOffset]::Parse($env:LANE_STARTED_AT)
$baseline = 'ab3c63c68d1cf8cbf9d9aa732d4dded95cb108fb'
$expectedSource = '08e9b32215b1d32dcb5e2eae288602c3dc7f52fb'
$revision = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify diagnostic checkout.' }
$execution = [ordered]@{
    diagnosticOnly = $true; acceptanceEligible = $false; complete = $false
    outcome = 'Prepared'; failure = $null; revision = $revision
    workflowRevision = $env:CRASH_DIAGNOSTIC_WORKFLOW_SHA
    expectedRevision = $env:CRASH_DIAGNOSTIC_EXPECTED_REVISION
    workflowPath = '.github/workflows/scale-verification.yml'; registeredWorkflowId = 413877982
    normalApplicationBaseline = $baseline; expectedSourceTree = $expectedSource
    runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
    startedAt = $started.ToString('O'); ceilingSeconds = 660; jobWatchdogMinutes = 11
    expectedCases = @(Get-CrashDiagnosticCaseNames); steps = @()
    interpretation = 'A passing focused run is diagnostic nonreproduction only, never recovery acceptance or exoneration.'
}
Write-CrashDiagnosticRecord (Join-Path $results 'execution.json') $execution
$steps = [Collections.Generic.List[object]]::new()
try {
    if (!([OperatingSystem]::IsWindows()) -or $env:GITHUB_ACTIONS -cne 'true' -or
        $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $revision -cne $env:GITHUB_SHA -or
        $revision -cne $env:CRASH_DIAGNOSTIC_WORKFLOW_SHA -or
        $env:CRASH_DIAGNOSTIC_EXPECTED_REVISION -cnotmatch '^[0-9a-f]{40}$' -or
        $revision -cne $env:CRASH_DIAGNOSTIC_EXPECTED_REVISION) {
        throw 'This temporary diagnostic requires manual hosted Windows execution at the exact workflow/checkout SHA.'
    }
    if ($env:GITHUB_RUN_ID -notmatch '^\d+$' -or $env:GITHUB_RUN_ATTEMPT -notmatch '^\d+$' -or
        @(git status --porcelain=v1 --untracked-files=all).Count) { throw 'Diagnostic identity or clean checkout is invalid.' }
    git merge-base --is-ancestor $baseline HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostic checkout does not preserve its approved normal baseline.' }
    $source = git rev-parse 'HEAD:src'
    if ($LASTEXITCODE -ne 0 -or $source -cne $expectedSource) { throw 'Production source differs from approved ab3 baseline.' }
    $execution['sourceTree'] = $source
    $execution['testTree'] = git rev-parse 'HEAD:tests'
    $execution['workflowHash'] = (Get-FileHash -LiteralPath (Join-Path $root '.github\workflows\scale-verification.yml')).Hash
    $execution['runnerHash'] = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    if (Test-Path -LiteralPath (Join-Path $results 'crash-recovery.trx')) {
        throw 'Diagnostic output contains an earlier TRX; refusing to reuse or delete previous evidence.'
    }
    if (([DateTimeOffset]::UtcNow - $started).TotalSeconds -gt 660) { throw 'Crash diagnostic filesystem budget (including setup) is exhausted.' }
    if ($InitializeOnly) { return }
    $execution['outcome'] = 'Running'
    Write-CrashDiagnosticRecord (Join-Path $results 'execution.json') $execution
    $build = Join-Path $results 'build'
    $project = 'tests\OrgExplorer.Persistence.Tests'
    Invoke-CrashDiagnosticProcess dotnet @('--info') environment $results $started 660 $steps
    Invoke-CrashDiagnosticProcess dotnet @('restore', '--artifacts-path', $build, $project, '--locked-mode') restore $results $started 660 $steps
    Invoke-CrashDiagnosticProcess dotnet @('build', '--artifacts-path', $build, $project, '-c', 'Release', '--no-restore') build $results $started 660 $steps
    $binaryRoot = Join-Path $build 'bin\OrgExplorer.Persistence.Tests\release'
    $binaries = @('OrgExplorer.Persistence.Tests.dll', 'OrgExplorer.Workflow.dll',
        'OrgExplorer.Storage.ProcessFixture.dll', 'runtimes\win-x64\native\e_sqlite3.dll') | ForEach-Object {
        $file = Get-Item -LiteralPath (Join-Path $binaryRoot $_)
        [ordered]@{ file = $_; sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash
            bytes = $file.Length; productVersion = $file.VersionInfo.ProductVersion }
    }
    $native = $binaries[-1]
    foreach ($binary in $binaries[0..2]) {
        if (!$binary.productVersion.EndsWith("+$revision", [StringComparison]::Ordinal)) {
            throw 'A diagnostic test, Workflow or process-fixture binary is not stamped with the exact checkout.'
        }
    }
    if ($native.sha256 -cne '6AD8E149F8CE3ED3716402B4B3A2268EBBDC7B64391B5FAFED747E03BB1B9418') {
        throw 'Native SQLite asset differs from the approved, previously identified crash baseline.'
    }
    Write-CrashDiagnosticRecord (Join-Path $results 'identity.json') ([ordered]@{
        revision = $revision; workflowRevision = $env:CRASH_DIAGNOSTIC_WORKFLOW_SHA
        sourceTree = $source; normalApplicationBaseline = $baseline
        binaries = $binaries; sdk = Get-Content -LiteralPath (Join-Path $results 'environment.log') -Raw
        nativeIdentity = 'Bundled exact-hash SQLite 3.53.4; source bf7c7f30031888f4e796e429ab3978879485813aaca6f641c7b33e4e09459bcc, previously queried through the actual provider. This is asset identity, not a new runtime query, loaded-module hook or WAL/FULL inference.'
        desktopProduct = 'Not rebuilt or launched: normal application source baseline is exact ab3; this job executes Workflow and the existing storage child only.'
    })
    $testFailure = $null
    try {
        Invoke-CrashDiagnosticProcess dotnet @('test', '--artifacts-path', $build, $project, '-c', 'Release',
            '--no-build', '--no-restore', '--filter',
            'FullyQualifiedName=OrgExplorer.Persistence.Tests.SqliteWorkspaceTests.Abrupt_process_death_at_actual_SQLite_commit_boundaries_recovers_only_committed_writing',
            '--logger', 'console;verbosity=detailed', '--logger', 'trx;LogFileName=crash-recovery.trx',
            '--results-directory', $results) test $results $started 660 $steps
    } catch { $testFailure = $_ }
    $resultFailure = $null
    try { Read-CrashDiagnosticTrx (Join-Path $results 'crash-recovery.trx') (Join-Path $results 'cases.json') }
    catch { $resultFailure = $_ }
    if ($testFailure) {
        if ($resultFailure) { $execution['resultFailure'] = $resultFailure.Exception.Message }
        throw $testFailure
    }
    if ($resultFailure) { throw $resultFailure }
    if (([DateTimeOffset]::UtcNow - $started).TotalSeconds -gt 660) { throw 'Complete diagnostic execution exceeded 660 seconds including setup.' }
    $execution['complete'] = $true
    $execution['outcome'] = 'DiagnosticNonreproduction'
} catch {
    $execution['outcome'] = 'Failed'
    $execution['failure'] = $_.Exception.Message
    throw
} finally {
    $execution['steps'] = @($steps.ToArray())
    $execution['executionSeconds'] = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
    Write-CrashDiagnosticRecord (Join-Path $results 'execution.json') $execution
}
