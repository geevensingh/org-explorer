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
try {
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
    $workflow = Get-Content "$root\.github\workflows\scale-verification.yml" -Raw
    foreach ($suite in @(
        @{ job = 'non-ui-performance'; command = 'Run-Performance.ps1'; artifact = 'non-ui-performance-' },
        @{ job = 'ui-scale'; command = 'Run-UiScale.ps1'; artifact = 'ui-scale-' }
    )) {
        $job = [regex]::Match($workflow, "(?ms)^  $($suite.job):\r?`n(?<body>.*?)(?=^  [\w-]+:|\z)").Groups['body'].Value
        foreach ($required in @('runs-on: windows-latest', 'timeout-minutes: 11', 'SCALE_STARTED_AT=',
            'TESTED_REVISION=', 'CANDIDATE_REVISION', 'if-no-files-found: error', $suite.artifact,
            ($suite.command + ' -ExpectedRevision $env:TESTED_REVISION'))) {
            if (!$job.Contains($required)) { throw "$($suite.job) is missing $required" }
        }
        if ($job -match '(?m)^\s+(needs|strategy):' -or
            $job -notmatch 'if:\s*\$\{\{\s*always\(\)\s*\}\}' -or
            $job -notmatch 'ref: \$\{\{ inputs.revision \|\| github.sha \}\}') {
            throw 'Separate suites must run independently at the explicit candidate with failure artifacts.'
        }
    }
    $runner = Get-Content "$root\eng\Run-UiScale.ps1" -Raw
    foreach ($required in @('Assert-DesktopResults', 'UiScaleScenarios.json', "'--ui-scale'", 'Invoke-ScaleSuite')) {
        if (!$runner.Contains($required)) { throw "UI-scale entry point is missing $required" }
    }
    $shared = Get-Content "$root\eng\ScaleRunner.ps1" -Raw
    if (!$shared.Contains('$process.Kill($true)')) { throw 'Shared scale execution lost owned process-tree termination.' }
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
