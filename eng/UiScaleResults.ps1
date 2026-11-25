function Get-UiScalePlan {
    $directory = Join-Path $PSScriptRoot '..\tests'
    $scenarios = @(Get-Content (Join-Path $directory 'UiScaleScenarios.json') -Raw | ConvertFrom-Json)
    $workers = Get-Content (Join-Path $directory 'UiScaleWorkers.json') -Raw | ConvertFrom-Json -AsHashtable
    $phases = Get-Content (Join-Path $directory 'UiScalePhases.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($plan in @($workers, $phases)) {
        if ($plan.Count -ne 2 -or !$plan.ContainsKey('prefix') -or !$plan.ContainsKey('tail') -or
            @($plan.Values | Where-Object { $_ -isnot [Collections.IList] -or !$_.Count -or
                @($_ | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count }).Count) {
            throw 'UI scale requires both nonempty isolated worker and phase selections.'
        }
    }
    $union = @($workers.prefix) + @($workers.tail)
    if (!$scenarios.Count -or $union.Count -ne $scenarios.Count -or
        @($union | Select-Object -Unique).Count -ne $union.Count -or
        (Compare-Object @($scenarios | Sort-Object) @($union | Sort-Object) -CaseSensitive)) {
        throw 'UI-scale workers must execute the complete scenario union exactly once.'
    }
    return @{ workers = $workers; phases = $phases }
}

function Assert-UiScalePhases([string] $Log, [string[]] $Expected) {
    if (!(Test-Path -LiteralPath $Log -PathType Leaf)) { throw 'Missing actual UI-scale phase log.' }
    $begins = @()
    $ends = @()
    $active = [Collections.Generic.Stack[string]]::new()
    foreach ($line in Get-Content -LiteralPath $Log) {
        if ($line -match '^SCALE BEGIN (?<name>[^;]+)(;|$)' -and $Expected -ccontains $Matches.name) {
            $begins += $Matches.name
            $active.Push($Matches.name)
        } elseif ($line -match '^SCALE END (?<name>[^;]+); outcome=(?<outcome>[^;]+);' -and
            $Expected -ccontains $Matches.name) {
            if ($Matches.outcome -cne 'Passed') { throw "Failed UI-scale phase: $($Matches.name)" }
            if (!$active.Count -or $active.Pop() -cne $Matches.name) {
                throw 'UI-scale phase completion did not belong to its actual admitted stage.'
            }
            $ends += $Matches.name
        }
    }
    if ($active.Count -or ($begins -join "`n") -cne ($Expected -join "`n") -or $ends.Count -ne $Expected.Count -or
        (Compare-Object @($ends | Sort-Object) @($Expected | Sort-Object) -CaseSensitive)) {
        throw 'UI-scale execution omitted, duplicated or reordered a required original phase.'
    }
}

function Get-UiScaleWorkerResult([string] $Directory, [string] $Worker, [string] $Revision,
    [string] $RunId, [string] $RunAttempt) {
    if ($Revision -cnotmatch '^[0-9a-f]{40}$' -or !$RunId -or !$RunAttempt) {
        throw 'UI-scale collection requires exact candidate, run and attempt.'
    }
    $plan = Get-UiScalePlan
    $report = Get-Content (Join-Path $Directory 'result.json') -Raw | ConvertFrom-Json
    if ($report.suite -cne 'ui-scale' -or $report.worker -cne $Worker -or $report.selector -cne "--ui-scale-$Worker" -or $report.outcome -cne 'success' -or
        $report.observedOutcome -cne 'success' -or $report.phase -cne 'finished' -or $report.failure -or
        $report.revision -cne $Revision -or $report.expectedRevision -cne $Revision -or
        $report.runId -cne $RunId -or $report.runAttempt -cne $RunAttempt -or
        $report.trackedChanges -isnot [bool] -or $report.trackedChanges -or
        $report.untrackedChanges -isnot [bool] -or $report.untrackedChanges -or
        $report.diagnosticOnly -isnot [bool] -or $report.diagnosticOnly -or
        $report.ceilingSeconds -ne 660 -or $report.observationSeconds -ne 660 -or
        $null -eq $report.executionSeconds -or ![double]::IsFinite($report.executionSeconds) -or
        $report.executionSeconds -lt 0 -or $report.executionSeconds -gt 660 -or
        !$report.startedAt -or !$report.completedAt -or
        [DateTimeOffset]$report.completedAt -lt [DateTimeOffset]$report.startedAt -or
        [DateTimeOffset]$report.completedAt -gt [DateTimeOffset]::UtcNow -or
        ([DateTimeOffset]$report.completedAt - [DateTimeOffset]$report.startedAt).TotalSeconds -gt 660 -or
        $report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$' -or
        (@($report.requiredScenarios) -join "`n") -cne (@($plan.workers[$Worker]) -join "`n") -or
        (@($report.completedPhases) -join "`n") -cne (@($plan.phases[$Worker]) -join "`n")) {
        throw "UI-scale $Worker is missing, failed, stale, diagnostic, incomplete or outside its unchanged budget."
    }
    $run = Join-Path $Directory $report.runDirectory
    $nested = Get-Content (Join-Path $run 'result.json') -Raw
    if ($nested -cne (Get-Content (Join-Path $Directory 'result.json') -Raw)) {
        throw 'UI-scale root and terminal owned-run reports differ.'
    }
    $requiredSteps = @('restore-app', 'restore-tests', 'build-app', 'display-prerequisite', 'ui-scale')
    if ((@($report.steps.name) -join "`n") -cne ($requiredSteps -join "`n") -or
        @($report.steps | Where-Object { $_.exitCode -ne 0 -or $null -eq $_.seconds -or
            ![double]::IsFinite($_.seconds) -or $_.seconds -lt 0 }).Count) {
        throw 'UI-scale worker omitted or failed required build/display/native execution.'
    }
    Assert-DesktopResults (Join-Path $run 'scenarios.json') @($plan.workers[$Worker])
    Assert-UiScalePhases (Join-Path $run 'ui-scale.log') @($plan.phases[$Worker])
    return @{ report = $report; run = $run }
}

function Read-UiScaleEvidenceIdentity([string]$Run, [string]$Name, [string]$Revision,
    [string]$RunId, [string]$RunAttempt, [switch]$AsHashtable) {
    $text = Get-Content (Join-Path $Run $Name) -Raw
    $data = if ($AsHashtable) { $text | ConvertFrom-Json -AsHashtable -DateKind String } else { $text | ConvertFrom-Json }
    if ($data.revision -cne $Revision -or $data.runId -cne $RunId -or $data.runAttempt -cne $RunAttempt -or
        $data.productExeSha256 -cnotmatch '^[0-9A-F]{64}$' -or $data.productAssemblySha256 -cnotmatch '^[0-9A-F]{64}$') {
        throw 'Rich evidence is not from the same actual candidate/product/run/attempt.'
    }
    return $data
}

function Read-UiScaleEvidence([string]$Run, [string]$Name, [string]$Revision,
    [string]$RunId, [string]$RunAttempt, $Binding, [switch]$AsHashtable) {
    $data = Read-UiScaleEvidenceIdentity $Run $Name $Revision $RunId $RunAttempt -AsHashtable:$AsHashtable
    if ($data.productExeSha256 -cne $Binding.productFiles.'OrgExplorer.Desktop.exe' -or
        $data.productAssemblySha256 -cne $Binding.productFiles.'OrgExplorer.Desktop.dll') {
        throw 'Rich evidence is not from the same actual candidate/product/run/attempt.'
    }
    return $data
}

function Assert-UiScaleUnion([string] $PrefixDirectory, [string] $TailDirectory, [string] $Revision,
    [string] $RunId, [string] $RunAttempt) {
    $prefix = Get-UiScaleWorkerResult $PrefixDirectory prefix $Revision $RunId $RunAttempt
    $tail = Get-UiScaleWorkerResult $TailDirectory tail $Revision $RunId $RunAttempt
    $handoffPath = Join-Path $prefix.run 'handoff\handoff.json'
    $handoffHash = (Get-FileHash -LiteralPath $handoffPath -Algorithm SHA256).Hash
    $handoff = Get-Content $handoffPath -Raw | ConvertFrom-Json
    $receipt = Get-Content (Join-Path $tail.run 'handoff-received.json') -Raw | ConvertFrom-Json
    foreach ($binding in @($handoff.binding, $receipt.binding)) {
        foreach ($key in @('revision', 'sourceTree', 'testTree', 'engTree', 'workflowBlob')) {
            if ($binding.$key -cnotmatch '^[0-9a-f]{40}$') { throw 'Incomplete handoff source/component identity.' }
        }
        if (!$binding.nativeVersion -or !$binding.nativeSourceId -or $binding.loadedNativeSha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw 'Incomplete handoff actual native identity.'
        }
        foreach ($component in @($binding.productFiles, $binding.harnessFiles)) {
            if (!$component -or !@($component.PSObject.Properties).Count -or
                @($component.PSObject.Properties | Where-Object { $_.Value -cnotmatch '^[0-9A-F]{64}$' }).Count) {
                throw 'Incomplete handoff component binary hashes.'
            }
        }
    }
    if ($prefix.report.handoffSha256 -cne $handoffHash -or $tail.report.handoffSha256 -cne $handoffHash -or
        $receipt.schemaVersion -ne 1 -or $receipt.outcome -cne 'Passed' -or
        $receipt.handoffSha256 -cne $handoffHash -or $handoff.schemaVersion -ne 1 -or
        $handoff.binding.revision -cne $Revision -or $handoff.binding.runId -cne $RunId -or
        $handoff.binding.runAttempt -cne $RunAttempt -or
        ($handoff.binding | ConvertTo-Json -Depth 8 -Compress) -cne
            ($receipt.binding | ConvertTo-Json -Depth 8 -Compress) -or
        $handoff.sourceRunDirectory -cne $prefix.report.runDirectory -or
        $receipt.sourceRunDirectory -cne $handoff.sourceRunDirectory -or
        $receipt.sourceWorkspace -cne $handoff.sourceWorkspace -or
        $receipt.receiverWorkspace -ceq $handoff.sourceWorkspace) {
        throw 'Both current workers must bind to the same actual complete logical snapshot and binary/source/native identity.'
    }
    foreach ($key in @('workspaceId', 'head', 'snapshotSha256', 'payloadSha256', 'preferencesSha256', 'publicStateSha256', 'selection')) {
        if (($receipt.$key | ConvertTo-Json -Compress) -cne ($handoff.$key | ConvertTo-Json -Compress)) {
            throw "Logical handoff mismatch: $key"
        }
        foreach ($key in @('snapshotSha256', 'payloadSha256', 'preferencesSha256', 'publicStateSha256')) {
            if ($handoff.$key -cnotmatch '^[0-9A-F]{64}$') { throw 'Incomplete bound logical handoff hash.' }
        }
    }
    $bundle = Split-Path $handoffPath -Parent
    $guidPattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
    if ($handoff.workspaceId -cnotmatch $guidPattern -or $handoff.head -cnotmatch $guidPattern) {
        throw 'Logical snapshot identities must be GUIDs, never filesystem paths.'
    }
    if ($handoff.snapshotFile -cne "$($handoff.workspaceId)-$($handoff.head).oesnapshot" -or
        (Get-FileHash (Join-Path $bundle $handoff.snapshotFile)).Hash -cne $handoff.snapshotSha256 -or
        (Get-FileHash (Join-Path $bundle 'preferences.json')).Hash -cne $handoff.preferencesSha256) {
        throw 'The actual logical snapshot/preferences bytes differ from the validated handoff.'
    }
    function Read-Evidence([string] $Run, [string] $Name) {
        return Read-UiScaleEvidence $Run $Name $Revision $RunId $RunAttempt $handoff.binding
    }
    function Assert-Samples($Samples, [int] $Count) {
        if (@($Samples).Count -ne $Count -or @($Samples | Where-Object {
            $_ -is [bool] -or $_ -isnot [ValueType] -or ![double]::IsFinite([double]$_) -or [double]$_ -lt 0 }).Count) {
            throw 'Missing, nonnumeric or invalid original latency samples.'
        }
        return @($Samples | Sort-Object)[[int][Math]::Ceiling($Count * .95) - 1]
    }
    foreach ($phase in @('normal-executable', 'background-export')) {
        $note = Read-Evidence $prefix.run "scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
        $durability = $note.localDurability
        $editorP95 = Assert-Samples $note.addTeamNote.samplesMs 20
        $navigationP95 = Assert-Samples $note.personNavigation.samplesMs 42
        $durabilityP95 = Assert-Samples $durability.samplesMs 21
        $null = Assert-Samples $durability.independentFullReopenVerificationMs 21
        if ($note.richScale -isnot [bool] -or !$note.richScale -or $note.phase -cne $phase -or
            $note.heldExport -isnot [bool] -or $note.heldExport -ne ($phase -eq 'background-export') -or
            @($note.addTeamNote.samplesMs).Count -ne 20 -or @($note.personNavigation.samplesMs).Count -ne 42 -or
            @($durability.samplesMs).Count -ne 21 -or @($durability.committedDuringTyping).Count -ne 21 -or
            @($durability.committedDuringTyping | Where-Object { $_ -isnot [bool] -or !$_ }).Count -or
            $note.addTeamNote.p95Ms -ne $editorP95 -or $note.personNavigation.p95Ms -ne $navigationP95 -or
            $durability.p95Ms -ne $durabilityP95 -or $durabilityP95 -gt 1050 -or
            $durability.budgetMs -isnot [ValueType] -or $durability.budgetMs -is [bool] -or $durability.budgetMs -ne 1050 -or
            $durability.goalMs -isnot [ValueType] -or $durability.goalMs -is [bool] -or $durability.goalMs -ne 1000 -or
            $durability.goalMet -isnot [bool] -or
            $durability.goalMet -ne ($durabilityP95 -le 1000) -or
            $durability.budgetEnforced -isnot [bool] -or !$durability.budgetEnforced -or
            $durability.temporaryCeiling -isnot [bool] -or !$durability.temporaryCeiling -or
            $durability.timingUse -cne 'acceptance' -or
            $durability.independentVerificationScope -cne 'fresh-final-prefix-owner-and-exact-writing' -or
            $durability.continuedEditsPerNote -ne 6 -or $durability.nominalEditSpacingMs -ne 250) {
            throw 'Combined original normal/held-export 20/42/21 continuous-typing proof is incomplete or nonaccepting.'
        }
    }
    foreach ($sample in @(
        @($prefix.run, 'scale-mutation-save-prefix', 'background-export'),
        @($tail.run, 'scale-mutation-delivery-tail', 'normal-executable')
    )) {
        $profile = Read-Evidence $sample[0] "$($sample[1])-ui-sqlite-rich-profile-latency-$($sample[2]).json"
        $profileP95 = Assert-Samples $profile.samplesMs 21
        if ($profile.phase -cne $sample[2] -or @($profile.samplesMs).Count -ne 21 -or
            @($profile.independentFullReopenVerificationMs).Count -ne 21 -or
            $profile.budgetMs -ne 1000 -or $profile.p95Ms -ne $profileP95 -or $profileP95 -gt 1000) {
            throw 'Actual21 profile Save evidence is incomplete or nonaccepting.'
        }
    }
    $lifecycle = Read-Evidence $prefix.run 'scale-mutation-save-prefix-ui-sqlite-rich-export-lifecycle.json'
    if ($lifecycle.outcome -cne 'Passed' -or $lifecycle.error -or !$lifecycle.profileAdmittedAt -or
        !$lifecycle.profileBrowsedAt -or !$lifecycle.admittedAt -or !$lifecycle.localWritingVerifiedAt -or
        !$lifecycle.releasedAt -or $lifecycle.exportedRevision -ceq $lifecycle.latestRevision -or
        $lifecycle.latestRevision -cne $handoff.head) {
        throw 'Same real held profile/export, latest publication and normal process death are required.'
    }
}
