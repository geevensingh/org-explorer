param(
    [Parameter(Mandatory)][string] $HarnessAssembly,
    [switch] $RequireWholeProcessCompletion
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assembly = (Resolve-Path -LiteralPath $HarnessAssembly).Path
$results = Join-Path $root ('.ci-results\passive-exit-support-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results | Out-Null
$runs = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt 10; $index++) {
    $directory = Join-Path $results "process-$index"
    New-Item -ItemType Directory -Path $directory | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($assembly)
    $start.ArgumentList.Add('--passive-exit-support')
    $start.Environment['ORG_EXPLORER_DIAGNOSTIC_DIRECTORY'] = $directory
    $process = [Diagnostics.Process]::Start($start)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit(10000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'The no-window support process did not exit within its own bounded test deadline.'
        }
        if ($process.ExitCode -ne 0) { throw 'The actual no-wait harness Main did not retain its original success result.' }
        $stdout = $output.GetAwaiter().GetResult()
        $fixture = $stdout.Trim() | ConvertFrom-Json
        if (!$fixture.supportOnly -or !$fixture.structurallyLinkable -or $fixture.emittedRecords -ge 4096) {
            throw 'The owned lifecycle fixture must have a valid causal recordset and stay below the global capacity limit.'
        }
        $markers = @(Get-ChildItem -LiteralPath $directory -Filter *.complete.json -File)
        $complete = $markers.Count -eq 1 -and (Get-Content -LiteralPath $markers[0].FullName -Raw | ConvertFrom-Json).diagnosticsComplete
        $runs.Add(@{ index = $index; processId = $process.Id; exitCode = $process.ExitCode
            executionMs = $timer.Elapsed.TotalMilliseconds; closureMarker = $markers.Count; captureComplete = $complete })
        [IO.File]::WriteAllText((Join-Path $directory 'stderr.log'), $errors.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $stdout)
    } finally { $process.Dispose() }
}
$incomplete = @($runs | Where-Object { !$_.captureComplete }).Count
@{ diagnosticOnly = $true; semantics = 'Actual Smoke.Program.Main using/dispose/return lifecycle; no WaitForDrain, WPF window or desktop automation'
    attempts = $runs.Count; incomplete = $incomplete; runs = @($runs.ToArray())
    policy = 'A missing/incomplete post-close marker remains incomplete; no exit delay or policy relaxation.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results 'result.json')
Write-Host "NO-WAIT-MAIN: $incomplete/$($runs.Count) incomplete traces; all original process exits successful. Evidence: $results"
if ($RequireWholeProcessCompletion -and $incomplete) {
    throw 'RED: actual no-wait Main exit cannot guarantee whole-process capture completion.'
}
if ($incomplete -eq 0) { throw 'The short no-wait support run did not reproduce the completion race; do not claim it absent.' }
Write-Host 'PASS: completion race reproduced without delaying exit; missing-marker policy still refuses complete diagnosis.'
