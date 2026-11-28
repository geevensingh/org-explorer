$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\eng\CiGate.ps1"
Assert-CiResults @('success', 'success', 'success', 'success')
foreach ($bad in @('failure', 'skipped', 'cancelled', 'timed_out', '', 'unknown')) {
    foreach ($lane in 0..3) {
        $results = @('success', 'success', 'success', 'success')
        $results[$lane] = $bad
        $rejected = $false
        try { Assert-CiResults $results } catch { $rejected = $true }
        if (!$rejected) { throw "CI accepted $bad in required lane $lane" }
    }
}
foreach ($results in @(@(), @('success'), @('success','success'), @('success','success','success'),
    @('success','success','success','success','success'))) {
    $rejected = $false
    try { Assert-CiResults $results } catch { $rejected = $true }
    if (!$rejected) { throw 'CI accepted missing or unexpected required jobs' }
}
Write-Host 'PASS: CI gate rejects failures, skips, cancellations, timeouts and incomplete lanes.'
$workflow = Get-Content "$PSScriptRoot\..\.github\workflows\ci.yml" -Raw
$budget = 0
foreach ($lane in @('core', 'filesystem', 'desktop', 'high-resolution', 'ci')) {
    $job = [regex]::Match($workflow, "(?ms)^  ${lane}:\r?`n(?<body>.*?)(?=^  [\w-]+:|\z)").Groups['body'].Value
    if (!$job) { throw "Missing required CI lane $lane" }
    $minutes = [regex]::Match($job, 'timeout-minutes:\s*(\d+)').Groups[1].Value
    if (!$minutes) { throw "Missing execution deadline for $lane" }
    $budget += [int]$minutes
    $expected = if ($lane -eq 'core') { 8 } elseif ($lane -eq 'ci') { 2 } `
        elseif ($lane -in @('desktop', 'high-resolution')) { 15 } else { 11 }
    if ([int]$minutes -ne $expected) { throw "Required lane $lane changed its established execution deadline." }
    if ($lane -ne 'ci' -and $job -match '(?m)^\s+needs:') { throw "Required lane $lane was serialized" }
    if ($lane -ne 'ci' -and $job -notmatch "Run-CiLane.ps1 $lane") { throw "Required lane $lane skipped the meaningful runner" }
    if ($lane -in @('desktop', 'high-resolution')) {
        $partitionPattern = if ($lane -eq 'desktop') { 'partition:\s*\[people,\s*workspace\]' } `
            else { 'partition:\s*\[reading,\s*context\]' }
        $argumentPattern = if ($lane -eq 'desktop') { 'DesktopPartition.*matrix.partition' } `
            else { 'HighResolutionPartition.*matrix.partition' }
        if ($job -notmatch 'fail-fast:\s*false' -or
            $job -notmatch $partitionPattern -or $job -notmatch $argumentPattern) {
            throw "Both isolated $lane partitions must execute without fail-fast cancellation."
        }
        $budget += [int]$minutes
    }
}
if ($budget -ne 81) { throw "The partitioned four-lane workflow ceiling must total eighty-one minutes: $budget" }
if (!$workflow.Contains('needs: [core, filesystem, desktop, high-resolution]') -or
    !$workflow.Contains('HIGH_RESOLUTION_RESULT: ${{ needs.high-resolution.result }}') -or
    !$workflow.Contains('$env:HIGH_RESOLUTION_RESULT')) {
    throw 'The stable CI aggregate must require the independent high-resolution lane.'
}
if (!$workflow.Contains('Assert-DesktopPartitions') -or
    !$workflow.Contains('desktop-${{ matrix.partition }}-attempt-${{ github.run_attempt }}') -or
    !$workflow.Contains('desktop-*-attempt-${{ github.run_attempt }}')) {
    throw 'The CI gate must collect and validate both exact-attempt desktop artifacts.'
}
if (!$workflow.Contains('high-resolution-${{ matrix.partition }}-attempt-${{ github.run_attempt }}') -or
    !$workflow.Contains('high-resolution-*-attempt-${{ github.run_attempt }}') -or
    !$workflow.Contains('Assert-DesktopPartitions -Path .ci-results\high-resolution-collection') -or
    !$workflow.Contains('-RunAttempt $env:GITHUB_RUN_ATTEMPT -HighResolution')) {
    throw 'The CI gate must collect and validate both exact-attempt high-resolution artifacts.'
}
if ($workflow -match '(?m)^\s+paths(?:-ignore)?:') { throw 'Required CI cannot disappear on docs/test-only changes' }
Write-Host "PASS: four required logical lanes include two ordinary and two high-resolution workers; summed ceiling $budget minutes."
& "$PSScriptRoot\CoreTimingPolicy.Tests.ps1"
