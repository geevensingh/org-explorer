param(
    [Parameter(Mandatory)][ValidateSet('workspace', 'prefix')][string] $Worker,
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedRevision = $env:REVIEWED_DIAGNOSTIC_REVISION
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Read-GitIdentity([string] $Identity) {
    $value = & git -C $root rev-parse $Identity
    if ($LASTEXITCODE -ne 0 -or $value -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Cannot establish an exact diagnostic source identity.'
    }
    return $value.Trim()
}
$shipping = 'cae977166df7327b4a2d268a10a20578adf338e6'
$pins = @{
    tree = 'ad625fa53e6592840e1e44e0bc5ef6e4ed65e9de'
    src = 'd0c87216bb9fcefe5391eb6a79dfeee23a75ae31'
    tests = 'f5b7e1b303529af29c2a1e8a34a89cd5b00cc24d'
    eng = 'f0d4df23d4361e01fc5be4d32bfeeecba2211b7d'
    scaleWorkflow = '019d5924de3378d077fac76abc8c04452846d18a'
}
$revision = Read-GitIdentity HEAD
if (!$ExpectedRevision -or $revision -cne $ExpectedRevision -or
    $env:GITHUB_SHA -cne $revision -or !$env:GITHUB_RUN_ID -or !$env:GITHUB_RUN_ATTEMPT) {
    throw 'Passive capture requires the exact dispatched diagnostic source, run and attempt.'
}
$firstDiagnostic = '4229987d54c0d060f233fd88beb5556884f0381e'
$firstTree = 'e36bf9945c36b288099a42175a9d9e69494948ca'
$rawChain = @(& git -C $root rev-list --parents "$shipping..HEAD")
if ($LASTEXITCODE -ne 0 -or !$rawChain.Count) { throw 'Cannot establish the exact isolated diagnostic ancestry.' }
$chain = [Collections.Generic.List[object]]::new()
$expected = $revision
foreach ($line in $rawChain) {
    $parts = $line.Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Length -ne 2 -or $parts[0] -cne $expected -or
        $parts[0] -cnotmatch '^[0-9a-f]{40}$' -or $parts[1] -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Diagnostic source must be an exactly pinned linear chain without merges.'
    }
    $chain.Add(@{ revision = $parts[0]; parent = $parts[1]; tree = Read-GitIdentity "$($parts[0])`^{tree}" })
    $expected = $parts[1]
}
if ($expected -cne $shipping -or $chain[$chain.Count - 1].revision -cne $firstDiagnostic -or
    $chain[$chain.Count - 1].tree -cne $firstTree) {
    throw 'Diagnostic ancestry must reach immutable shipping only through the known first diagnostic commit.'
}
foreach ($key in $pins.Keys) {
    $object = switch ($key) {
        tree { "$shipping`^{tree}" }
        scaleWorkflow { "$shipping`:.github/workflows/scale-verification.yml" }
        default { "$shipping`:$key" }
    }
    if ((Read-GitIdentity $object) -cne $pins[$key]) { throw 'Shipping source binding differs from the approved pin.' }
}
if (!$env:GITHUB_ENV) { throw 'The diagnostic destination must be owned by the current hosted worker.' }
$directory = [IO.Path]::GetFullPath($ResultsDirectory, $root)
if (!$directory.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Diagnostic artifacts must remain inside the owned checkout.'
}
$correlation = Join-Path $directory 'correlation'
New-Item -ItemType Directory -Path $correlation -Force | Out-Null
$binding = @{
    schema = 2; diagnosticOnly = $true; purpose = 'One passive correlation capture; NEVER release or complete acceptance evidence'
    sourceRevision = $revision; sourceTree = Read-GitIdentity 'HEAD^{tree}'
    reviewedDiagnosticRevision = $ExpectedRevision
    sourceSrc = Read-GitIdentity 'HEAD:src'; sourceTests = Read-GitIdentity 'HEAD:tests'; sourceEng = Read-GitIdentity 'HEAD:eng'
    sourceCiWorkflow = Read-GitIdentity 'HEAD:.github/workflows/ci.yml'
    sourceScaleWorkflow = Read-GitIdentity 'HEAD:.github/workflows/scale-verification.yml'
    shippingRevision = $shipping; shipping = $pins
    firstDiagnosticRevision = $firstDiagnostic; diagnosticChain = @($chain.ToArray())
    mainRevision = '53bd82496bbc0e64c041470c2e1b4ed90b577776'
    runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT; worker = $Worker
    setupInclusiveBudgetSeconds = if ($Worker -ceq 'workspace') { 900 } else { 660 }
    watchdogMinutes = if ($Worker -ceq 'workspace') { 15 } else { 13 }
    originalCommand = if ($Worker -ceq 'workspace') {
        '.\eng\Run-CiLane.ps1 desktop -DesktopPartition workspace'
    } else {
        '.\eng\Run-UiScale.ps1 -ExpectedRevision $env:TESTED_REVISION -Worker prefix -ResultsDirectory .ci-results\ui-scale-prefix -ExtendedObservation:([bool]::Parse($env:EXTENDED_OBSERVATION))'
    }
    normalEntryRole = if ($Worker -ceq 'workspace') { 'workspace-controlled-entry-exe' } else { 'rich-normal-entry-exe' }
    controlledHostRole = if ($Worker -ceq 'workspace') { 'workspace-controlled-adoption-host' } else { 'rich-controlled-wpf-host' }
    nativeExpectedVersion = '3.54.1'
    nativeExpectedSource = '2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76'
    nativeExpectedSha256 = '922AB7FF7E47EF2694CA5F62ADBEFCC44C637BF5D082488C286816ED5D06363F'
    interpretation = 'Record roles and PID/start identities separately; QPC comparisons only within the same runner. Drops, missing boundaries, output failure or ambiguous generations make diagnosis incomplete. No overhead subtraction.'
}
$path = Join-Path $directory 'diagnostic-source-binding.json'
if (Test-Path -LiteralPath $path) { throw 'Do not overwrite an earlier diagnostic binding.' }
$binding | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
"ORG_EXPLORER_DIAGNOSTIC_DIRECTORY=$correlation" | Add-Content -LiteralPath $env:GITHUB_ENV
"ORG_EXPLORER_DIAGNOSTIC_WORKER=$Worker" | Add-Content -LiteralPath $env:GITHUB_ENV
Write-Host "ISOLATED DIAGNOSTIC SOURCE ONLY: $revision; worker $Worker; NEVER release or complete acceptance evidence."
