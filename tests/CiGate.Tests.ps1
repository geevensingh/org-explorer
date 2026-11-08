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
