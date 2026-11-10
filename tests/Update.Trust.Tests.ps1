param(
    [Parameter(Mandatory)][string] $Assembly,
    [Parameter(Mandatory)][string] $Configuration
)
$ErrorActionPreference = 'Stop'
$compiled = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Assembly).Path)
if ($compiled.GetType('OrgExplorer.Desktop.ControlledUpdateEffectsMarker')) {
    throw 'Controlled external update effects leaked into the product publish.'
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
