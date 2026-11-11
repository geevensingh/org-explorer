$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\Release.ps1"
$checks = 0
function Assert([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Rejects([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert $rejected $Message
}

$root = Join-Path $PSScriptRoot '..'
Assert (Test-ProductImpact @('src/OrgExplorer.Workflow/ApplicationWorkflow.cs') @()) 'Shipped source must publish.'
Assert (!(Test-ProductImpact @('README.md', 'tests/new-test.cs', 'eng/Run-UiScale.ps1') @())) 'Repository docs/tests/test tooling alone must not publish.'
Assert (Test-ProductImpact @('docs/INSTALLATION.md') @('docs/INSTALLATION.md')) 'Shipped Markdown is a product change.'
Assert (Test-ProductImpact @('docs/new-help.md') @('docs/new-help.md')) 'Linked resources must not depend on an extension allowlist.'
Assert (Test-ProductImpact @('README.md', 'src/assets/new.png') @()) 'Mixed changes must publish.'
foreach ($path in @('src/runtime.json', 'schemas/person.json', 'assets/logo.png', 'NuGet.Config',
    'Directory.Build.props', '.config/dotnet-tools.json', 'eng/Package-Windows.ps1', '.github/workflows/release.yml')) {
    Assert (Test-ProductImpact @($path) @()) "Product dependency/build/packaging effect missing: $path"
}
Assert ((Get-NextReleaseVersion -InitialVersion '0.1.0') -ceq '0.1.0') 'First release uses the deliberate app baseline.'
Assert ((Get-NextReleaseVersion -InitialVersion '0.1.0' -LastVersion '0.1.9') -ceq '0.1.10') 'Versions increment the last successful publication, not source defaults.'
foreach ($version in @('01.2.3', '1.2.3-beta', '1.2.3.4', ' 1.2.3', "1.2.3`n", '1000000000.1.0')) {
    Rejects { Get-NextReleaseVersion -InitialVersion $version } "Noncanonical version accepted: $version"
}
$fixture = Join-Path $root ('.test-workspaces\release-' + [guid]::NewGuid().ToString('N'))
New-Item "$fixture\src\OrgExplorer.Desktop" -ItemType Directory -Force | Out-Null
New-Item "$fixture\docs" -ItemType Directory -Force | Out-Null
function Commit-Fixture([string]$Message) {
    git -C $fixture add --all
    git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m $Message
    if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed.' }
    return (git -C $fixture rev-parse HEAD).Trim()
}
try {
    git -C $fixture init --quiet
    '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup><ItemGroup><Content Include="..\..\docs\help.md" /></ItemGroup></Project>' |
        Set-Content "$fixture\src\OrgExplorer.Desktop\OrgExplorer.Desktop.csproj"
    'shipped' | Set-Content "$fixture\docs\help.md"
    $first = Commit-Fixture 'First product'
    $plan = Get-ReleasePlan -Root $fixture -Candidate $first
    Assert ($plan.publish -and $plan.version -ceq '0.1.0' -and $plan.tag -ceq 'v0.1.0' -and !$plan.lastSourceSha) 'First publication must be explicit.'
    $summaryPath = Join-Path $fixture 'summary.md'
    Write-ReleasePlanSummary -Plan $plan -Path $summaryPath
    $summary = Get-Content -LiteralPath $summaryPath -Raw
    Assert ($summary.Contains('Release planned - publication pending') -and
        $summary.Contains('v0.1.0') -and $summary.Contains('None (first product release)') -and
        $summary.Contains('not confirmation of a published release')) 'A first-release summary must distinguish planning from successful publication.'
    Remove-Item -LiteralPath $summaryPath
    $retryPlan = Get-ReleasePlan -Root $fixture -Candidate $first -ReservedVersions @('0.1.0', '0.1.1')
    Assert ($retryPlan.publish -and $retryPlan.version -ceq '0.1.2' -and !$retryPlan.lastSourceSha) 'Failed reserved tags must be skipped without becoming the successful comparison baseline or being overwritten.'
    $last = @{ sourceSha = $first; version = '0.1.0'; tag = 'v0.1.0' }
    'repository only' | Set-Content "$fixture\README.md"
    $docs = Commit-Fixture 'Docs alone'
    $docsPlan = Get-ReleasePlan -Root $fixture -Candidate $docs -LastRelease $last
    Assert (!$docsPlan.publish) 'Released product plus docs alone must not publish.'
    'Existing summary' | Set-Content -LiteralPath $summaryPath
    Write-ReleasePlanSummary -Plan $docsPlan -Path $summaryPath
    $summary = Get-Content -LiteralPath $summaryPath -Raw
    Assert ($summary.StartsWith('Existing summary') -and
        $summary.Contains('No release published - expected no-op') -and
        $summary.Contains('This is a successful no-op, not a publication failure.') -and
        $summary.Contains('No unpublished product effect.') -and
        $summary.Contains('**v0.1.0 remains the current app release.**') -and
        $summary.Contains('Scale verification and publication were intentionally skipped.') -and
        $summary.Contains("/releases/tag/v0.1.0") -and $summary.Contains($first) -and
        $summary.Contains($docs) -and $summary.Contains('<code>README.md</code>') -and
        !$summary.Contains('0.1.1')) 'A no-op summary must explain intentional skips and the actual baseline/candidate, without implying a new version was published.'
    Remove-Item -LiteralPath $summaryPath
    $docsPlan.changedPaths = @('docs/<img src=x onerror=alert(1)>`[help].md')
    Write-ReleasePlanSummary -Plan $docsPlan -Path $summaryPath
    $summary = Get-Content -LiteralPath $summaryPath -Raw
    Assert ($summary.Contains('<code>docs/&lt;img src=x onerror=alert(1)&gt;`[help].md</code>') -and
        !$summary.Contains('<img')) 'Changed paths must render as text, not injected Markdown or HTML.'
    Remove-Item -LiteralPath $summaryPath
    'pending product' | Set-Content "$fixture\src\product.txt"
    $null = Commit-Fixture 'Product whose publication failed'
    'more docs' | Set-Content "$fixture\README.md"
    $candidate = Commit-Fixture 'Docs after failed product publication'
    $plan = Get-ReleasePlan -Root $fixture -Candidate $candidate -LastRelease $last
    Assert ($plan.publish -and $plan.version -ceq '0.1.1' -and $plan.sourceSha -ceq $candidate -and $plan.lastSourceSha -ceq $first) 'Pending product changes must survive a later docs-only commit.'
    Write-ReleasePlanSummary -Plan $plan -Path $summaryPath
    $summary = Get-Content -LiteralPath $summaryPath -Raw
    Assert ($summary.Contains('Release planned - publication pending') -and
        $summary.Contains('**v0.1.1**') -and $summary.Contains('not confirmation of a published release') -and
        $summary.Contains('<code>src/product.txt</code>') -and
        !$summary.Contains('expected no-op') -and !$summary.Contains('intentionally skipped')) 'Pending product effects must not be presented as a no-op or a successful publication.'
    Remove-Item -LiteralPath $summaryPath
    $last = @{ sourceSha = $candidate; version = '0.1.1'; tag = 'v0.1.1' }
    'changed shipped Markdown' | Set-Content "$fixture\docs\help.md"
    $candidate = Commit-Fixture 'In-app help'
    Assert ((Get-ReleasePlan -Root $fixture -Candidate $candidate -LastRelease $last).publish) 'MSBuild-linked Markdown must publish.'
    $last = @{ sourceSha = $candidate; version = '0.1.2'; tag = 'v0.1.2' }
    Remove-Item "$fixture\docs\help.md"
    '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>' |
        Set-Content "$fixture\src\OrgExplorer.Desktop\OrgExplorer.Desktop.csproj"
    $candidate = Commit-Fixture 'Remove shipped resource'
    Assert ((Get-ReleasePlan -Root $fixture -Candidate $candidate -LastRelease $last).publish) 'Removed shipped inputs still count.'
    '<Project><PropertyGroup><Version>0.1.0</Version><HelpRoot>..\..\docs</HelpRoot></PropertyGroup><ItemGroup><Content Include="$(HelpRoot)\property-help.md" /><None Update="..\..\docs\output-help.md" CopyToOutputDirectory="PreserveNewest" /></ItemGroup></Project>' |
        Set-Content "$fixture\src\OrgExplorer.Desktop\OrgExplorer.Desktop.csproj"
    'initial help' | Set-Content "$fixture\docs\property-help.md"
    'initial output help' | Set-Content "$fixture\docs\output-help.md"
    $candidate = Commit-Fixture 'Property-based and output-linked help'
    $last = @{ sourceSha = $candidate; version = '0.1.3'; tag = 'v0.1.3' }
    'changed property help' | Set-Content "$fixture\docs\property-help.md"
    $candidate = Commit-Fixture 'Only property-based shipped Markdown changes'
    Assert ((Get-ReleasePlan -Root $fixture -Candidate $candidate -LastRelease $last).publish) 'MSBuild property-based shipped resources must not be silently ignored.'
    $last = @{ sourceSha = $candidate; version = '0.1.4'; tag = 'v0.1.4' }
    'changed output help' | Set-Content "$fixture\docs\output-help.md"
    $candidate = Commit-Fixture 'Only output-linked shipped Markdown changes'
    Assert ((Get-ReleasePlan -Root $fixture -Candidate $candidate -LastRelease $last).publish) 'Output-copy items inherited by publish must count as shipped resources.'
    $key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $wrongKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $private = [Convert]::ToBase64String($key.ExportPkcs8PrivateKey())
        $public = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
        $trustFile = Join-Path $fixture 'trust.json'
        @{ schemaVersion = 1; manifestUrl = 'https://github.com/geevensingh/org-explorer/releases/latest/download/org-explorer-update.win-x64.json'
            keys = @(@{ keyId = 'test-active'; algorithm = 'ES256'; publicKey = $public }) } |
            ConvertTo-Json -Depth 5 | Set-Content $trustFile
        $signer = Open-ReleaseSigningKey -TrustFile $trustFile -KeyId test-active -PrivateKeyBase64 $private
        try {
            [IO.File]::WriteAllBytes((Join-Path $fixture 'geevensingh.OrgExplorer-0.1.3-full.nupkg'), [Text.Encoding]::UTF8.GetBytes('abc'))
            $manifestPath = New-SignedReleaseManifest -ReleaseDirectory $fixture -Version 0.1.3 -Key $signer -KeyId test-active `
                -Now ([DateTimeOffset]::Parse('2026-11-10T00:00:00Z'))
            $envelope = Get-Content $manifestPath -Raw | ConvertFrom-Json
            $payloadBytes = [Convert]::FromBase64String($envelope.payload)
            $payload = [Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json
            $signature = [Convert]::FromBase64String($envelope.signature)
            Assert ($envelope.keyId -ceq 'test-active' -and $envelope.algorithm -ceq 'ES256' -and $signature.Length -eq 64 -and
                $key.VerifyData($payloadBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                    [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) 'The envelope must sign exact decoded payload bytes with ES256/P1363.'
            Assert ($payload.protocolVersion -eq 1 -and $payload.appId -ceq 'geevensingh.OrgExplorer' -and
                $payload.version -ceq '0.1.3' -and $payload.platform -ceq 'win-x64' -and $payload.channel -ceq 'win' -and
                $payload.releaseTag -ceq 'v0.1.3' -and @($payload.assets).Count -eq 1) 'Manifest protocol and product identity must match the installed verifier.'
            Assert ($payload.assets[0].fileName -ceq 'geevensingh.OrgExplorer-0.1.3-full.nupkg' -and
                $payload.assets[0].size -eq 3 -and $payload.assets[0].sha256 -ceq 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad' -and
                $payload.assets[0].url -ceq 'https://github.com/geevensingh/org-explorer/releases/download/v0.1.3/geevensingh.OrgExplorer-0.1.3-full.nupkg') 'Signed authorization must bind the actual bytes and immutable versioned URL.'
            Assert (([DateTimeOffset]$payload.expiresAt - [DateTimeOffset]$payload.issuedAt).TotalDays -eq 14) 'Metadata validity must be bounded below 31 days.'
            Rejects { New-SignedReleaseManifest -ReleaseDirectory $fixture -Version 0.1.3 -Key $signer -KeyId test-active } 'Existing authorization must never be silently overwritten.'
        } finally { $signer.Dispose() }
        foreach ($badPrivate in @('', 'not base64', $public, [Convert]::ToBase64String($wrongKey.ExportPkcs8PrivateKey()))) {
            Rejects { Open-ReleaseSigningKey -TrustFile $trustFile -KeyId test-active -PrivateKeyBase64 $badPrivate } 'Missing, malformed, public-only or mismatched private material must fail.'
        }
        Rejects { Open-ReleaseSigningKey -TrustFile $trustFile -KeyId unknown -PrivateKeyBase64 $private } 'An unembedded active key must fail.'
        Rejects { Open-ReleaseSigningKey -TrustFile $trustFile -KeyId '' -PrivateKeyBase64 $private } 'Missing owner Actions variable must fail.'
        $validTrust = Get-Content $trustFile -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 6 -Compress
        foreach ($invalidTrust in @(
            $validTrust.Replace('"schemaVersion":1', '"schemaVersion":"1"'),
            $validTrust.Insert(1, '"schemaVersion":1,'),
            $validTrust.Insert(1, '"newKeys":[],')
        )) {
            $invalidTrust | Set-Content $trustFile
            Rejects { Open-ReleaseSigningKey -TrustFile $trustFile -KeyId test-active -PrivateKeyBase64 $private } 'Production trust that the actual client parser rejects must not publish.'
        }
        '{"schemaVersion":1,"manifestUrl":"https://example.invalid","keys":[]}' | Set-Content $trustFile
        Rejects { Open-ReleaseSigningKey -TrustFile $trustFile -KeyId test-active -PrivateKeyBase64 $private } 'Empty production trust must fail closed.'
    } finally { $key.Dispose(); $wrongKey.Dispose(); $private = $null }
} finally {
    Remove-Item $fixture -Recurse -Force
}
Write-Host "PASS: $checks release checks."
