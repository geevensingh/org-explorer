param(
    [string]$Version = '0.1.0',
    [string]$UpgradeVersion = '0.1.1'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $repo 'eng\Test-WindowsInstaller.ps1') -Version $Version -UpgradeVersion $UpgradeVersion -Mode Hosted -PrepareOnly
$prepared = Join-Path $repo ".artifacts\installer-verification\$Version-to-$UpgradeVersion"
$plan = Get-Content (Join-Path $prepared 'prepared.json') -Raw | ConvertFrom-Json
if ($plan.execution -ne 'not-run' -or $plan.mode -ne 'Hosted' -or $plan.baselineVersion -ne $Version -or $plan.upgradeVersion -ne $UpgradeVersion) {
    throw 'Preparing isolated inputs must not claim a successful install or lose the two-version lifecycle identity.'
}
foreach ($file in 'package-input\baseline\geevensingh.OrgExplorer-win-Setup.exe',
    'package-input\baseline\package-metadata.json', 'package-input\upgrade\geevensingh.OrgExplorer-win-Setup.exe',
    'package-input\upgrade\package-metadata.json', "package-input\upgrade\geevensingh.OrgExplorer-$UpgradeVersion-full.nupkg",
    'smoke-input\OrgExplorer.Desktop.Smoke.exe', 'test-input\OrgExplorer.Installer.Smoke.exe') {
    if (!(Test-Path (Join-Path $prepared $file) -PathType Leaf)) { throw "Missing isolated lifecycle input: $file" }
}
if (Test-Path (Join-Path $prepared 'results\result.json')) { throw 'Preparation unexpectedly executed an installation lifecycle.' }
& (Join-Path $PSScriptRoot 'Installer.Isolation.Tests.ps1') -Harness (Join-Path $prepared 'test-input\OrgExplorer.Installer.Smoke.exe')
Write-Host 'PASS: preparation builds real self-contained harnesses and stages two real installer versions without installing or claiming hosted results.'
