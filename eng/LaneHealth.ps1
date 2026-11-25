. "$PSScriptRoot\FilesystemHealth.ps1"
. "$PSScriptRoot\DesktopResults.ps1"
. "$PSScriptRoot\PerformanceSuite.ps1"
. "$PSScriptRoot\UiScaleResults.ps1"

function Get-CoreHealthSteps {
    return @('restore', 'test', 'CiGate.Tests.ps1', 'CiRunner.Tests.ps1', 'FilesystemHealth.Tests.ps1',
        'LaneHealth.Tests.ps1', 'ImplementationBaseline.Tests.ps1', 'DocumentationHandoff.Tests.ps1',
        'DraftDiscard.Tests.ps1', 'Performance.Tests.ps1', 'DesktopResults.Tests.ps1', 'UiScale.Tests.ps1',
        'Release.Tests.ps1', 'Release.Gates.Tests.ps1', 'Release.Publication.Tests.ps1',
        'Release.Renewal.Tests.ps1', 'Release.Workflow.Tests.ps1', 'Release.Payload.Tests.ps1',
        'Update.TrustLifetime.Tests.ps1', 'ApplicationIcon.Tests.ps1')
}

function Get-LaneHealthRecordTypes {
    return @{
        'lane-health' = @{
            PathPattern = '^lanes/[1-9][0-9]*/[1-9][0-9]*/[a-z][a-z0-9-]*\.json$'
            GetPath = { param($record) "lanes/$($record.runId)/$($record.runAttempt)/$($record.lane).json" }
            Validate = { param($record, $path) Assert-LaneHealthRecord $record $path }
        }
    }
}

function Assert-LaneHealthRecord($Record, [string]$Path) {
    if ($Record.schemaVersion -ne 2 -or $Record.recordType -cne 'lane-health' -or
        $Record.repository -cne $FilesystemHealthRepository -or $Record.revision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.runId -cnotmatch '^[1-9][0-9]*$' -or $Record.runAttempt -cnotmatch '^[1-9][0-9]*$' -or
        $Record.authority -cne 'trusted-main-push' -or $Record.sourceBranch -cne 'main' -or
        $Record.sourceRepository -cne $FilesystemHealthRepository -or
        $Record.sourceEvent -cnotin @('push', 'schedule', 'workflow_run') -or
        $Record.workflowPath -cnotin @('.github/workflows/ci.yml', '.github/workflows/scale-verification.yml',
            '.github/workflows/installer-verification.yml', '.github/workflows/release.yml',
            '.github/workflows/release-verification.yml') -or
        $Record.validation -cnotin @('success', 'failure', 'incomplete', 'cancelled', 'skipped') -or
        $Record.timingHealth -cnotin @('observation', 'unbaselined', 'inconclusive') -or
        $Record.policyVersion -cne 'lane-observation-v2' -or
        $Path -cne "lanes/$($Record.runId)/$($Record.runAttempt)/$($Record.lane).json") {
        throw 'Invalid durable lane observation schema/provenance.'
    }
    Assert-HealthDate $Record.observedAt 'observedAt'
    foreach ($phase in @($Record.hosted.steps)) {
        if ($null -ne $phase.seconds) { Assert-HealthNumber $phase.seconds 'hosted.phase.seconds' }
    }
    if ($null -ne $Record.hosted.seconds) { Assert-HealthNumber $Record.hosted.seconds 'hosted.seconds' }
    if ($null -ne $Record.measurements) {
        Assert-HealthNumber $Record.measurements.executionSeconds 'executionSeconds'
        if ($null -ne $Record.measurements.headroomSeconds) {
            Assert-HealthNumber $Record.measurements.headroomSeconds 'headroomSeconds' -AllowNegative
            Assert-HealthNumber $Record.measurements.ceilingSeconds 'ceilingSeconds'
            if ($Record.measurements.headroomSeconds -ne
                $Record.measurements.ceilingSeconds - $Record.measurements.executionSeconds) { throw 'Lane headroom disagrees.' }
        }
        foreach ($step in @($Record.measurements.steps)) {
            if ($null -ne $step.seconds) { Assert-HealthNumber $step.seconds 'phase.seconds' }
        }
        foreach ($metric in @($Record.measurements.metrics)) {
            Assert-HealthNumber $metric.value $metric.name
            foreach ($sample in @($metric.samples)) { Assert-HealthNumber $sample $metric.name }
        }
    }
    if ($Record.validation -ceq 'success' -and $Record.verificationScope -ceq 'runner' -and $null -eq $Record.measurements) {
        throw 'Successful runner validation lacks complete evidence.'
    }
    if ($Record.evidence.state -cnotin @('available', 'missing', 'expired', 'unavailable', 'rejected') -or
        $Record.evidence.url -cne "https://github.com/$FilesystemHealthRepository/actions/runs/$($Record.runId)/attempts/$($Record.runAttempt)") {
        throw 'Invalid lane evidence link/state.'
    }
    if ($null -ne $Record.evidence.expiresAt) { Assert-HealthDate $Record.evidence.expiresAt 'expiresAt' }
    if ($null -ne $Record.reference) {
        if ($Record.reference.revision -ceq $Record.revision -or $Record.reference.revision -cnotmatch '^[0-9a-f]{40}$') {
            throw 'Invalid self-referencing lane comparison.'
        }
        Assert-HealthNumber $Record.reference.executionSeconds 'reference.seconds'
        Assert-HealthNumber $Record.reference.deltaSeconds 'reference.delta' -AllowNegative
        if ($Record.reference.deltaSeconds -ne $Record.measurements.executionSeconds - $Record.reference.executionSeconds) {
            throw 'Lane comparison inputs disagree.'
        }
    }
}

function Get-HostedHealth($Job) {
    $steps = @()
    foreach ($step in @($Job.steps)) {
        $seconds = $null
        $valid = $false
        if ($step.started_at -and $step.completed_at) {
            $valid = [DateTimeOffset]$step.completed_at -ge [DateTimeOffset]$step.started_at
            if ($valid -and $step.conclusion -cne 'skipped') {
                $seconds = ([DateTimeOffset]$step.completed_at - [DateTimeOffset]$step.started_at).TotalSeconds
            }
        }
        $steps += @{ name = $step.name; outcome = $step.conclusion; startedAt = $step.started_at
            finishedAt = $step.completed_at; clockValid = $valid; seconds = $seconds }
    }
    $seconds = $null
    $valid = $false
    if ($Job.started_at -and $Job.completed_at) {
        $valid = [DateTimeOffset]$Job.completed_at -ge [DateTimeOffset]$Job.started_at
        if ($valid -and $Job.conclusion -cne 'skipped') {
            $seconds = ([DateTimeOffset]$Job.completed_at - [DateTimeOffset]$Job.started_at).TotalSeconds
        }
    }
    return @{ startedAt = $Job.started_at; finishedAt = $Job.completed_at; clockValid = $valid
        seconds = $seconds; steps = $steps; scope = 'API job wall time including setup and post/upload, not product latency' }
}

function Get-CoreHealthMeasurement([string]$Directory, $Run) {
    $execution = Get-Content (Join-Path $Directory 'execution.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    if ($execution.schemaVersion -ne 1 -or $execution.lane -cne 'core' -or
        $execution.revision -cne $Run.head_sha -or $execution.runId -cne [string]$Run.id -or
        $execution.runAttempt -cne [string]$Run.run_attempt -or $execution.selector -cne 'all' -or
        $execution.policyVersion -cne 'core-original-v1' -or $execution.ceilingSeconds -ne 300 -or
        $execution.complete -isnot [bool]) { throw 'Unbound core policy/selection.' }
    Assert-HealthNumber $execution.executionSeconds 'executionSeconds'
    Assert-HealthDate $execution.startedAt 'startedAt'
    Assert-HealthDate $execution.finishedAt 'finishedAt'
    $validation = if ($execution.outcome -ceq 'failure') { 'failure' } else { 'incomplete' }
    $tests = Read-FilesystemTests $Directory
    $expected = @(Get-CoreHealthSteps)
    $steps = @($execution.steps)
    foreach ($step in $steps) { Assert-HealthNumber $step.seconds $step.name }
    if ($execution.complete -and $execution.outcome -ceq 'success' -and
        ($steps.name -join "`n") -ceq ($expected -join "`n") -and
        !@($steps | Where-Object { $_.status -cne 'Passed' }).Count -and
        $tests.failed -eq 0 -and $tests.notExecuted -eq 0 -and $tests.summary -ceq 'Completed') { $validation = 'success' }
    if ($tests.failed -gt 0) { $validation = 'failure' }
    return @{ validation = $validation; tests = $tests; configuration = $execution.configuration
        measuredAt = $execution.finishedAt; runnerOutcome = $execution.outcome; runnerComplete = $execution.complete
        measurements = @{ executionSeconds = $execution.executionSeconds; ceilingSeconds = 300
            headroomSeconds = 300 - $execution.executionSeconds; steps = $steps; metrics = @() }
        selector = 'all'; originalPolicy = 'core-original-v1' }
}

function Get-LaneHealthSeriesKey($Measurement) {
    $execution = @{ configuration = $Measurement.configuration; policyVersion = $Measurement.originalPolicy
        selector = $Measurement.selector }
    return Get-HealthSeriesKey $execution
}

function Set-LaneHealthComparison($Record, $History) {
    $attempts = @($History.records | Where-Object { $_.lane -ceq $Record.lane -and
        $_.workflowPath -ceq $Record.workflowPath -and !($_.runId -ceq $Record.runId -and $_.runAttempt -ceq $Record.runAttempt) })
    $counts = [ordered]@{ attempts = $attempts.Count + 1 }
    foreach ($outcome in @('success', 'failure', 'incomplete', 'cancelled', 'skipped')) {
        $counts[$outcome] = @($attempts + @($Record) | Where-Object { $_.validation -ceq $outcome }).Count
    }
    $counts.failureRatePercent = 100 * $counts.failure / $counts.attempts
    $Record.reliability = $counts
    $Record.recent = @($attempts | Sort-Object observedAt -Descending | Select-Object -First 10 |
        ForEach-Object { @{ revision = $_.revision; runId = $_.runId; runAttempt = $_.runAttempt
            validation = $_.validation; executionSeconds = if ($_.measurements) { $_.measurements.executionSeconds } else { $null } } })
    if ($null -eq $Record.seriesKey -or $null -eq $Record.measurements) { return }
    $compatible = @($attempts | Where-Object { $_.seriesKey -ceq $Record.seriesKey -and
        $_.validation -ceq 'success' -and $_.revision -cne $Record.revision -and
        $null -ne $_.measurements -and [DateTimeOffset]$_.measuredAt -le [DateTimeOffset]$Record.observedAt } |
        Sort-Object measuredAt -Descending)
    if (!$compatible.Count) {
        $Record.timingHealth = if ($attempts.Count) { 'inconclusive' } else { 'unbaselined' }
        return
    }
    $previous = $compatible[0]
    $Record.reference = @{ revision = $previous.revision; runId = $previous.runId; runAttempt = $previous.runAttempt
        kind = 'prior-compatible-observation'; measuredAt = $previous.measuredAt
        executionSeconds = $previous.measurements.executionSeconds; steps = $previous.measurements.steps
        deltaSeconds = $Record.measurements.executionSeconds - $previous.measurements.executionSeconds
        ageSeconds = ([DateTimeOffset]$Record.observedAt - [DateTimeOffset]$previous.measuredAt).TotalSeconds
        evidenceState = if ($previous.evidence.expiresAt -and
            [DateTimeOffset]$previous.evidence.expiresAt -le [DateTimeOffset]$Record.observedAt) { 'expired' } else { $previous.evidence.state }
        evidenceUrl = $previous.evidence.url }
    $Record.timingHealth = 'observation'
    if ($compatible.Count -gt 1) {
        $range = $compatible | ForEach-Object { $_.measurements.executionSeconds } | Measure-Object -Minimum -Maximum
        $Record.variability = @{ count = $compatible.Count; minimumSeconds = $range.Minimum; maximumSeconds = $range.Maximum }
    }
}

function Format-LaneHealth($Records, [string]$Publication) {
    $lines = @('## Validation lane observations', '',
        'Timing is observation only; numerical bands, eligibility and new gates remain uncalibrated. Original job/gate outcomes are unchanged.',
        'Historical records never replace fresh exact-candidate release acceptance.', '')
    foreach ($record in $Records) {
        $lines += @("### $([Net.WebUtility]::HtmlEncode($record.jobName))",
            "Validation: **$($record.validation)**; timing: **$($record.timingHealth)**; original job: **$($record.jobOutcome)**.",
            "Candidate: ``$($record.revision)``; controller: ``$($record.controllerRevision)``; run/attempt: $($record.runId)/$($record.runAttempt).",
            "Policy: ``$($record.originalPolicy)``; scope: $($record.verificationScope).",
            '| Measurement / phase | Value |', '|---|---:|')
        if ($record.measurements) {
            $lines += "| Whole runner execution (setup/cleanup included) | $($record.measurements.executionSeconds) s |"
            foreach ($step in $record.measurements.steps) { $lines += "| $([Net.WebUtility]::HtmlEncode($step.name)) | $($step.seconds) s |" }
            foreach ($metric in $record.measurements.metrics) { $lines += "| $([Net.WebUtility]::HtmlEncode($metric.name)) | $($metric.value) $($metric.unit) |" }
            $headroom = if ($null -ne $record.measurements.headroomSeconds) { "$($record.measurements.headroomSeconds) s (original budget, not a warning band)" } else { 'unavailable' }
            $lines += "Headroom: $headroom."
        } else { $lines += '| Runner measurements | unavailable, not zero |' }
        $wall = if ($null -ne $record.hosted.seconds) { "$($record.hosted.seconds) s" } else { 'unavailable (skipped or invalid/missing clocks)' }
        $lines += "Hosted job wall time: $wall; includes setup/post/upload, not a runner or product latency."
        foreach ($step in $record.hosted.steps) {
            $value = if ($null -ne $step.seconds) { "$($step.seconds) s" } else { 'unavailable' }
            $lines += "Hosted phase $([Net.WebUtility]::HtmlEncode($step.name)): $($step.outcome), $value."
        }
        if ($record.reference) {
            $lines += "Reference: ``$($record.reference.revision)``; age $($record.reference.ageSeconds) s; change $($record.reference.deltaSeconds) s; raw evidence [$($record.reference.evidenceState)]($($record.reference.evidenceUrl))."
        } else { $lines += 'Reference: unavailable (no compatible non-self observation); no release waiting dependency.' }
        if ($record.variability) {
            $lines += "Descriptive cross-revision range: $($record.variability.minimumSeconds)-$($record.variability.maximumSeconds) s, $($record.variability.count) observations; not calibrated noise."
        } else { $lines += 'Variability: unavailable.' }
        if ($record.reliability) {
            $lines += "All attempts: $($record.reliability.attempts); success $($record.reliability.success), failure $($record.reliability.failure), incomplete $($record.reliability.incomplete), cancelled $($record.reliability.cancelled), skipped $($record.reliability.skipped); failure rate $($record.reliability.failureRatePercent)%."
            foreach ($recent in $record.recent) { $lines += "Recent $($recent.runId)/$($recent.runAttempt): $($recent.validation), revision ``$($recent.revision)``." }
        }
        foreach ($detail in $record.diagnostics) { $lines += [Net.WebUtility]::HtmlEncode($detail) }
        $lines += @("Evidence: [$($record.evidence.state)]($($record.evidence.url)); expiry $($record.evidence.expiresAt).",
            'Attribution: unknown unless the original evidence explicitly establishes it.', '')
    }
    $lines += "History publication: $([Net.WebUtility]::HtmlEncode($Publication))."
    return $lines -join "`n"
}
