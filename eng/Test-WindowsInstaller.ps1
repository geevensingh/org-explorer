param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$UpgradeVersion,
    [ValidateSet('Sandbox', 'Hosted')][string]$Mode = 'Sandbox',
    [string]$NuGetConfig,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([version]$UpgradeVersion -le [version]$Version) { throw 'Use two increasing stable fixture versions for real installation upgrade verification.' }
$baseline = Join-Path $repo ".artifacts\installer\$Version\releases"
$upgrade = Join-Path $repo ".artifacts\installer\$UpgradeVersion\releases"
& (Join-Path $repo 'tests\Installer.Artifacts.Tests.ps1') -ReleaseDirectory $baseline -Version $Version
& (Join-Path $repo 'tests\Installer.Artifacts.Tests.ps1') -ReleaseDirectory $upgrade -Version $UpgradeVersion
$configArguments = if ($NuGetConfig) { @('--configfile', (Resolve-Path $NuGetConfig).Path) } else { @() }
$output = Join-Path $repo ".artifacts\installer-verification\$Version-to-$UpgradeVersion"
if (Test-Path $output) { throw "Verification output already exists: $output. Preserve its evidence or explicitly remove that local run before retrying." }
$inputFolder = Join-Path $output 'package-input'
$smokeFolder = Join-Path $output 'smoke-input'
$testFolder = Join-Path $output 'test-input'
$resultFolder = Join-Path $output 'results'
New-Item -ItemType Directory $inputFolder, $smokeFolder, $testFolder, $resultFolder -Force | Out-Null
foreach ($fixture in @(@{ Name = 'baseline'; Release = $baseline }, @{ Name = 'upgrade'; Release = $upgrade })) {
    $target = Join-Path $inputFolder $fixture.Name
    New-Item -ItemType Directory $target | Out-Null
    Copy-Item (Join-Path $fixture.Release 'geevensingh.OrgExplorer-win-Setup.exe'), (Join-Path $fixture.Release 'package-metadata.json') $target
}
Copy-Item (Join-Path $upgrade "geevensingh.OrgExplorer-$UpgradeVersion-full.nupkg") (Join-Path $inputFolder 'upgrade')
Push-Location $repo
try {
    foreach ($runner in @(
        @{ Project = 'tests\OrgExplorer.Desktop.Smoke\OrgExplorer.Desktop.Smoke.csproj'; Output = $smokeFolder },
        @{ Project = 'tests\OrgExplorer.Installer.Smoke\OrgExplorer.Installer.Smoke.csproj'; Output = $testFolder }
    )) {
        $project = Join-Path $repo $runner.Project
        dotnet restore $project --locked-mode @configArguments
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained test runner restore failed.' }
        dotnet publish $project -c Release --runtime win-x64 --self-contained true --no-restore --output $runner.Output `
            -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=false -p:PublishTrimmed=false
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained test runner publish failed.' }
    }
} finally { Pop-Location }

if ([Environment]::UserName -ne 'WDAGUtilityAccount') {
    & (Join-Path $repo 'tests\Installer.Isolation.Tests.ps1') -Harness (Join-Path $testFolder 'OrgExplorer.Installer.Smoke.exe')
}
[ordered]@{ execution = 'not-run'; mode = $Mode; baselineVersion = $Version; upgradeVersion = $UpgradeVersion } |
    ConvertTo-Json | Set-Content (Join-Path $output 'prepared.json') -Encoding utf8
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
Write-Host "Prepared isolated installer verification ($Mode): $output"
if ($PrepareOnly) {
    Write-Host 'Prepared only; no installation executed and no acceptance success is claimed.'
    return
}
if ($Mode -eq 'Hosted') {
    # The native harness independently checks the runner provenance and all path boundaries.
    & (Join-Path $testFolder 'OrgExplorer.Installer.Smoke.exe') --github-hosted $inputFolder `
        (Join-Path $smokeFolder 'OrgExplorer.Desktop.Smoke.exe') (Join-Path $output 'lifecycle') $resultFolder
    if ($LASTEXITCODE -ne 0) { throw "Isolated hosted lifecycle failed or refused execution. See $resultFolder" }
    $result = Get-Content (Join-Path $resultFolder 'result.json') -Raw | ConvertFrom-Json
    if ($result.result -ne 'passed') { throw 'Hosted lifecycle did not report a passed result.' }
    Write-Host "PASS on $($result.os.description). Windows 11 client coverage: $($result.os.windows11Client). Evidence: $resultFolder"
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
    Write-Host "PASS: actual per-user install/automatic WPF launch, bundled runtime, two-version upgrade, uninstall/reinstall, and retained independent Workspace. Evidence: $resultPath"
} finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(10000)) { $process.Kill($true); $process.WaitForExit() }
    }
    $process.Dispose()
}
