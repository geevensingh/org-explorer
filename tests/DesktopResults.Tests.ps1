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
}
finally { Remove-Item $root -Recurse -Force }
