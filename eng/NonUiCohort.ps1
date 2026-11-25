. "$PSScriptRoot\PerformanceSuite.ps1"
. "$PSScriptRoot\FilesystemHealth.ps1"

$NonUiCohortPhases = @('fixture', 'fixture-export', 'load', 'navigation-100', 'profile-100', 'search-current',
    'search-history', 'favorite-save', 'note-save', 'profile-save', 'validated-preview', 'validated-apply',
    'reopen', 'reopened-projections', 'latest-replay', 'initial-replay-preview', 'initial-replay-apply',
    'unchanged-refresh', 'validated-delivery', 'reopen-after-delivery', 'resume-unpublished')

function Read-NonUiCohortSample($Sample, [string]$Directory, [string]$Revision, [string]$CohortId,
    [string]$RunId, [string]$RunAttempt) {
    $report = Get-Content -LiteralPath (Join-Path $Directory 'result.json') -Raw |
        ConvertFrom-Json -AsHashtable -DateKind String -Depth 20
    if ($report.schemaVersion -ne 1 -or $report.suite -cne 'non-ui-performance' -or
        $report.revision -cne $Revision -or $report.expectedRevision -cne $Revision -or
        $report.cohortId -cne $CohortId -or $report.sample -ne $Sample.number -or
        $report.runId -cne $RunId -or $report.runAttempt -cne $RunAttempt -or
        $report.invocationId -cnotmatch '^[0-9a-f]{32}$' -or
        $report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$' -or
        $report.filter -cne $PerformanceFilter -or @($report.requiredTests).Count -ne 1 -or
        $report.requiredTests[0] -cne $PerformanceTests[0] -or $report.ceilingSeconds -ne 660 -or
        $report.observationSeconds -ne 660 -or $report.diagnosticOnly -isnot [bool] -or $report.diagnosticOnly -or
        $report.trackedChanges -isnot [bool] -or $report.untrackedChanges -isnot [bool] -or
        $report.trackedChanges -or $report.untrackedChanges) {
        throw 'Performance result is unbound, substituted, dirty or uses a different selection/policy.'
    }
    Assert-HealthNumber $report.executionSeconds 'executionSeconds'
    Assert-HealthDate $report.startedAt 'startedAt'
    if ($null -ne $report.completedAt) {
        Assert-HealthDate $report.completedAt 'completedAt'
        if ([DateTimeOffset]$report.completedAt -lt [DateTimeOffset]$report.startedAt) { throw 'Performance clocks are reversed.' }
    }
    $Sample.invocationId = $report.invocationId
    $Sample.runnerOutcome = $report.outcome
    $Sample.configuration = $report.configuration
    $Sample.measurements = [ordered]@{ executionSeconds = $report.executionSeconds; phasesMs = @{}; profile = $null
        steps = $report.steps; headroomSeconds = 660 - $report.executionSeconds }
    try {
        $Sample.seriesKey = Get-HealthSeriesKey @{
            policyVersion = 'non-ui-original-v1'; selector = $PerformanceFilter; configuration = $report.configuration
        }
    } catch { $Sample.diagnostics += "Comparison configuration unavailable: $($_.Exception.Message)" }
    $names = @('restore', 'test')
    $steps = @($report.steps)
    if ($steps.Count -gt 2) { throw 'Unexpected non-UI command selection.' }
    for ($index = 0; $index -lt $steps.Count; $index++) {
        Assert-HealthNumber $steps[$index].seconds $steps[$index].name
        Assert-HealthNumber $steps[$index].exitCode 'exitCode' -AllowNegative
        if ($steps[$index].name -cne $names[$index]) { throw 'Non-UI command order differs from the original workload.' }
    }
    $runDirectory = Join-Path $Directory $report.runDirectory
    $testsError = $null
    try {
        $Sample.tests = Read-FilesystemTests $runDirectory
        Assert-PerformanceResults $runDirectory
    } catch { $testsError = $_; $Sample.diagnostics += "Required TRX rejected: $($_.Exception.Message)" }
    $timingFile = Join-Path $runDirectory 'scale-measurements.log'
    if (Test-Path -LiteralPath $timingFile -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $timingFile) {
            if ($line -cmatch '^SCALE END (?<name>[^;]+); outcome=(?<outcome>Passed|Failed); ms=(?<ms>[0-9]+(?:\.[0-9]+)?);') {
                $name = $Matches.name
                if ($name -cin $NonUiCohortPhases) {
                    if ($Sample.measurements.phasesMs.ContainsKey($name)) { throw 'Duplicate measured non-UI phase.' }
                    $Sample.measurements.phasesMs[$name] = @{
                        value = [double]::Parse($Matches.ms, [Globalization.CultureInfo]::InvariantCulture)
                        outcome = $Matches.outcome
                    }
                }
            } elseif ($line.StartsWith('SCALE ORDINARY-PROFILE ', [StringComparison]::Ordinal)) {
                if ($null -ne $Sample.measurements.profile) { throw 'Duplicate ordinary-profile measurement.' }
                $profile = $line.Substring('SCALE ORDINARY-PROFILE '.Length) | ConvertFrom-Json -AsHashtable -Depth 10
                if (@($profile.samplesMs).Count -ne 21 -or $profile.budgetMs -ne 1000 -or
                    $profile.percentile -cne 'Nearest rank: sorted sample at ceil(0.95 * count), one based.') {
                    throw 'Original profile samples, percentile or hard limit changed.'
                }
                foreach ($value in $profile.samplesMs) { Assert-HealthNumber $value 'profile sample' }
                Assert-HealthNumber $profile.p95Ms 'profile p95'
                if ($profile.p95Ms -ne @($profile.samplesMs | Sort-Object)[19]) { throw 'Profile p95 disagrees with all 21 raw samples.' }
                $Sample.measurements.profile = $profile
            }
        }
    }
    $complete = $report.phase -ceq 'finished' -and $null -ne $report.completedAt -and
        $report.outcome -ceq 'success' -and $report.observedOutcome -ceq 'success' -and
        $steps.Count -eq 2 -and !@($steps | Where-Object { $_.exitCode -ne 0 }).Count -and
        $null -eq $testsError -and $Sample.measurements.phasesMs.Count -eq $NonUiCohortPhases.Count -and
        !@($Sample.measurements.phasesMs.Values | Where-Object { $_.outcome -cne 'Passed' }).Count -and
        $null -ne $Sample.measurements.profile
    if ($report.executionSeconds -gt 660 -or
        ($null -ne $Sample.measurements.profile -and $Sample.measurements.profile.p95Ms -gt 1000)) {
        $Sample.validation = 'failure'; $Sample.diagnostics += 'Original per-execution hard acceptance limit exceeded.'
    } elseif ($report.outcome -ceq 'failure' -or ($null -ne $Sample.tests -and $Sample.tests.failed -gt 0)) {
        $Sample.validation = 'failure'
    } elseif (!$complete) {
        $Sample.validation = 'incomplete'; $Sample.diagnostics += 'Required complete selection/measurements are missing; no acceptance is established.'
    } else { $Sample.validation = 'success' }
}

function Get-NonUiCohortMetrics($Samples) {
    $values = @{}
    foreach ($sample in $Samples) {
        if ($null -eq $sample.measurements) { continue }
        foreach ($metric in @('executionSeconds')) {
            if (!$values.ContainsKey($metric)) { $values[$metric] = @() }
            $values[$metric] += $sample.measurements[$metric]
        }
        foreach ($step in $sample.measurements.steps) {
            $key = "command-$($step.name)-seconds"
            if (!$values.ContainsKey($key)) { $values[$key] = @() }
            $values[$key] += $step.seconds
        }
        foreach ($name in $sample.measurements.phasesMs.Keys) {
            $key = "phase-$name-ms"
            if (!$values.ContainsKey($key)) { $values[$key] = @() }
            $values[$key] += $sample.measurements.phasesMs[$name].value
        }
        if ($null -ne $sample.measurements.profile) {
            if (!$values.ContainsKey('profile-p95-ms')) { $values['profile-p95-ms'] = @() }
            $values['profile-p95-ms'] += $sample.measurements.profile.p95Ms
        }
    }
    $metrics = @{}
    foreach ($name in $values.Keys) {
        $ordered = @($values[$name] | Sort-Object)
        $middle = [int][Math]::Floor($ordered.Count / 2)
        $median = if ($ordered.Count % 2) { $ordered[$middle] } else { ($ordered[$middle - 1] + $ordered[$middle]) / 2 }
        $metrics[$name] = @{
            count = $ordered.Count; unavailable = @($Samples).Count - $ordered.Count; median = $median
            minimum = $ordered[0]; maximum = $ordered[-1]; range = $ordered[-1] - $ordered[0]
            scope = 'all measured attempts, including failures; descriptive range, not calibrated noise'
        }
    }
    return $metrics
}

function Assert-NonUiCohortRecord($Record, [string]$Path) {
    if ($Record.schemaVersion -ne 1 -or $Record.recordType -cne 'performance-cohort' -or
        $Record.repository -cne $FilesystemHealthRepository -or $Record.revision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.cohortRunId -cnotmatch '^[1-9][0-9]*$' -or $Record.cohortRunAttempt -cnotmatch '^[1-9][0-9]*$' -or
        $Record.cohortId -cne "$($Record.revision)-$($Record.cohortRunId)-$($Record.cohortRunAttempt)" -or
        $Path -cne "performance-cohorts/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt).json" -or
        $Record.mode -cne 'observation' -or $Record.promotion -cne 'disabled' -or
        $Record.richScaleAcceptance -cne 'not-executed' -or
        $Record.policyVersion -cnotin @('non-ui-cohort-observation-v1', 'TEST-non-ui-range-v1') -or
        $Record.authority -cnotin @('trusted-main-push', 'pilot-observation')) { throw 'Invalid non-UI cohort schema/provenance.' }
    if ($Record.authority -ceq 'trusted-main-push' -and $Record.ciRevision -cne $Record.revision) {
        throw 'Authoritative cohort candidate lacks its own successful required main CI.'
    }
    if (@($Record.samples).Count -notin @(3, 5)) { throw 'Cohort must retain exactly three or five executions.' }
    Assert-HealthDate $Record.observedAt 'observedAt'
    if ($Record.controllerRevision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.ciRunId -cnotmatch '^[1-9][0-9]*$' -or $Record.ciRunAttempt -cnotmatch '^[1-9][0-9]*$') {
        throw 'Missing exact controller/required CI provenance.'
    }
    $seen = @{}
    for ($index = 0; $index -lt $Record.samples.Count; $index++) {
        $sample = $Record.samples[$index]
        Assert-HealthNumber $sample.number 'sample number'
        if ($sample.number -ne $index + 1 -or
            $sample.validation -cnotin @('success', 'failure', 'incomplete', 'cancelled', 'skipped')) { throw 'Invalid cohort sample order/outcome.' }
        foreach ($field in @('jobId', 'runnerId', 'invocationId')) {
            if ($null -ne $sample[$field]) {
                $key = "$field-$($sample[$field])"
                if ($seen.ContainsKey($key) -and $sample.validation -ceq 'success') { throw "Independent cohort executions reused $field." }
                $seen[$key] = $true
            } elseif ($sample.validation -ceq 'success') { throw "Complete execution lacks independent $field." }
        }
        if ($null -ne $sample.measurements) {
            Assert-HealthNumber $sample.measurements.executionSeconds 'executionSeconds'
            if ($sample.measurements.headroomSeconds -ne 660 - $sample.measurements.executionSeconds) { throw 'Cohort headroom differs from the original policy.' }
            foreach ($step in $sample.measurements.steps) { Assert-HealthNumber $step.seconds 'command seconds' }
            foreach ($phase in $sample.measurements.phasesMs.Values) { Assert-HealthNumber $phase.value 'phase ms' }
            if ($null -ne $sample.measurements.profile) {
                foreach ($value in $sample.measurements.profile.samplesMs) { Assert-HealthNumber $value 'profile sample' }
                Assert-HealthNumber $sample.measurements.profile.p95Ms 'profile p95'
                if (@($sample.measurements.profile.samplesMs).Count -ne 21 -or
                    $sample.measurements.profile.p95Ms -ne @($sample.measurements.profile.samplesMs | Sort-Object)[19] -or
                    $sample.measurements.profile.budgetMs -ne 1000) { throw 'Historical profile policy/raw percentile changed.' }
            }
        } elseif ($sample.validation -ceq 'success') { throw 'Successful execution lacks complete measured evidence.' }
        if ($sample.validation -ceq 'success' -and ($sample.measurements.executionSeconds -gt 660 -or
            $null -eq $sample.measurements.profile -or $sample.measurements.profile.p95Ms -gt 1000 -or
            $sample.measurements.phasesMs.Count -ne $NonUiCohortPhases.Count -or
            $null -eq $sample.tests -or $sample.tests.total -ne 1 -or $sample.tests.passed -ne 1 -or
            $sample.tests.failed -ne 0 -or $sample.tests.notExecuted -ne 0)) { throw 'Historical successful execution lacks original complete acceptance.' }
        if ($sample.evidence.state -cnotin @('missing', 'available', 'unavailable', 'expired', 'rejected') -or
            $sample.evidence.url -cne "https://github.com/$FilesystemHealthRepository/actions/runs/$($Record.cohortRunId)/attempts/$($Record.cohortRunAttempt)") {
            throw 'Invalid retained sample evidence.'
        }
        if ($null -ne $sample.seriesKey) {
            if ($sample.seriesKey -cne (Get-HealthSeriesKey @{
                policyVersion = 'non-ui-original-v1'; selector = $PerformanceFilter; configuration = $sample.configuration
            })) { throw 'Historical configuration/series key differs.' }
        }
    }
    $expected = Get-NonUiCohortMetrics $Record.samples
    if ($expected.Count -ne $Record.metrics.Count) { throw 'Cohort metric selection differs from the individual attempts.' }
    foreach ($name in $expected.Keys) {
        foreach ($field in @('count', 'unavailable', 'median', 'minimum', 'maximum', 'range')) {
            Assert-HealthNumber $Record.metrics[$name][$field] "$name.$field"
            if ($Record.metrics[$name][$field] -ne $expected[$name][$field]) { throw 'Cohort summary excludes or changes individual measurements.' }
        }
        foreach ($outcome in @('success', 'failure', 'incomplete', 'cancelled', 'skipped')) {
            if ($Record.reliability[$outcome] -ne @($Record.samples | Where-Object { $_.validation -ceq $outcome }).Count) {
                throw 'Cohort reliability excludes original attempts.'
            }
        }
        if ($Record.reliability.attempts -ne $Record.samples.Count -or
            ($Record.validation -ceq 'success' -and $Record.reliability.success -ne $Record.samples.Count)) {
            throw 'Cohort validation averages away an original failed/missing attempt.'
        }
    }
}

function Get-NonUiCohortRecordTypes {
    return @{
        'performance-cohort' = @{
            PathPattern = '^performance-cohorts/[0-9a-f]{40}/[1-9][0-9]*/[1-9][0-9]*\.json$'
            GetPath = { param($record) "performance-cohorts/$($record.revision)/$($record.cohortRunId)/$($record.cohortRunAttempt).json" }
            Validate = {
                param($record, $path)
                if ($record.policyVersion -cne 'non-ui-cohort-observation-v1' -or $record.authority -cne 'trusted-main-push') {
                    throw 'Live history cannot accept TEST-policy or manual pilot cohorts.'
                }
                Assert-NonUiCohortRecord $record $path
            }
        }
    }
}

function Set-NonUiCohortComparison($Record, $History) {
    $Record.historySnapshot = $History.tip
    $keys = @($Record.samples | ForEach-Object { $_.seriesKey } | Select-Object -Unique)
    if ($keys.Count -ne 1 -or $null -eq $keys[0] -or $Record.validation -cne 'success') { return }
    $prior = @($History.records | Where-Object {
        $_.ContainsKey('recordType') -and $_.recordType -ceq 'performance-cohort' -and
        $_.revision -cne $Record.revision -and $_.validation -ceq 'success' -and
        @($_.samples | Where-Object { $_.seriesKey -cne $keys[0] }).Count -eq 0
    } | Sort-Object @{ Expression = { [DateTimeOffset]$_.observedAt }; Descending = $true })
    if (!$prior.Count) { return }
    $reference = $prior[0]
    $Record.reference = @{
        kind = 'prior-compatible-non-ui-cohort-observation'; revision = $reference.revision
        cohortId = $reference.cohortId; observedAt = $reference.observedAt
        ageSeconds = ([DateTimeOffset]$Record.observedAt - [DateTimeOffset]$reference.observedAt).TotalSeconds
        medianSeconds = $reference.metrics.executionSeconds.median
        deltaSeconds = $Record.metrics.executionSeconds.median - $reference.metrics.executionSeconds.median
        evidenceUrl = $reference.samples[0].evidence.url
        evidenceState = if (@($reference.samples | Where-Object {
            $null -eq $_.evidence.expiresAt -or [DateTimeOffset]$_.evidence.expiresAt -le [DateTimeOffset]$Record.observedAt
        }).Count) { 'expired-or-unavailable' } else { 'retained-metadata-only' }
    }
    $Record.timingHealth = 'observation'
}

function Format-NonUiCohort($Record, [string]$Publication) {
    $lines = @(
        '### Independent non-UI performance cohort'
        ''
        "Candidate: ``$($Record.revision)``; cohort ``$($Record.cohortId)``."
        "Validation: **$($Record.validation)**; timing: **$($Record.timingHealth)**; eligibility: **$($Record.eligibility)**."
        "Policy: ``$($Record.policyVersion)``. Active-reference promotion: **disabled**. Rich-scale acceptance: **not executed**."
        'Original 660-second setup/workload/cleanup budget and 1,000-ms profile p95 hard limit remain enforced.'
        ''
        '| Sample | Original job / runner outcome | Validation | Whole seconds | Evidence |'
        '|---|---|---|---:|---|'
        foreach ($sample in $Record.samples) {
            $seconds = if ($null -eq $sample.measurements) { 'unavailable' } else { $sample.measurements.executionSeconds }
            "| $($sample.number) | $($sample.jobOutcome) / $($sample.runnerOutcome) | $($sample.validation) | $seconds | [$($sample.evidence.state)]($($sample.evidence.url)) |"
        }
        ''
        foreach ($sample in $Record.samples) {
            foreach ($detail in $sample.diagnostics) { "Sample $($sample.number): $([Net.WebUtility]::HtmlEncode($detail))" }
        }
        '| Metric | Median | Minimum | Maximum | Range | Measured / unavailable attempts |'
        '|---|---:|---:|---:|---:|---|'
        foreach ($name in @($Record.metrics.Keys | Sort-Object)) {
            $metric = $Record.metrics[$name]
            "| $name | $($metric.median) | $($metric.minimum) | $($metric.maximum) | $($metric.range) | $($metric.count) / $($metric.unavailable) |"
        }
        ''
        "All attempts: $($Record.reliability.attempts); success $($Record.reliability.success); failure $($Record.reliability.failure); incomplete $($Record.reliability.incomplete); cancelled $($Record.reliability.cancelled); skipped $($Record.reliability.skipped)."
        'Medians include every available measurement, including failed attempts; unavailable evidence is not zero. No cause or calibrated noise threshold is inferred.'
        'Live eligibility/noise calibration is pending #130. This is not a canonical reference or full performance/scale acceptance.'
        if ($null -ne $Record.reference) {
            "Reference: prior compatible non-UI observation ``$($Record.reference.revision)`` / ``$($Record.reference.cohortId)``; age $($Record.reference.ageSeconds) s; median $($Record.reference.medianSeconds) s; change $($Record.reference.deltaSeconds) s. Raw evidence: [$($Record.reference.evidenceState)]($($Record.reference.evidenceUrl))."
        } else { 'Comparison reference: unavailable (no complete compatible non-self cohort observation).' }
        foreach ($detail in $Record.historyDiagnostics) { "History detail: $([Net.WebUtility]::HtmlEncode($detail))" }
        'Collection never waits for self-baselining, blocks or cancels release, or substitutes historical data for fresh candidate verification.'
        "History publication: $([Net.WebUtility]::HtmlEncode($Publication)). Raw Actions evidence retention: 30 days; compact numbers survive expiry."
    )
    return $lines -join "`n"
}
