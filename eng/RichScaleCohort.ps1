. "$PSScriptRoot\DesktopResults.ps1"
. "$PSScriptRoot\UiScaleResults.ps1"

function Read-RichCohortPhases([string[]]$Lines) {
    $phases = @{}
    $occurrences = @{}
    foreach ($line in $Lines) {
        if ($line -cmatch '^SCALE END (?<name>[^;]+); outcome=(?<outcome>[^;]+); ms=(?<ms>[0-9]+(?:\.[0-9]+)?)') {
            $name = $Matches.name
            if (!$occurrences.ContainsKey($name)) { $occurrences[$name] = 0 }
            $occurrences[$name]++
            $key = if ($occurrences[$name] -eq 1) { $name } else { "$name#$($occurrences[$name])" }
            $phases[$key] = @{
                value = [double]::Parse($Matches.ms, [Globalization.CultureInfo]::InvariantCulture)
                outcome = $Matches.outcome
            }
        }
    }
    return $phases
}

function Get-RichCohortSeriesKey($Sample, [string]$ExpectedPolicyVersion = 'rich-scale-original-v1') {
    $configuration = @{} + $Sample.configuration
    foreach ($worker in @('prefix', 'tail')) {
        $configuration[$worker] = Get-HealthSeriesKey @{
            policyVersion = $ExpectedPolicyVersion; selector = "--ui-scale-$worker"
            configuration = $Sample.rich[$worker].report.configuration
        }
    }
    $configuration.richTestTree = $Sample.rich.handoff.binding.testTree
    $configuration.nativeVersion = $Sample.rich.handoff.binding.nativeVersion
    $configuration.nativeSourceId = $Sample.rich.handoff.binding.nativeSourceId
    return Get-HealthSeriesKey @{
        policyVersion = $ExpectedPolicyVersion; selector = 'complete-non-ui-prefix-tail-union'
        configuration = $configuration
    }
}

function Read-RichCohortSample($Sample, [string]$Directory, [string]$Revision, [string]$CohortId,
    [string]$RunId, [string]$RunAttempt, $Jobs, $Artifacts, $Collection,
    [string]$ExpectedPolicyVersion = 'rich-scale-original-v1') {
    $policy = Get-ApprovedTimingPolicy $ExpectedPolicyVersion
    $Sample.rich = [ordered]@{ validation = 'incomplete'; seriesKey = $null; handoff = $null; receipt = $null }
    $directories = @{}
    $outcomes = @($Sample.validation)
    foreach ($worker in @('prefix', 'tail', 'union')) {
        $name = "rich-cohort-$CohortId-sample-$($Sample.number)-$worker"
        $templateName = [regex]::Escape("rich-sample-$($Sample.number) / Rich cohort $worker (" + '${{ inputs.sample }}' + ')')
        $workerJobs = @($Jobs | Where-Object {
            $_.name -cmatch "(^| / )Rich cohort $worker \($($Sample.number)\)$" -or
            ($_.conclusion -cin @('skipped', 'cancelled') -and $_.name -cmatch "(^| / )$templateName$")
        })
        $found = @($Artifacts | Where-Object { $_.name -ceq $name })
        $part = [ordered]@{
            jobId = $null; jobName = $null; runnerId = $null; invocationId = $null; jobOutcome = 'missing'
            validation = 'incomplete'; report = $null; phasesMs = @{}; notes = @{}; profile = $null; lifecycle = $null
            evidence = @{ state = 'missing'; artifactId = $null; digest = $null; expiresAt = $null
                url = "https://github.com/$FilesystemHealthRepository/actions/runs/$RunId/attempts/$RunAttempt" }
        }
        $Sample.rich[$worker] = $part
        if ($workerJobs.Count -eq 1) {
            $part.jobId = $workerJobs[0].id; $part.runnerId = $workerJobs[0].runner_id; $part.jobOutcome = $workerJobs[0].conclusion
            $part.jobName = $workerJobs[0].name
            $part.validation = switch -CaseSensitive ($part.jobOutcome) {
                'failure' { 'failure' }; 'cancelled' { 'cancelled' }; 'skipped' { 'skipped' }; default { 'incomplete' }
            }
        } else { $Sample.diagnostics += "Missing/duplicate rich $worker job for this exact sample." }
        if ($found.Count -ne 1) {
            $Sample.diagnostics += "Missing/duplicate rich $worker artifact for this exact sample."
        } else {
            $artifact = $found[0]
            $part.evidence.artifactId = $artifact.id; $part.evidence.digest = $artifact.digest
            $part.evidence.expiresAt = $artifact.expires_at
            if ($artifact.expired) { $part.evidence.state = 'expired' }
            elseif ($artifact.workflow_run.id -ne $RunId -or $artifact.workflow_run.head_sha -cne $Collection.head_sha) {
                $part.evidence.state = 'rejected'; $Sample.diagnostics += "Rich $worker artifact controller identity differs."
            } else {
                $downloadDirectory = Join-Path $Directory "$worker-$([guid]::NewGuid().ToString('N'))"
                & gh run download $RunId --repo $FilesystemHealthRepository --name $name --dir $downloadDirectory
                if ($LASTEXITCODE -ne 0) {
                    $part.evidence.state = 'unavailable'; $Sample.diagnostics += "Rich $worker artifact download failed."
                } else {
                    $part.evidence.state = 'available'
                    try {
                        $report = Get-Content (Join-Path $downloadDirectory 'result.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String -Depth 20
                        if ($report.revision -cne $Revision -or $report.expectedRevision -cne $Revision -or
                            $report.runId -cne $RunId -or $report.runAttempt -cne $RunAttempt -or
                            $report.cohortId -cne $CohortId -or $report.sample -ne $Sample.number -or
                            $report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$') {
                            throw 'Rich result belongs to another candidate/cohort/sample/run/attempt.'
                        }
                        $part.report = $report; $part.invocationId = $report.runDirectory.Substring(4)
                        $directories[$worker] = $downloadDirectory
                        $run = Join-Path $downloadDirectory $report.runDirectory
                        if ($worker -ne 'union') {
                            Assert-HealthNumber $report.executionSeconds "$worker executionSeconds"
                            $limit = if ($worker -ceq 'prefix') { $policy.prefixSafetySeconds } else { $policy.tailSafetySeconds }
                            if ($report.outcome -ceq 'failure' -or $report.executionSeconds -gt $limit) { $part.validation = 'failure' }
                            $log = Join-Path $run 'ui-scale.log'
                            if (Test-Path -LiteralPath $log) {
                                $part.phasesMs = Read-RichCohortPhases @(Get-Content $log)
                            }
                            $files = if ($worker -eq 'prefix') {
                                @{
                                    'note-normal-executable' = 'scale-mutation-save-prefix-ui-sqlite-note-latency-rich-normal-executable.json'
                                    'note-background-export' = 'scale-mutation-save-prefix-ui-sqlite-note-latency-rich-background-export.json'
                                    profile = 'scale-mutation-save-prefix-ui-sqlite-rich-profile-latency-background-export.json'
                                    lifecycle = 'scale-mutation-save-prefix-ui-sqlite-rich-export-lifecycle.json'
                                    handoff = 'handoff\handoff.json'
                                }
                            } else {
                                @{ profile = 'scale-mutation-delivery-tail-ui-sqlite-rich-profile-latency-normal-executable.json'
                                    receipt = 'handoff-received.json' }
                            }
                            foreach ($key in $files.Keys) {
                                $file = Join-Path $run $files[$key]
                                if (!(Test-Path -LiteralPath $file)) { $Sample.diagnostics += "Rich $worker $key evidence missing."; continue }
                                $data = if ($key -in @('handoff', 'receipt')) {
                                    Get-Content $file -Raw | ConvertFrom-Json -AsHashtable -DateKind String -Depth 20
                                } else { Read-UiScaleEvidenceIdentity $run $files[$key] $Revision $RunId $RunAttempt -AsHashtable }
                                if ($key.StartsWith('note-')) { $part.notes[$key.Substring(5)] = $data }
                                elseif ($key -in @('handoff', 'receipt')) { $Sample.rich[$key] = $data }
                                else { $part[$key] = $data }
                            }
                            $null = Get-UiScaleWorkerResult $downloadDirectory $worker $Revision $RunId $RunAttempt $CohortId $Sample.number $ExpectedPolicyVersion
                        } elseif ($report.suite -cne 'ui-scale' -or $report.verificationMode -cne 'isolated-workers' -or
                            $report.outcome -cne 'success') { throw 'Original strict union did not pass.' }
                        if ($workerJobs.Count -eq 1 -and $workerJobs[0].status -ceq 'completed' -and $workerJobs[0].conclusion -ceq 'success') {
                            if ($part.jobId -gt 0 -and $part.runnerId -gt 0) { $part.validation = 'success' }
                            else { $Sample.diagnostics += "Rich $worker lacks a real hosted worker/job identity." }
                        }
                    } catch {
                        $part.evidence.state = 'rejected'
                        $Sample.diagnostics += "Rich $worker evidence rejected: $($_.Exception.Message)"
                    }
                }
            }
        }
        $outcomes += $part.validation
    }
    if ($directories.Count -eq 3) {
        try {
            Assert-UiScaleUnion $directories.prefix $directories.tail $Revision $RunId $RunAttempt $CohortId $Sample.number $ExpectedPolicyVersion
            $union = Join-Path $directories.union $Sample.rich.union.report.runDirectory
            Assert-UiScaleUnion (Join-Path $union 'prefix') (Join-Path $union 'tail') $Revision $RunId $RunAttempt $CohortId $Sample.number $ExpectedPolicyVersion
            if (@($outcomes | Where-Object { $_ -cne 'success' }).Count -eq 0) { $Sample.rich.validation = 'success' }
        } catch { $Sample.diagnostics += "Complete rich union rejected: $($_.Exception.Message)" }
    }
    if ($Sample.rich.validation -cne 'success') {
        $Sample.validation = if ($outcomes -ccontains 'failure') { 'failure' }
            elseif ($outcomes -ccontains 'cancelled') { 'cancelled' }
            elseif ($outcomes -ccontains 'skipped') { 'skipped' } else { 'incomplete' }
    }
    try { $Sample.rich.seriesKey = Get-RichCohortSeriesKey $Sample $ExpectedPolicyVersion }
    catch { $Sample.diagnostics += "Complete cohort comparison unavailable: $($_.Exception.Message)" }
}

function Assert-RichCohortSample($Sample, $Record, $Seen, [string]$ExpectedPolicyVersion = 'rich-scale-original-v1') {
    $policy = Get-ApprovedTimingPolicy $ExpectedPolicyVersion
    if ($Sample.rich.validation -cnotin @('success', 'incomplete') -or
        ($Sample.validation -ceq 'success' -and $Sample.rich.validation -cne 'success')) {
        throw 'Complete cohort acceptance lacks the original strict rich union.'
    }
    foreach ($worker in @('prefix', 'tail', 'union')) {
        $part = $Sample.rich[$worker]
        foreach ($field in @('jobId', 'runnerId', 'invocationId')) {
            if ($null -ne $part[$field]) {
                if ($field -eq 'invocationId') {
                    if ($part[$field] -cnotmatch '^[0-9a-f]{32}$') { throw 'Invalid rich invocation identity.' }
                } else {
                    Assert-HealthNumber $part[$field] "rich $field"
                    if ($Sample.validation -ceq 'success' -and $part[$field] -le 0) { throw 'Complete rich evidence lacks a real hosted worker/job.' }
                }
                $key = "$field-$($part[$field])"
                if ($Seen.ContainsKey($key) -and $Sample.validation -ceq 'success') { throw "Independent rich sample $($Sample.number) $worker reused $key." }
                $Seen[$key] = $true
            } elseif ($Sample.validation -ceq 'success') { throw 'Complete rich evidence lacks independent hosted identity.' }
        }
        if ($part.evidence.state -cnotin @('missing', 'available', 'unavailable', 'expired', 'rejected') -or
            $part.evidence.url -cne $Sample.evidence.url) { throw 'Invalid rich evidence provenance.' }
        if ($null -ne $part.evidence.expiresAt) { Assert-HealthDate $part.evidence.expiresAt 'rich expiry' }
        if ($null -ne $part.report) {
            $report = $part.report
            if ($report.revision -cne $Record.revision -or $report.expectedRevision -cne $Record.revision -or
                $report.runId -cne $Record.cohortRunId -or $report.runAttempt -cne $Record.cohortRunAttempt -or
                $report.cohortId -cne $Record.cohortId -or $report.sample -ne $Sample.number) { throw 'Historical rich sample binding differs.' }
            if ($worker -ne 'union' -and ($part.evidence.state -ceq 'available' -or $Sample.validation -ceq 'success')) {
                Assert-HealthNumber $report.executionSeconds 'rich whole execution'
            }
        }
        $limit = if ($worker -ceq 'prefix') { $policy.prefixSafetySeconds } else { $policy.tailSafetySeconds }
        if ($Sample.validation -ceq 'success' -and ($part.validation -cne 'success' -or $part.jobOutcome -cne 'success' -or
            $null -eq $part.report -or $part.report.outcome -cne 'success' -or
            ($worker -ne 'union' -and ($part.report.executionSeconds -gt $limit -or $part.report.diagnosticOnly -or
                $part.report.ceilingSeconds -ne $limit -or $part.report.observationSeconds -ne $limit)))) {
            throw 'A failed, diagnostic or over-budget rich worker cannot qualify.'
        }
        if ($Sample.validation -ceq 'success' -and $worker -ne 'union') {
            $report = $part.report | ConvertTo-Json -Depth 20 | ConvertFrom-Json -Depth 20
            $plan = Assert-UiScaleWorkerReport $report $worker $Record.revision $Record.cohortRunId $Record.cohortRunAttempt $Record.cohortId $Sample.number $ExpectedPolicyVersion
            if ($part.report.Contains('cleanup') -and $part.report.cleanup.warning) {
                $proof = $part.report.cleanup.proof | ConvertFrom-Json -AsHashtable -DateKind String -Depth 40
                if ((ConvertTo-HealthCanonicalValue $part.phasesMs | ConvertTo-Json -Depth 30 -Compress) -cne
                    (ConvertTo-HealthCanonicalValue $proof.phasesMs | ConvertTo-Json -Depth 30 -Compress)) {
                    throw 'Historical rich phases differ from signed verification.'
                }
                $evidence = $proof.evidence
                $profilePhase = if ($worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }
                if ((ConvertTo-HealthCanonicalValue $part.profile | ConvertTo-Json -Depth 30 -Compress) -cne
                    (ConvertTo-HealthCanonicalValue $evidence["profile-$profilePhase"] | ConvertTo-Json -Depth 30 -Compress)) {
                    throw 'Historical rich profile differs from signed verification.'
                }
                if ($worker -ceq 'prefix') {
                    if ((ConvertTo-HealthCanonicalValue $part.lifecycle | ConvertTo-Json -Depth 30 -Compress) -cne
                        (ConvertTo-HealthCanonicalValue $evidence.lifecycle | ConvertTo-Json -Depth 30 -Compress) -or
                        (ConvertTo-HealthCanonicalValue $Sample.rich.handoff | ConvertTo-Json -Depth 30 -Compress) -cne
                        (ConvertTo-HealthCanonicalValue $proof.handoff | ConvertTo-Json -Depth 30 -Compress)) {
                        throw 'Historical lifecycle/handoff differs from signed verification.'
                    }
                    foreach ($phase in @('normal-executable', 'background-export')) {
                        if ((ConvertTo-HealthCanonicalValue $part.notes[$phase] | ConvertTo-Json -Depth 30 -Compress) -cne
                            (ConvertTo-HealthCanonicalValue $evidence["note-$phase"] | ConvertTo-Json -Depth 30 -Compress)) {
                            throw 'Historical rich notes differ from signed verification.'
                        }
                    }
                } elseif ((ConvertTo-HealthCanonicalValue $Sample.rich.receipt | ConvertTo-Json -Depth 30 -Compress) -cne
                    (ConvertTo-HealthCanonicalValue $proof.handoff | ConvertTo-Json -Depth 30 -Compress)) {
                    throw 'Historical Join receipt differs from signed verification.'
                }
            }
            $occurrences = @{}
            foreach ($phase in $plan.phases[$worker]) {
                if (!$occurrences.ContainsKey($phase)) { $occurrences[$phase] = 0 }
                $occurrences[$phase]++
                $key = if ($occurrences[$phase] -eq 1) { $phase } else { "$phase#$($occurrences[$phase])" }
                if (!$part.phasesMs.ContainsKey($key) -or $part.phasesMs[$key].outcome -cne 'Passed') {
                    throw 'Historical complete rich execution lacks an original phase.'
                }
            }
        }
    }
    if ($Sample.rich.validation -ceq 'success') {
        $handoff = $Sample.rich.handoff
        $receipt = $Sample.rich.receipt
        if ($handoff.binding.revision -cne $Record.revision -or $handoff.binding.runId -cne $Record.cohortRunId -or
            $handoff.binding.runAttempt -cne $Record.cohortRunAttempt -or
            $handoff.sourceRunDirectory -cne $Sample.rich.prefix.report.runDirectory -or
            $receipt.sourceRunDirectory -cne $handoff.sourceRunDirectory -or $receipt.outcome -cne 'Passed' -or
            $receipt.receiverWorkspace -ceq $handoff.sourceWorkspace -or
            $receipt.handoffSha256 -cne $Sample.rich.prefix.report.handoffSha256 -or
            $receipt.handoffSha256 -cne $Sample.rich.tail.report.handoffSha256) { throw 'Historical rich logical handoff identity differs.' }
        foreach ($key in @('binding', 'workspaceId', 'head', 'snapshotSha256', 'payloadSha256', 'preferencesSha256', 'publicStateSha256', 'selection')) {
            if (($receipt[$key] | ConvertTo-Json -Depth 12 -Compress) -cne ($handoff[$key] | ConvertTo-Json -Depth 12 -Compress)) {
                throw 'Historical rich snapshot/receipt differs.'
            }
        }
        $evidence = @{
            'note-normal-executable' = $Sample.rich.prefix.notes.'normal-executable'
            'note-background-export' = $Sample.rich.prefix.notes.'background-export'
            'profile-background-export' = $Sample.rich.prefix.profile
            'profile-normal-executable' = $Sample.rich.tail.profile
            lifecycle = $Sample.rich.prefix.lifecycle
        }
        foreach ($key in @($evidence.Keys)) {
            $evidence[$key] = $evidence[$key] | ConvertTo-Json -Depth 20 | ConvertFrom-Json -Depth 20
        }
        $handoff = $handoff | ConvertTo-Json -Depth 20 | ConvertFrom-Json -Depth 20
        Assert-UiScaleRichEvidence $evidence $handoff $Record.revision $Record.cohortRunId $Record.cohortRunAttempt $ExpectedPolicyVersion
    }
    if ($null -ne $Sample.rich.seriesKey -and $Sample.rich.seriesKey -cne (Get-RichCohortSeriesKey $Sample $ExpectedPolicyVersion)) {
        throw 'Historical complete cohort comparison configuration differs.'
    }
}
