param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$NuGetConfig = (Join-Path $PSScriptRoot '..\NuGet.Config'),
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releases = Join-Path $repo ".artifacts\installer\$Version\releases"
& (Join-Path $repo 'tests\Installer.Artifacts.Tests.ps1') -ReleaseDirectory $releases -Version $Version
$config = (Resolve-Path $NuGetConfig).Path
$output = Join-Path $repo ('.artifacts\installer-verification\' + $Version + '-' + [guid]::NewGuid().ToString('N'))
$inputFolder = Join-Path $output 'package-input'
$smokeFolder = Join-Path $output 'smoke-input'
$testFolder = Join-Path $output 'test-input'
$resultFolder = Join-Path $output 'results'
New-Item -ItemType Directory $inputFolder, $smokeFolder, $testFolder, $resultFolder -Force | Out-Null
Copy-Item (Join-Path $releases 'geevensingh.OrgExplorer-win-Setup.exe'), (Join-Path $releases 'package-metadata.json') $inputFolder
Push-Location $repo
try {
    foreach ($runner in @(
        @{ Project = 'tests\OrgExplorer.Desktop.Smoke\OrgExplorer.Desktop.Smoke.csproj'; Output = $smokeFolder },
        @{ Project = 'tests\OrgExplorer.Installer.Smoke\OrgExplorer.Installer.Smoke.csproj'; Output = $testFolder }
    )) {
        $project = Join-Path $repo $runner.Project
        dotnet restore $project --locked-mode --configfile $config
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained test runner restore failed.' }
        dotnet publish $project -c Release --runtime win-x64 --self-contained true --no-restore --output $runner.Output `
            -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=false -p:PublishTrimmed=false
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained test runner publish failed.' }
    }
} finally { Pop-Location }

$wsb = Join-Path $output 'VerifyInstaller.wsb'
$escape = { param($value) [Security.SecurityElement]::Escape($value) }
@"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$(& $escape $inputFolder)</HostFolder><SandboxFolder>C:\PackageInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(& $escape $smokeFolder)</HostFolder><SandboxFolder>C:\SmokeInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(& $escape $testFolder)</HostFolder><SandboxFolder>C:\TestInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(& $escape $resultFolder)</HostFolder><SandboxFolder>C:\TestResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>C:\TestInput\OrgExplorer.Installer.Smoke.exe</Command></LogonCommand>
</Configuration>
"@ | Set-Content $wsb -Encoding utf8
Write-Host "Prepared isolated offline installer verification: $wsb"
if ($PrepareOnly) {
    Write-Host 'Prepared only; no installation executed and no acceptance success is claimed.'
    return
}
$sandbox = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
if (!(Test-Path $sandbox)) {
    throw "Windows Sandbox is unavailable. No host installation was attempted. Run the prepared $wsb on a Windows 11 x64 machine with Windows Sandbox already enabled."
}
$start = [Diagnostics.ProcessStartInfo]::new($sandbox)
$start.UseShellExecute = $false
$start.ArgumentList.Add($wsb)
$process = [Diagnostics.Process]::Start($start)
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $resultPath = Join-Path $resultFolder 'result.json'
    while (!(Test-Path $resultPath)) {
        if ($watch.Elapsed.TotalSeconds -gt 480) { throw 'Isolated install/start verification exceeded eight minutes including Sandbox boot; no successful result was reported.' }
        Start-Sleep -Milliseconds 500
    }
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    if ($result.result -ne 'passed') { throw "Isolated installer verification failed: $($result.error). See $resultFolder" }
    Write-Host "PASS: actual per-user install/automatic WPF launch, self-contained desktop flows, replacement, uninstall/reinstall, and retained independent Workspace. Evidence: $resultPath"
} finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(10000)) { $process.Kill($true); $process.WaitForExit() }
    }
    $process.Dispose()
}
