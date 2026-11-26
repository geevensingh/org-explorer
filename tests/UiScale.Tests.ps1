$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root ".ci-results\ui-scale-contract-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $results | Out-Null
function Invoke-RunnerProbe([string[]] $Arguments, [string] $Log) {
    $quoted = @('-NoProfile', '-File', "$root\eng\Run-UiScale.ps1", '-Worker', 'prefix') + $Arguments |
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
    $notes = Get-Content "$root\tests\OrgExplorer.Desktop.Smoke\LocalSqliteWorkspace.cs" -Raw
    $journeySignature = [regex]::Match($notes, 'RunLocalNoteAutosaveJourney\(\s*AutomationElement window')
    $journeyStart = if ($journeySignature.Success) { $journeySignature.Index } else { -1 }
    if ($journeyStart -lt 0) { throw 'The actual note journey could not be inspected.' }
    $journeyEnd = $notes.IndexOf('private static int ObserveNoteLatency(', $journeyStart, [StringComparison]::Ordinal)
    if ($journeyEnd -le $journeyStart) { throw 'The actual note journey boundaries could not be inspected.' }
    $notes = $notes.Substring($journeyStart, $journeyEnd - $journeyStart)
    $percentile = [regex]::Match($notes, '(?m)^\s*(?<body>double P95\(List<double> values\) => .+;)').Groups['body'].Value
    $condition = [regex]::Match($notes,
        '(?m)^\s*if \((?<body>P95\(.+)\)\r?\n\s*throw new InvalidOperationException\(\$"Hosted ordinary note').Groups['body'].Value
    $goal = [regex]::Match($notes,
        '(?m)^\s*(?<body>const int durabilityGoalMs = \d+;)').Groups['body'].Value
    $ceiling = [regex]::Match($notes,
        '(?m)^\s*(?<body>var durabilityCeilingMs = richFixture is null \? durabilityGoalMs : \d+;)').Groups['body'].Value
    $report = [regex]::Match($notes,
        '(?ms)^\s*Console\.WriteLine\((?<body>\$"PASS: hosted SQLite note durability.+?)\);\r?$').Groups['body'].Value
    if (!$percentile -or !$condition -or !$goal -or !$ceiling -or !$report) { throw 'The actual note-latency verdict/report could not be inspected.' }
    $typeName = 'NoteLatencyPolicy_' + [guid]::NewGuid().ToString('N')
    $policy = Add-Type -PassThru -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Linq;
public static class $typeName {
    public static bool Reject(double editor, double person, double commit, bool duringTyping, bool rich) =>
        RejectSeries(editor, person, new[] { commit }, duringTyping, rich);
    public static bool RejectSeries(double editor, double person, double[] commits, bool duringTyping, bool rich) {
        var richFixture = rich ? new object() : null;
        var editors = new List<double> { editor };
        var navigation = new List<double> { person };
        var durability = new List<double>(commits);
        var committedDuringTyping = new[] { duringTyping };
        $goal
        $ceiling
        $percentile
        return $condition;
    }
    public static string Report(double commit, bool rich) {
        var richFixture = rich ? new object() : null;
        var durability = new List<double> { commit };
        var editors = new List<double> { 1 };
        var navigation = new List<double> { 1 };
        $goal
        $ceiling
        $percentile
        return $report;
    }
}
"@
    if ($policy::Reject(102.4401, 120.0399, 228.7048, $true, $false) -or
        $policy::Reject(3000, 3000, 1000, $true, $false) -or
        !$policy::Reject(1, 1, 1000.1, $true, $false) -or
        !$policy::Reject(1, 1, 228.7048, $false, $false)) {
        throw 'Editor/navigation timings must be informational; one-second durability and during-typing commits must still fail closed.'
    }
    foreach ($case in @(
        @{ p95 = 1034.3852; rejected = $false },
        @{ p95 = 1499.9999; rejected = $false },
        @{ p95 = 1500.0; rejected = $false },
        @{ p95 = 1500.0001; rejected = $true }
    )) {
        # Rank 20/21 is the proposed p95; a single larger last sample is not another gate.
        $samples = [double[]](@(1) * 19 + @($case.p95, ($case.p95 + 20)))
        if ($policy::RejectSeries(3000, 3000, $samples, $true, $true) -ne $case.rejected) {
            throw "Actual rich note p95 verdict disagrees at $($case.p95) ms."
        }
        if (!$policy::RejectSeries(1, 1, $samples, $true, $false) -or
            !$policy::RejectSeries(1, 1, $samples, $false, $true)) {
            throw 'The temporary rich ceiling must not relax small-fixture durability or any during-typing requirement.'
        }
    }
    $overGoal = $policy::Report(1034.3852, $true)
    if (!$overGoal.Contains('goal=1000 ms (NOT met)') -or
        !$overGoal.Contains('acceptance ceiling=1500 ms (approved, enforced)') -or
        !$policy::Report(1000, $true).Contains('goal=1000 ms (met)') -or
        !$policy::Report(1000, $false).Contains('acceptance ceiling=1000 ms (enforced)')) {
        throw 'Actual note reporting must distinguish the1000ms goal from approved rich1500ms enforcement.'
    }
    Write-Host 'PASS: actual note verdict preserves informational editor/navigation and typing; small fixtures enforce1000ms, rich nearest-rank p95 accepts through approved1500ms only.'
    $probe = Join-Path $results 'revision-probe'
    New-Item -ItemType Directory $probe | Out-Null
    '{"outcome":"success","revision":"stale"}' | Set-Content (Join-Path $probe 'result.json')
    $exitCode = Invoke-RunnerProbe @('-ExpectedRevision', ('0' * 40), '-ResultsDirectory', $probe) (Join-Path $results 'revision.log')
    if ($exitCode -eq 0) { throw 'A different candidate revision passed UI-scale verification.' }
    $status = Get-Content (Join-Path $probe 'result.json') -Raw | ConvertFrom-Json
    if ($status.outcome -ne 'failure' -or $status.revision -notmatch '^[0-9a-f]{40}$' -or
        $status.expectedRevision -ne ('0' * 40) -or $status.steps.Count -ne 0 -or $status.suite -ne 'ui-scale' -or
        @($status.requiredScenarios).Count -ne 1 -or $status.worker -cne 'prefix' -or
        $status.policyVersion -cne 'ci-timing-v2' -or $status.ceilingSeconds -ne 900 -or
        $status.observationSeconds -ne 900 -or $status.diagnosticOnly) {
        throw 'Candidate mismatch was not reported as a fresh named UI-scale failure before execution.'
    }
    Write-Host 'PASS: UI-scale candidate mismatch replaces stale success before any build or desktop execution.'
    $started = $env:SCALE_STARTED_AT
    try {
        $env:SCALE_STARTED_AT = [DateTimeOffset]::UtcNow.AddMinutes(-16).ToString('O')
        $expired = Join-Path $results 'expired-probe'
        $exitCode = Invoke-RunnerProbe @('-ResultsDirectory', $expired) (Join-Path $results 'expired.log')
        if ($exitCode -eq 0) { throw 'Exhausted setup budget passed UI-scale verification.' }
        $status = Get-Content (Join-Path $expired 'result.json') -Raw | ConvertFrom-Json
        if ($status.outcome -ne 'timed_out' -or $status.steps.Count -ne 0 -or $status.executionSeconds -le 900) {
            throw 'UI-scale setup did not consume the shared bounded execution budget.'
        }
    } finally { $env:SCALE_STARTED_AT = $started }
    Write-Host 'PASS: exhausted UI-scale setup budget fails before any build or desktop execution.'
    $workflow = Get-Content "$root\.github\workflows\scale-verification.yml" -Raw
    if ($workflow -match '(?m)^\s*concurrency:') {
        throw 'Independent scale verification runs must not serialize or cancel one another on the same ref.'
    }
    foreach ($suite in @(
        @{ job = 'non-ui-performance'; command = 'Run-Performance.ps1'; artifact = 'non-ui-performance-' },
        @{ job = 'ui-scale-prefix'; command = 'Run-UiScale.ps1'; artifact = 'ui-scale-prefix-' },
        @{ job = 'ui-scale-tail'; command = 'Run-UiScale.ps1'; artifact = 'ui-scale-tail-' }
    )) {
        $job = [regex]::Match($workflow, "(?ms)^  $($suite.job):\r?`n(?<body>.*?)(?=^  [\w-]+:|\z)").Groups['body'].Value
        $watchdog = if ($suite.job -eq 'ui-scale-prefix') {
            'timeout-minutes: 30'
        } else { 'timeout-minutes: 13' }
        foreach ($required in @('runs-on: windows-latest', $watchdog, 'SCALE_STARTED_AT=',
            'TESTED_REVISION=', 'CANDIDATE_REVISION', 'if-no-files-found: error', $suite.artifact,
            ($suite.command + ' -ExpectedRevision $env:TESTED_REVISION'))) {
            if (!$job.Contains($required)) { throw "$($suite.job) is missing $required" }
        }
        if ($job -match '(?m)^\s+strategy:' -or
            ($suite.job -ne 'ui-scale-tail' -and $job -match '(?m)^\s+needs:') -or
            $job -notmatch 'if:\s*\$\{\{\s*always\(\)\s*\}\}' -or
            $job -notmatch 'ref: \$\{\{ inputs.revision \|\| github.sha \}\}') {
            throw 'Separate suites must run independently at the explicit candidate with failure artifacts.'
        }
        $tail = [regex]::Match($workflow, '(?ms)^  ui-scale-tail:\r?\n(?<body>.*?)(?=^  [\w-]+:|\z)').Groups['body'].Value
        if (!$tail.Contains('needs: ui-scale-prefix') -or !$tail.Contains('actions/download-artifact@v4') -or
            !$tail.Contains('-Worker tail -PrefixResultsDirectory .ci-results\ui-scale-prefix')) {
            throw 'The isolated tail must resume only its current prefix artifact, not rebuild an equivalent seed.'
        }
        $union = [regex]::Match($workflow, '(?ms)^  ui-scale:\r?\n(?<body>.*?)(?=^  [\w-]+:|\z)').Groups['body'].Value
        foreach ($required in @('needs: [non-ui-performance, ui-scale-prefix, ui-scale-tail]', 'always()', 'Assert-UiScaleUnion.ps1',
            'PREFIX_OUTCOME', 'TAIL_OUTCOME', 'NON_UI_OUTCOME')) {
            if (!$union.Contains($required)) { throw "Strict scale union is missing $required" }
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
    foreach ($required in @('Assert-DesktopResults', 'Get-UiScalePlan', '"--ui-scale-$Worker"', 'Invoke-ScaleSuite',
        'Assert-UiScalePhases', 'Get-UiScaleWorkerResult', 'UI_SCALE_HANDOFF_DIRECTORY')) {
        if (!$runner.Contains($required)) { throw "UI-scale entry point is missing $required" }
    }
    $shared = Get-Content "$root\eng\ScaleRunner.ps1" -Raw
    if (!$shared.Contains('$process.Kill($true)')) { throw 'Shared scale execution lost owned process-tree termination.' }
    if (!$shared.Contains('$ceilingSeconds = 660') -or !$shared.Contains("'diagnostic_complete'") -or
        !$workflow.Contains("inputs.extended_observation && 'diagnostic-' || ''") -or
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
    . "$root\eng\DesktopResults.ps1"
    . "$root\eng\UiScaleResults.ps1"
    $plan = Get-UiScalePlan
    $originalPhases = @{
        prefix = @('UI-mutation-open', 'UI-ordinary-save-latency', 'UI-publish-before-held-export',
            'UI-close-before-held-export', 'UI-background-export-independence', 'UI-profile-mutation', 'UI-handoff-capture')
        tail = @('UI-handoff-join', 'UI-ordinary-profile-latency', 'UI-favorite-mutation', 'UI-import-file',
            'UI-import-navigation', 'UI-validated-refresh', 'UI-import-preview', 'UI-import-apply', 'UI-import-return',
            'UI-external-delivery', 'UI-close-before-reopen', 'UI-reopen', 'UI-close',
            'UI-load', 'UI-tree', 'UI-current-search', 'UI-photo-render', 'UI-history-search',
            'UI-manager-navigation', 'UI-deep-profile', 'UI-final-person', 'UI-close')
    }
    foreach ($worker in @('prefix', 'tail')) {
        if ((@($plan.phases[$worker]) -join "`n") -cne ($originalPhases[$worker] -join "`n")) {
            throw 'The declared phase union must preserve every original prefix, tail and navigation stage without replacement.'
        }
    }
    $phaseLog = Join-Path $results 'phases.log'
    foreach ($worker in @('prefix', 'tail')) {
        $phases = @($plan.phases[$worker])
        $lines = @($phases | ForEach-Object { "SCALE BEGIN $_; utc=contract"; "SCALE END $_; outcome=Passed; ms=0" })
        $lines | Set-Content $phaseLog
        Assert-UiScalePhases $phaseLog $phases
        foreach ($bad in @(
            @($lines[2..($lines.Length - 1)]),
            @($lines + $lines[0..1]),
            @($lines | ForEach-Object { $_.Replace('outcome=Passed', 'outcome=Failed') })
        )) {
            $bad | Set-Content $phaseLog
            $rejected = $false
            try { Assert-UiScalePhases $phaseLog $phases } catch { $rejected = $true }
            if (!$rejected) { throw 'Missing, duplicate or failed original UI-scale phases passed.' }
        }
    }
    $source = Get-Content "$root\tests\OrgExplorer.Desktop.Smoke\ScaleFlow.cs" -Raw
    $prefixSource = $source.Substring($source.IndexOf('private static void RunScaleMutationPrefix'),
        $source.IndexOf('private static void RunScaleMutationTail') - $source.IndexOf('private static void RunScaleMutationPrefix'))
    $tailSource = $source.Substring($source.IndexOf('private static void RunScaleMutationTail'))
    if (!$prefixSource.Contains('RunLocalNoteAutosaveJourney') -or !$prefixSource.Contains('RunRichBackgroundExport') -or
        !$prefixSource.Contains('UiScaleHandoff.Capture') -or $tailSource.Contains('fixture.Build()') -or
        !$tailSource.Contains('UiScaleHandoff.Receive') -or !$tailSource.Contains('RunRichProfileLatency') -or
        !$tailSource.Contains('ScaleFixture.Population') -or !$tailSource.Contains('joinExistingWorkspace: true')) {
        throw 'The split lost its original combined save or complete real tail, or reseeded instead of joining.'
    }
    Write-Host 'PASS: isolated scale workers preserve strict named phase/scenario union and use the actual logical handoff outside required PR smoke.'
    & "$PSScriptRoot\UiScaleUnion.Tests.ps1" -ResultsDirectory (Join-Path $results 'union')
} finally { Remove-Item $results -Recurse -Force }
