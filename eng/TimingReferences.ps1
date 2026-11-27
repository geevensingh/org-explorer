function Set-TimingMainContext($History) {
    $null = Invoke-HealthGit $History.directory @('-c', 'credential.helper=',
        '-c', 'credential.helper=!gh auth git-credential', 'fetch', '--quiet', 'origin',
        'refs/heads/main:refs/heads/timing-main')
    if ((Invoke-HealthGit $History.directory @('rev-parse', '--is-shallow-repository')) -cne 'false') {
        throw 'Canonical timing references require complete actual main ancestry, never shallow or guessed order.'
    }
    $History.mainRevision = Invoke-HealthGit $History.directory @('rev-parse', 'refs/heads/timing-main')
    $History.mainContextDirectory = $History.directory
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

function Assert-TimingEventVersion($Record, [int]$ExpectedVersion) {
    if (!$Record.Contains('schemaVersion') -or
        ($Record.schemaVersion -isnot [int] -and $Record.schemaVersion -isnot [long]) -or
        $Record.schemaVersion -ne $ExpectedVersion) {
        throw "Unsupported or missing $($Record.recordType) schema version; expected integer schemaVersion $ExpectedVersion."
    }
}

function Get-TimingControlPath($Record) {
    if ($Record.recordType -ceq 'timing-bootstrap') { return "timing-bootstrap/$($Record.policyVersion).json" }
    return "timing-plans/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt).json"
}

function Assert-TimingControl($Record, [string]$Path) {
    Assert-TimingEventVersion $Record $(if ($Record.recordType -ceq 'timing-bootstrap') { 1 } else { 2 })
    if ($Record.recordType -cnotin @('timing-bootstrap', 'timing-plan') -or
        $Record.repository -cne $FilesystemHealthRepository -or $Record.authority -cne 'trusted-main-push' -or
        $Record.policyVersion -cnotin @('ci-timing-v2', 'ci-timing-v3', 'ci-timing-v4') -or $Record.revision -cnotmatch '^[0-9a-f]{40}$' -or
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
        'timing-bootstrap'   = @{
            PathPattern     = '^timing-bootstrap/ci-timing-v[234]\.json$'
            GetPath         = { param($record) Get-TimingControlPath $record }
            Validate        = { param($record, $path) Assert-TimingControl $record $path }
            ValidateHistory = { param($history) Assert-TimingHistory $history }
        }
        'timing-plan'        = @{
            PathPattern = '^timing-plans/[0-9a-f]{40}/[1-9][0-9]*/[1-9][0-9]*\.json$'
            GetPath     = { param($record) Get-TimingControlPath $record }
            Validate    = { param($record, $path) Assert-TimingControl $record $path }
        }
        'timing-decision'    = @{
            PathPattern = '^timing-decisions/[0-9a-f]{40}/[1-9][0-9]*/[1-9][0-9]*/3\.json$'
            GetPath     = { param($record) Get-TimingDecisionPath $record }
            Validate    = { param($record, $path) Assert-TimingDecision $record $path }
        }
        'timing-reference'   = @{
            PathPattern = '^timing-references/[0-9a-f]{64}/[0-9a-f]{40}/[1-9][0-9]*/[1-9][0-9]*\.json$'
            GetPath     = { param($record) Get-TimingReferencePath $record }
            Validate    = { param($record, $path) Assert-TimingReference $record $path }
        }
        'cohort-observation' = @{
            PathPattern              = '^observations/cohorts/[1-9][0-9]*/[1-9][0-9]*\.json$'
            GetPath                  = { param($record) "observations/cohorts/$($record.cohortRunId)/$($record.cohortRunAttempt).json" }
            Validate                 = { param($record, $path) Assert-TimingUnavailable $record $path }
            ObservationWriterAllowed = $true
        }
    }
}

function Get-TimingBootstrap($History, [string]$ExplicitRevision,
    [ValidateSet('ci-timing-v2', 'ci-timing-v3', 'ci-timing-v4')][string]$PolicyVersion = 'ci-timing-v2') {
    $saved = @($History.records | Where-Object {
        $_.Contains('recordType') -and $_.recordType -ceq 'timing-bootstrap' -and $_.policyVersion -ceq $PolicyVersion
    })
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
        if ($LASTEXITCODE -ne 0 -or $head -cne $env:GITHUB_WORKFLOW_SHA -or $env:GITHUB_ACTIONS -cne 'true' -or
            $env:GITHUB_REPOSITORY -cne $FilesystemHealthRepository) {
            throw 'Timing publication requires the committed authenticated hosted controller, not candidate artifact authority.'
        }
        $writer = Invoke-HealthApi "actions/runs/$($env:GITHUB_RUN_ID)/attempts/$($env:GITHUB_RUN_ATTEMPT)"
        if ($writer.id -ne [long]$env:GITHUB_RUN_ID -or $writer.run_attempt -ne [int]$env:GITHUB_RUN_ATTEMPT -or
            $writer.head_sha -cne $head -or $writer.event -cne 'workflow_run' -or
            $writer.head_repository.full_name -cne $FilesystemHealthRepository -or
            $writer.path -cnotin @('.github/workflows/non-ui-cohorts.yml', '.github/workflows/non-ui-cohort-history.yml') -or
            (Get-SourceTimingPolicyVersion $head) -cnotin @('ci-timing-v2', 'ci-timing-v3', 'ci-timing-v4')) {
            throw 'Canonical history requires the current approved trusted controller, independently of the older collection source.'
        }
        $dirty = & git -C $source status --porcelain
        if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Timing controller source must be clean and committed.' }
    }
}

function Assert-TimingUnavailable($Record, [string]$Path) {
    Assert-TimingEventVersion $Record 1
    if ($Record.recordType -cne 'cohort-observation' -or $Record.schemaVersion -ne 1 -or
        $Record.repository -cne $FilesystemHealthRepository -or $Record.authority -cne 'trusted-controller-observation' -or
        $Record.controllerRevision -cnotmatch '^[0-9a-f]{40}$' -or $Record.baselineEligible -isnot [bool] -or
        $Record.baselineEligible -or $Record.comparisonInputEligible -isnot [bool] -or $Record.comparisonInputEligible -or
        $null -ne $Record.revision -or $Record.validation -cne 'incomplete' -or $Record.timingHealth -cne 'inconclusive' -or
        $Record.cohortRunId -cnotmatch '^[1-9][0-9]*$' -or $Record.cohortRunAttempt -cnotmatch '^[1-9][0-9]*$' -or
        $Path -cne "observations/cohorts/$($Record.cohortRunId)/$($Record.cohortRunAttempt).json" -or
        [string]::IsNullOrWhiteSpace($Record.reason)) { throw 'Malformed baseline-ineligible unavailable cohort observation.' }
    Assert-HealthDate $Record.observedAt 'unavailable cohort observedAt'
}

function Publish-TimingUnavailable($Run, [string]$Reason, [string]$Directory, [string]$Remote, [switch]$AllowLocalFixture) {
    if ($Run.status -cne 'completed') { throw 'Unavailable cohort history requires an original terminal collection attempt.' }
    Assert-TimingAutomation $Run $Run.id $Run.run_attempt -AllowLocalFixture:$AllowLocalFixture
    $history = Open-HealthHistory $Directory $Remote -AllowLocalFixture:$AllowLocalFixture -RecordTypes (Get-LaneHealthRecordTypes)
    $history.observationWriterAuthorized = $true
    $pages = Invoke-HealthApi "actions/runs/$($Run.id)/attempts/$($Run.run_attempt)/jobs?per_page=100" @('--paginate', '--slurp')
    $record = @{
        schemaVersion = 1; recordType = 'cohort-observation'; repository = $FilesystemHealthRepository
        authority = 'trusted-controller-observation'; controllerRevision = $Run.head_sha; revision = $null
        cohortRunId = [string]$Run.id; cohortRunAttempt = [string]$Run.run_attempt
        observedAt = [DateTimeOffset]::UtcNow.ToString('O'); baselineEligible = $false; comparisonInputEligible = $false
        validation = 'incomplete'; timingHealth = 'inconclusive'; reason = $Reason
        collectionOutcome = if ($Run.conclusion) { $Run.conclusion } else { 'unavailable' }
        jobs              = @($pages | ForEach-Object { $_.jobs } |
                ForEach-Object { @{ id = $_.id; name = $_.name; status = $_.status; outcome = $_.conclusion } })
    }
    return Publish-HealthRecord $history $record
}

function Get-TimingDecisionPath($Record) {
    return "timing-decisions/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt)/3.json"
}

function Get-TimingReferencePath($Record) {
    return "timing-references/$($Record.seriesKey)/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt).json"
}

function Assert-TimingDecision($Record, [string]$Path) {
    Assert-TimingEventVersion $Record 1
    if ($Record.recordType -cne 'timing-decision' -or $Record.repository -cne $FilesystemHealthRepository -or
        $Record.authority -cne 'trusted-main-push' -or $Record.policyVersion -cnotin @('ci-timing-v2', 'ci-timing-v3', 'ci-timing-v4') -or
        $Record.policyVersion -cne $Record.cohort.policyVersion -or
        $Path -cne (Get-TimingDecisionPath $Record) -or $Record.cohort.samples.Count -ne 3 -or
        $Record.revision -cne $Record.cohort.revision -or $Record.cohortRunId -cne $Record.cohort.cohortRunId -or
        $Record.cohortRunAttempt -cne $Record.cohort.cohortRunAttempt) { throw 'Malformed frozen three-execution timing decision.' }
    Assert-NonUiCohortRecord $Record.cohort (Get-NonUiCohortPath $Record.cohort)
}

function Assert-TimingReference($Record, [string]$Path) {
    Assert-TimingEventVersion $Record 1
    if ($Record.recordType -cne 'timing-reference' -or $Record.repository -cne $FilesystemHealthRepository -or
        $Record.authority -cne 'trusted-main-push' -or $Record.policyVersion -cnotin @('ci-timing-v2', 'ci-timing-v3', 'ci-timing-v4') -or
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
    if (!$events.Count) { return $null }
    if (!$History.Contains('mainOrder') -or !$History.Contains('mainContextDirectory') -or
        $History.mainContextDirectory -cne $History.directory) { Set-TimingMainContext $History }
    if ($CandidateRevision) { Assert-TimingMainRevision $History $CandidateRevision }
    foreach ($event in $events) {
        Assert-TimingMainRevision $History $event.revision
        if ((Get-SourceTimingPolicyVersion $event.revision -GitDirectory $History.directory) -cne $event.policyVersion) {
            throw 'A canonical reference cannot rewrite an unactivated historical source as approved evidence.'
        }
    }
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
    $effectivePolicy = if ($Record.Contains('effectivePolicyVersion')) { $Record.effectivePolicyVersion }
        else { $Record.sourcePolicyVersion }
    foreach ($sample in $Record.samples) {
        $sample.rich.seriesKey = $null
        try { $sample.rich.seriesKey = Get-RichCohortSeriesKey $sample $effectivePolicy }
        catch {
            $diagnostic = "Complete cohort comparison unavailable: $($_.Exception.Message)"
            if ($sample.diagnostics -cnotcontains $diagnostic) { $sample.diagnostics += $diagnostic }
        }
    }
    $reference = Get-TimingPinnedReference $Record $History
    $key = Get-TimingCohortKey $Record
    if ($reference) {
        if ($reference.revision -ceq $Record.revision -or $reference.eligibility -cne 'eligible' -or
            $reference.policyVersion -cne $Record.policyVersion -or
            ($Record.validation -ceq 'success' -and $key -and (Get-TimingCohortKey $reference) -cne $key)) {
            throw 'Pinned canonical comparison is self-referencing, ineligible or incompatible.'
        }
        if (!$key) {
            $Record.historyDiagnostics += 'Pinned predecessor metadata is retained; missing/incompatible current keys cannot establish relative comparison.'
        }
        $Record.reference = @{
            kind = 'pinned-eligible-reference'; recordPath = Get-NonUiCohortPath $reference
            revision = $reference.revision; cohortId = $reference.cohortId; observedAt = $reference.observedAt
            ageSeconds    = ([DateTimeOffset]$Record.observedAt - [DateTimeOffset]$reference.observedAt).TotalSeconds
            medianSeconds = $reference.metrics.executionSeconds.median
            deltaSeconds  = if ($Record.metrics.Contains('executionSeconds')) {
                $Record.metrics.executionSeconds.median - $reference.metrics.executionSeconds.median
            }
            else { $null }
            evidenceUrl   = $reference.samples[0].evidence.url
            evidenceState = if (@($reference.samples | Where-Object {
                        $parts = @($_, $_.rich.prefix, $_.rich.tail, $_.rich.union)
                        @($parts | Where-Object { !$_.evidence.expiresAt -or
                                [DateTimeOffset]$_.evidence.expiresAt -le [DateTimeOffset]$Record.observedAt }).Count
                    }).Count) { 'expired-or-unavailable' } else { 'retained-metadata-only' }
        }
    }
    Set-TimingPolicyEvaluation $Record $reference $Record.historySnapshot -PolicyVersion $Record.policyVersion
    $Record.eligibility = $Record.timingEvaluation.eligibility
    $Record.timingHealth = $Record.timingEvaluation.health
    $Record.extraSamples = @($Record.timingEvaluation.additionalSamples)
    $Record.promotion = if ($Record.eligibility -ceq 'eligible') { 'eligible-after-comparison' } else { 'retained' }
}

function Get-TimingEvaluationState($Evaluation) {
    $state = @{}
    foreach ($field in @('version', 'mode', 'approval', 'health', 'blocking', 'validationBlocking', 'eligibility',
            'additionalSamples', 'historySnapshot')) { $state[$field] = $Evaluation[$field] }
    $state.metrics = @{}
    foreach ($name in $Evaluation.metrics.Keys) {
        $metric = @{} + $Evaluation.metrics[$name]
        $metric.Remove('reason'); $metric.Remove('relativeRule')
        $state.metrics[$name] = $metric
    }
    return (ConvertTo-HealthCanonicalValue $state | ConvertTo-Json -Depth 30 -Compress)
}

function Assert-TimingHistory($History) {
    $bootstrap = @($History.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-bootstrap' })
    $cohorts = @($History.records | Where-Object {
            $_.Contains('recordType') -and $_.recordType -ceq 'performance-cohort' -and $_.mode -ceq 'automatic-baseline'
        })
    $cohorts += @($History.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-decision' } |
            ForEach-Object { $_.cohort })
    foreach ($cohort in $cohorts) {
        $policyBootstrap = @($bootstrap | Where-Object { $_.policyVersion -ceq $cohort.policyVersion })
        if ($policyBootstrap.Count -ne 1 -or $cohort.bootstrapRevision -cne $policyBootstrap[0].revision) {
            throw 'Automatic comparison lacks the once-pinned bootstrap source.'
        }
        $plans = @($History.records | Where-Object {
                $_.Contains('recordType') -and $_.recordType -ceq 'timing-plan' -and
                $_.revision -ceq $cohort.revision -and $_.cohortRunId -ceq $cohort.cohortRunId -and
                $_.cohortRunAttempt -ceq $cohort.cohortRunAttempt
            })
        if ($plans.Count -ne 1 -or !$plans[0].collect -or $plans[0].policyVersion -cne $cohort.policyVersion -or $plans[0].ciRunId -cne $cohort.ciRunId -or
            $plans[0].ciRunAttempt -cne $cohort.ciRunAttempt) { throw 'Automatic comparison lost its immutable source-qualified plan.' }
        $reference = if ($cohort.reference) { Get-TimingSavedCohort $History $cohort.reference.recordPath } else { $null }
        if ($reference -and ($reference.revision -ceq $cohort.revision -or $reference.eligibility -cne 'eligible' -or
                $reference.policyVersion -cne $cohort.policyVersion)) { throw 'Stored comparison used a self or ineligible reference.' }
        $expected = $cohort | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable -DateKind String -Depth 30
        Set-TimingPolicyEvaluation $expected $reference $cohort.historySnapshot -PolicyVersion $cohort.policyVersion
        if ((Get-TimingEvaluationState $expected.timingEvaluation) -cne (Get-TimingEvaluationState $cohort.timingEvaluation) -or
            $cohort.eligibility -cne $expected.timingEvaluation.eligibility -or
            $cohort.timingHealth -cne $expected.timingEvaluation.health -or
            ($cohort.extraSamples -join ',') -cne ($expected.timingEvaluation.additionalSamples -join ',')) {
            throw 'Stored timing health/eligibility does not reproduce from its immutable approved comparison inputs.'
        }
    }
    $events = @($History.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-reference' })
    foreach ($event in $events) {
        $policyBootstrap = @($bootstrap | Where-Object { $_.policyVersion -ceq $event.policyVersion })
        if ($policyBootstrap.Count -ne 1 -or $event.bootstrapRevision -cne $policyBootstrap[0].revision) {
            throw 'Canonical reference lacks the once-pinned trusted bootstrap.'
        }

        $cohort = Get-TimingSavedCohort $History $event.recordPath
        if ($cohort.policyVersion -cne $event.policyVersion -or $cohort.eligibility -cne 'eligible' -or
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
    Set-TimingMainContext $History
    Assert-TimingMainRevision $History $Record.revision
    $key = Get-TimingCohortKey $Record
    $active = Get-TimingCanonicalReference $History $key
    $bootstrap = Get-TimingBootstrap $History $Record.bootstrapRevision -PolicyVersion $Record.policyVersion
    if (!$active -and $History.mainOrder[$Record.revision] -lt $History.mainOrder[$bootstrap]) {
        return 'retained: queued revision predates the explicit bootstrap source'
    }
    if ($active -and $History.mainOrder[$active.revision] -ge $History.mainOrder[$Record.revision]) {
        return 'retained: older or already eligible main revision cannot overwrite the canonical reference'
    }
    $event = @{
        schemaVersion = 1; recordType = 'timing-reference'; repository = $FilesystemHealthRepository
        authority = 'trusted-main-push'; policyVersion = $Record.policyVersion; seriesKey = $key
        revision = $Record.revision; cohortRunId = $Record.cohortRunId; cohortRunAttempt = $Record.cohortRunAttempt
        mainRevision = $History.mainRevision; bootstrapRevision = $bootstrap; comparisonCommit = $ComparisonCommit
        observedAt = [DateTimeOffset]::UtcNow.ToString('O'); recordPath = Get-NonUiCohortPath $Record
        predecessorPath = if ($Record.reference) { $Record.reference.recordPath } else { $null }
    }
    $commit = Publish-HealthRecord $History $event
    return "eligible reference event published: $commit; effective pointer follows actual main order"
}
