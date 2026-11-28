param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Verify')][string]$Stage,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedRevision,
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [string]$AttestationBundle
)
$ErrorActionPreference = 'Stop'
$candidate = [IO.Path]::GetFullPath($CandidateDirectory)
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if ((& git -C $candidate rev-parse HEAD).Trim() -cne $ExpectedRevision -or
    (& git -C $candidate status --porcelain) -or $LASTEXITCODE -ne 0) {
    throw 'Signing-only probe requires the clean immutable verifier candidate.'
}
. (Join-Path $candidate 'eng\ScaleCleanupPolicy.ps1')
$path = Join-Path $results 'manifest.json'
$expectedPath = Join-Path $results 'expected.json'
if ($Stage -ceq 'Prepare') {
    if (Test-Path -LiteralPath $results) { throw 'Probe results must be fresh; no signing lease may be replayed.' }
    New-Item -ItemType Directory -Path $results | Out-Null
    $now = [DateTimeOffset]::UtcNow
    $started = if ($env:CLEANUP_PROBE_STARTED_AT) { [DateTimeOffset]$env:CLEANUP_PROBE_STARTED_AT } else { $now }
    $deadline = $started.AddMinutes(5)
    if ($started -gt $now -or $deadline -le $now) { throw 'Signing-only probe setup exhausted its original five-minute watchdog.' }
    $nonce = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
    $components = @{}
    foreach ($name in @('ScaleCleanupPolicy.ps1', 'ScaleRunner.ps1', 'TimingPolicy.ps1', 'timing-policy.json')) {
        $components[$name] = (Get-FileHash -LiteralPath (Join-Path $candidate "eng\$name")).Hash
    }
    $proof = @{
        schemaVersion = 1; recordType = 'signing-only-probe'; repository = 'geevensingh/org-explorer'
        revision = $ExpectedRevision; nonce = $nonce; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        scope = 'No native, performance, cohort, product verification or canonical authority.'
        components = $components; createdAt = $now.ToString('O')
    }
    [IO.File]::WriteAllText($path, ($proof | ConvertTo-Json -Depth 10))
    @{ nonce = $nonce; revision = $ExpectedRevision; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        startedAt = $started.ToString('O'); witnessNotBefore = $now.ToString('O'); witnessNotAfter = $deadline.ToString('O')
        signerWorkflowRevision = $env:GITHUB_WORKFLOW_SHA } | ConvertTo-Json | Set-Content -LiteralPath $expectedPath
    if ($env:GITHUB_OUTPUT) { "subject=$path" | Add-Content $env:GITHUB_OUTPUT }
    return
}
if (!$AttestationBundle) { throw 'The actual signing-only probe produced no attestation bundle.' }
$expected = Get-Content -LiteralPath $expectedPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
if ($expected.revision -cne $ExpectedRevision -or $expected.runId -cne $env:GITHUB_RUN_ID -or
    $expected.runAttempt -cne $env:GITHUB_RUN_ATTEMPT -or $expected.signerWorkflowRevision -cne $env:GITHUB_WORKFLOW_SHA) {
    throw 'Signing-only probe expected context changed.'
}
$proofText = Get-Content -LiteralPath $path -Raw
$bundleText = Get-Content -LiteralPath $AttestationBundle -Raw
[IO.File]::WriteAllText((Join-Path $results 'bundle.json'), $bundleText)
$parameters = @{
    Revision = $ExpectedRevision; RunId = $expected.runId; RunAttempt = $expected.runAttempt
    ExpectedNonce = $expected.nonce; WitnessNotBefore = [DateTimeOffset]$expected.witnessNotBefore
    WitnessNotAfter = [DateTimeOffset]$expected.witnessNotAfter
    VerificationDeadline = [DateTimeOffset]$expected.witnessNotAfter
}
$timer = [Diagnostics.Stopwatch]::StartNew()
$verified = Read-VerifiedScaleSignature -ProofText $proofText -BundleText $bundleText @parameters
if ($verified.signature.certificate.buildSignerDigest -cne $expected.signerWorkflowRevision -or
    $verified.signature.certificate.sourceRepositoryDigest -cne $expected.signerWorkflowRevision -or
    $expected.signerWorkflowRevision -ceq $ExpectedRevision) {
    throw 'Signing-only probe did not establish independently expected, distinct candidate A and controller B.'
}
$checks = @('genuine-standard-root-signature', 'positive-github-hosted-certificate', 'candidate-A/controller-B-binding')
$refusals = @{}
foreach ($defect in @('nonce', 'run', 'source', 'manifest-mutation')) {
    $arguments = @{} + $parameters
    $text = $proofText
    switch ($defect) {
        'nonce' { $arguments.ExpectedNonce = '0' * 64 }
        'run' { $arguments.RunId = ([long]$expected.runId + 1).ToString() }
        'source' { $arguments.Revision = '0' * 40 }
        'manifest-mutation' {
            $changed = $proofText | ConvertFrom-Json -AsHashtable -DateKind String
            $changed.nonce = '0' * 64
            $arguments.ExpectedNonce = $changed.nonce
            $text = $changed | ConvertTo-Json -Depth 10
        }
    }
    $rejected = $false
    try { $null = Read-VerifiedScaleSignature -ProofText $text -BundleText $bundleText @arguments }
    catch {
        $reason = $_.Exception.Message
        $rejected = if ($defect -ceq 'manifest-mutation') { $reason.StartsWith('Cleanup signature rejected:') }
            else { $reason.StartsWith('Signed proof differs from independent expected source/run/nonce context.') }
        if (!$rejected) { throw }
        $refusals[$defect] = $reason
    }
    if (!$rejected) { throw "Actual signing-only provenance accepted $defect." }
    $checks += "$defect-rejected"
}
$certificateBytes = [Convert]::FromBase64String(
    ($bundleText | ConvertFrom-Json -AsHashtable -Depth 40).verificationMaterial.certificate.rawBytes)
$certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificate($certificateBytes)
try {
    $result = @{
        schemaVersion = 1; outcome = 'success'; scope = 'signing-only; no workload or baseline authority'
        candidateRevision = $ExpectedRevision; signerRevision = $verified.signature.certificate.sourceRepositoryDigest
        runId = $expected.runId; runAttempt = $expected.runAttempt; checks = $checks; refusals = $refusals
        verificationSeconds = $timer.Elapsed.TotalSeconds
        setupSeconds = ([DateTimeOffset]$expected.witnessNotBefore - [DateTimeOffset]$expected.startedAt).TotalSeconds
        executionSeconds = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$expected.startedAt).TotalSeconds
        ceilingSeconds = 300
        certificateExpiresAt = $certificate.NotAfter.ToUniversalTime().ToString('O')
        verifiedCertificate = $verified.signature.certificate; verifiedTimestamps = $verified.verifiedTimestamps
        laterVerification = 'Pending one read-only verification after expiry; worker does not wait.'
    }
    if ([DateTimeOffset]::UtcNow -ge [DateTimeOffset]$expected.witnessNotAfter) {
        throw 'Signing-only conformance exhausted its original five-minute watchdog.'
    }
    $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $results 'result.json')
} finally { $certificate.Dispose() }
$verified | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $results 'verified.json')
if ($env:GITHUB_STEP_SUMMARY) {
    "### Signing-only provenance verified`nCandidate ``$ExpectedRevision``; signer/controller ``$($result.signerRevision)``. Positive hosted identity and nonce/context/hash refusals passed. No product, native, performance, cohort or baseline proof. Worker ends without waiting for expiry." |
        Add-Content $env:GITHUB_STEP_SUMMARY
}
