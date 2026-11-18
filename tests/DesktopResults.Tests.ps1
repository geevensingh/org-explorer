$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\eng\DesktopResults.ps1"
$root = Join-Path "$PSScriptRoot\..\.test-workspaces" ("desktop-contract-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root -Force | Out-Null
$report = Join-Path $root 'scenarios.json'
$expected = @('first', 'second')
function Write-Result($scenarios) {
    @{ schemaVersion = 1; scenarios = @($scenarios) } | ConvertTo-Json -Depth 6 | Set-Content $report
}
function Assert-Rejected([scriptblock] $Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw 'Desktop result validation accepted incomplete or failed required execution.' }
}
function Passing-Results {
    foreach ($name in $expected) {
        @{ name = $name; status = 'Passed'; startedAt = '2026-11-01T17:00:00Z'; finishedAt = '2026-11-01T17:00:01Z'; seconds = 1; error = $null }
    }
}
try {
    Assert-Rejected { Assert-DesktopResults $report $expected }
    Write-Result (Passing-Results)
    Assert-DesktopResults $report $expected
    $culture = [Threading.Thread]::CurrentThread.CurrentCulture
    try {
        [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('en-GB')
        $scenarios = @(Passing-Results)
        $scenarios[0].startedAt = '2026-11-23T17:00:00Z'
        $scenarios[0].finishedAt = '2026-11-23T18:00:01+01:00'
        Write-Result $scenarios
        Assert-DesktopResults $report $expected
        $scenarios[0].finishedAt = '2026-11-23T17:59:59+01:00'
        Write-Result $scenarios
        Assert-Rejected { Assert-DesktopResults $report $expected }
    } finally { [Threading.Thread]::CurrentThread.CurrentCulture = $culture }
    foreach ($status in @('NotRun', 'Running', 'Failed', 'Skipped', 'Cancelled', 'TimedOut', '', 'unknown')) {
        $scenarios = @(Passing-Results)
        $scenarios[1].status = $status
        Write-Result $scenarios
        Assert-Rejected { Assert-DesktopResults $report $expected }
    }
    foreach ($scenarios in @(@(), @((Passing-Results)[0]), @((Passing-Results)[0], (Passing-Results)[0]),
        @((Passing-Results) + @{ name = 'unexpected'; status = 'Passed' }))) {
        Write-Result $scenarios
        Assert-Rejected { Assert-DesktopResults $report $expected }
    }
    foreach ($field in @('startedAt', 'finishedAt', 'seconds')) {
        $scenarios = @(Passing-Results)
        $scenarios[0].Remove($field)
        Write-Result $scenarios
        Assert-Rejected { Assert-DesktopResults $report $expected }
    }
    $scenarios = @(Passing-Results)
    $scenarios[0].error = 'Reported failure despite success-shaped status'
    Write-Result $scenarios
    Assert-Rejected { Assert-DesktopResults $report $expected }
    '{"schemaVersion":1,"scenarios":[' | Set-Content $report
    Assert-Rejected { Assert-DesktopResults $report $expected }
    Write-Result (Passing-Results)
    Assert-Rejected { Assert-DesktopResults $report @() }
    $expected = @(Get-Content "$PSScriptRoot\HighResolutionScenarios.json" -Raw | ConvertFrom-Json)
    Write-Result (Passing-Results)
    Assert-DesktopResults $report $expected
    Write-Result @((Passing-Results)[0])
    Assert-Rejected { Assert-DesktopResults $report $expected }
    Write-Result @(@{ name = 'search-favorites'; status = 'Passed'; startedAt = '2026-11-01T17:00:00Z'; finishedAt = '2026-11-01T17:00:01Z'; seconds = 1 })
    Assert-Rejected { Assert-DesktopResults $report $expected }
    Write-Host 'PASS: desktop results require every named scenario to finish successfully; missing, partial and contradictory evidence fails closed.'

    $collection = Join-Path $root 'collection'
    $revision = 'a' * 40
    $partitionCases = @{
        people = @('profile-journey', 'draft-navigation', 'person-note-rendering', 'team-context-history', 'search-favorites')
        workspace = @('archival-navigation', 'compatibility-settings', 'update-settings', 'external-refresh')
    }
    $highCollection = Join-Path $root 'high-resolution'
    $highPartitionCases = @{
        reading = @('responsive-workspace')
        context = @('responsive-draft-navigation', 'responsive-team-history')
    }
    function Write-Partitions([switch] $HighResolution) {
        $lane = if ($HighResolution) { 'high-resolution' } else { 'desktop' }
        $destination = if ($HighResolution) { $highCollection } else { $collection }
        $cases = if ($HighResolution) { $highPartitionCases } else { $partitionCases }
        foreach ($partition in $cases.Keys) {
            $directory = Join-Path $destination "$lane-$partition-attempt-2"
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $names = @('restore-app', 'restore-tests', 'build-app', 'display-prerequisite', 'desktop-flows')
            if ($partition -eq 'workspace') { $names += @('profile-note-refresh', 'update-yield', 'release-isolation') }
            @{ schemaVersion = 1; lane = $lane; partition = $partition; revision = $revision; runId = '123';
                runAttempt = '2'; complete = $true; ceilingSeconds = 900; executionSeconds = 120
                steps = @($names | ForEach-Object { @{ name = $_; status = 'Passed'; seconds = 1 } })
            } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $directory 'execution.json')
            @{ schemaVersion = 1; scenarios = @($cases[$partition] | ForEach-Object {
                @{ name = $_; status = 'Passed'; startedAt = '2026-11-01T17:00:00Z';
                    finishedAt = '2026-11-01T17:00:01Z'; seconds = 1; error = $null }
            }) } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $directory 'scenarios.json')
        }
    }
    Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    Write-Partitions
    Assert-DesktopPartitions $collection $revision '123' '2'
    $workspace = Join-Path $collection 'desktop-workspace-attempt-2'
    foreach ($field in @('partition', 'revision', 'runId', 'runAttempt', 'complete')) {
        Write-Partitions
        $path = Join-Path $workspace 'execution.json'
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $metadata.$field = if ($field -eq 'complete') { $false } else { 'wrong' }
        $metadata | ConvertTo-Json -Depth 6 | Set-Content $path
        Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    }
    Write-Partitions
    $path = Join-Path $workspace 'execution.json'
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $metadata.steps = @($metadata.steps | Where-Object name -ne 'update-yield')
    $metadata | ConvertTo-Json -Depth 6 | Set-Content $path
    Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    Write-Partitions
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $metadata.steps[0].status = 'Failed'
    $metadata | ConvertTo-Json -Depth 6 | Set-Content $path
    Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    foreach ($seconds in @(901, 'NaN')) {
        Write-Partitions
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $metadata.executionSeconds = $seconds
        $metadata | ConvertTo-Json -Depth 6 | Set-Content $path
        Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    }
    Write-Partitions
    $path = Join-Path $workspace 'scenarios.json'
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $metadata.scenarios[0].status = 'Failed'
    $metadata | ConvertTo-Json -Depth 6 | Set-Content $path
    Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    Write-Partitions
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $metadata.scenarios[0].name = $metadata.scenarios[0].name.ToUpperInvariant()
    $metadata | ConvertTo-Json -Depth 6 | Set-Content $path
    Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    Write-Partitions
    $people = Join-Path $collection 'desktop-people-attempt-2'
    Remove-Item -LiteralPath $people -Recurse -Force
    Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    Write-Partitions
    $unexpected = Join-Path $collection 'desktop-extra-attempt-2'
    New-Item -ItemType Directory -Path $unexpected | Out-Null
    Assert-Rejected { Assert-DesktopPartitions $collection $revision '123' '2' }
    Remove-Item -LiteralPath $unexpected
    Assert-DesktopPartitions $collection $revision '123' '2'

    Assert-Rejected { Assert-DesktopPartitions $highCollection $revision '123' '2' -HighResolution }
    Write-Partitions -HighResolution
    Assert-DesktopPartitions $highCollection $revision '123' '2' -HighResolution
    $context = Join-Path $highCollection 'high-resolution-context-attempt-2'
    foreach ($field in @('lane', 'partition', 'revision', 'runId', 'runAttempt', 'complete', 'ceilingSeconds')) {
        Write-Partitions -HighResolution
        $path = Join-Path $context 'execution.json'
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $metadata.$field = if ($field -eq 'complete') { $false } elseif ($field -eq 'ceilingSeconds') { 901 } else { 'wrong' }
        $metadata | ConvertTo-Json -Depth 6 | Set-Content $path
        Assert-Rejected { Assert-DesktopPartitions $highCollection $revision '123' '2' -HighResolution }
    }
    foreach ($failure in @('late', 'missing-command', 'failed-scenario')) {
        Write-Partitions -HighResolution
        $path = Join-Path $context 'execution.json'
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($failure -eq 'late') { $metadata.executionSeconds = 901 }
        elseif ($failure -eq 'missing-command') { $metadata.steps = @($metadata.steps | Where-Object name -ne 'desktop-flows') }
        else {
            $path = Join-Path $context 'scenarios.json'
            $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $metadata.scenarios[0].status = 'Failed'
        }
        $metadata | ConvertTo-Json -Depth 6 | Set-Content $path
        Assert-Rejected { Assert-DesktopPartitions $highCollection $revision '123' '2' -HighResolution }
    }
    Write-Partitions -HighResolution
    Remove-Item -LiteralPath (Join-Path $highCollection 'high-resolution-reading-attempt-2') -Recurse -Force
    Assert-Rejected { Assert-DesktopPartitions $highCollection $revision '123' '2' -HighResolution }
    Write-Partitions -HighResolution
    $unexpected = Join-Path $highCollection 'desktop-people-attempt-2'
    New-Item -ItemType Directory -Path $unexpected | Out-Null
    Assert-Rejected { Assert-DesktopPartitions $highCollection $revision '123' '2' -HighResolution }
    Remove-Item -LiteralPath $unexpected
    Assert-DesktopPartitions $highCollection $revision '123' '2' -HighResolution
    Assert-DesktopPartitions $collection $revision '123' '2'

    $configuration = Join-Path $root 'invalid-partitions'
    New-Item -ItemType Directory -Path $configuration | Out-Null
    '["first","second"]' | Set-Content (Join-Path $configuration 'DesktopScenarios.json')
    '{"people":["first"],"workspace":["first"]}' | Set-Content (Join-Path $configuration 'DesktopPartitions.json')
    Assert-Rejected { Get-DesktopPartitions $configuration }
    '["first","second"]' | Set-Content (Join-Path $configuration 'HighResolutionScenarios.json')
    '{"reading":["first"],"context":["first"]}' | Set-Content (Join-Path $configuration 'HighResolutionPartitions.json')
    Assert-Rejected { Get-DesktopPartitions $configuration -HighResolution }
    Write-Host 'PASS: both ordinary and high-resolution partition pairs require complete exact-run evidence and all companion commands.'
}
finally { Remove-Item $root -Recurse -Force }
