param(
    [Parameter(Mandatory)][string] $PrefixDirectory,
    [Parameter(Mandatory)][string] $TailDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedRevision,
    [Parameter(Mandatory)][string] $RunId,
    [Parameter(Mandatory)][string] $RunAttempt,
    [string[]] $RequiredJobOutcomes = @(),
    [string] $ResultsDirectory = '.ci-results\ui-scale-union'
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopResults.ps1"
. "$PSScriptRoot\UiScaleResults.ps1"
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$runName = 'run-' + [guid]::NewGuid().ToString('N')
$run = Join-Path $ResultsDirectory $runName
New-Item -ItemType Directory -Path $run | Out-Null
$report = @{ schemaVersion = 1; suite = 'ui-scale'; verificationMode = 'isolated-workers'; outcome = 'running'
    revision = $ExpectedRevision; expectedRevision = $ExpectedRevision; runId = $RunId; runAttempt = $RunAttempt
    runDirectory = $runName; collectionStartedAt = [DateTimeOffset]::UtcNow.ToString('O') }
$path = Join-Path $ResultsDirectory 'result.json'
$report | ConvertTo-Json | Set-Content $path
try {
    if ($RequiredJobOutcomes.Count -and ($RequiredJobOutcomes.Count -ne 3 -or
        @($RequiredJobOutcomes | Where-Object { $_ -cne 'success' }).Count)) {
        throw 'Missing, failed, skipped or diagnostic scale work cannot pass acceptance.'
    }
    Assert-UiScaleUnion $PrefixDirectory $TailDirectory $ExpectedRevision $RunId $RunAttempt
    Copy-Item -LiteralPath $PrefixDirectory -Destination (Join-Path $run 'prefix') -Recurse
    Copy-Item -LiteralPath $TailDirectory -Destination (Join-Path $run 'tail') -Recurse
    Assert-UiScaleUnion (Join-Path $run 'prefix') (Join-Path $run 'tail') $ExpectedRevision $RunId $RunAttempt
    $report.outcome = 'success'
    Write-Host 'PASS: both isolated UI-scale workers, complete original phase/scenario union and exact validated logical snapshot handoff.'
} catch {
    $report.outcome = 'failure'
    $report.failure = $_.Exception.Message
    throw
} finally {
    $report.collectionCompletedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $json = $report | ConvertTo-Json
    foreach ($target in @($path, (Join-Path $run 'result.json'))) {
        [IO.File]::WriteAllText("$target.writing", $json)
        [IO.File]::Move("$target.writing", $target, $true)
    }
}
