$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\Release.ps1"
$fixture = Join-Path $PSScriptRoot ("..\.test-workspaces\release-publication-" + [guid]::NewGuid().ToString('N'))
$history = "$fixture-history"
New-Item $fixture, $history -ItemType Directory -Force | Out-Null
git -C $history init --quiet
$candidateMessage = 'Ship café 🚀 *bold* _notes_ [link](https://example.invalid) `code` ``ticks`` <b> & &#65; \ #64 @owner :smile: ~done~ ! |'
$storageMessage = 'Make Workspace saves durable without blocking Person browsing (#119) café `notes` @owner'
foreach ($message in @('Previously released source', 'Make update-org startup, resume, and reporting turnkey (#115)',
    $storageMessage,
    "$candidateMessage`n`nPrivate candidate body must not enter the description.",
    'Adjacent newer revision must not enter the description')) {
    git -C $history -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet --allow-empty -m $message
    if ($LASTEXITCODE -ne 0) { throw 'Publication source fixture commit failed.' }
}
$candidate = (git -C $history rev-parse HEAD~1).Trim()
$storageSource = (git -C $history rev-parse HEAD~2).Trim()
$startupSource = (git -C $history rev-parse HEAD~3).Trim()
$previousSource = (git -C $history rev-parse HEAD~4).Trim()
$checks = 0
function Assert([bool]$Condition, [string]$Reason) { if (!$Condition) { throw $Reason }; $script:checks++ }
function Rejects([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert $rejected "Release accepted $Reason."
}
function Update-FixtureProvenance([hashtable]$Plan) {
    Remove-Item "$fixture\release-provenance.json"
    New-ReleaseProvenance -Plan $Plan -ReleaseDirectory $fixture -CiRunId 42 -VerificationRunId 43
}
$run = @{
    id = 42; event = 'push'; head_branch = 'main'; head_sha = $candidate; conclusion = 'success'; status = 'completed'
    path = '.github/workflows/ci.yml'; head_repository = @{ full_name = 'geevensingh/org-explorer' }
}
$jobNames = @('Core and view models', 'Real filesystem persistence',
    'Actual Windows desktop smoke (people)', 'Actual Windows desktop smoke (workspace)',
    'High-resolution Windows smoke (reading)', 'High-resolution Windows smoke (context)', 'CI')
$jobs = @($jobNames | ForEach-Object { @{ name = $_; status = 'completed'; conclusion = 'success' } })
$workflowBlob = '6bca4e7e585fef8e9a8c2b6cbdb6a0562bcb5cb9'
$originalCiJobs = $null
$extraJobPage = @()
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
$currentTag = 'v0.1.0'
$uploadDirectory = $null
$latestRelease = $null
$manifestResponse = $null
$renewalProvenanceResponse = $null
$missingCurrentCi = $false
$createdBody = $null

# The only fake is the external GitHub CLI boundary. No production API is called.
function gh {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'release' -and $arguments[1] -eq 'upload') {
        if ($script:failUpload) { $global:LASTEXITCODE = 1; return }
        $script:effects.Add('upload')
        if ($script:moveMainOnUpload) { $script:main = 'b' * 40 }
        $directory = if ($script:uploadDirectory) { $script:uploadDirectory } else { $fixture }
        $script:remoteAssets = @(Get-ChildItem $directory -File | ForEach-Object {
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
        '/actions/runs/(42|44)/jobs\?per_page=100$' {
            if ($arguments -cnotcontains '--paginate' -or $arguments -cnotcontains '--slurp') {
                throw 'CI validation must inspect every jobs page.'
            }
            $selectedJobs = if ($endpoint.Contains('/42/') -and $null -ne $script:originalCiJobs) {
                $script:originalCiJobs
            } else { $script:jobs }
            ConvertTo-Json -InputObject @(@{ jobs = @($selectedJobs) }, @{ jobs = $script:extraJobPage }) -Depth 10 -Compress
            return
        }
        '/contents/\.github/workflows/ci\.yml\?ref=' {
            if ($endpoint -cne "repos/geevensingh/org-explorer/contents/.github/workflows/ci.yml?ref=$candidate") {
                throw 'Historical CI policy must come from the exact authenticated original source, not main.'
            }
            @{ type = 'file'; path = '.github/workflows/ci.yml'; sha = $script:workflowBlob } | ConvertTo-Json -Compress
            return
        }
        '/actions/runs/42$' { $script:run | ConvertTo-Json -Depth 10 -Compress; return }
        '/actions/runs/44$' { $newRun = $script:run.Clone(); $newRun.id = 44; $newRun.head_sha = 'd' * 40; $newRun | ConvertTo-Json -Depth 10 -Compress; return }
        '/actions/workflows/ci.yml/runs\?' {
            @{ workflow_runs = if ($script:missingCurrentCi) { @() } else { @($script:run) } } | ConvertTo-Json -Depth 10 -Compress; return
        }
        '/git/ref/heads/main$' { @{ object = @{ sha = $script:main } } | ConvertTo-Json -Compress; return }
        '/git/ref/tags/(v0.0.9|metadata-v0.0.9-45-1)$' { @{ object = @{ sha = $previousSource; type = 'commit' } } | ConvertTo-Json -Compress; return }
        '/git/ref/tags/(v0.1.0|metadata-v0.1.0-[0-9]+-[0-9]+)$' { @{ object = @{ sha = $candidate; type = 'commit' } } | ConvertTo-Json -Compress; return }
        '/git/refs$' {
            Assert ($method -eq 'POST') 'Tags must be created, never force-updated.'
            $script:effects.Add('tag'); '{"object":{}}'; return
        }
        '/releases/assets/7$' { $script:assetResponse; return }
        '/releases/assets/8$' { $script:manifestResponse; return }
        '/releases/assets/9$' { $script:renewalProvenanceResponse; return }
        '/releases/latest$' { $script:latestRelease | ConvertTo-Json -Depth 10 -Compress; return }
        '/releases/99$' {
            if ($method -eq 'PATCH') {
                $inputIndex = [Array]::IndexOf($arguments, '--input')
                $body = Get-Content $arguments[$inputIndex + 1] -Raw | ConvertFrom-Json
                Assert ($body.draft -eq $false -and $body.make_latest -ceq 'true') 'Only fully uploaded verified drafts may become latest.'
                $script:effects.Add('publish'); $script:draft = $false
            }
            @{ id = 99; tag_name = $script:currentTag; draft = $script:draft; prerelease = $false; assets = $script:remoteAssets } | ConvertTo-Json -Depth 10 -Compress
            return
        }
        '/releases$' {
            if ($method -eq 'POST') {
                $inputIndex = [Array]::IndexOf($arguments, '--input')
                $body = Get-Content $arguments[$inputIndex + 1] -Raw | ConvertFrom-Json
                $script:currentTag = $body.tag_name
                $script:createdBody = $body.body
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
    Assert-TrustedCi -Candidate $candidate -CiRunId 42
    foreach ($field in @('id', 'event', 'head_branch', 'head_sha', 'status', 'conclusion', 'path')) {
        $old = $run[$field]; $run[$field] = 'untrusted'
        Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } "untrusted CI $field"
        $run[$field] = $old
    }
    $run.head_repository.full_name = 'attacker/fork'
    Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } 'fork-origin CI'
    $run.head_repository.full_name = 'geevensingh/org-explorer'
    $currentJobs = $jobs
    foreach ($job in $currentJobs) {
        foreach ($bad in @('failure', 'skipped', 'cancelled', 'timed_out', '')) {
            $job.conclusion = $bad
            Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } "required $($job.name) $bad"
        }
        $job.conclusion = 'success'
        $job.status = 'in_progress'
        Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } "unfinished $($job.name)"
        $job.status = 'completed'
        $jobs = @($currentJobs | Where-Object { $_ -ne $job })
        Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } "missing $($job.name)"
        $jobs = @($currentJobs) + @($job)
        Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } "duplicated $($job.name)"
        $jobs = $currentJobs
    }
    $extraJobPage = @($jobs[2])
    Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } 'a duplicated partition on a later jobs page'
    $extraJobPage = @()
    foreach ($layout in @(
        @{ blob = '959f172a7a495274b15dd95598930d6ca17c8084'; highResolution = $false },
        @{ blob = '2e07763102c4fd4187d6ea1f4094120e5efd0fc2'; highResolution = $false },
        @{ blob = '299ed12a0c9513ec6740bd4300df7466a6399d05'; highResolution = $true },
        @{ blob = '044330069aa2c0e80f663aad6816dc6795a54865'; highResolution = $true },
        @{ blob = 'a8070b98ddb256dbf6dfd8880c0ededfe9cdb369'; highResolution = $true }
    )) {
        $workflowBlob = $layout.blob
        $historicalNames = @('Core and view models', 'Real filesystem persistence', 'Actual Windows desktop smoke', 'CI')
        if ($layout.highResolution) { $historicalNames += 'High-resolution Windows smoke' }
        $jobs = @($historicalNames | ForEach-Object { @{ name = $_; status = 'completed'; conclusion = 'success' } })
        Assert-TrustedCi -Candidate $candidate -CiRunId 42
        Rejects { Assert-TrustedMainCi -Candidate $candidate -CiRunId 42 } 'a historical-layout downgrade of current main approval'
        $historicalJobs = $jobs
        foreach ($job in $historicalJobs) {
            $jobs = @($historicalJobs | Where-Object { $_ -ne $job })
            Rejects { Assert-TrustedCi -Candidate $candidate -CiRunId 42 } "missing historical $($job.name)"
            $jobs = @($historicalJobs) + @($job)
            Rejects { Assert-TrustedCi -Candidate $candidate -CiRunId 42 } "duplicate historical $($job.name)"
            $jobs = $historicalJobs
            $job.conclusion = 'skipped'
            Rejects { Assert-TrustedCi -Candidate $candidate -CiRunId 42 } "skipped historical $($job.name)"
            $job.conclusion = 'success'
        }
        $jobs = @($historicalJobs) + @($currentJobs[2])
        Rejects { Assert-TrustedCi -Candidate $candidate -CiRunId 42 } 'a spoofed mixed historical/current subset'
        $jobs = $currentJobs
        Rejects { Assert-TrustedCi -Candidate $candidate -CiRunId 42 } 'current job names substituted for the historical source layout'
    }
    $workflowBlob = '6bca4e7e585fef8e9a8c2b6cbdb6a0562bcb5cb9'
    $jobs = $historicalJobs
    Rejects { Assert-TrustedCi -Candidate $candidate -CiRunId 42 } 'historical job names substituted for current source policy'
    $jobs = @($currentJobs[0], $currentJobs[1], $historicalJobs[2], $currentJobs[4], $currentJobs[5], $currentJobs[6])
    Rejects { Assert-TrustedCi -Candidate $candidate -CiRunId 42 } 'a singular desktop substituted for the required current partition pair'
    $jobs = $currentJobs
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
    $release = @{ id = 1; draft = $false; prerelease = $false; published_at = '2026-11-09T00:00:00Z'; tag_name = 'v0.1.0'; assets = @() }
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
        Rejects { Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history } 'partial upload'
        Assert (!$effects.Contains('publish') -and $draft) 'Upload failure must leave only an unpublished draft.'
        $failUpload = $false; $effects.Clear(); $draft = $false
        $badDigest = $true
        Rejects { Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history } 'missing GitHub-uploaded digest'
        Assert (!$effects.Contains('publish')) 'Unavailable remote digest must block promotion.'
        $badDigest = $false; $effects.Clear(); $draft = $false; $missingRemoteAsset = $true
        Rejects { Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history } 'incomplete remote upload'
        Assert (!$effects.Contains('publish')) 'Missing remote asset must block promotion.'
        $missingRemoteAsset = $false; $effects.Clear(); $draft = $false
        $moveMainOnUpload = $true
        Rejects { Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history } 'main advancing during upload'
        Assert (!$effects.Contains('publish') -and $draft) 'A superseded candidate must never become public/latest.'
        $moveMainOnUpload = $false; $main = $candidate; $effects.Clear(); $draft = $false
        $consoleEncoding = [Console]::OutputEncoding
        try {
            [Console]::OutputEncoding = [Text.Encoding]::GetEncoding(1252)
            Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history
        } finally { [Console]::OutputEncoding = $consoleEncoding }
        Assert (($effects -join ',') -ceq 'tag,draft,upload,publish') 'Publish only a complete verified draft, with immutable exact-source tag and latest updated last.'
        $expectedProvenance = "Exact source: $candidate. Trusted CI: 42. Fresh complete scale verification: 43. See release-provenance.json for immutable asset hashes. Project-key authenticated updates; not Windows publisher signing."
        $expectedSubject = '``` Ship café 🚀 *bold* _notes_ [link](https://example.invalid) `code` ``ticks`` <b> & &#65; \ #64 @owner :smile: ~done~ ! | ```'
        $expectedPendingChanges = @(
            "- [$($startupSource.Substring(0, 7))](https://github.com/geevensingh/org-explorer/commit/$startupSource) " + '` Make update-org startup, resume, and reporting turnkey (#115) `'
            "- [$($storageSource.Substring(0, 7))](https://github.com/geevensingh/org-explorer/commit/$storageSource) " + '`` Make Workspace saves durable without blocking Person browsing (#119) café `notes` @owner ``'
            "- [$($candidate.Substring(0, 7))](https://github.com/geevensingh/org-explorer/commit/$candidate) $expectedSubject"
        ) -join "`n"
        $expectedFirstChanges = @(
            '## Changes since the last successful release'
            ''
            'First product release; no previous successful app release.'
            ''
            "- [$($previousSource.Substring(0, 7))](https://github.com/geevensingh/org-explorer/commit/$previousSource) " + '` Previously released source `'
            $expectedPendingChanges
            ''
            '## Technical provenance'
            ''
            "Source commit: $expectedSubject"
            ''
            $expectedProvenance
        ) -join "`n"
        Assert ($createdBody -ceq $expectedFirstChanges) 'A first product publication must explicitly identify the absence of a previous successful app release and list its complete mainline history through the exact candidate, without inventing a comparison baseline.'
        $releases = @(@{ id = 1; draft = $false; prerelease = $false; published_at = '2026-11-09T00:00:00Z'
            tag_name = 'v0.0.9'; assets = @(@{ name = 'release-provenance.json'; id = 7 }) })
        $assetResponse = @{ schemaVersion = 1; sourceSha = $previousSource; tag = 'v0.0.9'; version = '0.0.9'; ciRunId = 40; verificationRunId = 41 } | ConvertTo-Json
        $plan.lastSourceSha = $previousSource; $plan.lastTag = 'v0.0.9'
        Update-FixtureProvenance -Plan $plan
        $effects.Clear(); $draft = $false
        Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history
        $expectedChanges = @(
            '## Changes since the last successful release'
            ''
            "Since [v0.0.9](https://github.com/geevensingh/org-explorer/releases/tag/v0.0.9). [Full comparison](https://github.com/geevensingh/org-explorer/compare/$previousSource...$candidate)."
            ''
            $expectedPendingChanges
            ''
            '## Technical provenance'
            ''
        ) -join "`n"
        Assert ($createdBody -ceq "$expectedChanges`nSource commit: $expectedSubject`n`n$expectedProvenance") 'The description must cover all three pending mainline changes chronologically, including durable Workspace saves, with literal subjects, exact-source links and unchanged technical provenance; released history, newer HEAD and private bodies must not enter it.'
        $previousRelease = $releases[0]
        $previousRelease.assets[0].digest = 'sha256:' + ('b' * 64)
        $renewalProvenanceResponse = @{
            schemaVersion = 1; kind = 'metadata-renewal'; sourceSha = $previousSource; tag = 'v0.0.9'; version = '0.0.9'
            ciRunId = 40; verificationRunId = 45; publicationTag = 'metadata-v0.0.9-45-1'
            originalReleaseId = 1; originalProvenanceSha256 = 'b' * 64; controllerSha = $storageSource; controllerCiRunId = 42
        } | ConvertTo-Json
        $releases = @($previousRelease,
            @{ id = 2; draft = $false; prerelease = $false; published_at = '2026-11-10T00:00:00Z'
                tag_name = 'metadata-v0.0.9-45-1'; assets = @(@{ name = 'release-provenance.json'; id = 9 }) },
            @{ id = 3; draft = $true; prerelease = $false; published_at = '2026-11-11T00:00:00Z'; tag_name = 'v0.1.0'; assets = @() },
            @{ id = 4; draft = $false; prerelease = $true; published_at = '2026-11-12T00:00:00Z'; tag_name = 'v0.1.1'; assets = @() })
        $lastApp = Get-LastSuccessfulRelease
        $plan.lastSourceSha = $lastApp.sourceSha; $plan.lastTag = $lastApp.tag
        Update-FixtureProvenance -Plan $plan
        $effects.Clear(); $draft = $false
        Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history
        Assert ($lastApp.sourceSha -ceq $previousSource -and $lastApp.tag -ceq 'v0.0.9' -and
            $createdBody -ceq "$expectedChanges`nSource commit: $expectedSubject`n`n$expectedProvenance") 'Later failed drafts, prereleases and authenticated metadata renewal must not truncate cumulative changes at a failed source or the renewal controller; the original published app source remains the baseline.'
        $releases = @($previousRelease)
        $savedCandidate = $candidate
        $savedPreviousSource = $previousSource
        $tree = (git -C $history rev-parse "$candidate^{tree}").Trim()
        $sideSource = (git -C $history -c user.name=Fixture -c user.email=fixture@example.invalid commit-tree $tree -p $previousSource -m 'WIP implementation must not be duplicated').Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Side-history fixture commit failed.' }
        $candidate = (git -C $history -c user.name=Fixture -c user.email=fixture@example.invalid commit-tree $tree -p $candidate -p $sideSource -m 'Merge reviewed work (#122)').Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Mainline merge fixture commit failed.' }
        $plan.sourceSha = $candidate; $main = $candidate; $run.head_sha = $candidate
        Update-FixtureProvenance -Plan $plan
        $effects.Clear(); $draft = $false
        Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history
        Assert ($createdBody.Contains($expectedPendingChanges) -and
            $createdBody.Contains("- [$($candidate.Substring(0, 7))](https://github.com/geevensingh/org-explorer/commit/$candidate) " + '` Merge reviewed work (#122) `') -and
            $createdBody.Contains("/compare/$previousSource...$candidate") -and
            [regex]::Matches($createdBody, '(?m)^- ').Count -eq 4 -and
            !$createdBody.Contains($sideSource) -and !$createdBody.Contains('WIP implementation')) 'Mainline merge subjects must cover the release without duplicating side-branch work-in-progress commits.'
        $previousSource = $sideSource
        $plan.lastSourceSha = $sideSource
        $assetResponse = @{ schemaVersion = 1; sourceSha = $sideSource; tag = 'v0.0.9'; version = '0.0.9'; ciRunId = 40; verificationRunId = 41 } | ConvertTo-Json
        Update-FixtureProvenance -Plan $plan
        $effects.Clear(); $draft = $false
        $rangeError = $null
        try {
            Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history
        } catch { $rangeError = $_.Exception.Message }
        Assert ($rangeError -match 'first-parent' -and $effects.Count -eq 0 -and !$draft) 'An authenticated app source on a side parent, rather than the candidate mainline, must fail explicitly before tag or draft creation instead of manufacturing a misleading range.'
        foreach ($invalidBaseline in @(('f' * 40), (git -C $history rev-parse HEAD).Trim(), $candidate)) {
            $previousSource = $invalidBaseline; $plan.lastSourceSha = $invalidBaseline
            $assetResponse = @{ schemaVersion = 1; sourceSha = $invalidBaseline; tag = 'v0.0.9'; version = '0.0.9'; ciRunId = 40; verificationRunId = 41 } | ConvertTo-Json
            Update-FixtureProvenance -Plan $plan
            $effects.Clear(); $draft = $false
            $rangeError = $null
            try {
                Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history
            } catch { $rangeError = $_.Exception.Message }
            Assert ($rangeError -match 'changes since the last successful release|No mainline changes' -and
                $effects.Count -eq 0 -and !$draft) 'Missing, non-ancestor and empty comparison histories must fail explicitly before tag or draft creation, never substitute HEAD or another baseline.'
        }
        $candidate = $savedCandidate; $previousSource = $savedPreviousSource
        $plan.sourceSha = $candidate; $main = $candidate; $run.head_sha = $candidate
        $releases = @(); $plan.lastSourceSha = ''; $plan.lastTag = ''
        Update-FixtureProvenance -Plan $plan
        $effects.Clear(); $draft = $false
        $shallowHistory = "$fixture-shallow"
        git clone --quiet --no-local --depth 2 $history $shallowHistory
        if ($LASTEXITCODE -ne 0) { throw 'Shallow publication history fixture failed.' }
        Assert ((git -C $shallowHistory rev-parse --is-shallow-repository) -ceq 'true') 'The missing-history regression must use a genuinely shallow clone.'
        $rangeError = $null
        try {
            Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $shallowHistory
        } catch { $rangeError = $_.Exception.Message }
        Assert ($rangeError -match 'shallow' -and $effects.Count -eq 0 -and !$draft) 'A first publication with incomplete shallow history must fail explicitly before tag or draft creation, never present its truncated history as complete.'
        Remove-Item $shallowHistory -Recurse -Force
        $unavailableHistory = "$fixture-unavailable"
        git init --quiet $unavailableHistory
        $subjectError = $null
        try {
            Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $unavailableHistory
        } catch { $subjectError = $_.Exception.Message }
        Assert ($subjectError -match 'source commit subject' -and $effects.Count -eq 0 -and !$draft) 'Unavailable exact-source history must fail explicitly on subject resolution before creating a tag or draft, without falling back to another checkout.'
        Remove-Item $unavailableHistory -Recurse -Force
        $savedCandidate = $candidate
        foreach ($unusableMessage in @('', "Subject with an escape `e[31msequence")) {
            git -C $history -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet --allow-empty --allow-empty-message --cleanup=verbatim -m $unusableMessage
            if ($LASTEXITCODE -ne 0) { throw 'Unusable-subject fixture commit failed.' }
            $candidate = (git -C $history rev-parse HEAD).Trim()
            $plan.sourceSha = $candidate; $main = $candidate; $run.head_sha = $candidate
            Update-FixtureProvenance -Plan $plan
            $subjectError = $null
            try {
                Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history
            } catch { $subjectError = $_.Exception.Message }
            Assert ($subjectError -match 'source commit subject' -and $effects.Count -eq 0 -and !$draft) 'An empty or control-character Git subject must fail explicitly before tag or draft creation, never publish an unusable label or placeholder.'
        }
        git -C $history -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet --allow-empty -m 'Valid candidate after unusable unpublished subjects'
        if ($LASTEXITCODE -ne 0) { throw 'Valid-subject follow-up fixture commit failed.' }
        $candidate = (git -C $history rev-parse HEAD).Trim()
        $plan.sourceSha = $candidate; $main = $candidate; $run.head_sha = $candidate
        Update-FixtureProvenance -Plan $plan
        $subjectError = $null
        try {
            Publish-Release -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -Key $key -KeyId test-active -Root $history
        } catch { $subjectError = $_.Exception.Message }
        Assert ($subjectError -match 'Unusable source commit subject' -and !$subjectError.Contains($candidate) -and
            $effects.Count -eq 0 -and !$draft) 'An unusable earlier mainline subject must block publication even when the exact candidate subject is valid; silently dropping a pending change is not a complete description.'
        $candidate = $savedCandidate; $plan.sourceSha = $candidate; $main = $candidate; $run.head_sha = $candidate
        Update-FixtureProvenance -Plan $plan
        Remove-Item "$fixture\org-explorer-update.win-x64.json", "$fixture\release-provenance.json"
        $null = New-SignedReleaseManifest -ReleaseDirectory $fixture -Version 0.1.0 -Key $key -KeyId test-active -Now ([DateTimeOffset]::UtcNow.AddDays(-8))
        New-ReleaseProvenance -Plan $plan -ReleaseDirectory $fixture -CiRunId 42 -VerificationRunId 43
        $assetResponse = Get-Content "$fixture\release-provenance.json" -Raw
        $originalRelease = @{ id = 100; draft = $false; prerelease = $false; tag_name = 'v0.1.0'; published_at = [DateTimeOffset]::UtcNow.AddDays(-8).ToString('O')
            assets = @(Get-ReleaseInventory $fixture | ForEach-Object {
                @{ id = if ($_.fileName -ceq 'release-provenance.json') { 7 } else { 8 }; name = $_.fileName; size = $_.size; digest = 'sha256:' + $_.sha256 }
            }) }
        $releases = @($originalRelease); $latestRelease = $originalRelease
        $renewalState = Get-ReleasePublicationState
        $renewalAuthorization = @{ expiresAt = [DateTimeOffset]::UtcNow.AddDays(6).ToString('O') }
        $renewalPlan = Get-MetadataRenewalPlan -State $renewalState -Authorization $renewalAuthorization -ControllerRevision $candidate -RunId 123 -Attempt 1
        $renewalDirectory = "$fixture-renewal"
        $uploadDirectory = $renewalDirectory
        New-MetadataRenewalArtifacts -OriginalDirectory $fixture -Destination $renewalDirectory -Plan $renewalPlan -Key $key -KeyId test-active -VerificationRunId 123 -ControllerCiRunId 42
        Assert-RenewalArtifacts -OriginalDirectory $fixture -RenewalDirectory $renewalDirectory -Plan $renewalPlan -Key $key -KeyId test-active
        $renewedEnvelope = Get-Content "$renewalDirectory\org-explorer-update.win-x64.json" -Raw | ConvertFrom-Json
        $renewedPayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($renewedEnvelope.payload)) | ConvertFrom-Json
        Assert ($renewedPayload.version -ceq '0.1.0' -and $renewedPayload.releaseTag -ceq 'v0.1.0' -and
            $renewedPayload.assets[0].url -ceq 'https://github.com/geevensingh/org-explorer/releases/download/v0.1.0/geevensingh.OrgExplorer-0.1.0-full.nupkg' -and
            $renewedPayload.assets[0].size -eq 3 -and $renewedPayload.assets[0].sha256 -ceq 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') 'Renewal must pin the original full package URL/filename/version/length/SHA256, not its metadata release tag.'
        [IO.File]::WriteAllBytes("$renewalDirectory\geevensingh.OrgExplorer-win-Setup.exe", [Text.Encoding]::UTF8.GetBytes('changed bootstrap'))
        Rejects { Assert-RenewalArtifacts -OriginalDirectory $fixture -RenewalDirectory $renewalDirectory -Plan $renewalPlan -Key $key -KeyId test-active } 'changed copied latest-download bootstrap bytes'
        [IO.File]::WriteAllBytes("$renewalDirectory\geevensingh.OrgExplorer-win-Setup.exe", [Text.Encoding]::UTF8.GetBytes('abc'))
        $failUpload = $true; $effects.Clear(); $draft = $false
        Rejects { Publish-MetadataRenewal -Plan $renewalPlan -OriginalDirectory $fixture -RenewalDirectory $renewalDirectory -ControllerCiRunId 42 -Key $key -KeyId test-active } 'failed renewal upload'
        Assert (!$effects.Contains('publish')) 'Failed renewal must leave latest unchanged.'
        $failUpload = $false; $effects.Clear(); $draft = $false
        $latestRelease = @{ id = 101 }
        Rejects { Publish-MetadataRenewal -Plan $renewalPlan -OriginalDirectory $fixture -RenewalDirectory $renewalDirectory -ControllerCiRunId 42 -Key $key -KeyId test-active } 'a concurrent product/metadata latest promotion'
        Assert ($effects.Count -eq 0) 'Superseded latest must reject before any new tag or draft.'
        $latestRelease = $originalRelease
        $moveMainOnUpload = $true; $effects.Clear(); $draft = $false
        Rejects { Publish-MetadataRenewal -Plan $renewalPlan -OriginalDirectory $fixture -RenewalDirectory $renewalDirectory -ControllerCiRunId 42 -Key $key -KeyId test-active } 'renewal main advancing during upload'
        Assert (!$effects.Contains('publish')) 'Stale renewal cannot replace latest.'
        $moveMainOnUpload = $false; $main = $candidate; $effects.Clear(); $draft = $false
        $renewalPlan.controllerSha = 'd' * 40; $main = 'd' * 40
        $renewalProof = Get-Content "$renewalDirectory\release-provenance.json" -Raw | ConvertFrom-Json
        $renewalProof.controllerSha = 'd' * 40; $renewalProof.controllerCiRunId = 44
        $renewalProof | ConvertTo-Json -Depth 10 | Set-Content "$renewalDirectory\release-provenance.json"
        $originalCiJobs = $historicalJobs
        $workflowBlob = 'a8070b98ddb256dbf6dfd8880c0ededfe9cdb369'
        Publish-MetadataRenewal -Plan $renewalPlan -OriginalDirectory $fixture -RenewalDirectory $renewalDirectory -ControllerCiRunId 44 -Key $key -KeyId test-active
        $originalCiJobs = $null
        $workflowBlob = '6bca4e7e585fef8e9a8c2b6cbdb6a0562bcb5cb9'
        Assert (($effects -join ',') -ceq 'tag,draft,upload,publish' -and $currentTag -ceq 'metadata-v0.1.0-123-1') 'Renewal must publish only an immutable metadata tag while preserving the original app version.'
        Assert ($createdBody -ceq "Metadata renewal only. Original app/package remains v0.1.0 at $candidate; no new app version. Bootstrap assets are byte-identical. Fresh exact-product scale verification and provenance are recorded in release-provenance.json.") 'Metadata renewal descriptions must remain unchanged, without a new source-subject paragraph.'
        $renewalProvenanceResponse = Get-Content "$renewalDirectory\release-provenance.json" -Raw
        $renewedRelease = @{ id = 99; tag_name = $currentTag; draft = $false; prerelease = $false; published_at = [DateTimeOffset]::UtcNow.ToString('O')
            assets = @(Get-ReleaseInventory $renewalDirectory | ForEach-Object {
                @{ id = if ($_.fileName -ceq 'release-provenance.json') { 9 } else { 10 }; name = $_.fileName; size = $_.size; digest = 'sha256:' + $_.sha256 }
            }) }
        $releases = @($originalRelease, $renewedRelease); $latestRelease = $renewedRelease
        Assert ((Get-LastSuccessfulRelease).sourceSha -ceq $candidate -and
            (Get-NextReleaseVersion -InitialVersion '0.1.0' -LastVersion (Get-LastSuccessfulRelease).version) -ceq '0.1.1') 'A genuinely generated metadata publication must preserve the original product baseline and next app version.'
        $releases = @($originalRelease); $latestRelease = $originalRelease
        Remove-Item $renewalDirectory -Recurse -Force
        $sourceRoot = "$fixture-source"
        New-Item "$sourceRoot\src\OrgExplorer.Desktop", "$sourceRoot\tests\OrgExplorer.Desktop.Smoke", "$sourceRoot\tests\OrgExplorer.Core.Tests" -ItemType Directory -Force | Out-Null
        @{ schemaVersion = 1; manifestUrl = 'https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json'
            keys = @(@{ keyId = 'test-active'; algorithm = 'ES256'; publicKey = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) }) } |
            ConvertTo-Json -Depth 5 | Set-Content "$sourceRoot\src\OrgExplorer.Desktop\update-trust.json"
        git -C $sourceRoot init --quiet
        $currentRegressions = Get-Content "$PSScriptRoot\OrgExplorer.Core.Tests\DesktopScenarioRunnerTests.cs" -Raw
        foreach ($source in @(
            @{ name = 'current'; regressions = $currentRegressions; isolated = $true; allowed = $true },
            @{ name = 'historical'; regressions = 'Scale_fixtures_opt_out_of_production_updates_before_any_application_can_launch'; isolated = $true; allowed = $true },
            @{ name = 'missing-regression'; regressions = 'Unrelated_passing_tests'; isolated = $true; allowed = $false },
            @{ name = 'missing-isolation'; regressions = $currentRegressions; isolated = $false; allowed = $false }
        )) {
            $runner = if ($source.isolated) { 'checkOnStartup = false' } else { 'No startup opt-out' }
            $runner | Set-Content "$sourceRoot\tests\OrgExplorer.Desktop.Smoke\SmokeScenarioRunner.cs"
            $source.regressions | Set-Content "$sourceRoot\tests\OrgExplorer.Core.Tests\DesktopScenarioRunnerTests.cs"
            git -C $sourceRoot add -- src\OrgExplorer.Desktop\update-trust.json `
                tests\OrgExplorer.Desktop.Smoke\SmokeScenarioRunner.cs tests\OrgExplorer.Core.Tests\DesktopScenarioRunnerTests.cs
            git -C $sourceRoot -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m "Controlled original source: $($source.name)"
            if ($LASTEXITCODE -ne 0) { throw 'Controlled original source commit failed.' }
            $candidate = (git -C $sourceRoot rev-parse HEAD).Trim()
            $main = $candidate; $run.head_sha = $candidate
            $original = $assetResponse | ConvertFrom-Json
            $original.sourceSha = $candidate
            $original | ConvertTo-Json -Depth 10 | Set-Content "$fixture\release-provenance.json"
            $assetResponse = Get-Content "$fixture\release-provenance.json" -Raw
            $manifestResponse = Get-Content "$fixture\org-explorer-update.win-x64.json" -Raw
            $originalRelease.assets = @(Get-ReleaseInventory $fixture | ForEach-Object {
                @{ id = if ($_.fileName -ceq 'release-provenance.json') { 7 } else { 8 }; name = $_.fileName; size = $_.size; digest = 'sha256:' + $_.sha256 }
            })
            $effects.Clear()
            if ($source.allowed) {
                $context = Get-MetadataRenewalContext -Root $sourceRoot -ControllerRevision $candidate -RunId 123 -Attempt 1
                Assert ($context.plan.publish -and $context.plan.sourceSha -ceq $candidate -and $context.controllerCiRunId -eq 42 -and $effects.Count -eq 0) "Scheduled evaluation must authenticate $($source.name) original metadata/trust at its real Git source without publication effects."
                $missingCurrentCi = $true
                Rejects { Get-MetadataRenewalContext -Root $sourceRoot -ControllerRevision $candidate -RunId 123 -Attempt 1 } 'a controller main without successful CI'
                $missingCurrentCi = $false
            } else {
                Rejects { Get-MetadataRenewalContext -Root $sourceRoot -ControllerRevision $candidate -RunId 123 -Attempt 1 } $source.name
                Assert ($effects.Count -eq 0) 'Missing isolation evidence must fail before publication effects.'
            }
        }
        $releases = @()
        Assert (!(Get-MetadataRenewalContext -Root $sourceRoot -ControllerRevision $candidate -RunId 123 -Attempt 1).plan.publish) 'First scheduled evaluation without a public product must not require or fabricate an original source/release.'
        Remove-Item $sourceRoot -Recurse -Force
    } finally { $key.Dispose() }
    Write-Host "PASS: $checks trusted publication checks."
} finally {
    Remove-Item Function:\gh
    Remove-Item $fixture -Recurse -Force
    Remove-Item $history -Recurse -Force
    if (Test-Path "$fixture-renewal") { Remove-Item "$fixture-renewal" -Recurse -Force }
    if (Test-Path "$fixture-source") { Remove-Item "$fixture-source" -Recurse -Force }
    if (Test-Path "$fixture-unavailable") { Remove-Item "$fixture-unavailable" -Recurse -Force }
    if (Test-Path "$fixture-shallow") { Remove-Item "$fixture-shallow" -Recurse -Force }
}
