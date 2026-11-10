function Invoke-ReleaseApi([string]$Endpoint, [string[]]$Arguments = @()) {
    $text = & gh api $Endpoint @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'GitHub release API failed. Publication remains incomplete; do not overwrite existing tags/assets to recover.' }
    if ($text) { return (($text -join "`n") | ConvertFrom-Json -Depth 30) }
}

function Assert-TrustedCi([string]$Candidate, [long]$CiRunId) {
    if ($Candidate -cnotmatch '^[0-9a-f]{40}$' -or $CiRunId -le 0) { throw 'An exact candidate and successful trusted CI run ID are required.' }
    $run = Invoke-ReleaseApi "repos/$ReleaseRepository/actions/runs/$CiRunId"
    if ($run.id -ne $CiRunId -or $run.event -cne 'push' -or $run.head_branch -cne 'main' -or
        $run.head_sha -cne $Candidate -or $run.status -cne 'completed' -or $run.conclusion -cne 'success' -or
        $run.path -cne '.github/workflows/ci.yml' -or $run.head_repository.full_name -cne $ReleaseRepository) {
        throw 'Publication requires successful ci.yml push CI from this repository main at the exact candidate. PR/manual/fork runs cannot authorize production.'
    }
    $jobs = (Invoke-ReleaseApi "repos/$ReleaseRepository/actions/runs/$CiRunId/jobs").jobs
    foreach ($name in @('Core and view models', 'Real filesystem persistence', 'Actual Windows desktop smoke', 'CI')) {
        $matches = @($jobs | Where-Object { $_.name -ceq $name })
        if ($matches.Count -ne 1 -or $matches[0].status -cne 'completed' -or $matches[0].conclusion -cne 'success') {
            throw "Required CI lane did not succeed: $name. Missing/skipped/cancelled/timed-out work is not approval."
        }
    }
}

function Assert-TrustedMainCi([string]$Candidate, [long]$CiRunId) {
    Assert-TrustedCi -Candidate $Candidate -CiRunId $CiRunId
    $head = Invoke-ReleaseApi "repos/$ReleaseRepository/git/ref/heads/main"
    if ($head.object.sha -cne $Candidate) { throw 'Main moved past this candidate. This older build must not publish or replace latest; wait for successful CI of the new main.' }
}

function Get-ReleasePublicationState {
    $pages = Invoke-ReleaseApi "repos/$ReleaseRepository/releases" @('--paginate', '--slurp')
    $published = @($pages | ForEach-Object { $_ } | Where-Object { !$_.draft -and !$_.prerelease })
    if ($published.Count -eq 0) { return $null }
    $ordered = @($published | Sort-Object @{ Expression = { [DateTimeOffset]$_.published_at }; Descending = $true },
        @{ Expression = { $_.id }; Descending = $true })
    $latest = $ordered[0]
    $products = @($ordered | Where-Object { $_.tag_name -cmatch '^v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z' })
    if ($products.Count -eq 0) { throw 'Public metadata has no recognized original app release. Owner must reconcile provenance; no guessed baseline is allowed.' }
    $productRelease = $products[0]
    $product = Get-PublishedReleaseProvenance $productRelease
    if ($product.tag -cne $productRelease.tag_name -or
        ($product.PSObject.Properties.Name -contains 'kind' -and $product.kind -cne 'product-release')) { throw 'App release has invalid product provenance.' }
    $publication = $product
    if ($latest.tag_name -cne $productRelease.tag_name) {
        $publication = Get-PublishedReleaseProvenance $latest
        $original = @($productRelease.assets | Where-Object name -CEQ 'release-provenance.json')[0]
        if ($latest.tag_name -cnotmatch ('^metadata-' + [regex]::Escape($product.tag) + '-' + $publication.verificationRunId + '-[1-9][0-9]*\z') -or
            $publication.kind -cne 'metadata-renewal' -or $publication.publicationTag -cne $latest.tag_name -or
            $publication.sourceSha -cne $product.sourceSha -or $publication.version -cne $product.version -or
            $publication.tag -cne $product.tag -or $publication.ciRunId -ne $product.ciRunId -or
            $publication.originalReleaseId -ne $productRelease.id -or
            $publication.originalProvenanceSha256 -cnotmatch '^[0-9a-f]{64}\z' -or
            "sha256:$($publication.originalProvenanceSha256)" -cne $original.digest -or
            $publication.controllerSha -cnotmatch '^[0-9a-f]{40}\z' -or $publication.controllerCiRunId -le 0) { throw 'Metadata publication does not reference the exact original app/provenance.' }
    }
    return @{ product = $product; productRelease = $productRelease; publication = $publication; publicationRelease = $latest }
}

function Get-PublishedReleaseProvenance($Release) {
    $assets = @($Release.assets | Where-Object { $_.name -ceq 'release-provenance.json' })
    if ($assets.Count -ne 1) { throw 'Last published release lacks unique release-provenance.json. Owner must reconcile legacy publication before automation proceeds.' }
    $provenance = Invoke-ReleaseApi "repos/$ReleaseRepository/releases/assets/$($assets[0].id)" @('-H', 'Accept: application/octet-stream')
    if ($provenance.schemaVersion -ne 1 -or $provenance.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or
        $provenance.tag -cne "v$($provenance.version)" -or
        $provenance.ciRunId -le 0 -or $provenance.verificationRunId -le 0) { throw 'Last publication provenance is invalid.' }
    Assert-ReleaseVersion $provenance.version
    $tag = Invoke-ReleaseApi "repos/$ReleaseRepository/git/ref/tags/$($Release.tag_name)"
    if ($tag.object.type -cne 'commit' -or $tag.object.sha -cne $provenance.sourceSha) { throw 'Published tag and recorded exact source disagree.' }
    return $provenance
}

function Get-LastSuccessfulRelease {
    $state = Get-ReleasePublicationState
    if ($null -ne $state) { return $state.product }
}

function Get-ReservedReleaseVersions {
    $pages = Invoke-ReleaseApi "repos/$ReleaseRepository/git/matching-refs/tags/v" @('--paginate', '--slurp')
    foreach ($reference in @($pages | ForEach-Object { $_ })) {
        if ($reference.ref -cmatch '^refs/tags/v((0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8}))$') { $Matches[1] }
    }
}

function Invoke-ReleaseWriteApi([string]$Endpoint, [string]$Method, $Body) {
    $directory = Join-Path $PSScriptRoot '..\.artifacts\release-requests'
    New-Item $directory -ItemType Directory -Force | Out-Null
    $path = Join-Path $directory ([guid]::NewGuid().ToString('N') + '.json')
    try {
        $Body | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        return Invoke-ReleaseApi $Endpoint @('--method', $Method, '--input', $path)
    } finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

function Assert-UnchangedReleaseBaseline($Plan) {
    $last = Get-LastSuccessfulRelease
    $source = if ($null -eq $last) { '' } else { $last.sourceSha }
    $tag = if ($null -eq $last) { '' } else { $last.tag }
    if ($source -cne $Plan.lastSourceSha -or $tag -cne $Plan.lastTag) { throw 'Another publication changed the comparison baseline. Replan from successful main CI; never replace it with an older build.' }
}

function Publish-Release($Plan, [string]$ReleaseDirectory, [long]$CiRunId, [Security.Cryptography.ECDsa]$Key, [string]$KeyId) {
    if (!$Plan.publish -or $Plan.tag -cne "v$($Plan.version)") { throw 'This plan does not authorize an app publication.' }
    Assert-ReleaseInventory -ReleaseDirectory $ReleaseDirectory -Version $Plan.version -Key $Key -KeyId $KeyId
    $provenance = Get-Content (Join-Path $ReleaseDirectory 'release-provenance.json') -Raw | ConvertFrom-Json
    if ($provenance.sourceSha -cne $Plan.sourceSha -or $provenance.ciRunId -ne $CiRunId) { throw 'Packaged source/CI provenance does not match the authorized candidate.' }
    Publish-VerifiedReleaseDraft -PublicationTag $Plan.tag -SourceSha $Plan.sourceSha -Name "Org explorer $($Plan.version)" `
        -Body "Exact source: $($Plan.sourceSha). Trusted CI: $CiRunId. Fresh complete scale verification: $($provenance.verificationRunId). See release-provenance.json for immutable asset hashes. Project-key authenticated updates; not Windows publisher signing." `
        -ReleaseDirectory $ReleaseDirectory -BeforePromotion {
            Assert-ReleaseInventory -ReleaseDirectory $ReleaseDirectory -Version $Plan.version -Key $Key -KeyId $KeyId
            Assert-UnchangedReleaseBaseline $Plan
            Assert-TrustedMainCi -Candidate $Plan.sourceSha -CiRunId $CiRunId
        }
}

function Publish-VerifiedReleaseDraft(
    [string]$PublicationTag, [string]$SourceSha, [string]$Name, [string]$Body, [string]$ReleaseDirectory, [scriptblock]$BeforePromotion
) {
    & $BeforePromotion
    # Creation is deliberately non-idempotent: a collision requires owner recovery, never a force update.
    $null = Invoke-ReleaseWriteApi "repos/$ReleaseRepository/git/refs" POST @{ ref = "refs/tags/$PublicationTag"; sha = $SourceSha }
    $release = Invoke-ReleaseWriteApi "repos/$ReleaseRepository/releases" POST @{
        tag_name = $PublicationTag; target_commitish = $SourceSha; name = $Name; body = $Body
        draft = $true; prerelease = $false; make_latest = 'false'
    }
    $inventory = @(Get-ReleaseInventory $ReleaseDirectory)
    $paths = @($inventory | ForEach-Object { Join-Path $ReleaseDirectory $_.fileName })
    & gh release upload $PublicationTag @paths --repo $ReleaseRepository
    if ($LASTEXITCODE -ne 0) { throw 'Release asset upload failed. The draft stays unpublished. Owner must reconcile the draft/tag; no assets are overwritten automatically.' }
    $uploaded = Invoke-ReleaseApi "repos/$ReleaseRepository/releases/$($release.id)"
    if (!$uploaded.draft -or $uploaded.tag_name -cne $PublicationTag -or @($uploaded.assets).Count -ne $inventory.Count) { throw 'Draft upload inventory is incomplete or changed.' }
    foreach ($item in $inventory) {
        $asset = @($uploaded.assets | Where-Object { $_.name -ceq $item.fileName })
        if ($asset.Count -ne 1 -or $asset[0].size -ne $item.size -or $asset[0].digest -cne "sha256:$($item.sha256)") {
            throw 'GitHub uploaded asset length/SHA256 differs or is unavailable. Leave the draft unpublished; do not waive digest verification.'
        }
    }
    & $BeforePromotion
    $tag = Invoke-ReleaseApi "repos/$ReleaseRepository/git/ref/tags/$PublicationTag"
    if ($tag.object.type -cne 'commit' -or $tag.object.sha -cne $SourceSha) { throw 'Candidate tag changed before publication.' }
    $published = Invoke-ReleaseWriteApi "repos/$ReleaseRepository/releases/$($release.id)" PATCH @{ draft = $false; make_latest = 'true' }
    if ($published.draft -or $published.prerelease -or $published.tag_name -cne $PublicationTag) { throw 'GitHub did not confirm the requested publication.' }
    Write-Host "Published $PublicationTag from exact source $SourceSha."
}
