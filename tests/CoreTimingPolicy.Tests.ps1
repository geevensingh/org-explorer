$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$root\eng\LaneHealth.ps1"
$fixture = Join-Path $root ('.ci-results\core-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    git -C $fixture init --quiet -b main
    'Original source without a core policy amendment.' | Set-Content -LiteralPath (Join-Path $fixture 'source.txt')
    git -C $fixture add .
    git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Original core source'
    $original = (git -C $fixture rev-parse HEAD).Trim()
    New-Item -ItemType Directory -Path (Join-Path $fixture 'eng') | Out-Null
    '{"version":"core-goal-safety-v2"}' | Set-Content -LiteralPath (Join-Path $fixture 'eng\core-timing-policy.json')
    git -C $fixture add .
    git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Approved goal and safety separation'
    if ($LASTEXITCODE -ne 0) { throw 'Core policy fixture source could not be committed.' }
    $current = (git -C $fixture rev-parse HEAD).Trim()
    foreach ($case in @(
        @{ revision = $original; version = 'core-original-v1'; seconds = 301.0; ceiling = 300; validation = 'failure'; health = 'red' },
        @{ revision = $current; version = 'core-goal-safety-v2'; seconds = 300.0; ceiling = 420; validation = 'success'; health = 'unbaselined' },
        @{ revision = $current; version = 'core-goal-safety-v2'; seconds = 311.6; ceiling = 420; validation = 'success'; health = 'yellow' },
        @{ revision = $current; version = 'core-goal-safety-v2'; seconds = 420.0; ceiling = 420; validation = 'success'; health = 'yellow' },
        @{ revision = $current; version = 'core-goal-safety-v2'; seconds = 420.001; ceiling = 420; validation = 'failure'; health = 'red' },
        @{ revision = $current; version = 'core-goal-safety-v2'; seconds = 311.6; ceiling = 420; validation = 'failure'; health = 'yellow'; failedWork = $true }
    )) {
        $directory = Join-Path $fixture ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $directory | Out-Null
        $finished = [DateTimeOffset]::UtcNow
        @{ schemaVersion = 1; lane = 'core'; revision = $case.revision; runId = '9001'; runAttempt = '1'
            selector = 'all'; policyVersion = $case.version; ceilingSeconds = $case.ceiling; targetSeconds = 300
            complete = $true; outcome = 'success'; executionSeconds = $case.seconds
            startedAt = $finished.AddSeconds(-$case.seconds).ToString('O'); finishedAt = $finished.ToString('O')
            steps = @(Get-CoreHealthSteps | ForEach-Object { @{ name = $_; status = 'Passed'; seconds = 1.0 } })
            configuration = @{} } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'execution.json')
        if ($case.failedWork) {
            $failed = Get-Content -LiteralPath (Join-Path $directory 'execution.json') -Raw | ConvertFrom-Json -AsHashtable
            $failed.complete = $false; $failed.outcome = 'failure'; $failed.steps[2].status = 'Failed'
            $failed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'execution.json')
        }
        '<TestRun><Results><UnitTestResult testName="Controlled input" outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>' |
            Set-Content -LiteralPath (Join-Path $directory 'input.trx')
        $measurement = Get-CoreHealthMeasurement $directory @{ head_sha = $case.revision; id = 9001; run_attempt = 1 } -SourceGitDirectory (Join-Path $fixture '.git')
        if ($measurement.validation -cne $case.validation -or $measurement.originalPolicy -cne $case.version) {
            throw 'Core validation changed a historical policy or ignored the trusted source.'
        }
        $record = @{ schemaVersion = 2; recordType = 'lane-observation'; lane = 'core'
            validation = $measurement.validation; originalPolicy = $measurement.originalPolicy; measurements = $measurement.measurements }
        Set-TimingPolicyEvaluation $record -PolicyVersion ci-timing-v4
        if ($record.timingEvaluation.health -cne $case.health -or
            $record.timingEvaluation.blocking -ne ($case.validation -ceq 'failure')) {
            throw "Goal/safety classification differs for $($case.version) at $($case.seconds)s."
        }
    }
    $forged = Get-Content -LiteralPath (Join-Path $directory 'execution.json') -Raw | ConvertFrom-Json -AsHashtable
    $forged.revision = $original
    $forged | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'execution.json')
    $refused = $null
    try { $null = Get-CoreHealthMeasurement $directory @{ head_sha = $original; id = 9001; run_attempt = 1 } -SourceGitDirectory (Join-Path $fixture '.git') }
    catch { $refused = $_ }
    if (!$refused -or $refused.Exception.Message -cne 'Unbound core policy/selection.') { throw 'An artifact amended its source core policy.' }
    '{"version":"core-goal-safety-v2","safetySeconds":600}' | Set-Content -LiteralPath (Join-Path $fixture 'eng\core-timing-policy.json')
    git -C $fixture add .
    git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Unapproved fixture amendment'
    $amended = (git -C $fixture rev-parse HEAD).Trim()
    $refused = $null
    try { $null = Get-SourceTimingPolicyVersion $amended -Core -GitDirectory (Join-Path $fixture '.git') } catch { $refused = $_ }
    if (!$refused -or $refused.Exception.Message -cne 'Unknown or amended timing policy requires explicit approval and implementation.') {
        throw 'A source marker silently amended the approved numerical core policy.'
    }
    Write-Host 'PASS: source-bound core goals, inclusive safety, yellow completion and historical failure retention.'
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
