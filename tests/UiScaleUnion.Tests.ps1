param([Parameter(Mandatory)][string] $ResultsDirectory, [switch]$NativeProfilePolicyOnly)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\eng\DesktopResults.ps1"
. "$PSScriptRoot\..\eng\UiScaleResults.ps1"
. "$PSScriptRoot\UiScaleUnionFixture.ps1"
$candidate = 'a' * 40
$fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
Assert-UiScaleUnion $fixture.prefix $fixture.tail $candidate '123' '2'
$compiled = Join-Path $fixture.prefix "$($fixture.runName)\build\bin\fixture"
New-Item -ItemType Directory -Path $compiled -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $compiled 'e_sqlite3.dll'), [byte[]]@(77, 90, 1, 2))
'{"compiled":"not verification proof"}' | Set-Content (Join-Path $compiled 'compiled.json')
'raw TRX proof' | Set-Content (Join-Path $fixture.prefix "$($fixture.runName)\proof.trx")
$selected = Join-Path $ResultsDirectory 'selected-evidence'
& "$PSScriptRoot\..\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $fixture.prefix -TailDirectory $fixture.tail `
    -ExpectedRevision $candidate -RunId 123 -RunAttempt 2 -ExpectedPolicyVersion rich-scale-original-v1 -ResultsDirectory $selected
$selectedReport = Get-Content (Join-Path $selected 'result.json') -Raw | ConvertFrom-Json
$selectedPrefix = Join-Path $selected "$($selectedReport.runDirectory)\prefix\$($fixture.runName)"
if (Test-Path -LiteralPath (Join-Path $selectedPrefix 'build')) {
    throw 'The public union artifact selection copied leftover compiled build/native bytes.'
}
foreach ($file in @('result.json', 'scenarios.json', 'ui-scale.log', 'proof.trx', 'handoff\handoff.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $selectedPrefix $file))) { throw "Artifact selection omitted required raw evidence: $file" }
}
$checks = 0
$native = New-UiScaleUnionFixture (Join-Path $ResultsDirectory 'native-profile-v3') $candidate -TimingPolicyVersion ci-timing-v3
foreach ($phase in @(
    @{ worker = 'prefix'; scenario = 'scale-mutation-save-prefix'; phase = 'background-export' },
    @{ worker = 'tail'; scenario = 'scale-mutation-delivery-tail'; phase = 'normal-executable' }
)) {
    $path = Join-Path $native[$phase.worker] "$($native.runName)\$($phase.scenario)-ui-sqlite-rich-profile-latency-$($phase.phase).json"
    $profile = Get-Content $path -Raw | ConvertFrom-Json
    $profile.samplesMs = @(1) * 19 + @(1500.0, 1600.0)
    $profile.p95Ms = 1500.0; $profile.goalMet = $false
    $profile | ConvertTo-Json -Depth 12 | Set-Content $path
}
Assert-UiScaleUnion $native.prefix $native.tail $candidate '123' '2' -ExpectedPolicyVersion ci-timing-v3
$checks++
foreach ($phase in @(
    @{ worker = 'prefix'; scenario = 'scale-mutation-save-prefix'; phase = 'background-export' },
    @{ worker = 'tail'; scenario = 'scale-mutation-delivery-tail'; phase = 'normal-executable' }
)) {
    foreach ($defect in @('over-ceiling', 'false-goal', 'unknown-policy', 'boolean-policy',
        'boolean-note-policy', 'boolean-worker-policy', 'legacy-v2-ceiling')) {
        $version = if ($defect -ceq 'legacy-v2-ceiling') { 'ci-timing-v2' } else { 'ci-timing-v3' }
        $case = New-UiScaleUnionFixture (Join-Path $ResultsDirectory "$($phase.worker)-profile-$defect") $candidate -TimingPolicyVersion $version
        $path = Join-Path $case[$phase.worker] "$($case.runName)\$($phase.scenario)-ui-sqlite-rich-profile-latency-$($phase.phase).json"
        $profile = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        $value = if ($defect -ceq 'over-ceiling') { 1500.0001 } else { 1500.0 }
        $profile.samplesMs = @(1) * 19 + @($value, 1600.0); $profile.p95Ms = $value
        $profile.budgetMs = 1500; $profile.goalMet = $defect -ceq 'false-goal'
        if ($defect -ceq 'unknown-policy') { $profile.policyVersion = 'ci-timing-v4' }
        if ($defect -ceq 'boolean-policy') { $profile.policyVersion = $true }
        $profile | ConvertTo-Json -Depth 12 | Set-Content $path
        if ($defect -ceq 'boolean-note-policy') {
            $notePath = Join-Path $case.prefix "$($case.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$($phase.phase).json"
            $note = Get-Content $notePath -Raw | ConvertFrom-Json
            $note.localDurability.policyVersion = $true
            $note | ConvertTo-Json -Depth 12 | Set-Content $notePath
        }
        if ($defect -ceq 'boolean-worker-policy') {
            $reportPath = Join-Path $case[$phase.worker] 'result.json'
            $report = Get-Content $reportPath -Raw | ConvertFrom-Json
            $report.policyVersion = $true
            $report | ConvertTo-Json -Depth 12 | Set-Content $reportPath
            Copy-Item $reportPath (Join-Path $case[$phase.worker] "$($case.runName)\result.json")
        }
        $rejected = $false
        try { Assert-UiScaleUnion $case.prefix $case.tail $candidate '123' '2' -ExpectedPolicyVersion $version }
        catch { $rejected = $true }
        if (!$rejected) { throw "Native rich-profile $($phase.phase) accepted $defect." }
        $checks++
    }
}
if ($NativeProfilePolicyOnly) { Write-Host "PASS: $checks native rich-profile policy checks."; return }
$active = New-UiScaleUnionFixture (Join-Path $ResultsDirectory 'active') $candidate -TimingPolicyVersion ci-timing-v2
foreach ($phase in @('normal-executable', 'background-export')) {
    $path = Join-Path $active.prefix "$($active.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
    $note = Get-Content $path -Raw | ConvertFrom-Json
    $note.localDurability.samplesMs = @(1) * 19 + @(1500.0, 1520.0)
    $note.localDurability.p95Ms = 1500.0
    $note.localDurability.goalMet = $false
    $note | ConvertTo-Json -Depth 12 | Set-Content $path
}
$path = Join-Path $active.prefix 'result.json'
$report = Get-Content $path -Raw | ConvertFrom-Json
$report.startedAt = [DateTimeOffset]::UtcNow.AddMinutes(-16).ToString('O')
$report.completedAt = ([DateTimeOffset]$report.startedAt).AddSeconds(900).ToString('O')
$report.executionSeconds = 900.0
$report | ConvertTo-Json -Depth 12 | Set-Content $path
$report | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $active.prefix "$($active.runName)\result.json")
Assert-UiScaleUnion $active.prefix $active.tail $candidate '123' '2' -ExpectedPolicyVersion ci-timing-v2
$checks++
foreach ($defect in @('prefix-safety', 'tail-safety', 'declared-ceiling', 'string-duration', 'string-ceiling',
    'diagnostic', 'unfinished', 'note-ceiling', 'source-policy', 'legacy-source')) {
    $active = New-UiScaleUnionFixture (Join-Path $ResultsDirectory "active-$defect") $candidate -TimingPolicyVersion ci-timing-v2
    $expected = 'ci-timing-v2'
    $worker = if ($defect -ceq 'tail-safety') { 'tail' } else { 'prefix' }
    $path = Join-Path $active[$worker] 'result.json'
    $report = Get-Content $path -Raw | ConvertFrom-Json
    switch ($defect) {
        'prefix-safety' { $report.executionSeconds = 900.0001 }
        'tail-safety' { $report.executionSeconds = 660.0001 }
        'declared-ceiling' { $report.ceilingSeconds = 1800; $report.observationSeconds = 1800 }
        'string-duration' { $report.executionSeconds = '1' }
        'string-ceiling' { $report.ceilingSeconds = '900' }
        'diagnostic' { $report.diagnosticOnly = $true }
        'unfinished' { $report.phase = 'cleanup' }
        'source-policy' { $report.policyVersion = 'ci-timing-v3' }
        'legacy-source' { $expected = 'rich-scale-original-v1' }
        'note-ceiling' {
            $notePath = Join-Path $active.prefix "$($active.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-background-export.json"
            $note = Get-Content $notePath -Raw | ConvertFrom-Json
            $note.localDurability.samplesMs = @(1) * 19 + @(1500.0001, 1520)
            $note.localDurability.p95Ms = 1500.0001; $note.localDurability.goalMet = $false
            $note | ConvertTo-Json -Depth 12 | Set-Content $notePath
        }
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content $path
    $report | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $active[$worker] "$($active.runName)\result.json")
    $rejected = $false
    try { Assert-UiScaleUnion $active.prefix $active.tail $candidate '123' '2' -ExpectedPolicyVersion $expected }
    catch { $rejected = $true }
    if (!$rejected) { throw "Active policy accepted $defect." }
    $checks++
}
foreach ($phase in @('normal-executable', 'background-export')) {
    foreach ($case in @(
        @{ p95 = 1000.0; rejected = $false },
        @{ p95 = 1034.3852; rejected = $false },
        @{ p95 = 1049.9999; rejected = $false },
        @{ p95 = 1050.0; rejected = $false },
        @{ p95 = 1050.0001; rejected = $true }
    )) {
        $fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
        $notePath = Join-Path $fixture.prefix "$($fixture.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
        $note = Get-Content $notePath -Raw | ConvertFrom-Json
        $note.localDurability.samplesMs = @(1) * 19 + @($case.p95, ($case.p95 + 20))
        $note.localDurability.p95Ms = $case.p95
        $note.localDurability.goalMet = $case.p95 -le 1000
        $note | ConvertTo-Json -Depth 8 | Set-Content $notePath
        $rejected = $false
        try { Assert-UiScaleUnion $fixture.prefix $fixture.tail $candidate '123' '2' } catch { $rejected = $true }
        if ($rejected -ne $case.rejected) { throw "Rich $phase collector disagrees at p95=$($case.p95) ms." }
        $checks++
    }
    foreach ($verificationCase in @(
        @{ name = 'numeric-zero-and-fraction'; samples = @(0, 0.125, 1.5) + @(2) * 18; rejected = $false },
        @{ name = 'all-true'; samples = @($true) * 21; rejected = $true },
        @{ name = 'all-false'; samples = @($false) * 21; rejected = $true },
        @{ name = 'mixed-booleans'; samples = @($true) * 11 + @($false) * 10; rejected = $true },
        @{ name = 'numeric-and-boolean'; samples = @(0.5) * 20 + @($true); rejected = $true }
    )) {
        $fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
        $notePath = Join-Path $fixture.prefix "$($fixture.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
        $note = Get-Content $notePath -Raw | ConvertFrom-Json
        $note.localDurability.independentFullReopenVerificationMs = $verificationCase.samples
        $note | ConvertTo-Json -Depth 8 | Set-Content $notePath
        $failure = $null
        try { Assert-UiScaleUnion $fixture.prefix $fixture.tail $candidate '123' '2' }
        catch { $failure = $_.Exception.Message }
        if (($null -ne $failure) -ne $verificationCase.rejected -or
            ($verificationCase.rejected -and $failure -cne 'Missing, nonnumeric or invalid original latency samples.')) {
            throw "Rich $phase independent verification collector disagrees for $($verificationCase.name): $failure"
        }
        $checks++
    }
    foreach ($defect in @('goal', 'goal-type', 'goal-met', 'ceiling', 'ceiling-type', 'temporary', 'enforced', 'informational',
        'missing-goal', 'verification-count', 'verification-scope')) {
        $fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
        $notePath = Join-Path $fixture.prefix "$($fixture.runName)\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
        $note = Get-Content $notePath -Raw | ConvertFrom-Json
        switch ($defect) {
            'goal' { $note.localDurability.goalMs = 1050 }
            'goal-type' { $note.localDurability.goalMs = $true }
            'goal-met' { $note.localDurability.goalMet = $false }
            'ceiling' { $note.localDurability.budgetMs = 1051 }
            'ceiling-type' { $note.localDurability.budgetMs = $true }
            'temporary' { $note.localDurability.temporaryCeiling = $false }
            'enforced' { $note.localDurability.budgetEnforced = $false }
            'informational' { $note.localDurability.timingUse = 'informational' }
            'missing-goal' { $note.localDurability.PSObject.Properties.Remove('goalMs') }
            'verification-count' { $note.localDurability.independentFullReopenVerificationMs = @(1) * 20 }
            'verification-scope' { $note.localDurability.independentVerificationScope = 'earlier-prefix-only' }
        }
        $note | ConvertTo-Json -Depth 8 | Set-Content $notePath
        $rejected = $false
        try { Assert-UiScaleUnion $fixture.prefix $fixture.tail $candidate '123' '2' } catch { $rejected = $true }
        if (!$rejected) { throw "Rich $phase collector accepted misleading or incomplete $defect policy metadata." }
        $checks++
    }
}
foreach ($sample in @(
    @('prefix', 'scale-mutation-save-prefix', 'background-export'),
    @('tail', 'scale-mutation-delivery-tail', 'normal-executable')
)) {
    $fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
    $profilePath = Join-Path $fixture[$sample[0]] "$($fixture.runName)\$($sample[1])-ui-sqlite-rich-profile-latency-$($sample[2]).json"
    $profile = Get-Content $profilePath -Raw | ConvertFrom-Json
    $profile.samplesMs = @(1000.0001) * 21
    $profile.p95Ms = 1000.0001
    $profile | ConvertTo-Json -Depth 8 | Set-Content $profilePath
    $rejected = $false
    try { Assert-UiScaleUnion $fixture.prefix $fixture.tail $candidate '123' '2' } catch { $rejected = $true }
    if (!$rejected) { throw "Rich profile $($sample[2]) must retain its enforced1000ms p95." }
    $checks++
}
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
Write-Host "PASS: complete split acceptance fixture plus $checks rich ceiling/policy and fail-closed union checks."
$fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
$failedJobs = Join-Path $ResultsDirectory 'failed-upstream'
$rejected = $false
try {
    & "$PSScriptRoot\..\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $fixture.prefix -TailDirectory $fixture.tail `
        -ExpectedRevision $candidate -RunId '123' -RunAttempt '2' -RequiredJobOutcomes @('failure', 'success', 'success') `
        -ResultsDirectory $failedJobs -ExpectedPolicyVersion rich-scale-original-v1
} catch { $rejected = $true }
$failure = Get-Content (Join-Path $failedJobs 'result.json') -Raw | ConvertFrom-Json
if (!$rejected -or $failure.outcome -cne 'failure' -or !$failure.failure.Contains('Missing, failed')) {
    throw 'An upstream failure must remain primary and retain a failed union artifact.'
}
Write-Host 'PASS: upstream failure produces retained nonaccepting union evidence even when both synthetic UI workers passed.'

foreach ($defect in @('primary-run-report', 'primary-both-reports', 'run-report', 'root-report')) {
    $fixture = New-UiScaleUnionFixture $ResultsDirectory $candidate
    $blocked = Join-Path $ResultsDirectory $defect
    $outcomes = if ($defect.StartsWith('primary-')) { @('failure', 'success', 'success') }
        else { @('success', 'success', 'success') }
    $failure = $null
    try {
        & {
            # Obstruct real report paths immediately after the initial running report.
            function Set-Content {
                [CmdletBinding()]
                param([Parameter(Position = 0)][string] $Path, [Parameter(ValueFromPipeline)][object] $Value)
                process {
                    Microsoft.PowerShell.Management\Set-Content -LiteralPath $Path -Value $Value
                    $ownedRun = @(Get-ChildItem -LiteralPath (Split-Path $Path) -Directory -Filter 'run-*')
                    if ($ownedRun.Count -ne 1) { throw 'Expected exactly one owned union run for report obstruction.' }
                    $targets = if ($defect -eq 'root-report') { @($Path) }
                        elseif ($defect -eq 'primary-both-reports') { @($Path, (Join-Path $ownedRun[0].FullName 'result.json')) }
                        else { @((Join-Path $ownedRun[0].FullName 'result.json')) }
                    foreach ($target in $targets) { New-Item -ItemType Directory -Path "$target.writing" | Out-Null }
                }
            }
            & "$PSScriptRoot\..\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $fixture.prefix -TailDirectory $fixture.tail `
                -ExpectedRevision $candidate -RunId '123' -RunAttempt '2' -RequiredJobOutcomes $outcomes `
                -ResultsDirectory $blocked -ExpectedPolicyVersion rich-scale-original-v1
        }
    } catch { $failure = $_ }
    if (!$failure) { throw "Union verification passed $defect." }
    if ($defect.StartsWith('primary-') -and !$failure.Exception.Message.Contains('Missing, failed')) {
        throw "Report failure replaced the original union rejection for $defect`: $($failure.Exception.Message)"
    }
    $rootReport = Get-Content (Join-Path $blocked 'result.json') -Raw | ConvertFrom-Json
    if ($rootReport.outcome -ceq 'success' -or
        ($defect -in 'primary-run-report', 'run-report' -and $rootReport.outcome -cne 'failure')) {
        throw "A report failure must remain nonaccepting and still attempt the writable root report for $defect."
    }
}
Write-Host 'PASS: report I/O faults preserve primary rejection, attempt both outputs and cannot publish accepting root evidence.'
