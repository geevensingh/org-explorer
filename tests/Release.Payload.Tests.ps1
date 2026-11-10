$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\ReleaseArtifacts.ps1"
$root = Join-Path $PSScriptRoot ("..\.test-workspaces\release-payload-" + [guid]::NewGuid().ToString('N'))
New-Item $root -ItemType Directory -Force | Out-Null
$path = Join-Path $root 'controlled.zip'
function Write-Archive([string]$Name) {
    if (Test-Path $path) { Remove-Item $path }
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry($Name)
        $stream = $entry.Open()
        try { $stream.Write([Text.Encoding]::UTF8.GetBytes('controlled bytes')) } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
}
$checks = 0
try {
    Write-Archive 'lib/app/System.Private.CoreLib.dll'
    Assert-ReleasePayload $path
    foreach ($name in @(
        'lib/app/Workspace/people/person.json', 'lib/app/fixtures/sample.txt', 'lib/app/OrgExplorer.Core.Tests.dll',
        'lib/app/OrgExplorer.Desktop.Smoke.dll', 'lib/app/company-export.json', 'lib/app/owner.pkcs8',
        'lib/app/owner.der', 'lib/app/owner.pem', 'lib/app/owner.pfx', 'lib/app/debug.pdb',
        'current/.test-workspaces/fake/person.txt', '../outside.txt', 'C:/outside.txt'
    )) {
        Write-Archive $name
        $rejected = $false
        try { Assert-ReleasePayload $path } catch { $rejected = $true }
        if (!$rejected) { throw "Product payload accepted $name" }
        $checks++
    }
    Write-Host "PASS: $checks private/Workspace/fixture/test payload exclusions; real runtime private-core assembly remains allowed."
} finally { Remove-Item $root -Recurse -Force }
