$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
. "$root\eng\LaneHealth.ps1"
$fixture = Join-Path $root ('.test-workspaces\mh-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$remote = Join-Path $fixture 'remote.git'
$evidence = Join-Path $fixture 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
git init --bare --quiet $remote
$global:MixedHealthFixture = @{
    run = @{ id = 71; run_attempt = 1; head_sha = 'a' * 40; path = '.github/workflows/ci.yml'
        event = 'push'; head_branch = 'main'; status = 'completed'; head_repository = @{ full_name = 'geevensingh/org-explorer' } }
    lane = 'core'; evidence = $evidence
}
function gh {
    $a = @($args)
    $global:LASTEXITCODE = 0
    $f = $global:MixedHealthFixture
    if ($a[0] -ceq 'run') {
        $directory = $a[[Array]::IndexOf($a, '--dir') + 1]
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Copy-Item (Join-Path $f.evidence '*') $directory
    } elseif ($a[1] -cmatch '/git/trees/[0-9a-f]{40}\?recursive=1$') {
        @{ truncated = $false; tree = @() } | ConvertTo-Json -Depth 5
    } elseif ($a[1] -cmatch '/jobs\?') {
        ConvertTo-Json -InputObject @(@{ jobs = @(@{ id = 172
            name = if ($f.lane -ceq 'core') { 'Core and view models' } else { 'Real filesystem persistence' }
            status = 'completed'; conclusion = 'success'; started_at = '2026-11-25T10:00:00Z'
            completed_at = '2026-11-25T10:02:00Z'; steps = @() }) }) -Depth 10
    } elseif ($a[1] -cmatch '/artifacts\?') {
        ConvertTo-Json -InputObject @(@{ artifacts = @(@{ id = 272; name = "$($f.lane)-attempt-$($f.run.run_attempt)"
            expired = $false; digest = 'sha256:' + ('b' * 64); expires_at = '2099-01-01T00:00:00Z'
            workflow_run = @{ id = 71; head_sha = $f.run.head_sha } }) }) -Depth 10
    } else { $f.run | ConvertTo-Json -Depth 10 }
}
function Run-Observation([string]$Lane, [int]$Attempt, [switch]$Unqualified) {
    $f = $global:MixedHealthFixture
    $f.lane = $Lane; $f.run.run_attempt = $Attempt
    $f.run.event = if ($Unqualified) { 'pull_request' } else { 'push' }
    $f.run.head_branch = if ($Unqualified) { 'feature' } else { 'main' }
    $f.run.head_repository.full_name = if ($Unqualified) { 'attacker/fork' } else { 'geevensingh/org-explorer' }
    $steps = if ($Lane -ceq 'core') { Get-CoreHealthSteps } else { @('restore', 'test') }
    @{ schemaVersion = 1; lane = $Lane; revision = $f.run.head_sha; runId = '71'; runAttempt = [string]$Attempt
        complete = $true; outcome = 'success'; executionSeconds = 120; targetSeconds = 300
        ceilingSeconds = if ($Lane -ceq 'core') { 300 } else { 660 }
        policyVersion = "$Lane-original-v1"; selector = if ($Lane -ceq 'core') { 'all' } else { 'Category!=Performance' }
        startedAt = '2026-11-25T10:00:00Z'; finishedAt = '2026-11-25T10:02:00Z'; configurationErrors = @()
        configuration = @{ workload = if ($Lane -ceq 'core') { 'OrgExplorer.Core.Tests' } else { 'OrgExplorer.Persistence.Tests' }
            testTree = 'b' * 40; fixtureTree = 'c' * 40; sdk = '10.0.100'; runtimes = @('Microsoft.NETCore.App 10.0.0')
            os = 'Windows'; architecture = 'X64'; processors = 4; image = 'win25'; imageVersion = '1'
            powershell = '7.6.10'; setup = 'locked-restore;isolated-build;setup-and-cleanup-included' }
        steps = @($steps | ForEach-Object { @{ name = $_; seconds = 1; status = 'Passed' } })
    } | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $evidence 'execution.json')
    $test = if ($Lane -ceq 'core') { 'OrgExplorer.Core.Tests.OrgExportTests.Producer_retains_attempts' } else { 'OrgExplorer.Persistence.Tests.WorkspaceReadLifetimeTests.Readers_keep_durable_writing' }
    "<TestRun><Results><UnitTestResult testName=`"$test`" outcome=`"Passed`" duration=`"00:00:00.5000000`" /></Results><ResultSummary outcome=`"Completed`"><Counters total=`"1`" executed=`"1`" passed=`"1`" failed=`"0`" notExecuted=`"0`" /></ResultSummary></TestRun>" |
        Set-Content (Join-Path $evidence 'result.trx')
    $work = Join-Path $fixture "$Lane-$Attempt"
    $script = if ($Lane -ceq 'core') { 'Invoke-LaneHealth.ps1' } else { 'Invoke-FilesystemHealth.ps1' }
    $arguments = @{ RunId = 71; RunAttempt = $Attempt; HistoryRemote = $remote; AllowLocalFixture = $true; WorkingDirectory = $work }
    if ($Unqualified) { $arguments.PublishObservations = $true } else { $arguments.Publish = $true }
    & "$root\eng\$script" @arguments
}
try {
    Run-Observation core 1
    Run-Observation filesystem 1
    Run-Observation core 3 -Unqualified
    Run-Observation core 2
    Run-Observation filesystem 2
    $paths = @(git --git-dir=$remote ls-tree -r --name-only refs/heads/ci-health-data)
    foreach ($path in @('lanes/71/1/core.json', 'filesystem/71/1.json', 'lanes/71/2/core.json', 'filesystem/71/2.json', 'observations/71/3/core.json')) {
        if ($paths -cnotcontains $path) { throw "Mixed publication lost $path" }
        $saved = (git --git-dir=$remote show "refs/heads/ci-health-data:$path") -join "`n" | ConvertFrom-Json -AsHashtable
        if ($saved.validation -cne 'success' -or $saved.measurements.executionSeconds -ne 120) { throw 'Mixed readers lost real validation/numeric inputs.' }
    }
    $filesystem = (git --git-dir=$remote show refs/heads/ci-health-data:filesystem/71/2.json) -join "`n" | ConvertFrom-Json -AsHashtable
    if ($filesystem.reliability.attempts -ne 2 -or $filesystem.reliability.success -ne 2) { throw 'Filesystem reliability counted other lane records.' }
    if ($filesystem.tests.workloads[0].name -cne 'WorkspaceReadLifetimeTests' -or
        $filesystem.tests.workloads[0].passed -ne 1 -or $filesystem.tests.workloads[0].seconds -ne .5) {
        throw 'Nested concurrent-reader coverage or actual test-case duration was lost.'
    }
    $tip = git --git-dir=$remote rev-parse refs/heads/ci-health-data
    $rejected = $false
    try { Run-Observation filesystem 2 } catch { $rejected = $true }
    if (!$rejected -or (git --git-dir=$remote rev-parse refs/heads/ci-health-data) -cne $tip) { throw 'Mixed history lost immutable-attempt protection.' }
    Write-Host 'PASS: both public observers retain complete mixed history and reject duplicate immutable publication.'
} finally {
    Remove-Item Function:\gh
    Remove-Variable MixedHealthFixture -Scope Global
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
