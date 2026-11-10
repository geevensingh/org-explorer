param(
    [Parameter(Mandatory)][string]$Harness
)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -eq 'WDAGUtilityAccount') { throw 'The fail-closed host guard test must run outside Windows Sandbox.' }
$start = [Diagnostics.ProcessStartInfo]::new((Resolve-Path $Harness).Path)
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
try {
    $output = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(10000)) { throw 'The installer harness did not refuse host execution promptly.' }
    if ($process.ExitCode -ne 1 -or $output.GetAwaiter().GetResult() -notmatch 'No host installation was attempted') {
        throw 'The installer harness must fail closed outside the disposable Windows Sandbox account.'
    }
    Write-Host 'PASS: installer harness refuses to install into the shared host profile.'
} finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
