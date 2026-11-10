param([Parameter(Mandatory)][string] $Directory)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$port = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
try {
    $port.Start()
    $endpoint = "http://127.0.0.1:$($port.LocalEndpoint.Port)/manifest"
    [IO.File]::WriteAllText((Join-Path $Directory 'ephemeral-private.pem'), $key.ExportPkcs8PrivateKeyPem())
    @{
        schemaVersion = 1
        manifestUrl = $endpoint
        keys = @(@{
            keyId = 'ephemeral-release-key'
            algorithm = 'ES256'
            publicKey = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
        })
    } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Directory 'trust.json')
} finally {
    $port.Stop()
    $key.Dispose()
}
