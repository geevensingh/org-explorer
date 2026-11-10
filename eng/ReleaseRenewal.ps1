function Assert-ReleaseJsonFields($Element, [string[]]$Names) {
    $actual = @($Element.EnumerateObject().Name)
    if ($actual.Count -ne $Names.Count -or (Compare-Object $Names $actual -CaseSensitive)) { throw 'Signed metadata contains missing, extra or duplicate fields.' }
}

function Read-ReleaseAuthorization([string]$Text, [string]$TrustFile, $Product, [switch]$AllowExpired) {
    if ([Text.Encoding]::UTF8.GetByteCount($Text) -gt 65536) { throw 'Metadata exceeds protocol 1 limits.' }
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 16
    $envelopeDocument = [Text.Json.JsonDocument]::Parse($Text, $options)
    $payloadDocument = $null
    $key = $null
    try {
        Assert-ReleaseJsonFields $envelopeDocument.RootElement @('keyId', 'algorithm', 'payload', 'signature')
        foreach ($field in @('keyId', 'algorithm', 'payload', 'signature')) {
            if ($envelopeDocument.RootElement.GetProperty($field).ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Envelope fields must be JSON strings.' }
        }
        $envelope = $Text | ConvertFrom-Json
        if ($envelope.algorithm -cne 'ES256') { throw 'Unsupported metadata algorithm.' }
        $bytes = [Convert]::FromBase64String($envelope.payload)
        $signature = [Convert]::FromBase64String($envelope.signature)
        if ($bytes.Length -gt 65536 -or $signature.Length -ne 64) { throw 'Invalid signed metadata size/signature representation.' }
        $key = Open-ReleasePublicKey -TrustFile $TrustFile -KeyId $envelope.keyId
        if (!$key.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Original metadata authentication failed.' }
        $payloadDocument = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes), $options)
        $json = $payloadDocument.RootElement
        Assert-ReleaseJsonFields $json @('protocolVersion', 'appId', 'version', 'platform', 'channel', 'releaseTag', 'issuedAt', 'expiresAt', 'assets')
        foreach ($field in @('appId', 'version', 'platform', 'channel', 'releaseTag', 'issuedAt', 'expiresAt')) {
            if ($json.GetProperty($field).ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Signed app identity/timestamps must be JSON strings.' }
        }
        if ($json.GetProperty('assets').ValueKind -ne [Text.Json.JsonValueKind]::Array -or $json.GetProperty('assets').GetArrayLength() -ne 1) { throw 'Signed assets must be a one-element JSON array.' }
        $payload = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        $package = @($Product.artifacts | Where-Object { $_.fileName -ceq "geevensingh.OrgExplorer-$($Product.version)-full.nupkg" })
        if ($package.Count -ne 1 -or $package[0].size -le 0 -or $package[0].size -gt 2147483648 -or $package[0].sha256 -cnotmatch '^[0-9a-f]{64}\z') { throw 'Original package provenance is invalid.' }
        Assert-ReleaseVersion $Product.version
        if (@($payload.assets).Count -ne 1) { throw 'Original authorization must select exactly one full package.' }
        $asset = $json.GetProperty('assets')[0]
        Assert-ReleaseJsonFields $asset @('kind', 'fileName', 'size', 'sha256', 'url')
        foreach ($field in @('kind', 'fileName', 'sha256', 'url')) {
            if ($asset.GetProperty($field).ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Signed package identity must use JSON strings.' }
        }
        $issued = $json.GetProperty('issuedAt').GetString()
        $expires = $json.GetProperty('expiresAt').GetString()
        if ($json.GetProperty('protocolVersion').ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            $asset.GetProperty('size').ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            $json.GetProperty('protocolVersion').GetInt32() -ne 1 -or $asset.GetProperty('size').GetInt64() -ne $package[0].size -or
            $payload.appId -cne 'geevensingh.OrgExplorer' -or
            $payload.version -cne $Product.version -or $payload.releaseTag -cne $Product.tag -or
            $payload.platform -cne 'win-x64' -or $payload.channel -cne 'win' -or $payload.assets[0].kind -cne 'full' -or
            $payload.assets[0].fileName -cne $package[0].fileName -or $payload.assets[0].size -ne $package[0].size -or
            $payload.assets[0].sha256 -ine $package[0].sha256 -or
            $payload.assets[0].url -cne "https://github.com/geevensingh/org-explorer/releases/download/$($Product.tag)/$($package[0].fileName)" -or
            $issued -cnotmatch '(Z|[+-][0-9]{2}:[0-9]{2})\z' -or $expires -cnotmatch '(Z|[+-][0-9]{2}:[0-9]{2})\z' -or
            [DateTimeOffset]$issued -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or [DateTimeOffset]$expires -le [DateTimeOffset]$issued -or
            ([DateTimeOffset]$expires - [DateTimeOffset]$issued).TotalDays -gt 31 -or
            (!$AllowExpired -and [DateTimeOffset]$expires -le [DateTimeOffset]::UtcNow)) { throw 'Authorization does not bind the exact original stable app/package/protocol and bounded validity.' }
        $payload.issuedAt = $issued
        $payload.expiresAt = $expires
        return $payload
    } finally {
        if ($key) { $key.Dispose() }
        if ($payloadDocument) { $payloadDocument.Dispose() }
        $envelopeDocument.Dispose()
    }
}

function Get-MetadataRenewalPlan(
    $State, $Authorization, [string]$ControllerRevision, [long]$RunId, [int]$Attempt,
    [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow
) {
    if ($ControllerRevision -cnotmatch '^[0-9a-f]{40}\z' -or $RunId -le 0 -or $Attempt -le 0) { throw 'Renewal requires an exact controller revision and execution identity.' }
    if ($null -eq $State) { return @{ publish = $false; reason = 'No published product; metadata renewal cannot create an app release.' } }
    if ([DateTimeOffset]$Authorization.expiresAt -gt $Now.AddDays(7)) {
        return @{ publish = $false; reason = 'Authenticated metadata has more than seven days remaining.' }
    }
    $original = @($State.productRelease.assets | Where-Object name -CEQ 'release-provenance.json')
    $manifest = @($State.publicationRelease.assets | Where-Object name -CEQ 'org-explorer-update.win-x64.json')
    if ($original.Count -ne 1 -or $manifest.Count -ne 1 -or
        $original[0].digest -cnotmatch '^sha256:[0-9a-f]{64}\z' -or $manifest[0].digest -cnotmatch '^sha256:[0-9a-f]{64}\z') {
        throw 'Renewal requires immutable GitHub SHA256 provenance and manifest digests.'
    }
    return [ordered]@{
        publish = $true; kind = 'metadata-renewal'; reason = 'Authenticated metadata expires within seven days.'
        version = $State.product.version; tag = $State.product.tag; sourceSha = $State.product.sourceSha
        ciRunId = $State.product.ciRunId; verificationRunId = $RunId; publicationTag = "metadata-$($State.product.tag)-$RunId-$Attempt"
        controllerSha = $ControllerRevision; originalReleaseId = $State.productRelease.id
        originalProvenanceSha256 = $original[0].digest.Substring(7)
        previousPublicationTag = $State.publicationRelease.tag_name; previousPublicationId = $State.publicationRelease.id
        previousManifestDigest = $manifest[0].digest
    }
}

function Assert-PublishedReleaseInventory($Release, $Provenance) {
    $expected = @('geevensingh.OrgExplorer-win-Setup.exe', "geevensingh.OrgExplorer-$($Provenance.version)-full.nupkg",
        'geevensingh.OrgExplorer-win-Portable.zip', 'releases.win.json', 'assets.win.json', 'RELEASES',
        'package-metadata.json', 'org-explorer-update.win-x64.json', 'release-provenance.json')
    if (@($Release.assets).Count -ne 9 -or @($Provenance.artifacts).Count -ne 8 -or
        (Compare-Object @($expected | Sort-Object) @($Release.assets.name | Sort-Object) -CaseSensitive) -or
        (Compare-Object @($expected | Where-Object { $_ -cne 'release-provenance.json' } | Sort-Object) @($Provenance.artifacts.fileName | Sort-Object) -CaseSensitive)) {
        throw 'Published original/bootstrap inventory is incomplete or ambiguous.'
    }
    foreach ($asset in $Release.assets) {
        if ($asset.size -le 0 -or $asset.digest -cnotmatch '^sha256:[0-9a-f]{64}\z') { throw 'Published bootstrap assets need positive lengths and GitHub SHA256 digests.' }
        if ($asset.name -ceq 'release-provenance.json') { continue }
        $item = @($Provenance.artifacts | Where-Object { $_.fileName -ceq $asset.name })
        if ($item.Count -ne 1 -or $item[0].size -ne $asset.size -or "sha256:$($item[0].sha256)" -cne $asset.digest) {
            throw 'Published bootstrap bytes differ from original provenance.'
        }
    }
}

function Assert-OriginalPackageTrust([string]$PackagePath, [string]$TrustFile) {
    Assert-ReleaseTrustJson $TrustFile
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    $memory = [IO.MemoryStream]::new()
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -cmatch '^lib/[^/]+/OrgExplorer\.Desktop\.dll\z' })
        if ($entries.Count -ne 1) { throw 'Original full package has no unique product assembly.' }
        $input = $entries[0].Open()
        try { $input.CopyTo($memory) } finally { $input.Dispose() }
        $assembly = [Reflection.Assembly]::Load($memory.ToArray())
        if ($assembly.GetType('OrgExplorer.Desktop.ControlledUpdateEffectsMarker')) { throw 'Controlled update effects cannot authorize production renewal.' }
        $resource = $assembly.GetManifestResourceStream('OrgExplorer.UpdateTrust.json')
        if (!$resource) { throw 'Original product has no embedded public trust.' }
        $reader = [IO.StreamReader]::new($resource)
        try { $embedded = [Text.Json.Nodes.JsonNode]::Parse($reader.ReadToEnd()) } finally { $reader.Dispose() }
        $source = [Text.Json.Nodes.JsonNode]::Parse([string](Get-Content $TrustFile -Raw))
        # Git blob versus historic Windows checkout line endings need not be byte-identical.
        if (![Text.Json.Nodes.JsonNode]::DeepEquals($embedded, $source)) { throw 'Original package embedded trust differs from its exact source public trust.' }
    } finally { $memory.Dispose(); $archive.Dispose() }
}

function New-MetadataRenewalArtifacts(
    [string]$OriginalDirectory, [string]$Destination, $Plan, [Security.Cryptography.ECDsa]$Key, [string]$KeyId,
    [long]$VerificationRunId, [long]$ControllerCiRunId
) {
    if (!$Plan.publish -or $VerificationRunId -ne $Plan.verificationRunId -or $ControllerCiRunId -le 0 -or (Test-Path $Destination)) { throw 'Renewal needs a fresh output and verified execution identities.' }
    $original = Get-Content (Join-Path $OriginalDirectory 'release-provenance.json') -Raw | ConvertFrom-Json
    if ((Get-FileHash (Join-Path $OriginalDirectory 'release-provenance.json')).Hash.ToLowerInvariant() -cne $Plan.originalProvenanceSha256) { throw 'Original provenance bytes changed.' }
    New-Item $Destination -ItemType Directory | Out-Null
    foreach ($item in $original.artifacts) {
        if ($item.fileName -ceq 'org-explorer-update.win-x64.json') { continue }
        if ($item.fileName -cnotin @('geevensingh.OrgExplorer-win-Setup.exe', "geevensingh.OrgExplorer-$($Plan.version)-full.nupkg",
            'geevensingh.OrgExplorer-win-Portable.zip', 'releases.win.json', 'assets.win.json', 'RELEASES', 'package-metadata.json')) { throw 'Unexpected original bootstrap artifact.' }
        Copy-Item -LiteralPath (Join-Path $OriginalDirectory $item.fileName) -Destination (Join-Path $Destination $item.fileName)
    }
    $null = New-SignedReleaseManifest -ReleaseDirectory $Destination -Version $Plan.version -Key $Key -KeyId $KeyId
    [ordered]@{
        schemaVersion = 1; kind = 'metadata-renewal'; sourceSha = $Plan.sourceSha; version = $Plan.version; tag = $Plan.tag
        publicationTag = $Plan.publicationTag; originalReleaseId = $Plan.originalReleaseId; originalProvenanceSha256 = $Plan.originalProvenanceSha256
        previousPublicationTag = $Plan.previousPublicationTag; controllerSha = $Plan.controllerSha; controllerCiRunId = $ControllerCiRunId
        ciRunId = $Plan.ciRunId; verificationRunId = $VerificationRunId; createdAt = [DateTimeOffset]::UtcNow.ToString('O')
        artifacts = @(Get-ReleaseInventory $Destination)
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Destination 'release-provenance.json') -Encoding utf8NoBOM
    Assert-RenewalArtifacts -OriginalDirectory $OriginalDirectory -RenewalDirectory $Destination -Plan $Plan -Key $Key -KeyId $KeyId
}

function Assert-RenewalArtifacts([string]$OriginalDirectory, [string]$RenewalDirectory, $Plan, [Security.Cryptography.ECDsa]$Key, [string]$KeyId) {
    $originalPath = Join-Path $OriginalDirectory 'release-provenance.json'
    if ((Get-FileHash $originalPath).Hash.ToLowerInvariant() -cne $Plan.originalProvenanceSha256) { throw 'Original product provenance changed during renewal.' }
    $original = Get-Content $originalPath -Raw | ConvertFrom-Json
    if ($original.sourceSha -cne $Plan.sourceSha -or $original.tag -cne $Plan.tag -or
        $original.version -cne $Plan.version -or $original.ciRunId -ne $Plan.ciRunId) { throw 'Renewal cannot change original product identity/provenance.' }
    foreach ($item in $original.artifacts) {
        if ($item.fileName -ceq 'org-explorer-update.win-x64.json') { continue }
        $copy = Get-Item -LiteralPath (Join-Path $RenewalDirectory $item.fileName)
        if ($copy.Length -ne $item.size -or (Get-FileHash $copy.FullName).Hash -ine $item.sha256) { throw 'Renewal bootstrap assets must be byte-identical to the original product publication.' }
    }
    Assert-ReleaseInventory -ReleaseDirectory $RenewalDirectory -Version $Plan.version -Key $Key -KeyId $KeyId
    $renewal = Get-Content (Join-Path $RenewalDirectory 'release-provenance.json') -Raw | ConvertFrom-Json
    if ($renewal.kind -cne 'metadata-renewal' -or $renewal.publicationTag -cne $Plan.publicationTag -or
        $renewal.sourceSha -cne $Plan.sourceSha -or $renewal.ciRunId -ne $Plan.ciRunId -or
        $renewal.originalReleaseId -ne $Plan.originalReleaseId -or $renewal.originalProvenanceSha256 -cne $Plan.originalProvenanceSha256 -or
        $renewal.controllerSha -cne $Plan.controllerSha -or $renewal.verificationRunId -ne $Plan.verificationRunId -or $renewal.controllerCiRunId -le 0) { throw 'Renewal publication provenance is invalid.' }
}

function Assert-RenewalSnapshot($Plan, [long]$ControllerCiRunId) {
    $state = Get-ReleasePublicationState
    if ($null -eq $state -or $state.productRelease.id -ne $Plan.originalReleaseId -or $state.product.tag -cne $Plan.tag -or
        $state.product.sourceSha -cne $Plan.sourceSha -or $state.publicationRelease.id -ne $Plan.previousPublicationId -or
        $state.publicationRelease.tag_name -cne $Plan.previousPublicationTag) { throw 'A product or metadata publication superseded this renewal. Re-evaluate without replacing latest.' }
    Assert-PublishedReleaseInventory $state.productRelease $state.product
    $manifest = @($state.publicationRelease.assets | Where-Object name -CEQ 'org-explorer-update.win-x64.json')
    $original = @($state.productRelease.assets | Where-Object name -CEQ 'release-provenance.json')
    if ($manifest.Count -ne 1 -or $manifest[0].digest -cne $Plan.previousManifestDigest -or
        $original.Count -ne 1 -or $original[0].digest -cne "sha256:$($Plan.originalProvenanceSha256)" -or
        (Invoke-ReleaseApi "repos/$ReleaseRepository/releases/latest").id -ne $Plan.previousPublicationId) { throw 'Latest bootstrap/manifest/provenance changed during renewal.' }
    Assert-TrustedCi -Candidate $Plan.sourceSha -CiRunId $Plan.ciRunId
    Assert-TrustedMainCi -Candidate $Plan.controllerSha -CiRunId $ControllerCiRunId
}

function Publish-MetadataRenewal(
    $Plan, [string]$OriginalDirectory, [string]$RenewalDirectory, [long]$ControllerCiRunId, [Security.Cryptography.ECDsa]$Key, [string]$KeyId
) {
    if (!$Plan.publish -or $Plan.kind -cne 'metadata-renewal' -or
        $Plan.publicationTag -cnotmatch ('^metadata-' + [regex]::Escape($Plan.tag) + '-[1-9][0-9]*-[1-9][0-9]*\z')) { throw 'Invalid immutable metadata renewal plan.' }
    $proof = Get-Content (Join-Path $RenewalDirectory 'release-provenance.json') -Raw | ConvertFrom-Json
    if ($proof.controllerCiRunId -ne $ControllerCiRunId) { throw 'Renewal controller CI provenance differs from its guarded execution.' }
    Publish-VerifiedReleaseDraft -PublicationTag $Plan.publicationTag -SourceSha $Plan.sourceSha -Name "Org explorer $($Plan.version) update metadata renewal" `
        -Body "Metadata renewal only. Original app/package remains $($Plan.tag) at $($Plan.sourceSha); no new app version. Bootstrap assets are byte-identical. Fresh exact-product scale verification and provenance are recorded in release-provenance.json." `
        -ReleaseDirectory $RenewalDirectory -BeforePromotion {
            Assert-RenewalArtifacts -OriginalDirectory $OriginalDirectory -RenewalDirectory $RenewalDirectory -Plan $Plan -Key $Key -KeyId $KeyId
            Assert-RenewalSnapshot -Plan $Plan -ControllerCiRunId $ControllerCiRunId
        }
}
