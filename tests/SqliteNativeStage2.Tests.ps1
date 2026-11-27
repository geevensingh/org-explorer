$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\ci.yml') -Raw
if ($workflow -notmatch '(?s)sqlite_native_trace_stage2:\s+description:.*?default: false\s+type: boolean' -or
    $workflow -notmatch "github.event_name == 'workflow_dispatch' && inputs.sqlite_native_trace_stage2") {
    throw 'Stage2 must be a separate default-off manual-only investigative route.'
}
$baseline = (git -C $root show 'fb175267ae4ba879ccbccb2053453c41cc3db3d2:.github/workflows/ci.yml') -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'The approved normal CI baseline is unavailable.' }
function Job([string]$source, [string]$name) {
    $match = [regex]::Match($source, "(?ms)^  $([regex]::Escape($name)):\r?`n.*?(?=^  [a-z][a-z0-9-]*:\r?`n|\z)")
    if (!$match.Success) { throw "Missing required job $name." }
    return ($match.Value -replace "`r", '').TrimEnd()
}
$guard = '    if: ${{ github.event_name != ''workflow_dispatch'' || !inputs.sqlite_native_trace_stage2 }}'
foreach ($name in @('core', 'filesystem', 'desktop', 'high-resolution')) {
    $normal = (Job $workflow $name).Replace($guard + "`n", '')
    if ($normal -cne (Job $baseline $name)) { throw "Normal $name commands/partitions/budgets changed." }
}
if ((Job $workflow 'ci') -cne (Job $baseline 'ci')) { throw 'The required CI aggregate must remain unchanged.' }
foreach ($name in @('scale-verification', 'release-verification')) {
    $normal = (Job $workflow $name).Replace(' && !inputs.sqlite_native_trace_stage2', '')
    if ($normal -cne (Job $baseline $name)) { throw 'Optional normal verification behavior changed.' }
}
. (Join-Path $root 'eng\CiGate.ps1')
$rejected = $false
try { Assert-CiResults @('skipped', 'skipped', 'skipped', 'skipped') }
catch {
    if ($_.Exception.Message -cne 'Required CI lanes did not all succeed: skipped, skipped, skipped, skipped') { throw }
    $rejected = $true
}
if (!$rejected) { throw 'An investigative dispatch could attest to normal CI.' }
$results = Join-Path $root ('.ci-results\stage2-contract-' + [guid]::NewGuid().ToString('N'))
$previous = $env:GITHUB_ACTIONS
try {
    $env:GITHUB_ACTIONS = 'false'
    & (Get-Command pwsh).Source -NoProfile -NonInteractive -File (Join-Path $root 'eng\Run-SqliteNativeStage2.ps1') `
        -NativeManifest 'deliberately-absent.json' -ResultsDirectory $results 2>$null
    if ($LASTEXITCODE -ne 2) { throw 'The actual diagnostic route did not refuse the local/shared host.' }
    $record = Get-Content -LiteralPath (Join-Path $results 'execution.json') -Raw | ConvertFrom-Json
    if ($record.status -cne 'RefusedHost' -or !$record.diagnosticOnly -or $record.normalCiAttested -or
        $record.testStarted -or $record.selectedResults -ne 0 -or $record.nativeSha256) {
        throw 'Stage2 host refusal failed its no-native-substitution/no-fixture execution contract.'
    }
    Write-Host 'PASS: the default-off Stage2 route refuses local collection/substitution before reading a manifest or launching a test.'
}
finally {
    $env:GITHUB_ACTIONS = $previous
    if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results -Recurse -Force }
}
