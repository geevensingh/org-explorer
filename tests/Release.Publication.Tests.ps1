$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\Release.ps1"
$candidate = 'a' * 40
$checks = 0
function Assert([bool]$Condition, [string]$Reason) { if (!$Condition) { throw $Reason }; $script:checks++ }
function Rejects([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert $rejected "Release accepted $Reason."
}
$run = @{
    id = 42; event = 'push'; head_branch = 'main'; head_sha = $candidate; conclusion = 'success'; status = 'completed'
    path = '.github/workflows/ci.yml'; head_repository = @{ full_name = 'geevensingh/org-explorer' }
}
$jobNames = @('Core and view models', 'Real filesystem persistence', 'Actual Windows desktop smoke', 'CI')
$jobs = @($jobNames | ForEach-Object { @{ name = $_; status = 'completed'; conclusion = 'success' } })
$main = $candidate
$effects = [Collections.Generic.List[string]]::new()
$releases = @()
$reservedRefs = @()
$assetResponse = $null
$remoteAssets = @()
$draft = $false
$failUpload = $false
$moveMainOnUpload = $false
$badDigest = $false
$missingRemoteAsset = $false
$fixture = Join-Path $PSScriptRoot ("..\.test-workspaces\release-publication-" + [guid]::NewGuid().ToString('N'))
New-Item $fixture -ItemType Directory -Force | Out-Null

# The only fake is the external GitHub CLI boundary. No production API is called.
function gh {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'release' -and $arguments[1] -eq 'upload') {
        if ($script:failUpload) { $global:LASTEXITCODE = 1; return }
        $script:effects.Add('upload')
        if ($script:moveMainOnUpload) { $script:main = 'b' * 40 }
        $script:remoteAssets = @(Get-ChildItem $fixture -File | ForEach-Object {
            @{ name = $_.Name; size = $_.Length; digest = 'sha256:' + (Get-FileHash $_.FullName).Hash.ToLowerInvariant() }
        })
        if ($script:badDigest) { $script:remoteAssets[0].digest = $null }
        if ($script:missingRemoteAsset) { $script:remoteAssets = @($script:remoteAssets | Select-Object -Skip 1) }
        return
    }
    if ($arguments[0] -ne 'api') { throw 'Unexpected fake GitHub operation.' }
    $endpoint = [string]$arguments[1]
    $methodIndex = [Array]::IndexOf($arguments, '--method')
    $method = if ($methodIndex -ge 0) { $arguments[$methodIndex + 1] } else { 'GET' }
    switch -Regex ($endpoint) {
        '/git/matching-refs/tags/v$' { ConvertTo-Json -InputObject @(@($script:reservedRefs)) -Depth 10 -Compress; return }
        '/actions/runs/42/jobs$' { @{ jobs = $script:jobs } | ConvertTo-Json -Depth 10 -Compress; return }
        '/actions/runs/42$' { $script:run | ConvertTo-Json -Depth 10 -Compress; return }
        '/git/ref/heads/main$' { @{ object = @{ sha = $script:main } } | ConvertTo-Json -Compress; return }
        '/git/ref/tags/v0.1.0$' { @{ object = @{ sha = $candidate; type = 'commit' } } | ConvertTo-Json -Compress; return }
        '/git/refs$' {
            Assert ($method -eq 'POST') 'Tags must be created, never force-updated.'
            $script:effects.Add('tag'); '{"object":{}}'; return
        }
        '/releases/assets/7$' { $script:assetResponse; return }
        '/releases/99$' {
            if ($method -eq 'PATCH') {
                $inputIndex = [Array]::IndexOf($arguments, '--input')
                $body = Get-Content $arguments[$inputIndex + 1] -Raw | ConvertFrom-Json
                Assert ($body.draft -eq $false -and $body.make_latest -ceq 'true') 'Only fully uploaded verified drafts may become latest.'
                $script:effects.Add('publish'); $script:draft = $false
            }
            @{ id = 99; tag_name = 'v0.1.0'; draft = $script:draft; prerelease = $false; assets = $script:remoteAssets } | ConvertTo-Json -Depth 10 -Compress
            return
        }
        '/releases$' {
            if ($method -eq 'POST') {
                $inputIndex = [Array]::IndexOf($arguments, '--input')
                $body = Get-Content $arguments[$inputIndex + 1] -Raw | ConvertFrom-Json
                Assert ($body.target_commitish -ceq $candidate -and $body.draft -eq $true -and $body.make_latest -ceq 'false') 'Public assets must not precede provenance and gates.'
                $script:effects.Add('draft'); $script:draft = $true
                '{"id":99}'; return
            }
            ConvertTo-Json -InputObject @(@($script:releases)) -Depth 10 -Compress; return
        }
        default { throw "Unexpected fake endpoint: $endpoint" }
    }
}
try {
    Assert-TrustedMainCi -Candidate $candidate -CiRunId 42
    foreach ($field in @('event', 'head_branch', 'head_sha', 'conclusion', 'path')) {
        $old = $run[$field]; $run[$field] = 'untrusted'
        Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } "untrusted CI $field"
        $run[$field] = $old
    }
    $run.head_repository.full_name = 'attacker/fork'
    Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } 'fork-origin CI'
    $run.head_repository.full_name = 'geevensingh/org-explorer'
    foreach ($bad in @('failure', 'skipped', 'cancelled', 'timed_out', '')) {
        $jobs[2].conclusion = $bad
        Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } "required lane $bad"
    }
    $jobs[2].conclusion = 'success'
    $savedJobs = $jobs; $jobs = @($jobs[0], $jobs[1], $jobs[3])
    Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } 'missing required desktop lane'
    $jobs = $savedJobs
    $main = 'b' * 40
    Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } 'older finishing candidate after main moves'
    $main = $candidate
    Assert ($effects.Count -eq 0) 'Rejected candidates must have no publication effects.'
    Assert ($null -eq (Get-LastSuccessfulRelease)) 'No publication is an explicit first-release state.'
    Assert (@(Get-ReservedReleaseVersions).Count -eq 0) 'No tags must allow an automatic first release.'
    $reservedRefs = @(@{ ref = 'refs/tags/v0.1.0' }, @{ ref = 'refs/tags/not-a-version' })
    Assert ((@(Get-ReservedReleaseVersions) -join ',') -ceq '0.1.0') 'Only canonical app-version tag reservations count.'
    $releases = @(@{ draft = $true; prerelease = $false }, @{ draft = $false; prerelease = $true })
    Assert ($null -eq (Get-LastSuccessfulRelease)) 'Failed drafts and prereleases must not consume app versions or become comparison baselines.'
    $release = @{ draft = $false; prerelease = $false; published_at = '2026-11-09T00:00:00Z'; tag_name = 'v0.1.0'; assets = @() }
    $releases = @($release)
    Rejects { Get-LastSuccessfulRelease } 'a legacy release without exact source provenance'
    $release.assets = @(@{ name = 'release-provenance.json'; id = 7 })
    $assetResponse = @{ schemaVersion = 1; sourceSha = $candidate; tag = 'v0.1.0'; version = '0.1.0'; ciRunId = 42; verificationRunId = 43 } | ConvertTo-Json
    Assert ((Get-LastSuccessfulRelease).sourceSha -ceq $candidate) 'The last successful publication must supply its source, not an adjacent commit.'
    $assetResponse = $assetResponse.Replace($candidate, ('c' * 40))
    Rejects { Get-LastSuccessfulRelease } 'tag/source provenance mismatch'
    $releases = @()

    $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $plan = @{ publish = $true; sourceSha = $candidate; lastSourceSha = ''; lastTag = ''; version = '0.1.0'; tag = 'v0.1.0' }
        foreach ($name in @('geevensingh.OrgExplorer-win-Setup.exe', 'geevensingh.OrgExplorer-0.1.0-full.nupkg',
            'geevensingh.OrgExplorer-win-Portable.zip')) {
            [IO.File]::WriteAllBytes((Join-Path $fixture $name), [Text.Encoding]::UTF8.GetBytes('abc'))
        }
        @(@{ RelativeFileName = 'geevensingh.OrgExplorer-win-Setup.exe'; Type = 'Installer' },
            @{ RelativeFileName = 'geevensingh.OrgExplorer-0.1.0-full.nupkg'; Type = 'Full' },
            @{ RelativeFileName = 'geevensingh.OrgExplorer-win-Portable.zip'; Type = 'Portable' }) |
            ConvertTo-Json | Set-Content "$fixture\assets.win.json"
        'A9993E364706816ABA3E25717850C26C9CD0D89D geevensingh.OrgExplorer-0.1.0-full.nupkg 3' | Set-Content "$fixture\RELEASES"
        @{ Assets = @(@{ PackageId = 'geevensingh.OrgExplorer'; Type = 'Full'; Version = '0.1.0'; FileName = 'geevensingh.OrgExplorer-0.1.0-full.nupkg'; Size = 3
            SHA1 = 'A9993E364706816ABA3E25717850C26C9CD0D89D'; SHA256 = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad' }) } |
            ConvertTo-Json -Depth 5 | Set-Content "$fixture\releases.win.json"
        @{ schemaVersion = 1; appId = 'geevensingh.OrgExplorer'; version = '0.1.0'; platform = 'win-x64'; channel = 'win'
            artifacts = @(Get-ChildItem $fixture -File | ForEach-Object {
            @{ fileName = $_.Name; size = $_.Length; sha256 = (Get-FileHash $_.FullName).Hash.ToLowerInvariant() }
        }) } | ConvertTo-Json -Depth 5 | Set-Content "$fixture\package-metadata.json"
        $null = New-SignedReleaseManifest -ReleaseDirectory $fixture -Version 0.1.0 -Key $key -KeyId test-active
        New-ReleaseProvenance -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -VerificationRunId 43
        Assert-ReleaseInventory -ReleaseDirectory $fixture -Version 0.1.0 -Key $key -KeyId test-active
        $originalAssets = Get-Content "$fixture\assets.win.json" -Raw
        $originalMetadata = Get-Content "$fixture\package-metadata.json" -Raw
        $originalProvenance = Get-Content "$fixture\release-provenance.json" -Raw
        $originalAssets.Replace('-0.1.0-full', '-9.9.9-full') | Set-Content "$fixture\assets.win.json"
        $metadata = $originalMetadata | ConvertFrom-Json
        $changed = $metadata.artifacts | Where-Object fileName -CEQ 'assets.win.json'
        $changed.sha256 = (Get-FileHash "$fixture\assets.win.json").Hash.ToLowerInvariant()
        $changed.size = (Get-Item "$fixture\assets.win.json").Length
        $metadata | ConvertTo-Json -Depth 8 | Set-Content "$fixture\package-metadata.json"
        Remove-Item "$fixture\release-provenance.json"
        New-ReleaseProvenance -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -VerificationRunId 43
        Rejects { Assert-ReleaseInventory -ReleaseDirectory $fixture -Version 0.1.0 -Key $key -KeyId test-active } 'a self-consistent inventory pointing a second feed at another app version'
        $originalAssets | Set-Content "$fixture\assets.win.json" -NoNewline
        $originalMetadata | Set-Content "$fixture\package-metadata.json" -NoNewline
        $originalProvenance | Set-Content "$fixture\release-provenance.json" -NoNewline
        'private material' | Set-Content "$fixture\private.pkcs8"
        Rejects { Assert-ReleaseInventory -ReleaseDirectory $fixture -Version 0.1.0 -Key $key -KeyId test-active } 'unexpected private/fixture publication input'
        Remove-Item "$fixture\private.pkcs8"
        Move-Item "$fixture\geevensingh.OrgExplorer-win-Setup.exe" "$fixture\missing-setup"
        Rejects { Assert-ReleaseInventory -ReleaseDirectory $fixture -Version 0.1.0 -Key $key -KeyId test-active } 'missing required installer'
        Move-Item "$fixture\missing-setup" "$fixture\geevensingh.OrgExplorer-win-Setup.exe"
        $saved = [IO.File]::ReadAllBytes("$fixture\geevensingh.OrgExplorer-0.1.0-full.nupkg")
        'tampered' | Set-Content "$fixture\geevensingh.OrgExplorer-0.1.0-full.nupkg"
        Rejects { Assert-ReleaseInventory -ReleaseDirectory $fixture -Version 0.1.0 -Key $key -KeyId test-active } 'changed package after authorization'
        [IO.File]::WriteAllBytes("$fixture\geevensingh.OrgExplorer-0.1.0-full.nupkg", $saved)
        $failUpload = $true
        Rejects { Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active } 'partial upload'
        Assert (!$effects.Contains('publish') -and $draft) 'Upload failure must leave only an unpublished draft.'
        $failUpload = $false; $effects.Clear(); $draft = $false
        $badDigest = $true
        Rejects { Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active } 'missing GitHub-uploaded digest'
        Assert (!$effects.Contains('publish')) 'Unavailable remote digest must block promotion.'
        $badDigest = $false; $effects.Clear(); $draft = $false; $missingRemoteAsset = $true
        Rejects { Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active } 'incomplete remote upload'
        Assert (!$effects.Contains('publish')) 'Missing remote asset must block promotion.'
        $missingRemoteAsset = $false; $effects.Clear(); $draft = $false
        $moveMainOnUpload = $true
        Rejects { Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active } 'main advancing during upload'
        Assert (!$effects.Contains('publish') -and $draft) 'A superseded candidate must never become public/latest.'
        $moveMainOnUpload = $false; $main = $candidate; $effects.Clear(); $draft = $false
        Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active
        Assert (($effects -join ',') -ceq 'tag,draft,upload,publish') 'Publish only a complete verified draft, with immutable exact-source tag and latest updated last.'
    } finally { $key.Dispose() }
    Write-Host "PASS: $checks trusted publication checks."
} finally {
    Remove-Item Function:\gh
    Remove-Item $fixture -Recurse -Force
}
