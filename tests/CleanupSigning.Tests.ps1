$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.ci-results\cleanup-signing-' + [guid]::NewGuid().ToString('N'))
$candidate = Join-Path $fixture 'candidate'
$results = Join-Path $fixture 'results'
$revision = (& git -C $root rev-parse HEAD).Trim()
$previousEnvironment = @{}
foreach ($name in @('GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'GITHUB_WORKFLOW_SHA', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY', 'CLEANUP_PROBE_STARTED_AT')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $phase = [Diagnostics.Stopwatch]::StartNew()
    & git clone --quiet --no-hardlinks $root $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Isolated signing candidate could not be cloned.' }
    'Controlled controller B; not workload proof.' | Set-Content -LiteralPath (Join-Path $candidate 'probe-controller.txt')
    & git -C $candidate add probe-controller.txt
    & git -C $candidate -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Separate signing controller'
    if ($LASTEXITCODE -ne 0) { throw 'Isolated controller could not be committed.' }
    $controller = (& git -C $candidate rev-parse HEAD).Trim()
    & git -C $candidate checkout --quiet --detach $revision
    if ($LASTEXITCODE -ne 0) { throw 'Isolated immutable target could not be selected.' }
    $env:GITHUB_RUN_ID = '321'; $env:GITHUB_RUN_ATTEMPT = '1'; $env:GITHUB_WORKFLOW_SHA = $controller
    $env:GITHUB_OUTPUT = ''; $env:GITHUB_STEP_SUMMARY = ''
    $env:CLEANUP_PROBE_STARTED_AT = [DateTimeOffset]::UtcNow.ToString('O')
    Write-Host "Signing fixture source setup: $($phase.Elapsed.TotalSeconds.ToString('F3'))s."
    function gh {
        $global:LASTEXITCODE = 0
        if ($args[0] -cne 'api') { throw 'Unexpected external GitHub operation.' }
        switch -Regex ($args[1]) {
            '/actions/runs/321/attempts/1$' {
                @{ id = 321; run_attempt = 1; head_sha = $controller; path = '.github/workflows/scale-verification.yml'
                    head_repository = @{ full_name = 'geevensingh/org-explorer' }; referenced_workflows = @() } |
                    ConvertTo-Json -Depth 10
            }
            '/git/trees/(?<sha>[0-9a-f]{40})\?recursive=1$' {
                $entry = (& git -C $candidate ls-tree $Matches.sha -- eng/timing-policy.json) -split '\s+'
                if ($LASTEXITCODE -ne 0 -or $entry.Count -lt 4) { throw 'Fixture source tree unavailable.' }
                @{ truncated = $false; tree = @(@{ path = 'eng/timing-policy.json'; type = 'blob'; sha = $entry[2] }) } |
                    ConvertTo-Json -Depth 10
            }
            '/git/blobs/(?<sha>[0-9a-f]{40})$' {
                $text = (& git -C $candidate cat-file blob $Matches.sha) -join "`n"
                if ($LASTEXITCODE -ne 0) { throw 'Fixture source blob unavailable.' }
                @{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)) } |
                    ConvertTo-Json
            }
            default { throw "Unexpected fixture GitHub endpoint: $($args[1])" }
        }
    }
    $trust = Join-Path $fixture 'controlled-root.json'
    $ghExecutable = @(Get-Command gh -CommandType Application)[0].Source
    function Start-Process {
        param([string]$FilePath, [object[]]$ArgumentList, [switch]$PassThru, [switch]$NoNewWindow,
            [string]$RedirectStandardOutput, [string]$RedirectStandardError)
        if ($FilePath -cne 'gh') { throw 'Signing fixture permits only the actual external verifier.' }
        $ArgumentList += @('--custom-trusted-root', "`"$trust`"")
        & (Get-Command Start-Process -CommandType Cmdlet) -FilePath $ghExecutable -ArgumentList $ArgumentList `
            -PassThru:$PassThru -NoNewWindow:$NoNewWindow -RedirectStandardOutput $RedirectStandardOutput `
            -RedirectStandardError $RedirectStandardError
    }
    $arguments = @{ ExpectedRevision = $revision; CandidateDirectory = $candidate; ResultsDirectory = $results }
    & "$root\eng\Test-CleanupSigning.ps1" -Stage Prepare @arguments
    $manifest = Join-Path $results 'manifest.json'
    $original = Get-Content -LiteralPath $manifest -Raw
    $refused = $false
    try { & "$root\eng\Test-CleanupSigning.ps1" -Stage Prepare @arguments }
    catch { $refused = $_.Exception.Message -like '*must be fresh*' }
    if (!$refused -or (Get-Content -LiteralPath $manifest -Raw) -cne $original) {
        throw 'The public probe reused or replaced a prepared signing lease.'
    }
    $phase.Restart()
    Add-Type -Path "$PSScriptRoot\CleanupProvenanceFixture.cs" -ReferencedAssemblies `
        System.Security.Cryptography.Pkcs,System.Security.Cryptography,System.Formats.Asn1,System.Text.Json,System.Runtime,System.Runtime.Numerics,System.Collections
    $bundle = Join-Path $fixture 'external-attestation.json'
    [CleanupProvenanceFixture]::Sign($manifest, $bundle, $trust, $controller, '321', '1')
    Write-Host "Controlled certificate/TSA setup: $($phase.Elapsed.TotalSeconds.ToString('F3'))s."
    $phase.Restart()
    & "$root\eng\Test-CleanupSigning.ps1" -Stage Verify -AttestationBundle $bundle @arguments
    $report = Get-Content -LiteralPath (Join-Path $results 'result.json') -Raw | ConvertFrom-Json
    if ($report.outcome -cne 'success' -or $report.candidateRevision -cne $revision -or
        $report.signerRevision -cne $controller -or $report.checks.Count -ne 7 -or @($report.refusals.PSObject.Properties).Count -ne 4 -or
        $report.executionSeconds -gt 300 -or
        $report.scope -cne 'signing-only; no workload or baseline authority') {
        throw 'The public signing-only probe lost source separation, scope or signature refusal evidence.'
    }
    Write-Host "Actual signature and negative-context verification: $($phase.Elapsed.TotalSeconds.ToString('F3'))s."
    Write-Host 'PASS: public signing-only prepare/verify retains separate A/B, fresh lease and real cryptographic context/hash refusals.'
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }
}
