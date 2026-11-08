param(
    [Parameter(Mandatory)][string]$Executable
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path $Executable).Path
$root = Join-Path $PSScriptRoot ('.test-workspaces\bootstrap-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    foreach ($hook in '--veloapp-install', '--veloapp-updated', '--veloapp-obsolete', '--veloapp-uninstall') {
        $start = [Diagnostics.ProcessStartInfo]::new($Executable)
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $root
        $start.ArgumentList.Add($hook)
        $start.ArgumentList.Add('1.0.0')
        [void]$start.Environment.Remove('VELOPACK_DEBUG')
        $start.Environment['VELOPACK_TEMP'] = $root
        $start.Environment['TEMP'] = $root
        $start.Environment['TMP'] = $root
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (!$process.WaitForExit(10000)) {
                throw "$hook loaded the desktop instead of exiting before application/Workspace startup."
            }
            if ($process.ExitCode -ne 0) { throw "$hook failed with exit code $($process.ExitCode)." }
        } finally {
            if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
        Write-Host "PASS: $hook exits before desktop/Workspace startup."
    }
    if (Get-ChildItem $root -Filter workspace.json -Recurse) {
        throw 'An installer lifecycle hook created Workspace data.'
    }
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
