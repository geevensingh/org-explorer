param(
    [Parameter(Mandatory)][ValidateSet('Start', 'Complete')][string]$Stage,
    [string]$EvidenceDirectory = '.ci-results\scale-worker-step'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory, $root)
$contextPath = Join-Path $evidence 'context.json'

if ($Stage -ceq 'Start') {
    if (Test-Path -LiteralPath $contextPath) { throw 'A step-lifetime probe cannot reuse another invocation.' }
    $source = Join-Path $evidence ('source-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path "$source\eng" | Out-Null
    Copy-Item -Path "$root\eng\*.ps1" -Destination "$source\eng"
    '{"version":"ci-timing-v4"}' | Set-Content -LiteralPath "$source\eng\timing-policy.json"
    '.results/' | Set-Content -LiteralPath "$source\.gitignore"
    @'
param([string]$ExpectedRevision, [string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$run = 'run-' + [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path "$ResultsDirectory\$run" | Out-Null
@{ revision = $ExpectedRevision; runDirectory = $run; outcome = 'running'; phase = 'cleanup-authentication' } |
    ConvertTo-Json | Set-Content -LiteralPath "$ResultsDirectory\result.json"
[IO.File]::WriteAllText("$ResultsDirectory\cleanup-proof.json.writing", '{"diagnosticOnly":true}')
[IO.File]::Move("$ResultsDirectory\cleanup-proof.json.writing", "$ResultsDirectory\cleanup-proof.json")
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
while (!(Test-Path -LiteralPath "$ResultsDirectory\diagnostic-release")) {
    if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Diagnostic release expired.' }
    Start-Sleep -Milliseconds 20
}
Write-Host 'Diagnostic resumed across the workflow step boundary.'
throw 'Deliberate diagnostic failure; this probe cannot produce policy acceptance.'
'@ | Set-Content -LiteralPath "$source\eng\Run-Performance.ps1"
    & git -C $source init --quiet -b main
    & git -C $source add .
    & git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Nonaccepting cross-step diagnostic'
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the isolated diagnostic source.' }
    $revision = (& git -C $source rev-parse HEAD).Trim()
    $results = Join-Path $source '.results\diagnostic'
    & "$source\eng\Invoke-ScaleWorker.ps1" -Stage Start -Worker non-ui-performance `
        -ExpectedRevision $revision -ResultsDirectory $results
    $metadata = Get-Content -LiteralPath "$results\worker-process.json" -Raw | ConvertFrom-Json -DateKind String
    $worker = Get-Process -Id $metadata.processId -ErrorAction Stop
    try {
        if ($worker.HasExited -or $worker.StartTime.ToUniversalTime().ToString('O') -cne $metadata.processStartedAt) {
            throw 'The actual worker was not alive at its proof handoff.'
        }
        @{ source = $source; results = $results; revision = $revision; controllerPid = $PID
            workerPid = $worker.Id; processStartedAt = $metadata.processStartedAt; aliveAtHandoff = $true } |
            ConvertTo-Json | Set-Content -LiteralPath $contextPath
    } finally { $worker.Dispose() }
    Write-Host 'START: actual worker is paused; this workflow controller must exit before Complete.'
    return
}

$context = Get-Content -LiteralPath $contextPath -Raw | ConvertFrom-Json -DateKind String
$worker = Get-Process -Id $context.workerPid -ErrorAction SilentlyContinue
@{ controllerPid = $PID; originalControllerPid = $context.controllerPid; workerPid = $context.workerPid
    aliveAfterStep = [bool]$worker; receiptExists = (Test-Path -LiteralPath "$($context.results)\worker-exit.json") } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'after-step.json')
if (!$worker) { throw 'RED: the actual paused worker disappeared across the workflow step boundary without its exit receipt.' }
try {
    if ($PID -eq $context.controllerPid -or
        $worker.StartTime.ToUniversalTime().ToString('O') -cne $context.processStartedAt) {
        throw 'The diagnostic requires a separate controller and the original process identity.'
    }
    [IO.File]::WriteAllText("$($context.results)\diagnostic-release", 'release')
    if (!$worker.WaitForExit(15000)) { throw 'The diagnostic worker did not finish after release.' }
    $receiptPath = Join-Path $context.results 'worker-exit.json'
    if (!(Test-Path -LiteralPath $receiptPath)) { throw 'RED: actual worker exit has no supervised receipt.' }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -DateKind String
    if ($receipt.processId -ne $context.workerPid -or $receipt.processStartedAt -cne $context.processStartedAt -or
        $receipt.revision -cne $context.revision -or $receipt.exitCode -ne 1) {
        throw 'The diagnostic receipt lost the actual failure or source/process identity.'
    }
    $refusal = $null
    try {
        & "$($context.source)\eng\Invoke-ScaleWorker.ps1" -Stage Complete -Worker non-ui-performance `
            -ExpectedRevision $context.revision -ResultsDirectory $context.results
    } catch { $refusal = $_ }
    if (!$refusal -or $refusal.Exception.Message -cnotlike 'Scale worker did not finish successfully:*') {
        throw 'Complete did not preserve the intentionally failed worker verdict.'
    }
    Write-Host 'PASS: a real worker survives separate workflow controllers; actual exit is retained and failure is refused.'
} finally {
    if (!$worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }
    $worker.Dispose()
}
