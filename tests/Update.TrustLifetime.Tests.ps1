$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.test-workspaces\trust-lifetime-' + [guid]::NewGuid().ToString('N'))
New-Item "$fixture\installer\publish", "$fixture\production" -ItemType Directory -Force | Out-Null
try {
    '{"schemaVersion":1,"manifestUrl":"https://example.invalid","keys":[]}' | Set-Content "$fixture\public-trust.json"
    'public sealed class ControlledTrustResourceFixture {}' | Set-Content "$fixture\Fixture.cs"
    $sdk = (& dotnet --version).Trim()
    $compiler = Join-Path (Split-Path (Get-Command dotnet).Source) "sdk\$sdk\Roslyn\bincore\csc.dll"
    & dotnet $compiler /nologo /target:library "/reference:$([object].Assembly.Location)" `
        "/resource:$fixture\public-trust.json,OrgExplorer.UpdateTrust.json" `
        "/out:$fixture\installer\publish\OrgExplorer.Desktop.dll" "$fixture\Fixture.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Controlled trust resource compilation failed.' }
    Copy-Item "$fixture\installer\publish\OrgExplorer.Desktop.dll" "$fixture\production\OrgExplorer.Desktop.dll"
    . "$root\eng\Release.ps1"
    $archive = [IO.Compression.ZipFile]::Open("$fixture\original.nupkg", [IO.Compression.ZipArchiveMode]::Create)
    try { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, "$fixture\installer\publish\OrgExplorer.Desktop.dll", 'lib/app/OrgExplorer.Desktop.dll') }
    finally { $archive.Dispose() }
    Assert-OriginalPackageTrust -PackagePath "$fixture\original.nupkg" -TrustFile "$fixture\public-trust.json"
    '{"schemaVersion":1,"manifestUrl":"https://different.invalid","keys":[]}' | Set-Content "$fixture\wrong-trust.json"
    $rejected = $false
    try { Assert-OriginalPackageTrust -PackagePath "$fixture\original.nupkg" -TrustFile "$fixture\wrong-trust.json" } catch { $rejected = $true }
    if (!$rejected) { throw 'Original package accepted a different source/public trust configuration.' }
    $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', @'
$ErrorActionPreference = 'Stop'
& $env:TRUST_LIFETIME_VERIFIER -Assembly "$env:TRUST_LIFETIME_FIXTURE\installer\publish\OrgExplorer.Desktop.dll" -Configuration "$env:TRUST_LIFETIME_FIXTURE\public-trust.json"
& $env:TRUST_LIFETIME_VERIFIER -Assembly "$env:TRUST_LIFETIME_FIXTURE\production\OrgExplorer.Desktop.dll" -Configuration "$env:TRUST_LIFETIME_FIXTURE\public-trust.json"
Remove-Item "$env:TRUST_LIFETIME_FIXTURE\installer" -Recurse -Force
if (Test-Path "$env:TRUST_LIFETIME_FIXTURE\installer") { throw 'Verified installer tree was not removed.' }
'@)) { $start.ArgumentList.Add($argument) }
    $start.Environment['TRUST_LIFETIME_VERIFIER'] = "$root\tests\Update.Trust.Tests.ps1"
    $start.Environment['TRUST_LIFETIME_FIXTURE'] = $fixture
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { $process.Kill($true); $process.WaitForExit(); throw 'Trust lifetime regression exceeded its deadline.' }
        if ($process.ExitCode -ne 0) { throw "Trust verification retained a package file through caller cleanup: $($output.Result) $($errors.Result)" }
    } finally { $process.Dispose() }
    Write-Host 'PASS: both trust checks permit immediate same-process installer-tree removal; no app or installer executed.'
} finally { Remove-Item $fixture -Recurse -Force }
