param([Parameter(Mandatory)][string]$TimingFixtureMetadata)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$metadata = Get-Content -LiteralPath $TimingFixtureMetadata -Raw |
    ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
if ($metadata.kind -cne 'isolated-non-production-public-contract-fixture' -or
    $metadata.hostedMeasurement -ne $false -or $metadata.policyVersion -cne 'ci-timing-v4' -or
    $metadata.priorCohortId -cnotmatch '^([0-9a-f]{40})-([1-9][0-9]*)-([1-9][0-9]*)$') {
    throw 'The retained-cleanup contract requires an isolated publisher-produced v4 fixture.'
}
$path = "rich-scale-cohorts/$($Matches[1])/$($Matches[2])/$($Matches[3]).json"
. "$root\eng\NonUiCohort.ps1"
. "$PSScriptRoot\CleanupEvidenceFixture.ps1"
function gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -cne 'api') { throw 'Unexpected fixture external command.' }
    Invoke-CleanupFixtureApi $args[1] $metadata.sourceRemote $metadata.cleanupContexts |
        ConvertTo-Json -Depth 20
}
function Start-Process {
    param([string]$FilePath, [object[]]$ArgumentList, [switch]$PassThru, [switch]$NoNewWindow,
        [string]$RedirectStandardOutput, [string]$RedirectStandardError)
    Start-CleanupFixtureProcess $FilePath $ArgumentList $metadata.cleanupRoots `
        -PassThru:$PassThru -NoNewWindow:$NoNewWindow `
        -RedirectStandardOutput $RedirectStandardOutput -RedirectStandardError $RedirectStandardError
}
$raw = & git "--git-dir=$($metadata.runningHistoryRemote)" show "refs/heads/ci-health-data:$path"
if ($LASTEXITCODE -ne 0) { throw 'The original retained v4 cohort fixture is unavailable.' }
$original = $raw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
Assert-NonUiCohortRecord $original $path
if (!$original.samples[0].runnerReport.cleanup.warning) {
    throw 'The retained-history contract requires an original signed cleanup warning.'
}
foreach ($mutation in @('missing', 'null')) {
    $record = $raw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
    if ($mutation -ceq 'missing') { $record.samples[0].Remove('runnerReport') }
    else { $record.samples[0].runnerReport = $null }
    $failure = $null
    try { Assert-NonUiCohortRecord $record $path } catch { $failure = $_ }
    if (!$failure -or $failure.Exception.Message -cne 'Accepted v4 sample lacks its retained verification/cleanup report.') {
        throw "Retained v4 history did not reject the $mutation report through its required-proof guard."
    }
}
$legacyPaths = @(& git "--git-dir=$($metadata.runningHistoryRemote)" ls-tree -r --name-only `
    refs/heads/ci-health-data -- rich-scale-cohorts | Where-Object { $_.EndsWith('/99/1.json') })
if ($LASTEXITCODE -ne 0 -or $legacyPaths.Count -ne 1) { throw 'The original v2 fixture is unavailable.' }
$legacyRaw = & git "--git-dir=$($metadata.runningHistoryRemote)" show "refs/heads/ci-health-data:$($legacyPaths[0])"
if ($LASTEXITCODE -ne 0) { throw 'The original v2 fixture could not be read.' }
$legacy = $legacyRaw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
if ($legacy.effectivePolicyVersion -cne 'ci-timing-v2') { throw 'The legacy fixture policy changed.' }
Assert-NonUiCohortRecord $legacy $legacyPaths[0]
foreach ($sample in $legacy.samples) { $sample.Remove('runnerReport') }
Assert-NonUiCohortRecord $legacy $legacyPaths[0]
Write-Host 'PASS: original signed v4 history remains valid; missing or null retained verification/cleanup reports cannot qualify.'
Write-Host 'PASS: the original v2 cohort does not require v4-only retained reports.'
