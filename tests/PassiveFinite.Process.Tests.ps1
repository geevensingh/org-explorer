param([Parameter(Mandatory)][string] $HarnessAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assembly = (Resolve-Path -LiteralPath $HarnessAssembly).Path
$results = Join-Path $root ('.ci-results\finite-work-process-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results | Out-Null
$runs = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt 5; $index++) {
    $directory = Join-Path $results "process-$index"
    New-Item -ItemType Directory -Path $directory | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($assembly)
    $start.ArgumentList.Add('--passive-finite-work-support')
    $start.Environment['ORG_EXPLORER_DIAGNOSTIC_DIRECTORY'] = $directory
    $start.Environment['GITHUB_SHA'] = 'dddddddddddddddddddddddddddddddddddddddd'
    $start.Environment['GITHUB_RUN_ID'] = 'finite-support'
    $start.Environment['GITHUB_RUN_ATTEMPT'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit(10000)) {
            $process.Kill($true); $process.WaitForExit()
            throw 'The finite-work no-window process exceeded its support-only deadline.'
        }
        [IO.File]::WriteAllText((Join-Path $directory 'stderr.log'), $errors.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $output.GetAwaiter().GetResult())
        if ($process.ExitCode -ne 0) { throw 'Finite packets were not exported during controlled original work.' }
        $proof = Get-Content -LiteralPath (Join-Path $directory 'finite-work-proof.json') -Raw | ConvertFrom-Json
        if ($proof.outcome -cne 'ObservationCovered' -or !$proof.supportOnly -or $proof.wholeProcessComplete) {
            throw 'Finite proof was confused with whole-process/native acceptance.'
        }
        $whole = @(Get-ChildItem -LiteralPath $directory -Filter *.complete.json -File)
        $runs.Add(@{ index = $index; exitCode = $process.ExitCode; finiteOutcome = $proof.outcome;
            supportOnly = $true; wholeProcessMarker = $whole.Count })
    } finally { $process.Dispose() }
}
if (@($runs | Where-Object wholeProcessMarker -eq 0).Count -eq 0) {
    throw 'This support run did not demonstrate independent finite coverage with incomplete whole-process closure.'
}
@{ diagnosticOnly = $true; runs = @($runs.ToArray())
    statement = 'Closed finite packets exported during controlled work; actual Main exit does not wait for drain. No native UI or native acceptance.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'result.json')
Write-Host "PASS: five finite work proofs, successful no-wait exits, and independent incomplete whole-process closure. Evidence: $results"
