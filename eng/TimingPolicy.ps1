function Set-TimingPolicyPreview($Record, $History) {
    if ($Record.recordType -cne 'performance-cohort') {
        $metrics = @{}
        if ($Record.measurements) {
            $metrics.executionSeconds = @{
                health = if ($Record.validation -ceq 'success') { 'unbaselined' } else { 'inconclusive' }
                median = $Record.measurements.executionSeconds; range = $null; count = 1; reference = $null
                reason = 'A single lane execution is not an independent three-to-five reference cohort.'
            }
            foreach ($metric in $Record.measurements.metrics) {
                $metrics[$metric.name] = @{
                    health = 'unbaselined'; median = $metric.value; range = $null; count = 1; reference = $null
                    reason = 'No independent compatible cohort for this metric.'
                }
                if ($metric.Contains('budgetMs') -and $metric.value -gt $metric.budgetMs) {
                    $metrics[$metric.name].health = 'red'
                    $metrics[$metric.name].reason = 'Original hard product limit violated; current validation remains enforced.'
                }
            }
        }
        $Record.policyPreview = @{
            version = 'ci-timing-preview-v1'; mode = 'observation'; approval = 'pending'
            blocking = $false; promotion = 'disabled'; currentValidation = $Record.validation
            historySnapshot = if ($History) { $History.tip } else { $null }
            metrics = $metrics; additionalSamples = @(); eligibility = 'pending'
        }
        return
    }
    $key = Get-TimingCohortKey $Record
    $reference = $null
    if ($History -and $key) {
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
    $serious = $false
    foreach ($name in $Record.metrics.Keys) {
        $metric = $Record.metrics[$name]
        $gateable = $name -cin @('executionSeconds', 'prefix-executionSeconds', 'tail-executionSeconds',
            'profile-p95-ms', 'prefix-profile-p95-ms', 'tail-profile-p95-ms') -or
            $name -cmatch '^prefix-.*-localDurability-p95-ms$'
        $preview = @{
            health = 'unbaselined'; median = $metric.median; range = $metric.range
            count = $metric.count; reference = $null; seriousRegression = $false; lowHeadroom = $false
            relativeRule = if ($gateable) { 'proposed-gate; not activated' } else { 'informational' }
            reason = 'No complete compatible non-self reference cohort; no green comparison.'
        }
        if ($reference -and $reference.metrics.ContainsKey($name)) {
            $prior = $reference.metrics[$name]
            $preview.reference = @{
                revision = $reference.revision; cohortId = $reference.cohortId
                median = $prior.median; minimum = $prior.minimum; maximum = $prior.maximum
                range = $prior.range; count = $prior.count
                ageSeconds = ([DateTimeOffset]$Record.observedAt - [DateTimeOffset]$reference.observedAt).TotalSeconds
                kind = 'proposal-reference-observation; not canonical'
            }
            $completeTiming = $Record.validation -ceq 'success'
            if (!$completeTiming -and $name -ceq 'prefix-executionSeconds' -and $Record.schemaVersion -eq 2) {
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
            } else {
                $variable = $metric.range -gt $prior.range
                $widerRange = $widerRange -or $variable
                $warning = $variable -or $metric.maximum -gt $prior.maximum
                $preview.health = if ($warning) { 'yellow' } else { 'green' }
                $preview.reason = if ($warning) { 'Outside the observed reference envelope or wider observed range; not a proven regression.' }
                    else { 'Within the observed reference envelope and variability; not a calibrated probability.' }
                if ($gateable -and $Record.validation -ceq 'success' -and $Record.samples.Count -eq 5 -and
                    !$variable -and $metric.minimum -gt ($prior.maximum + $prior.range) -and
                    $metric.median - $prior.median -gt $prior.range) {
                    $preview.health = 'red'; $preview.seriousRegression = $true
                    $preview.reason = 'All five complete measurements exceed the reference maximum by more than its full observed range, with no wider candidate range.'
                    $serious = $true
                }
                $inconsistent = $inconsistent -or $variable -or $warning
            }
        }
        $preview.relativeHealth = $preview.health
        $hardLimit = if ($name -ceq 'profile-p95-ms' -or $name -cmatch '^(prefix|tail)-profile-p95-ms$') { 1000 }
            elseif ($name -cmatch '^prefix-.*-localDurability-p95-ms$') { 1050 } else { $null }
        $executionLimit = if ($name -cin @('executionSeconds', 'tail-executionSeconds')) { 660 }
            elseif ($name -ceq 'prefix-executionSeconds') { 900 } else { $null }
        $limit = if ($null -ne $hardLimit) { $hardLimit } else { $executionLimit }
        if ($null -ne $limit) {
            $preview.headroom = $limit - $metric.maximum
            $preview.limit = $limit
            if ($Record.validation -ceq 'success' -and $preview.reference -and
                $preview.headroom -le $preview.reference.range) {
                $preview.lowHeadroom = $true
                if ($preview.health -cne 'red') { $preview.health = 'yellow' }
                $preview.reason = 'Remaining headroom is no larger than one full observed reference range.'
                $inconsistent = $true
            }
        }
        if ($null -ne $hardLimit) {
            $preview.hardLimit = $hardLimit
            $preview.headroom = $hardLimit - $metric.maximum
            if ($metric.maximum -gt $hardLimit) {
                $preview.health = 'red'; $preview.seriousRegression = $false
                $preview.reason = 'An individual original hard product limit is violated; a cohort median cannot erase it.'
            }
            if ($hardLimit -eq 1050) {
                $preview.goal = 1000; $preview.goalMet = $metric.maximum -le 1000
            }
            if ($hardLimit -eq 1050 -and $metric.maximum -gt 1000 -and $metric.maximum -le 1050 -and $preview.health -cne 'red') {
                $preview.health = 'yellow'
                $preview.reason = 'Original1000ms goal missed within temporary1050ms enforcement; relative comparison remains ' + $preview.relativeHealth + '.'
            }
        }
        $metrics[$name] = $preview
    }
    $Record.policyPreview = @{
        version = 'ci-timing-preview-v1'; mode = 'observation'; approval = 'pending'
        blocking = $false; promotion = 'disabled'; currentValidation = $Record.validation
        historySnapshot = if ($History) { $History.tip } else { $null }
        metrics = $metrics; additionalSamples = @(); eligibility = 'pending'
    }
    if ($Record.validation -ceq 'success' -and !$Record.unexpectedExecutions -and $inconsistent -and $Record.samples.Count -eq 3) {
        $Record.policyPreview.additionalSamples = @(4, 5)
        $Record.policyPreview.eligibility = 'extend-proposed; no dispatch authorized'
    }
    if ($serious) { $Record.policyPreview.eligibility = 'rejected-proposed-relative; current gates unchanged' }
    elseif ($Record.samples.Count -eq 5 -and $widerRange) { $Record.policyPreview.eligibility = 'inconclusive' }
    if ($Record.validation -cne 'success') { $Record.policyPreview.eligibility = 'rejected-current-validation' }
}

function Get-TimingCohortKey($Record) {
    $keys = @($Record.samples | ForEach-Object {
        if ($Record.schemaVersion -eq 2) { $_.rich.seriesKey } else { $_.seriesKey }
    } | Select-Object -Unique)
    if ($keys.Count -eq 1 -and $null -ne $keys[0]) { return $keys[0] }
    return $null
}

function Format-TimingPolicyPreview($Preview) {
    $lines = @('', '### Timing policy preview', '',
        "``$($Preview.version)``; OBSERVATION ONLY; approval pending. No new gate or canonical promotion.",
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
    return $lines -join "`n"
}
