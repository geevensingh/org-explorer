param(
    [Parameter(Mandatory)][string] $NativeManifest,
    [ValidateSet('diagnostic', 'control')][string] $Variant = 'diagnostic',
    [string] $ResultsDirectory = '.ci-results\sqlite-native-stage2',
    [switch] $BuildNative
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$results = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $results -Force | Out-Null
$record = [ordered]@{
    schemaVersion = 1; diagnosticOnly = $true; normalCiAttested = $false
    variant = $Variant; candidate = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT; status = 'Incomplete'; testStarted = $false
    testExitCode = $null; selectedResults = 0; nativeSha256 = $null
    outsideActors = 'Unknown'; contentThroughRead = 'Unknown'
}
function Invoke-BoundedDotnet([string] $Name, [string[]] $Arguments, [int] $Seconds) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errorOutput = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit($Seconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Name exceeded its diagnostic execution bound."
        }
        return $process.ExitCode
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $results "$Name.log"), $output.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $results "$Name-errors.log"), $errorOutput.GetAwaiter().GetResult())
        $process.Dispose()
    }
}
try {
    if (!$IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or
        $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
        $record.status = 'RefusedHost'
        throw 'Stage2 requires the isolated hosted Windows diagnostic route.'
    }
    $revision = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -cne $env:GITHUB_SHA -or
        $env:GITHUB_REPOSITORY -cne 'geevensingh/org-explorer') {
        $record.status = 'RefusedCandidate'
        throw 'Stage2 candidate/repository does not match the dispatch.'
    }
    $build = Join-Path $results 'build'
    $project = 'tests\OrgExplorer.Persistence.Tests'
    if ((Invoke-BoundedDotnet restore @('restore', $project, '--locked-mode', '--artifacts-path', $build) 180) -ne 0) {
        throw 'Stage2 locked restore failed.'
    }
    if ((Invoke-BoundedDotnet build @('build', $project, '-c', 'Release', '--no-restore',
        '--artifacts-path', $build, '--verbosity', 'quiet') 180) -ne 0) {
        throw 'Stage2 unchanged managed build failed.'
    }
    $asset = Join-Path $build 'bin\OrgExplorer.Persistence.Tests\release\runtimes\win-x64\native\e_sqlite3.dll'
    $original = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash
    if ($original -cne '922AB7FF7E47EF2694CA5F62ADBEFCC44C637BF5D082488C286816ED5D06363F') {
        throw 'The original pinned win-x64 native package asset differs from the investigated identity.'
    }
    if ($BuildNative) {
        & (Join-Path $PSScriptRoot 'Build-SqliteStage2Native.ps1') `
            -ArtifactsDirectory ([IO.Path]::GetFullPath((Split-Path $NativeManifest -Parent))) `
            -OriginalNativePath $asset -ExpectedRevision $revision
    }
    $manifest = Get-Content -LiteralPath $NativeManifest -Raw | ConvertFrom-Json
    $record.manifestSha256 = (Get-FileHash -LiteralPath $NativeManifest -Algorithm SHA256).Hash
    if ($manifest.candidate -cne $revision -or $manifest.sourceRelease -cne '3.54.1' -or
        $manifest.sourceId -cne '2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76' -or
        !$manifest.diagnosticOnly -or $manifest.normalCiAttested -or !$manifest.standardOriginalExportSurfacePreserved) {
        throw 'Stage2 native build is not bound to the original source release/dispatch/export surface.'
    }
    $selected = $manifest.$Variant
    if (!$selected.path -or !$selected.sha256 -or
        (Get-FileHash -LiteralPath $selected.path -Algorithm SHA256).Hash -cne $selected.sha256) {
        throw 'Stage2 selected native binary lacks an exact hash binding.'
    }
    Copy-Item -LiteralPath $selected.path -Destination $asset
    $record.nativeSha256 = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash
    if ($record.nativeSha256 -cne $selected.sha256) { throw 'Stage2 native substitution hash mismatch.' }
    $env:ORGEXPLORER_SQLITE_STAGE2 = if ($Variant -eq 'diagnostic') { '1' } else { '0' }
    $env:ORGEXPLORER_SQLITE_STAGE2_NATIVE_DLL = $asset
    $env:ORGEXPLORER_SQLITE_STAGE2_NATIVE_SHA256 = $record.nativeSha256
    $env:ORGEXPLORER_SQLITE_STAGE2_RESULTS = Join-Path $results 'capture'
    $filter = 'FullyQualifiedName=OrgExplorer.Persistence.Tests.SqliteWorkspaceTests.Stale_SHM_recovery_preserves_committed_bytes_with_a_writable_file_handle_retained&DisplayName~workspace.sqlite-wal'
    $record.filter = $filter
    $record.testStarted = $true
    $record.testExitCode = Invoke-BoundedDotnet exact-case @('test', $project, '-c', 'Release',
        '--no-build', '--no-restore', '--artifacts-path', $build, '--filter', $filter,
        '--logger', 'trx;LogFileName=exact-case.trx', '--results-directory', $results, '--verbosity', 'quiet') 120
    [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'exact-case.trx') -Raw
    $tests = @($trx.TestRun.Results.UnitTestResult)
    $record.selectedResults = $tests.Count
    if ($tests.Count -ne 1 -or $tests[0].testName -notlike '*workspace.sqlite-wal*') {
        throw 'The investigative selection did not execute exactly the original retained-WAL case.'
    }
    $record.testOutcome = $tests[0].outcome
    if ($Variant -eq 'diagnostic') {
        $bindings = @(Get-ChildItem -LiteralPath $env:ORGEXPLORER_SQLITE_STAGE2_RESULTS -Recurse -Filter fixture-binding.json)
        if ($bindings.Count -ne 1) { throw 'Stage2 lacks exactly one original-fixture scope binding.' }
        $binding = Get-Content -LiteralPath $bindings[0].FullName -Raw | ConvertFrom-Json
        if (!$binding.complete -or $binding.candidate -cne $revision -or
            $binding.nativeSha256 -cne $record.nativeSha256 -or !$binding.firstRevision) {
            throw 'Stage2 capture binding is incomplete or does not match the candidate/native/first-read boundary.'
        }
        $verdict = Get-Content -LiteralPath (Join-Path $bindings[0].DirectoryName 'native-verdict.json') -Raw | ConvertFrom-Json
        if (!$verdict.complete) { throw 'Stage2 critical records are incomplete; no causal verdict is supported.' }
    }
    $record.status = if ($record.testOutcome -eq 'Failed') { 'CapturedOriginalCaseFailure' } else { 'DiagnosticPassNotAcceptance' }
}
catch {
    $record.error = $_.Exception.Message
    Write-Error $_ -ErrorAction Continue
}
finally {
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $results 'execution.json')
}
if ($record.status -eq 'CapturedOriginalCaseFailure') { exit 1 }
if ($record.status -eq 'DiagnosticPassNotAcceptance') { exit 0 }
exit 2
