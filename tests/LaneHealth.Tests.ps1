$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.test-workspaces\lh-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$remote = Join-Path $fixture 'history.git'
$evidence = Join-Path $fixture 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
git init --bare --quiet $remote
$checks = 0
function Assert([bool]$Condition, [string]$Reason) {
    if (!$Condition) { throw $Reason }
    $script:checks++
}
function Rejects([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert $rejected $Reason
}
$run = @{
    id = 51; run_attempt = 1; head_sha = 'a' * 40; event = 'push'; head_branch = 'main'
    path = '.github/workflows/ci.yml'; status = 'completed'
    head_repository = @{ full_name = 'geevensingh/org-explorer' }
}
$jobs = @(
    @{ id = 101; name = 'Core and view models'; status = 'completed'; conclusion = 'success'
        started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:02:05Z'
        steps = @(@{ name = 'Setup .NET'; conclusion = 'success'; started_at = '2026-11-25T10:00:00Z'; completed_at = '2026-11-25T10:00:05Z' }) }
)
$artifacts = @(@{
    id = 201; name = 'core-attempt-1'; expired = $false; expires_at = '2099-01-01T00:00:00Z'
    digest = 'sha256:' + ('b' * 64); workflow_run = @{ id = 51; head_sha = $run.head_sha }
})
$global:LaneHealthFixture = @{ run = $run; jobs = $jobs; artifacts = $artifacts; evidence = $evidence }
function gh {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if ($arguments[0] -ceq 'run' -and $arguments[1] -ceq 'download') {
        $destination = $arguments[[Array]::IndexOf($arguments, '--dir') + 1]
        $name = $arguments[[Array]::IndexOf($arguments, '--name') + 1]
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $global:LaneHealthFixture.evidence "$name\*") -Destination $destination -Recurse
        return
    }
    if ($arguments[0] -cne 'api') { throw 'Unexpected external operation.' }
    switch -Regex ($arguments[1]) {
        '/actions/runs/51/attempts/[1-9][0-9]*$' { $global:LaneHealthFixture.run | ConvertTo-Json -Depth 10 -Compress; return }
        '/actions/runs/51/attempts/[1-9][0-9]*/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:LaneHealthFixture.jobs }) -Depth 10 -Compress; return
        }
        '/actions/runs/51/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = $global:LaneHealthFixture.artifacts }) -Depth 10 -Compress; return
        }
        default { throw "Unexpected endpoint: $($arguments[1])" }
    }
}
function Observe([switch]$Publish) {
    $work = Join-Path $fixture ('o-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    & "$root\eng\Invoke-LaneHealth.ps1" -RunId 51 -RunAttempt $run.run_attempt -HistoryRemote $remote `
        -AllowLocalFixture -WorkingDirectory $work -Publish:$Publish
    return @{
        records = @(Get-Content (Join-Path $work 'records.json') -Raw | ConvertFrom-Json -AsHashtable)
        summary = Get-Content (Join-Path $work 'summary.md') -Raw
    }
}
try {
    $core = Join-Path $evidence 'core-attempt-1'
    New-Item -ItemType Directory -Path $core | Out-Null
    $companions = @('CiGate', 'CiRunner', 'FilesystemHealth', 'LaneHealth', 'ImplementationBaseline',
        'DocumentationHandoff', 'DraftDiscard', 'Performance', 'NonUiCohort', 'DesktopResults', 'UiScale', 'Release',
        'Release.Gates', 'Release.Publication', 'Release.Renewal', 'Release.Workflow', 'Release.Payload',
        'Update.TrustLifetime', 'ApplicationIcon')
    $execution = @{
        schemaVersion = 1; lane = 'core'; revision = $run.head_sha; runId = '51'; runAttempt = '1'
        complete = $true; outcome = 'success'; executionSeconds = 120; ceilingSeconds = 300
        targetSeconds = 300; selector = 'all'; policyVersion = 'core-original-v1'
        startedAt = '2026-11-25T10:00:00Z'; finishedAt = '2026-11-25T10:02:00Z'
        configuration = @{
            workload = 'OrgExplorer.Core.Tests'; testTree = 'c' * 40; fixtureTree = 'd' * 40
            sdk = '10.0.100'; runtimes = @('Microsoft.NETCore.App 10.0.0')
            os = 'Windows'; architecture = 'X64'; processors = 4; image = 'win25'; imageVersion = '1'
            powershell = '7.6.10'; setup = 'locked-restore;isolated-build;setup-and-cleanup-included'
        }
        steps = @(@{ name = 'restore'; seconds = 10; status = 'Passed' },
            @{ name = 'test'; seconds = 60; status = 'Passed' }) +
            @($companions | ForEach-Object { @{ name = "$_.Tests.ps1"; seconds = 1; status = 'Passed' } })
    }
    $execution | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $core 'execution.json')
    '<TestRun><Results><UnitTestResult testName="Real.workflow" outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>' |
        Set-Content (Join-Path $core 'result.trx')
    $observed = Observe -Publish
    $saved = (git --git-dir=$remote show refs/heads/ci-health-data:lanes/51/1/core.json) -join "`n" | ConvertFrom-Json -AsHashtable
    Assert ($saved.validation -ceq 'success' -and $saved.measurements.executionSeconds -eq 120 -and
        $saved.measurements.headroomSeconds -eq 180 -and $saved.tests.total -eq 1 -and
        $saved.measurements.steps[2].name -ceq 'CiGate.Tests.ps1') 'Core lost complete runner/companion evidence.'
    Assert ($saved.timingHealth -ceq 'unbaselined' -and $null -eq $saved.reference -and
        $observed.summary.Contains('Core and view models') -and $observed.summary.Contains('observation only')) 'Core invented comparative health.'
    $previousStart = $env:LANE_STARTED_AT
    $previousSummary = $env:GITHUB_STEP_SUMMARY
    $previousTiming = $env:SCALE_TIMING_FILE
    $previousSha = $env:GITHUB_SHA
    try {
        $probe = Join-Path $fixture 'core-probe'
        New-Item -ItemType Directory -Path (Join-Path $probe 'eng'), (Join-Path $probe 'tests') -Force | Out-Null
        foreach ($name in @('Run-CiLane.ps1', 'DesktopResults.ps1')) {
            Copy-Item "$root\eng\$name" (Join-Path $probe "eng\$name")
        }
        foreach ($name in @('DesktopScenarios.json', 'DesktopPartitions.json')) {
            Copy-Item "$root\tests\$name" (Join-Path $probe "tests\$name")
        }
        git -C $probe init --quiet
        git -C $probe add .
        git -C $probe -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Isolated runner'
        $env:LANE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-301).ToString('O')
        $env:GITHUB_STEP_SUMMARY = ''
        $env:GITHUB_SHA = ''
        & pwsh -NoProfile -File (Join-Path $probe 'eng\Run-CiLane.ps1') core *> (Join-Path $fixture 'core-probe.log')
        Assert ($LASTEXITCODE -ne 0) 'An exhausted core budget passed.'
        $terminal = Get-Content (Join-Path $probe '.ci-results\core\execution.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert (!$terminal.complete -and $terminal.outcome -ceq 'timed_out' -and $terminal.steps.Count -eq 0 -and
            $terminal.executionSeconds -gt 300 -and $terminal.selector -ceq 'all') 'Core setup failure lost its original terminal runner result.'
    } finally {
        $env:LANE_STARTED_AT = $previousStart
        $env:GITHUB_STEP_SUMMARY = $previousSummary
        $env:SCALE_TIMING_FILE = $previousTiming
        $env:GITHUB_SHA = $previousSha
    }
    Write-Host "PASS: $checks lane observation contracts."
} finally {
    Remove-Item Function:\gh
    Remove-Variable LaneHealthFixture -Scope Global
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
