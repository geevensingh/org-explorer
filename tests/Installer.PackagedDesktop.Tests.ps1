param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$SmokeRunner,
    [string]$NewerReleaseDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$release = (Resolve-Path $ReleaseDirectory).Path
$runner = (Resolve-Path $SmokeRunner).Path
$metadata = Get-Content (Join-Path $release 'package-metadata.json') -Raw | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'Installer.Artifacts.Tests.ps1') -ReleaseDirectory $release -Version $metadata.version
$root = Join-Path $PSScriptRoot ('.test-workspaces\packaged-' + [guid]::NewGuid().ToString('N'))
$application = Join-Path $root 'Application'
$workspace = Join-Path $root 'Application-workspace'
$portable = Join-Path $release 'geevensingh.OrgExplorer-win-Portable.zip'
New-Item -ItemType Directory $root -Force | Out-Null

function Invoke-RealDesktop([string]$mode) {
    $start = [Diagnostics.ProcessStartInfo]::new($runner)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $root
    foreach ($argument in (Join-Path $application 'current\OrgExplorer.Desktop.exe'), $workspace, $mode, $metadata.version) {
        $start.ArgumentList.Add($argument)
    }
    foreach ($name in 'TEMP', 'TMP', 'VELOPACK_TEMP') { $start.Environment[$name] = $root }
    [void]$start.Environment.Remove('VELOPACK_DEBUG')
    $start.Environment['DOTNET_ROOT'] = Join-Path $root 'no-global-dotnet'
    $start.Environment['DOTNET_ROOT_X64'] = Join-Path $root 'no-global-dotnet'
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(120000)) { throw 'Packaged real-desktop flow exceeded two minutes.' }
        if ($process.ExitCode -ne 0) { throw "Packaged real-desktop flow failed with exit code $($process.ExitCode)." }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($portable, $application)
    if ($NewerReleaseDirectory) {
        $newer = (Resolve-Path $NewerReleaseDirectory).Path
        $newMetadata = Get-Content (Join-Path $newer 'package-metadata.json') -Raw | ConvertFrom-Json
        & (Join-Path $PSScriptRoot 'Installer.Artifacts.Tests.ps1') -ReleaseDirectory $newer -Version $newMetadata.version
        if ([version]$newMetadata.version -le [version]$metadata.version) { throw 'The controlled pending package must have a newer app version.' }
        $packages = Join-Path $application 'packages'
        New-Item -ItemType Directory $packages -Force | Out-Null
        Copy-Item (Join-Path $newer "geevensingh.OrgExplorer-$($newMetadata.version)-full.nupkg") $packages
    }
    Invoke-RealDesktop 'create'
    if ($NewerReleaseDirectory) { Write-Host 'PASS: a staged newer real package is not implicitly applied during bootstrap; original desktop version remains running.' }
    Remove-Item $application -Recurse -Force
    [IO.Compression.ZipFile]::ExtractToDirectory($portable, $application)
    Invoke-RealDesktop 'reopen'
    Write-Host 'PASS: self-contained packaged WPF, separate Workspace, installation-root/ancestor rejection, and durable data after replacing all app files. No setup, registry entry, or shortcuts were installed on the host.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
