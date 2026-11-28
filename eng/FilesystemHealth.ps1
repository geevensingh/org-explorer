$FilesystemHealthRepository = 'geevensingh/org-explorer'
$FilesystemHealthBranch = 'ci-health-data'

function Invoke-HealthApi([string]$Endpoint, [string[]]$Arguments = @()) {
    $text = & gh api "repos/$FilesystemHealthRepository/$Endpoint" @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Filesystem observation GitHub read failed: $Endpoint" }
    return (($text -join "`n") | ConvertFrom-Json -AsHashtable -DateKind String -Depth 30)
}

function Invoke-HealthGit([string]$Directory, [string[]]$Arguments, [string]$InputText) {
    $output = if ($PSBoundParameters.ContainsKey('InputText')) {
        $InputText | & git "--git-dir=$Directory" @Arguments 2>&1
    } else { & git "--git-dir=$Directory" @Arguments 2>&1 }
    if ($LASTEXITCODE -ne 0) { throw "Filesystem history Git operation failed ($($Arguments[0])): $($output -join ' ')" }
    return ($output -join "`n")
}

function Assert-HealthNumber($Value, [string]$Name, [switch]$AllowNegative) {
    if ($Value -isnot [long] -and $Value -isnot [int] -and $Value -isnot [double] -and $Value -isnot [decimal]) {
        throw "Missing or nonnumeric filesystem measurement: $Name"
    }
    if (![double]::IsFinite([double]$Value) -or (!$AllowNegative -and $Value -lt 0)) { throw "Invalid filesystem measurement: $Name" }
}

function Assert-HealthDate($Value, [string]$Name) {
    if ($Value -isnot [string] -or $Value -cnotmatch '^\d{4}-\d{2}-\d{2}T') { throw "Invalid filesystem timestamp: $Name" }
    $null = [DateTimeOffset]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Assert-HealthRecord($Record, [string]$Path) {
    Assert-HealthNumber $Record.schemaVersion 'schemaVersion'
    if ($Record.schemaVersion -ne 1 -or $Record.repository -cne $FilesystemHealthRepository -or
        $Record.lane -cne 'filesystem' -or $Record.revision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.runId -isnot [string] -or $Record.runId -cnotmatch '^[1-9][0-9]*$' -or
        $Record.runAttempt -isnot [string] -or $Record.runAttempt -cnotmatch '^[1-9][0-9]*$' -or
        $Record.authority -cne 'trusted-main-push' -or $Record.sourceEvent -cne 'push' -or
        $Record.sourceBranch -cne 'main' -or $Record.sourceRepository -cne $FilesystemHealthRepository -or
        $Record.workflowPath -cne '.github/workflows/ci.yml' -or
        $Path -cne "filesystem/$($Record.runId)/$($Record.runAttempt).json" -or
        $Record.validation -cnotin @('success', 'failure', 'incomplete', 'cancelled', 'skipped') -or
        $Record.timingHealth -cnotin @('observation', 'unbaselined', 'inconclusive')) {
        throw 'Invalid durable filesystem history schema/provenance.'
    }
    Assert-HealthDate $Record.observedAt 'observedAt'
    if ($null -ne $Record.measurements) {
        Assert-HealthDate $Record.measuredAt 'measuredAt'
        if ([DateTimeOffset]$Record.measuredAt -gt [DateTimeOffset]$Record.observedAt) {
            throw 'Historical measurement is later than its observation.'
        }
        Assert-HealthNumber $Record.measurements.executionSeconds 'executionSeconds'
        Assert-HealthNumber $Record.measurements.headroomSeconds 'headroomSeconds' -AllowNegative
        if ($Record.measurements.headroomSeconds -ne 660 - $Record.measurements.executionSeconds) {
            throw 'Historical headroom disagrees with the original policy.'
        }
        $names = @('restore', 'test')
        $steps = @($Record.measurements.steps)
        if ($steps.Count -gt 2) { throw 'Unexpected historical filesystem phases.' }
        for ($index = 0; $index -lt $steps.Count; $index++) {
            Assert-HealthNumber $steps[$index].seconds $steps[$index].name
            if ($steps[$index].name -cne $names[$index] -or $steps[$index].status -cnotin @('Passed', 'Failed')) {
                throw 'Invalid historical filesystem phase selection/outcome.'
            }
        }
        if ($Record.policyVersion -cne 'filesystem-original-v1') { throw 'Unsupported historical filesystem policy.' }
    } elseif ($Record.validation -ceq 'success') { throw 'Successful historical validation lacks measurements.' }
    if ($null -ne $Record.seriesKey) {
        $execution = @{ policyVersion = $Record.policyVersion; selector = 'Category!=Performance'; configuration = $Record.configuration }
        if ($Record.seriesKey -cne (Get-HealthSeriesKey $execution)) { throw 'Historical comparison configuration/hash disagrees.' }
    }
    if ($null -ne $Record.tests) {
        foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
            Assert-HealthNumber $Record.tests[$name] "tests.$name"
            if ($Record.tests[$name] % 1 -ne 0) { throw 'Fractional historical test count.' }
        }
        if ($Record.tests.total -ne $Record.tests.executed + $Record.tests.notExecuted -or
            $Record.tests.executed -ne $Record.tests.passed + $Record.tests.failed) {
            throw 'Historical test counters disagree.'
        }
    }
    if ($Record.validation -ceq 'success' -and ($null -eq $Record.tests -or
        $Record.tests.total -le 0 -or $Record.tests.failed -ne 0 -or $Record.tests.notExecuted -ne 0)) {
        throw 'Successful historical validation lacks complete passed tests.'
    }
    if ($Record.evidence.state -cnotin @('available', 'missing', 'expired', 'unavailable', 'rejected') -or
        $Record.evidence.url -cne "https://github.com/$FilesystemHealthRepository/actions/runs/$($Record.runId)/attempts/$($Record.runAttempt)") {
        throw 'Invalid historical evidence link/state.'
    }
    if ($null -ne $Record.evidence.expiresAt) { Assert-HealthDate $Record.evidence.expiresAt 'evidence.expiresAt' }
    if ($null -ne $Record.reference) {
        if ($Record.reference.revision -cnotmatch '^[0-9a-f]{40}$' -or $Record.reference.revision -ceq $Record.revision -or
            $Record.reference.kind -cne 'prior-compatible-observation') { throw 'Invalid or self-referencing historical comparison.' }
        Assert-HealthNumber $Record.reference.executionSeconds 'reference.executionSeconds'
        Assert-HealthNumber $Record.reference.deltaSeconds 'reference.deltaSeconds' -AllowNegative
        Assert-HealthNumber $Record.reference.ageSeconds 'reference.ageSeconds'
        if ($Record.reference.deltaSeconds -ne $Record.measurements.executionSeconds - $Record.reference.executionSeconds) {
            throw 'Historical comparison inputs disagree.'
        }
        if ($null -ne $Record.reference.deltaPercent) {
            Assert-HealthNumber $Record.reference.deltaPercent 'reference.deltaPercent' -AllowNegative
            if ($Record.reference.executionSeconds -le 0 -or
                $Record.reference.deltaPercent -ne 100 * $Record.reference.deltaSeconds / $Record.reference.executionSeconds) {
                throw 'Historical percentage comparison inputs disagree.'
            }
        } elseif ($Record.reference.executionSeconds -gt 0) { throw 'Historical comparison lacks its supported percentage.' }
    }
    if ($null -ne $Record.variability) {
        foreach ($name in @('count', 'minimumSeconds', 'maximumSeconds')) {
            Assert-HealthNumber $Record.variability[$name] "variability.$name"
        }
        if ($Record.variability.count -le 1 -or $Record.variability.minimumSeconds -gt $Record.variability.maximumSeconds) {
            throw 'Invalid historical descriptive range.'
        }
    }
    if ($null -ne $Record.reliability) {
        $sum = 0
        foreach ($name in @('success', 'failure', 'incomplete', 'cancelled', 'skipped')) {
            Assert-HealthNumber $Record.reliability[$name] "reliability.$name"
            $sum += $Record.reliability[$name]
        }
        Assert-HealthNumber $Record.reliability.attempts 'reliability.attempts'
        Assert-HealthNumber $Record.reliability.failureRatePercent 'reliability.failureRatePercent'
        if ($Record.reliability.attempts -le 0 -or $sum -ne $Record.reliability.attempts -or
            $Record.reliability.failureRatePercent -ne 100 * $Record.reliability.failure / $Record.reliability.attempts) {
            throw 'Historical reliability inputs disagree.'
        }
    }
}

function Get-HealthRecordPath($Record, [hashtable]$RecordTypes = @{}) {
    if ($Record.Contains('recordType')) {
        if (!$RecordTypes.ContainsKey($Record.recordType)) { throw 'Unregistered health record type.' }
        $type = $RecordTypes[$Record.recordType]
        $path = & $type.GetPath $Record
        Assert-RegisteredHealthRecord $Record $path $type
        return $path
    }
    $path = "filesystem/$($Record.runId)/$($Record.runAttempt).json"
    Assert-HealthRecord $Record $path
    return $path
}

function Assert-RegisteredHealthRecord($Record, [string]$Path, $Type) {
    if ($Type.PathPattern -isnot [string] -or !$Type.PathPattern.StartsWith('^') -or
        !$Type.PathPattern.EndsWith('$') -or $Type.GetPath -isnot [scriptblock] -or
        $Type.Validate -isnot [scriptblock] -or $Path -cnotmatch $Type.PathPattern -or
        $Path -cne (& $Type.GetPath $Record)) {
        throw 'Invalid registered health namespace/identity.'
    }
    & $Type.Validate $Record $Path
}

function Get-FilesystemMeasurement([string]$Directory, $Run) {
    $execution = Get-Content -LiteralPath (Join-Path $Directory 'execution.json') -Raw |
        ConvertFrom-Json -AsHashtable -DateKind String -Depth 20
    Assert-HealthNumber $execution.schemaVersion 'schemaVersion'
    Assert-HealthNumber $execution.ceilingSeconds 'ceilingSeconds'
    Assert-HealthNumber $execution.targetSeconds 'targetSeconds'
    if ($execution.schemaVersion -ne 1 -or $execution.lane -cne 'filesystem' -or
        $execution.revision -cne $Run.head_sha -or $execution.runId -cne [string]$Run.id -or
        $execution.runAttempt -cne [string]$Run.run_attempt -or $execution.complete -isnot [bool] -or
        $execution.selector -cne 'Category!=Performance' -or
        $execution.policyVersion -cne 'filesystem-original-v1' -or
        $execution.ceilingSeconds -ne 660 -or $execution.targetSeconds -ne 300) {
        throw 'Filesystem execution is stale, unbound or uses an unsupported original policy/selection.'
    }
    Assert-HealthNumber $execution.executionSeconds 'executionSeconds'
    Assert-HealthDate $execution.startedAt 'startedAt'
    Assert-HealthDate $execution.finishedAt 'finishedAt'
    if ([DateTimeOffset]$execution.finishedAt -lt [DateTimeOffset]$execution.startedAt -or
        $execution.outcome -cnotin @('success', 'failure', 'timed_out')) { throw 'Invalid terminal filesystem outcome/clocks.' }
    $steps = @($execution.steps)
    $names = @('restore', 'test')
    if ($steps.Count -gt 2) { throw 'Unexpected filesystem commands.' }
    for ($index = 0; $index -lt $steps.Count; $index++) {
        Assert-HealthNumber $steps[$index].seconds $steps[$index].name
        if ($steps[$index].name -cne $names[$index] -or $steps[$index].status -cnotin @('Passed', 'Failed')) {
            throw 'Unexpected filesystem command selection/outcome.'
        }
    }
    $diagnostics = @()
    if ($execution.ContainsKey('configurationErrors')) { $diagnostics += @($execution.configurationErrors) }
    $validation = if ($execution.outcome -ceq 'failure') { 'failure' } `
        elseif (!$execution.complete -or $execution.outcome -cne 'success' -or
            $steps.Count -ne 2 -or @($steps | Where-Object { $_.status -cne 'Passed' }).Count) { 'incomplete' } else { 'success' }
    $tests = $null
    try { $tests = Read-FilesystemTests $Directory }
    catch {
        $diagnostics += "TRX evidence rejected: $($_.Exception.Message)"
        if ($validation -ceq 'success') { $validation = 'incomplete' }
    }
    if ($null -ne $tests) {
        if ($tests.failed -gt 0) { $validation = 'failure' }
        elseif ($tests.notExecuted -gt 0 -or $tests.summary -cne 'Completed') { $validation = 'incomplete' }
    }
    if ($validation -cne 'success') { $diagnostics += 'Required execution failed or did not complete; timings are observations, not acceptance.' }
    return @{
        execution = $execution; tests = $tests; validation = $validation; diagnostics = $diagnostics
        measurements = @{
            executionSeconds = $execution.executionSeconds; steps = $steps
            headroomSeconds = 660 - $execution.executionSeconds
            startedAt = $execution.startedAt; finishedAt = $execution.finishedAt
        }
    }
}

function Read-FilesystemTests([string]$Directory) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File)
    if ($files.Count -ne 1) { throw 'Expected exactly one filesystem TRX result.' }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($files[0].FullName, $settings)
    try { $xml = [xml]::new(); $xml.Load($reader) } finally { $reader.Dispose() }
    $counters = $xml.TestRun.ResultSummary.Counters
    $tests = @{}
    foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
        $text = $counters.GetAttribute($name)
        if ($text -cnotmatch '^(0|[1-9][0-9]*)$') { throw "Invalid filesystem TRX counter: $name" }
        $tests[$name] = [long]$text
    }
    $results = @($xml.TestRun.Results.UnitTestResult)
    if ($tests.total -le 0 -or $tests.total -ne $tests.executed + $tests.notExecuted -or
        $tests.executed -ne $tests.passed + $tests.failed -or $results.Count -ne $tests.total -or
        @($results | Where-Object { $_.outcome -ceq 'Passed' }).Count -ne $tests.passed -or
        @($results | Where-Object { $_.outcome -ceq 'Failed' }).Count -ne $tests.failed -or
        @($results | Where-Object { $_.outcome -ceq 'NotExecuted' }).Count -ne $tests.notExecuted) {
        throw 'Filesystem TRX counters and individual outcomes disagree.'
    }
    $tests.summary = $xml.TestRun.ResultSummary.outcome
    $tests.failedNames = @($results | Where-Object { $_.outcome -ceq 'Failed' } | ForEach-Object { $_.testName })
    $tests.skippedNames = @($results | Where-Object { $_.outcome -ceq 'NotExecuted' } | ForEach-Object { $_.testName })
    $workloads = @{}
    foreach ($result in $results) {
        $name = if ($result.testName -cmatch '^OrgExplorer\.(Core|Persistence)\.Tests\.(?<workload>[A-Za-z0-9_]+)\.') {
            $Matches.workload
        } else { 'unclassified' }
        if (!$workloads.ContainsKey($name)) {
            $workloads[$name] = @{ name = $name; total = 0; passed = 0; failed = 0; skipped = 0
                seconds = 0.0; unavailableDurations = 0
                durationScope = 'sum of test-case durations; can overlap, not lane wall time or internal-operation latency' }
        }
        $group = $workloads[$name]
        $group.total++
        switch -CaseSensitive ($result.outcome) {
            'Passed' { $group.passed++ }
            'Failed' { $group.failed++ }
            'NotExecuted' { $group.skipped++ }
        }
        $duration = [TimeSpan]::Zero
        if ([TimeSpan]::TryParse($result.GetAttribute('duration'), [Globalization.CultureInfo]::InvariantCulture, [ref]$duration) -and
            $duration.TotalSeconds -ge 0) { $group.seconds += $duration.TotalSeconds }
        else { $group.unavailableDurations++ }
    }
    foreach ($group in $workloads.Values) { if ($group.unavailableDurations -gt 0) { $group.seconds = $null } }
    $tests.workloads = @($workloads.Values | Sort-Object name)
    return $tests
}

function ConvertTo-HealthCanonicalValue($Value) {
    if ($Value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object -CaseSensitive)) { $ordered[$key] = ConvertTo-HealthCanonicalValue $Value[$key] }
        return $ordered
    }
    if ($Value -is [Collections.IList]) { return ,@($Value | ForEach-Object { ConvertTo-HealthCanonicalValue $_ }) }
    return $Value
}

function Assert-HealthNativeBinding($Native, $Binding, [string]$Selector) {
    if ($Native -isnot [Collections.IDictionary] -or $Binding -isnot [Collections.IDictionary]) {
        throw "Comparison configuration unavailable: $Selector validated native binding"
    }
    $fields = [ordered]@{ version = 'nativeVersion'; sourceId = 'nativeSourceId'; loadedSha256 = 'loadedNativeSha256' }
    foreach ($field in $fields.Keys) {
        $actual = $Binding[$fields[$field]]
        if ($actual -isnot [string] -or [string]::IsNullOrWhiteSpace($actual) -or
            ($field -ceq 'loadedSha256' -and $actual -cnotmatch '^[0-9A-F]{64}$') -or $Native[$field] -cne $actual) {
            throw "Comparison configuration unavailable: $Selector native.$field differs from validated handoff/receipt binding"
        }
    }
}

function Get-RetainedHealthSeriesKey($Execution) {
    if ($Execution.configuration -isnot [Collections.IDictionary]) {
        throw 'Comparison configuration unavailable: resolved configuration'
    }
    foreach ($name in @('workload', 'testTree', 'fixtureTree', 'sdk', 'os', 'architecture', 'image', 'imageVersion', 'powershell', 'setup')) {
        if (!$Execution.configuration.ContainsKey($name) -or
            $Execution.configuration[$name] -isnot [string] -or
            [string]::IsNullOrWhiteSpace($Execution.configuration[$name])) {
            throw "Comparison configuration unavailable: $name"
        }
    }
    if ($Execution.configuration.testTree -cnotmatch '^[0-9a-f]{40}$' -or $Execution.configuration.fixtureTree -cnotmatch '^[0-9a-f]{40}$' -or
        $Execution.configuration.workload -cnotin @('OrgExplorer.Persistence.Tests', 'OrgExplorer.Core.Tests',
            'OrgExplorer.Desktop.Smoke')) { throw 'Invalid validation workload identity.' }
    Assert-HealthNumber $Execution.configuration.processors 'processors'
    if ($Execution.configuration.processors -le 0 -or
        $Execution.configuration.runtimes -isnot [Collections.IList] -or !$Execution.configuration.runtimes.Count -or
        @($Execution.configuration.runtimes | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count) {
        throw 'Comparison requires resolved runtime/processor configuration.'
    }
    $ordered = [ordered]@{ policy = $Execution.policyVersion; selector = $Execution.selector }
    foreach ($key in @($Execution.configuration.Keys | Sort-Object -CaseSensitive)) {
        $ordered[$key] = ConvertTo-HealthCanonicalValue $Execution.configuration[$key]
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($ordered | ConvertTo-Json -Compress -Depth 10))
    return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
}

function Get-HealthSeriesKey($Execution) {
    $key = Get-RetainedHealthSeriesKey $Execution
    if ($Execution.selector -clike '--ui-scale-*') {
        $display = $Execution.configuration['display']
        if ($display -isnot [Collections.IDictionary] -or
            ($display['schemaVersion'] -isnot [int] -and $display['schemaVersion'] -isnot [long]) -or
            $display['schemaVersion'] -ne 1 -or $display['command'] -isnot [string] -or
            [string]::IsNullOrWhiteSpace($display['command']) -or
            $display['state'] -isnot [Collections.IDictionary]) {
            throw "Comparison configuration unavailable: $($Execution.selector) actual display state"
        }
        $state = $display['state']
        $mode = $state['Mode']
        if ($mode -isnot [Collections.IDictionary] -or $state['Device'] -isnot [string] -or
            [string]::IsNullOrWhiteSpace($state['Device'])) {
            throw "Comparison configuration unavailable: $($Execution.selector) actual display state"
        }
        foreach ($field in @('MonitorWidth', 'MonitorHeight', 'WorkWidth', 'WorkHeight', 'Dpi',
            'MaximumTrackWidth', 'MaximumTrackHeight', 'Width', 'Height', 'BitsPerPixel', 'Frequency')) {
            $value = if ($field -cin @('Width', 'Height', 'BitsPerPixel', 'Frequency')) {
                $mode[$field]
            } else { $state[$field] }
            if (($value -isnot [int] -and $value -isnot [long]) -or $value -le 0) {
                throw "Comparison configuration unavailable: $($Execution.selector) display.$field"
            }
        }
        $native = $Execution.configuration['native']
        if ($native -isnot [Collections.IDictionary]) {
            throw "Comparison configuration unavailable: $($Execution.selector) loaded native identity"
        }
        foreach ($field in @('version', 'sourceId', 'loadedSha256')) {
            if ($native[$field] -isnot [string] -or [string]::IsNullOrWhiteSpace($native[$field])) {
                throw "Comparison configuration unavailable: $($Execution.selector) native.$field"
            }
        }
        if ($native.loadedSha256 -cnotmatch '^[0-9a-fA-F]{64}$') {
            throw "Comparison configuration unavailable: $($Execution.selector) native.loadedSha256"
        }
    }
    return $key
}

function Set-FilesystemComparison($Record, $History) {
    if ($null -eq $Record.seriesKey -or $null -eq $Record.measurements) {
        $Record.timingHealth = 'inconclusive'
        return
    }
    $prior = @($History.records | Where-Object { !$_.Contains('recordType') -and $_.lane -ceq 'filesystem' })
    $compatible = @($prior | Where-Object {
        $_.validation -ceq 'success' -and $_.seriesKey -ceq $Record.seriesKey -and
        $_.revision -cne $Record.revision -and $_.measurements.executionSeconds -le 660
    } | Sort-Object @{ Expression = { [DateTimeOffset]$_.measuredAt }; Descending = $true },
        @{ Expression = { [long]$_.runId }; Descending = $true },
        @{ Expression = { [int]$_.runAttempt }; Descending = $true })
    if (!$compatible.Count) {
        $Record.timingHealth = if ($prior.Count) { 'inconclusive' } else { 'unbaselined' }
        $Record.diagnostics += 'No prior compatible non-self observation is available. This is not a release prerequisite.'
        return
    }
    $reference = $compatible[0]
    $delta = $Record.measurements.executionSeconds - $reference.measurements.executionSeconds
    $percent = if ($reference.measurements.executionSeconds -gt 0) {
        100 * $delta / $reference.measurements.executionSeconds
    } else { $null }
    $Record.reference = @{
        kind = 'prior-compatible-observation'; revision = $reference.revision
        runId = $reference.runId; runAttempt = $reference.runAttempt; measuredAt = $reference.measuredAt
        ageSeconds = ([DateTimeOffset]$Record.observedAt - [DateTimeOffset]$reference.measuredAt).TotalSeconds
        executionSeconds = $reference.measurements.executionSeconds; steps = $reference.measurements.steps
        deltaSeconds = $delta; deltaPercent = $percent
        evidenceState = if ([DateTimeOffset]$reference.evidence.expiresAt -le [DateTimeOffset]$Record.observedAt) {
            'expired'
        } else { $reference.evidence.state }
        evidenceUrl = $reference.evidence.url
    }
    $Record.timingHealth = 'observation'
    $Record.variability = if ($compatible.Count -gt 1) {
        $range = $compatible | ForEach-Object { $_.measurements.executionSeconds } | Measure-Object -Minimum -Maximum
        @{ count = $compatible.Count; minimumSeconds = $range.Minimum; maximumSeconds = $range.Maximum
            scope = 'prior compatible observations across revisions, not calibrated noise' }
    } else { $null }
}

function Read-HealthHistoryJson([string]$Directory, [string]$Tip, [string[]]$Paths) {
    if (!('HealthHistoryJson' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
public static class HealthHistoryJson {
    public static string[] Split(string text) {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text),
            new JsonReaderOptions { AllowMultipleValues = true, MaxDepth = 30 });
        var values = new List<string>();
        while (reader.Read()) {
            using var document = JsonDocument.ParseValue(ref reader);
            values.Add(document.RootElement.GetRawText());
        }
        return values.ToArray();
    }
}
'@
    }
    $records = @()
    for ($offset = 0; $offset -lt $Paths.Count; $offset += 64) {
        $last = [Math]::Min($offset + 63, $Paths.Count - 1)
        $chunk = @($Paths[$offset..$last])
        $objects = @($chunk | ForEach-Object { "${Tip}:$_" })
        $text = Invoke-HealthGit $Directory (@('--no-pager', 'show') + $objects)
        $json = @([HealthHistoryJson]::Split($text))
        if ($json.Count -ne $chunk.Count) { throw 'History blob/JSON cardinality disagrees.' }
        foreach ($value in $json) { $records += ($value | ConvertFrom-Json -AsHashtable -DateKind String -Depth 30) }
    }
    return $records
}

function Open-HealthHistory([string]$Directory, [string]$Remote, [switch]$AllowLocalFixture,
    [hashtable]$RecordTypes = @{}) {
    if (!$AllowLocalFixture -and $Remote -cne "https://github.com/$FilesystemHealthRepository.git") {
        throw 'Authoritative history requires this repository HTTPS remote; local remotes are isolated fixtures only.'
    }
    if ($AllowLocalFixture -and !(Test-Path -LiteralPath $Remote -PathType Container)) {
        throw 'Local history fixtures require an existing isolated directory, not a network fallback.'
    }
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $null = Invoke-HealthGit $Directory @('init', '--bare', '--quiet')
    $null = Invoke-HealthGit $Directory @('remote', 'add', 'origin', $Remote)
    $refs = & git "--git-dir=$Directory" -c credential.helper= -c 'credential.helper=!gh auth git-credential' `
        ls-remote --exit-code origin "refs/heads/$FilesystemHealthBranch" 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -notin @(0, 2)) { throw "Filesystem history read failed: $($refs -join ' ')" }
    $tip = $null
    $records = @()
    if ($exitCode -eq 0) {
        $null = Invoke-HealthGit $Directory @('-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential',
            'fetch', '--quiet', 'origin', "refs/heads/${FilesystemHealthBranch}:refs/heads/history")
        $tip = Invoke-HealthGit $Directory @('rev-parse', 'refs/heads/history')
        $paths = @()
        foreach ($entry in @((Invoke-HealthGit $Directory @('ls-tree', '-r', $tip)) -split "`n")) {
            if ($entry -cnotmatch '^100644 blob [0-9a-f]{40}\t(?<path>.*)$') { throw 'History must contain regular JSON data files, not links or executable entries.' }
            $paths += $Matches.path
        }
        $parsed = @(Read-HealthHistoryJson $Directory $tip $paths)
        for ($index = 0; $index -lt $paths.Count; $index++) {
            $path = $paths[$index]
            $record = $parsed[$index]
            if ($path -cmatch '^filesystem/[1-9][0-9]*/[1-9][0-9]*\.json$') {
                Assert-HealthRecord $record $path
            } else {
                $matches = @($RecordTypes.Keys | Where-Object { $path -cmatch $RecordTypes[$_].PathPattern })
                if ($matches.Count -ne 1 -or $record.recordType -cne $matches[0]) {
                    throw 'Unexpected data-branch content; only trusted controller registrations can validate it.'
                }
                Assert-RegisteredHealthRecord $record $path $RecordTypes[$matches[0]]
            }
            $records += $record
        }
    }
    $history = @{ directory = $Directory; tip = $tip; records = $records; remote = $Remote
        localFixture = [bool]$AllowLocalFixture; recordTypes = $RecordTypes; observationWriterAuthorized = $false }
    foreach ($type in $RecordTypes.Values) {
        if ($type.ContainsKey('ValidateHistory')) { & $type.ValidateHistory $history }
    }
    return $history
}

function Publish-HealthRecord($History, $Record) {
    return Publish-HealthRecords $History @($Record)
}

function Publish-HealthRecords($History, $Records) {
    if (!@($Records).Count) { throw 'History publication requires nonempty records.' }
    $callerHistory = $History
    $pending = @{}
    foreach ($record in $Records) {
        $registeredObservation = $record.Contains('recordType') -and $History.recordTypes.ContainsKey($record.recordType) -and
            $History.recordTypes[$record.recordType].ContainsKey('ObservationWriterAllowed') -and
            $History.recordTypes[$record.recordType].ObservationWriterAllowed -eq $true
        if ($record.authority -cne 'trusted-main-push' -and
            ($record.authority -cne 'trusted-controller-observation' -or
                ($record.recordType -cne 'lane-observation' -and !$registeredObservation) -or
                !$History.observationWriterAuthorized)) {
            throw 'PR, fork and manual candidate artifacts cannot authorize history writes.'
        }
        $path = Get-HealthRecordPath $record $History.recordTypes
        if ($pending.ContainsKey($path)) { throw 'Duplicate namespace/attempt in publication batch.' }
        $pending[$path] = $record
    }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $existing = @($History.records | Where-Object { $pending.ContainsKey((Get-HealthRecordPath $_ $History.recordTypes)) })
        if ($existing.Count) {
            throw 'This attempt is already recorded. Immutable history cannot be overwritten by a rerun of the observer.'
        }
        $directory = $History.directory
        $null = Invoke-HealthGit $directory @('read-tree', '--empty')
        if ($History.tip) { $null = Invoke-HealthGit $directory @('read-tree', $History.tip) }
        $blobs = @{}
        $paths = @($pending.Keys | Sort-Object -CaseSensitive)
        $inputDirectory = Join-Path $directory 'record-inputs'
        New-Item -ItemType Directory -Path $inputDirectory -Force | Out-Null
        $files = @()
        try {
            for ($index = 0; $index -lt $paths.Count; $index++) {
                $file = Join-Path $inputDirectory "$index.json"
                [IO.File]::WriteAllText($file, ($pending[$paths[$index]] | ConvertTo-Json -Depth 30) + [Environment]::NewLine,
                    [Text.UTF8Encoding]::new($false))
                $files += $file
            }
            $hashes = (Invoke-HealthGit $directory @('hash-object', '-w', '--stdin-paths') ($files -join "`n")) -split "`n"
            if ($hashes.Count -ne $paths.Count -or @($hashes | Where-Object { $_ -cnotmatch '^[0-9a-f]{40}$' }).Count) {
                throw 'History batch blob count/identity disagrees.'
            }
            $update = @('update-index', '--add')
            for ($index = 0; $index -lt $paths.Count; $index++) {
                $blobs[$paths[$index]] = $hashes[$index]
                $update += @('--cacheinfo', "100644,$($hashes[$index]),$($paths[$index])")
            }
            $null = Invoke-HealthGit $directory $update
        } finally {
            foreach ($file in $files) { Remove-Item -LiteralPath $file -Force }
        }
        $tree = Invoke-HealthGit $directory @('write-tree')
        $arguments = @('-c', 'user.name=CI observation', '-c', 'user.email=ci-observation@users.noreply.github.com', 'commit-tree', $tree)
        if ($History.tip) { $arguments += @('-p', $History.tip) }
        $commit = Invoke-HealthGit $directory $arguments "Record $($pending.Keys -join ', ')`n`nCo-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
        try {
            $null = Invoke-HealthGit $directory @('-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential',
                'push', '--quiet', 'origin', "${commit}:refs/heads/$FilesystemHealthBranch")
            $callerHistory.directory = $History.directory
            $callerHistory.tip = $commit
            $callerHistory.records = @($History.records) + @($pending.Values)
            return $commit
        } catch {
            $pushError = $_
            $retryDirectory = Join-Path (Split-Path $History.directory -Parent) ('h-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.git')
            $updated = Open-HealthHistory $retryDirectory $History.remote -AllowLocalFixture:$History.localFixture -RecordTypes $History.recordTypes
            $updated.observationWriterAuthorized = $History.observationWriterAuthorized
            if ($updated.tip -ceq $History.tip) { throw $pushError }
            if ($History.tip) {
                $null = Invoke-HealthGit $updated.directory @('merge-base', '--is-ancestor', $History.tip, $updated.tip)
            }
            $same = @($updated.records | Where-Object { $pending.ContainsKey((Get-HealthRecordPath $_ $updated.recordTypes)) })
            foreach ($saved in $same) {
                $path = Get-HealthRecordPath $saved $updated.recordTypes
                $savedBlob = Invoke-HealthGit $updated.directory @('rev-parse', "$($updated.tip):$path")
                if ($savedBlob -cne $blobs[$path]) { throw 'Concurrent publication recorded different data for the same attempt; no overwrite is allowed.' }
                $pending.Remove($path)
            }
            if (!$pending.Count) {
                $callerHistory.directory = $updated.directory
                $callerHistory.tip = $updated.tip
                $callerHistory.records = $updated.records
                return $updated.tip
            }
            $History = $updated
        }
    }
    throw 'Filesystem history publication remained concurrent after three bounded append attempts. No history was overwritten.'
}

function Format-FilesystemHealth($Record, [string]$Publication) {
    $lines = @(
        '### Filesystem CI observation'
        ''
        "Validation: **$($Record.validation)**. Timing: **$($Record.timingHealth)** (observation only; numerical policy is not calibrated)."
        "Original hosted job outcome: **$([Net.WebUtility]::HtmlEncode($Record.jobOutcome))**; this observer does not change that verdict."
        ''
        "Candidate: ``$($Record.revision)``; run/attempt $($Record.runId)/$($Record.runAttempt)."
        "Policy: ``$($Record.policyVersion)``; selector: ``Category!=Performance``."
        ''
        '| Lane / phase | Seconds | Reference seconds | Change seconds |'
        '|---|---:|---:|---:|'
        if ($null -ne $Record.measurements) {
            $referenceSeconds = if ($null -ne $Record.reference) { $Record.reference.executionSeconds } else { 'unavailable' }
            $delta = if ($null -ne $Record.reference) { $Record.reference.deltaSeconds } else { 'unavailable' }
            "| Whole execution (setup and cleanup included) | $($Record.measurements.executionSeconds) | $referenceSeconds | $delta |"
            foreach ($step in $Record.measurements.steps) {
                $previous = @()
                if ($null -ne $Record.reference) { $previous = @($Record.reference.steps | Where-Object { $_.name -ceq $step.name }) }
                $seconds = if ($previous.Count -eq 1) { $previous[0].seconds } else { 'unavailable' }
                $change = if ($previous.Count -eq 1) { $step.seconds - $previous[0].seconds } else { 'unavailable' }
                "| $($step.name) ($($step.status)) | $($step.seconds) | $seconds | $change |"
            }
        } else { '| Whole execution | unavailable | unavailable | unavailable |' }
        ''
        if ($null -ne $Record.measurements) {
            "Original 660-second budget headroom: $($Record.measurements.headroomSeconds) s; this is not a warning threshold."
        } else { 'Headroom: unavailable, not zero.' }
        if ($null -ne $Record.reference) {
            "Reference: prior compatible observation ``$($Record.reference.revision)`` ($($Record.reference.runId)/$($Record.reference.runAttempt)), age $([Math]::Round($Record.reference.ageSeconds, 1)) s."
            $percentage = if ($null -eq $Record.reference.deltaPercent) { 'percentage unavailable (zero-duration reference)' } else { "$($Record.reference.deltaPercent)%" }
            "Change: $($Record.reference.deltaSeconds) s; $percentage. Raw reference evidence: [$($Record.reference.evidenceState)]($($Record.reference.evidenceUrl))."
            'Raw reference availability is from retained metadata and its expiry, not fresh acceptance or a new artifact download.'
        } else { 'Reference: unavailable (no compatible non-self observation).' }
        if ($null -ne $Record.variability) {
            "Observed range: $($Record.variability.minimumSeconds)-$($Record.variability.maximumSeconds) s over $($Record.variability.count) $($Record.variability.scope)."
        } else { 'Variability: unavailable (insufficient prior comparable observations).' }
        if ($null -ne $Record.reliability) {
            "Attempts: $($Record.reliability.attempts). success: $($Record.reliability.success); failure: $($Record.reliability.failure); incomplete: $($Record.reliability.incomplete); cancelled: $($Record.reliability.cancelled); skipped: $($Record.reliability.skipped). Failure rate: $($Record.reliability.failureRatePercent)%. No cause is inferred."
        } else { 'Attempt history unavailable; only this attempt is shown. No reliability rate can be reported.' }
        foreach ($diagnostic in $Record.diagnostics) { "Observation detail: $([Net.WebUtility]::HtmlEncode($diagnostic))" }
        "History publication: $([Net.WebUtility]::HtmlEncode($Publication))."
        $expiry = if ($null -eq $Record.evidence.expiresAt) { 'unavailable' } else { $Record.evidence.expiresAt }
        "Evidence: [$($Record.evidence.state)]($($Record.evidence.url)); expiry $expiry."
        ''
        'Historical numbers are comparison inputs, not fresh release acceptance. No reference promotion or new blocking gate is active.'
    )
    return $lines -join "`n"
}
