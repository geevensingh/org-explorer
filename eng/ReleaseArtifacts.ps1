function Assert-ReleasePayload([string]$ArchivePath) {
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$))|(^|/)(people|photos|notes|history|generations|workspaces?|fixtures|\.git|\.test-workspaces|testresults)(/|$)|(^|/)workspace\.json$|\.(pkcs8|p8|der|pfx|p12|pem|key|pdb)$|OrgExplorer\.[^/]*(Tests|Smoke|Fixture)|ScaleFixture|ControlledUpdateEffects') {
                throw 'Workspace/company fixtures, private keys, test/debug material or unsafe archive paths cannot ship.'
            }
            if ($entry.Name.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase) -and
                $entry.Name -cnotin @('OrgExplorer.Desktop.runtimeconfig.json', 'OrgExplorer.Desktop.deps.json')) {
                throw 'Only the product runtime/dependency JSON files may ship in application archives; Workspace/export/test/trust JSON must not be copied.'
            }
        }
    } finally { $archive.Dispose() }
}

function Get-ReleaseInventory([string]$Directory) {
    return @(Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ fileName = $_.Name; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}

function New-ReleaseProvenance($Plan, [string]$ReleaseDirectory, [long]$CiRunId, [long]$VerificationRunId) {
    if ($CiRunId -le 0 -or $VerificationRunId -le 0) { throw 'Both trusted CI and fresh verification execution IDs are required.' }
    $provenance = [ordered]@{
        schemaVersion = 1; sourceSha = $Plan.sourceSha; version = $Plan.version; tag = $Plan.tag
        lastSourceSha = $Plan.lastSourceSha; lastTag = $Plan.lastTag; ciRunId = $CiRunId; verificationRunId = $VerificationRunId
        createdAt = [DateTimeOffset]::UtcNow.ToString('O'); artifacts = @(Get-ReleaseInventory $ReleaseDirectory)
    } | ConvertTo-Json -Depth 8
    $stream = [IO.File]::Open((Join-Path $ReleaseDirectory 'release-provenance.json'), [IO.FileMode]::CreateNew)
    try { $stream.Write([Text.Encoding]::UTF8.GetBytes($provenance)); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Assert-ReleaseInventory([string]$ReleaseDirectory, [string]$Version, [Security.Cryptography.ECDsa]$Key, [string]$KeyId) {
    Assert-ReleaseVersion $Version
    $expected = @(
        'geevensingh.OrgExplorer-win-Setup.exe', "geevensingh.OrgExplorer-$Version-full.nupkg",
        'geevensingh.OrgExplorer-win-Portable.zip', 'releases.win.json', 'assets.win.json', 'RELEASES',
        'package-metadata.json', 'org-explorer-update.win-x64.json', 'release-provenance.json'
    )
    $files = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Force)
    if ($files.Count -ne $expected.Count -or @($files | Where-Object { $_.PSIsContainer -or $_.Length -le 0 }).Count -gt 0 -or
        (Compare-Object @($expected | Sort-Object) @($files.Name | Sort-Object) -CaseSensitive)) {
        throw 'Release assets must be exactly the installer, full package, portable, three Velopack feeds/inventories, package metadata, signed manifest and provenance. Missing/extra/private/fixture files cannot publish.'
    }
    $actual = @(Get-ReleaseInventory $ReleaseDirectory)
    $provenance = Get-Content (Join-Path $ReleaseDirectory 'release-provenance.json') -Raw | ConvertFrom-Json
    $metadata = Get-Content (Join-Path $ReleaseDirectory 'package-metadata.json') -Raw | ConvertFrom-Json
    if ($provenance.schemaVersion -ne 1 -or $provenance.version -cne $Version -or $provenance.tag -cne "v$Version") { throw 'Artifact provenance does not match the candidate version.' }
    if ($metadata.schemaVersion -ne 1 -or $metadata.appId -cne 'geevensingh.OrgExplorer' -or
        $metadata.version -cne $Version -or $metadata.platform -cne 'win-x64' -or $metadata.channel -cne 'win') { throw 'Package inventory identity differs from signed app identity.' }
    foreach ($inventorySource in @(
        @{ items = $metadata.artifacts; required = $expected[0..5] },
        @{ items = $provenance.artifacts; required = @($expected | Where-Object { $_ -cne 'release-provenance.json' }) }
    )) {
        $inventory = $inventorySource.items
        $names = @($inventory.fileName)
        $required = $inventorySource.required
        if ($names.Count -ne $required.Count -or (Compare-Object @($required | Sort-Object) @($names | Sort-Object) -CaseSensitive)) { throw 'Incomplete or duplicate artifact inventory.' }
        foreach ($item in $inventory) {
            $file = @($actual | Where-Object { $_.fileName -ceq $item.fileName })
            if ($file.Count -ne 1 -or $item.size -ne $file[0].size -or $item.sha256 -cne $file[0].sha256) { throw 'Artifact inventory changed after packaging/signing.' }
        }
    }
    $envelope = Get-Content (Join-Path $ReleaseDirectory 'org-explorer-update.win-x64.json') -Raw | ConvertFrom-Json
    $bytes = [Convert]::FromBase64String($envelope.payload)
    $signature = [Convert]::FromBase64String($envelope.signature)
    if ($envelope.keyId -cne $KeyId -or $envelope.algorithm -cne 'ES256' -or $signature.Length -ne 64 -or
        !$Key.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Release manifest authentication failed.' }
    $payload = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    $package = @($actual | Where-Object { $_.fileName -ceq "geevensingh.OrgExplorer-$Version-full.nupkg" })[0]
    if ($payload.protocolVersion -ne 1 -or $payload.appId -cne 'geevensingh.OrgExplorer' -or $payload.version -cne $Version -or
        $payload.platform -cne 'win-x64' -or $payload.channel -cne 'win' -or $payload.releaseTag -cne "v$Version" -or
        @($payload.assets).Count -ne 1 -or $payload.assets[0].kind -cne 'full' -or $payload.assets[0].fileName -cne $package.fileName -or
        $payload.assets[0].size -ne $package.size -or $payload.assets[0].sha256 -cne $package.sha256 -or
        $payload.assets[0].url -cne "https://github.com/geevensingh/org-explorer/releases/download/v$Version/$($package.fileName)" -or
        [DateTimeOffset]$payload.issuedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or
        [DateTimeOffset]$payload.expiresAt -le [DateTimeOffset]::UtcNow -or
        ([DateTimeOffset]$payload.expiresAt - [DateTimeOffset]$payload.issuedAt).TotalDays -gt 31 -or
        [DateTimeOffset]$payload.expiresAt -le [DateTimeOffset]$payload.issuedAt) { throw 'Signed authorization does not bind this exact immutable full package or validity interval.' }
    $feed = Get-Content (Join-Path $ReleaseDirectory 'releases.win.json') -Raw | ConvertFrom-Json
    $sha1 = (Get-FileHash (Join-Path $ReleaseDirectory $package.fileName) -Algorithm SHA1).Hash
    if (@($feed.Assets).Count -ne 1 -or $feed.Assets[0].PackageId -cne 'geevensingh.OrgExplorer' -or
        $feed.Assets[0].Type -cne 'Full' -or $feed.Assets[0].Version -cne $Version -or
        $feed.Assets[0].FileName -cne $package.fileName -or $feed.Assets[0].Size -ne $package.size -or
        $feed.Assets[0].SHA256 -ine $package.sha256 -or $feed.Assets[0].SHA1 -ine $sha1) { throw 'Velopack feed and signed authorization disagree.' }
    $assets = @(Get-Content (Join-Path $ReleaseDirectory 'assets.win.json') -Raw | ConvertFrom-Json)
    if ($assets.Count -ne 3) { throw 'Velopack asset index must contain exactly installer/full/portable.' }
    foreach ($entry in @(
        @{ type = 'Installer'; name = 'geevensingh.OrgExplorer-win-Setup.exe' },
        @{ type = 'Full'; name = $package.fileName },
        @{ type = 'Portable'; name = 'geevensingh.OrgExplorer-win-Portable.zip' }
    )) {
        $matches = @($assets | Where-Object { $_.Type -ceq $entry.type -and $_.RelativeFileName -ceq $entry.name })
        if ($matches.Count -ne 1) { throw 'Velopack asset index differs from the authorized release inventory.' }
    }
    $legacy = @(Get-Content (Join-Path $ReleaseDirectory 'RELEASES') | Where-Object { $_.Trim().Length -gt 0 })
    if ($legacy.Count -ne 1 -or $legacy[0].Trim() -ine "$sha1 $($package.fileName) $($package.size)") {
        throw 'Legacy RELEASES index differs from the full package. SHA1 is legacy consistency only, never authentication.'
    }
}
