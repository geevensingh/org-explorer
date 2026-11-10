function Assert-DesktopResults([string] $Path, [string[]] $Expected) {
    if (!$Expected -or @($Expected | Select-Object -Unique).Count -ne $Expected.Count) {
        throw 'Desktop execution requires a nonempty, unique scenario selection.'
    }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing desktop scenario results: $Path" }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $scenarios = @($report.scenarios)
    if ($report.schemaVersion -ne 1 -or $scenarios.Count -ne $Expected.Count -or
        @($scenarios.name | Select-Object -Unique).Count -ne $Expected.Count -or
        (Compare-Object @($Expected | Sort-Object) @($scenarios.name | Sort-Object))) {
        throw 'Desktop scenario results do not match the complete required selection.'
    }
    foreach ($scenario in $scenarios) {
        if ($scenario.status -cne 'Passed' -or $scenario.error -or !$scenario.startedAt -or !$scenario.finishedAt -or
            $null -eq $scenario.seconds -or $scenario.seconds -lt 0 -or
            [DateTimeOffset]::Parse($scenario.finishedAt) -lt [DateTimeOffset]::Parse($scenario.startedAt)) {
            throw "Desktop scenario '$($scenario.name)' did not finish successfully: $($scenario.status) $($scenario.error)"
        }
    }
}
