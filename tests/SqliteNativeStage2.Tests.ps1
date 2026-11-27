$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\ci.yml') -Raw
if ($workflow -notmatch '(?s)sqlite_native_trace_stage2:\s+description:.*?default: false\s+type: boolean' -or
    $workflow -notmatch "inputs.sqlite_native_trace_stage2 && !inputs.sqlite_native_trace_stage1") {
    throw 'Stage2 must be a separate default-off manual-only investigative route.'
}
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
