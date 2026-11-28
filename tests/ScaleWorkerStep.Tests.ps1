param(
    [Parameter(Mandatory)][ValidateSet('Start', 'Complete')][string]$Stage,
    [string]$EvidenceDirectory = '.ci-results\scale-worker-step',
    [ValidatePattern('^[0-9a-f]{40}$')][string]$SupervisorRevision
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory, $root)
$contextPath = Join-Path $evidence 'context.json'
$completionSteps = @(
    foreach ($workflow in @('scale-verification.yml', 'rich-cohort-sample.yml', 'non-ui-cohort-execution.yml')) {
        $text = Get-Content -LiteralPath (Join-Path $root ".github\workflows\$workflow") -Raw
        foreach ($step in ($text -split '(?m)^      - ')) {
            if ($step -cmatch 'Invoke-ScaleWorker\.ps1 -Stage Complete') {
                if ($step -cnotmatch '(?m)^          GH_TOKEN: \$\{\{ github\.token \}\}\s*$') {
                    throw "Worker completion has no independently authenticated GitHub context: $workflow"
                }
                $workflow
            }
        }
    }
)
if ($completionSteps.Count -ne 7) { throw 'The complete supervised worker workflow selection changed.' }

if ($Stage -ceq 'Start') {
    if (Test-Path -LiteralPath $contextPath) { throw 'A step-lifetime test cannot reuse another invocation.' }
    $source = Join-Path $evidence 'source'
    if (Test-Path -LiteralPath $source) { throw 'Select a fresh invocation directory for the step-lifetime test.' }
    New-Item -ItemType Directory -Path "$source\eng" | Out-Null
    Copy-Item -Path "$root\eng\*.ps1" -Destination "$source\eng"
    if ($SupervisorRevision) {
        $original = & git -C $root show "${SupervisorRevision}:eng/Invoke-ScaleWorker.ps1"
        if ($LASTEXITCODE -ne 0) { throw 'Original supervisor source is unavailable.' }
        $original | Set-Content -LiteralPath "$source\eng\Invoke-ScaleWorker.ps1"
    }
    '{"version":"ci-timing-v4"}' | Set-Content -LiteralPath "$source\eng\timing-policy.json"
    '.results/' | Set-Content -LiteralPath "$source\.gitignore"
    @'
param([string]$ExpectedRevision, [string]$ResultsDirectory, [switch]$PrepareCleanupAuthentication)
$ErrorActionPreference = 'Stop'
$run = 'run-' + [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path "$ResultsDirectory\$run" | Out-Null
@{ revision = $ExpectedRevision; runDirectory = $run; outcome = 'running'; phase = 'cleanup-authentication'
    acceptanceDeadlineAt = ([DateTimeOffset]$env:SCALE_STARTED_AT).AddSeconds(660).ToString('O')
    diagnosticOnly = $true } | ConvertTo-Json | Set-Content -LiteralPath "$ResultsDirectory\result.json"
[IO.File]::WriteAllText("$ResultsDirectory\cleanup-proof.json.writing", '{"diagnosticOnly":true}')
[IO.File]::Move("$ResultsDirectory\cleanup-proof.json.writing", "$ResultsDirectory\cleanup-proof.json")
Start-Sleep -Seconds 2
Write-Host 'Nonaccepting diagnostic finished before its Start controller.'
'@ | Set-Content -LiteralPath "$source\eng\Run-Performance.ps1"
    & git -C $source init --quiet -b main
    & git -C $source add .
    & git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Unaccepted prepared-proof fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the isolated source.' }
    $revision = (& git -C $source rev-parse HEAD).Trim()
    $results = Join-Path $source '.results\diagnostic'
    $previousStarted = $env:SCALE_STARTED_AT
    try {
        $env:SCALE_STARTED_AT = [DateTimeOffset]::UtcNow.ToString('O')
        & "$source\eng\Invoke-ScaleWorker.ps1" -Stage Start -Worker non-ui-performance `
            -ExpectedRevision $revision -ResultsDirectory $results
    } finally { $env:SCALE_STARTED_AT = $previousStarted }
    $metadata = Get-Content -LiteralPath "$results\worker-process.json" -Raw | ConvertFrom-Json -DateKind String
    if ($metadata.observedExitCode -ne 0 -or !(Test-Path -LiteralPath "$results\worker-exit.json")) {
        throw 'RED: Start returned a proof handoff before observing actual worker exit and its receipt.'
    }
    $receipt = Get-Content -LiteralPath "$results\worker-exit.json" -Raw | ConvertFrom-Json -DateKind String
    if ($receipt.processId -ne $metadata.processId -or $receipt.exitCode -ne 0 -or
        $receipt.processStartedAt -cne $metadata.processStartedAt) {
        throw 'Prepared handoff lacks its actual matching exit receipt.'
    }
    @{ source = $source; results = $results; revision = $revision; controllerPid = $PID } |
        ConvertTo-Json | Set-Content -LiteralPath $contextPath
    Write-Host 'PASS: Start observed real exit before returning unaccepted proof.'
    return
}

$context = Get-Content -LiteralPath $contextPath -Raw | ConvertFrom-Json -DateKind String
if ($PID -eq $context.controllerPid) { throw 'Complete requires a separate controller process.' }
$receiptPath = Join-Path $context.results 'worker-exit.json'
$receiptText = Get-Content -LiteralPath $receiptPath -Raw
try {
    Remove-Item -LiteralPath $receiptPath
    $missing = $null
    try {
        & "$($context.source)\eng\Invoke-ScaleWorker.ps1" -Stage Complete -Worker non-ui-performance `
            -ExpectedRevision $context.revision -ResultsDirectory $context.results
    } catch { $missing = $_ }
    if (!$missing -or $missing.Exception.Message -cnotlike '*worker-exit.json*') {
        throw 'Missing original exit receipt was not refused.'
    }
} finally { [IO.File]::WriteAllText($receiptPath, $receiptText) }
$reportPath = Join-Path $context.results 'result.json'
$reportText = Get-Content -LiteralPath $reportPath -Raw
try {
    $extended = $reportText | ConvertFrom-Json -AsHashtable -DateKind String
    $extended.acceptanceDeadlineAt = ([DateTimeOffset]$extended.acceptanceDeadlineAt).AddSeconds(60).ToString('O')
    [IO.File]::WriteAllText($reportPath, ($extended | ConvertTo-Json))
    $changedDeadline = $null
    try {
        & "$($context.source)\eng\Invoke-ScaleWorker.ps1" -Stage Complete -Worker non-ui-performance `
            -ExpectedRevision $context.revision -ResultsDirectory $context.results
    } catch { $changedDeadline = $_ }
    if (!$changedDeadline -or $changedDeadline.Exception.Message -cne 'Worker completion differs from its original supervised deadline.') {
        throw 'Prepared completion accepted a changed deadline.'
    }
} finally { [IO.File]::WriteAllText($reportPath, $reportText) }
$refusal = $null
try {
    & "$($context.source)\eng\Invoke-ScaleWorker.ps1" -Stage Complete -Worker non-ui-performance `
        -ExpectedRevision $context.revision -ResultsDirectory $context.results
} catch { $refusal = $_ }
if (!$refusal -or $refusal.Exception.Message -cne 'Prepared cleanup requires the original attestation before completion.') {
    throw 'A real worker exit or prepared report bypassed the required signature.'
}
Write-Host 'PASS: separate Complete refuses missing exit evidence and unsigned prepared work.'
