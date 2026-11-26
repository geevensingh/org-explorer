function Get-TimingLane([string]$Lane) {
    if ($Lane -cmatch '(^|-)non-ui-performance$') { return 'non-ui-performance' }
    if ($Lane -cmatch '(^|-)ui-scale-prefix$') { return 'ui-scale-prefix' }
    if ($Lane -cmatch '(^|-)ui-scale-tail$') { return 'ui-scale-tail' }
    if ($Lane -cmatch '(^|-)ui-scale$') { return 'ui-scale' }
    return $Lane
}

function Get-ApprovedTimingPolicy(
    [ValidateSet('rich-scale-original-v1', 'ci-timing-v2')][string]$Version = 'ci-timing-v2') {
    return @{
        version = $Version
        prefixSafetySeconds = if ($Version -ceq 'ci-timing-v2') { 900 } else { 660 }
        prefixWatchdogMinutes = if ($Version -ceq 'ci-timing-v2') { 30 } else { 13 }
        tailSafetySeconds = 660
        richNoteGoalMs = 1000
        richNoteCeilingMs = if ($Version -ceq 'ci-timing-v2') { 1500 } else { 1050 }
    }
}

function Get-SourceTimingPolicyVersion([string]$Revision, [switch]$Hosted, [string]$GitDirectory) {
    if ($Revision -cnotmatch '^[0-9a-f]{40}$') { throw 'Timing policy requires an exact trusted source revision.' }
    $path = 'eng/timing-policy.json'
    if ($Hosted) {
        $tree = Invoke-HealthApi "git/trees/$Revision`?recursive=1"
        if ($tree.truncated) { throw 'Truncated source tree cannot establish timing policy.' }
        $entries = @($tree.tree | Where-Object { $_.path -ceq $path })
        if (!$entries.Count) { return 'rich-scale-original-v1' }
        if ($entries.Count -ne 1 -or $entries[0].type -cne 'blob') { throw 'Invalid timing policy source entry.' }
        $blob = Invoke-HealthApi "git/blobs/$($entries[0].sha)"
        if ($blob.encoding -cne 'base64') { throw 'Unsupported timing policy source encoding.' }
        $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($blob.content))
    } else {
        [string[]]$gitArguments = if ($GitDirectory) { @("--git-dir=$GitDirectory") }
            else { @('-C', (Split-Path $PSScriptRoot -Parent)) }
        $entry = @(git @gitArguments ls-tree $Revision -- $path)
        if ($LASTEXITCODE -ne 0) { throw 'Trusted timing policy source revision is unavailable.' }
        if (!$entry.Count) { return 'rich-scale-original-v1' }
        $text = (git @gitArguments show "${Revision}:$path") -join "`n"
        if ($LASTEXITCODE -ne 0) { throw 'Trusted timing policy source cannot be read.' }
    }
    $policy = $text | ConvertFrom-Json -AsHashtable
    if ($policy.Count -ne 1 -or $policy.version -cne 'ci-timing-v2') {
        throw 'Unknown or amended timing policy requires explicit approval and implementation.'
    }
    return $policy.version
}

function New-TimingExecutionRecord([string]$PerformanceDirectory, [string]$PrefixDirectory, [string]$TailDirectory,
    [string]$Revision, [string]$RunId, [string]$RunAttempt) {
    $record = @{
        schemaVersion = 2; recordType = 'verification-execution'; revision = $Revision; validation = 'success'
        runId = $RunId; runAttempt = $RunAttempt; policyVersion = 'ci-timing-v2'
        observedAt = [DateTimeOffset]::UtcNow.ToString('O'); unexpectedExecutions = $false; measurements = $null
        samples = @(); metrics = @{}; comparisonDiagnostics = @()
        verificationScope = 'one complete exact-candidate execution; never a three-to-five collection cohort'
    }
    function Add-Metric([string]$Name, $Value) {
        Assert-HealthNumber $Value $Name
        $record.metrics[$Name] = @{ count = 1; minimum = $Value; maximum = $Value; median = $Value; range = 0 }
    }
    $sample = @{ configuration = $null; rich = @{ seriesKey = $null } }
    foreach ($worker in @('prefix', 'tail')) {
        $directory = if ($worker -ceq 'prefix') { $PrefixDirectory } else { $TailDirectory }
        $validated = Get-UiScaleWorkerResult $directory $worker $Revision $RunId $RunAttempt -ExpectedPolicyVersion ci-timing-v2
        $sample.rich[$worker] = @{ run = $validated.run
            report = $validated.report | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable -Depth 30 -DateKind String }
        Add-Metric "$worker-executionSeconds" $validated.report.executionSeconds
        $phase = if ($worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }
        $scenario = if ($worker -ceq 'prefix') { 'scale-mutation-save-prefix' } else { 'scale-mutation-delivery-tail' }
        $profile = Get-Content (Join-Path $validated.run "$scenario-ui-sqlite-rich-profile-latency-$phase.json") -Raw | ConvertFrom-Json
        Add-Metric "$worker-profile-p95-ms" $profile.p95Ms
    }
    $report = Get-Content (Join-Path $PerformanceDirectory 'result.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    if ($report.revision -cne $Revision -or $report.expectedRevision -cne $Revision -or
        $report.runId -cne $RunId -or $report.runAttempt -cne $RunAttempt -or $report.outcome -cne 'success') {
        throw 'Timing comparison requires the same complete non-UI candidate/run/attempt.'
    }
    if ($report.Contains('configuration')) { $sample.configuration = $report.configuration }
    Add-Metric executionSeconds $report.executionSeconds
    $sample.rich.handoff = Get-Content (Join-Path $sample.rich.prefix.run 'handoff\handoff.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($phase in @('normal-executable', 'background-export')) {
        $note = Get-Content (Join-Path $sample.rich.prefix.run "scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json") -Raw | ConvertFrom-Json
        Add-Metric "prefix-$phase-localDurability-p95-ms" $note.localDurability.p95Ms
    }
    $log = Join-Path $PerformanceDirectory "$($report.runDirectory)\scale-measurements.log"
    $lines = @()
    if (Test-Path -LiteralPath $log) { $lines = @(Get-Content $log | Where-Object { $_.StartsWith('SCALE ORDINARY-PROFILE ') }) }
    if ($lines.Count -eq 1) {
        $profile = $lines[0].Substring('SCALE ORDINARY-PROFILE '.Length) | ConvertFrom-Json -AsHashtable
        $metric = Read-HealthLatency $profile 'profile-save' 21
        Add-Metric profile-p95-ms $metric.value
    } else { $record.comparisonDiagnostics += 'Non-UI profile timing unavailable; no substitute or zero was supplied.' }
    $record.samples = @($sample)
    return $record
}

function Set-TimingExecutionComparison($Record, $History) {
    $sample = $Record.samples[0]
    if ($null -eq $sample.configuration) { throw 'Resolved non-UI comparison configuration is unavailable.' }
    $sample.rich.seriesKey = Get-RichCohortSeriesKey $sample ci-timing-v2
    $reference = $null
    $Record.knownSeriousCohorts = @()
    if ($History -and @($History.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-reference' }).Count) {
        if (!$History.Contains('mainOrder')) { Set-TimingMainContext $History }
        $candidate = if ($History.mainOrder.ContainsKey($Record.revision)) { $Record.revision } else { $null }
        $reference = Get-TimingCanonicalReference $History $sample.rich.seriesKey $candidate
        foreach ($cohort in @($History.records | Where-Object {
            $_.Contains('recordType') -and $_.recordType -ceq 'performance-cohort' -and
            $_.policyVersion -ceq 'ci-timing-v2' -and $_.revision -ceq $Record.revision -and
            $_.samples.Count -eq 5 -and $_.validation -ceq 'success' -and
            (Get-TimingCohortKey $_) -ceq $sample.rich.seriesKey
        })) {
            $copy = $cohort | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable -Depth 40 -DateKind String
            $pinned = Get-TimingPinnedReference $copy $History
            Set-TimingPolicyEvaluation $copy $pinned $History.tip
            if (@($copy.timingEvaluation.metrics.Values | Where-Object { $_.Contains('seriousRegression') -and $_.seriousRegression }).Count) {
                $Record.knownSeriousCohorts += @{ cohortId = $copy.cohortId; evidenceUrl = $copy.samples[0].evidence.url
                    referenceRevision = $pinned.revision }
            }
        }
    }
    Set-TimingPolicyEvaluation $Record $reference $(if ($History) { $History.tip } else { $null })
    if ($Record.knownSeriousCohorts.Count) { $Record.timingEvaluation.blocking = $true; $Record.timingEvaluation.health = 'red' }
}

function Set-TimingPolicyPreview($Record, $History,
    [ValidateSet('ci-timing-preview-v1', 'ci-timing-preview-v2')][string]$PolicyVersion = 'ci-timing-preview-v2',
    $PinnedReference = $null, [switch]$Active) {
    $richNoteCeiling = if ($PolicyVersion -ceq 'ci-timing-preview-v1') { 1050 } else { 1500 }
    $prefixWatchdog = if ($PolicyVersion -ceq 'ci-timing-preview-v1') { 17 } else { 30 }
    $approval = if ($PolicyVersion -ceq 'ci-timing-preview-v1') { 'pending' } else { 'approved' }
    if ($Record.recordType -cne 'performance-cohort' -and !($Active -and $Record.recordType -ceq 'verification-execution')) {
        $metrics = @{}
        if ($Record.measurements) {
            $metrics.executionSeconds = @{
                health = if ($Record.validation -ceq 'success') { 'unbaselined' } else { 'inconclusive' }
                median = $Record.measurements.executionSeconds; range = $null; count = 1; reference = $null
                reason = 'A single lane execution is not an independent three-to-five reference cohort.'
            }
            foreach ($metric in $Record.measurements.metrics) {
                $richNote = (Get-TimingLane $Record.lane) -cin @('ui-scale-prefix', 'ui-scale') -and
                    $metric.name -cmatch '^note-durability-(normal-executable|background-export)$'
                $goal = if ($richNote) { 1000 } elseif ($metric.Contains('goalMs')) { $metric.goalMs } else { $null }
                $budget = if ($richNote) { $richNoteCeiling } elseif ($metric.Contains('budgetMs') -and
                    (!$metric.Contains('budgetEnforced') -or $metric.budgetEnforced)) { $metric.budgetMs } else { $null }
                if ($Active -and !$richNote) {
                    $goal = $null
                    $budget = if ($metric.name -cmatch '^(profile-save|note-durability)(-|$)') { 1000 } else { $null }
                }
                $metrics[$metric.name] = @{
                    health = 'unbaselined'; median = $metric.value; range = $null; count = 1; reference = $null
                    relativeHealth = 'unbaselined'
                    reason = 'No independent compatible cohort for this metric.'
                }
                if ($null -ne $goal) {
                    $metrics[$metric.name].goal = $goal
                    $metrics[$metric.name].goalMet = $metric.value -le $goal
                    if ($metric.value -gt $goal) {
                        $metrics[$metric.name].health = 'yellow'
                        $metrics[$metric.name].reason = "Policy${goal}ms goal missed; relative comparison remains unbaselined."
                    }
                }
                if ($metric.Contains('budgetMs')) { $metrics[$metric.name].originalLimit = $metric.budgetMs }
                if ($null -ne $budget) {
                    $metrics[$metric.name].limit = $budget
                    $metrics[$metric.name].hardLimit = $budget
                    $metrics[$metric.name].headroom = $budget - $metric.value
                    $metrics[$metric.name].absoluteRequirement = if ($metric.value -le $budget) { 'satisfied' } else { 'violated' }
                }
                if ($null -ne $budget -and $metric.value -gt $budget) {
                    $metrics[$metric.name].health = 'red'
                    $metrics[$metric.name].reason = "Policy${budget}ms absolute ceiling violated; original validation remains unchanged."
                }
            }
        }
        $Record.policyPreview = @{
            version = $PolicyVersion; mode = 'observation'; approval = $approval
            prefixSafetySeconds = 900; prefixWatchdogMinutes = $prefixWatchdog
            practicalFloors = if ($PolicyVersion -ceq 'ci-timing-preview-v2') {
                @{ medianIncreasePercent = 20; productLatencyIncreaseMs = 100; durationIncreaseSeconds = 30 }
            } else { $null }
            blocking = $false; promotion = 'disabled'; currentValidation = $Record.validation
            historySnapshot = if ($History) { $History.tip } else { $null }
            metrics = $metrics; additionalSamples = @(); eligibility = 'pending'
        }
        return
    }
    $key = Get-TimingCohortKey $Record
    $reference = $null
    if ($Active) {
        if ($PinnedReference -and $key -and $PinnedReference.revision -cne $Record.revision -and
            $PinnedReference.validation -ceq 'success' -and $PinnedReference.authority -ceq 'trusted-main-push' -and
            (Get-TimingCohortKey $PinnedReference) -ceq $key) {
            $reference = $PinnedReference
        }
    } elseif ($History -and $key) {
        $references = @($History.records | Where-Object {
            $_.Contains('recordType') -and $_.recordType -ceq 'performance-cohort' -and
            $_.schemaVersion -eq $Record.schemaVersion -and $_.validation -ceq 'success' -and
            $_.authority -ceq 'trusted-main-push' -and $_.revision -cne $Record.revision -and
            [DateTimeOffset]$_.observedAt -le [DateTimeOffset]$Record.observedAt -and
            (Get-TimingCohortKey $_) -ceq $key
        } | Sort-Object @{ Expression = { [DateTimeOffset]$_.observedAt }; Descending = $true })
        if ($references.Count) { $reference = $references[0] }
    }
    $metrics = @{}
    $inconsistent = $false
    $widerRange = $false
    $lowHeadroom = $false
    $serious = $false
    foreach ($name in $Record.metrics.Keys) {
        $metric = $Record.metrics[$name]
        $gateable = $name -cin @('executionSeconds', 'prefix-executionSeconds', 'tail-executionSeconds',
            'profile-p95-ms', 'prefix-profile-p95-ms', 'tail-profile-p95-ms') -or
            $name -cmatch '^prefix-.*-localDurability-p95-ms$'
        $preview = @{
            health = 'unbaselined'; median = $metric.median; range = $metric.range
            count = $metric.count; reference = $null; seriousRegression = $false; lowHeadroom = $false
            deltaAbsolute = $null; deltaPercent = $null; practicalSignificance = $null
            relativeRule = if ($gateable) { 'proposed-gate; not activated' } else { 'informational' }
            reason = 'No complete compatible non-self reference cohort; no green comparison.'
        }
        if ($reference -and $reference.metrics.ContainsKey($name)) {
            $prior = $reference.metrics[$name]
            $preview.deltaAbsolute = $metric.median - $prior.median
            if ($prior.median -gt 0) { $preview.deltaPercent = 100 * $preview.deltaAbsolute / $prior.median }
            $preview.reference = @{
                revision = $reference.revision; cohortId = $reference.cohortId
                median = $prior.median; minimum = $prior.minimum; maximum = $prior.maximum
                range = $prior.range; count = $prior.count
                ageSeconds = ([DateTimeOffset]$Record.observedAt - [DateTimeOffset]$reference.observedAt).TotalSeconds
                kind = 'proposal-reference-observation; not canonical'
            }
            $completeTiming = $Record.validation -ceq 'success'
            if (!$Active -and !$completeTiming -and $name -ceq 'prefix-executionSeconds' -and $Record.schemaVersion -eq 2) {
                $plan = Get-UiScalePlan
                $completeTiming = !@($Record.samples | Where-Object {
                    $report = $_.rich.prefix.report
                    !$report -or $report.observedOutcome -cne 'success' -or $report.phase -cne 'finished' -or
                    $report.outcome -cnotin @('success', 'timed_out') -or
                    $report.diagnosticOnly -or $report.trackedChanges -or $report.untrackedChanges -or
                    $report.executionSeconds -gt 900 -or
                    (@($report.completedPhases) -join "`n") -cne (@($plan.phases.prefix) -join "`n") -or
                    (@($report.requiredScenarios) -join "`n") -cne (@($plan.workers.prefix) -join "`n") -or
                    (@($report.steps.name) -join "`n") -cne (@('restore-app', 'restore-tests', 'build-app', 'display-prerequisite', 'ui-scale') -join "`n") -or
                    @($report.steps | Where-Object { $_.exitCode -ne 0 }).Count
                }).Count
            }
            if (!$completeTiming -or
                $metric.count -ne $Record.samples.Count -or $prior.count -ne $reference.samples.Count) {
                $preview.health = 'inconclusive'
                $preview.reason = 'Failed/incomplete verification or missing measurements cannot supply a complete comparison.'
            } elseif ($prior.range -eq 0) {
                $preview.health = 'inconclusive'
                $preview.reason = 'Zero observed reference range cannot establish a calibrated noise floor.'
            } elseif ($PolicyVersion -ceq 'ci-timing-preview-v2' -and $gateable -and $prior.median -le 0) {
                $preview.health = 'inconclusive'
                $preview.reason = 'Reference median is zero; practical percentage significance is unavailable.'
            } else {
                $variable = $metric.range -gt $prior.range
                $widerRange = $widerRange -or $variable
                $warning = $variable -or $metric.maximum -gt $prior.maximum
                $preview.health = if ($warning) { 'yellow' } else { 'green' }
                $preview.reason = if ($warning) { 'Outside the observed reference envelope or wider observed range; not a proven regression.' }
                    else { 'Within the observed reference envelope and variability; not a calibrated probability.' }
                $durationMetric = $name -cin @('executionSeconds', 'prefix-executionSeconds', 'tail-executionSeconds')
                $absoluteFloor = if ($durationMetric) { 30 } else { 100 }
                $practical = $PolicyVersion -ceq 'ci-timing-preview-v1' -or
                    ($null -ne $preview.deltaPercent -and $preview.deltaPercent -ge 20 -and $preview.deltaAbsolute -ge $absoluteFloor)
                if ($gateable -and $PolicyVersion -ceq 'ci-timing-preview-v2') { $preview.practicalSignificance = $practical }
                if ($gateable -and $practical -and $Record.validation -ceq 'success' -and $Record.samples.Count -eq 5 -and
                    !$variable -and $metric.minimum -gt ($prior.maximum + $prior.range) -and
                    $metric.median - $prior.median -gt $prior.range) {
                    $preview.health = 'red'; $preview.seriousRegression = $true
                    $preview.reason = 'All five complete measurements exceed the reference maximum by more than its full observed range, with no wider candidate range and all applicable practical floors met.'
                    $serious = $true
                }
                $inconsistent = $inconsistent -or $variable -or $warning
            }
        }
        $preview.relativeHealth = $preview.health
        $richNote = $name -cmatch '^prefix-.*-localDurability-p95-ms$'
        $hardLimit = if ($name -ceq 'profile-p95-ms' -or $name -cmatch '^(prefix|tail)-profile-p95-ms$') { 1000 }
            elseif ($richNote) { $richNoteCeiling } else { $null }
        $executionLimit = if ($name -cin @('executionSeconds', 'tail-executionSeconds')) { 660 }
            elseif ($name -ceq 'prefix-executionSeconds') { 900 } else { $null }
        $limit = if ($null -ne $hardLimit) { $hardLimit } else { $executionLimit }
        if ($null -ne $limit) {
            $preview.headroom = $limit - $metric.maximum
            $preview.limit = $limit
            $headroomRange = if ($preview.reference) { $preview.reference.range } else { $metric.range }
            if ($Record.validation -ceq 'success' -and $preview.headroom -le $headroomRange) {
                $preview.lowHeadroom = $true
                $lowHeadroom = $true
                if ($preview.health -cne 'red') { $preview.health = 'yellow' }
                $preview.reason = if ($preview.reference) {
                    'Remaining headroom is no larger than one full observed reference range.'
                } else { 'The observed bootstrap range spans remaining headroom; relative comparison remains unbaselined.' }
                $inconsistent = $true
            }
            if ($Active -and $null -ne $executionLimit -and $metric.maximum -gt $executionLimit) {
                $preview.health = 'red'
                $preview.reason = 'An individual whole execution exceeds its approved safety limit.'
            }
        }
        if ($null -ne $hardLimit) {
            $preview.hardLimit = $hardLimit
            $preview.headroom = $hardLimit - $metric.maximum
            $preview.absoluteRequirement = if ($metric.maximum -le $hardLimit) { 'satisfied' } else { 'violated' }
            if ($metric.maximum -gt $hardLimit) {
                $preview.health = 'red'; $preview.seriousRegression = $false
                $preview.reason = 'An individual policy absolute ceiling is violated; original validation and raw evidence remain unchanged.'
            }
            if ($richNote) {
                $preview.goal = 1000; $preview.goalMet = $metric.maximum -le 1000
            }
            if ($richNote -and $metric.maximum -gt 1000 -and $metric.maximum -le $richNoteCeiling -and $preview.health -cne 'red') {
                $preview.health = 'yellow'
                $preview.reason = "Policy1000ms goal missed within${richNoteCeiling}ms absolute ceiling; relative comparison remains " + $preview.relativeHealth + '.'
            }
        }
        $metrics[$name] = $preview
    }
    $Record.policyPreview = @{
        version = $PolicyVersion; mode = 'observation'; approval = $approval
        prefixSafetySeconds = 900; prefixWatchdogMinutes = $prefixWatchdog
        practicalFloors = if ($PolicyVersion -ceq 'ci-timing-preview-v2') {
            @{ medianIncreasePercent = 20; productLatencyIncreaseMs = 100; durationIncreaseSeconds = 30 }
        } else { $null }
        blocking = $false; promotion = 'disabled'; currentValidation = $Record.validation
        historySnapshot = if ($History) { $History.tip } else { $null }
        metrics = $metrics; additionalSamples = @(); eligibility = 'pending'
    }
    if ($Record.validation -ceq 'success' -and $key -and !$Record.unexpectedExecutions -and $inconsistent -and $Record.samples.Count -eq 3) {
        $Record.policyPreview.additionalSamples = @(4, 5)
        $Record.policyPreview.eligibility = 'extend-proposed; no dispatch authorized'
    }
    if ($serious) { $Record.policyPreview.eligibility = 'rejected-proposed-relative; current gates unchanged' }
    elseif ($Record.samples.Count -eq 5 -and ($widerRange -or $lowHeadroom)) { $Record.policyPreview.eligibility = 'inconclusive' }
    if ($Record.validation -cne 'success') { $Record.policyPreview.eligibility = 'rejected-current-validation' }
}

function Set-TimingPolicyEvaluation($Record, $PinnedReference = $null, [string]$HistoryTip = $null) {
    if ($Record.recordType -cin @('performance-cohort', 'verification-execution')) {
        foreach ($entry in @($Record.metrics.GetEnumerator())) {
            $metric = $entry.Value
            foreach ($field in @('median', 'minimum', 'maximum', 'range', 'count')) {
                Assert-HealthNumber $metric[$field] "candidate $($entry.Key).$field"
            }
        }
        if ($PinnedReference) {
            if ($PinnedReference.policyVersion -cne 'ci-timing-v2' -or
                $PinnedReference.samples.Count -notin @(3, 5)) {
                throw 'Pinned reference is not an eligible approved-policy cohort.'
            }
            foreach ($entry in @($PinnedReference.metrics.GetEnumerator())) {
                foreach ($field in @('median', 'minimum', 'maximum', 'range', 'count')) {
                    Assert-HealthNumber $entry.Value[$field] "reference $($entry.Key).$field"
                }
            }
        }
    }
    $previousPreview = if ($Record.Contains('policyPreview')) { $Record.policyPreview } else { $null }
    Set-TimingPolicyPreview $Record $null ci-timing-preview-v2 -PinnedReference $PinnedReference -Active
    $evaluation = $Record.policyPreview
    if ($null -ne $previousPreview) { $Record.policyPreview = $previousPreview } else { $Record.Remove('policyPreview') }
    $evaluation.version = 'ci-timing-v2'; $evaluation.mode = 'enforced'; $evaluation.approval = 'approved'
    $evaluation.historySnapshot = $HistoryTip
    $evaluation.promotion = 'external; never a release prerequisite'
    $evaluation.validationBlocking = $Record.validation -cne 'success'
    if ($Record.recordType -cne 'performance-cohort' -and $Record.measurements) {
        $duration = $evaluation.metrics.executionSeconds
        $lane = Get-TimingLane $Record.lane
        $limit = if ($lane -ceq 'core') { 300 }
            elseif ($lane -cmatch '^(desktop-|high-resolution-)') { 900 }
            elseif ($lane -ceq 'ui-scale-prefix') { 900 }
            elseif ($lane -cin @('filesystem', 'non-ui-performance', 'ui-scale-tail')) { 660 } else { $null }
        if ($null -ne $limit) {
            $duration.limit = $limit; $duration.headroom = $limit - $duration.median
            if ($duration.median -gt $limit) {
                $duration.health = 'red'; $duration.reason = 'Whole execution exceeds its effective safety limit.'
            }
        }
    }
    $red = @($evaluation.metrics.Values | Where-Object { $_.health -ceq 'red' }).Count -gt 0
    $yellow = @($evaluation.metrics.Values | Where-Object { $_.health -ceq 'yellow' }).Count -gt 0
    $evaluation.health = if ($red) { 'red' } elseif ($yellow) { 'yellow' }
        elseif ($evaluation.validationBlocking) { 'inconclusive' }
        elseif (!$evaluation.metrics.Count -or @($evaluation.metrics.Values | Where-Object { $_.health -cne 'green' }).Count) { 'unbaselined' } else { 'green' }
    $evaluation.blocking = $evaluation.validationBlocking -or $red
    $relativeUncertain = @($evaluation.metrics.Values | Where-Object {
        $_.Contains('relativeRule') -and $_.relativeRule -cne 'informational' -and $_.relativeHealth -ceq 'inconclusive'
    }).Count -gt 0
    if ($Record.recordType -ceq 'performance-cohort' -and !$evaluation.blocking -and !$relativeUncertain -and
        (Get-TimingCohortKey $Record) -and $Record.samples.Count -eq 3 -and $yellow) {
        $evaluation.additionalSamples = @(4, 5)
    }
    $evaluation.eligibility = if ($evaluation.blocking) { 'rejected' }
        elseif ($relativeUncertain) { 'inconclusive' }
        elseif ($evaluation.additionalSamples.Count) { 'extend' }
        elseif ($evaluation.eligibility -ceq 'inconclusive') { 'inconclusive' } else { 'eligible' }
    if ($Record.Contains('sourcePolicyCompatible') -and !$Record.sourcePolicyCompatible) {
        $evaluation.eligibility = 'inconclusive'; $evaluation.additionalSamples = @()
    }
    if ($Record.recordType -ceq 'performance-cohort' -and !(Get-TimingCohortKey $Record) -and !$evaluation.blocking) {
        $evaluation.eligibility = 'inconclusive'; $evaluation.additionalSamples = @()
    }
    foreach ($metric in $evaluation.metrics.Values) {
        if ($metric.Contains('relativeRule') -and $metric.relativeRule -cne 'informational') { $metric.relativeRule = 'approved-gate' }
        if ($metric.reference) { $metric.reference.kind = 'pinned-eligible-reference' }
    }
    $Record.timingEvaluation = $evaluation
}

function Format-TimingPolicyEvaluation($Evaluation) {
    $text = Format-TimingPolicyPreview $Evaluation
    $text = $text.Replace('### Timing policy preview', '### Active timing policy')
    $text = $text.Replace('OBSERVATION ONLY; numerical policy approved; activation pending. No new gate or canonical promotion.',
        'APPROVED AND ENFORCED. Reference selection/promotion remains independent.')
    $text = $text.Replace('These are separate limits, not current activation.', 'Watchdog allowance is not accepting execution time.')
    $text = $text.Replace('preview blocking: **false**', "gate blocking: **$($Evaluation.blocking)**")
    return $text + "`nTiming health: **$($Evaluation.health)**. Measurement overhead is included; unknown overhead remains unquantified, never subtracted."
}

function Get-TimingCohortKey($Record) {
    $key = $null
    foreach ($sample in $Record.samples) {
        $value = if ($Record.schemaVersion -eq 2) { $sample.rich.seriesKey } else { $sample.seriesKey }
        if ($value -isnot [string] -or $value -cnotmatch '^[0-9a-f]{64}$') { return $null }
        if ($null -ne $key -and $key -cne $value) { return $null }
        $key = $value
    }
    return $key
}

function Format-TimingPolicyPreview($Preview) {
    $approval = if ($Preview.approval -ceq 'approved') { 'numerical policy approved; activation pending' } else { 'approval pending' }
    $lines = @('', '### Timing policy preview', '',
        "``$($Preview.version)``; OBSERVATION ONLY; $approval. No new gate or canonical promotion.",
        "Policy prefix safety: $($Preview.prefixSafetySeconds)s; watchdog: $($Preview.prefixWatchdogMinutes)min. These are separate limits, not current activation.",
        "Current validation remains **$($Preview.currentValidation)**; preview blocking: **false**.",
        'Release never waits for a reference. Fixture previews are not hosted timing acceptance.',
        '', '| Metric | Health | Median | Range | Reference envelope | Headroom | Reason |',
        '|---|---|---:|---:|---|---:|---|')
    foreach ($name in @($Preview.metrics.Keys | Sort-Object)) {
        $metric = $Preview.metrics[$name]
        $reference = if ($metric.reference) {
            "``$($metric.reference.revision)``; $($metric.reference.minimum)-$($metric.reference.maximum); age $($metric.reference.ageSeconds)s"
        } else { 'unavailable' }
        $range = if ($null -ne $metric.range) { $metric.range } else { 'unavailable' }
        $headroom = if ($metric.Contains('headroom')) { "$($metric.headroom) (limit $($metric.limit))" } else { 'unavailable' }
        $label = [Net.WebUtility]::HtmlEncode($name).Replace('|', '&#124;')
        $reason = [Net.WebUtility]::HtmlEncode($metric.reason).Replace('|', '&#124;')
        $lines += "| $label | $($metric.health) | $($metric.median) | $range | $reference | $headroom | $reason |"
    }
    if ($Preview.practicalFloors) {
        $lines += 'Selected serious-relative gates also require BOTH median increase >=20% and >=100ms product latency or >=30s whole execution, inclusive. Missing/zero reference median cannot establish percentage significance.'
    }
    return $lines -join "`n"
}
