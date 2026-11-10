$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\eng\CiGate.ps1"
Assert-CiResults @('success', 'success', 'success')
foreach ($bad in @('failure', 'skipped', 'cancelled', 'timed_out', '', 'unknown')) {
    foreach ($lane in 0..2) {
        $results = @('success', 'success', 'success')
        $results[$lane] = $bad
        $rejected = $false
        try { Assert-CiResults $results } catch { $rejected = $true }
        if (!$rejected) { throw "CI accepted $bad in required lane $lane" }
    }
}
foreach ($results in @(@(), @('success'), @('success','success'), @('success','success','success','success'))) {
    $rejected = $false
    try { Assert-CiResults $results } catch { $rejected = $true }
    if (!$rejected) { throw 'CI accepted missing or unexpected required jobs' }
}
Write-Host 'PASS: CI gate rejects failures, skips, cancellations, timeouts and incomplete lanes.'
$workflow = Get-Content "$PSScriptRoot\..\.github\workflows\ci.yml" -Raw
$budget = 0
foreach ($lane in @('core', 'filesystem', 'desktop', 'ci')) {
    $job = [regex]::Match($workflow, "(?ms)^  ${lane}:\r?`n(?<body>.*?)(?=^  [\w-]+:|\z)").Groups['body'].Value
    if (!$job) { throw "Missing required CI lane $lane" }
    $minutes = [regex]::Match($job, 'timeout-minutes:\s*(\d+)').Groups[1].Value
    if (!$minutes) { throw "Missing execution deadline for $lane" }
    $budget += [int]$minutes
    if ($lane -ne 'ci' -and $job -match '(?m)^\s+needs:') { throw "Required lane $lane was serialized" }
    if ($lane -ne 'ci' -and $job -notmatch "Run-CiLane.ps1 $lane") { throw "Required lane $lane skipped the meaningful runner" }
}
if ($budget -gt 30) { throw "Total CI execution ceiling exceeds thirty minutes: $budget" }
if ($workflow -match '(?m)^\s+paths(?:-ignore)?:') { throw 'Required CI cannot disappear on docs/test-only changes' }
Write-Host "PASS: three independent lanes share a total $budget-minute ceiling including setup and validation."
