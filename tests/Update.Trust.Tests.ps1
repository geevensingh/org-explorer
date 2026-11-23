param(
    [Parameter(Mandatory)][string] $Assembly,
    [Parameter(Mandatory)][string] $Configuration
)
$ErrorActionPreference = 'Stop'
$compiled = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Assembly).Path))
foreach ($testType in @(
    'OrgExplorer.Desktop.ControlledUpdateEffectsMarker',
    'OrgExplorer.TestFixtures.WorkspacePublicState',
    'OrgExplorer.TestFixtures.InstallerPreservationEvidence',
    'OrgExplorer.TestFixtures.InstallerImportReplay',
    'OrgExplorer.TestFixtures.InstallerWorkspacePreservation'
)) {
    if ($compiled.GetType($testType)) {
        throw "Controlled external update effects or preservation oracle leaked into the product publish: $testType"
    }
}
$stream = $compiled.GetManifestResourceStream('OrgExplorer.UpdateTrust.json')
if (!$stream) { throw 'The published app has no embedded update trust configuration.' }
try {
    $actual = [Security.Cryptography.SHA256]::HashData($stream)
    $expected = [Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Configuration).Path))
    if ([Convert]::ToHexString($actual) -ne [Convert]::ToHexString($expected)) {
        throw 'Published trust differs from the production configuration; an ephemeral test key/feed may have leaked.'
    }
} finally { $stream.Dispose() }
Write-Host 'PASS: compiled app trust matches the expected configuration byte-for-byte.'
