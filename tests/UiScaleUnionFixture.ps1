# Synthetic runner artifacts only: no native UI or product behavior is claimed by this contract fixture.
function New-UiScaleUnionFixture([string] $Directory, [string] $Candidate,
    [string] $RunId = '123', [string] $RunAttempt = '2', [DateTimeOffset] $StartedAt = [DateTimeOffset]::UtcNow.AddMinutes(-1),
    [string] $RunName = ('run-' + ('b' * 32)), [string] $TailRunName = $RunName) {
    . "$PSScriptRoot\..\eng\DesktopResults.ps1"
    . "$PSScriptRoot\..\eng\UiScaleResults.ps1"
    $plan = Get-UiScalePlan
    $runName = $RunName
    $prefix = Join-Path $Directory 'prefix'
    $tail = Join-Path $Directory 'tail'
    $bundle = Join-Path $prefix "$runName\handoff"
    New-Item -ItemType Directory -Path $bundle -Force | Out-Null
    function Write-Json([string] $Path, $Data) {
        New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
        $Data | ConvertTo-Json -Depth 12 | Set-Content $Path
    }
    $binding = @{ revision = $Candidate; runId = $RunId; runAttempt = $RunAttempt
        sourceTree = ('b' * 40); testTree = ('c' * 40); engTree = ('d' * 40); workflowBlob = ('e' * 40)
        productFiles = @{ 'OrgExplorer.Desktop.exe' = ('A' * 64); 'OrgExplorer.Desktop.dll' = ('B' * 64) }
        harnessFiles = @{ 'OrgExplorer.Desktop.Smoke.dll' = ('C' * 64) }
        nativeVersion = 'synthetic-native'; nativeSourceId = 'synthetic-source'; loadedNativeSha256 = ('D' * 64) }
    $head = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    $workspaceId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    $snapshotFile = "$workspaceId-$head.oesnapshot"
    'Synthetic runner fixture, not a logical Workspace' | Set-Content (Join-Path $bundle $snapshotFile)
    '{"schemaVersion":1,"fixture":"synthetic preferences"}' | Set-Content (Join-Path $bundle 'preferences.json')
    $metadata = @{ schemaVersion = 1; binding = $binding; sourceRunDirectory = $runName; sourceWorkspace = 'C:\synthetic\source'
        workspaceId = $workspaceId; head = $head; snapshotFile = $snapshotFile
        snapshotSha256 = (Get-FileHash (Join-Path $bundle $snapshotFile)).Hash; payloadSha256 = ('E' * 64)
        preferencesSha256 = (Get-FileHash (Join-Path $bundle 'preferences.json')).Hash
        selection = @{ personEmail = 'owner@example.com' }; publicStateSha256 = ('F' * 64) }
    Write-Json (Join-Path $bundle 'handoff.json') $metadata
    $hash = (Get-FileHash (Join-Path $bundle 'handoff.json')).Hash
    $receipt = @{} + $metadata
    $receipt.Remove('snapshotFile')
    $receipt.outcome = 'Passed'; $receipt.handoffSha256 = $hash; $receipt.receiverWorkspace = 'C:\synthetic\receiver'
    Write-Json (Join-Path $tail "$TailRunName\handoff-received.json") $receipt
    foreach ($worker in @('prefix', 'tail')) {
        $workerDirectory = Join-Path $Directory $worker
        $workerRun = if ($worker -eq 'prefix') { $runName } else { $TailRunName }
        $run = Join-Path $workerDirectory $workerRun
        $report = @{ suite = 'ui-scale'; worker = $worker; selector = "--ui-scale-$worker"
            outcome = 'success'; observedOutcome = 'success'; phase = 'finished'; failure = $null
            revision = $Candidate; expectedRevision = $Candidate; runId = $RunId; runAttempt = $RunAttempt
            trackedChanges = $false; untrackedChanges = $false; diagnosticOnly = $false
            environment = @{ processors = 4; os = 'Windows'; runtime = '.NET 10'; powershell = '7.6'
                image = 'win25'; imageVersion = '20261116' }
            ceilingSeconds = 660; observationSeconds = 660; executionSeconds = 1
            startedAt = $StartedAt.ToString('O'); completedAt = $StartedAt.AddSeconds(1).ToString('O')
            runDirectory = $workerRun; requiredScenarios = @($plan.workers[$worker]); completedPhases = @($plan.phases[$worker])
            handoffSha256 = $hash; steps = @('restore-app', 'restore-tests', 'build-app', 'display-prerequisite', 'ui-scale') |
                ForEach-Object { @{ name = $_; exitCode = 0; seconds = 0 } } }
        Write-Json (Join-Path $workerDirectory 'result.json') $report
        Write-Json (Join-Path $run 'result.json') $report
        Write-Json (Join-Path $run 'scenarios.json') @{ schemaVersion = 1; scenarios = @($plan.workers[$worker] |
            ForEach-Object { @{ name = $_; status = 'Passed'; error = $null; seconds = 0
                startedAt = $StartedAt.ToString('O'); finishedAt = $StartedAt.ToString('O') } }) }
        @($plan.phases[$worker] | ForEach-Object {
            "SCALE BEGIN $_; utc=synthetic"; "SCALE END $_; outcome=Passed; ms=0"
        }) | Set-Content (Join-Path $run 'ui-scale.log')
    }
    $common = @{ revision = $Candidate; runId = $RunId; runAttempt = $RunAttempt
        productExeSha256 = ('A' * 64); productAssemblySha256 = ('B' * 64) }
    foreach ($phase in @('normal-executable', 'background-export')) {
        $note = @{} + $common
        $note.phase = $phase; $note.richScale = $true; $note.heldExport = $phase -eq 'background-export'
        $note.addTeamNote = @{ samplesMs = @(1) * 20; p95Ms = 1 }
        $note.personNavigation = @{ samplesMs = @(1) * 42; p95Ms = 1 }
        $note.localDurability = @{ samplesMs = @(1) * 21; p95Ms = 1; goalMs = 1000; goalMet = $true
            budgetMs = 1050; budgetEnforced = $true; temporaryCeiling = $true; timingUse = 'acceptance'
            independentFullReopenVerificationMs = @(1) * 21
            independentVerificationScope = 'fresh-final-prefix-owner-and-exact-writing'
            committedDuringTyping = @($true) * 21; continuedEditsPerNote = 6; nominalEditSpacingMs = 250 }
        Write-Json (Join-Path $prefix "$runName\scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json") $note
    }
    foreach ($sample in @(@($prefix, 'scale-mutation-save-prefix', 'background-export'),
        @($tail, 'scale-mutation-delivery-tail', 'normal-executable'))) {
        $profile = @{} + $common
        $profile.phase = $sample[2]; $profile.samplesMs = @(1) * 21; $profile.p95Ms = 1
        $profile.budgetMs = 1000; $profile.independentFullReopenVerificationMs = @(1) * 21
        $workerRun = if ($sample[0] -eq $prefix) { $runName } else { $TailRunName }
        Write-Json (Join-Path $sample[0] "$workerRun\$($sample[1])-ui-sqlite-rich-profile-latency-$($sample[2]).json") $profile
    }
    $lifecycle = @{} + $common
    $lifecycle.outcome = 'Passed'; $lifecycle.error = $null; $lifecycle.profileAdmittedAt = 1
    $lifecycle.profileBrowsedAt = 2; $lifecycle.admittedAt = 3; $lifecycle.localWritingVerifiedAt = 4
    $lifecycle.releasedAt = 5; $lifecycle.exportedRevision = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'; $lifecycle.latestRevision = $head
    Write-Json (Join-Path $prefix "$runName\scale-mutation-save-prefix-ui-sqlite-rich-export-lifecycle.json") $lifecycle
    return @{ prefix = $prefix; tail = $tail; runName = $runName; tailRunName = $TailRunName }
}

function New-RichCohortSampleFixture([string]$Directory, [string]$Candidate, [int]$Sample,
    [string]$RunId, [string]$RunAttempt) {
    $fixture = New-UiScaleUnionFixture $Directory $Candidate $RunId $RunAttempt `
        -RunName ('run-' + [guid]::NewGuid().ToString('N')) -TailRunName ('run-' + [guid]::NewGuid().ToString('N'))
    $cohortId = "$Candidate-$RunId-$RunAttempt"
    foreach ($worker in @('prefix', 'tail')) {
        $path = Join-Path $fixture[$worker] 'result.json'
        $report = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        $report.cohortId = $cohortId; $report.sample = $Sample
        $report | ConvertTo-Json -Depth 15 | Set-Content $path
        $report | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $fixture[$worker] "$($report.runDirectory)\result.json")
    }
    $fixture.union = Join-Path $Directory 'union'
    & "$PSScriptRoot\..\eng\Assert-UiScaleUnion.ps1" -PrefixDirectory $fixture.prefix -TailDirectory $fixture.tail `
        -ExpectedRevision $Candidate -RunId $RunId -RunAttempt $RunAttempt -ResultsDirectory $fixture.union -CohortId $cohortId -Sample $Sample
    return $fixture
}
