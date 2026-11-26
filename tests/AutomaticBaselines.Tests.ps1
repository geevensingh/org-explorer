param([switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.test-workspaces\baselines-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
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
function Invoke-FixtureGit([string]$Directory, [string[]]$Arguments) {
    $output = & git -C $Directory @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $($output -join ' ')" }
    return ($output -join "`n")
}
function gh {
    $global:LASTEXITCODE = 0
    switch -Regex ($args[1]) {
        '/actions/runs/42/attempts/1$' { $global:BaselineFixture.ci | ConvertTo-Json -Depth 10; return }
        '/actions/runs/42(/attempts/1)?/jobs\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ jobs = $global:BaselineFixture.ciJobs }) -Depth 10; return
        }
        '/actions/runs/42/artifacts\?per_page=100$' {
            ConvertTo-Json -InputObject @(@{ artifacts = @() }) -Depth 10; return
        }
        '/actions/runs/99/attempts/1$' {
            @{ id = 99; run_attempt = 1; head_sha = $global:BaselineFixture.controller
                event = 'workflow_run'; path = '.github/workflows/non-ui-cohorts.yml'; status = 'in_progress'
                head_repository = @{ full_name = 'geevensingh/org-explorer' } } | ConvertTo-Json -Depth 10
            return
        }
        default { throw "Unexpected external GitHub operation: $($args -join ' ')" }
    }
}
try {
    $remote = Join-Path $fixture 'remote.git'
    $source = Join-Path $fixture 'source'
    & git init --bare --quiet $remote
    & git clone --quiet $remote $source
    $null = Invoke-FixtureGit $source @('checkout', '--quiet', '-b', 'main')
    'First product revision' | Set-Content (Join-Path $source 'product.txt')
    $null = Invoke-FixtureGit $source @('add', 'product.txt')
    $null = Invoke-FixtureGit $source @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '-m', 'First main fixture')
    $first = Invoke-FixtureGit $source @('rev-parse', 'HEAD')
    'Second product revision' | Set-Content (Join-Path $source 'product.txt')
    $null = Invoke-FixtureGit $source @('add', 'product.txt')
    $null = Invoke-FixtureGit $source @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '-m', 'Second main fixture')
    $second = Invoke-FixtureGit $source @('rev-parse', 'HEAD')
    $null = Invoke-FixtureGit $source @('push', '--quiet', 'origin', 'main')
    $ci = @{
        id = 42; run_attempt = 1; head_sha = $second; event = 'push'; head_branch = 'main'
        path = '.github/workflows/ci.yml'; status = 'completed'; conclusion = 'success'
        head_repository = @{ full_name = 'geevensingh/org-explorer' }
    }
    $ciJobs = @(@('Core and view models', 'Real filesystem persistence', 'Actual Windows desktop smoke (people)',
        'Actual Windows desktop smoke (workspace)', 'High-resolution Windows smoke (reading)',
        'High-resolution Windows smoke (context)', 'CI') | ForEach-Object {
        @{ name = $_; status = 'completed'; conclusion = 'success' }
    })
    $global:BaselineFixture = @{ ci = $ci; ciJobs = $ciJobs; controller = $second }
    $work = Join-Path $fixture 'plan'
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory $work -RichScale -CheckHistory `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2 -BootstrapRevision $second
    $plan = Get-Content (Join-Path $work 'plan.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($plan.mode -ceq 'automatic-baseline' -and $plan.policyVersion -ceq 'ci-timing-v2' -and
        ($plan.samples -join ',') -ceq '1,2,3' -and $plan.bootstrapRevision -ceq $second -and
        $plan.mainRevision -ceq $second -and $null -eq $plan.historySnapshot -and $plan.collect) `
        'Trusted automatic plan did not retain explicit bootstrap, approved policy, actual main and exactly three executions.'
    if ($PlanOnly) { Write-Host "PASS: $checks automatic baseline plan checks."; return }
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 99 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'durable-plan') -RichScale `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2 -BootstrapRevision $second -PublishPlan
    $bootstrap = (& git --git-dir=$remote show 'refs/heads/ci-health-data:timing-bootstrap/ci-timing-v2.json') -join "`n" |
        ConvertFrom-Json -AsHashtable
    Assert ($bootstrap.revision -ceq $second -and $bootstrap.policyVersion -ceq 'ci-timing-v2') `
        'Explicit first bootstrap was not durable before collection/promotion.'
    'Third product revision' | Set-Content (Join-Path $source 'product.txt')
    $null = Invoke-FixtureGit $source @('add', 'product.txt')
    $null = Invoke-FixtureGit $source @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
        'commit', '--quiet', '-m', 'Main advances during collection')
    $third = Invoke-FixtureGit $source @('rev-parse', 'HEAD')
    $null = Invoke-FixtureGit $source @('push', '--quiet', 'origin', 'main')
    & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
        -CohortRunId 100 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'advanced-main-plan') -RichScale `
        -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    $advanced = Get-Content (Join-Path $fixture 'advanced-main-plan\plan.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert ($advanced.bootstrapRevision -ceq $second -and $advanced.mainRevision -ceq $third -and $advanced.collect) `
        'Advancing main moved the once-pinned bootstrap or starved the previously planned candidate.'
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 101 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'moving-bootstrap') -RichScale `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2 -BootstrapRevision $third
    } 'A new main tip replaced the durable bootstrap.'
    $ci.head_sha = 'f' * 40
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $ci.head_sha -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 102 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'unknown-main') -RichScale `
            -AllowLocalFixture -HistoryRemote $remote -ApprovedPolicyVersion ci-timing-v2
    } 'Advertised CI provenance replaced actual main membership.'
    $ci.head_sha = $second
    $emptyRemote = Join-Path $fixture 'empty.git'
    & git init --bare --quiet $emptyRemote
    Rejects {
        & "$root\eng\Invoke-NonUiCohort.ps1" -Stage Plan -Revision $second -CiRunId 42 -CiRunAttempt 1 `
            -CohortRunId 103 -CohortRunAttempt 1 -WorkingDirectory (Join-Path $fixture 'missing-source') -RichScale `
            -AllowLocalFixture -HistoryRemote $emptyRemote -ApprovedPolicyVersion ci-timing-v2 -BootstrapRevision $second
    } 'A failed actual-main fetch became empty-history bootstrap success.'
    Write-Host "PASS: $checks public automatic baseline plan checks."
    $global:LASTEXITCODE = 0
} finally {
    Remove-Variable BaselineFixture -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
