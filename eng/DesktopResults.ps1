function Assert-DesktopResults([string] $Path, [string[]] $Expected) {
    if (!$Expected -or @($Expected | Select-Object -Unique).Count -ne $Expected.Count) {
        throw 'Desktop execution requires a nonempty, unique scenario selection.'
    }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing desktop scenario results: $Path" }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $scenarios = @($report.scenarios)
    if ($report.schemaVersion -ne 1 -or $scenarios.Count -ne $Expected.Count -or
        @($scenarios.name | Select-Object -Unique).Count -ne $Expected.Count -or
        (Compare-Object @($Expected | Sort-Object) @($scenarios.name | Sort-Object) -CaseSensitive)) {
        throw 'Desktop scenario results do not match the complete required selection.'
    }
    foreach ($scenario in $scenarios) {
        if ($scenario.status -cne 'Passed' -or $scenario.error -or !$scenario.startedAt -or !$scenario.finishedAt -or
            $null -eq $scenario.seconds -or $scenario.seconds -lt 0 -or
            [DateTimeOffset]$scenario.finishedAt -lt [DateTimeOffset]$scenario.startedAt) {
            throw "Desktop scenario '$($scenario.name)' did not finish successfully: $($scenario.status) $($scenario.error)"
        }
    }
}

function Get-DesktopPartitions([string] $Directory = (Join-Path $PSScriptRoot '..\tests'), [switch] $HighResolution) {
    $prefix = if ($HighResolution) { 'HighResolution' } else { 'Desktop' }
    $required = if ($HighResolution) { @('reading', 'context') } else { @('people', 'workspace') }
    $expected = @(Get-Content -LiteralPath (Join-Path $Directory "${prefix}Scenarios.json") -Raw | ConvertFrom-Json)
    $partitions = Get-Content -LiteralPath (Join-Path $Directory "${prefix}Partitions.json") -Raw |
        ConvertFrom-Json -AsHashtable
    if ($partitions.Count -ne 2 -or @($required | Where-Object { $partitions.Keys -cnotcontains $_ }).Count -or !$expected) {
        throw "$prefix execution requires its two declared partitions."
    }
    $selected = @()
    foreach ($partition in $required) {
        $names = $partitions[$partition]
        if ($names -isnot [Collections.IList] -or $names.Count -eq 0 -or
            @($names | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count) {
            throw 'Desktop partitions require nonempty scenario lists.'
        }
        $selected += @($names)
    }
    if ($selected.Count -ne $expected.Count -or @($selected | Select-Object -Unique).Count -ne $expected.Count -or
        (Compare-Object @($expected | Sort-Object) @($selected | Sort-Object) -CaseSensitive)) {
        throw "$prefix partitions must execute every required scenario exactly once."
    }
    return $partitions
}

function Get-DesktopPartitionSteps([string] $Partition) {
    $names = @('restore-app', 'restore-tests', 'build-app', 'display-prerequisite', 'desktop-flows')
    if ($Partition -eq 'workspace') { $names += @('profile-note-refresh', 'update-yield', 'release-isolation') }
    return $names
}

function Assert-DesktopPartitions([string] $Path, [string] $Revision, [string] $RunId, [string] $RunAttempt,
    [switch] $HighResolution) {
    if ([string]::IsNullOrWhiteSpace($Revision) -or [string]::IsNullOrWhiteSpace($RunId) -or
        [string]::IsNullOrWhiteSpace($RunAttempt)) {
        throw 'Desktop collection requires an explicit candidate, run and attempt.'
    }
    $partitions = Get-DesktopPartitions -HighResolution:$HighResolution
    $lane = if ($HighResolution) { 'high-resolution' } else { 'desktop' }
    $expectedDirectories = @($partitions.Keys | ForEach-Object { "$lane-$_-attempt-$RunAttempt" })
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw 'Missing desktop partition artifacts.' }
    $directories = @(Get-ChildItem -LiteralPath $Path -Directory)
    if ($directories.Count -ne 2 -or
        (Compare-Object @($expectedDirectories | Sort-Object) @($directories.Name | Sort-Object) -CaseSensitive)) {
        throw 'Both current-attempt desktop partition artifacts are required.'
    }
    foreach ($partition in $partitions.Keys) {
        $directory = Join-Path $Path "$lane-$partition-attempt-$RunAttempt"
        $metadataPath = Join-Path $directory 'execution.json'
        if (!(Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw 'Missing desktop execution binding.' }
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        if ($metadata.schemaVersion -ne 1 -or $metadata.lane -cne $lane -or
            $metadata.partition -cne $partition -or $metadata.revision -cne $Revision -or
            $metadata.runId -cne $RunId -or $metadata.runAttempt -cne $RunAttempt -or
            $metadata.complete -isnot [bool] -or !$metadata.complete -or
            $metadata.ceilingSeconds -ne 900 -or $null -eq $metadata.executionSeconds -or
            ![double]::IsFinite($metadata.executionSeconds) -or
            $metadata.executionSeconds -lt 0 -or $metadata.executionSeconds -gt 900) {
            throw 'Desktop execution is incomplete, stale, unbound or outside its deadline.'
        }
        $expectedSteps = @(Get-DesktopPartitionSteps $partition)
        $steps = @($metadata.steps)
        if ($steps.Count -ne $expectedSteps.Count -or
            @($steps.name | Select-Object -Unique).Count -ne $expectedSteps.Count -or
            (Compare-Object @($expectedSteps | Sort-Object) @($steps.name | Sort-Object) -CaseSensitive)) {
            throw 'Desktop execution omitted or duplicated a required command.'
        }
        foreach ($step in $steps) {
            if ($step.status -cne 'Passed' -or $null -eq $step.seconds -or
                ![double]::IsFinite($step.seconds) -or $step.seconds -lt 0) {
                throw "Desktop command '$($step.name)' did not succeed."
            }
        }
        Assert-DesktopResults (Join-Path $directory 'scenarios.json') @($partitions[$partition])
    }
}
