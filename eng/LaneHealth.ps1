. "$PSScriptRoot\FilesystemHealth.ps1"
. "$PSScriptRoot\DesktopResults.ps1"
. "$PSScriptRoot\PerformanceSuite.ps1"
. "$PSScriptRoot\UiScaleResults.ps1"
. "$PSScriptRoot\NonUiCohort.ps1"

function Get-CoreHealthSteps {
    return @('restore', 'test', 'CiGate.Tests.ps1', 'CiRunner.Tests.ps1', 'FilesystemHealth.Tests.ps1',
        'LaneHealth.Tests.ps1', 'MixedHealth.Tests.ps1', 'ImplementationBaseline.Tests.ps1', 'DocumentationHandoff.Tests.ps1',
        'DraftDiscard.Tests.ps1', 'Performance.Tests.ps1', 'NonUiCohort.Tests.ps1', 'DesktopResults.Tests.ps1', 'UiScale.Tests.ps1',
        'Release.Tests.ps1', 'Release.Gates.Tests.ps1', 'Release.Publication.Tests.ps1',
        'Release.Renewal.Tests.ps1', 'Release.Workflow.Tests.ps1', 'Release.Payload.Tests.ps1',
        'Update.TrustLifetime.Tests.ps1', 'ApplicationIcon.Tests.ps1')
}

function Get-LaneHealthJobs($Run, $Jobs) {
    $ci = @(
        @('core', 'Core and view models', 'core'),
        @('filesystem', 'Real filesystem persistence', 'filesystem'),
        @('desktop-people', 'Actual Windows desktop smoke (people)', 'desktop'),
        @('desktop-workspace', 'Actual Windows desktop smoke (workspace)', 'desktop'),
        @('high-resolution-reading', 'High-resolution Windows smoke (reading)', 'high-resolution'),
        @('high-resolution-context', 'High-resolution Windows smoke (context)', 'high-resolution'),
        @('ci', 'CI', 'controller'),
        @('optional-scale', 'Optional manual scale verification', 'controller'),
        @('optional-rehearsal', 'Optional product release rehearsal', 'controller')
    )
    $scale = @(
        @('non-ui-performance', 'Thorough non-UI performance', 'non-ui-performance'),
        @('ui-scale-prefix', 'Rich combined normal and held-export saves', 'ui-scale-prefix'),
        @('ui-scale-tail', 'Rich logical Join, delivery, reopen and navigation', 'ui-scale-tail'),
        @('ui-scale', 'Complete scale acceptance union', 'ui-scale')
    )
    $definitions = @()
    switch -CaseSensitive ($Run.path) {
        '.github/workflows/ci.yml' { $definitions = $ci }
        '.github/workflows/scale-verification.yml' { $definitions = $scale }
        '.github/workflows/installer-verification.yml' { $definitions = ,@('installer', 'Isolated Windows installer lifecycle', 'installer') }
        '.github/workflows/release.yml' {
            $definitions = @(@('release-plan', 'plan', 'controller'), @('release-publish', 'publish', 'controller'),
                @('renewal-plan', 'renewal-plan', 'controller'), @('renewal-publish', 'renew-metadata', 'controller')
            )
        }
        '.github/workflows/release-verification.yml' { $definitions = ,@('rehearsal-package', 'package', 'rehearsal') }
        default { throw 'Unsupported observed workflow.' }
    }
    $prefixes = switch -CaseSensitive ($Run.path) {
        '.github/workflows/ci.yml' { @('Optional manual scale verification', 'Optional product release rehearsal / scale') }
        '.github/workflows/release.yml' { @('scale', 'renewal-scale') }
        '.github/workflows/release-verification.yml' { @('scale') }
        default { @() }
    }
    foreach ($prefix in $prefixes) {
        $scope = switch -CaseSensitive ($prefix) {
            'Optional manual scale verification' { 'optional-scale' }
            'Optional product release rehearsal / scale' { 'optional-rehearsal-scale' }
            default { $prefix }
        }
        foreach ($definition in $scale) {
            $name = "$prefix / $($definition[1])"
            if (@($Jobs | Where-Object { $_.name -ceq $name }).Count) {
                $definitions += ,@("$scope-$($definition[0])", $name, $definition[2])
            }
        }
    }
    if ($Run.path -ceq '.github/workflows/ci.yml' -and
        @($Jobs | Where-Object { $_.name -ceq 'Optional product release rehearsal / package' }).Count) {
        $definitions += ,@('optional-rehearsal-package', 'Optional product release rehearsal / package', 'rehearsal')
    }
    $result = @()
    foreach ($definition in $definitions) {
        $matched = @($Jobs | Where-Object { $_.name -ceq $definition[1] })
        if ($matched.Count -gt 1) { throw "Duplicate current-attempt job: $($definition[1])" }
        $job = if ($matched.Count) { $matched[0] } else {
            @{ id = $null; name = $definition[1]; status = 'missing'; conclusion = 'missing'
                steps = @(); started_at = $null; completed_at = $null }
        }
        $pattern = if ($definition[2] -cin @('non-ui-performance', 'ui-scale-prefix', 'ui-scale-tail', 'ui-scale')) {
            "^(diagnostic-)?$($definition[2])-(?<candidate>[0-9a-f]{40})-$($Run.id)-$($Run.run_attempt)$"
        } elseif ($definition[2] -ceq 'installer') { "^isolated-installer-evidence-$($Run.run_attempt)$" }
        elseif ($definition[2] -ceq 'rehearsal') { "^secret-free-release-evidence-(?<candidate>[0-9a-f]{40})-$($Run.id)-$($Run.run_attempt)$" }
        else { "^$($definition[0])-attempt-$($Run.run_attempt)$" }
        $result += @{ lane = $definition[0]; kind = $definition[2]; job = $job; artifactPattern = $pattern }
    }
    foreach ($job in $Jobs) {
        if (@($result | Where-Object { $_.job.name -ceq $job.name }).Count) { continue }
        if ([string]$job.id -cnotmatch '^[1-9][0-9]*$') { throw 'Unmapped job has no stable observed identity.' }
        $result += @{ lane = "unmapped-$($job.id)"; kind = 'controller'; job = $job; artifactPattern = '^never$' }
    }
    return $result
}

function Get-LaneHealthRecordTypes {
    $types = @{
        'lane-health' = @{
            PathPattern = '^lanes/[1-9][0-9]*/[1-9][0-9]*/[a-z][a-z0-9-]*\.json$'
            GetPath = { param($record) "lanes/$($record.runId)/$($record.runAttempt)/$($record.lane).json" }
            Validate = { param($record, $path) Assert-LaneHealthRecord $record $path }
        }
        'lane-observation' = @{
            PathPattern = '^observations/[1-9][0-9]*/[1-9][0-9]*/[a-z][a-z0-9-]*\.json$'
            GetPath = { param($record) "observations/$($record.runId)/$($record.runAttempt)/$($record.lane).json" }
            Validate = { param($record, $path) Assert-LaneHealthRecord $record $path }
        }
    }
    foreach ($type in (Get-NonUiCohortRecordTypes).GetEnumerator()) { $types[$type.Key] = $type.Value }
    return $types
}

function Test-QualifiedHealthSource([string]$Event, [string]$Branch, [string]$Repository) {
    return $Event -cin @('push', 'schedule', 'workflow_run') -and $Branch -ceq 'main' -and
        $Repository -ceq $FilesystemHealthRepository
}

function Assert-HealthObservationController([switch]$AllowLocalFixture) {
    if ($AllowLocalFixture) { return }
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne $FilesystemHealthRepository -or
        $env:GITHUB_RUN_ID -cnotmatch '^[1-9][0-9]*$' -or
        $env:HEALTH_CONTROLLER_REVISION -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Observation publication requires the authenticated trusted workflow controller, not candidate credentials.'
    }
    $controller = Invoke-HealthApi "actions/runs/$($env:GITHUB_RUN_ID)"
    $root = Split-Path $PSScriptRoot -Parent
    $revision = git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Trusted controller checkout identity unavailable.' }
    $changed = @(git -C $root status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $revision -cne $env:HEALTH_CONTROLLER_REVISION -or
        $changed.Count -ne 0 -or
        $controller.path -cne '.github/workflows/lane-health.yml' -or $controller.event -cne 'workflow_run' -or
        $controller.head_branch -cne 'main' -or $controller.head_repository.full_name -cne $FilesystemHealthRepository -or
        $controller.head_sha -cne $env:HEALTH_CONTROLLER_REVISION) {
        throw 'Observation writer code/provenance is not the trusted main controller.'
    }
}

function Assert-LaneHealthRecord($Record, [string]$Path) {
    Assert-HealthNumber $Record.schemaVersion 'schemaVersion'
    foreach ($name in @('recordType', 'repository', 'lane', 'revision', 'controllerRevision', 'runId', 'runAttempt',
        'authority', 'sourceEvent', 'sourceBranch', 'sourceRepository', 'workflowPath', 'validation', 'timingHealth',
        'policyVersion', 'originalPolicy', 'verificationScope')) {
        if ($Record[$name] -isnot [string] -or [string]::IsNullOrWhiteSpace($Record[$name])) { throw "Invalid lane text field: $name" }
    }
    $qualified = Test-QualifiedHealthSource $Record.sourceEvent $Record.sourceBranch $Record.sourceRepository
    $observation = $Record.recordType -ceq 'lane-observation'
    $namespace = if ($observation) { 'observations' } else { 'lanes' }
    $lanePattern = '^(core|filesystem|desktop-(people|workspace)|high-resolution-(reading|context)|ci|optional-(scale|rehearsal)|release-(plan|publish)|renewal-(plan|publish)|installer|(?:optional-)?rehearsal-package|(?:(?:optional-scale|optional-rehearsal-scale|scale|renewal-scale)-)?(?:non-ui-performance|ui-scale(?:-prefix|-tail)?)|unmapped-[1-9][0-9]*)$'
    if ($Record.schemaVersion -ne 2 -or $Record.recordType -cnotin @('lane-health', 'lane-observation') -or
        $Record.repository -cne $FilesystemHealthRepository -or $Record.revision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.controllerRevision -cnotmatch '^[0-9a-f]{40}$' -or $Record.lane -cnotmatch $lanePattern -or
        $Record.runId -cnotmatch '^[1-9][0-9]*$' -or $Record.runAttempt -cnotmatch '^[1-9][0-9]*$' -or
        ($observation -and ($qualified -or $Record.authority -cne 'trusted-controller-observation')) -or
        (!$observation -and (!$qualified -or $Record.authority -cne 'trusted-main-push')) -or
        $Record.baselineEligible -isnot [bool] -or $Record.baselineEligible -or
        $Record.comparisonInputEligible -isnot [bool] -or $Record.comparisonInputEligible -ne $qualified -or
        $Record.sourceEvent -cnotin @('push', 'schedule', 'workflow_run', 'pull_request', 'workflow_dispatch') -or
        $Record.workflowPath -cnotin @('.github/workflows/ci.yml', '.github/workflows/scale-verification.yml',
            '.github/workflows/installer-verification.yml', '.github/workflows/release.yml',
            '.github/workflows/release-verification.yml') -or
        $Record.validation -cnotin @('success', 'failure', 'incomplete', 'cancelled', 'skipped') -or
        $Record.timingHealth -cnotin @('observation', 'unbaselined', 'inconclusive') -or
        $Record.policyVersion -cne 'lane-observation-v2' -or
        $Path -cne "$namespace/$($Record.runId)/$($Record.runAttempt)/$($Record.lane).json") {
        throw 'Invalid durable lane observation schema/provenance.'
    }
    Assert-HealthDate $Record.observedAt 'observedAt'
    if ($null -ne $Record.writerRevision -and $Record.writerRevision -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid writer revision.' }
    if ($Record.seriesKey -ne $null) {
        $measurement = @{ configuration = $Record.configuration; originalPolicy = $Record.originalPolicy
            selector = $Record.selector; measuredAt = $Record.measuredAt }
        if ($Record.seriesKey -cne (Get-LaneHealthSeriesKey $measurement)) { throw 'Lane comparison configuration/hash disagrees.' }
    }
    if ($null -ne $Record.measuredAt) {
        Assert-HealthDate $Record.measuredAt 'measuredAt'
        if ([DateTimeOffset]$Record.measuredAt -gt [DateTimeOffset]$Record.observedAt) { throw 'Lane measurement is later than its observation.' }
    }
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
            $ceiling = if ($Record.lane -ceq 'core') { 300 } elseif ($Record.lane -cmatch '^(desktop-|high-resolution-)') { 900 } else { 660 }
            if ($Record.measurements.ceilingSeconds -ne $ceiling) { throw 'Lane measurement changed an original budget.' }
        }
        foreach ($step in @($Record.measurements.steps)) {
            if ($null -ne $step.seconds) { Assert-HealthNumber $step.seconds 'phase.seconds' }
        }
        foreach ($metric in @($Record.measurements.metrics)) {
            Assert-HealthNumber $metric.value $metric.name
            foreach ($sample in @($metric.samples)) { Assert-HealthNumber $sample $metric.name }
            $count = if ($metric.name -clike 'team-editor-*') { 20 } elseif ($metric.name -clike 'person-navigation-*') { 42 } else { 21 }
            if (@($metric.samples).Count -ne $count -or $metric.value -ne @($metric.samples | Sort-Object)[[int][Math]::Ceiling(.95 * $count) - 1]) {
                throw 'Historical latency changed its original samples or nearest-rank p95.'
            }
        }
    }
    if ($Record.validation -ceq 'success' -and $Record.verificationScope -ceq 'runner' -and $null -eq $Record.measurements) {
        throw 'Successful runner validation lacks complete evidence.'
    }
    if ($Record.tests) {
        foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
            Assert-HealthNumber $Record.tests[$name] "tests.$name"
            if ($Record.tests[$name] % 1 -ne 0) { throw 'Fractional test count.' }
        }
        if ($Record.tests.total -ne $Record.tests.executed + $Record.tests.notExecuted -or
            $Record.tests.executed -ne $Record.tests.passed + $Record.tests.failed) { throw 'Lane test counters disagree.' }
        if ($Record.validation -ceq 'success' -and ($Record.tests.failed -ne 0 -or $Record.tests.notExecuted -ne 0 -or
            $Record.tests.summary -cne 'Completed')) { throw 'Successful lane lacks completed passed tests.' }
    }
    if ($Record.evidence.state -cnotin @('available', 'missing', 'expired', 'unavailable', 'rejected') -or
        $Record.evidence.url -cne "https://github.com/$FilesystemHealthRepository/actions/runs/$($Record.runId)/attempts/$($Record.runAttempt)") {
        throw 'Invalid lane evidence link/state.'
    }
    if ($null -ne $Record.evidence.expiresAt) { Assert-HealthDate $Record.evidence.expiresAt 'expiresAt' }
    if ($null -ne $Record.reference) {
        if ($Record.reference.revision -ceq $Record.revision -or $Record.reference.revision -cnotmatch '^[0-9a-f]{40}$' -or
            $Record.reference.kind -cne 'prior-compatible-observation') {
            throw 'Invalid self-referencing lane comparison.'
        }
        Assert-HealthNumber $Record.reference.executionSeconds 'reference.seconds'
        Assert-HealthNumber $Record.reference.deltaSeconds 'reference.delta' -AllowNegative
        if ($Record.reference.deltaSeconds -ne $Record.measurements.executionSeconds - $Record.reference.executionSeconds) {
            throw 'Lane comparison inputs disagree.'
        }
        Assert-HealthNumber $Record.reference.ageSeconds 'reference.ageSeconds'
        foreach ($step in @($Record.reference.steps)) { if ($null -ne $step.seconds) { Assert-HealthNumber $step.seconds 'reference.phase' } }
        foreach ($metric in $Record.reference.metrics) { Assert-HealthNumber $metric.value 'reference.metric' }
        if ($null -ne $Record.reference.deltaPercent) {
            Assert-HealthNumber $Record.reference.deltaPercent 'reference.deltaPercent' -AllowNegative
            if ($Record.reference.executionSeconds -le 0 -or $Record.reference.deltaPercent -ne
                100 * $Record.reference.deltaSeconds / $Record.reference.executionSeconds) { throw 'Invalid relative percentage.' }
        }
    }
    if ($Record.reliability) {
        $sum = 0
        foreach ($name in @('success', 'failure', 'incomplete', 'cancelled', 'skipped')) {
            Assert-HealthNumber $Record.reliability[$name] "reliability.$name"
            if ($Record.reliability[$name] % 1 -ne 0) { throw 'Fractional reliability count.' }
            $sum += $Record.reliability[$name]
        }
        Assert-HealthNumber $Record.reliability.attempts 'attempts'
        Assert-HealthNumber $Record.reliability.failureRatePercent 'failureRatePercent'
        if ($sum -ne $Record.reliability.attempts -or $sum -le 0 -or
            $Record.reliability.failureRatePercent -ne 100 * $Record.reliability.failure / $sum) { throw 'Lane reliability inputs disagree.' }
    }
    if ($Record.variability) {
        foreach ($name in @('count', 'minimumSeconds', 'maximumSeconds')) { Assert-HealthNumber $Record.variability[$name] $name }
        if ($Record.variability.count -lt 2 -or $Record.variability.count % 1 -ne 0 -or
            $Record.variability.minimumSeconds -gt $Record.variability.maximumSeconds) { throw 'Invalid descriptive variability.' }
    }
}

function Get-HostedHealth($Job) {
    $steps = @()
    foreach ($step in @($Job.steps)) {
        $seconds = $null
        $valid = $false
        $start = [DateTimeOffset]::MinValue; $end = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse($step.started_at, [ref]$start) -and [DateTimeOffset]::TryParse($step.completed_at, [ref]$end)) {
            $valid = $end -ge $start
            if ($valid -and $step.conclusion -cne 'skipped') {
                $seconds = ($end - $start).TotalSeconds
            }
        }
        $steps += @{ name = $step.name; outcome = $step.conclusion; startedAt = $step.started_at
            finishedAt = $step.completed_at; clockValid = $valid; seconds = $seconds }
    }
    $seconds = $null
    $valid = $false
    $start = [DateTimeOffset]::MinValue; $end = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse($Job.started_at, [ref]$start) -and [DateTimeOffset]::TryParse($Job.completed_at, [ref]$end)) {
        $valid = $end -ge $start
        if ($valid -and $Job.conclusion -cne 'skipped') {
            $seconds = ($end - $start).TotalSeconds
        }
    }
    return @{ startedAt = $Job.started_at; finishedAt = $Job.completed_at; clockValid = $valid
        seconds = $seconds; steps = $steps; scope = 'API job wall time including setup and post/upload, not product latency' }
}

function Get-CoreHealthMeasurement([string]$Directory, $Run) {
    $execution = Get-Content (Join-Path $Directory 'execution.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($name in @('schemaVersion', 'ceilingSeconds', 'targetSeconds')) { Assert-HealthNumber $execution[$name] $name }
    if ($execution.schemaVersion -ne 1 -or $execution.lane -cne 'core' -or
        $execution.revision -cne $Run.head_sha -or $execution.runId -cne [string]$Run.id -or
        $execution.runAttempt -cne [string]$Run.run_attempt -or $execution.selector -cne 'all' -or
        $execution.policyVersion -cne 'core-original-v1' -or $execution.ceilingSeconds -ne 300 -or $execution.targetSeconds -ne 300 -or
        $execution.complete -isnot [bool]) { throw 'Unbound core policy/selection.' }
    Assert-HealthNumber $execution.executionSeconds 'executionSeconds'
    Assert-HealthDate $execution.startedAt 'startedAt'
    Assert-HealthDate $execution.finishedAt 'finishedAt'
    if ([DateTimeOffset]$execution.finishedAt -lt [DateTimeOffset]$execution.startedAt -or
        [DateTimeOffset]$execution.finishedAt -gt [DateTimeOffset]::UtcNow) { throw 'Invalid terminal core clocks.' }
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
    $correctness = $validation
    $diagnostics = @()
    if ($execution.ContainsKey('configurationErrors')) { $diagnostics += @($execution.configurationErrors) }
    if ($execution.ContainsKey('error') -and $execution.error) { $diagnostics += "Original runner error: $($execution.error)" }
    if ($execution.executionSeconds -gt 300) {
        if ($validation -ceq 'success') { $validation = 'failure' }
        $diagnostics += 'Completed checks/runner exit cannot waive the original 300-second whole-execution acceptance budget.'
    }
    return @{ validation = $validation; tests = $tests; configuration = $execution.configuration; diagnostics = $diagnostics
        correctnessChecks = $correctness; originalPolicyAcceptance = $validation
        measuredAt = $execution.finishedAt; runnerOutcome = $execution.outcome; runnerComplete = $execution.complete
        measurements = @{ executionSeconds = $execution.executionSeconds; ceilingSeconds = 300
            headroomSeconds = 300 - $execution.executionSeconds; steps = $steps; metrics = @() }
        selector = 'all'; originalPolicy = 'core-original-v1' }
}

function Export-LocalCoreHealth([string]$Directory, [string]$WorkingDirectory, [Nullable[int]]$ObservedExitCode) {
    $execution = Get-Content (Join-Path $Directory 'execution.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $run = @{ head_sha = $execution.revision; id = $execution.runId; run_attempt = $execution.runAttempt }
    $result = Get-CoreHealthMeasurement $Directory $run
    $record = [ordered]@{
        schemaVersion = 2; recordType = 'local-lane-observation'; repository = $FilesystemHealthRepository; lane = 'core'
        revision = $execution.revision; controllerRevision = $null; writerRevision = $null; writerScope = 'local result projection'
        runId = $execution.runId; runAttempt = $execution.runAttempt; authority = 'untrusted-local-observation'
        baselineEligible = $false; comparisonInputEligible = $false; candidateState = 'actual local runner metadata; not hosted acceptance'
        jobName = 'Shared-local core'; jobOutcome = 'unavailable (no hosted job)'; observedCommandExitCode = $ObservedExitCode
        runnerOutcome = $result.runnerOutcome; runnerComplete = $result.runnerComplete
        correctnessChecks = $result.correctnessChecks; originalPolicyAcceptance = $result.originalPolicyAcceptance
        observedAt = [DateTimeOffset]::UtcNow.ToString('O'); measuredAt = $result.measuredAt
        validation = $result.validation; verificationScope = 'shared-local runner only'; policyVersion = 'lane-observation-v2'
        originalPolicy = $result.originalPolicy; measurements = $result.measurements; tests = $result.tests
        configuration = $result.configuration; diagnostics = @($result.diagnostics) + @('No hosted run, timing reference or canonical baseline eligibility is established.')
        hosted = @{ seconds = $null; steps = @() }; timingHealth = 'inconclusive'; reference = $null
        variability = $null; reliability = $null; recent = @()
        evidence = @{ state = 'local'; url = ''; expiresAt = $null }
    }
    New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
    $record | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $WorkingDirectory 'record.json')
    Format-LaneHealth @($record) 'local projection only; no history publication' | Set-Content (Join-Path $WorkingDirectory 'summary.md')
    return $record
}

function Get-DesktopHealthMeasurement($Descriptor, $Evidence, $Run) {
    $directory = $Evidence[$Descriptor.lane].directory
    $metadata = Get-Content (Join-Path $directory 'execution.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $partition = $Descriptor.lane.Substring($Descriptor.kind.Length + 1)
    if ($metadata.revision -cne $Run.head_sha -or $metadata.runId -cne [string]$Run.id -or
        $metadata.runAttempt -cne [string]$Run.run_attempt -or $metadata.partition -cne $partition -or
        $metadata.lane -cne $Descriptor.kind -or $metadata.complete -isnot [bool]) {
        throw 'Unbound desktop candidate/run/attempt/partition.'
    }
    Assert-HealthNumber $metadata.executionSeconds 'executionSeconds'
    foreach ($step in $metadata.steps) { Assert-HealthNumber $step.seconds $step.name }
    $collection = Join-Path (Split-Path $directory -Parent) "$($Descriptor.kind)-collection"
    New-Item -ItemType Directory -Path $collection -Force | Out-Null
    $partitions = Get-DesktopPartitions -HighResolution:($Descriptor.kind -ceq 'high-resolution')
    foreach ($name in $partitions.Keys) {
        $item = $Evidence["$($Descriptor.kind)-$name"]
        if ($item.directory) {
            $destination = Join-Path $collection "$($Descriptor.kind)-$name-attempt-$($Run.run_attempt)"
            if (!(Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $item.directory -Destination $destination -Recurse }
        }
    }
    $validation = 'success'
    $diagnostics = @()
    if ($metadata.ContainsKey('configurationErrors')) { $diagnostics += @($metadata.configurationErrors) }
    try {
        Assert-DesktopPartitions $collection $Run.head_sha ([string]$Run.id) ([string]$Run.run_attempt) `
            -HighResolution:($Descriptor.kind -ceq 'high-resolution')
    } catch { $validation = 'incomplete'; $diagnostics += "Native selection/companions rejected: $($_.Exception.Message)" }
    $scenarios = Get-Content (Join-Path $directory 'scenarios.json') -Raw | ConvertFrom-Json -AsHashtable
    $steps = @($metadata.steps)
    if ($metadata.ContainsKey('companionSteps')) {
        foreach ($companion in $metadata.companionSteps) {
            Assert-HealthNumber $companion.seconds $companion.name
            $steps += $companion
        }
    } elseif ($partition -ceq 'workspace') { $diagnostics += 'Compiled trust/icon/output companion timing unavailable in this execution schema.' }
    foreach ($scenario in $scenarios.scenarios) {
        Assert-HealthNumber $scenario.seconds $scenario.name
        $steps += @{ name = $scenario.name; seconds = $scenario.seconds; status = $scenario.status; scope = 'native scenario' }
    }
    return @{ validation = $validation; tests = $null; diagnostics = $diagnostics
        configuration = if ($metadata.ContainsKey('configuration')) { $metadata.configuration } else { $null }
        measuredAt = if ($metadata.ContainsKey('finishedAt')) { $metadata.finishedAt } else { $null }
        runnerOutcome = if ($metadata.complete) { 'success' } else { 'incomplete' }; runnerComplete = $metadata.complete
        measurements = @{ executionSeconds = $metadata.executionSeconds; ceilingSeconds = 900
            headroomSeconds = 900 - $metadata.executionSeconds; steps = $steps; metrics = @() }
        selector = ($partitions[$partition] -join ';'); originalPolicy = "$($Descriptor.kind)-original-v1" }
}

function Get-LaneHealthMeasurement($Descriptor, $Evidence, $Run) {
    $directory = $Evidence[$Descriptor.lane].directory
    foreach ($entry in Get-ChildItem -LiteralPath $directory -Recurse -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Candidate evidence must not contain filesystem links.' }
    }
    switch -CaseSensitive ($Descriptor.kind) {
        'core' { return Get-CoreHealthMeasurement $directory $Run }
        'filesystem' {
            $result = Get-FilesystemMeasurement $directory $Run
            $result.measurements.ceilingSeconds = 660
            $result.measurements.metrics = @()
            return @{ validation = $result.validation; tests = $result.tests; diagnostics = $result.diagnostics
                configuration = $result.execution.configuration; measuredAt = $result.execution.finishedAt
                measurements = $result.measurements; selector = $result.execution.selector
                originalPolicy = $result.execution.policyVersion }
        }
        { $_ -cin @('desktop', 'high-resolution') } { return Get-DesktopHealthMeasurement $Descriptor $Evidence $Run }
        { $_ -cin @('non-ui-performance', 'ui-scale-prefix', 'ui-scale-tail', 'ui-scale') } {
            return Get-ScaleHealthMeasurement $Descriptor $Evidence $Run
        }
        { $_ -cin @('installer', 'rehearsal') } { return Get-LifecycleHealthMeasurement $Descriptor $Evidence $Run }
        default { throw 'Unsupported measurement adapter.' }
    }
}

function Get-LifecycleHealthMeasurement($Descriptor, $Evidence, $Run) {
    $files = @(Get-ChildItem -LiteralPath $Evidence[$Descriptor.lane].directory -Recurse -File -Filter 'result.json')
    if ($files.Count -ne 1) { throw 'Exactly one completed lifecycle result is required.' }
    $result = Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $diagnostics = @()
    $seconds = $null
    if ($Descriptor.kind -ceq 'installer') {
        if ($result.sourceCommit -cne $Run.head_sha -or $result.runId -cne [string]$Run.id -or
            $result.runAttempt -cne [string]$Run.run_attempt -or $result.result -cnotin @('passed', 'failed') -or
            $result.os.windows11Client -isnot [bool] -or !$result.os.coverageLimit -or !$result.trustLimitations) {
            throw 'Installer result lacks exact provenance or platform/trust scope.'
        }
        Assert-HealthNumber $result.executionSeconds 'installer.executionSeconds'
        $seconds = $result.executionSeconds
        $validation = if ($result.result -ceq 'passed') { 'success' } else { 'failure' }
        $diagnostics += @($result.os.coverageLimit, $result.trustLimitations,
            'Installer process seconds exclude outer restore, packaging and post/upload. Hosted Server success is not Windows publisher or Windows 11 client certification.')
    } else {
        if ($result.revision -cne $Evidence[$Descriptor.lane].candidate -or
            $result.verificationRunId -ne $Run.id -or $result.runAttempt -cne [string]$Run.run_attempt -or
            $result.productionPublication -isnot [bool] -or $result.productionPublication -or
            $result.installationExecuted -isnot [bool] -or $result.installationExecuted -or
            $result.ephemeralSigningOnly -isnot [bool] -or !$result.ephemeralSigningOnly -or
            $result.productionTrustIsolation -isnot [bool] -or !$result.productionTrustIsolation -or
            $result.metadataRenewalVerified -isnot [bool] -or !$result.metadataRenewalVerified -or
            $result.outcome -cne 'success') { throw 'Incomplete or unbound release rehearsal result.' }
        $validation = 'success'
        $diagnostics += 'Rehearsal verifies reported ephemeral packaging/metadata renewal, not production publication, production signing-key provisioning or installer execution.'
    }
    return @{ validation = $validation; tests = $null; diagnostics = $diagnostics
        configuration = $null; measuredAt = $null; originalPolicy = "$($Descriptor.kind)-original-v1"; selector = $Descriptor.kind
        measurements = if ($null -ne $seconds) { @{ executionSeconds = $seconds; ceilingSeconds = $null
            headroomSeconds = $null; steps = @(); metrics = @()
            scope = 'installer native lifecycle process only; outer setup/package/cleanup/upload is separate' } } else { $null } }
}

function Read-HealthPhases([string]$Path) {
    $phases = [Collections.Generic.List[object]]::new()
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -cmatch '^SCALE BEGIN (?<name>[^;]+);') {
            $name = $Matches.name
            $occurrence = @($phases | Where-Object { $_.name -ceq $name }).Count + 1
            $phases.Add(@{ name = $name; occurrence = $occurrence; seconds = $null; status = 'incomplete'; scope = 'observed phase, may overlap' })
        } elseif ($line -cmatch '^SCALE END (?<name>[^;]+);.*outcome=(?<status>[^;]+); ms=(?<ms>[0-9]+(?:\.[0-9]+)?);') {
            $name = $Matches.name; $status = $Matches.status
            $seconds = [double]::Parse($Matches.ms, [Globalization.CultureInfo]::InvariantCulture) / 1000
            $active = @($phases | Where-Object { $_.name -ceq $name -and $_.status -ceq 'incomplete' })
            if (!$active.Count) { throw 'Phase end lacks its observed start.' }
            $active[-1].seconds = $seconds; $active[-1].status = $status
        }
    }
    return @($phases.ToArray())
}

function Read-HealthLatency($Data, [string]$Name, [int]$Count) {
    $samples = @($Data.samplesMs)
    if ($samples.Count -ne $Count) { throw "Missing original sample matrix: $Name" }
    foreach ($sample in $samples) { Assert-HealthNumber $sample $Name }
    Assert-HealthNumber $Data.p95Ms "$Name.p95"
    if ($Data.p95Ms -ne @($samples | Sort-Object)[[int][Math]::Ceiling(.95 * $Count) - 1]) {
        throw "Original nearest-rank p95 disagrees: $Name"
    }
    $metric = @{ name = $Name; value = $Data.p95Ms; unit = 'ms'; samples = $samples
        percentile = 'nearest-rank p95'; scope = 'original user-facing measurement; not a cohort median' }
    foreach ($field in @('goalMs', 'budgetMs')) {
        if ($Data.ContainsKey($field)) { Assert-HealthNumber $Data[$field] $field; $metric[$field] = $Data[$field] }
    }
    foreach ($field in @('goalMet', 'budgetEnforced', 'temporaryCeiling')) {
        if ($Data.ContainsKey($field)) {
            if ($Data[$field] -isnot [bool]) { throw "Invalid original latency policy field: $field" }
            $metric[$field] = $Data[$field]
        }
    }
    return $metric
}

function Get-ScaleHealthMeasurement($Descriptor, $Evidence, $Run) {
    $item = $Evidence[$Descriptor.lane]
    $directory = $item.directory
    $candidate = $item.candidate
    $reportText = Get-Content (Join-Path $directory 'result.json') -Raw
    $report = $reportText | ConvertFrom-Json -AsHashtable -DateKind String -Depth 30
    if ($candidate -cnotmatch '^[0-9a-f]{40}$' -or $report.revision -cne $candidate -or
        $report.expectedRevision -cne $candidate -or $report.runId -cne [string]$Run.id -or
        $report.runAttempt -cne [string]$Run.run_attempt -or $report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$' -or
        $report.suite -cne $(if ($Descriptor.kind -ceq 'non-ui-performance') { 'non-ui-performance' } else { 'ui-scale' })) {
        throw 'Scale candidate/run/attempt/owned-directory provenance is missing or mismatched.'
    }
    $owned = Join-Path $directory $report.runDirectory
    if ((Get-Content (Join-Path $owned 'result.json') -Raw) -cne $reportText) { throw 'Scale root/owned report disagreement.' }
    $steps = @()
    $metrics = @()
    $diagnostics = @()
    if ($report.ContainsKey('configurationErrors')) { $diagnostics += @($report.configurationErrors) }
    if ($report.ContainsKey('failure') -and $report.failure) { $diagnostics += "Original runner failure: $($report.failure)" }
    $validation = 'incomplete'
    $configuration = if ($report.ContainsKey('configuration')) { $report.configuration } else { $null }
    $tests = $null
    $selector = $Descriptor.kind
    $policy = if ($report.ContainsKey('policyVersion')) { $report.policyVersion } else { "$($Descriptor.kind)-original-v1" }
    $ceiling = $null
    if ($Descriptor.kind -ceq 'ui-scale') {
        if ($report.verificationMode -cne 'isolated-workers') { throw 'Only the actual isolated-worker union is supported.' }
        Assert-HealthDate $report.collectionStartedAt 'collectionStartedAt'
        Assert-HealthDate $report.collectionCompletedAt 'collectionCompletedAt'
        $seconds = ([DateTimeOffset]$report.collectionCompletedAt - [DateTimeOffset]$report.collectionStartedAt).TotalSeconds
        Assert-HealthNumber $seconds 'collectionSeconds'
        $finished = $report.collectionCompletedAt
        try {
            Assert-UiScaleUnion (Join-Path $owned 'prefix') (Join-Path $owned 'tail') $candidate ([string]$Run.id) ([string]$Run.run_attempt)
            if ($report.outcome -ceq 'success') { $validation = 'success' }
        } catch { $diagnostics += "Complete prefix/tail/union rejected: $($_.Exception.Message)" }
        $diagnostics += 'Union collection seconds are validation/transfer cost, not summed worker time or product latency.'
    } else {
        Assert-HealthNumber $report.executionSeconds 'executionSeconds'
        Assert-HealthDate $report.startedAt 'startedAt'
        Assert-HealthDate $report.completedAt 'completedAt'
        if ([DateTimeOffset]$report.completedAt -lt [DateTimeOffset]$report.startedAt -or
            $report.ceilingSeconds -ne 660 -or $report.outcome -cnotin @('success', 'failure', 'timed_out', 'diagnostic_complete')) {
            throw 'Invalid original terminal scale clocks/policy/outcome.'
        }
        $seconds = $report.executionSeconds; $ceiling = 660; $finished = $report.completedAt
        foreach ($step in @($report.steps)) {
            Assert-HealthNumber $step.seconds $step.name
            $steps += @{ name = $step.name; seconds = $step.seconds
                status = if ($step.exitCode -eq 0) { 'Passed' } else { 'Failed' }; scope = 'runner command' }
        }
        if ($report.outcome -ceq 'failure') { $validation = 'failure' }
        if ($Descriptor.kind -ceq 'non-ui-performance') {
            $selector = $PerformanceFilter
            $tests = Read-FilesystemTests $owned
            try {
                Assert-PerformanceResults $owned
                if ($report.filter -cne $PerformanceFilter -or
                    ($report.requiredTests -join "`n") -cne ($PerformanceTests -join "`n") -or
                    ($report.steps.name -join "`n") -cne "restore`ntest" -or
                    @($report.steps | Where-Object { $_.exitCode -ne 0 }).Count -or
                    $report.phase -cne 'finished' -or $report.outcome -cne 'success' -or $seconds -gt 660 -or
                    $report.trackedChanges -isnot [bool] -or $report.trackedChanges -or
                    $report.untrackedChanges -isnot [bool] -or $report.untrackedChanges -or
                    $report.diagnosticOnly -isnot [bool] -or $report.diagnosticOnly) { throw 'Incomplete/nonaccepting non-UI policy/selection.' }
                $validation = 'success'
            } catch { $diagnostics += "Non-UI selection rejected: $($_.Exception.Message)" }
            if ($tests.failed -gt 0) { $validation = 'failure' }
            $log = Join-Path $owned 'scale-measurements.log'
            $steps += @(Read-HealthPhases $log)
            if (Test-Path -LiteralPath $log) {
                foreach ($line in Get-Content -LiteralPath $log) {
                    if ($line.StartsWith('SCALE ORDINARY-PROFILE ')) {
                        $latency = $line.Substring('SCALE ORDINARY-PROFILE '.Length) | ConvertFrom-Json -AsHashtable
                        $metrics += Read-HealthLatency $latency 'profile-save' 21
                    }
                }
            }
        } else {
            $worker = if ($Descriptor.kind -ceq 'ui-scale-prefix') { 'prefix' } else { 'tail' }
            $selector = "--ui-scale-$worker"
            try { $null = Get-UiScaleWorkerResult $directory $worker $candidate ([string]$Run.id) ([string]$Run.run_attempt); $validation = 'success' }
            catch { $diagnostics += "Original rich worker rejected: $($_.Exception.Message)" }
            $steps += @(Read-HealthPhases (Join-Path $owned 'ui-scale.log'))
            $bindingPath = if ($worker -ceq 'prefix') { Join-Path $owned 'handoff\handoff.json' } else { Join-Path $owned 'handoff-received.json' }
            $binding = $null
            if (Test-Path -LiteralPath $bindingPath) { $binding = (Get-Content $bindingPath -Raw | ConvertFrom-Json).binding }
            if ($null -eq $binding) {
                if ($validation -ceq 'success') { $validation = 'incomplete' }
                $diagnostics += 'Final logical handoff unavailable: partial metric candidate/run/attempt and reported product hashes are retained, not a verified product/handoff binding.'
            }
            if ($worker -ceq 'prefix') {
                foreach ($phase in @('normal-executable', 'background-export')) {
                    $path = Join-Path $owned "scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json"
                    if (Test-Path -LiteralPath $path) {
                        $note = if ($binding) {
                            Read-UiScaleEvidence $owned (Split-Path $path -Leaf) $candidate ([string]$Run.id) ([string]$Run.run_attempt) $binding -AsHashtable
                        } else { Read-UiScaleEvidenceIdentity $owned (Split-Path $path -Leaf) $candidate ([string]$Run.id) ([string]$Run.run_attempt) -AsHashtable }
                        $metrics += Read-HealthLatency $note.localDurability "note-durability-$phase" 21
                        $metrics += Read-HealthLatency $note.addTeamNote "team-editor-$phase" 20
                        $metrics += Read-HealthLatency $note.personNavigation "person-navigation-$phase" 42
                    } else { $diagnostics += "Missing original note metrics: $phase" }
                }
            }
            $phase = if ($worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }
            $scenario = if ($worker -ceq 'prefix') { 'scale-mutation-save-prefix' } else { 'scale-mutation-delivery-tail' }
            $path = Join-Path $owned "$scenario-ui-sqlite-rich-profile-latency-$phase.json"
            if (Test-Path -LiteralPath $path) {
                $profile = if ($binding) {
                    Read-UiScaleEvidence $owned (Split-Path $path -Leaf) $candidate ([string]$Run.id) ([string]$Run.run_attempt) $binding -AsHashtable
                } else { Read-UiScaleEvidenceIdentity $owned (Split-Path $path -Leaf) $candidate ([string]$Run.id) ([string]$Run.run_attempt) -AsHashtable }
                $metrics += Read-HealthLatency $profile "profile-save-$phase" 21
            } else { $diagnostics += 'Missing original profile latency metrics.' }
            $diagnostics += 'Worker scenario/phase proof is not accepting union proof. Full raw product/native/logical continuity is established only by the exact accepting union.'
        }
    }
    return @{ validation = $validation; tests = $tests; diagnostics = $diagnostics; revision = $candidate
        configuration = $configuration; measuredAt = $finished; originalPolicy = $policy; selector = $selector
        measurements = @{ executionSeconds = $seconds; ceilingSeconds = $ceiling
            headroomSeconds = if ($null -ne $ceiling) { $ceiling - $seconds } else { $null }
            steps = $steps; metrics = $metrics } }
}

function Get-LaneHealthSeriesKey($Measurement) {
    if ($null -eq $Measurement.configuration -or $null -eq $Measurement.measuredAt) {
        throw 'Resolved configuration or measurement clock unavailable.'
    }
    if ($Measurement.configuration.workload -ceq 'OrgExplorer.Desktop.Smoke' -and
        (!$Measurement.configuration.ContainsKey('display') -or $null -eq $Measurement.configuration.display)) {
        throw 'Actual display configuration unavailable.'
    }
    if ($Measurement.selector -clike '--ui-scale-*' -and
        (!$Measurement.configuration.ContainsKey('native') -or $null -eq $Measurement.configuration.native)) {
        throw 'Actual loaded native binding unavailable.'
    }
    $execution = @{ configuration = $Measurement.configuration; policyVersion = $Measurement.originalPolicy
        selector = $Measurement.selector }
    return Get-HealthSeriesKey $execution
}

function Set-LaneHealthComparison($Record, $History) {
    $attempts = @($History.records | Where-Object { $_.Contains('lane') -and $_.lane -ceq $Record.lane -and
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
        (($_.Contains('comparisonInputEligible') -and $_.comparisonInputEligible) -or
            (!$_.Contains('recordType') -and $_.authority -ceq 'trusted-main-push')) -and
        $_.validation -ceq 'success' -and $_.revision -cne $Record.revision -and
        $null -ne $_.measurements -and [DateTimeOffset]$_.measuredAt -le [DateTimeOffset]$Record.observedAt } |
        Sort-Object measuredAt -Descending)
    if (!$compatible.Count) {
        $Record.timingHealth = if ($attempts.Count) { 'inconclusive' } else { 'unbaselined' }
        return
    }
    $previous = $compatible[0]
    $previousMetrics = @()
    if ($previous.measurements.Contains('metrics')) { $previousMetrics = @($previous.measurements.metrics) }
    $Record.reference = @{ revision = $previous.revision; runId = $previous.runId; runAttempt = $previous.runAttempt
        kind = 'prior-compatible-observation'; measuredAt = $previous.measuredAt
        executionSeconds = $previous.measurements.executionSeconds; steps = $previous.measurements.steps
        deltaSeconds = $Record.measurements.executionSeconds - $previous.measurements.executionSeconds
        deltaPercent = if ($previous.measurements.executionSeconds -gt 0) {
            100 * ($Record.measurements.executionSeconds - $previous.measurements.executionSeconds) / $previous.measurements.executionSeconds
        } else { $null }
        metrics = $previousMetrics
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
            "Candidate: ``$($record.revision)``; source controller: $(if ($record.controllerRevision) { '``' + $record.controllerRevision + '``' } else { 'unavailable (local result)' }); run/attempt: $($record.runId)/$($record.runAttempt).",
            "Candidate evidence: $($record.candidateState).",
            "Observer code: $(if ($record.writerRevision) { '``' + $record.writerRevision + '``' } else { 'unavailable (local projection)' }) ($($record.writerScope)); source workflow revision is recorded separately.",
            "History eligibility: $(if ($record.comparisonInputEligible) { 'qualified main observation; canonical baseline eligibility remains pending calibration' } elseif ($record.recordType -ceq 'local-lane-observation') { 'baseline-ineligible local observation; never a comparison input' } else { 'baseline-ineligible PR/manual/fork observation; never a comparison input' }).",
            "Policy: ``$($record.originalPolicy)``; scope: $($record.verificationScope).")
        if ($record.Contains('runnerOutcome') -and $null -ne $record.runnerOutcome) {
            $lines += "Raw runner outcome: **$($record.runnerOutcome)**; completed correctness checks: **$($record.correctnessChecks)**; original-policy acceptance: **$($record.originalPolicyAcceptance)**."
        }
        if ($record.Contains('observedCommandExitCode') -and $null -ne $record.observedCommandExitCode) { $lines += "Observed command exit: $($record.observedCommandExitCode)." }
        $lines += @('', '| Measurement / phase | Value | Reference | Change |', '|---|---:|---:|---:|')
        if ($record.measurements) {
            $scope = if ($record.measurements.Contains('scope')) { $record.measurements.scope } else { 'Whole runner execution (setup/cleanup included)' }
            $reference = if ($record.reference) { "$($record.reference.executionSeconds) s" } else { 'unavailable' }
            $delta = if ($record.reference) { "$($record.reference.deltaSeconds) s" } else { 'unavailable' }
            $lines += "| $scope | $($record.measurements.executionSeconds) s | $reference | $delta |"
            foreach ($step in $record.measurements.steps) {
                $prior = @()
                if ($record.reference) { $prior = @($record.reference.steps | Where-Object {
                    $_.name -ceq $step.name -and
                    (!$step.Contains('occurrence') -or ($_.Contains('occurrence') -and $_.occurrence -eq $step.occurrence))
                }) }
                $value = if ($null -ne $step.seconds) { "$($step.seconds) s" } else { 'unavailable (unfinished phase)' }
                $reference = if ($prior.Count -eq 1 -and $null -ne $prior[0].seconds) { "$($prior[0].seconds) s" } else { 'unavailable' }
                $delta = if ($prior.Count -eq 1 -and $null -ne $prior[0].seconds -and $null -ne $step.seconds) { "$($step.seconds - $prior[0].seconds) s" } else { 'unavailable' }
                $lines += "| $([Net.WebUtility]::HtmlEncode($step.name).Replace('|', '&#124;')) | $value | $reference | $delta |"
            }
            foreach ($metric in $record.measurements.metrics) {
                $prior = @()
                if ($record.reference) { $prior = @($record.reference.metrics | Where-Object { $_.name -ceq $metric.name -and $_.unit -ceq $metric.unit }) }
                $reference = if ($prior.Count -eq 1) { "$($prior[0].value) $($metric.unit)" } else { 'unavailable' }
                $delta = if ($prior.Count -eq 1) { "$($metric.value - $prior[0].value) $($metric.unit)" } else { 'unavailable' }
                $lines += "| $([Net.WebUtility]::HtmlEncode($metric.name)) | $($metric.value) $($metric.unit) | $reference | $delta |"
                foreach ($field in @('goalMs', 'budgetMs', 'temporaryCeiling')) {
                    if ($metric.Contains($field)) { $lines += "Original $($metric.name) $field`: $($metric[$field]); no comparative gate is activated." }
                }
            }
            $headroom = if ($null -ne $record.measurements.headroomSeconds) { "$($record.measurements.headroomSeconds) s (original budget, not a warning band)" } else { 'unavailable' }
            $lines += "Headroom: $headroom."
        } else { $lines += '| Runner measurements | unavailable, not zero | unavailable | unavailable |' }
        if ($record.tests -and $record.tests.ContainsKey('workloads')) {
            foreach ($workload in $record.tests.workloads) {
                $seconds = if ($null -eq $workload.seconds) { 'unavailable' } else { "$($workload.seconds) s" }
                $lines += "Test workload $([Net.WebUtility]::HtmlEncode($workload.name)): $($workload.total) results, $($workload.passed) passed, $($workload.failed) failed, $($workload.skipped) skipped; case-duration sum $seconds (may overlap; not lane wall time)."
            }
        }
        $wall = if ($null -ne $record.hosted.seconds) { "$($record.hosted.seconds) s" } else { 'unavailable (skipped or invalid/missing clocks)' }
        $lines += "Hosted job wall time: $wall; includes setup/post/upload, not a runner or product latency."
        foreach ($step in $record.hosted.steps) {
            $value = if ($null -ne $step.seconds) { "$($step.seconds) s" } else { 'unavailable' }
            $lines += "Hosted phase $([Net.WebUtility]::HtmlEncode($step.name)): $($step.outcome), $value."
        }
        if ($record.reference) {
            $percent = if ($null -ne $record.reference.deltaPercent) { "$($record.reference.deltaPercent)%" } else { 'unavailable (zero-duration reference)' }
            $lines += "Reference: ``$($record.reference.revision)``; age $($record.reference.ageSeconds) s; change $($record.reference.deltaSeconds) s ($percent); raw evidence [$($record.reference.evidenceState)]($($record.reference.evidenceUrl))."
            $lines += 'Reference availability is retained metadata plus expiry, not a new download or fresh acceptance.'
        } else { $lines += 'Reference: unavailable (no compatible non-self observation); no release waiting dependency.' }
        if ($record.variability) {
            $lines += "Descriptive cross-revision range: $($record.variability.minimumSeconds)-$($record.variability.maximumSeconds) s, $($record.variability.count) observations; not calibrated noise."
        } else { $lines += 'Variability: unavailable.' }
        if ($record.reliability) {
            $lines += "All attempts: $($record.reliability.attempts); success $($record.reliability.success), failure $($record.reliability.failure), incomplete $($record.reliability.incomplete), cancelled $($record.reliability.cancelled), skipped $($record.reliability.skipped); failure rate $($record.reliability.failureRatePercent)%."
            foreach ($recent in $record.recent) {
                $seconds = if ($null -ne $recent.executionSeconds) { "$($recent.executionSeconds) s" } else { 'unavailable' }
                $lines += "Recent $($recent.runId)/$($recent.runAttempt): $($recent.validation), $seconds, revision ``$($recent.revision)``."
            }
        }
        foreach ($detail in $record.diagnostics) { $lines += [Net.WebUtility]::HtmlEncode($detail) }
        $lines += @("Evidence: $(if ($record.evidence.url) { '[' + $record.evidence.state + '](' + $record.evidence.url + ')' } else { 'local files, not GitHub artifacts' }); expiry $($record.evidence.expiresAt).",
            'Advertised runner/configuration identity is not proof of identical physical host load or a timing cause.',
            'Attribution: unknown unless the original evidence explicitly establishes it.', '')
    }
    $lines += "History publication: $([Net.WebUtility]::HtmlEncode($Publication))."
    return $lines -join "`n"
}
