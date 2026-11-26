function Set-TimingMainContext($History) {
    $null = Invoke-HealthGit $History.directory @('-c', 'credential.helper=',
        '-c', 'credential.helper=!gh auth git-credential', 'fetch', '--quiet', 'origin',
        'refs/heads/main:refs/heads/timing-main')
    if ((Invoke-HealthGit $History.directory @('rev-parse', '--is-shallow-repository')) -cne 'false') {
        throw 'Canonical timing references require complete actual main ancestry, never shallow or guessed order.'
    }
    $History.mainRevision = Invoke-HealthGit $History.directory @('rev-parse', 'refs/heads/timing-main')
    $History.mainOrder = @{}
    $revisions = (Invoke-HealthGit $History.directory @('rev-list', '--first-parent', '--reverse', $History.mainRevision)) -split "`n"
    for ($index = 0; $index -lt $revisions.Count; $index++) {
        $History.mainOrder[$revisions[$index]] = $index
    }
}

function Assert-TimingMainRevision($History, [string]$Revision) {
    if (!$History.mainOrder.ContainsKey($Revision)) {
        throw 'Timing candidate is not a verified actual main first-parent revision; source/order is unavailable.'
    }
}

    function Get-TimingControlPath($Record) {
        if ($Record.recordType -ceq 'timing-bootstrap') { return "timing-bootstrap/$($Record.policyVersion).json" }
        return "timing-plans/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt).json"
    }

    function Assert-TimingControl($Record, [string]$Path) {
        if ($Record.recordType -cnotin @('timing-bootstrap', 'timing-plan') -or
            $Record.repository -cne $FilesystemHealthRepository -or $Record.authority -cne 'trusted-main-push' -or
            $Record.policyVersion -cne 'ci-timing-v2' -or $Record.revision -cnotmatch '^[0-9a-f]{40}$' -or
            $Record.mainRevision -cnotmatch '^[0-9a-f]{40}$' -or $Path -cne (Get-TimingControlPath $Record)) {
            throw 'Malformed trusted timing bootstrap/plan provenance.'
        }
        Assert-HealthDate $Record.observedAt 'timing control observedAt'
        if ($Record.recordType -ceq 'timing-plan' -and ($Record.mode -cne 'automatic-baseline' -or
            $Record.bootstrapRevision -cnotmatch '^[0-9a-f]{40}$' -or
            $Record.cohortRunId -cnotmatch '^[1-9][0-9]*$' -or $Record.cohortRunAttempt -cnotmatch '^[1-9][0-9]*$' -or
            $Record.cohortId -cne "$($Record.revision)-$($Record.cohortRunId)-$($Record.cohortRunAttempt)" -or
            $Record.ciRevision -cne $Record.revision -or $Record.ciRunId -cnotmatch '^[1-9][0-9]*$' -or
            $Record.ciRunAttempt -cnotmatch '^[1-9][0-9]*$' -or $Record.collect -isnot [bool] -or
            ($Record.samples -join ',') -cne '1,2,3')) { throw 'Malformed automatic timing collection plan.' }
    }

    function Get-TimingReferenceRecordTypes {
        return @{
            'timing-bootstrap' = @{
                PathPattern = '^timing-bootstrap/ci-timing-v2\.json$'
                GetPath = { param($record) Get-TimingControlPath $record }
                Validate = { param($record, $path) Assert-TimingControl $record $path }
                ValidateHistory = { param($history) Assert-TimingHistory $history }
            }
            'timing-plan' = @{
                PathPattern = '^timing-plans/[0-9a-f]{40}/[1-9][0-9]*/[1-9][0-9]*\.json$'
                GetPath = { param($record) Get-TimingControlPath $record }
                Validate = { param($record, $path) Assert-TimingControl $record $path }
            }
            'timing-decision' = @{
                PathPattern = '^timing-decisions/[0-9a-f]{40}/[1-9][0-9]*/[1-9][0-9]*/3\.json$'
                GetPath = { param($record) Get-TimingDecisionPath $record }
                Validate = { param($record, $path) Assert-TimingDecision $record $path }
            }
            'timing-reference' = @{
                PathPattern = '^timing-references/[0-9a-f]{64}/[0-9a-f]{40}/[1-9][0-9]*/[1-9][0-9]*\.json$'
                GetPath = { param($record) Get-TimingReferencePath $record }
                Validate = { param($record, $path) Assert-TimingReference $record $path }
            }
        }
    }

    function Get-TimingBootstrap($History, [string]$ExplicitRevision) {
        $saved = @($History.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-bootstrap' })
        if ($saved.Count -gt 1) { throw 'Timing bootstrap is not uniquely pinned.' }
        if ($saved.Count) {
            if ($ExplicitRevision -and $ExplicitRevision -cne $saved[0].revision) {
                throw 'Explicit bootstrap contradicts the already durable source; it cannot move with main.'
            }
            Assert-TimingMainRevision $History $saved[0].revision
            return $saved[0].revision
        }
        if (!$ExplicitRevision) { throw 'Absent timing bootstrap requires an explicit trusted source, not an empty-history fallback.' }
        Assert-TimingMainRevision $History $ExplicitRevision
        if ($ExplicitRevision -cne $History.mainRevision) {
            throw 'The initial explicit bootstrap source must be current verified main when first planned.'
        }
        return $ExplicitRevision
    }

    function Assert-TimingAutomation($Collection, [long]$RunId, [int]$RunAttempt, [switch]$AllowLocalFixture) {
        if ($Collection.id -ne $RunId -or $Collection.run_attempt -ne $RunAttempt -or
            $Collection.head_repository.full_name -cne $FilesystemHealthRepository -or
            $Collection.event -cne 'workflow_run' -or $Collection.path -cne '.github/workflows/non-ui-cohorts.yml') {
            throw 'Only authenticated automatic collection can maintain timing references.'
        }
        if (!$AllowLocalFixture) {
            $source = Split-Path $PSScriptRoot -Parent
            $head = & git -C $source rev-parse HEAD
            if ($LASTEXITCODE -ne 0 -or $head -cne $env:GITHUB_WORKFLOW_SHA -or
                $head -cne $Collection.head_sha -or $env:GITHUB_ACTIONS -cne 'true' -or
                $env:GITHUB_REPOSITORY -cne $FilesystemHealthRepository) {
                throw 'Timing publication requires the committed authenticated hosted controller, not candidate artifact authority.'
            }
            $dirty = & git -C $source status --porcelain
            if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Timing controller source must be clean and committed.' }
        }
    }

function Get-TimingDecisionPath($Record) {
    return "timing-decisions/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt)/3.json"
}

function Get-TimingReferencePath($Record) {
    return "timing-references/$($Record.seriesKey)/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt).json"
}

function Assert-TimingDecision($Record, [string]$Path) {
    if ($Record.recordType -cne 'timing-decision' -or $Record.repository -cne $FilesystemHealthRepository -or
        $Record.authority -cne 'trusted-main-push' -or $Record.policyVersion -cne 'ci-timing-v2' -or
        $Path -cne (Get-TimingDecisionPath $Record) -or $Record.cohort.samples.Count -ne 3 -or
        $Record.revision -cne $Record.cohort.revision -or $Record.cohortRunId -cne $Record.cohort.cohortRunId -or
        $Record.cohortRunAttempt -cne $Record.cohort.cohortRunAttempt) { throw 'Malformed frozen three-execution timing decision.' }
    Assert-NonUiCohortRecord $Record.cohort (Get-NonUiCohortPath $Record.cohort)
}

function Assert-TimingReference($Record, [string]$Path) {
    if ($Record.recordType -cne 'timing-reference' -or $Record.repository -cne $FilesystemHealthRepository -or
        $Record.authority -cne 'trusted-main-push' -or $Record.policyVersion -cne 'ci-timing-v2' -or
        $Record.seriesKey -cnotmatch '^[0-9a-f]{64}$' -or $Record.revision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.mainRevision -cnotmatch '^[0-9a-f]{40}$' -or $Record.bootstrapRevision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.comparisonCommit -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.cohortRunId -cnotmatch '^[1-9][0-9]*$' -or $Record.cohortRunAttempt -cnotmatch '^[1-9][0-9]*$' -or
        $Record.recordPath -cne "rich-scale-cohorts/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt).json" -or
        $Path -cne (Get-TimingReferencePath $Record)) { throw 'Malformed canonical timing reference provenance.' }
    Assert-HealthDate $Record.observedAt 'reference observedAt'
}

function Get-TimingSavedCohort($History, [string]$Path) {
    $found = @($History.records | Where-Object {
        $_.Contains('recordType') -and $_.recordType -ceq 'performance-cohort' -and
        (Get-NonUiCohortPath $_) -ceq $Path
    })
    if ($found.Count -ne 1) { throw 'Canonical reference requires its uniquely preserved complete comparison record.' }
    return $found[0]
}

function Get-TimingCanonicalReference($History, [string]$SeriesKey, [string]$CandidateRevision) {
    if (!$SeriesKey) { return $null }
    $events = @($History.records | Where-Object {
        $_.Contains('recordType') -and $_.recordType -ceq 'timing-reference' -and $_.seriesKey -ceq $SeriesKey
    })
    foreach ($event in $events) { Assert-TimingMainRevision $History $event.revision }
    $events = @($events | Sort-Object @{ Expression = { $History.mainOrder[$_.revision] }; Descending = $true })
    if (!$events.Count) { return $null }
    $selected = $events[0]
    if ($CandidateRevision -and $selected.revision -ceq $CandidateRevision) {
        if (!$selected.predecessorPath) { return $null }
        return Get-TimingSavedCohort $History $selected.predecessorPath
    }
    if ($CandidateRevision -and $History.mainOrder[$selected.revision] -gt $History.mainOrder[$CandidateRevision]) {
        $previous = @($events | Where-Object { $History.mainOrder[$_.revision] -lt $History.mainOrder[$CandidateRevision] })
        if (!$previous.Count) { return $null }
        $selected = $previous[0]
    }
    return Get-TimingSavedCohort $History $selected.recordPath
}

function Get-TimingPinnedReference($Record, $History) {
    $key = Get-TimingCohortKey $Record
    $decisions = @($History.records | Where-Object {
        $_.Contains('recordType') -and $_.recordType -ceq 'timing-decision' -and
        $_.revision -ceq $Record.revision -and $_.cohortRunId -ceq $Record.cohortRunId -and
        $_.cohortRunAttempt -ceq $Record.cohortRunAttempt
    })
    if ($decisions.Count -gt 1) { throw 'Timing comparison decision is not uniquely pinned.' }
    if ($decisions.Count) {
        $initial = $decisions[0].cohort
        $Record.historySnapshot = $initial.historySnapshot
        if ($initial.reference) { return Get-TimingSavedCohort $History $initial.reference.recordPath }
        return $null
    }
    $Record.historySnapshot = $History.tip
    return Get-TimingCanonicalReference $History $key $Record.revision
}

function Set-TimingCanonicalComparison($Record, $History) {
    $reference = Get-TimingPinnedReference $Record $History
    if ($reference) {
        if ($reference.revision -ceq $Record.revision -or $reference.eligibility -cne 'eligible' -or
            $reference.policyVersion -cne 'ci-timing-v2' -or
            (Get-TimingCohortKey $reference) -cne (Get-TimingCohortKey $Record)) {
            throw 'Pinned canonical comparison is self-referencing, ineligible or incompatible.'
        }
        $Record.reference = @{
            kind = 'pinned-eligible-reference'; recordPath = Get-NonUiCohortPath $reference
            revision = $reference.revision; cohortId = $reference.cohortId; observedAt = $reference.observedAt
            ageSeconds = ([DateTimeOffset]$Record.observedAt - [DateTimeOffset]$reference.observedAt).TotalSeconds
            medianSeconds = $reference.metrics.executionSeconds.median
            deltaSeconds = if ($Record.metrics.Contains('executionSeconds')) {
                $Record.metrics.executionSeconds.median - $reference.metrics.executionSeconds.median
            } else { $null }
            evidenceUrl = $reference.samples[0].evidence.url
            evidenceState = if (@($reference.samples | Where-Object {
                $parts = @($_, $_.rich.prefix, $_.rich.tail, $_.rich.union)
                @($parts | Where-Object { !$_.evidence.expiresAt -or
                    [DateTimeOffset]$_.evidence.expiresAt -le [DateTimeOffset]$Record.observedAt }).Count
            }).Count) { 'expired-or-unavailable' } else { 'retained-metadata-only' }
        }
    }
    Set-TimingPolicyEvaluation $Record $reference $Record.historySnapshot
    $Record.eligibility = $Record.timingEvaluation.eligibility
    $Record.timingHealth = $Record.timingEvaluation.health
    $Record.extraSamples = @($Record.timingEvaluation.additionalSamples)
    $Record.promotion = if ($Record.eligibility -ceq 'eligible') { 'eligible-after-comparison' } else { 'retained' }
}

function Assert-TimingHistory($History) {
    $bootstrap = @($History.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-bootstrap' })
    $events = @($History.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-reference' })
    foreach ($event in $events) {
        if ($bootstrap.Count -ne 1 -or $event.bootstrapRevision -cne $bootstrap[0].revision) {
            throw 'Canonical reference lacks the once-pinned trusted bootstrap.'
        }
        $cohort = Get-TimingSavedCohort $History $event.recordPath
        if ($cohort.policyVersion -cne 'ci-timing-v2' -or $cohort.eligibility -cne 'eligible' -or
            $cohort.validation -cne 'success' -or $cohort.timingEvaluation.blocking -or
            $cohort.revision -cne $event.revision -or (Get-TimingCohortKey $cohort) -cne $event.seriesKey) {
            throw 'Canonical reference attempted to promote rejected, incompatible or unapproved evidence.'
        }
        $saved = Invoke-HealthGit $History.directory @('rev-parse', "$($event.comparisonCommit):$($event.recordPath)")
        $current = Invoke-HealthGit $History.directory @('rev-parse', "$($History.tip):$($event.recordPath)")
        if ($saved -cne $current) { throw 'Immutable comparison changed after reference promotion.' }
        $predecessor = if ($cohort.reference) { $cohort.reference.recordPath } else { $null }
        if ($event.predecessorPath -cne $predecessor) { throw 'Canonical reference lost its pinned eligible predecessor.' }
    }
}

function Publish-TimingReference($History, $Record, [string]$ComparisonCommit) {
    if ($Record.eligibility -cne 'eligible') { return "retained: $($Record.eligibility)" }
    $key = Get-TimingCohortKey $Record
    $active = Get-TimingCanonicalReference $History $key
    $bootstrap = Get-TimingBootstrap $History $Record.bootstrapRevision
    if (!$active -and $History.mainOrder[$Record.revision] -lt $History.mainOrder[$bootstrap]) {
        return 'retained: queued revision predates the explicit bootstrap source'
    }
    if ($active -and $History.mainOrder[$active.revision] -ge $History.mainOrder[$Record.revision]) {
        return 'retained: older or already eligible main revision cannot overwrite the canonical reference'
    }
    $event = @{
        schemaVersion = 1; recordType = 'timing-reference'; repository = $FilesystemHealthRepository
        authority = 'trusted-main-push'; policyVersion = 'ci-timing-v2'; seriesKey = $key
        revision = $Record.revision; cohortRunId = $Record.cohortRunId; cohortRunAttempt = $Record.cohortRunAttempt
        mainRevision = $History.mainRevision; bootstrapRevision = $bootstrap; comparisonCommit = $ComparisonCommit
        observedAt = [DateTimeOffset]::UtcNow.ToString('O'); recordPath = Get-NonUiCohortPath $Record
        predecessorPath = if ($Record.reference) { $Record.reference.recordPath } else { $null }
    }
    $commit = Publish-HealthRecord $History $event
    return "eligible reference event published: $commit; effective pointer follows actual main order"
}
