function Assert-ReleaseTrustJson([string]$TrustFile) {
    if ((Get-Item -LiteralPath $TrustFile).Length -gt 65536) { throw 'Trust configuration is too large.' }
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 16
    $document = [Text.Json.JsonDocument]::Parse([string](Get-Content -LiteralPath $TrustFile -Raw), $options)
    try {
        $root = $document.RootElement
        $names = @($root.EnumerateObject().Name)
        if ($names.Count -ne 3 -or (Compare-Object @('schemaVersion', 'manifestUrl', 'keys') $names -CaseSensitive) -or
            $root.GetProperty('schemaVersion').ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            $root.GetProperty('schemaVersion').GetInt32() -ne 1 -or
            $root.GetProperty('manifestUrl').ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $root.GetProperty('keys').ValueKind -ne [Text.Json.JsonValueKind]::Array) { throw 'Unsupported trust fields or types.' }
        foreach ($entry in $root.GetProperty('keys').EnumerateArray()) {
            $names = @($entry.EnumerateObject().Name)
            if ($names.Count -ne 3 -or (Compare-Object @('keyId', 'algorithm', 'publicKey') $names -CaseSensitive)) { throw 'Unsupported public-key fields.' }
            foreach ($name in $names) {
                if ($entry.GetProperty($name).ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Public-key fields must be strings.' }
            }
        }
    } finally { $document.Dispose() }
}

function Open-ReleasePublicKey([string]$TrustFile, [string]$KeyId) {
    Assert-ReleaseTrustJson $TrustFile
    $trust = Get-Content -LiteralPath $TrustFile -Raw | ConvertFrom-Json
    if ($trust.manifestUrl -cne 'https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json' -or
        @($trust.keys).Count -lt 1 -or @($trust.keys).Count -gt 8) { throw 'Original embedded production trust is not configured.' }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $trust.keys) {
        if ([string]::IsNullOrWhiteSpace($entry.keyId) -or $entry.keyId.Length -gt 80 -or
            !$ids.Add($entry.keyId) -or $entry.algorithm -cne 'ES256') { throw 'Original embedded public-key identity is invalid.' }
        $check = [Security.Cryptography.ECDsa]::Create()
        try {
            $bytes = [Convert]::FromBase64String($entry.publicKey)
            $read = 0
            $check.ImportSubjectPublicKeyInfo($bytes, [ref]$read)
            if ($read -ne $bytes.Length -or $check.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7') { throw 'Original embedded key is not standard P-256 SPKI.' }
        } finally { $check.Dispose() }
    }
    $active = @($trust.keys | Where-Object { $_.keyId -ceq $KeyId })
    if ($active.Count -ne 1) { throw 'Metadata signer is not trusted by the original embedded app configuration.' }
    $key = [Security.Cryptography.ECDsa]::Create()
    try { $read = 0; $key.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($active[0].publicKey), [ref]$read); return $key }
    catch { $key.Dispose(); throw }
}

function Open-ReleaseSigningKey([string]$TrustFile, [string]$KeyId, [string]$PrivateKeyBase64) {
    $key = $null
    $privateBytes = $null
    try {
        Assert-ReleaseTrustJson $TrustFile
        $trust = Get-Content -LiteralPath $TrustFile -Raw | ConvertFrom-Json
        if ($trust.schemaVersion -ne 1 -or
            $trust.manifestUrl -cne 'https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json' -or
            @($trust.keys).Count -lt 1 -or @($trust.keys).Count -gt 8 -or
            [string]::IsNullOrWhiteSpace($KeyId) -or $KeyId.Length -gt 80) { throw 'Invalid public trust.' }
        $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $trust.keys) {
            if ([string]::IsNullOrWhiteSpace($entry.keyId) -or $entry.keyId.Length -gt 80 -or
                !$ids.Add($entry.keyId) -or $entry.algorithm -cne 'ES256') { throw 'Invalid public key identity.' }
            $publicKey = [Security.Cryptography.ECDsa]::Create()
            try {
                $bytes = [Convert]::FromBase64String($entry.publicKey)
                $read = 0
                $publicKey.ImportSubjectPublicKeyInfo($bytes, [ref]$read)
                if ($read -ne $bytes.Length -or $publicKey.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7') { throw 'Invalid curve.' }
            } finally { $publicKey.Dispose() }
        }
        $active = @($trust.keys | Where-Object { $_.keyId -ceq $KeyId })
        if ($active.Count -ne 1) { throw 'Active key not embedded.' }
        $privateBytes = [Convert]::FromBase64String($PrivateKeyBase64)
        $key = [Security.Cryptography.ECDsa]::Create()
        $read = 0
        $key.ImportPkcs8PrivateKey($privateBytes, [ref]$read)
        if ($read -ne $privateBytes.Length -or $key.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7' -or
            ![Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                $key.ExportSubjectPublicKeyInfo(), [Convert]::FromBase64String($active[0].publicKey))) {
            throw 'Private/public key mismatch.'
        }
        return $key
    } catch {
        if ($key) { $key.Dispose() }
        # Do not expose imported values or underlying crypto/parser exceptions in Actions logs.
        throw 'Release signing is not configured. Owner: set secrets.ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8 to base64 DER PKCS#8 P-256, set vars.ORG_EXPLORER_UPDATE_KEY_ID, and commit its matching public SPKI in src\OrgExplorer.Desktop\update-trust.json. No artifacts were published.'
    } finally {
        if ($null -ne $privateBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($privateBytes) }
        $PrivateKeyBase64 = $null
    }
}

function New-SignedReleaseManifest(
    [string]$ReleaseDirectory, [string]$Version, [Security.Cryptography.ECDsa]$Key, [string]$KeyId,
    [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow
) {
    Assert-ReleaseVersion $Version
    if ([string]::IsNullOrWhiteSpace($KeyId) -or $KeyId.Length -gt 80) { throw 'A valid embedded signer ID is required.' }
    $fileName = "geevensingh.OrgExplorer-$Version-full.nupkg"
    $package = Get-Item -LiteralPath (Join-Path $ReleaseDirectory $fileName)
    if ($package.Length -le 0 -or $package.Length -gt 2147483648) { throw 'Full package length is outside protocol 1 limits.' }
    $payload = [ordered]@{
        protocolVersion = 1; appId = 'geevensingh.OrgExplorer'; version = $Version; platform = 'win-x64'; channel = 'win'
        releaseTag = "v$Version"; issuedAt = $Now.ToUniversalTime().ToString('O'); expiresAt = $Now.AddDays(14).ToUniversalTime().ToString('O')
        assets = @([ordered]@{
            kind = 'full'; fileName = $fileName; size = $package.Length
            sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            url = "https://github.com/geevensingh/org-explorer/releases/download/v$Version/$fileName"
        })
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 6 -Compress))
    $signature = $Key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $envelope = [ordered]@{
        keyId = $KeyId; algorithm = 'ES256'; payload = [Convert]::ToBase64String($bytes); signature = [Convert]::ToBase64String($signature)
    } | ConvertTo-Json -Compress
    $path = Join-Path $ReleaseDirectory 'org-explorer-update.win-x64.json'
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $encoded = [Text.Encoding]::UTF8.GetBytes($envelope)
        $stream.Write($encoded)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    return $path
}
