param([Parameter(Mandatory)][string] $ResultsDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\eng\DesktopResults.ps1"
. "$PSScriptRoot\..\eng\UiScaleResults.ps1"
. "$PSScriptRoot\UiScaleUnionFixture.ps1"
$candidate = 'a' * 40
$fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
Assert-UiScaleUnion $fixture.prefix $fixture.tail $candidate '123' '2'
$checks = 0
foreach ($defect in @('missing-worker', 'failed-worker', 'diagnostic', 'attempt', 'budget', 'step',
    'missing-phase', 'duplicate-phase', 'failed-phase', 'scenario', 'receipt', 'snapshot', 'preferences',
    'selection', 'source-invocation', 'native', 'samples', 'during-typing', 'profile', 'lifecycle')) {
    $fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
    $run = Join-Path $fixture.tail $fixture.runName
    $path = Join-Path $fixture.tail 'result.json'
    $metadata = Get-Content $path -Raw | ConvertFrom-Json
    switch ($defect) {
        'missing-worker' { Remove-Item $path }
        'failed-worker' { $metadata.outcome = 'failure' }
        'diagnostic' { $metadata.diagnosticOnly = $true }
        'attempt' { $metadata.runAttempt = '3' }
        'budget' { $metadata.executionSeconds = 660.01 }
        'step' { $metadata.steps = @($metadata.steps | Select-Object -Skip 1) }
        'missing-phase' { Get-Content (Join-Path $run 'ui-scale.log') | Select-Object -Skip 2 | Set-Content (Join-Path $run 'bad.log')
            Move-Item (Join-Path $run 'bad.log') (Join-Path $run 'ui-scale.log') -Force }
        'duplicate-phase' { "SCALE BEGIN UI-reopen; utc=synthetic`nSCALE END UI-reopen; outcome=Passed; ms=0" |
            Add-Content (Join-Path $run 'ui-scale.log') }
        'failed-phase' { (Get-Content (Join-Path $run 'ui-scale.log') -Raw).Replace('outcome=Passed', 'outcome=Failed') |
            Set-Content (Join-Path $run 'ui-scale.log') }
        'scenario' {
            $scenarioPath = Join-Path $run 'scenarios.json'
            $scenarios = Get-Content $scenarioPath -Raw | ConvertFrom-Json
            $scenarios.scenarios[0].name = 'substituted-tail'
            $scenarios | ConvertTo-Json -Depth 8 | Set-Content $scenarioPath
        }
        'receipt' { Remove-Item (Join-Path $run 'handoff-received.json') }
        'snapshot' { 'corrupt' | Add-Content (Get-ChildItem (Join-Path $fixture.prefix "$($fixture.runName)\handoff") -Filter '*.oesnapshot').FullName }
        'preferences' { 'corrupt' | Add-Content (Join-Path $fixture.prefix "$($fixture.runName)\handoff\preferences.json") }
        { $_ -in 'selection', 'native', 'source-invocation' } {
            $receiptPath = Join-Path $run 'handoff-received.json'
            $receipt = Get-Content $receiptPath -Raw | ConvertFrom-Json
            if ($defect -eq 'native') { $receipt.binding.nativeSourceId = 'different' }
            elseif ($defect -eq 'source-invocation') { $receipt.sourceRunDirectory = 'run-' + ('c' * 32) }
            else { $receipt.selection.personEmail = 'wrong-owner@example.com' }
            $receipt | ConvertTo-Json -Depth 12 | Set-Content $receiptPath
        }
        { $_ -in 'samples', 'during-typing' } {
            $notePath = Join-Path $fixture.prefix "$($fixture.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-background-export.json"
            $note = Get-Content $notePath -Raw | ConvertFrom-Json
            if ($defect -eq 'samples') { $note.addTeamNote.samplesMs = @(1) * 19 }
            else { $note.localDurability.committedDuringTyping[0] = $false }
            $note | ConvertTo-Json -Depth 8 | Set-Content $notePath
        }
        'profile' { Remove-Item (Join-Path $run 'scale-mutation-delivery-tail-ui-sqlite-rich-profile-latency-normal-executable.json') }
        'lifecycle' {
            $lifecyclePath = Join-Path $fixture.prefix "$($fixture.runName)\scale-mutation-save-prefix-ui-sqlite-rich-export-lifecycle.json"
            $lifecycle = Get-Content $lifecyclePath -Raw | ConvertFrom-Json
            $lifecycle.outcome = 'Failed'
            $lifecycle | ConvertTo-Json -Depth 8 | Set-Content $lifecyclePath
        }
    }
    if ($defect -in 'failed-worker', 'diagnostic', 'attempt', 'budget', 'step') {
        $metadata | ConvertTo-Json -Depth 8 | Set-Content $path
        $metadata | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'result.json')
    }
    $rejected = $false
    try { Assert-UiScaleUnion $fixture.prefix $fixture.tail $candidate '123' '2' } catch { $rejected = $true }
    if (!$rejected) { throw "UI-scale acceptance passed $defect." }
    $checks++
}
Write-Host "PASS: complete split acceptance fixture plus $checks missing/failed/mismatched/duplicate/substituted/over-budget union refusals."
$fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
$failedJobs = Join-Path $ResultsDirectory 'failed-upstream'
$rejected = $false
try {
    & "$PSScriptRoot\..\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $fixture.prefix -TailDirectory $fixture.tail `
        -ExpectedRevision $candidate -RunId '123' -RunAttempt '2' -RequiredJobOutcomes @('failure', 'success', 'success') `
        -ResultsDirectory $failedJobs
} catch { $rejected = $true }
$failure = Get-Content (Join-Path $failedJobs 'result.json') -Raw | ConvertFrom-Json
if (!$rejected -or $failure.outcome -cne 'failure' -or !$failure.failure.Contains('Missing, failed')) {
    throw 'An upstream failure must remain primary and retain a failed union artifact.'
}
Write-Host 'PASS: upstream failure produces retained nonaccepting union evidence even when both synthetic UI workers passed.'
