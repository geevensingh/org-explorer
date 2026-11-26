$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\ci.yml') -Raw
$baseline = (git -C $root show '3ee4cdd8ebc41427f2781c7f3574107adfb1a15d:.github/workflows/ci.yml') -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'The reviewed CI baseline is unavailable.' }
if ($workflow -notmatch '(?s)sqlite_native_trace_stage1:\s+description:.*?default: false\s+type: boolean') {
    throw 'The investigative route must be a default-off Boolean dispatch input.'
}
function Job([string]$source, [string]$name) {
    $match = [regex]::Match($source, "(?ms)^  $([regex]::Escape($name)):\r?`n.*?(?=^  [a-z][a-z0-9-]*:\r?`n|\z)")
    if (!$match.Success) { throw "Missing required job $name." }
    return ($match.Value -replace "`r", '').TrimEnd()
}
$guard = '    if: ${{ github.event_name != ''workflow_dispatch'' || !inputs.sqlite_native_trace_stage1 }}'
foreach ($name in @('core', 'filesystem', 'desktop', 'high-resolution')) {
    $actual = Job $workflow $name
    if (!$actual.Contains($guard)) { throw 'Stage1 must not execute an ordinary suite.' }
    $normal = $actual.Replace($guard + "`n", '')
    if ($normal -cne (Job $baseline $name)) { throw "Normal $name commands/partitions/budgets changed." }
}
if ((Job $workflow 'ci') -cne (Job $baseline 'ci')) { throw 'The required CI aggregate must remain unchanged.' }
foreach ($name in @('scale-verification', 'release-verification')) {
    $normal = (Job $workflow $name).Replace(' && !inputs.sqlite_native_trace_stage1', '')
    if ($normal -cne (Job $baseline $name)) { throw 'Optional verification behavior changed outside stage1.' }
}
$stage = Job $workflow 'sqlite-native-stage1'
if ($stage -notmatch 'NOT CI' -or $stage -notmatch "github.event_name == 'workflow_dispatch' && inputs.sqlite_native_trace_stage1") {
    throw 'Stage1 must be explicitly investigative and manual-only.'
}
. (Join-Path $root 'eng\CiGate.ps1')
$rejected = $false
try { Assert-CiResults @('skipped', 'skipped', 'skipped', 'skipped') }
catch {
    if ($_.Exception.Message -cne 'Required CI lanes did not all succeed: skipped, skipped, skipped, skipped') { throw }
    $rejected = $true
}
if (!$rejected) { throw 'An investigative dispatch could attest to normal CI.' }
$tokens = $null
$errors = $null
$check = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'eng\Test-SqliteNativeTraceStage1.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Stage1 feasibility syntax is invalid.' }
foreach ($command in $check.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true)) {
    if ($command.GetCommandName() -in @('wpr','xperf','logman','Start-Process','Start-Trace','Start-EtwTraceSession')) {
        throw 'The feasibility-only route must not include a collector or launch the fixture.'
    }
    if ($command.GetCommandName() -eq 'Get-WinEvent' -and
        !@($command.CommandElements | Where-Object {
            $_ -is [Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -eq 'ListProvider'
        }).Count) { throw 'Stage1 must not collect event records or private process data.' }
}
$results = Join-Path $root ('.ci-results\stage1-contract-' + [guid]::NewGuid().ToString('N'))
$previous = $env:GITHUB_ACTIONS
try {
    $env:GITHUB_ACTIONS = 'false'
    $revision = git -C $root rev-parse HEAD
    $shell = (Get-Command pwsh).Source
    & $shell -NoProfile -NonInteractive -File (Join-Path $root 'eng\Test-SqliteNativeTraceStage1.ps1') `
        -ExpectedRevision $revision -ResultsDirectory $results
    if ($LASTEXITCODE -ne 2) { throw 'The actual stage1 check did not refuse a shared/local host.' }
    $record = Get-Content -LiteralPath (Join-Path $results 'feasibility.json') -Raw | ConvertFrom-Json
    if ($record.status -cne 'RefusedHost' -or !$record.diagnosticOnly -or $record.normalCiAttested -or
        $record.collectionStarted -or $record.fixtureStarted -or $record.revision -cne $revision) {
        throw 'Local refusal metadata lost its safety or exact-revision contract.'
    }
    Write-Host 'PASS: stage1 is default-off/manual-only, ordinary CI is preserved, and local collection is refused.'
}
finally {
    $env:GITHUB_ACTIONS = $previous
    if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results -Recurse -Force }
}
