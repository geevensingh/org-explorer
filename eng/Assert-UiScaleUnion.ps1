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
$primaryFailure = $null
$reportingFailures = [Collections.Generic.List[Management.Automation.ErrorRecord]]::new()
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
} catch {
    $primaryFailure = $_
    $report.outcome = 'failure'
    $report.failure = $_.Exception.Message
} finally {
    $report.collectionCompletedAt = [DateTimeOffset]::UtcNow.ToString('O')
    foreach ($target in @((Join-Path $run 'result.json'), $path)) {
        try {
            $json = $report | ConvertTo-Json
            [IO.File]::WriteAllText("$target.writing", $json)
            [IO.File]::Move("$target.writing", $target, $true)
        } catch {
            $reportingFailures.Add($_)
            $report.outcome = 'failure'
            $report.reportingFailures = @($reportingFailures | ForEach-Object { $_.Exception.Message })
            Write-Warning "UI-scale union report failed at ${target}: $($_.Exception.Message)" -WarningAction Continue
        }
    }
}
if ($primaryFailure) { throw $primaryFailure }
if ($reportingFailures.Count) { throw $reportingFailures[0] }
Write-Host 'PASS: both isolated UI-scale workers, complete original phase/scenario union and exact validated logical snapshot handoff.'
