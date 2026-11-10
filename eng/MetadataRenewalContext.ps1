function Read-PublishedAuthorization($Release, [string]$TrustFile, $Product) {
    $assets = @($Release.assets | Where-Object name -CEQ 'org-explorer-update.win-x64.json')
    if ($assets.Count -ne 1) { throw 'Published release has no unique signed manifest.' }
    $text = & gh api "repos/$ReleaseRepository/releases/assets/$($assets[0].id)" -H 'Accept: application/octet-stream'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the original/current signed metadata; no renewal is authorized.' }
    return Read-ReleaseAuthorization -Text ($text -join "`n") -TrustFile $TrustFile -Product $Product -AllowExpired
}

function Get-MetadataRenewalContext([string]$Root, [string]$ControllerRevision, [long]$RunId, [int]$Attempt) {
    $state = Get-ReleasePublicationState
    if ($null -eq $state) {
        return @{ state = $null; plan = (Get-MetadataRenewalPlan -State $null -Authorization $null -ControllerRevision $ControllerRevision -RunId $RunId -Attempt $Attempt) }
    }
    $null = Invoke-ReleaseGit $Root @('merge-base', '--is-ancestor', $state.product.sourceSha, $ControllerRevision)
    $runner = (Invoke-ReleaseGit $Root @('show', "$($state.product.sourceSha):tests/OrgExplorer.Desktop.Smoke/SmokeScenarioRunner.cs", '--')) -join "`n"
    $regressions = (Invoke-ReleaseGit $Root @('show', "$($state.product.sourceSha):tests/OrgExplorer.Core.Tests/DesktopScenarioRunnerTests.cs", '--')) -join "`n"
    $hasIsolationRegression =
        $regressions.Contains('Scale_fixtures_opt_out_of_production_updates_before_any_application_can_launch') -or
        $regressions.Contains('Non_update_fixtures_opt_out_of_production_updates_before_any_application_can_launch')
    if (!$runner.Contains('checkOnStartup = false') -or !$hasIsolationRegression) {
        throw 'Original candidate predates scale production-update isolation. Ship a genuine bridge product release before automatic renewal; do not modify an historical candidate or run it against production updates.'
    }
    Assert-PublishedReleaseInventory $state.productRelease $state.product
    Assert-PublishedReleaseInventory $state.publicationRelease $state.publication
    if ((Invoke-ReleaseApi "repos/$ReleaseRepository/releases/latest").id -ne $state.publicationRelease.id) { throw 'GitHub latest differs from the recognized publication history; owner reconciliation is required.' }
    $directory = Join-Path $Root '.artifacts\metadata-renewal'
    New-Item $directory -ItemType Directory -Force | Out-Null
    $trustFile = Join-Path $directory 'original-public-trust.json'
    (Invoke-ReleaseGit $Root @('show', "$($state.product.sourceSha):src/OrgExplorer.Desktop/update-trust.json", '--')) -join "`n" |
        Set-Content $trustFile -Encoding utf8NoBOM
    $null = Read-PublishedAuthorization -Release $state.productRelease -TrustFile $trustFile -Product $state.product
    $authorization = Read-PublishedAuthorization -Release $state.publicationRelease -TrustFile $trustFile -Product $state.product
    Assert-TrustedCi -Candidate $state.product.sourceSha -CiRunId $state.product.ciRunId
    $runs = Invoke-ReleaseApi "repos/$ReleaseRepository/actions/workflows/ci.yml/runs?branch=main&event=push&head_sha=$ControllerRevision&status=success&per_page=100"
    $matches = @($runs.workflow_runs | Where-Object { $_.head_sha -ceq $ControllerRevision -and $_.conclusion -ceq 'success' })
    if ($matches.Count -eq 0) { throw 'Renewal controller main has no successful trusted push CI. Wait for successful main CI; do not bypass it.' }
    $controllerCiRunId = [long]$matches[0].id
    Assert-TrustedMainCi -Candidate $ControllerRevision -CiRunId $controllerCiRunId
    return @{
        state = $state; trustFile = $trustFile; controllerCiRunId = $controllerCiRunId
        plan = (Get-MetadataRenewalPlan -State $state -Authorization $authorization -ControllerRevision $ControllerRevision -RunId $RunId -Attempt $Attempt)
    }
}

function Receive-OriginalRelease($State, [string]$Directory, [string]$TrustFile) {
    if (Test-Path $Directory) { throw 'Original release inspection requires a fresh directory, never a prior download/cache.' }
    & gh release download $State.product.tag --repo $ReleaseRepository --dir $Directory
    if ($LASTEXITCODE -ne 0) { throw 'Original immutable release download failed; no replacement feed/package is permitted.' }
    Assert-PublishedReleaseInventory $State.productRelease $State.product
    $actual = @(Get-ReleaseInventory $Directory)
    if ($actual.Count -ne @($State.productRelease.assets).Count) { throw 'Original download is incomplete or contains extra files.' }
    foreach ($asset in $State.productRelease.assets) {
        $item = @($actual | Where-Object { $_.fileName -ceq $asset.name })
        if ($item.Count -ne 1 -or $item[0].size -ne $asset.size -or "sha256:$($item[0].sha256)" -cne $asset.digest) { throw 'Original downloaded asset differs from immutable publication provenance/digest.' }
    }
    $envelope = Get-Content (Join-Path $Directory 'org-explorer-update.win-x64.json') -Raw
    $null = Read-ReleaseAuthorization -Text $envelope -TrustFile $TrustFile -Product $State.product -AllowExpired
    Assert-OriginalPackageTrust -PackagePath (Join-Path $Directory "geevensingh.OrgExplorer-$($State.product.version)-full.nupkg") -TrustFile $TrustFile
    & "$PSScriptRoot\..\tests\Installer.Artifacts.Tests.ps1" -ReleaseDirectory $Directory -Version $State.product.version
}
