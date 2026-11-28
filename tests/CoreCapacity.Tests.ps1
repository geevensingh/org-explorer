$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root '.ci-results\core-capacity'
if (Test-Path -LiteralPath $results) { throw 'Preserve the existing capacity observation before running this contract.' }
$previous = $env:LANE_STARTED_AT
try {
    $env:LANE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-901).ToString('O')
    & (Get-Process -Id $PID).Path -NoProfile -File "$root\eng\Run-CiLane.ps1" core -CoreCapacityDiagnostic
    if ($LASTEXITCODE -eq 0) { throw 'An exhausted diagnostic budget was accepted.' }
    $record = Get-Content -LiteralPath (Join-Path $results 'execution.json') -Raw | ConvertFrom-Json
    if ($record.complete -or !$record.diagnosticOnly -or $record.observedComplete -or
        $record.outcome -cne 'diagnostic_timed_out' -or $record.policyVersion -cne 'core-capacity-diagnostic-v1' -or
        $record.targetSeconds -ne 300 -or $record.ceilingSeconds -ne 900 -or $record.selector -cne 'all') {
        throw 'Capacity observation changed acceptance, selection or the original goal.'
    }
    $workflow = Get-Content -LiteralPath "$root\.github\workflows\ci.yml" -Raw
    foreach ($job in @('core', 'filesystem', 'desktop', 'high-resolution')) {
        if ($workflow -cnotmatch ("(?ms)^  " + [regex]::Escape($job) + ":\r?\n    if: \$\{\{ !inputs\.core_capacity_diagnostic \}\}")) {
            throw "Diagnostic dispatch could supply ordinary $job acceptance."
        }
    }
    if ($workflow -cnotmatch '\.\\eng\\Run-CiLane\.ps1 core -CoreCapacityDiagnostic') {
        throw 'Hosted calibration does not invoke the actual complete core entry point.'
    }
    Write-Host 'PASS: bounded core calibration retains the original goal and cannot provide ordinary CI acceptance.'
} finally {
    $env:LANE_STARTED_AT = $previous
    if (Test-Path -LiteralPath $results) {
        Move-Item -LiteralPath $results -Destination (Join-Path $root ('.ci-results\core-capacity-contract-' + [guid]::NewGuid().ToString('N')))
    }
}
